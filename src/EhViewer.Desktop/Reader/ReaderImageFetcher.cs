using System.Buffers;
using System.Collections.Concurrent;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Network;
using Microsoft.Extensions.Logging;

namespace EhViewer.Desktop.Reader;

/// <summary>
/// Resolves and downloads page images using the site's page-view protocol:
/// <c>GET {pageUrl}</c> yields the original image url, which is then fetched with the page as referer.
/// Supports multi-page gallery preview sets and H@H node retries via <c>?nl={skipHathKey}</c>.
/// </summary>
public sealed class ReaderImageFetcher
{
    private const int MaxDownloadAttempts = 4;
    private const long DefaultMaxCachedBytes = 24L * 1024 * 1024;
    private const int DefaultMaxCachedEntries = 16;
    private static readonly string[] LocalExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"];

    private readonly EhEngine _engine;
    private readonly Func<HttpClient> _getImageClient;
    private readonly ConcurrentDictionary<int, string> _resolvedUrls = new();
    private readonly ConcurrentDictionary<int, string> _skipHathKeys = new();
    private readonly object _byteCacheGate = new();
    private readonly Dictionary<string, LinkedListNode<(string Url, byte[] Payload)>> _byteCacheIndex = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Url, byte[] Payload)> _byteCacheOrder = new();
    private readonly long _maxCachedBytes;
    private readonly int _maxCachedEntries;
    private readonly ILogger<ReaderImageFetcher> _logger;

    private long _cachedByteTotal;
    private GalleryDetail? _detail;
    private GalleryPageUrlSource? _urlSource;
    private string _referer = EhUrl.RefererE;
    private string? _localDirectory;
    private volatile string? _lastLocalExtension;

    /// <summary>Raised when resolving or downloading a page reports progress.</summary>
    public event Action<ReaderPageProgress>? PageProgressChanged;

    public ReaderImageFetcher(
        EhEngine engine,
        EhHttpClientFactory clientFactory,
        ILogger<ReaderImageFetcher> logger)
        : this(
            engine,
            () => (clientFactory ?? throw new ArgumentNullException(nameof(clientFactory))).ImageClient,
            logger)
    {
    }

    internal ReaderImageFetcher(
        EhEngine engine,
        Func<HttpClient> getImageClient,
        ILogger<ReaderImageFetcher> logger,
        long maxCachedBytes = DefaultMaxCachedBytes,
        int maxCachedEntries = DefaultMaxCachedEntries)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _getImageClient = getImageClient ?? throw new ArgumentNullException(nameof(getImageClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxCachedBytes = Math.Max(0, maxCachedBytes);
        _maxCachedEntries = Math.Max(0, maxCachedEntries);
    }

    internal int CachedByteEntryCount
    {
        get
        {
            lock (_byteCacheGate)
            {
                return _byteCacheIndex.Count;
            }
        }
    }

    internal long CachedByteTotal
    {
        get
        {
            lock (_byteCacheGate)
            {
                return _cachedByteTotal;
            }
        }
    }

    /// <summary>Binds the fetcher to one gallery and its ordered page urls.</summary>
    public void Bind(GalleryDetail detail, IReadOnlyList<string?> pageUrls, string referer, string? localDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(detail);

        EhSite site = !string.IsNullOrEmpty(referer)
            && referer.Contains("exhentai", StringComparison.OrdinalIgnoreCase)
            ? EhSite.Ex
            : EhSite.E;

        _detail = detail;
        _urlSource = new GalleryPageUrlSource(_engine, detail, pageUrls, site);
        _referer = referer;
        _localDirectory = string.IsNullOrWhiteSpace(localDirectory) ? null : localDirectory;
        _lastLocalExtension = null;
        _resolvedUrls.Clear();
        _skipHathKeys.Clear();
        ClearByteCache();
    }

    public void Unbind()
    {
        _detail = null;
        _urlSource = null;
        _localDirectory = null;
        _lastLocalExtension = null;
        _resolvedUrls.Clear();
        _skipHathKeys.Clear();
        ClearByteCache();
    }

    /// <summary>
    /// Binds to a completed local download that has no live gallery session: every page is served
    /// from <paramref name="localDirectory"/>, so the reader works fully offline.
    /// </summary>
    public void BindLocal(string localDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDirectory);

        _detail = new GalleryDetail();
        _urlSource = null;
        _localDirectory = localDirectory;
        _lastLocalExtension = null;
        _resolvedUrls.Clear();
        _skipHathKeys.Clear();
        ClearByteCache();
    }

    /// <summary>
    /// Downloads the image bytes for a zero-based page index. A completed local download is served
    /// from disk first so an offline library opens without any network round-trip.
    /// </summary>
    public async Task<byte[]> FetchAsync(int index, CancellationToken cancellationToken)
    {
        if (_detail is null)
        {
            return [];
        }

        byte[] local = await TryReadLocalPageAsync(index, cancellationToken).ConfigureAwait(false);
        if (local.Length > 0)
        {
            return local;
        }

        PageProgressChanged?.Invoke(new ReaderPageProgress(index, ReaderPageLoadStage.ResolvingUrl));
        string? imageUrl = await ResolveImageUrlAsync(index, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(imageUrl))
        {
            return [];
        }

        if (TryGetCachedBytes(imageUrl, out byte[] cached))
        {
            return cached;
        }

        byte[] payload = await DownloadAsync(imageUrl, index, cancellationToken).ConfigureAwait(false);
        if (payload.Length == 0)
        {
            return [];
        }

        StoreCachedBytes(imageUrl, payload);
        return payload;
    }

    private bool TryGetCachedBytes(string url, out byte[] payload)
    {
        lock (_byteCacheGate)
        {
            if (_byteCacheIndex.TryGetValue(url, out LinkedListNode<(string Url, byte[] Payload)>? node))
            {
                _byteCacheOrder.Remove(node);
                _byteCacheOrder.AddFirst(node);
                payload = node.Value.Payload;
                return true;
            }
        }

        payload = [];
        return false;
    }

    private void StoreCachedBytes(string url, byte[] payload)
    {
        if (_maxCachedEntries <= 0 || _maxCachedBytes <= 0 || payload.Length > _maxCachedBytes)
        {
            return;
        }

        lock (_byteCacheGate)
        {
            if (_byteCacheIndex.TryGetValue(url, out LinkedListNode<(string Url, byte[] Payload)>? existing))
            {
                _cachedByteTotal -= existing.Value.Payload.Length;
                _byteCacheOrder.Remove(existing);
                _byteCacheIndex.Remove(url);
            }

            LinkedListNode<(string Url, byte[] Payload)> node = _byteCacheOrder.AddFirst((url, payload));
            _byteCacheIndex[url] = node;
            _cachedByteTotal += payload.Length;

            while ((_cachedByteTotal > _maxCachedBytes || _byteCacheIndex.Count > _maxCachedEntries)
                && _byteCacheOrder.Last is { } oldest)
            {
                _byteCacheOrder.RemoveLast();
                _byteCacheIndex.Remove(oldest.Value.Url);
                _cachedByteTotal -= oldest.Value.Payload.Length;
            }
        }
    }

    private void ClearByteCache()
    {
        lock (_byteCacheGate)
        {
            _byteCacheOrder.Clear();
            _byteCacheIndex.Clear();
            _cachedByteTotal = 0;
        }
    }

    internal async Task<byte[]> TryReadLocalPageAsync(int index, CancellationToken cancellationToken)
    {
        if (_localDirectory is null || index < 0)
        {
            return [];
        }

        string stem = index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        string? preferred = _lastLocalExtension;
        if (!string.IsNullOrEmpty(preferred))
        {
            byte[]? preferredBytes = await TryReadCandidateAsync(
                Path.Combine(_localDirectory, stem + preferred),
                cancellationToken).ConfigureAwait(false);
            if (preferredBytes is { Length: > 0 })
            {
                return preferredBytes;
            }
        }

        foreach (string extension in LocalExtensions)
        {
            if (string.Equals(extension, preferred, StringComparison.Ordinal))
            {
                continue;
            }

            byte[]? bytes = await TryReadCandidateAsync(
                Path.Combine(_localDirectory, stem + extension),
                cancellationToken).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
            {
                _lastLocalExtension = extension;
                return bytes;
            }
        }

        return [];
    }

    private async Task<byte[]?> TryReadCandidateAsync(string candidate, CancellationToken cancellationToken)
    {
        if (!File.Exists(candidate))
        {
            return null;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(candidate, cancellationToken).ConfigureAwait(false);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "本地页面读取失败：{Path}", candidate);
            return null;
        }
    }

    /// <summary>
    /// Serves a page from the on-disk download folder using the upstream
    /// <c>{index:D8}.{ext}</c> naming so a downloaded gallery reads identically to Android.
    /// </summary>
    internal byte[] TryReadLocalPage(int index)
    {
        if (_localDirectory is null || index < 0)
        {
            return [];
        }

        string stem = index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        foreach (string extension in LocalExtensions)
        {
            string candidate = Path.Combine(_localDirectory, stem + extension);
            try
            {
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 0)
                {
                    return File.ReadAllBytes(candidate);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "本地页面读取失败：{Path}", candidate);
            }
        }

        return [];
    }

    private async Task<byte[]> DownloadAsync(string imageUrl, int index, CancellationToken cancellationToken)
    {
        var seenSkipKeys = new HashSet<string>(StringComparer.Ordinal);
        string? activeUrl = imageUrl;

        for (int attempt = 0; attempt < MaxDownloadAttempts; attempt++)
        {
            if (attempt > 0)
            {
                _resolvedUrls.TryRemove(index, out _);

                string? skipKey = _skipHathKeys.TryGetValue(index, out string? currentSkip)
                    ? currentSkip
                    : null;

                if (!string.IsNullOrEmpty(skipKey) && !seenSkipKeys.Add(skipKey))
                {
                    // Upstream SpiderQueen aborts when the same skipHathKey loops back unchanged.
                    break;
                }

                string? refreshed = await ResolveImageUrlCoreAsync(index, skipKey, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(refreshed))
                {
                    return [];
                }

                activeUrl = refreshed;
            }

            try
            {
                using HttpRequestMessage request = EhRequestBuilder.Get(activeUrl, _referer);
                using HttpResponseMessage response = await _getImageClient()
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("第 {Page} 页图片返回 {Code}（尝试 {Attempt}/{Max}）",
                        index, (int)response.StatusCode, attempt + 1, MaxDownloadAttempts);
                    continue;
                }

                string? mediaType = response.Content.Headers.ContentType?.MediaType;
                if (!string.IsNullOrEmpty(mediaType)
                    && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogDebug("第 {Page} 页返回非图片响应类型 {MediaType}，尝试切换 H@H 节点", index, mediaType);
                    continue;
                }

                byte[] payload = await ReadPayloadWithProgressAsync(response, index, cancellationToken).ConfigureAwait(false);

                if (IsPlainTextPayload(payload) || Downloads.DownloadManager.LooksLikeHtmlPayload(payload))
                {
                    // The site or proxy serves a text/HTML error page when the referer/H@H node rejects the request.
                    _logger.LogDebug("第 {Page} 页返回文本/HTML 拦截页，尝试切换 H@H 节点", index);
                    continue;
                }

                return payload;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "第 {Page} 页图片下载失败（尝试 {Attempt}/{Max}）",
                    index, attempt + 1, MaxDownloadAttempts);
            }
        }

        return [];
    }

    private async Task<byte[]> ReadPayloadWithProgressAsync(
        HttpResponseMessage response,
        int index,
        CancellationToken cancellationToken)
    {
        long? totalBytes = response.Content.Headers.ContentLength;
        PageProgressChanged?.Invoke(new ReaderPageProgress(index, ReaderPageLoadStage.Downloading, 0, totalBytes));

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        int initialCapacity = totalBytes is > 0 and <= 16 * 1024 * 1024 ? (int)totalBytes.Value : 64 * 1024;
        using var ms = new MemoryStream(initialCapacity);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        long received = 0;
        long lastReportedBytes = 0;

        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                ms.Write(buffer, 0, read);
                received += read;

                // Throttle UI progress notifications while keeping large downloads responsive.
                if (received - lastReportedBytes >= 32 * 1024)
                {
                    lastReportedBytes = received;
                    PageProgressChanged?.Invoke(new ReaderPageProgress(index, ReaderPageLoadStage.Downloading, received, totalBytes));
                }
            }

            if (received > lastReportedBytes)
            {
                PageProgressChanged?.Invoke(new ReaderPageProgress(index, ReaderPageLoadStage.Downloading, received, totalBytes));
            }

            if (ms.TryGetBuffer(out ArraySegment<byte> seg) &&
                seg.Array is not null &&
                seg.Offset == 0 &&
                seg.Count == seg.Array.Length)
            {
                return seg.Array;
            }

            return ms.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Resolves the original image url for a page, caching the result.</summary>
    public async Task<string?> ResolveImageUrlAsync(int index, CancellationToken cancellationToken)
    {
        if (_resolvedUrls.TryGetValue(index, out string? cached))
        {
            return cached;
        }

        return await ResolveImageUrlCoreAsync(index, skipHathKey: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ResolveImageUrlCoreAsync(
        int index,
        string? skipHathKey,
        CancellationToken cancellationToken)
    {
        if (_detail is null || _urlSource is null || index < 0)
        {
            return null;
        }

        string? pageUrl = await _urlSource.ResolvePageUrlAsync(index, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(pageUrl))
        {
            return null;
        }

        string requestUrl = GalleryPageUrlSource.AppendSkipHathKey(pageUrl, skipHathKey);

        try
        {
            GalleryPageParser.Result page = await _engine
                .GetGalleryPageAsync(requestUrl, _detail.Gid, _detail.Token ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(page.SkipHathKey))
            {
                _skipHathKeys[index] = page.SkipHathKey;
            }

            string? imageUrl = page.ImageUrl;
            if (!string.IsNullOrEmpty(imageUrl))
            {
                _resolvedUrls[index] = imageUrl;
            }

            return imageUrl;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "第 {Page} 页页面解析失败", index);
            return null;
        }
    }

    /// <summary>
    /// Upstream treats a payload whose every byte is &lt;= 126 as an error page rather than an image.
    /// </summary>
    internal static bool IsPlainTextPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return true;
        }

        foreach (byte b in payload)
        {
            if (b > 126)
            {
                return false;
            }
        }

        return true;
    }
}
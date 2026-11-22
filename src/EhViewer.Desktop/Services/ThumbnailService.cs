using EhViewer.Core.Client;
using EhViewer.Core.Network;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Three-level thumbnail cache: in-memory decoded bitmaps (byte-budgeted LRU), on-disk
/// content-addressed blobs, then the network. Concurrent requests for the same key share a single
/// download so a scrolling list never issues duplicate fetches.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private readonly ThumbnailDiskCache _diskCache;
    private readonly Func<HttpClient> _getHttpClient;
    private readonly IEhClientSettings _settings;
    private readonly ILogger<ThumbnailService> _logger;
    private readonly Func<long> _memoryBudgetBytesProvider;
    private readonly CancellationTokenSource _disposeCts = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _memoryIndex = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _memoryOrder = new();
    private readonly Dictionary<string, Task<CacheEntry?>> _inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<byte[]?>> _inFlightDownloads = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _downloadGate = new(6, 6);
    private readonly SemaphoreSlim _decodeGate = new(
        Math.Clamp(Environment.ProcessorCount, 2, 6),
        Math.Clamp(Environment.ProcessorCount, 2, 6));

    private long _memoryBytes;
    private bool _disposed;

    public ThumbnailService(
        ThumbnailDiskCache diskCache,
        EhHttpClientFactory clientFactory,
        IEhClientSettings settings,
        ILogger<ThumbnailService> logger,
        DesktopSettingsHolder? settingsHolder = null)
        : this(
            diskCache,
            () => (clientFactory ?? throw new ArgumentNullException(nameof(clientFactory))).ImageClient,
            settings,
            logger,
            settingsHolder is null
                ? static () => 96L * 1024 * 1024
                : () => Math.Max(8L * 1024 * 1024, settingsHolder.Current.ThumbnailMemoryBudgetMb * 1024L * 1024L))
    {
    }

    internal ThumbnailService(
        ThumbnailDiskCache diskCache,
        Func<HttpClient> getHttpClient,
        IEhClientSettings settings,
        ILogger<ThumbnailService> logger,
        Func<long> memoryBudgetBytesProvider)
    {
        _diskCache = diskCache ?? throw new ArgumentNullException(nameof(diskCache));
        _getHttpClient = getHttpClient ?? throw new ArgumentNullException(nameof(getHttpClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _memoryBudgetBytesProvider = memoryBudgetBytesProvider ?? throw new ArgumentNullException(nameof(memoryBudgetBytesProvider));
    }

    /// <summary>
    /// Loads a thumbnail, decoding it at roughly <paramref name="targetWidth"/> pixels so a list of
    /// 200 thumbnails does not decode 200 full-size bitmaps.
    /// </summary>
    public async Task<SKBitmap?> LoadAsync(string url, int targetWidth, CancellationToken cancellationToken = default)
    {
        CacheEntry? entry = await AcquireEntryAsync(url, targetWidth, pinEntry: false, cancellationToken).ConfigureAwait(false);
        return entry?.Bitmap;
    }

    /// <summary>
    /// Loads a thumbnail (and optional sprite crop rectangle for <c>NormalPreviewSet</c> tiles) into
    /// an independent <see cref="DecodedPixelBuffer"/> safe for immediate <c>WriteableBitmap</c> upload
    /// with zero shared-bitmap lifetime hazards.
    /// </summary>
    public async Task<DecodedPixelBuffer?> LoadPixelsAsync(
        string url,
        int targetWidth,
        SKRectI? clip = null,
        CancellationToken cancellationToken = default)
    {
        // Sprite sheets (gdtm/gdtl) carry CSS pixel offsets measured against the native image
        // dimensions and URL; downsampling or rewriting resolution would squish every tile.
        int effectiveWidth = clip.HasValue ? int.MaxValue : targetWidth;
        bool preserveUrl = clip.HasValue;
        CacheEntry? entry = await AcquireEntryAsync(
                url,
                effectiveWidth,
                pinEntry: true,
                cancellationToken,
                preserveUrl)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        try
        {
            if (clip.HasValue || (long)entry.Bitmap.Width * entry.Bitmap.Height > 65_536L)
            {
                return await Task.Run(
                        () => SkiaBitmapHelper.ExtractBgraPixels(entry.Bitmap, clip),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return SkiaBitmapHelper.ExtractBgraPixels(entry.Bitmap, clip);
        }
        finally
        {
            ReleaseEntry(entry);
        }
    }

    /// <summary>
    /// Returns the raw encoded thumbnail bytes from disk cache (or downloads and caches once if absent),
    /// coalescing concurrent requests for the same URL.
    /// </summary>
    public Task<byte[]?> LoadRawBytesAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult<byte[]?>(null);
        }

        string diskKey = ThumbnailDiskCache.ComputeUrlKey(url);
        string legacyKey = ThumbnailDiskCache.ComputeKey(url, 400);
        return AcquirePayloadAsync(url, legacyKey, diskKey, cancellationToken);
    }

    private async Task<CacheEntry?> AcquireEntryAsync(
        string url,
        int targetWidth,
        bool pinEntry,
        CancellationToken cancellationToken,
        bool preserveUrl = false)
    {
        if (string.IsNullOrWhiteSpace(url) || targetWidth <= 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        string resolved = preserveUrl
            ? url
            : (EhUtils.HandleThumbUrlResolution(url, _settings.ThumbResolution) ?? url);
        string memoryKey = ThumbnailDiskCache.ComputeKey(resolved, targetWidth);
        string diskKey = ThumbnailDiskCache.ComputeUrlKey(resolved);

        if (TryGetMemoryEntry(memoryKey, pinEntry, out CacheEntry? cached))
        {
            return cached;
        }

        Task<CacheEntry?> task;
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_inFlight.TryGetValue(memoryKey, out Task<CacheEntry?>? existing))
            {
                task = existing;
            }
            else
            {
                task = LoadCoreAsync(resolved, memoryKey, diskKey, targetWidth, _disposeCts.Token);
                _inFlight[memoryKey] = task;
            }
        }

        try
        {
            CacheEntry? loaded = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                return null;
            }

            if (!pinEntry)
            {
                return loaded;
            }

            // Re-pin under the gate before eviction can dispose the loaded bitmap.
            lock (_gate)
            {
                if (_disposed || loaded.Disposed)
                {
                    return null;
                }

                loaded.PinCount++;
                return loaded;
            }
        }
        finally
        {
            if (task.IsCompleted)
            {
                lock (_gate)
                {
                    if (_inFlight.TryGetValue(memoryKey, out Task<CacheEntry?>? current) && ReferenceEquals(current, task))
                    {
                        _inFlight.Remove(memoryKey);
                    }
                }
            }
        }
    }

    private async Task<CacheEntry?> LoadCoreAsync(
        string url,
        string memoryKey,
        string diskKey,
        int targetWidth,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[]? payload = await AcquirePayloadAsync(url, memoryKey, diskKey, cancellationToken).ConfigureAwait(false);
            if (payload is null || payload.Length == 0)
            {
                return null;
            }

            SKBitmap? bitmap;
            await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                bitmap = Decode(payload, targetWidth);
            }
            finally
            {
                _decodeGate.Release();
            }

            if (bitmap is null)
            {
                return null;
            }

            return StoreMemory(memoryKey, bitmap);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "缩略图加载失败：{Url}", url);
            return null;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(memoryKey);
            }
        }
    }

    private async Task<byte[]?> AcquirePayloadAsync(
        string url,
        string memoryKey,
        string diskKey,
        CancellationToken cancellationToken)
    {
        Task<byte[]?> downloadTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_inFlightDownloads.TryGetValue(diskKey, out Task<byte[]?>? existing))
            {
                downloadTask = existing;
            }
            else
            {
                downloadTask = ReadOrDownloadPayloadCoreAsync(url, memoryKey, diskKey, _disposeCts.Token);
                _inFlightDownloads[diskKey] = downloadTask;
            }
        }

        try
        {
            return await downloadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (downloadTask.IsCompleted)
            {
                lock (_gate)
                {
                    if (_inFlightDownloads.TryGetValue(diskKey, out Task<byte[]?>? current) &&
                        ReferenceEquals(current, downloadTask))
                    {
                        _inFlightDownloads.Remove(diskKey);
                    }
                }
            }
        }
    }

    private async Task<byte[]?> ReadOrDownloadPayloadCoreAsync(
        string url,
        string memoryKey,
        string diskKey,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_diskCache.TryGetPath(diskKey, out string cachedPath) ||
                _diskCache.TryGetPath(memoryKey, out cachedPath))
            {
                try
                {
                    return await File.ReadAllBytesAsync(cachedPath, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "缩略图磁盘缓存读取失败，回退网络：{Path}", cachedPath);
                }
            }

            await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            byte[] bytes;
            try
            {
                bytes = await DownloadAsync(url, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _downloadGate.Release();
            }

            if (bytes.Length > 0)
            {
                _diskCache.Set(diskKey, bytes);
            }

            return bytes;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "缩略图加载失败：{Url}", url);
            return null;
        }
        finally
        {
            lock (_gate)
            {
                _inFlightDownloads.Remove(diskKey);
            }
        }
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = EhRequestBuilder.Get(
            url,
            referer: EhUrl.RefererE);
        using HttpResponseMessage response = await _getHttpClient()
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Decodes at a sampled size so memory scales with the displayed size rather than the source
    /// resolution. Falls back to a full decode when the codec cannot sample safely.
    /// </summary>
    internal static SKBitmap? Decode(ReadOnlySpan<byte> payload, int targetWidth)
    {
        using SKData data = SKData.CreateCopy(payload);
        using SKCodec? codec = SKCodec.Create(data);
        if (codec is null)
        {
            return null;
        }

        SKImageInfo sourceInfo = codec.Info;
        int sampleWidth = Math.Min(targetWidth, Math.Max(1, sourceInfo.Width));
        SKSizeI targetSize = ComputeSampledSize(sourceInfo.Width, sourceInfo.Height, sampleWidth);

        SKAlphaType alphaType = sourceInfo.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var decodeInfo = new SKImageInfo(targetSize.Width, targetSize.Height, SKColorType.Bgra8888, alphaType);
        SKBitmap bitmap = new(decodeInfo);
        SKCodecResult result = codec.GetPixels(decodeInfo, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
        {
            return bitmap;
        }

        bitmap.Dispose();

        // Some codecs refuse scaled decoding or direct BGRA8888 conversion; fall back gracefully.
        var nativeInfo = new SKImageInfo(sourceInfo.Width, sourceInfo.Height, sourceInfo.ColorType, sourceInfo.AlphaType);
        SKBitmap native = new(nativeInfo);
        if (codec.GetPixels(nativeInfo, native.GetPixels()) is SKCodecResult.Success or SKCodecResult.IncompleteInput)
        {
            return native;
        }

        native.Dispose();
        return null;
    }

    internal static SKSizeI ComputeSampledSize(int width, int height, int targetWidth)
    {
        if (width <= 0 || height <= 0 || targetWidth <= 0 || width <= targetWidth)
        {
            return new SKSizeI(Math.Max(1, width), Math.Max(1, height));
        }

        // Integer sampling keeps the aspect ratio exact and is what the codec does natively.
        int sample = width / targetWidth;
        if (sample < 1)
        {
            sample = 1;
        }

        return new SKSizeI(Math.Max(1, width / sample), Math.Max(1, height / sample));
    }

    private bool TryGetMemoryEntry(string key, bool pinEntry, out CacheEntry? entry)
    {
        lock (_gate)
        {
            if (!_disposed && _memoryIndex.TryGetValue(key, out LinkedListNode<CacheEntry>? node))
            {
                _memoryOrder.Remove(node);
                _memoryOrder.AddFirst(node);
                if (pinEntry)
                {
                    node.Value.PinCount++;
                }

                entry = node.Value;
                return true;
            }
        }

        entry = null;
        return false;
    }

    private CacheEntry StoreMemory(string key, SKBitmap bitmap)
    {
        long size = Math.Max(1L, (long)bitmap.Info.Width * bitmap.Info.Height * Math.Max(1, bitmap.Info.BytesPerPixel));
        long budgetBytes = _memoryBudgetBytesProvider();

        lock (_gate)
        {
            if (_disposed)
            {
                bitmap.Dispose();
                return new CacheEntry(key, bitmap, size) { Disposed = true };
            }

            if (_memoryIndex.TryGetValue(key, out LinkedListNode<CacheEntry>? existing))
            {
                _memoryBytes -= existing.Value.Size;
                _memoryOrder.Remove(existing);
                _memoryIndex.Remove(key);
                if (!ReferenceEquals(existing.Value.Bitmap, bitmap))
                {
                    MarkEvictedLocked(existing.Value);
                }
            }

            var entry = new CacheEntry(key, bitmap, size);
            LinkedListNode<CacheEntry> node = _memoryOrder.AddFirst(entry);
            _memoryIndex[key] = node;
            _memoryBytes += size;

            while (_memoryBytes > budgetBytes && _memoryOrder.Last is { } last)
            {
                if (ReferenceEquals(last.Value, entry))
                {
                    break;
                }

                _memoryOrder.RemoveLast();
                _memoryIndex.Remove(last.Value.Key);
                _memoryBytes -= last.Value.Size;
                MarkEvictedLocked(last.Value);
            }

            return entry;
        }
    }

    private void ReleaseEntry(CacheEntry entry)
    {
        bool shouldDispose = false;
        lock (_gate)
        {
            if (entry.PinCount > 0)
            {
                entry.PinCount--;
            }

            if (entry.Evicted && entry.PinCount == 0 && !entry.Disposed)
            {
                entry.Disposed = true;
                shouldDispose = true;
            }
        }

        if (shouldDispose)
        {
            entry.Bitmap.Dispose();
        }
    }

    private static void MarkEvictedLocked(CacheEntry entry)
    {
        entry.Evicted = true;
        if (entry.PinCount == 0 && !entry.Disposed)
        {
            entry.Disposed = true;
            entry.Bitmap.Dispose();
        }
    }

    public void ClearMemory()
    {
        lock (_gate)
        {
            foreach (CacheEntry entry in _memoryOrder)
            {
                MarkEvictedLocked(entry);
            }

            _memoryOrder.Clear();
            _memoryIndex.Clear();
            _memoryBytes = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _disposeCts.Cancel();
        _disposeCts.Dispose();
        ClearMemory();
    }

    private sealed class CacheEntry
    {
        public CacheEntry(string key, SKBitmap bitmap, long size)
        {
            Key = key;
            Bitmap = bitmap;
            Size = size;
        }

        public string Key { get; }
        public SKBitmap Bitmap { get; }
        public long Size { get; }
        public int PinCount { get; set; }
        public bool Evicted { get; set; }
        public bool Disposed { get; set; }
    }
}

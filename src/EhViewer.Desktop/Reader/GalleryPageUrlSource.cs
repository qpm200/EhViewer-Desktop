using System.Collections.Concurrent;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Network;

namespace EhViewer.Desktop.Reader;

/// <summary>
/// Resolves gallery page urls lazily across multi-page preview sets and constructs H@H retry urls
/// using the page's <c>skipHathKey</c> (<c>?nl=</c> / <c>&amp;nl=</c>). Shared between
/// <see cref="ReaderImageFetcher"/> and <see cref="Downloads.DownloadManager"/> so multi-page
/// galleries and H@H node skipping follow identical rules everywhere.
/// </summary>
internal sealed class GalleryPageUrlSource
{
    private readonly EhEngine? _engine;
    private readonly long _gid;
    private readonly string _token;
    private readonly EhSite _site;
    private readonly ConcurrentDictionary<int, string> _pageUrls = new();
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private readonly HashSet<int> _fetchedPreviewPages = [];

    private int _previewPages;
    private int _perPageCount;

    public GalleryPageUrlSource(
        EhEngine? engine,
        GalleryDetail detail,
        IReadOnlyList<string?>? fallbackPageUrls = null,
        EhSite site = EhSite.E)
    {
        ArgumentNullException.ThrowIfNull(detail);

        _engine = engine;
        _gid = detail.Gid;
        _token = detail.Token ?? string.Empty;
        _site = site;

        int declaredPreviewPages = detail.PreviewPages > 0
            ? detail.PreviewPages
            : detail.SpiderInfoPreviewPages;
        _previewPages = Math.Max(1, declaredPreviewPages);

        PreviewSet? initialPreviews = detail.PreviewSet ?? detail.SpiderInfoPreviewSet;
        if (initialPreviews is not null && initialPreviews.Size > 0)
        {
            SeedPreviewSet(initialPreviews, previewPageIndex: 0);
            _perPageCount = Math.Max(1, initialPreviews.Size);
        }

        if (fallbackPageUrls is not null && fallbackPageUrls.Count > 0)
        {
            for (int i = 0; i < fallbackPageUrls.Count; i++)
            {
                string? url = fallbackPageUrls[i];
                if (!string.IsNullOrEmpty(url))
                {
                    _pageUrls.TryAdd(i, url);
                }
            }

            if (_perPageCount <= 0)
            {
                _perPageCount = Math.Max(1, fallbackPageUrls.Count);
            }

            _fetchedPreviewPages.Add(0);
        }
    }

    public int KnownCount => _pageUrls.Count;

    public bool TryGetCachedPageUrl(int pageIndex, out string? pageUrl)
        => _pageUrls.TryGetValue(pageIndex, out pageUrl);

    /// <summary>
    /// Returns the page url for zero-based <paramref name="pageIndex"/>, lazily fetching additional
    /// gallery preview pages when <paramref name="pageIndex"/> lies past preview page 0.
    /// </summary>
    public async Task<string?> ResolvePageUrlAsync(int pageIndex, CancellationToken cancellationToken)
    {
        if (pageIndex < 0)
        {
            return null;
        }

        if (_pageUrls.TryGetValue(pageIndex, out string? cached))
        {
            return cached;
        }

        if (_engine is null || _gid <= 0 || string.IsNullOrEmpty(_token))
        {
            return null;
        }

        await _previewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pageUrls.TryGetValue(pageIndex, out cached))
            {
                return cached;
            }

            // Estimate which preview page holds `pageIndex` using the page size observed on page 0.
            int perPage = Math.Max(1, _perPageCount);
            int estimatedPreviewPage = Math.Clamp(pageIndex / perPage, 0, Math.Max(0, _previewPages - 1));
            await FetchPreviewPageLockedAsync(estimatedPreviewPage, cancellationToken).ConfigureAwait(false);
            if (_pageUrls.TryGetValue(pageIndex, out cached))
            {
                return cached;
            }

            // Account-level preview density can differ if page 0 was restored from partial state;
            // scan any remaining unfetched preview pages in ascending order until found.
            for (int p = 0; p < _previewPages; p++)
            {
                if (_fetchedPreviewPages.Contains(p))
                {
                    continue;
                }

                await FetchPreviewPageLockedAsync(p, cancellationToken).ConfigureAwait(false);
                if (_pageUrls.TryGetValue(pageIndex, out cached))
                {
                    return cached;
                }
            }

            return null;
        }
        finally
        {
            _previewGate.Release();
        }
    }

    /// <summary>
    /// Builds the retry page url with <c>?nl=</c> / <c>&amp;nl=</c> appended, preserving the rest
    /// of the query string and matching upstream <c>SpiderQueen.getPageUrl</c>.
    /// </summary>
    public static string AppendSkipHathKey(string pageUrl, string? skipHathKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageUrl);

        if (string.IsNullOrWhiteSpace(skipHathKey))
        {
            return pageUrl;
        }

        string encoded = Uri.EscapeDataString(skipHathKey.Trim());
        string separator = pageUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{pageUrl}{separator}nl={encoded}";
    }

    private void SeedPreviewSet(PreviewSet set, int previewPageIndex)
    {
        _fetchedPreviewPages.Add(previewPageIndex);

        for (int i = 0; i < set.Size; i++)
        {
            int position = set.GetPosition(i);
            string? pageUrl = set.GetPageUrlAt(i);
            if (position >= 0 && !string.IsNullOrEmpty(pageUrl))
            {
                _pageUrls[position] = pageUrl;
            }
        }
    }

    private async Task FetchPreviewPageLockedAsync(int previewPageIndex, CancellationToken cancellationToken)
    {
        if (_engine is null || _fetchedPreviewPages.Contains(previewPageIndex))
        {
            return;
        }

        _fetchedPreviewPages.Add(previewPageIndex);
        string url = EhUrl.GetGalleryDetailUrl(_gid, _token, previewPageIndex, allComment: false, _site);

        (PreviewSet previewSet, int reportedPreviewPages) = await _engine
            .GetPreviewSetAsync(url, cancellationToken)
            .ConfigureAwait(false);

        if (reportedPreviewPages > _previewPages)
        {
            _previewPages = reportedPreviewPages;
        }

        if (previewSet.Size > 0)
        {
            if (_perPageCount <= 0 || (previewPageIndex == 0 && previewSet.Size > _perPageCount))
            {
                _perPageCount = previewSet.Size;
            }

            SeedPreviewSet(previewSet, previewPageIndex);
        }
    }
}

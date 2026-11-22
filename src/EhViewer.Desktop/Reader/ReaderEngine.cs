using System.Collections.Concurrent;
using EhViewer.Core.Client;
using EhViewer.Core.Network;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace EhViewer.Desktop.Reader;

/// <summary>Lifecycle stage reported while resolving, downloading, and decoding a reader page.</summary>
public enum ReaderPageLoadStage
{
    Pending = 0,
    ResolvingUrl = 1,
    Downloading = 2,
    Decoding = 3,
    Completed = 4,
    Failed = 5
}

/// <summary>Progress snapshot emitted for an in-flight reader page load.</summary>
public readonly record struct ReaderPageProgress(
    int PageIndex,
    ReaderPageLoadStage Stage,
    long BytesReceived = 0,
    long? TotalBytes = null);

/// <summary>Layout modes matching upstream <c>GalleryView.LAYOUT_*</c>.</summary>
public enum ReaderLayout
{
    LeftToRight = 0,
    RightToLeft = 1,
    TopToBottom = 2
}

/// <summary>Scale modes matching upstream <c>GalleryView.SCALE_*</c>.</summary>
public enum ReaderScale
{
    FitScreen = 0,
    FitWidth = 1,
    FitHeight = 2,
    Original = 3
}

/// <summary>
/// One decoded page pinned by a <see cref="ReaderPageCache.Lease"/>. The caller that requested the
/// page owns that lease and must <see cref="Dispose"/> it; the underlying bitmap stays valid until
/// then even if the cache evicts the entry in the meantime. Callers must never dispose
/// <see cref="Bitmap"/> themselves.
/// </summary>
public sealed class ReaderPageContent : IDisposable
{
    private readonly ReaderPageCache.Lease _lease;

    public ReaderPageContent(int index, ReaderPageCache.Lease lease, bool isAnimated, IReadOnlyList<int> frameDurations)
    {
        Index = index;
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        IsAnimated = isAnimated;
        FrameDurations = frameDurations ?? [];
    }

    public int Index { get; }

    /// <summary>Decoded pixels owned by the cache; do not dispose.</summary>
    public SKBitmap Bitmap => _lease.Bitmap;

    public bool IsAnimated { get; }

    /// <summary>Per-frame delays in milliseconds; empty for stills.</summary>
    public IReadOnlyList<int> FrameDurations { get; }

    public void Dispose() => _lease.Dispose();
}

/// <summary>
/// Reader engine: fetches page images, decodes them within a pixel budget, caches decoded bitmaps
/// under a byte budget, and prefetches neighbours around the current page.
/// </summary>
/// <remarks>
/// Decoding is keyed by page index and coalesced, but the shared work is bound to the engine's own
/// lifetime token so that the first caller cancelling its own wait cannot poison every other caller
/// waiting on the same page. Returned <see cref="ReaderPageContent"/> instances are pinned leases owned by
/// the caller; concurrent callers each receive their own independent lease.
/// </remarks>
public sealed class ReaderEngine : IDisposable
{
    private const int MaxAnimatedPayloads = 6;
    private const long MaxAnimatedPayloadBytes = 64L * 1024 * 1024;

    private static long s_nextScopeId;

    private readonly record struct InFlightDecode(Task<bool> Task, bool IsPrefetch);

    private readonly string _scopePrefix = $"r{Interlocked.Increment(ref s_nextScopeId)}:";
    private readonly Func<int, CancellationToken, Task<byte[]>> _fetchPage;
    private readonly ReaderPageCache _cache;
    private readonly ConcurrentDictionary<int, InFlightDecode> _inFlight = new();
    private readonly object _prefetchGate = new();
    private readonly SemaphoreSlim _prefetchConcurrency = new(2, 2);
    private CancellationTokenSource? _prefetchCts;
    private readonly ConcurrentDictionary<int, bool> _animated = new();
    private readonly ConcurrentDictionary<int, IReadOnlyList<int>> _durations = new();
    private readonly object _payloadGate = new();
    private readonly Dictionary<int, LinkedListNode<(int Index, byte[] Payload)>> _payloadIndex = [];
    private readonly LinkedList<(int Index, byte[] Payload)> _payloadLruOrder = new();
    private long _totalPayloadBytes;
    private readonly ConcurrentDictionary<int, ReaderPageProgress> _latestProgress = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILogger<ReaderEngine> _logger;

    private volatile bool _disposed;

    public ReaderEngine(
        Func<int, CancellationToken, Task<byte[]>> fetchPage,
        ReaderPageCache cache,
        ILogger<ReaderEngine> logger)
    {
        _fetchPage = fetchPage ?? throw new ArgumentNullException(nameof(fetchPage));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Number of pages before and after the current page to prefetch.</summary>
    public int PreloadCount { get; set; } = 2;

    /// <summary>Decode ceiling applied to width and height independently.</summary>
    public int MaxDecodeEdge { get; set; } = 4096;

    public ReaderLayout Layout { get; set; } = ReaderLayout.LeftToRight;

    public ReaderScale Scale { get; set; } = ReaderScale.FitScreen;

    /// <summary>When true, converts wide-gamut or untagged images to standard sRGB on decode.</summary>
    public bool ForceSrgb { get; set; }

    /// <summary>Total pages in the current gallery, or 0 when unknown.</summary>
    public int PageCount { get; set; }

    /// <summary>Current reading position; persisted as <c>SpiderInfo.startPage</c>.</summary>
    public int CurrentPage { get; private set; }

    /// <summary>Raised whenever <see cref="CurrentPage"/> advances or retreats.</summary>
    public event Action<int>? PageChanged;

    /// <summary>Raised when a page transitions load stage or receives HTTP image bytes.</summary>
    public event Action<ReaderPageProgress>? PageProgressChanged;

    public string CacheKey(int index) => $"{_scopePrefix}page:{index}";

    /// <summary>Updates cached stage progress for <paramref name="progress.PageIndex"/> and notifies listeners.</summary>
    public void ReportProgress(ReaderPageProgress progress)
    {
        if (progress.PageIndex < 0 || _disposed)
        {
            return;
        }

        if (progress.Stage is ReaderPageLoadStage.Completed or ReaderPageLoadStage.Failed)
        {
            _latestProgress.TryRemove(progress.PageIndex, out _);
        }
        else
        {
            _latestProgress[progress.PageIndex] = progress;
        }

        PageProgressChanged?.Invoke(progress);
    }

    /// <summary>Reads the latest known in-flight progress snapshot for a page, if any.</summary>
    public bool TryGetProgress(int index, out ReaderPageProgress progress)
        => _latestProgress.TryGetValue(index, out progress);

    /// <summary>
    /// Fast-path synchronous cache lookup that acquires a pinned lease immediately when the page is
    /// already decoded and resident in <see cref="ReaderPageCache"/>.
    /// </summary>
    public bool TryGetCachedPage(int index, out ReaderPageContent? page)
    {
        if (!_disposed && IsValidIndex(index) && _cache.TryAcquire(CacheKey(index), out ReaderPageCache.Lease? lease) && lease is not null)
        {
            page = new ReaderPageContent(index, lease, IsAnimated(index), Durations(index));
            return true;
        }

        page = null;
        return false;
    }

    /// <summary>
    /// Decode ceiling that adapts to memory pressure: a machine with less than 800 MB of available
    /// heap headroom decodes pages at half resolution instead of risking an OOM on very large pages.
    /// </summary>
    public int EffectiveMaxDecodeEdge
    {
        get
        {
            const long LowMemoryThreshold = 800L * 1024 * 1024;
            long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return available > 0 && available < LowMemoryThreshold
                ? Math.Min(MaxDecodeEdge, 2048)
                : MaxDecodeEdge;
        }
    }

    /// <summary>
    /// Returns the page at <paramref name="index"/> pinned for the caller. Dispose the result when
    /// the pixels are no longer displayed.
    /// </summary>
    public async Task<ReaderPageContent?> GetPageAsync(int index, CancellationToken cancellationToken = default)
    {
        if (!IsValidIndex(index))
        {
            return null;
        }

        if (!await EnsureDecodedAsync(index, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // A separate lease is acquired per caller so concurrent readers never share pin counts.
        if (_cache.TryAcquire(CacheKey(index), out ReaderPageCache.Lease? lease) && lease is not null)
        {
            return new ReaderPageContent(index, lease, IsAnimated(index), Durations(index));
        }

        return null;
    }

    /// <summary>
    /// Ensures the page is decoded and resident in the cache without keeping a pin. Used by
    /// prefetching, where retaining a lease would defeat LRU eviction.
    /// </summary>
    public async Task<bool> EnsureDecodedAsync(int index, CancellationToken cancellationToken = default)
    {
        if (!IsValidIndex(index))
        {
            return false;
        }

        string key = CacheKey(index);
        if (_cache.TryGet(key, out _))
        {
            return true;
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            InFlightDecode entry = _inFlight.GetOrAdd(
                index,
                i => new InFlightDecode(LoadCoreAsync(i, key, _lifetime.Token), IsPrefetch: false));

            bool succeeded;
            try
            {
                // Per-caller cancellation only abandons this wait; the shared decode keeps running.
                succeeded = await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
                if (entry.Task.IsCompleted)
                {
                    _inFlight.TryRemove(new KeyValuePair<int, InFlightDecode>(index, entry));
                }
            }

            if (succeeded)
            {
                return true;
            }

            if (entry.IsPrefetch &&
                !cancellationToken.IsCancellationRequested &&
                !_disposed &&
                !_cache.TryGet(key, out _))
            {
                continue;
            }

            return false;
        }

        return false;
    }

    /// <summary>Raw encoded bytes for a decoded animated page, used to render animation frames on demand.</summary>
    public bool TryGetPayload(int index, out byte[]? payload)
    {
        lock (_payloadGate)
        {
            if (_payloadIndex.TryGetValue(index, out LinkedListNode<(int Index, byte[] Payload)>? node))
            {
                _payloadLruOrder.Remove(node);
                _payloadLruOrder.AddFirst(node);
                payload = node.Value.Payload;
                return true;
            }
        }

        payload = null;
        return false;
    }

    private void StoreAnimatedPayload(int index, byte[] payload)
    {
        if (payload.Length == 0 || payload.Length > MaxAnimatedPayloadBytes)
        {
            RemoveAnimatedPayload(index);
            return;
        }

        lock (_payloadGate)
        {
            if (_disposed)
            {
                return;
            }

            if (_payloadIndex.TryGetValue(index, out LinkedListNode<(int Index, byte[] Payload)>? existing))
            {
                _totalPayloadBytes -= existing.Value.Payload.Length;
                _payloadLruOrder.Remove(existing);
                _payloadIndex.Remove(index);
            }

            var node = _payloadLruOrder.AddFirst((index, payload));
            _payloadIndex[index] = node;
            _totalPayloadBytes += payload.Length;

            while ((_payloadLruOrder.Count > MaxAnimatedPayloads || _totalPayloadBytes > MaxAnimatedPayloadBytes)
                   && _payloadLruOrder.Count > 1
                   && _payloadLruOrder.Last is { } oldest)
            {
                _payloadLruOrder.RemoveLast();
                _payloadIndex.Remove(oldest.Value.Index);
                _totalPayloadBytes -= oldest.Value.Payload.Length;
            }
        }
    }

    private void RemoveAnimatedPayload(int index)
    {
        lock (_payloadGate)
        {
            if (_payloadIndex.Remove(index, out LinkedListNode<(int Index, byte[] Payload)>? node))
            {
                _totalPayloadBytes -= node.Value.Payload.Length;
                _payloadLruOrder.Remove(node);
            }
        }
    }

    private bool IsValidIndex(int index) => index >= 0 && (PageCount <= 0 || index < PageCount);

    private async Task<bool> LoadCoreAsync(int index, string key, CancellationToken cancellationToken)
    {
        try
        {
            ReportProgress(new ReaderPageProgress(index, ReaderPageLoadStage.ResolvingUrl));
            byte[] payload = await _fetchPage(index, cancellationToken).ConfigureAwait(false);
            if (payload.Length == 0)
            {
                ReportProgress(new ReaderPageProgress(index, ReaderPageLoadStage.Failed));
                return false;
            }

            ReportProgress(new ReaderPageProgress(index, ReaderPageLoadStage.Decoding));
            int edge = EffectiveMaxDecodeEdge;
            ReaderImageDecoder.DecodedPageResult? decoded = ReaderImageDecoder.DecodeWithMetadata(payload, edge, edge, ForceSrgb);
            if (decoded is null)
            {
                _logger.LogDebug("第 {Page} 页无法解码（可能为超大图，需分块渲染）", index);
                ReportProgress(new ReaderPageProgress(index, ReaderPageLoadStage.Failed));
                return false;
            }

            bool animated = decoded.Value.IsAnimated;
            _animated[index] = animated;
            _durations[index] = decoded.Value.FrameDurations;
            if (animated)
            {
                // Only animated pages need raw bytes retained for subsequent frame ticks; keeping static
                // page payloads around leaks hundreds of megabytes across multi-hundred-page galleries.
                StoreAnimatedPayload(index, payload);
            }
            else
            {
                RemoveAnimatedPayload(index);
            }

            // PutAndAcquire pins the entry for the duration of the call, so the immediate release
            // below cannot race with an eviction that would dispose the bitmap before it is cached.
            ReaderPageCache.Lease lease = _cache.PutAndAcquire(key, decoded.Value.Bitmap);
            lease.Dispose();
            ReportProgress(new ReaderPageProgress(index, ReaderPageLoadStage.Completed));
            return true;
        }
        catch (OperationCanceledException)
        {
            ReportProgress(new ReaderPageProgress(index, ReaderPageLoadStage.Failed));
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "第 {Page} 页加载失败", index);
            ReportProgress(new ReaderPageProgress(index, ReaderPageLoadStage.Failed));
            return false;
        }
    }

    /// <summary>
    /// Moves to <paramref name="index"/>, optionally refreshes the prefetch window, and reports the change.
    /// </summary>
    public void GoTo(int index) => GoTo(index, prefetch: true);

    /// <summary>
    /// Moves to <paramref name="index"/>, optionally triggers background prefetching, and raises <see cref="PageChanged"/>.
    /// </summary>
    public void GoTo(int index, bool prefetch)
    {
        if (PageCount > 0)
        {
            index = Math.Clamp(index, 0, PageCount - 1);
        }

        if (index < 0 || index == CurrentPage)
        {
            return;
        }

        int previous = CurrentPage;
        CurrentPage = index;
        PageChanged?.Invoke(index);
        if (prefetch)
        {
            _ = PrefetchAroundAsync(index, previous);
        }
    }

    /// <summary>Advances one page, honouring the current layout direction.</summary>
    public bool Advance(int delta = 1)
    {
        int next = CurrentPage + delta;
        if (PageCount > 0 && (next < 0 || next >= PageCount))
        {
            return false;
        }

        GoTo(next);
        return true;
    }

    /// <summary>Resolves the physical direction for "next page" under the active layout.</summary>
    public bool IsRightToLeft => Layout == ReaderLayout.RightToLeft;

    /// <summary>Updates <see cref="CurrentPage"/> without triggering a prefetch burst (used by continuous scroll sync).</summary>
    public void SyncCurrentPage(int index)
    {
        if (PageCount > 0)
        {
            index = Math.Clamp(index, 0, PageCount - 1);
        }

        if (index >= 0)
        {
            CurrentPage = index;
        }
    }

    /// <summary>Evicts cached decoded data and metadata for <paramref name="index"/> so it can be re-fetched.</summary>
    public void InvalidatePage(int index)
    {
        if (index < 0)
        {
            return;
        }

        _cache.Remove(CacheKey(index));
        _latestProgress.TryRemove(index, out _);
        _animated.TryRemove(index, out _);
        _durations.TryRemove(index, out _);
        RemoveAnimatedPayload(index);
    }

    /// <summary>Starts cancellable, concurrency-bounded, forward-prioritized background decodes around <paramref name="index"/>.</summary>
    public Task PrefetchAroundAsync(int index, int? excludeIndex = null)
    {
        CancellationTokenSource cts;
        lock (_prefetchGate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            CancelPrefetchLocked();
            cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _prefetchCts = cts;
        }

        var candidates = new List<int>(PreloadCount * 2);
        for (int offset = 1; offset <= PreloadCount; offset++)
        {
            int forward = index + offset;
            if (forward != excludeIndex && IsValidIndex(forward) && !_cache.TryGet(CacheKey(forward), out _))
            {
                candidates.Add(forward);
            }
        }

        for (int offset = 1; offset <= PreloadCount; offset++)
        {
            int backward = index - offset;
            if (backward != excludeIndex && IsValidIndex(backward) && !_cache.TryGet(CacheKey(backward), out _))
            {
                candidates.Add(backward);
            }
        }

        if (candidates.Count == 0)
        {
            lock (_prefetchGate)
            {
                if (ReferenceEquals(_prefetchCts, cts))
                {
                    _prefetchCts = null;
                }
            }

            cts.Dispose();
            return Task.CompletedTask;
        }

        return RunPrefetchBatchAsync(cts, candidates);
    }

    private async Task RunPrefetchBatchAsync(CancellationTokenSource cts, List<int> candidates)
    {
        CancellationToken prefetchToken = cts.Token;
        try
        {
            var tasks = new List<Task>(candidates.Count);
            foreach (int candidate in candidates)
            {
                tasks.Add(PrefetchPageAsync(candidate, prefetchToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            lock (_prefetchGate)
            {
                if (ReferenceEquals(_prefetchCts, cts))
                {
                    _prefetchCts = null;
                }
            }

            cts.Dispose();
        }
    }

    private async Task PrefetchPageAsync(int index, CancellationToken prefetchToken)
    {
        if (prefetchToken.IsCancellationRequested || _disposed)
        {
            return;
        }

        string key = CacheKey(index);
        if (_cache.TryGet(key, out _))
        {
            return;
        }

        try
        {
            await _prefetchConcurrency.WaitAsync(prefetchToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (prefetchToken.IsCancellationRequested || _disposed || _cache.TryGet(key, out _))
            {
                return;
            }

            bool startedByPrefetch = false;
            InFlightDecode entry = _inFlight.GetOrAdd(index, i =>
            {
                startedByPrefetch = true;
                return new InFlightDecode(LoadCoreAsync(i, key, prefetchToken), IsPrefetch: true);
            });

            if (!startedByPrefetch)
            {
                return;
            }

            try
            {
                await entry.Task.ConfigureAwait(false);
            }
            finally
            {
                _inFlight.TryRemove(new KeyValuePair<int, InFlightDecode>(index, entry));
            }
        }
        catch (OperationCanceledException)
        {
            // Stale prefetch cancelled when user turned pages rapidly.
        }
        finally
        {
            _prefetchConcurrency.Release();
        }
    }

    private void CancelPrefetch()
    {
        lock (_prefetchGate)
        {
            CancelPrefetchLocked();
        }
    }

    private void CancelPrefetchLocked()
    {
        CancellationTokenSource? cts = _prefetchCts;
        _prefetchCts = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Releases every decoded page belonging to this engine instance; call when leaving the gallery.</summary>
    public void ReleasePages()
    {
        CancelPrefetch();
        _inFlight.Clear();
        _animated.Clear();
        _durations.Clear();
        lock (_payloadGate)
        {
            _payloadIndex.Clear();
            _payloadLruOrder.Clear();
            _totalPayloadBytes = 0;
        }
        _latestProgress.Clear();
        _cache.RemoveByPrefix(_scopePrefix);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        ReleasePages();
        _lifetime.Dispose();
    }

    private bool IsAnimated(int index) => _animated.TryGetValue(index, out bool value) && value;

    private IReadOnlyList<int> Durations(int index)
        => _durations.TryGetValue(index, out IReadOnlyList<int>? value) ? value : [];
}

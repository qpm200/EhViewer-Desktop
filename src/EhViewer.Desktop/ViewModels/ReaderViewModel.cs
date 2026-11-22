using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Spider;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Reader;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;

namespace EhViewer.Desktop.ViewModels;

public sealed partial class StripPageItem : ObservableObject
{
    public StripPageItem(int index)
    {
        Index = index;
    }

    public int Index { get; }

    public int PageNumber => Index + 1;

    public string Label => $"第 {PageNumber} 页";

    [ObservableProperty]
    public partial WriteableBitmap? Image { get; set; }

    [ObservableProperty]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "等待加载...";

    [ObservableProperty]
    public partial double DownloadProgress { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; } = true;
}

public sealed record ReaderBookmarkItem(
    long Gid,
    string Token,
    string Title,
    int Page,
    long Time)
{
    public string PageDisplay => $"P{Page + 1}";

    public string Display => $"{Title} · {PageDisplay}";
}

public sealed record ReaderThumbnailItem(
    int Index,
    int PageNumber,
    string? ThumbUrl,
    int OffsetX = -1,
    int OffsetY = -1,
    int ClipWidth = -1,
    int ClipHeight = -1)
{
    public string Label => $"P{PageNumber}";

    public bool IsClipped => OffsetX >= 0 && OffsetY >= 0 && ClipWidth > 0 && ClipHeight > 0;
}

internal readonly record struct DualPageSpread(
    int PrimaryPage,
    int SecondaryPage,
    int LeftPage,
    int RightPage,
    bool IsSplitSpread);

/// <summary>
/// Reader view model: owns the <see cref="Reader.ReaderEngine"/>, tracks the current page / dual spread /
/// continuous vertical strip, manages <see cref="EhDatabase"/> bookmarks and Windows keep-awake locks,
/// and persists reading progress in the upstream <c>SpiderInfo</c> format.
/// </summary>
/// <remarks>
/// The view model — not the view — owns pinned <see cref="Reader.ReaderPageContent"/> leases for the
/// pages on screen, so decoded bitmaps can never be evicted and disposed from under a render in progress.
/// </remarks>
public sealed partial class ReaderViewModel : ObservableObject, IDisposable
{
    private const int StripWindowBefore = 2;
    private const int StripWindowAfter = 3;
    private const int StripKeepRadius = 5;

    private readonly Reader.ReaderEngine _engine;
    private readonly Reader.ReaderImageFetcher _fetcher;
    private readonly GalleryBrowseService _browse;
    private readonly EhViewer.Core.Network.IEhClientSettings _settings;
    private readonly AppPaths _paths;
    private readonly ILogger<ReaderViewModel> _logger;
    private readonly bool _ownsEngine;
    private readonly Func<DesktopSettings>? _resolveDesktopSettings;
    private readonly EhDatabase? _database;
    private readonly ThumbnailService? _thumbnails;
    private readonly MangaOcrService _ocrService;
    private readonly MangaTranslationPipeline _translationPipeline;

    private GalleryDetail? _detail;
    private long _localBookmarkGid;
    private Reader.ReaderPageContent? _currentPage;
    private Reader.ReaderPageContent? _secondaryPage;
    private bool _leftPageBitmapIsSecondary;
    private string? _progressFilePath;
    private string _referer = EhUrl.RefererE;
    private int _animationFrame;
    private int _stripGeneration;
    private int _showGeneration;
    private volatile int _activeTargetPrimaryPage = -1;
    private volatile int _activeTargetSecondaryPage = -1;
    private volatile bool _isPageLoadingVolatile;
    private volatile bool _isContinuousVerticalVolatile;
    private long _lastStripProgressSaveTickMs;
    private CancellationTokenSource? _showPageCts;
    private CancellationTokenSource? _stripLoadCts;
    private SynchronizationContext? _syncContext;
    private IDisposable? _wakeLock;
    private readonly object _animDecoderGate = new();
    private Reader.ReaderImageDecoder.AnimatedFrameDecoder? _animDecoder;
    private int _animDecoderPageIndex = -1;
    private byte[]? _animDecoderPayload;
    private bool _initializingSettings;
    private bool _disposed;

    public ReaderViewModel(
        Reader.ReaderEngine engine,
        Reader.ReaderImageFetcher fetcher,
        GalleryBrowseService browse,
        EhViewer.Core.Network.IEhClientSettings settings,
        AppPaths paths,
        ILogger<ReaderViewModel> logger,
        bool ownsEngine = false,
        Func<DesktopSettings>? resolveDesktopSettings = null,
        EhDatabase? database = null,
        ThumbnailService? thumbnails = null,
        MangaOcrService? ocrService = null,
        MangaTranslationPipeline? translationPipeline = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _browse = browse ?? throw new ArgumentNullException(nameof(browse));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ownsEngine = ownsEngine;
        _resolveDesktopSettings = resolveDesktopSettings;
        _database = database;
        _thumbnails = thumbnails;
        _ocrService = ocrService ?? new MangaOcrService();
        _translationPipeline = translationPipeline ?? new MangaTranslationPipeline(
            llamaManager: null,
            settingsAccessor: () => _resolveDesktopSettings?.Invoke() ?? new DesktopSettings());
        _syncContext = SynchronizationContext.Current;

        ApplyInitialDesktopSettings();
        _engine.PageChanged += OnEnginePageChanged;
        _fetcher.PageProgressChanged += OnFetcherPageProgressChanged;
        _engine.PageProgressChanged += OnEnginePageProgressChanged;
        UpdateClock();
        RefreshBookmarks();
    }

    [ObservableProperty]
    public partial string GalleryTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int CurrentPageDisplay { get; set; }

    [ObservableProperty]
    public partial int PageCount { get; set; }

    [ObservableProperty]
    public partial string PositionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsPageLoading { get; set; }

    partial void OnIsPageLoadingChanged(bool value)
    {
        _isPageLoadingVolatile = value;
        OnPropertyChanged(nameof(ShowPagePlaceholder));
    }

    [ObservableProperty]
    public partial string PageLoadingTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PageLoadingStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double PageLoadingProgress { get; set; }

    [ObservableProperty]
    public partial bool IsPageLoadingIndeterminate { get; set; } = true;

    [ObservableProperty]
    public partial WriteableBitmap? CurrentImage { get; set; }

    [ObservableProperty]
    public partial WriteableBitmap? LeftPageImage { get; set; }

    [ObservableProperty]
    public partial WriteableBitmap? RightPageImage { get; set; }

    [ObservableProperty]
    public partial bool IsSplitDualSpread { get; set; }

    [ObservableProperty]
    public partial int LayoutIndex { get; set; }

    [ObservableProperty]
    public partial int ScaleIndex { get; set; }

    [ObservableProperty]
    public partial bool DualPageMode { get; set; }

    [ObservableProperty]
    public partial bool DualPageFirstAlone { get; set; } = true;

    [ObservableProperty]
    public partial bool ContinuousScroll { get; set; }

    [ObservableProperty]
    public partial int PreloadCount { get; set; } = 2;

    [ObservableProperty]
    public partial int MaxDecodeEdge { get; set; } = 4096;

    [ObservableProperty]
    public partial bool ForceSrgb { get; set; }

    [ObservableProperty]
    public partial bool ShowPageNumber { get; set; } = true;

    [ObservableProperty]
    public partial bool KeepScreenOn { get; set; } = true;

    [ObservableProperty]
    public partial bool PageTurnByWheel { get; set; } = true;

    [ObservableProperty]
    public partial bool HideReaderChromeInFullscreen { get; set; } = true;

    [ObservableProperty]
    public partial bool IsFullscreen { get; set; }

    [ObservableProperty]
    public partial bool IsAnimated { get; set; }

    [ObservableProperty]
    public partial bool IsCurrentPageBookmarked { get; set; }

    [ObservableProperty]
    public partial string CurrentTimeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OverlayInfoText { get; set; } = string.Empty;

    public ObservableCollection<StripPageItem> StripPages { get; } = [];

    public ObservableCollection<ReaderBookmarkItem> Bookmarks { get; } = [];

    public ObservableCollection<ReaderThumbnailItem> ThumbnailItems { get; } = [];

    public IReadOnlyList<int> MaxDecodeEdgeOptions { get; } = [2048, 3072, 4096, 6144, 8192];

    /// <summary>Current zero-based page index.</summary>
    public int CurrentPage => _engine.CurrentPage;

    public Reader.ReaderEngine Engine => _engine;

    public IReadOnlyList<int> FrameDurations => _currentPage?.FrameDurations ?? [];

    public int PinnedPageIndex => _currentPage?.Index ?? -1;

    public int SecondaryPinnedPageIndex => _secondaryPage?.Index ?? -1;

    public bool IsContinuousVertical => LayoutIndex == (int)Reader.ReaderLayout.TopToBottom;

    public bool ShowSingleSurface => !IsContinuousVertical && !IsSplitDualSpread;

    public bool ShowDualSurface => !IsContinuousVertical && IsSplitDualSpread;

    public bool ShowPagePlaceholder => !IsContinuousVertical && IsPageLoading;

    public bool ShowHeaderBar => !(IsFullscreen && HideReaderChromeInFullscreen);

    public bool ShowFooterBar => !(IsFullscreen && HideReaderChromeInFullscreen);

    public bool CanManageBookmarks => _database is not null && (_detail is not null || _localBookmarkGid > 0);

    public long CurrentGalleryGid => _detail?.Gid ?? _localBookmarkGid;

    private void ApplyInitialDesktopSettings()
    {
        DesktopSettings? ds = _resolveDesktopSettings?.Invoke();
        if (ds is null)
        {
            UpdateKeepAwake();
            return;
        }

        _initializingSettings = true;
        try
        {
            int layout = (ds.ContinuousScroll || ds.DefaultReaderLayout == (int)Reader.ReaderLayout.TopToBottom)
                ? (int)Reader.ReaderLayout.TopToBottom
                : Math.Clamp(ds.DefaultReaderLayout, 0, 2);

            _engine.PreloadCount = Math.Clamp(ds.PreloadCount, 1, 10);
            _engine.Layout = (Reader.ReaderLayout)layout;
            _engine.Scale = (Reader.ReaderScale)Math.Clamp(ds.DefaultReaderScale, 0, 3);
            _engine.MaxDecodeEdge = Math.Clamp(ds.MaxDecodeEdge, 512, 16384);
            _engine.ForceSrgb = ds.ForceSrgb;

            LayoutIndex = layout;
            ScaleIndex = (int)_engine.Scale;
            ContinuousScroll = layout == (int)Reader.ReaderLayout.TopToBottom;
            PreloadCount = _engine.PreloadCount;
            MaxDecodeEdge = _engine.MaxDecodeEdge;
            ForceSrgb = ds.ForceSrgb;
            DualPageMode = ds.DualPageMode;
            DualPageFirstAlone = ds.DualPageFirstAlone;
            ShowPageNumber = ds.ShowPageNumber;
            KeepScreenOn = ds.KeepScreenOn;
            PageTurnByWheel = ds.PageTurnByWheel;
            HideReaderChromeInFullscreen = ds.HideReaderChromeInFullscreen;
        }
        finally
        {
            _initializingSettings = false;
        }

        UpdateKeepAwake();
        _isContinuousVerticalVolatile = IsContinuousVertical;
    }

    /// <summary>Computes the primary/partner page pair and physical Left/Right slots for Dual-Page mode.</summary>
    internal static DualPageSpread ComputeDualPageSpread(
        int pageIndex,
        int pageCount,
        bool dualPageMode,
        bool firstAlone,
        bool isRightToLeft)
    {
        if (pageCount <= 0 || pageIndex < 0)
        {
            return new DualPageSpread(0, -1, 0, -1, false);
        }

        int clamped = Math.Clamp(pageIndex, 0, pageCount - 1);
        if (!dualPageMode)
        {
            return new DualPageSpread(clamped, -1, clamped, -1, false);
        }

        int lead;
        int partner;
        if (firstAlone)
        {
            if (clamped == 0)
            {
                return new DualPageSpread(0, -1, 0, -1, false);
            }

            lead = ((clamped - 1) / 2) * 2 + 1;
            partner = lead + 1 < pageCount ? lead + 1 : -1;
        }
        else
        {
            lead = (clamped / 2) * 2;
            partner = lead + 1 < pageCount ? lead + 1 : -1;
        }

        if (partner < 0)
        {
            return new DualPageSpread(lead, -1, lead, -1, false);
        }

        return isRightToLeft
            ? new DualPageSpread(lead, partner, partner, lead, true)
            : new DualPageSpread(lead, partner, lead, partner, true);
    }

    /// <summary>Computes the target zero-based page for stepping +1/-1 cleanly across single or dual spreads.</summary>
    internal static int? ComputeStepTarget(
        int currentPage,
        int pageCount,
        int direction,
        bool dualPageMode,
        bool firstAlone)
    {
        if (direction == 0)
        {
            return null;
        }

        int step = direction > 0 ? 1 : -1;
        if (!dualPageMode)
        {
            int next = currentPage + step;
            if (pageCount > 0 && (next < 0 || next >= pageCount))
            {
                return null;
            }

            return next < 0 ? null : next;
        }

        if (pageCount <= 0)
        {
            int next = currentPage + step;
            return next < 0 ? null : next;
        }

        DualPageSpread current = ComputeDualPageSpread(currentPage, pageCount, dualPageMode: true, firstAlone, isRightToLeft: false);
        if (step > 0)
        {
            int next = (current.SecondaryPage >= 0 ? current.SecondaryPage : current.PrimaryPage) + 1;
            return next >= pageCount ? null : next;
        }

        int prevProbe = current.PrimaryPage - 1;
        if (prevProbe < 0)
        {
            return null;
        }

        return ComputeDualPageSpread(prevProbe, pageCount, dualPageMode: true, firstAlone, isRightToLeft: false).PrimaryPage;
    }

    public Task OpenAsync(GalleryInfo info, CancellationToken cancellationToken = default)
        => OpenAsync(info, startPage: null, cancellationToken);

    public async Task OpenAsync(GalleryInfo info, int? startPage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);

        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            GalleryDetail detail = await _browse.GetDetailAsync(info, cancellationToken).ConfigureAwait(true);
            await OpenDetailAsync(detail, startPage, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "已取消";
        }
        catch (Exception ex)
        {
            ErrorMessage = GalleryBrowseService.DescribeFailure(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task OpenDetailAsync(GalleryDetail detail, int? startPage = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);

        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            _detail = detail;
            _localBookmarkGid = detail.Gid;
            _referer = EhUrl.GetReferer(_settings.GallerySite);
            GalleryTitle = EhUtils.GetSuitableTitle(detail) ?? string.Empty;
            PageCount = detail.Pages > 0 ? detail.Pages : 0;
            _engine.PageCount = PageCount;

            var pageUrls = new List<string?>(PageCount);
            PreviewSet? previews = detail.PreviewSet ?? detail.SpiderInfoPreviewSet;
            if (previews is not null)
            {
                for (int i = 0; i < previews.Size; i++)
                {
                    pageUrls.Add(previews.GetPageUrlAt(i));
                }
            }

            string downloadRoot = _paths.GetEffectiveDownloadDirectory(_resolveDesktopSettings?.Invoke());
            _progressFilePath = BuildProgressPath(downloadRoot, detail);
            string localDirectory = Path.GetDirectoryName(_progressFilePath) ?? downloadRoot;

            _fetcher.Bind(detail, pageUrls, _referer, localDirectory);
            RebuildStripAndThumbnails(previews);

            int target = startPage ?? ReadSavedStartPage(_progressFilePath);
            _engine.GoTo(target);
            CurrentPageDisplay = _engine.CurrentPage;
            UpdatePositionText();
            RefreshBookmarks();
            UpdateKeepAwake();

            await ShowPageAsync(_engine.CurrentPage, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "已取消";
        }
        catch (Exception ex)
        {
            ErrorMessage = GalleryBrowseService.DescribeFailure(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task OpenLocalAsync(
        string title,
        string directory,
        int startPage,
        int pageCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            _detail = null;
            _progressFilePath = null;
            _localBookmarkGid = TryParseLocalGid(directory);
            GalleryTitle = title ?? string.Empty;
            PageCount = Math.Max(0, pageCount);
            _engine.PageCount = PageCount;

            _fetcher.BindLocal(directory);
            RebuildStripAndThumbnails(previews: null);

            _engine.GoTo(Math.Max(0, startPage));
            CurrentPageDisplay = _engine.CurrentPage;
            UpdatePositionText();
            RefreshBookmarks();
            UpdateKeepAwake();

            await ShowPageAsync(_engine.CurrentPage, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "已取消";
        }
        catch (Exception ex)
        {
            ErrorMessage = GalleryBrowseService.DescribeFailure(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static long TryParseLocalGid(string directory)
    {
        string folderName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        int dash = folderName.IndexOf('-');
        string gidText = dash > 0 ? folderName[..dash] : folderName;
        return long.TryParse(gidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long gid) && gid > 0
            ? gid
            : 0;
    }

    private void RebuildStripAndThumbnails(PreviewSet? previews)
    {
        _stripGeneration++;
        StripPages.Clear();
        ThumbnailItems.Clear();

        long gid = _detail?.Gid ?? _localBookmarkGid;
        for (int i = 0; i < PageCount; i++)
        {
            StripPages.Add(new StripPageItem(i));
            if (previews is not null && i < previews.Size)
            {
                GalleryPreview preview = previews.GetGalleryPreview(gid, i);
                ThumbnailItems.Add(new ReaderThumbnailItem(
                    i,
                    i + 1,
                    preview.ImageUrl,
                    preview.OffsetX,
                    preview.OffsetY,
                    preview.ClipWidth,
                    preview.ClipHeight));
            }
            else
            {
                ThumbnailItems.Add(new ReaderThumbnailItem(i, i + 1, null));
            }
        }
    }

    /// <summary>
    /// Decodes (and crops, for sprite-sheet previews) a thumbnail tile through <see cref="ThumbnailService"/>
    /// with EH cookies/Referer and safe pixel isolation.
    /// </summary>
    public async Task<WriteableBitmap?> LoadThumbnailBitmapAsync(
        ReaderThumbnailItem item,
        CancellationToken cancellationToken = default)
    {
        if (_thumbnails is null || string.IsNullOrWhiteSpace(item.ThumbUrl))
        {
            return null;
        }

        SKRectI? clip = item.IsClipped
            ? new SKRectI(item.OffsetX, item.OffsetY, item.OffsetX + item.ClipWidth, item.OffsetY + item.ClipHeight)
            : null;

        DecodedPixelBuffer? pixels = await _thumbnails
            .LoadPixelsAsync(item.ThumbUrl, item.IsClipped ? int.MaxValue : 220, clip, cancellationToken)
            .ConfigureAwait(true);

        return pixels is null ? null : SkiaBitmapHelper.ToWriteableBitmap(pixels);
    }

    /// <summary>
    /// Updates visible page position and placeholder immediately during rapid slider scrubbing
    /// before firing the actual remote fetch.
    /// </summary>
    public void PreviewTargetPage(int index)
    {
        if (PageCount > 0)
        {
            index = Math.Clamp(index, 0, PageCount - 1);
        }
        else if (index < 0)
        {
            return;
        }

        _syncContext ??= SynchronizationContext.Current;
        _engine.GoTo(index, prefetch: false);
        ErrorMessage = string.Empty;

        if (IsContinuousVertical)
        {
            ClearPageLoadingState();
            UpdatePositionText();
            RefreshCurrentBookmarkFlag();
            return;
        }

        if (DualPageMode && PageCount > 0)
        {
            DualPageSpread spread = ComputeDualPageSpread(
                _engine.CurrentPage,
                PageCount,
                dualPageMode: true,
                DualPageFirstAlone,
                _engine.IsRightToLeft);

            if (spread.IsSplitSpread)
            {
                Reader.ReaderPageContent? p2 = null;
                if (_engine.TryGetCachedPage(spread.PrimaryPage, out Reader.ReaderPageContent? p1) &&
                    _engine.TryGetCachedPage(spread.SecondaryPage, out p2) &&
                    p1 is not null && p2 is not null)
                {
                    p1.Dispose();
                    p2.Dispose();
                    UpdatePositionText(spread);
                    return;
                }

                p1?.Dispose();
                p2?.Dispose();
                UpdatePositionText(spread);
                BeginPagePlaceholder(spread.PrimaryPage, spread.SecondaryPage);
                return;
            }
        }

        if (_engine.TryGetCachedPage(_engine.CurrentPage, out Reader.ReaderPageContent? cached) && cached is not null)
        {
            cached.Dispose();
            return;
        }

        BeginPagePlaceholder(_engine.CurrentPage, -1);
    }

    /// <summary>Shows an explicit page, updating Single, Dual-Spread, or Continuous Vertical surface.</summary>
    public async Task ShowPageAsync(int index, CancellationToken cancellationToken = default)
    {
        _syncContext ??= SynchronizationContext.Current;
        CancellationTokenSource? previousCts = _showPageCts;
        var currentCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _showPageCts = currentCts;
        previousCts?.Cancel();
        previousCts?.Dispose();

        int generation = Interlocked.Increment(ref _showGeneration);
        CancellationToken ct = currentCts.Token;

        _engine.GoTo(index, prefetch: false);
        ErrorMessage = string.Empty;

        if (IsContinuousVertical)
        {
            ClearPageLoadingState();
            UpdatePositionText();
            RefreshCurrentBookmarkFlag();
            SaveProgress();
            await LoadStripWindowAsync(_engine.CurrentPage, ct).ConfigureAwait(true);
            if (generation == _showGeneration && !ct.IsCancellationRequested)
            {
                _ = _engine.PrefetchAroundAsync(_engine.CurrentPage);
            }
            return;
        }

        if (DualPageMode && PageCount > 0)
        {
            DualPageSpread spread = ComputeDualPageSpread(
                _engine.CurrentPage,
                PageCount,
                dualPageMode: true,
                DualPageFirstAlone,
                _engine.IsRightToLeft);

            if (spread.IsSplitSpread)
            {
                UpdatePositionText(spread);

                Reader.ReaderPageContent? cachedSecondary = null;
                if (_engine.TryGetCachedPage(spread.PrimaryPage, out Reader.ReaderPageContent? cachedPrimary) &&
                    _engine.TryGetCachedPage(spread.SecondaryPage, out cachedSecondary) &&
                    cachedPrimary is not null && cachedSecondary is not null)
                {
                    ClearPageLoadingState();
                    await PresentDualSpreadAsync(spread, cachedPrimary, cachedSecondary, generation, ct).ConfigureAwait(true);
                    if (generation == _showGeneration && !ct.IsCancellationRequested)
                    {
                        _ = _engine.PrefetchAroundAsync(spread.SecondaryPage);
                    }
                    return;
                }

                cachedPrimary?.Dispose();
                cachedSecondary?.Dispose();
                BeginPagePlaceholder(spread.PrimaryPage, spread.SecondaryPage);

                Task<Reader.ReaderPageContent?> primaryTask = _engine.GetPageAsync(spread.PrimaryPage, ct);
                Task<Reader.ReaderPageContent?> secondaryTask = _engine.GetPageAsync(spread.SecondaryPage, ct);
                await Task.WhenAll(primaryTask, secondaryTask).ConfigureAwait(true);

                Reader.ReaderPageContent? primary = primaryTask.Result;
                Reader.ReaderPageContent? secondary = secondaryTask.Result;
                if (generation != _showGeneration || ct.IsCancellationRequested)
                {
                    primary?.Dispose();
                    secondary?.Dispose();
                    return;
                }

                if (primary is not null && secondary is not null)
                {
                    ErrorMessage = string.Empty;
                    await PresentDualSpreadAsync(spread, primary, secondary, generation, ct).ConfigureAwait(true);
                    if (generation == _showGeneration && !ct.IsCancellationRequested)
                    {
                        _ = _engine.PrefetchAroundAsync(spread.SecondaryPage);
                    }
                    return;
                }

                // If partner unavailable, release any acquired side safely and fall back to single-page display.
                secondary?.Dispose();
                if (primary is not null)
                {
                    ErrorMessage = string.Empty;
                    await PresentPageAsync(primary, generation, ct).ConfigureAwait(true);
                    if (generation == _showGeneration && !ct.IsCancellationRequested)
                    {
                        _ = _engine.PrefetchAroundAsync(_engine.CurrentPage);
                    }
                    return;
                }

                ClearPageLoadingState();
                ErrorMessage = "该页暂不可用。";
                return;
            }
        }

        if (_engine.TryGetCachedPage(_engine.CurrentPage, out Reader.ReaderPageContent? cachedPage) && cachedPage is not null)
        {
            ClearPageLoadingState();
            await PresentPageAsync(cachedPage, generation, ct).ConfigureAwait(true);
            if (generation == _showGeneration && !ct.IsCancellationRequested)
            {
                _ = _engine.PrefetchAroundAsync(_engine.CurrentPage);
            }
            return;
        }

        BeginPagePlaceholder(_engine.CurrentPage, -1);
        Reader.ReaderPageContent? page = await _engine.GetPageAsync(_engine.CurrentPage, ct).ConfigureAwait(true);

        if (generation != _showGeneration || ct.IsCancellationRequested)
        {
            page?.Dispose();
            return;
        }

        if (page is null)
        {
            ClearPageLoadingState();
            ErrorMessage = "该页暂不可用。";
            return;
        }

        ErrorMessage = string.Empty;
        await PresentPageAsync(page, generation, ct).ConfigureAwait(true);

        if (generation == _showGeneration && !ct.IsCancellationRequested)
        {
            _ = _engine.PrefetchAroundAsync(_engine.CurrentPage);
        }
    }

    private async Task PresentPageAsync(Reader.ReaderPageContent page, int generation, CancellationToken cancellationToken)
    {
        WriteableBitmap? presentedBitmap = null;
        if (SkiaBitmapHelper.CanCopyPixelsDirectly(page.Bitmap))
        {
            if (generation != _showGeneration || cancellationToken.IsCancellationRequested)
            {
                page.Dispose();
                return;
            }

            try
            {
                presentedBitmap = SkiaBitmapHelper.ToWriteableBitmapDirect(page.Bitmap);
            }
            catch (Exception ex)
            {
                page.Dispose();
                if (generation == _showGeneration)
                {
                    ClearPageLoadingState();
                    _logger.LogWarning(ex, "第 {Page} 页直接像素传输失败", page.Index);
                    ErrorMessage = "该页无法渲染。";
                }
                return;
            }
        }
        else
        {
            DecodedPixelBuffer? pixels;
            try
            {
                pixels = await Task
                    .Run(() => SkiaBitmapHelper.ExtractBgraPixels(page.Bitmap), cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                page.Dispose();
                return;
            }
            catch (Exception ex)
            {
                page.Dispose();
                if (generation == _showGeneration)
                {
                    ClearPageLoadingState();
                    _logger.LogWarning(ex, "第 {Page} 页像素提取失败", page.Index);
                    ErrorMessage = "该页无法渲染。";
                }
                return;
            }

            if (generation != _showGeneration || cancellationToken.IsCancellationRequested)
            {
                page.Dispose();
                return;
            }

            if (pixels is not null)
            {
                presentedBitmap = SkiaBitmapHelper.ToWriteableBitmap(pixels);
            }
        }

        if (presentedBitmap is null)
        {
            page.Dispose();
            ClearPageLoadingState();
            ErrorMessage = "该页无法渲染。";
            return;
        }

        ClearPageLoadingState();

        // Update backing page references BEFORE firing image PropertyChanged so OCR/overlay observers read the new page.
        Reader.ReaderPageContent? prevPrimary = _currentPage;
        Reader.ReaderPageContent? prevSecondary = _secondaryPage;
        _currentPage = page;
        _secondaryPage = null;
        _leftPageBitmapIsSecondary = false;
        IsSplitDualSpread = false;
        prevPrimary?.Dispose();
        prevSecondary?.Dispose();
        if (!page.IsAnimated)
        {
            DisposeAnimatedDecoder();
        }

        CurrentImage = presentedBitmap;
        LeftPageImage = null;
        RightPageImage = null;

        IsAnimated = page.IsAnimated && page.FrameDurations.Count > 1;
        _animationFrame = 0;
        CurrentPageDisplay = page.Index;
        UpdatePositionText();
        RefreshCurrentBookmarkFlag();
        SaveProgress();
        NotifySurfaceVisibilityChanged();
    }

    private async Task PresentDualSpreadAsync(
        DualPageSpread spread,
        Reader.ReaderPageContent primary,
        Reader.ReaderPageContent secondary,
        int generation,
        CancellationToken cancellationToken)
    {
        WriteableBitmap? primaryBmp = null;
        WriteableBitmap? secondaryBmp = null;
        bool directPrimary = SkiaBitmapHelper.CanCopyPixelsDirectly(primary.Bitmap);
        bool directSecondary = SkiaBitmapHelper.CanCopyPixelsDirectly(secondary.Bitmap);

        if (directPrimary && directSecondary)
        {
            if (generation != _showGeneration || cancellationToken.IsCancellationRequested)
            {
                primary.Dispose();
                secondary.Dispose();
                return;
            }

            try
            {
                primaryBmp = SkiaBitmapHelper.ToWriteableBitmapDirect(primary.Bitmap);
                secondaryBmp = SkiaBitmapHelper.ToWriteableBitmapDirect(secondary.Bitmap);
            }
            catch (Exception ex)
            {
                primary.Dispose();
                secondary.Dispose();
                if (generation == _showGeneration)
                {
                    ClearPageLoadingState();
                    _logger.LogWarning(ex, "双页直接像素传输失败：{P1}/{P2}", spread.PrimaryPage, spread.SecondaryPage);
                    ErrorMessage = "该页无法渲染。";
                }
                return;
            }
        }
        else
        {
            DecodedPixelBuffer? primaryPixels = null;
            DecodedPixelBuffer? secondaryPixels = null;
            try
            {
                Task<DecodedPixelBuffer?> t1 = directPrimary
                    ? Task.FromResult<DecodedPixelBuffer?>(null)
                    : Task.Run(() => SkiaBitmapHelper.ExtractBgraPixels(primary.Bitmap), cancellationToken);
                Task<DecodedPixelBuffer?> t2 = directSecondary
                    ? Task.FromResult<DecodedPixelBuffer?>(null)
                    : Task.Run(() => SkiaBitmapHelper.ExtractBgraPixels(secondary.Bitmap), cancellationToken);
                await Task.WhenAll(t1, t2).ConfigureAwait(true);
                primaryPixels = t1.Result;
                secondaryPixels = t2.Result;
            }
            catch (OperationCanceledException)
            {
                primary.Dispose();
                secondary.Dispose();
                return;
            }
            catch (Exception ex)
            {
                primary.Dispose();
                secondary.Dispose();
                if (generation == _showGeneration)
                {
                    ClearPageLoadingState();
                    _logger.LogWarning(ex, "双页像素提取失败：{P1}/{P2}", spread.PrimaryPage, spread.SecondaryPage);
                    ErrorMessage = "该页无法渲染。";
                }
                return;
            }

            if (generation != _showGeneration || cancellationToken.IsCancellationRequested)
            {
                primary.Dispose();
                secondary.Dispose();
                return;
            }

            primaryBmp = directPrimary
                ? SkiaBitmapHelper.ToWriteableBitmapDirect(primary.Bitmap)
                : (primaryPixels is null ? null : SkiaBitmapHelper.ToWriteableBitmap(primaryPixels));
            secondaryBmp = directSecondary
                ? SkiaBitmapHelper.ToWriteableBitmapDirect(secondary.Bitmap)
                : (secondaryPixels is null ? null : SkiaBitmapHelper.ToWriteableBitmap(secondaryPixels));
        }

        if (primaryBmp is null || secondaryBmp is null)
        {
            primary.Dispose();
            secondary.Dispose();
            ClearPageLoadingState();
            ErrorMessage = "该页无法渲染。";
            return;
        }

        ClearPageLoadingState();

        // Update backing page references BEFORE firing image PropertyChanged so OCR/overlay observers read the new spread.
        Reader.ReaderPageContent? prevPrimary = _currentPage;
        Reader.ReaderPageContent? prevSecondary = _secondaryPage;
        _currentPage = primary;
        _secondaryPage = secondary;
        _leftPageBitmapIsSecondary = spread.LeftPage != spread.PrimaryPage;
        IsSplitDualSpread = true;
        prevPrimary?.Dispose();
        prevSecondary?.Dispose();
        DisposeAnimatedDecoder();

        if (!_leftPageBitmapIsSecondary)
        {
            LeftPageImage = primaryBmp;
            RightPageImage = secondaryBmp;
        }
        else
        {
            LeftPageImage = secondaryBmp;
            RightPageImage = primaryBmp;
        }

        CurrentImage = primaryBmp;

        IsAnimated = false;
        _animationFrame = 0;
        CurrentPageDisplay = _engine.CurrentPage;
        UpdatePositionText(spread);
        RefreshCurrentBookmarkFlag();
        SaveProgress();
        NotifySurfaceVisibilityChanged();
    }

    private void ClearActivePageVisuals()
    {
        Reader.ReaderPageContent? prevPrimary = _currentPage;
        Reader.ReaderPageContent? prevSecondary = _secondaryPage;
        _currentPage = null;
        _secondaryPage = null;
        _leftPageBitmapIsSecondary = false;
        prevPrimary?.Dispose();
        prevSecondary?.Dispose();
        DisposeAnimatedDecoder();

        IsAnimated = false;
        _animationFrame = 0;
        IsSplitDualSpread = false;
        CurrentImage = null;
        LeftPageImage = null;
        RightPageImage = null;
        NotifySurfaceVisibilityChanged();
    }

    private void BeginPagePlaceholder(int primaryPage, int secondaryPage)
    {
        _activeTargetPrimaryPage = primaryPage;
        _activeTargetSecondaryPage = secondaryPage;
        _isPageLoadingVolatile = true;
        ClearActivePageVisuals();

        if (secondaryPage >= 0 && secondaryPage != primaryPage)
        {
            int minPage = Math.Min(primaryPage, secondaryPage) + 1;
            int maxPage = Math.Max(primaryPage, secondaryPage) + 1;
            PageLoadingTitle = PageCount > 0 ? $"第 {minPage}-{maxPage} / {PageCount} 页" : $"第 {minPage}-{maxPage} 页";
        }
        else
        {
            PageLoadingTitle = PageCount > 0 ? $"第 {primaryPage + 1} / {PageCount} 页" : $"第 {primaryPage + 1} 页";
        }

        if (_engine.TryGetProgress(primaryPage, out ReaderPageProgress existing)
            || (secondaryPage >= 0 && _engine.TryGetProgress(secondaryPage, out existing)))
        {
            (string status, double pct, bool indeterminate) = FormatProgressDisplay(existing);
            PageLoadingStatus = status;
            PageLoadingProgress = pct;
            IsPageLoadingIndeterminate = indeterminate;
        }
        else
        {
            PageLoadingStatus = "正在加载页面内容...";
            PageLoadingProgress = 0;
            IsPageLoadingIndeterminate = true;
        }

        IsPageLoading = true;
    }

    private void ClearPageLoadingState()
    {
        _activeTargetPrimaryPage = -1;
        _activeTargetSecondaryPage = -1;
        _isPageLoadingVolatile = false;
        IsPageLoading = false;
        PageLoadingStatus = string.Empty;
        PageLoadingProgress = 0;
        IsPageLoadingIndeterminate = true;
    }

    /// <summary>Loads a sliding window of pages for continuous vertical scroll mode and prunes distant bitmaps.</summary>
    public async Task LoadStripWindowAsync(int centerIndex, CancellationToken cancellationToken = default)
    {
        if (PageCount <= 0 || StripPages.Count == 0)
        {
            return;
        }

        _syncContext ??= SynchronizationContext.Current;
        CancellationTokenSource? prevStripCts = _stripLoadCts;
        var currentStripCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _stripLoadCts = currentStripCts;
        prevStripCts?.Cancel();
        prevStripCts?.Dispose();

        int generation = Interlocked.Increment(ref _stripGeneration);
        CancellationToken ct = currentStripCts.Token;

        int minKeep = Math.Max(0, centerIndex - StripKeepRadius);
        int maxKeep = Math.Min(StripPages.Count - 1, centerIndex + StripKeepRadius);
        for (int i = 0; i < StripPages.Count; i++)
        {
            if (i < minKeep || i > maxKeep)
            {
                StripPageItem distant = StripPages[i];
                if (distant.Image is not null || distant.IsLoaded)
                {
                    distant.Image = null;
                    distant.IsLoaded = false;
                    distant.StatusText = "等待加载...";
                    distant.DownloadProgress = 0;
                    distant.IsProgressIndeterminate = true;
                }
            }
        }

        int start = Math.Max(0, centerIndex - StripWindowBefore);
        int end = Math.Min(StripPages.Count - 1, centerIndex + StripWindowAfter);
        var order = new List<int>(end - start + 1);
        int clampedCenter = Math.Clamp(centerIndex, start, end);
        order.Add(clampedCenter);
        int maxRadius = Math.Max(clampedCenter - start, end - clampedCenter);
        for (int offset = 1; offset <= maxRadius; offset++)
        {
            if (clampedCenter + offset <= end)
            {
                order.Add(clampedCenter + offset);
            }
            if (clampedCenter - offset >= start)
            {
                order.Add(clampedCenter - offset);
            }
        }

        foreach (int idx in order)
        {
            if (ct.IsCancellationRequested || generation != _stripGeneration || !IsContinuousVertical)
            {
                return;
            }

            StripPageItem item = StripPages[idx];
            if (item.IsLoaded && item.Image is not null)
            {
                continue;
            }

            if (_engine.TryGetProgress(idx, out ReaderPageProgress existing))
            {
                (string status, double pct, bool indeterminate) = FormatProgressDisplay(existing);
                item.StatusText = status;
                item.DownloadProgress = pct;
                item.IsProgressIndeterminate = indeterminate;
            }
            else
            {
                item.StatusText = "正在加载页面...";
                item.DownloadProgress = 0;
                item.IsProgressIndeterminate = true;
            }

            Reader.ReaderPageContent? content = await _engine.GetPageAsync(idx, ct).ConfigureAwait(true);
            if (content is null)
            {
                if (generation == _stripGeneration && !ct.IsCancellationRequested)
                {
                    item.StatusText = "该页暂不可用";
                    item.DownloadProgress = 0;
                    item.IsProgressIndeterminate = false;
                }
                continue;
            }

            WriteableBitmap? stripBitmap = null;
            try
            {
                if (SkiaBitmapHelper.CanCopyPixelsDirectly(content.Bitmap))
                {
                    if (generation == _stripGeneration && !ct.IsCancellationRequested)
                    {
                        stripBitmap = SkiaBitmapHelper.ToWriteableBitmapDirect(content.Bitmap);
                    }
                }
                else
                {
                    DecodedPixelBuffer? pixels = await Task.Run(() => SkiaBitmapHelper.ExtractBgraPixels(content.Bitmap), ct)
                        .ConfigureAwait(true);
                    if (pixels is not null && generation == _stripGeneration && !ct.IsCancellationRequested)
                    {
                        stripBitmap = SkiaBitmapHelper.ToWriteableBitmap(pixels);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                content.Dispose();
            }

            if (stripBitmap is not null && generation == _stripGeneration && !ct.IsCancellationRequested)
            {
                item.Image = stripBitmap;
                item.IsLoaded = true;
                item.StatusText = string.Empty;
                item.DownloadProgress = 100;
                item.IsProgressIndeterminate = false;
            }
        }
    }

    public async Task<bool> RenderAnimationFrameAsync(int frameIndex, CancellationToken cancellationToken = default)
    {
        Reader.ReaderPageContent? page = _currentPage;
        if (page is null || !page.IsAnimated || frameIndex < 0)
        {
            return false;
        }

        if (!_engine.TryGetPayload(page.Index, out byte[]? payload) || payload is null)
        {
            return false;
        }

        int edge = _engine.EffectiveMaxDecodeEdge;
        bool forceSrgb = ForceSrgb;
        int pageIndex = page.Index;
        SKBitmap? decodedFrame;
        try
        {
            decodedFrame = await Task.Run(
                () =>
                {
                    lock (_animDecoderGate)
                    {
                        if (_disposed)
                        {
                            return null;
                        }

                        if (_animDecoder is null ||
                            _animDecoderPageIndex != pageIndex ||
                            !ReferenceEquals(_animDecoderPayload, payload))
                        {
                            _animDecoder?.Dispose();
                            _animDecoder = Reader.ReaderImageDecoder.AnimatedFrameDecoder.TryCreate(payload);
                            _animDecoderPageIndex = pageIndex;
                            _animDecoderPayload = payload;
                        }

                        return _animDecoder?.DecodeFrame(frameIndex, edge, forceSrgb);
                    }
                },
                cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "第 {Frame} 帧解码失败", frameIndex);
            return false;
        }

        if (decodedFrame is null)
        {
            return false;
        }

        using (decodedFrame)
        {
            if (_currentPage != page)
            {
                return false;
            }

            _animationFrame = frameIndex;
            WriteableBitmap? existing = CurrentImage as WriteableBitmap;
            WriteableBitmap? updated = SkiaBitmapHelper.ToWriteableBitmapDirect(decodedFrame, existing);
            if (updated is null)
            {
                return false;
            }

            if (!ReferenceEquals(updated, CurrentImage))
            {
                CurrentImage = updated;
            }

            return true;
        }
    }

    private void DisposeAnimatedDecoder()
    {
        lock (_animDecoderGate)
        {
            _animDecoder?.Dispose();
            _animDecoder = null;
            _animDecoderPageIndex = -1;
            _animDecoderPayload = null;
        }
    }

    public async Task<int> AdvanceAnimationAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<int> durations = FrameDurations;
        int count = durations.Count;
        if (count <= 1)
        {
            return 0;
        }

        int next = (_animationFrame + 1) % count;
        if (!await RenderAnimationFrameAsync(next, cancellationToken).ConfigureAwait(true))
        {
            return 0;
        }

        return Math.Max(20, durations[next]);
    }

    public Task NextPageAsync(CancellationToken cancellationToken = default)
    {
        int? target = ComputeStepTarget(
            _engine.CurrentPage,
            PageCount,
            direction: 1,
            dualPageMode: DualPageMode && !IsContinuousVertical,
            DualPageFirstAlone);

        if (!target.HasValue)
        {
            return Task.CompletedTask;
        }

        return ShowPageAsync(target.Value, cancellationToken);
    }

    public Task PreviousPageAsync(CancellationToken cancellationToken = default)
    {
        int? target = ComputeStepTarget(
            _engine.CurrentPage,
            PageCount,
            direction: -1,
            dualPageMode: DualPageMode && !IsContinuousVertical,
            DualPageFirstAlone);

        if (!target.HasValue)
        {
            return Task.CompletedTask;
        }

        return ShowPageAsync(target.Value, cancellationToken);
    }

    public void UpdateStripVisiblePage(int visibleIndex)
    {
        if (!IsContinuousVertical || PageCount <= 0)
        {
            return;
        }

        int clamped = Math.Clamp(visibleIndex, 0, PageCount - 1);
        if (clamped == _engine.CurrentPage && clamped == CurrentPageDisplay)
        {
            return;
        }

        _engine.SyncCurrentPage(clamped);
        CurrentPageDisplay = clamped;
        UpdatePositionText();
        RefreshCurrentBookmarkFlag();

        long now = Environment.TickCount64;
        if (now - _lastStripProgressSaveTickMs >= 1500)
        {
            _lastStripProgressSaveTickMs = now;
            SaveProgress();
        }
    }

    public Task RetryCurrentPageAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = string.Empty;
        _engine.InvalidatePage(_engine.CurrentPage);
        if (_secondaryPage is { Index: >= 0 } sec)
        {
            _engine.InvalidatePage(sec.Index);
        }

        return ShowPageAsync(_engine.CurrentPage, cancellationToken);
    }

    public bool ToggleBookmark()
    {
        if (_database is null)
        {
            ErrorMessage = "当前环境未启用书签数据库。";
            return false;
        }

        long gid = _detail?.Gid ?? _localBookmarkGid;
        if (gid <= 0)
        {
            ErrorMessage = "该离线本地画廊缺少 GID，无法保存书签。";
            return false;
        }

        BookmarkInfo? existing = _database.GetBookmark(gid);
        if (existing is not null && existing.Page == _engine.CurrentPage)
        {
            _database.RemoveBookmark(gid);
            IsCurrentPageBookmarked = false;
            RefreshBookmarks();
            return false;
        }

        GalleryInfo source = _detail is not null
            ? _detail
            : new GalleryInfo { Gid = gid, Title = GalleryTitle, Pages = PageCount };

        var bookmark = new BookmarkInfo(source)
        {
            Page = _engine.CurrentPage,
            Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        _database.PutBookmark(bookmark);
        IsCurrentPageBookmarked = true;
        RefreshBookmarks();
        return true;
    }

    public void RemoveBookmark(long gid)
    {
        _database?.RemoveBookmark(gid);
        RefreshBookmarks();
    }

    public void RefreshBookmarks()
    {
        Bookmarks.Clear();
        if (_database is null)
        {
            IsCurrentPageBookmarked = false;
            return;
        }

        try
        {
            foreach (BookmarkInfo bm in _database.GetAllBookmarks())
            {
                Bookmarks.Add(new ReaderBookmarkItem(
                    bm.Gid,
                    bm.Token ?? string.Empty,
                    EhUtils.GetSuitableTitle(bm) ?? bm.Title ?? $"画廊 {bm.Gid}",
                    bm.Page,
                    bm.Time));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取阅读书签失败");
        }

        RefreshCurrentBookmarkFlag();
    }

    public void RefreshCurrentBookmarkFlag()
    {
        long gid = _detail?.Gid ?? _localBookmarkGid;
        IsCurrentPageBookmarked = gid > 0
            && Bookmarks.Any(b => b.Gid == gid && b.Page == _engine.CurrentPage);
        UpdateOverlayText();
    }

    public void UpdateClock()
    {
        CurrentTimeText = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        UpdateOverlayText();
    }

    public async Task RefreshCurrentViewAsync(bool clearCacheFirst = false, CancellationToken cancellationToken = default)
    {
        if (clearCacheFirst)
        {
            Reader.ReaderPageContent? p1 = _currentPage;
            Reader.ReaderPageContent? p2 = _secondaryPage;
            _currentPage = null;
            _secondaryPage = null;
            p1?.Dispose();
            p2?.Dispose();
            _engine.ReleasePages();

            foreach (StripPageItem item in StripPages)
            {
                item.Image = null;
                item.IsLoaded = false;
            }
        }

        if (PageCount > 0 || _detail is not null || !string.IsNullOrEmpty(GalleryTitle))
        {
            await ShowPageAsync(_engine.CurrentPage, cancellationToken).ConfigureAwait(true);
        }
    }

    partial void OnLayoutIndexChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, 2);
        _engine.Layout = (Reader.ReaderLayout)clamped;
        if (!_initializingSettings)
        {
            ContinuousScroll = clamped == (int)Reader.ReaderLayout.TopToBottom;
            NotifySurfaceVisibilityChanged();
            _ = RefreshCurrentViewAsync();
        }
    }

    partial void OnScaleIndexChanged(int value)
    {
        _engine.Scale = (Reader.ReaderScale)Math.Clamp(value, 0, 3);
    }

    partial void OnContinuousScrollChanged(bool value)
    {
        if (_initializingSettings)
        {
            return;
        }

        int desiredLayout = value
            ? (int)Reader.ReaderLayout.TopToBottom
            : (LayoutIndex == (int)Reader.ReaderLayout.TopToBottom ? (int)Reader.ReaderLayout.LeftToRight : LayoutIndex);
        if (LayoutIndex != desiredLayout)
        {
            LayoutIndex = desiredLayout;
        }
    }

    partial void OnDualPageModeChanged(bool value)
    {
        if (_initializingSettings)
        {
            return;
        }

        if (!value)
        {
            IsSplitDualSpread = false;
            LeftPageImage = null;
            RightPageImage = null;
        }

        NotifySurfaceVisibilityChanged();
        _ = RefreshCurrentViewAsync();
    }

    partial void OnDualPageFirstAloneChanged(bool value)
    {
        if (!_initializingSettings && DualPageMode)
        {
            _ = RefreshCurrentViewAsync();
        }
    }

    partial void OnPreloadCountChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 10);
        if (clamped != value)
        {
            PreloadCount = clamped;
            return;
        }

        _engine.PreloadCount = clamped;
        if (!_initializingSettings)
        {
            _ = _engine.PrefetchAroundAsync(_engine.CurrentPage);
        }
    }

    partial void OnMaxDecodeEdgeChanged(int value)
    {
        int clamped = Math.Clamp(value, 512, 16384);
        _engine.MaxDecodeEdge = clamped;
        if (!_initializingSettings)
        {
            _ = RefreshCurrentViewAsync(clearCacheFirst: true);
        }
    }

    partial void OnForceSrgbChanged(bool value)
    {
        _engine.ForceSrgb = value;
        if (!_initializingSettings)
        {
            _ = RefreshCurrentViewAsync(clearCacheFirst: true);
        }
    }

    partial void OnKeepScreenOnChanged(bool value)
    {
        UpdateKeepAwake();
        _isContinuousVerticalVolatile = IsContinuousVertical;
    }

    partial void OnIsFullscreenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowHeaderBar));
        OnPropertyChanged(nameof(ShowFooterBar));
    }

    partial void OnHideReaderChromeInFullscreenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowHeaderBar));
        OnPropertyChanged(nameof(ShowFooterBar));
    }

    private void NotifySurfaceVisibilityChanged()
    {
        _isContinuousVerticalVolatile = IsContinuousVertical;
        OnPropertyChanged(nameof(IsContinuousVertical));
        OnPropertyChanged(nameof(ShowSingleSurface));
        OnPropertyChanged(nameof(ShowDualSurface));
        OnPropertyChanged(nameof(ShowPagePlaceholder));
    }

    private void UpdateKeepAwake()
    {
        if (_disposed)
        {
            _wakeLock?.Dispose();
            _wakeLock = null;
            return;
        }

        if (KeepScreenOn)
        {
            _wakeLock ??= WindowsKeepAwake.Acquire();
        }
        else
        {
            _wakeLock?.Dispose();
            _wakeLock = null;
        }
    }

    private void OnEnginePageChanged(int index)
    {
        CurrentPageDisplay = index;
        UpdatePositionText();
        RefreshCurrentBookmarkFlag();
    }

    private void OnFetcherPageProgressChanged(ReaderPageProgress progress)
    {
        _engine.ReportProgress(progress);
    }

    private void OnEnginePageProgressChanged(ReaderPageProgress progress)
    {
        if (_disposed)
        {
            return;
        }

        if (!_isContinuousVerticalVolatile)
        {
            int p1 = _activeTargetPrimaryPage;
            int p2 = _activeTargetSecondaryPage;
            if (!_isPageLoadingVolatile || (progress.PageIndex != p1 && progress.PageIndex != p2))
            {
                return;
            }
        }

        SynchronizationContext? ctx = _syncContext;
        if (ctx is not null && SynchronizationContext.Current != ctx)
        {
            ctx.Post(static state =>
            {
                var (vm, p) = ((ReaderViewModel, ReaderPageProgress))state!;
                vm.ApplyProgressOnUiThread(p);
            }, (this, progress));
        }
        else
        {
            ApplyProgressOnUiThread(progress);
        }
    }

    private void ApplyProgressOnUiThread(ReaderPageProgress progress)
    {
        if (_disposed)
        {
            return;
        }

        (string status, double pct, bool indeterminate) = FormatProgressDisplay(progress);

        if (IsPageLoading && (progress.PageIndex == _activeTargetPrimaryPage || progress.PageIndex == _activeTargetSecondaryPage))
        {
            PageLoadingStatus = status;
            PageLoadingProgress = pct;
            IsPageLoadingIndeterminate = indeterminate;
        }

        if (progress.PageIndex >= 0 && progress.PageIndex < StripPages.Count)
        {
            StripPageItem item = StripPages[progress.PageIndex];
            if (!item.IsLoaded)
            {
                item.StatusText = status;
                item.DownloadProgress = pct;
                item.IsProgressIndeterminate = indeterminate;
            }
        }
    }

    internal static (string Status, double ProgressPercent, bool IsIndeterminate) FormatProgressDisplay(ReaderPageProgress progress)
    {
        return progress.Stage switch
        {
            ReaderPageLoadStage.ResolvingUrl => ("正在解析图片地址...", 0, true),
            ReaderPageLoadStage.Downloading when progress.TotalBytes is > 0 =>
                (
                    $"正在下载图片 · {Math.Clamp(progress.BytesReceived * 100.0 / progress.TotalBytes.Value, 0, 100):0}% ({FormatByteSize(progress.BytesReceived)} / {FormatByteSize(progress.TotalBytes.Value)})",
                    Math.Clamp(progress.BytesReceived * 100.0 / progress.TotalBytes.Value, 0, 100),
                    false
                ),
            ReaderPageLoadStage.Downloading =>
                (
                    progress.BytesReceived > 0
                        ? $"正在下载图片 · {FormatByteSize(progress.BytesReceived)}"
                        : "正在下载图片...",
                    0,
                    true
                ),
            ReaderPageLoadStage.Decoding => ("正在解码渲染...", 100, true),
            ReaderPageLoadStage.Completed => ("加载完成", 100, false),
            ReaderPageLoadStage.Failed => ("该页暂不可用", 0, false),
            _ => ("正在加载页面内容...", 0, true)
        };
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        }
        if (bytes >= 1024)
        {
            return $"{bytes / 1024.0:0} KB";
        }
        return $"{bytes} B";
    }

    private void UpdatePositionText()
    {
        PositionText = PageCount > 0
            ? $"{_engine.CurrentPage + 1} / {PageCount}"
            : $"{_engine.CurrentPage + 1}";
        UpdateOverlayText();
    }

    private void UpdatePositionText(DualPageSpread spread)
    {
        if (spread.IsSplitSpread)
        {
            int minPage = Math.Min(spread.PrimaryPage, spread.SecondaryPage) + 1;
            int maxPage = Math.Max(spread.PrimaryPage, spread.SecondaryPage) + 1;
            PositionText = PageCount > 0 ? $"{minPage}-{maxPage} / {PageCount}" : $"{minPage}-{maxPage}";
        }
        else
        {
            UpdatePositionText();
            return;
        }

        UpdateOverlayText();
    }

    private void UpdateOverlayText()
    {
        string star = IsCurrentPageBookmarked ? " ★" : string.Empty;
        OverlayInfoText = string.IsNullOrEmpty(CurrentTimeText)
            ? $"{PositionText}{star}"
            : $"{PositionText}{star}  |  {CurrentTimeText}";
    }

    public void SaveProgress()
    {
        if (_detail is null || string.IsNullOrEmpty(_progressFilePath))
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_progressFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            SpiderInfo? existing = ReadExistingProgress(_progressFilePath);

            var spiderInfo = existing ?? new SpiderInfo();
            spiderInfo.StartPage = _engine.CurrentPage;
            spiderInfo.Gid = _detail.Gid;
            spiderInfo.Token = _detail.Token;
            spiderInfo.Pages = PageCount;
            spiderInfo.PreviewPages = _detail.PreviewPages;
            if (spiderInfo.PreviewPerPage < 0)
            {
                spiderInfo.PreviewPerPage = 0;
            }

            string tempPath = _progressFilePath + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                spiderInfo.Write(stream);
            }

            File.Move(tempPath, _progressFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存阅读进度失败：{Path}", _progressFilePath);
        }
    }

    internal static SpiderInfo? ReadExistingProgress(string? progressFilePath)
    {
        if (string.IsNullOrEmpty(progressFilePath) || !File.Exists(progressFilePath))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(progressFilePath);
            return SpiderInfo.Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    internal static int ReadSavedStartPage(string? progressFilePath)
    {
        if (string.IsNullOrEmpty(progressFilePath) || !File.Exists(progressFilePath))
        {
            return 0;
        }

        try
        {
            using FileStream stream = File.OpenRead(progressFilePath);
            SpiderInfo? info = SpiderInfo.ReadHeader(stream);
            return info is null || info.StartPage < 0 ? 0 : info.StartPage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal static string BuildProgressPath(string downloadRoot, GalleryDetail detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadRoot);
        ArgumentNullException.ThrowIfNull(detail);

        string title = EhUtils.ExtractTitle(detail.Title) ?? detail.Title ?? "untitled";
        string folder = $"{detail.Gid}-{EhViewer.Core.Util.FileUtils.SanitizeFilename(title)}";
        return Path.Combine(downloadRoot, folder, ".ehviewer");
    }

    private void CancelPendingPageLoads()
    {
        CancellationTokenSource? showCts = Interlocked.Exchange(ref _showPageCts, null);
        showCts?.Cancel();
        showCts?.Dispose();

        CancellationTokenSource? stripCts = Interlocked.Exchange(ref _stripLoadCts, null);
        stripCts?.Cancel();
        stripCts?.Dispose();
    }

    public string ConfiguredOcrLanguage =>
        _resolveDesktopSettings?.Invoke().ReaderOcrLanguage ?? "auto";

    public string ConfiguredTranslationProvider =>
        _resolveDesktopSettings?.Invoke().ReaderTranslationProvider ?? TranslationProviderIds.DirectMachineTranslation;

    public string ConfiguredInlineTranslationStyle =>
        _resolveDesktopSettings?.Invoke().ReaderInlineTranslationStyle ?? "mask";

    public bool ConfiguredAutoTranslateOnPageTurn =>
        _resolveDesktopSettings?.Invoke().ReaderAutoTranslateOnPageTurn ?? false;

    public bool HasComicOnnxPipeline => _ocrService.HasComicOnnxPipeline;

    public async Task<OcrPageResult?> RecognizeRenderedPageAsync(
        bool rightSideInDual,
        string? languageTagOverride,
        CancellationToken cancellationToken)
    {
        Reader.ReaderPageContent? target = ResolveDisplayedPage(rightSideInDual);
        if (target is null)
        {
            return null;
        }

        DecodedPixelBuffer? pixels = SkiaBitmapHelper.ExtractBgraPixels(target.Bitmap);
        if (pixels is null)
        {
            return null;
        }

        string pageKey = $"{_detail?.Gid ?? 0}:{target.Index}:{pixels.Width}x{pixels.Height}";
        string effectiveLang = string.IsNullOrWhiteSpace(languageTagOverride)
            ? ConfiguredOcrLanguage
            : languageTagOverride;

        return await _ocrService
            .RecognizeAsync(pageKey, pixels, effectiveLang, GalleryTitle, cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task<OcrTextBlock?> RecognizeCroppedRegionAsync(
        bool rightSideInDual,
        double normX,
        double normY,
        double normWidth,
        double normHeight,
        string? languageTagOverride,
        CancellationToken cancellationToken)
    {
        Reader.ReaderPageContent? target = ResolveDisplayedPage(rightSideInDual);
        if (target is null)
        {
            return null;
        }

        DecodedPixelBuffer? pixels = SkiaBitmapHelper.ExtractBgraPixels(target.Bitmap);
        if (pixels is null)
        {
            return null;
        }

        string effectiveLang = string.IsNullOrWhiteSpace(languageTagOverride)
            ? ConfiguredOcrLanguage
            : languageTagOverride;

        return await _ocrService
            .RecognizeNormalizedRegionAsync(
                pixels,
                normX,
                normY,
                normWidth,
                normHeight,
                effectiveLang,
                GalleryTitle,
                cancellationToken)
            .ConfigureAwait(true);
    }

    public Task<TranslationResult> TranslateOcrTextAsync(
        string sourceText,
        string? providerOverride = null,
        bool bypassCache = false,
        CancellationToken cancellationToken = default) =>
        _translationPipeline.TranslateAsync(sourceText, providerOverride, bypassCache, cancellationToken);

    public Task<TranslationResult> TranslateOcrTextAsync(
        string sourceText,
        IReadOnlyList<string>? contextLines,
        string? providerOverride = null,
        bool bypassCache = false,
        CancellationToken cancellationToken = default) =>
        _translationPipeline.TranslateAsync(sourceText, contextLines, providerOverride, bypassCache, cancellationToken);

    public async Task<OcrPageResult?> RecognizeStripPageAsync(
        int pageIndex,
        string? languageTagOverride,
        CancellationToken cancellationToken)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            return null;
        }

        DecodedPixelBuffer? pixels = ExtractPagePixelsOnCaller(pageIndex);
        if (pixels is null)
        {
            return null;
        }

        string pageKey = $"{_detail?.Gid ?? 0}:{pageIndex}:{pixels.Width}x{pixels.Height}";
        string effectiveLang = string.IsNullOrWhiteSpace(languageTagOverride)
            ? ConfiguredOcrLanguage
            : languageTagOverride;

        return await _ocrService
            .RecognizeAsync(pageKey, pixels, effectiveLang, GalleryTitle, cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task<OcrTextBlock?> RecognizeCroppedStripRegionAsync(
        int pageIndex,
        double normX,
        double normY,
        double normWidth,
        double normHeight,
        string? languageTagOverride,
        CancellationToken cancellationToken)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            return null;
        }

        DecodedPixelBuffer? pixels = ExtractPagePixelsOnCaller(pageIndex);
        if (pixels is null)
        {
            return null;
        }

        string effectiveLang = string.IsNullOrWhiteSpace(languageTagOverride)
            ? ConfiguredOcrLanguage
            : languageTagOverride;

        return await _ocrService
            .RecognizeNormalizedRegionAsync(
                pixels,
                normX,
                normY,
                normWidth,
                normHeight,
                effectiveLang,
                GalleryTitle,
                cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task PrefetchNextPageOcrAsync(
        int currentPageIndex,
        string? languageTagOverride,
        CancellationToken cancellationToken)
    {
        int nextIndex = currentPageIndex + 1;
        if (nextIndex < 0 || nextIndex >= PageCount || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            if (!_engine.TryGetCachedPage(nextIndex, out Reader.ReaderPageContent? cached) || cached is null)
            {
                return;
            }

            DecodedPixelBuffer? pixels;
            using (cached)
            {
                pixels = SkiaBitmapHelper.ExtractBgraPixels(cached.Bitmap);
            }

            if (pixels is null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            string pageKey = $"{_detail?.Gid ?? 0}:{nextIndex}:{pixels.Width}x{pixels.Height}";
            string effectiveLang = string.IsNullOrWhiteSpace(languageTagOverride)
                ? ConfiguredOcrLanguage
                : languageTagOverride;

            if (_ocrService.IsCached(pageKey, effectiveLang, GalleryTitle))
            {
                return;
            }

            _ = await _ocrService
                .RecognizeAsync(pageKey, pixels, effectiveLang, GalleryTitle, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Background OCR lookahead is best-effort and must never surface errors.
        }
    }

    private DecodedPixelBuffer? ExtractPagePixelsOnCaller(int pageIndex)
    {
        if ((uint)pageIndex < (uint)StripPages.Count && StripPages[pageIndex].Image is { } stripImg)
        {
            DecodedPixelBuffer? fromUi = SkiaBitmapHelper.ExtractBgraPixels(stripImg);
            if (fromUi is not null)
            {
                return fromUi;
            }
        }

        if (_engine.TryGetCachedPage(pageIndex, out Reader.ReaderPageContent? cached) && cached is not null)
        {
            using (cached)
            {
                return SkiaBitmapHelper.ExtractBgraPixels(cached.Bitmap);
            }
        }

        return null;
    }

    public Task<IReadOnlyDictionary<string, TranslationResult>> TranslateOcrPageBlocksAsync(
        IReadOnlyList<OcrTextBlock> blocks,
        string? providerOverride = null,
        bool bypassCache = false,
        CancellationToken cancellationToken = default) =>
        _translationPipeline.TranslatePageBlocksAsync(blocks, providerOverride, bypassCache, cancellationToken);

    public Task<IReadOnlyDictionary<string, TranslationResult>> TranslateOcrPageBlocksAsync(
        IReadOnlyList<OcrTextBlock> blocks,
        IReadOnlyList<string>? previousPageContext,
        Action<string, TranslationResult>? onBubbleTranslated = null,
        string? providerOverride = null,
        bool bypassCache = false,
        CancellationToken cancellationToken = default) =>
        _translationPipeline.TranslatePageBlocksAsync(
            blocks,
            previousPageContext,
            onBubbleTranslated,
            providerOverride,
            bypassCache,
            cancellationToken);

    private Reader.ReaderPageContent? ResolveDisplayedPage(bool rightSideInDual)
    {
        if (!IsSplitDualSpread)
        {
            return rightSideInDual ? null : _currentPage;
        }

        if (rightSideInDual)
        {
            return _leftPageBitmapIsSecondary ? _currentPage : _secondaryPage;
        }

        return _leftPageBitmapIsSecondary ? _secondaryPage : _currentPage;
    }

    public void ReleaseResources()
    {
        Interlocked.Increment(ref _showGeneration);
        Interlocked.Increment(ref _stripGeneration);
        CancelPendingPageLoads();
        ClearPageLoadingState();

        Reader.ReaderPageContent? page = _currentPage;
        Reader.ReaderPageContent? secondary = _secondaryPage;
        _currentPage = null;
        _secondaryPage = null;
        _leftPageBitmapIsSecondary = false;
        page?.Dispose();
        secondary?.Dispose();
        DisposeAnimatedDecoder();

        IsAnimated = false;
        IsSplitDualSpread = false;
        CurrentImage = null;
        LeftPageImage = null;
        RightPageImage = null;
        StripPages.Clear();
        ThumbnailItems.Clear();
        _engine.ReleasePages();
        _fetcher.Unbind();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine.PageChanged -= OnEnginePageChanged;
        _fetcher.PageProgressChanged -= OnFetcherPageProgressChanged;
        _engine.PageProgressChanged -= OnEnginePageProgressChanged;
        _wakeLock?.Dispose();
        _wakeLock = null;
        ReleaseResources();
        if (_ownsEngine)
        {
            _engine.Dispose();
        }
    }
}

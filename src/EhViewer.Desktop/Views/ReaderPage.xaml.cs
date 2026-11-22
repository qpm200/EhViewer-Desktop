using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using EhViewer.Desktop.Reader;
using EhViewer.Desktop.Services.Theming;
using EhViewer.Desktop.Services.Translation;
using EhViewer.Desktop.ViewModels;
using Microsoft.UI.Dispatching;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace EhViewer.Desktop.Views;

/// <summary>
/// Full-featured comic/image reader page with Android GalleryView 3x3 tap zones
/// (LEFT_AREA 0..1/3, RIGHT_AREA 2/3..1, MENU_AREA center-top, SLIDER_AREA center-bottom),
/// floating GalleryHeader &amp; RTL-aware SeekBarPanel HUD, keyboard/wheel/mouse navigation,
/// thumbnails drawer, bookmarks, and animated frame playback.
/// </summary>
public sealed partial class ReaderPage : Page
{
    private const int MaxCachedThumbnails = 120;

    private readonly DispatcherQueueTimer _clockTimer;
    private readonly DispatcherQueueTimer _animTimer;
    private readonly DispatcherQueueTimer _hudHideTimer;
    private readonly DispatcherQueueTimer _sliderScrubTimer;
    private readonly Dictionary<int, LinkedListNode<(int Index, WriteableBitmap Bitmap)>> _thumbBitmapCache = [];
    private readonly LinkedList<(int Index, WriteableBitmap Bitmap)> _thumbBitmapLruOrder = new();
    private CancellationTokenSource _pageLifetimeCts = new();
    private int _pendingSliderTarget = -1;
    private bool _isInitialized;
    private bool _eventsHooked;
    private bool _syncingPageSlider;
    private bool _syncingSettingsUi;
    private bool _hudHeaderVisible = true;
    private bool _hudFooterVisible = true;
    private bool _hudChromeInitialized;
    private bool _lastHeaderBarVisible;
    private bool _lastFooterBarVisible;
    private bool _lastCornerBadgeVisible;
    private bool _pointerOverHud;
    private DateTimeOffset _lastWheelTurnUtc = DateTimeOffset.MinValue;
    private bool _isPanning;
    private bool _panDragExceededThreshold;
    private bool _suppressNextTapFromDrag;
    private Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private ScrollViewer? _panScroller;
    private readonly ConditionalWeakTable<Image, CancellationTokenSource> _thumbLoadCts = new();
    private ScrollMode _basePageHScroll = ScrollMode.Disabled;
    private ScrollMode _basePageVScroll = ScrollMode.Disabled;
    private ScrollMode _baseDualHScroll = ScrollMode.Disabled;
    private ScrollMode _baseDualVScroll = ScrollMode.Disabled;
    private int _lastPresentedPageIndex = -1;
    private bool _ocrModeEnabled;
    private bool _syncingOcrEngineCombo;
    private string? _selectedOcrProviderOverride;
    private CancellationTokenSource? _ocrScanCts;
    private CancellationTokenSource? _ocrTranslateCts;
    private CancellationTokenSource? _ocrBatchCts;
    private SolidColorBrush? _ocrMaskActiveBorderBrush;
    private SolidColorBrush? _ocrBadgeActiveFillBrush;
    private SolidColorBrush? _ocrBadgeIdleFillBrush;
    private SolidColorBrush? _ocrBadgeActiveBorderBrush;
    private SolidColorBrush? _ocrBadgeIdleBorderBrush;
    private SolidColorBrush? _ocrBadgeTextBrush;
    private SolidColorBrush? _ocrBadgeCardBgBrush;
    private OcrPageResult? _singleOcrResult;
    private OcrPageResult? _leftOcrResult;
    private OcrPageResult? _rightOcrResult;
    private readonly List<OcrTextBlock> _singleCustomBlocks = [];
    private readonly List<OcrTextBlock> _leftCustomBlocks = [];
    private readonly List<OcrTextBlock> _rightCustomBlocks = [];
    private readonly Dictionary<int, OcrPageResult> _stripOcrResults = [];
    private readonly Dictionary<int, List<OcrTextBlock>> _stripCustomBlocks = [];
    private List<string> _previousPageDialogue = [];
    private Button? _activeOcrBox;
    private OcrTextBlock? _activeOcrBlock;
    private readonly Dictionary<string, TranslationResult> _bubbleTranslations = new(StringComparer.Ordinal);
    private bool _ocrBatchTranslating;
    private bool _autoTranslateEnabled;
    private bool _autoTranslateUserOverridden;
    private bool _useMaskInlineStyle = true;
    private bool _inlineStyleUserOverridden;
    private CancellationTokenSource? _ocrPrefetchCts;
    private readonly DispatcherQueueTimer _stripOcrDebounceTimer;
    private ScrollViewer? _stripScrollerHooked;
    private Canvas? _ocrDragCanvas;
    private Point _ocrDragStart;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _ocrDragRect;
    private bool _ocrDragExceededThreshold;

    private sealed record OcrButtonTag(
        OcrTextBlock Block,
        bool CustomBox,
        double BaseLeft,
        double BaseTop,
        double BaseWidth,
        double BaseHeight,
        double CanvasWidth);

    public ReaderPage(ReaderViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        _clockTimer = DispatcherQueue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(15);
        _clockTimer.Tick += (_, _) => ViewModel.UpdateClock();

        _animTimer = DispatcherQueue.CreateTimer();
        _animTimer.Tick += OnAnimationTimerTick;

        _hudHideTimer = DispatcherQueue.CreateTimer();
        _hudHideTimer.Interval = TimeSpan.FromSeconds(3.5);
        _hudHideTimer.Tick += (_, _) =>
        {
            _hudHideTimer.Stop();
            if (!_pointerOverHud && (_hudHeaderVisible || _hudFooterVisible))
            {
                SetHudVisibility(header: false, footer: false);
            }
        };

        _sliderScrubTimer = DispatcherQueue.CreateTimer();
        _sliderScrubTimer.Interval = TimeSpan.FromMilliseconds(75);
        _sliderScrubTimer.IsRepeating = false;
        _sliderScrubTimer.Tick += OnSliderScrubTimerTick;

        _stripOcrDebounceTimer = DispatcherQueue.CreateTimer();
        _stripOcrDebounceTimer.Interval = TimeSpan.FromMilliseconds(320);
        _stripOcrDebounceTimer.IsRepeating = false;
        _stripOcrDebounceTimer.Tick += OnStripOcrDebounceTick;

        // Guard XAML initialization events (such as Slider coercing Value from 0 to Minimum="1")
        // so they cannot access timers or trigger page loads before InitializeComponent completes.
        _syncingPageSlider = true;
        _syncingSettingsUi = true;
        try
        {
            InitializeComponent();
        }
        finally
        {
            _syncingPageSlider = false;
            _syncingSettingsUi = false;
        }

        _isInitialized = true;
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPagePointerWheelChanged), handledEventsToo: true);
    }

    public ReaderViewModel ViewModel { get; }

    public event EventHandler? CloseRequested;

    public event EventHandler<bool>? FullscreenChanged;

    public event EventHandler<ReaderBookmarkItem>? OpenBookmarkGalleryRequested;

    public bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    public Visibility GalleryLoadingVisibility(bool isLoading, bool showPagePlaceholder)
        => isLoading && !showPagePlaceholder ? Visibility.Visible : Visibility.Collapsed;

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_pageLifetimeCts.IsCancellationRequested)
        {
            _pageLifetimeCts.Dispose();
            _pageLifetimeCts = new CancellationTokenSource();
        }

        if (!_eventsHooked)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _eventsHooked = true;
        }

        ViewModel.UpdateClock();
        _clockTimer.Start();
        SyncPageSlider();
        SyncActiveThumbnailSelection();
        SyncHudChromeState(animate: false);
        ApplyViewportScale();
        RestartAnimationTimerIfNeeded();
        EnsureStripScrollerHooked();
        _hudHideTimer.Start();
        Focus(FocusState.Programmatic);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveProgress();
        _clockTimer.Stop();
        _animTimer.Stop();
        _hudHideTimer.Stop();
        _hudChromeInitialized = false;
        _sliderScrubTimer.Stop();
        _stripOcrDebounceTimer.Stop();
        _pendingSliderTarget = -1;
        _pageLifetimeCts.Cancel();
        CancelOcrWork();
        ClearThumbnailBitmapCache();
        _stripOcrResults.Clear();
        _stripCustomBlocks.Clear();
        _previousPageDialogue.Clear();
        EndSurfacePan();

        if (_stripScrollerHooked is not null)
        {
            _stripScrollerHooked.ViewChanged -= OnStripScrollerViewChanged;
            _stripScrollerHooked = null;
        }

        if (_eventsHooked)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _eventsHooked = false;
        }

        PageImage.Source = null;
        LeftPageImage.Source = null;
        RightPageImage.Source = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ReaderViewModel.CurrentPageDisplay):
                SyncPageSlider();
                SyncActiveThumbnailSelection();
                if (!ViewModel.IsContinuousVertical && _lastPresentedPageIndex != ViewModel.CurrentPageDisplay)
                {
                    _lastPresentedPageIndex = ViewModel.CurrentPageDisplay;
                    PageScroller.ChangeView(0, 0, 1.0f, disableAnimation: true);
                    DualScroller.ChangeView(0, 0, 1.0f, disableAnimation: true);
                    ApplyEffectiveScrollModes(PageScroller, _basePageHScroll, _basePageVScroll, 1.0f);
                    ApplyEffectiveScrollModes(DualScroller, _baseDualHScroll, _baseDualVScroll, 1.0f);
                    SyncZoomBadge(1.0f);
                }
                break;

            case nameof(ReaderViewModel.PageCount):
                SyncPageSlider();
                SyncActiveThumbnailSelection();
                break;

            case nameof(ReaderViewModel.ScaleIndex):
            case nameof(ReaderViewModel.LayoutIndex):
                SyncPageSlider();
                ApplyViewportScale();
                RestartAnimationTimerIfNeeded();
                RefreshOcrOnSurfaceChange();
                break;

            case nameof(ReaderViewModel.CurrentImage):
                ApplyViewportScale();
                RestartAnimationTimerIfNeeded();
                RefreshOcrOnSurfaceChange();
                break;

            case nameof(ReaderViewModel.IsAnimated):
                RestartAnimationTimerIfNeeded();
                break;

            case nameof(ReaderViewModel.IsFullscreen):
                FullscreenChanged?.Invoke(this, ViewModel.IsFullscreen);
                SyncHudChromeState();
                break;

            case nameof(ReaderViewModel.HideReaderChromeInFullscreen):
            case nameof(ReaderViewModel.ShowPageNumber):
                SyncHudChromeState();
                break;
        }
    }

    internal readonly record struct HudChromeTargets(
        bool HeaderVisible,
        bool FooterVisible,
        bool CornerBadgeVisible);

    internal static HudChromeTargets ComputeHudChromeTargets(
        bool showHeaderBar,
        bool hudHeaderVisible,
        bool showFooterBar,
        bool hudFooterVisible,
        bool showPageNumber)
    {
        bool headerVisible = showHeaderBar && hudHeaderVisible;
        bool footerVisible = showFooterBar && hudFooterVisible;
        bool cornerBadgeVisible = showPageNumber && !footerVisible;
        return new HudChromeTargets(headerVisible, footerVisible, cornerBadgeVisible);
    }

    private void SetHudVisibility(bool header, bool footer)
    {
        _hudHeaderVisible = header;
        _hudFooterVisible = footer;
        SyncHudChromeState(animate: true);
        if (header || footer)
        {
            _hudHideTimer.Stop();
            _hudHideTimer.Start();
        }
        else
        {
            _hudHideTimer.Stop();
        }
    }

    private void SyncHudChromeState(bool animate = true)
    {
        HudChromeTargets targets = ComputeHudChromeTargets(
            ViewModel.ShowHeaderBar,
            _hudHeaderVisible,
            ViewModel.ShowFooterBar,
            _hudFooterVisible,
            ViewModel.ShowPageNumber);

        if (!_hudChromeInitialized || !animate)
        {
            _hudChromeInitialized = true;
            _lastHeaderBarVisible = targets.HeaderVisible;
            _lastFooterBarVisible = targets.FooterVisible;
            _lastCornerBadgeVisible = targets.CornerBadgeVisible;

            HeaderHudBar.Visibility = targets.HeaderVisible ? Visibility.Visible : Visibility.Collapsed;
            HeaderHudBar.IsHitTestVisible = targets.HeaderVisible;

            FooterHudBar.Visibility = targets.FooterVisible ? Visibility.Visible : Visibility.Collapsed;
            FooterHudBar.IsHitTestVisible = targets.FooterVisible;

            CornerInfoBadge.Visibility = targets.CornerBadgeVisible ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (targets.HeaderVisible != _lastHeaderBarVisible)
        {
            _lastHeaderBarVisible = targets.HeaderVisible;
            HeaderHudBar.IsHitTestVisible = targets.HeaderVisible;
            if (targets.HeaderVisible)
            {
                ThemeMotionHelper.AnimateBarSlideShow(HeaderHudBar, fromOffsetY: -24f, durationMs: 200);
            }
            else
            {
                ThemeMotionHelper.AnimateBarSlideHide(HeaderHudBar, toOffsetY: -24f, durationMs: 170);
            }
        }

        if (targets.FooterVisible != _lastFooterBarVisible)
        {
            _lastFooterBarVisible = targets.FooterVisible;
            FooterHudBar.IsHitTestVisible = targets.FooterVisible;
            if (targets.FooterVisible)
            {
                ThemeMotionHelper.AnimateBarSlideShow(FooterHudBar, fromOffsetY: 24f, durationMs: 200);
            }
            else
            {
                ThemeMotionHelper.AnimateBarSlideHide(FooterHudBar, toOffsetY: 24f, durationMs: 170);
            }
        }

        if (targets.CornerBadgeVisible != _lastCornerBadgeVisible)
        {
            _lastCornerBadgeVisible = targets.CornerBadgeVisible;
            if (targets.CornerBadgeVisible)
            {
                ThemeMotionHelper.AnimatePopoverShow(CornerInfoBadge, fromOffsetY: 6f, fromScale: 0.95f, durationMs: 160);
            }
            else
            {
                ThemeMotionHelper.AnimatePopoverHide(CornerInfoBadge, toOffsetY: 6f, toScale: 0.95f, durationMs: 130);
            }
        }
    }

    private void SyncPageSlider()
    {
        _syncingPageSlider = true;
        try
        {
            int max = Math.Max(1, ViewModel.PageCount);
            int cur = Math.Clamp(ViewModel.CurrentPageDisplay + 1, 1, max);
            PageSlider.Maximum = max;
            PageSlider.Value = cur;
            // Match Android SeekBar RTL inversion for Right-To-Left comic reading
            PageSlider.FlowDirection = ViewModel.LayoutIndex == (int)ReaderLayout.RightToLeft
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
        }
        finally
        {
            _syncingPageSlider = false;
        }
    }

    private void SyncActiveThumbnailSelection()
    {
        int count = ViewModel.ThumbnailItems.Count;
        if (count == 0)
        {
            ThumbnailListView.SelectedIndex = -1;
            return;
        }

        int idx = Math.Clamp(ViewModel.CurrentPageDisplay, 0, count - 1);
        if (ThumbnailListView.SelectedIndex != idx)
        {
            ThumbnailListView.SelectedIndex = idx;
        }

        if (ThumbnailDrawer.Visibility == Visibility.Visible)
        {
            ThumbnailListView.ScrollIntoView(ViewModel.ThumbnailItems[idx], ScrollIntoViewAlignment.Default);
        }
    }

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyViewportScale();
        SyncZoomBadge();
    }

    private void OnPageScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (ReferenceEquals(sender, DualScroller))
        {
            ApplyEffectiveScrollModes(DualScroller, _baseDualHScroll, _baseDualVScroll);
        }
        else if (ReferenceEquals(sender, PageScroller))
        {
            ApplyEffectiveScrollModes(PageScroller, _basePageHScroll, _basePageVScroll);
        }

        SyncZoomBadge();
    }

    private void OnResetZoomClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsContinuousVertical)
        {
            return;
        }

        bool dual = ViewModel.ShowDualSurface;
        ScrollViewer active = dual ? DualScroller : PageScroller;
        ApplyEffectiveScrollModes(
            active,
            dual ? _baseDualHScroll : _basePageHScroll,
            dual ? _baseDualVScroll : _basePageVScroll,
            1.0f);
        active.ChangeView(0, 0, 1.0f);
        SyncZoomBadge(1.0f);
    }

    private void SyncZoomBadge(float? overrideZoom = null)
    {
        if (ViewModel.IsContinuousVertical)
        {
            ZoomResetButton.Visibility = Visibility.Collapsed;
            return;
        }

        ScrollViewer active = ViewModel.ShowDualSurface ? DualScroller : PageScroller;
        float zoom = overrideZoom ?? active.ZoomFactor;
        if (Math.Abs(zoom - 1.0f) >= 0.02f)
        {
            ZoomBadgeText.Text = $"{Math.Round(zoom * 100)}%";
            ZoomResetButton.Visibility = Visibility.Visible;
        }
        else
        {
            ZoomResetButton.Visibility = Visibility.Collapsed;
        }
    }

    private static void ApplyEffectiveScrollModes(
        ScrollViewer scroller,
        ScrollMode baseH,
        ScrollMode baseV,
        float? overrideZoom = null)
    {
        bool zoomed = (overrideZoom ?? scroller.ZoomFactor) > 1.01f;
        scroller.HorizontalScrollMode = zoomed ? ScrollMode.Auto : baseH;
        scroller.VerticalScrollMode = zoomed ? ScrollMode.Auto : baseV;
    }

    private void ZoomScrollerAnchored(
        ScrollViewer scroller,
        ScrollMode baseH,
        ScrollMode baseV,
        float factor,
        double anchorX,
        double anchorY)
    {
        float currentZoom = scroller.ZoomFactor;
        float nextZoom = Math.Clamp(currentZoom * factor, scroller.MinZoomFactor, scroller.MaxZoomFactor);
        ApplyEffectiveScrollModes(scroller, baseH, baseV, nextZoom);
        (double newH, double newV) = ReaderZoomMath.ComputeAnchoredZoomOffsets(
            scroller.HorizontalOffset,
            scroller.VerticalOffset,
            currentZoom,
            nextZoom,
            anchorX,
            anchorY,
            scroller.ViewportWidth,
            scroller.ViewportHeight,
            scroller.ExtentWidth,
            scroller.ExtentHeight);
        scroller.ChangeView(newH, newV, nextZoom, disableAnimation: true);
        SyncZoomBadge(nextZoom);
    }

    private void ApplyViewportScale()
    {
        double w = PageSurfaceGrid.ActualWidth;
        double h = PageSurfaceGrid.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        switch (ViewModel.ScaleIndex)
        {
            case (int)ReaderScale.FitWidth:
                PageImage.Stretch = Stretch.Uniform;
                PageImage.Width = Math.Max(1, w - 16);
                PageImage.Height = double.NaN;
                PageImage.MaxWidth = double.PositiveInfinity;
                PageImage.MaxHeight = double.PositiveInfinity;
                _basePageHScroll = ScrollMode.Disabled;
                _basePageVScroll = ScrollMode.Auto;

                LeftPageImage.Stretch = Stretch.Uniform;
                RightPageImage.Stretch = Stretch.Uniform;
                LeftPageImage.MaxWidth = Math.Max(1, (w - 24) / 2.0);
                RightPageImage.MaxWidth = Math.Max(1, (w - 24) / 2.0);
                LeftPageImage.MaxHeight = double.PositiveInfinity;
                RightPageImage.MaxHeight = double.PositiveInfinity;
                _baseDualHScroll = ScrollMode.Disabled;
                _baseDualVScroll = ScrollMode.Auto;
                break;

            case (int)ReaderScale.FitHeight:
                PageImage.Stretch = Stretch.Uniform;
                PageImage.Height = Math.Max(1, h - 16);
                PageImage.Width = double.NaN;
                PageImage.MaxWidth = double.PositiveInfinity;
                PageImage.MaxHeight = double.PositiveInfinity;
                _basePageHScroll = ScrollMode.Auto;
                _basePageVScroll = ScrollMode.Disabled;

                LeftPageImage.Stretch = Stretch.Uniform;
                RightPageImage.Stretch = Stretch.Uniform;
                LeftPageImage.MaxWidth = double.PositiveInfinity;
                RightPageImage.MaxWidth = double.PositiveInfinity;
                LeftPageImage.MaxHeight = Math.Max(1, h - 16);
                RightPageImage.MaxHeight = Math.Max(1, h - 16);
                _baseDualHScroll = ScrollMode.Auto;
                _baseDualVScroll = ScrollMode.Disabled;
                break;

            case (int)ReaderScale.Original:
                PageImage.Stretch = Stretch.None;
                PageImage.Width = double.NaN;
                PageImage.Height = double.NaN;
                PageImage.MaxWidth = double.PositiveInfinity;
                PageImage.MaxHeight = double.PositiveInfinity;
                _basePageHScroll = ScrollMode.Auto;
                _basePageVScroll = ScrollMode.Auto;

                LeftPageImage.Stretch = Stretch.None;
                RightPageImage.Stretch = Stretch.None;
                LeftPageImage.MaxWidth = double.PositiveInfinity;
                RightPageImage.MaxWidth = double.PositiveInfinity;
                LeftPageImage.MaxHeight = double.PositiveInfinity;
                RightPageImage.MaxHeight = double.PositiveInfinity;
                _baseDualHScroll = ScrollMode.Auto;
                _baseDualVScroll = ScrollMode.Auto;
                break;

            default:
                PageImage.Stretch = Stretch.Uniform;
                PageImage.Width = double.NaN;
                PageImage.Height = double.NaN;
                PageImage.MaxWidth = w;
                PageImage.MaxHeight = h;
                _basePageHScroll = ScrollMode.Disabled;
                _basePageVScroll = ScrollMode.Disabled;

                LeftPageImage.Stretch = Stretch.Uniform;
                RightPageImage.Stretch = Stretch.Uniform;
                LeftPageImage.MaxWidth = Math.Max(1, (w - 12) / 2.0);
                RightPageImage.MaxWidth = Math.Max(1, (w - 12) / 2.0);
                LeftPageImage.MaxHeight = h;
                RightPageImage.MaxHeight = h;
                _baseDualHScroll = ScrollMode.Disabled;
                _baseDualVScroll = ScrollMode.Disabled;
                break;
        }

        ApplyEffectiveScrollModes(PageScroller, _basePageHScroll, _basePageVScroll);
        ApplyEffectiveScrollModes(DualScroller, _baseDualHScroll, _baseDualVScroll);
    }

    public void PauseTransientPlayback()
    {
        _animTimer.Stop();
        HideOcrTranslationPopup();
    }

    private void RestartAnimationTimerIfNeeded()
    {
        _animTimer.Stop();
        IReadOnlyList<int> durations = ViewModel.FrameDurations;
        if (!ViewModel.IsAnimated || durations.Count <= 1)
        {
            return;
        }

        int delayMs = Math.Clamp(durations[0], 20, 5000);
        _animTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _animTimer.Start();
    }

    private async void OnAnimationTimerTick(DispatcherQueueTimer sender, object args)
    {
        _animTimer.Stop();
        if (!ViewModel.IsAnimated || ViewModel.FrameDurations.Count <= 1)
        {
            return;
        }

        try
        {
            int nextDelay = await ViewModel.AdvanceAnimationAsync().ConfigureAwait(true);
            if (nextDelay > 0 && ViewModel.IsAnimated)
            {
                _animTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(nextDelay, 20, 5000));
                _animTimer.Start();
            }
        }
        catch
        {
        }
    }

    private async Task JumpToPageAsync(int zeroBasedIndex)
    {
        _sliderScrubTimer.Stop();
        _pendingSliderTarget = -1;
        await ViewModel.ShowPageAsync(zeroBasedIndex).ConfigureAwait(true);
        if (ViewModel.IsContinuousVertical &&
            zeroBasedIndex >= 0 &&
            zeroBasedIndex < ViewModel.StripPages.Count)
        {
            StripListView.ScrollIntoView(ViewModel.StripPages[zeroBasedIndex], ScrollIntoViewAlignment.Leading);
        }
    }

    private async void OnSliderScrubTimerTick(DispatcherQueueTimer sender, object args)
    {
        _sliderScrubTimer.Stop();
        int target = _pendingSliderTarget;
        if (target < 0)
        {
            return;
        }

        _pendingSliderTarget = -1;
        try
        {
            await JumpToPageAsync(target).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = ex.Message;
        }
    }

    private async void OnPageSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isInitialized || _syncingPageSlider)
        {
            return;
        }

        _hudHideTimer.Stop();
        _hudHideTimer.Start();
        int targetIndex = Math.Clamp((int)Math.Round(e.NewValue) - 1, 0, Math.Max(0, ViewModel.PageCount - 1));

        // Fast-path for already decoded cache hits (or continuous strip scroll): jump immediately.
        ReaderPageContent? cached = null;
        if (ViewModel.IsContinuousVertical || ViewModel.Engine.TryGetCachedPage(targetIndex, out cached))
        {
            cached?.Dispose();
            try
            {
                await JumpToPageAsync(targetIndex).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ViewModel.ErrorMessage = ex.Message;
            }
            return;
        }

        // Update page indicator & loading placeholder right away, then debounce remote fetch across rapid drag ticks.
        ViewModel.PreviewTargetPage(targetIndex);
        _pendingSliderTarget = targetIndex;
        _sliderScrubTimer.Stop();
        _sliderScrubTimer.Start();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveProgress();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnPreviousClick(object sender, RoutedEventArgs e)
        => await ViewModel.PreviousPageAsync().ConfigureAwait(true);

    private async void OnNextClick(object sender, RoutedEventArgs e)
        => await ViewModel.NextPageAsync().ConfigureAwait(true);

    private void OnDualPageToggleClick(object sender, RoutedEventArgs e)
        => ViewModel.DualPageMode = !ViewModel.DualPageMode;

    private void OnDualFirstAloneClick(object sender, RoutedEventArgs e)
        => ViewModel.DualPageFirstAlone = !ViewModel.DualPageFirstAlone;

    private void OnBookmarkToggleClick(object sender, RoutedEventArgs e)
        => ViewModel.ToggleBookmark();

    private void OnFullscreenToggleClick(object sender, RoutedEventArgs e)
        => ToggleFullscreen();

    private void ToggleFullscreen()
        => ViewModel.IsFullscreen = !ViewModel.IsFullscreen;

    private void OnToggleThumbDrawerClick(object sender, RoutedEventArgs e)
    {
        bool show = ThumbnailDrawer.Visibility != Visibility.Visible;
        ThumbnailDrawer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ThumbDrawerToggle.IsChecked = show;
        if (show)
        {
            SyncActiveThumbnailSelection();
        }
    }

    private void OnPageJumpFlyoutOpened(object sender, object e)
    {
        _hudHideTimer.Stop();
        int max = Math.Max(1, ViewModel.PageCount);
        PageJumpBox.Maximum = max;
        PageJumpBox.Value = Math.Clamp(ViewModel.CurrentPageDisplay + 1, 1, max);
        PageJumpBox.Focus(FocusState.Programmatic);
    }

    private async void OnConfirmPageJumpClick(object sender, RoutedEventArgs e)
        => await SubmitPageJumpAsync().ConfigureAwait(true);

    private async void OnPageJumpBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await SubmitPageJumpAsync().ConfigureAwait(true);
        }
    }

    private async Task SubmitPageJumpAsync()
    {
        int max = Math.Max(1, ViewModel.PageCount);
        int targetOneBased = double.IsNaN(PageJumpBox.Value)
            ? ViewModel.CurrentPageDisplay + 1
            : (int)Math.Round(PageJumpBox.Value);
        int zeroBased = Math.Clamp(targetOneBased - 1, 0, max - 1);
        PageJumpFlyout.Hide();
        await JumpToPageAsync(zeroBased).ConfigureAwait(true);
    }

    private async void OnBookmarkItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ReaderBookmarkItem item })
        {
            BookmarksFlyout.Hide();
            if (item.Gid > 0 && item.Gid == ViewModel.CurrentGalleryGid)
            {
                await JumpToPageAsync(item.Page).ConfigureAwait(true);
            }
            else
            {
                OpenBookmarkGalleryRequested?.Invoke(this, item);
            }
        }
    }

    private void OnDeleteBookmarkClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: long gid })
        {
            ViewModel.RemoveBookmark(gid);
        }
    }

    private async void OnThumbnailItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ReaderThumbnailItem item)
        {
            await JumpToPageAsync(item.Index).ConfigureAwait(true);
        }
    }

    private async void OnStripContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        EnsureStripScrollerHooked();
        Canvas? stripCanvas = FindDescendant<Canvas>(args.ItemContainer);
        Image? stripImage = FindDescendant<Image>(args.ItemContainer);

        if (args.InRecycleQueue)
        {
            stripCanvas?.Children.Clear();
            return;
        }

        if (args.Item is StripPageItem item)
        {
            if (stripCanvas is not null)
            {
                stripCanvas.Tag = item.Index;
                stripCanvas.Children.Clear();
                if (_ocrModeEnabled &&
                    stripImage is not null &&
                    _stripOcrResults.TryGetValue(item.Index, out OcrPageResult? cachedRes))
                {
                    _stripCustomBlocks.TryGetValue(item.Index, out List<OcrTextBlock>? customs);
                    RenderOcrOverlayBoxes(stripImage, stripCanvas, cachedRes, customs);
                }
            }

            try
            {
                await ViewModel.LoadStripWindowAsync(item.Index).ConfigureAwait(true);
                if (_ocrModeEnabled &&
                    ViewModel.IsContinuousVertical &&
                    item.Image is not null &&
                    !_stripOcrResults.ContainsKey(item.Index))
                {
                    _stripOcrDebounceTimer.Stop();
                    _stripOcrDebounceTimer.Start();
                }
            }
            catch (Exception ex)
            {
                ViewModel.ErrorMessage = ex.Message;
            }
        }
    }

    private void OnStripImageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_ocrModeEnabled ||
            !ViewModel.IsContinuousVertical ||
            sender is not Image img ||
            img.Tag is not int pageIndex)
        {
            return;
        }

        Canvas? canvas = FindSiblingStripCanvas(img);
        if (canvas is not null && _stripOcrResults.TryGetValue(pageIndex, out OcrPageResult? res))
        {
            _stripCustomBlocks.TryGetValue(pageIndex, out List<OcrTextBlock>? customs);
            RenderOcrOverlayBoxes(img, canvas, res, customs);
        }
    }

    private static Canvas? FindSiblingStripCanvas(Image img)
    {
        if (VisualTreeHelper.GetParent(img) is not DependencyObject parent)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(parent, i) is Canvas canvas)
            {
                return canvas;
            }
        }

        return null;
    }

    private void EnsureStripScrollerHooked()
    {
        if (_stripScrollerHooked is not null)
        {
            return;
        }

        ScrollViewer? scroller = FindDescendant<ScrollViewer>(StripListView);
        if (scroller is null)
        {
            return;
        }

        scroller.ViewChanged += OnStripScrollerViewChanged;
        _stripScrollerHooked = scroller;
    }

    private void OnStripScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (ViewModel.IsContinuousVertical &&
            StripListView.ItemsPanelRoot is ItemsStackPanel panel &&
            panel.FirstVisibleIndex >= 0)
        {
            int visiblePage = _stripScrollerHooked is not null &&
                              _stripScrollerHooked.ScrollableHeight > 0 &&
                              _stripScrollerHooked.VerticalOffset >= _stripScrollerHooked.ScrollableHeight - 24 &&
                              panel.LastVisibleIndex >= 0
                ? panel.LastVisibleIndex
                : panel.FirstVisibleIndex;
            ViewModel.UpdateStripVisiblePage(visiblePage);
        }

        if (!_ocrModeEnabled || !ViewModel.IsContinuousVertical)
        {
            return;
        }

        _stripOcrDebounceTimer.Stop();
        _stripOcrDebounceTimer.Start();
    }

    private async void OnStripOcrDebounceTick(DispatcherQueueTimer sender, object args)
    {
        _stripOcrDebounceTimer.Stop();
        if (!_ocrModeEnabled || !ViewModel.IsContinuousVertical)
        {
            return;
        }

        await RunOcrForVisibleStripPagesAsync().ConfigureAwait(true);
    }

    private void CancelThumbLoad(Image img)
    {
        if (_thumbLoadCts.TryGetValue(img, out CancellationTokenSource? prev))
        {
            _thumbLoadCts.Remove(img);
            try
            {
                prev.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            prev.Dispose();
        }
    }

    private async void OnThumbContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        Image? img = FindDescendant<Image>(args.ItemContainer);
        FontIcon? placeholder = FindDescendant<FontIcon>(args.ItemContainer);
        if (img is null)
        {
            return;
        }

        CancelThumbLoad(img);

        if (args.InRecycleQueue)
        {
            img.Tag = null;
            img.Source = null;
            img.Visibility = Visibility.Collapsed;
            if (placeholder is not null)
            {
                placeholder.Visibility = Visibility.Visible;
            }
            return;
        }

        if (args.Item is not ReaderThumbnailItem item || string.IsNullOrWhiteSpace(item.ThumbUrl))
        {
            img.Tag = null;
            img.Source = null;
            img.Visibility = Visibility.Collapsed;
            if (placeholder is not null)
            {
                placeholder.Visibility = Visibility.Visible;
            }
            return;
        }

        img.Tag = item;
        if (TryGetCachedThumbnailBitmap(item.Index, out WriteableBitmap? cached))
        {
            img.Source = cached;
            img.Visibility = Visibility.Visible;
            if (placeholder is not null)
            {
                placeholder.Visibility = Visibility.Collapsed;
            }
            return;
        }

        img.Source = null;
        img.Visibility = Visibility.Collapsed;
        if (placeholder is not null)
        {
            placeholder.Visibility = Visibility.Visible;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetimeCts.Token);
        _thumbLoadCts.AddOrUpdate(img, cts);
        try
        {
            WriteableBitmap? bmp = await ViewModel
                .LoadThumbnailBitmapAsync(item, cts.Token)
                .ConfigureAwait(true);

            if (bmp is not null)
            {
                StoreCachedThumbnailBitmap(item.Index, bmp);
                if (ReferenceEquals(img.Tag, item) && !cts.IsCancellationRequested)
                {
                    img.Source = bmp;
                    img.Visibility = Visibility.Visible;
                    if (placeholder is not null)
                    {
                        placeholder.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Keep placeholder visible on thumbnail decode failure.
        }
        finally
        {
            if (_thumbLoadCts.TryGetValue(img, out CancellationTokenSource? current) && ReferenceEquals(current, cts))
            {
                _thumbLoadCts.Remove(img);
            }

            cts.Dispose();
        }
    }

    private void OnSettingsFlyoutOpened(object sender, object e)
    {
        _hudHideTimer.Stop();
        _syncingSettingsUi = true;
        try
        {
            PreloadSlider.Value = Math.Clamp(ViewModel.PreloadCount, 1, 10);
            PreloadLabelText.Text = $"预加载页数：{ViewModel.PreloadCount}";

            string targetTag = ViewModel.MaxDecodeEdge.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < MaxEdgeComboBox.Items.Count; i++)
            {
                if (MaxEdgeComboBox.Items[i] is ComboBoxItem cbi &&
                    string.Equals(cbi.Tag as string, targetTag, StringComparison.Ordinal))
                {
                    MaxEdgeComboBox.SelectedIndex = i;
                    break;
                }
            }
        }
        finally
        {
            _syncingSettingsUi = false;
        }
    }

    private void OnPreloadSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isInitialized)
        {
            return;
        }

        int rounded = Math.Clamp((int)Math.Round(e.NewValue), 1, 10);
        PreloadLabelText.Text = $"预加载页数：{rounded}";
        if (!_syncingSettingsUi)
        {
            ViewModel.PreloadCount = rounded;
        }
    }

    private void OnMaxEdgeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || _syncingSettingsUi)
        {
            return;
        }

        if (MaxEdgeComboBox.SelectedItem is ComboBoxItem { Tag: string raw } &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int edge))
        {
            ViewModel.MaxDecodeEdge = edge;
        }
    }

    private void OnHudChromeTapped(object sender, TappedRoutedEventArgs e)
    {
        // Prevent clicks inside Header/Footer HUD from bubbling down to PageSurfaceGrid's tap zones
        e.Handled = true;
        _hudHideTimer.Stop();
        _hudHideTimer.Start();
    }

    private void OnHudPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverHud = true;
        _hudHideTimer.Stop();
    }

    private void OnHudPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverHud = false;
        _hudHideTimer.Stop();
        _hudHideTimer.Start();
    }

    private void OnSurfaceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ToggleFullscreen();
        e.Handled = true;
    }

    /// <summary>
    /// Exact Android GalleryView 3x3 tap zones:
    /// LEFT_AREA (0..1/3), RIGHT_AREA (2/3..1), MENU_AREA (1/3..2/3, 0..1/2), SLIDER_AREA (1/3..2/3, 1/2..1).
    /// </summary>
    private async void OnSurfaceTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_suppressNextTapFromDrag)
        {
            _suppressNextTapFromDrag = false;
            return;
        }

        double w = PageSurfaceGrid.ActualWidth;
        double h = PageSurfaceGrid.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        Point pt = e.GetPosition(PageSurfaceGrid);
        double rx = pt.X / w;
        double ry = pt.Y / h;

        // Center zones toggle HUD even in vertical scroll mode
        if (rx >= 1.0 / 3.0 && rx <= 2.0 / 3.0)
        {
            if (ry < 0.5)
            {
                // MENU_AREA: toggle both GalleryHeader + SeekBarPanel
                bool next = !(_hudHeaderVisible || _hudFooterVisible);
                SetHudVisibility(header: next, footer: next);
            }
            else
            {
                // SLIDER_AREA: reveal SeekBarPanel (or hide both if footer was already sole visible bar)
                bool nextFooter = !_hudFooterVisible || _hudHeaderVisible;
                SetHudVisibility(header: false, footer: nextFooter);
                if (nextFooter)
                {
                    PageSlider.Focus(FocusState.Programmatic);
                }
            }
            return;
        }

        if (ViewModel.IsContinuousVertical)
        {
            return;
        }

        bool rtl = ViewModel.LayoutIndex == (int)ReaderLayout.RightToLeft;
        if (rx < 1.0 / 3.0)
        {
            await (rtl ? ViewModel.NextPageAsync() : ViewModel.PreviousPageAsync()).ConfigureAwait(true);
        }
        else
        {
            await (rtl ? ViewModel.PreviousPageAsync() : ViewModel.NextPageAsync()).ConfigureAwait(true);
        }
    }

    private async void OnSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(PageSurfaceGrid);
        PointerPointProperties props = point.Properties;
        if (props.IsXButton1Pressed)
        {
            await ViewModel.PreviousPageAsync().ConfigureAwait(true);
            e.Handled = true;
            return;
        }

        if (props.IsXButton2Pressed)
        {
            await ViewModel.NextPageAsync().ConfigureAwait(true);
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && !ViewModel.IsContinuousVertical)
        {
            ScrollViewer active = ViewModel.ShowDualSurface ? DualScroller : PageScroller;
            if (active.ScrollableWidth > 1 || active.ScrollableHeight > 1)
            {
                _isPanning = true;
                _panDragExceededThreshold = false;
                _suppressNextTapFromDrag = false;
                _panScroller = active;
                _panStartPoint = point.Position;
                _panStartHorizontalOffset = active.HorizontalOffset;
                _panStartVerticalOffset = active.VerticalOffset;
                PageSurfaceGrid.CapturePointer(e.Pointer);
            }
        }
    }

    private void OnSurfacePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        double h = PageSurfaceGrid.ActualHeight;
        if (h <= 0)
        {
            return;
        }

        Point pos = e.GetCurrentPoint(PageSurfaceGrid).Position;
        if (_isPanning && _panScroller is not null)
        {
            double dx = pos.X - _panStartPoint.X;
            double dy = pos.Y - _panStartPoint.Y;
            if (!_panDragExceededThreshold && (Math.Abs(dx) > 6 || Math.Abs(dy) > 6))
            {
                _panDragExceededThreshold = true;
                _suppressNextTapFromDrag = true;
            }

            if (_panDragExceededThreshold)
            {
                double nextH = Math.Clamp(_panStartHorizontalOffset - dx, 0, _panScroller.ScrollableWidth);
                double nextV = Math.Clamp(_panStartVerticalOffset - dy, 0, _panScroller.ScrollableHeight);
                _panScroller.ChangeView(nextH, nextV, null, disableAnimation: true);
            }
            return;
        }

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            const double edgeThreshold = 68.0;
            bool nearTop = pos.Y <= edgeThreshold;
            bool nearBottom = pos.Y >= h - edgeThreshold;

            if (nearTop && !_hudHeaderVisible && ViewModel.ShowHeaderBar)
            {
                SetHudVisibility(header: true, footer: _hudFooterVisible);
            }
            else if (nearBottom && !_hudFooterVisible && ViewModel.ShowFooterBar)
            {
                SetHudVisibility(header: _hudHeaderVisible, footer: true);
            }
        }
    }

    private void OnSurfacePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isPanning)
        {
            bool dragged = _panDragExceededThreshold;
            EndSurfacePan();
            PageSurfaceGrid.ReleasePointerCapture(e.Pointer);
            if (dragged)
            {
                _suppressNextTapFromDrag = true;
                e.Handled = true;
            }
        }
    }

    private void OnSurfacePointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => EndSurfacePan();

    private void EndSurfacePan()
    {
        _isPanning = false;
        _panDragExceededThreshold = false;
        _panScroller = null;
    }

    private static bool IsDescendantOfAny(DependencyObject? source, params DependencyObject?[] ancestors)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            for (int i = 0; i < ancestors.Length; i++)
            {
                if (ReferenceEquals(current, ancestors[i]))
                {
                    return true;
                }
            }

            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch
            {
                break;
            }
        }

        return false;
    }

    private async void OnRetryPageClick(object sender, RoutedEventArgs e)
        => await ViewModel.RetryCurrentPageAsync().ConfigureAwait(true);

    private async void OnPagePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (IsDescendantOfAny(
                e.OriginalSource as DependencyObject,
                ThumbnailDrawer,
                OcrTranslationCard,
                OcrStatusPill,
                HeaderHudBar,
                FooterHudBar))
        {
            return;
        }

        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        bool ctrlHeld = (e.KeyModifiers & VirtualKeyModifiers.Control) != 0;
        if (ctrlHeld && !ViewModel.IsContinuousVertical)
        {
            bool dual = ViewModel.ShowDualSurface;
            ScrollViewer activeScroller = dual ? DualScroller : PageScroller;
            Point anchor = e.GetCurrentPoint(activeScroller).Position;
            float factor = delta > 0 ? 1.15f : 1f / 1.15f;
            ZoomScrollerAnchored(
                activeScroller,
                dual ? _baseDualHScroll : _basePageHScroll,
                dual ? _baseDualVScroll : _basePageVScroll,
                factor,
                anchor.X,
                anchor.Y);
            e.Handled = true;
            return;
        }

        if (!ViewModel.PageTurnByWheel || ViewModel.IsContinuousVertical)
        {
            return;
        }

        ScrollViewer scroller = ViewModel.ShowDualSurface ? DualScroller : PageScroller;
        bool atEdge = delta > 0
            ? scroller.VerticalOffset <= 2.0
            : scroller.VerticalOffset >= scroller.ScrollableHeight - 2.0;

        if (!atEdge)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if ((now - _lastWheelTurnUtc).TotalMilliseconds < 150)
        {
            e.Handled = true;
            return;
        }

        _lastWheelTurnUtc = now;
        e.Handled = true;

        bool rtl = ViewModel.LayoutIndex == (int)ReaderLayout.RightToLeft;
        if (delta < 0)
        {
            await (rtl ? ViewModel.PreviousPageAsync() : ViewModel.NextPageAsync()).ConfigureAwait(true);
        }
        else
        {
            await (rtl ? ViewModel.NextPageAsync() : ViewModel.PreviousPageAsync()).ConfigureAwait(true);
        }
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
        {
            if (e.Key == VirtualKey.Escape)
            {
                HideOcrTranslationPopup();
                Focus(FocusState.Programmatic);
                e.Handled = true;
            }

            return;
        }

        bool rtl = ViewModel.LayoutIndex == (int)ReaderLayout.RightToLeft;
        bool shiftHeld = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);
        bool ctrlHeld = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (ctrlHeld && !ViewModel.IsContinuousVertical)
        {
            bool dual = ViewModel.ShowDualSurface;
            ScrollViewer zoomScroller = dual ? DualScroller : PageScroller;
            ScrollMode baseH = dual ? _baseDualHScroll : _basePageHScroll;
            ScrollMode baseV = dual ? _baseDualVScroll : _basePageVScroll;
            if (e.Key == VirtualKey.Add || e.Key == (VirtualKey)187)
            {
                ZoomScrollerAnchored(
                    zoomScroller,
                    baseH,
                    baseV,
                    1.15f,
                    zoomScroller.ViewportWidth / 2.0,
                    zoomScroller.ViewportHeight / 2.0);
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Subtract || e.Key == (VirtualKey)189)
            {
                ZoomScrollerAnchored(
                    zoomScroller,
                    baseH,
                    baseV,
                    1f / 1.15f,
                    zoomScroller.ViewportWidth / 2.0,
                    zoomScroller.ViewportHeight / 2.0);
                e.Handled = true;
                return;
            }

            if (e.Key is VirtualKey.Number0 or VirtualKey.NumberPad0)
            {
                ApplyEffectiveScrollModes(zoomScroller, baseH, baseV, 1.0f);
                zoomScroller.ChangeView(0, 0, 1.0f);
                SyncZoomBadge(1.0f);
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case VirtualKey.Up:
                if (!ViewModel.IsContinuousVertical)
                {
                    ScrollViewer scroller = ViewModel.ShowDualSurface ? DualScroller : PageScroller;
                    if (scroller.ScrollableHeight > 1 && scroller.VerticalOffset > 2)
                    {
                        scroller.ChangeView(null, Math.Max(0, scroller.VerticalOffset - 180), null);
                    }
                    else
                    {
                        await ViewModel.PreviousPageAsync().ConfigureAwait(true);
                    }

                    e.Handled = true;
                }
                break;

            case VirtualKey.Down:
                if (!ViewModel.IsContinuousVertical)
                {
                    ScrollViewer scroller = ViewModel.ShowDualSurface ? DualScroller : PageScroller;
                    if (scroller.ScrollableHeight > 1 && scroller.VerticalOffset < scroller.ScrollableHeight - 2)
                    {
                        scroller.ChangeView(null, Math.Min(scroller.ScrollableHeight, scroller.VerticalOffset + 180), null);
                    }
                    else
                    {
                        await ViewModel.NextPageAsync().ConfigureAwait(true);
                    }

                    e.Handled = true;
                }
                break;

            case VirtualKey.Left:
                await (rtl ? ViewModel.NextPageAsync() : ViewModel.PreviousPageAsync()).ConfigureAwait(true);
                e.Handled = true;
                break;

            case VirtualKey.Right:
                await (rtl ? ViewModel.PreviousPageAsync() : ViewModel.NextPageAsync()).ConfigureAwait(true);
                e.Handled = true;
                break;

            case VirtualKey.PageUp:
                await ViewModel.PreviousPageAsync().ConfigureAwait(true);
                e.Handled = true;
                break;

            case VirtualKey.PageDown:
                await ViewModel.NextPageAsync().ConfigureAwait(true);
                e.Handled = true;
                break;

            case VirtualKey.Space:
                await (shiftHeld ? ViewModel.PreviousPageAsync() : ViewModel.NextPageAsync()).ConfigureAwait(true);
                e.Handled = true;
                break;

            case VirtualKey.Home:
                await JumpToPageAsync(0).ConfigureAwait(true);
                e.Handled = true;
                break;

            case VirtualKey.End:
                if (ViewModel.PageCount > 0)
                {
                    await JumpToPageAsync(ViewModel.PageCount - 1).ConfigureAwait(true);
                }

                e.Handled = true;
                break;

            case VirtualKey.B:
                ViewModel.ToggleBookmark();
                e.Handled = true;
                break;

            case VirtualKey.G:
                if (ViewModel.ShowFooterBar)
                {
                    SetHudVisibility(header: _hudHeaderVisible, footer: true);
                    PageJumpFlyout.ShowAt(PositionJumpButton);
                    e.Handled = true;
                }
                break;

            case VirtualKey.T:
                OnToggleThumbDrawerClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case VirtualKey.O:
                await ToggleOcrModeAsync(!_ocrModeEnabled).ConfigureAwait(true);
                e.Handled = true;
                break;

            case VirtualKey.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;

            case VirtualKey.F5:
                await ViewModel.RetryCurrentPageAsync().ConfigureAwait(true);
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                if (OcrTranslationCard.Visibility == Visibility.Visible)
                {
                    HideOcrTranslationPopup();
                }
                else if (ViewModel.IsFullscreen)
                {
                    ViewModel.IsFullscreen = false;
                }
                else
                {
                    ViewModel.SaveProgress();
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                }

                e.Handled = true;
                break;

            default:
                if (e.Key >= VirtualKey.Number1 && e.Key <= VirtualKey.Number9 && ViewModel.PageCount > 0)
                {
                    int slot = (int)(e.Key - VirtualKey.Number1);
                    int target = ViewModel.PageCount == 1
                        ? 0
                        : (int)Math.Round(slot * (ViewModel.PageCount - 1) / 8.0);
                    await JumpToPageAsync(target).ConfigureAwait(true);
                    e.Handled = true;
                }

                break;
        }
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                return typed;
            }

            T? found = FindDescendant<T>(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private async void OnOcrModeToggleClick(object sender, RoutedEventArgs e)
    {
        bool enabled = OcrModeToggleButton.IsChecked == true;
        await ToggleOcrModeAsync(enabled).ConfigureAwait(true);
    }

    private async void OnCloseOcrModeClick(object sender, RoutedEventArgs e)
    {
        await ToggleOcrModeAsync(false).ConfigureAwait(true);
    }

    private void OnCloseOcrCardClick(object sender, RoutedEventArgs e)
    {
        HideOcrTranslationPopup();
        Focus(FocusState.Programmatic);
    }

    private async void OnRetranslateOcrClick(object sender, RoutedEventArgs e)
    {
        await TranslateOcrSourceTextAsync(bypassCache: true).ConfigureAwait(true);
    }

    private async void OnOcrEngineOverrideSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingOcrEngineCombo ||
            sender is not ComboBox combo ||
            combo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string providerId)
        {
            return;
        }

        _selectedOcrProviderOverride = providerId;
        SyncOcrEngineComboSelection();

        if (_ocrModeEnabled && (_bubbleTranslations.Count > 0 || _ocrBatchTranslating))
        {
            await TranslateActiveOcrBlocksAsync(
                bypassCache: false,
                updateActiveCard: OcrTranslationCard.Visibility == Visibility.Visible).ConfigureAwait(true);
        }
        else if (OcrTranslationCard.Visibility == Visibility.Visible &&
                 !string.IsNullOrWhiteSpace(OcrSourceTextBox.Text))
        {
            await TranslateOcrSourceTextAsync(bypassCache: true).ConfigureAwait(true);
        }
    }

    private async void OnPrevOcrBubbleClick(object sender, RoutedEventArgs e) =>
        await StepActiveOcrBubbleAsync(-1).ConfigureAwait(true);

    private async void OnNextOcrBubbleClick(object sender, RoutedEventArgs e) =>
        await StepActiveOcrBubbleAsync(1).ConfigureAwait(true);

    private async Task StepActiveOcrBubbleAsync(int delta)
    {
        List<(Button Box, OcrButtonTag Tag)> entries = CollectActiveOcrButtonEntries();
        if (entries.Count <= 1)
        {
            return;
        }

        int currentIdx = -1;
        if (_activeOcrBlock is not null)
        {
            currentIdx = entries.FindIndex(e => string.Equals(e.Tag.Block.Id, _activeOcrBlock.Id, StringComparison.Ordinal));
        }

        int nextIdx = currentIdx < 0
            ? 0
            : (currentIdx + delta + entries.Count) % entries.Count;
        (Button box, OcrButtonTag tag) = entries[nextIdx];
        await SelectAndTranslateOcrBoxAsync(box, tag).ConfigureAwait(true);
    }

    private void OnCopyOcrSourceClick(object sender, RoutedEventArgs e) =>
        CopyTextToClipboard(OcrSourceTextBox.Text);

    private void OnCopyOcrTranslationClick(object sender, RoutedEventArgs e) =>
        CopyTextToClipboard(OcrTranslatedTextBlock.Text);

    private void OnOcrImageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_ocrModeEnabled)
        {
            return;
        }

        if (ReferenceEquals(sender, PageImage))
        {
            RenderOcrOverlayBoxes(PageImage, SingleOcrCanvas, _singleOcrResult, _singleCustomBlocks);
        }
        else if (ReferenceEquals(sender, LeftPageImage))
        {
            RenderOcrOverlayBoxes(LeftPageImage, LeftOcrCanvas, _leftOcrResult, _leftCustomBlocks);
        }
        else if (ReferenceEquals(sender, RightPageImage))
        {
            RenderOcrOverlayBoxes(RightPageImage, RightOcrCanvas, _rightOcrResult, _rightCustomBlocks);
        }
    }

    private async Task ToggleOcrModeAsync(bool enabled)
    {
        _ocrModeEnabled = enabled;
        OcrModeToggleButton.IsChecked = enabled;
        ConfigureOcrCanvasHitBackground(enabled);

        if (!enabled)
        {
            ClearOcrOverlaysAndCard();
            return;
        }

        if (!_inlineStyleUserOverridden)
        {
            _useMaskInlineStyle = !string.Equals(
                ViewModel.ConfiguredInlineTranslationStyle,
                "badge",
                StringComparison.OrdinalIgnoreCase);
        }

        if (!_autoTranslateUserOverridden)
        {
            _autoTranslateEnabled = ViewModel.ConfiguredAutoTranslateOnPageTurn;
        }

        EnsureStripScrollerHooked();
        SyncOcrEngineComboSelection();
        SyncInlineStyleToggleUi();
        AutoTranslateToggleButton.IsChecked = _autoTranslateEnabled;

        await RunOcrForCurrentSurfaceAsync().ConfigureAwait(true);
    }

    private void ConfigureOcrCanvasHitBackground(bool enabled)
    {
        Brush? bg = enabled
            ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0))
            : null;
        SingleOcrCanvas.Background = bg;
        LeftOcrCanvas.Background = bg;
        RightOcrCanvas.Background = bg;
    }

    private void RefreshOcrOnSurfaceChange()
    {
        if (!_ocrModeEnabled)
        {
            return;
        }

        HideOcrTranslationPopup();
        _ = RunOcrForCurrentSurfaceAsync();
    }

    private async Task RunOcrForCurrentSurfaceAsync()
    {
        if (!_ocrModeEnabled)
        {
            return;
        }

        CancelOcrWork();
        ClearOcrCanvases(captureOutgoingDialogue: true);

        if (ViewModel.IsContinuousVertical)
        {
            await RunOcrForVisibleStripPagesAsync().ConfigureAwait(true);
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetimeCts.Token);
        _ocrScanCts = cts;

        ShowOcrStatus("正在本地离线识别页面文字...", scanning: true);

        try
        {
            if (ViewModel.ShowDualSurface)
            {
                Task<OcrPageResult?> leftTask = ViewModel.RecognizeRenderedPageAsync(
                    rightSideInDual: false,
                    languageTagOverride: null,
                    cts.Token);
                Task<OcrPageResult?> rightTask = ViewModel.RecognizeRenderedPageAsync(
                    rightSideInDual: true,
                    languageTagOverride: null,
                    cts.Token);

                await Task.WhenAll(leftTask, rightTask).ConfigureAwait(true);
                if (cts.IsCancellationRequested || !_ocrModeEnabled)
                {
                    return;
                }

                _leftOcrResult = leftTask.Result;
                _rightOcrResult = rightTask.Result;
                RenderOcrOverlayBoxes(LeftPageImage, LeftOcrCanvas, _leftOcrResult, _leftCustomBlocks);
                RenderOcrOverlayBoxes(RightPageImage, RightOcrCanvas, _rightOcrResult, _rightCustomBlocks);

                int totalBlocks = (_leftOcrResult?.Blocks.Count ?? 0) + (_rightOcrResult?.Blocks.Count ?? 0);
                string lang = _leftOcrResult?.LanguageTag ?? _rightOcrResult?.LanguageTag ?? ViewModel.ConfiguredOcrLanguage;
                ShowOcrStatus(
                    totalBlocks > 0
                        ? $"已识别 {totalBlocks} 个气泡（{lang}）· 点击翻译或按住拖拽自由框选（按 O 退出）"
                        : $"本页未检测到气泡（{lang}）· 按住左键拖拽可自由框选（按 O 退出）",
                    scanning: false);

                StartOcrPrefetchForCurrentPage(ViewModel.IsSplitDualSpread ? ViewModel.CurrentPage + 1 : ViewModel.CurrentPage);
                if (_autoTranslateEnabled && CollectActiveOcrBlocks().Count > 0)
                {
                    await TranslateActiveOcrBlocksAsync(bypassCache: false).ConfigureAwait(true);
                }
            }
            else
            {
                OcrPageResult? single = await ViewModel
                    .RecognizeRenderedPageAsync(rightSideInDual: false, languageTagOverride: null, cts.Token)
                    .ConfigureAwait(true);

                if (cts.IsCancellationRequested || !_ocrModeEnabled)
                {
                    return;
                }

                _singleOcrResult = single;
                if (single is null)
                {
                    ShowOcrStatus("页面仍在解码加载，加载完成后将自动识别（也可按住左键拖拽框选）", scanning: true);
                    return;
                }

                RenderOcrOverlayBoxes(PageImage, SingleOcrCanvas, single, _singleCustomBlocks);

                int count = single.Blocks.Count;
                string lang = single.LanguageTag;
                ShowOcrStatus(
                    count > 0
                        ? $"已识别 {count} 个气泡（{lang}）· 点击翻译或按住拖拽自由框选（按 O 退出）"
                        : $"本页未检测到气泡（{lang}）· 按住左键拖拽可自由框选（按 O 退出）",
                    scanning: false);

                StartOcrPrefetchForCurrentPage(ViewModel.CurrentPage);
                if (_autoTranslateEnabled && CollectActiveOcrBlocks().Count > 0)
                {
                    await TranslateActiveOcrBlocksAsync(bypassCache: false).ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowOcrStatus($"识字失败：{ex.Message}", scanning: false);
        }
    }

    private async Task RunOcrForVisibleStripPagesAsync()
    {
        if (!_ocrModeEnabled || !ViewModel.IsContinuousVertical || ViewModel.PageCount <= 0)
        {
            return;
        }

        EnsureStripScrollerHooked();

        CancellationTokenSource? prevScan = Interlocked.Exchange(ref _ocrScanCts, null);
        prevScan?.Cancel();
        prevScan?.Dispose();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetimeCts.Token);
        _ocrScanCts = cts;

        int firstVisible = Math.Clamp(ViewModel.CurrentPage, 0, ViewModel.PageCount - 1);
        int lastVisible = Math.Min(ViewModel.PageCount - 1, firstVisible + 1);
        if (StripListView.ItemsPanelRoot is ItemsStackPanel panel && panel.FirstVisibleIndex >= 0)
        {
            firstVisible = Math.Clamp(panel.FirstVisibleIndex, 0, ViewModel.PageCount - 1);
            int panelLast = panel.LastVisibleIndex >= firstVisible ? panel.LastVisibleIndex : firstVisible;
            lastVisible = Math.Clamp(Math.Min(panelLast, firstVisible + 2), firstVisible, ViewModel.PageCount - 1);
        }

        bool needScan = false;
        for (int i = firstVisible; i <= lastVisible; i++)
        {
            if (!_stripOcrResults.ContainsKey(i) &&
                i < ViewModel.StripPages.Count &&
                ViewModel.StripPages[i].Image is not null)
            {
                needScan = true;
                break;
            }
        }

        if (needScan)
        {
            ShowOcrStatus("正在识别当前长图视口页面文字...", scanning: true);
        }

        try
        {
            int visibleBlockCount = 0;
            string? detectedLang = null;

            for (int pageIdx = firstVisible; pageIdx <= lastVisible; pageIdx++)
            {
                if (cts.IsCancellationRequested || !_ocrModeEnabled)
                {
                    return;
                }

                OcrPageResult? res;
                if (!_stripOcrResults.TryGetValue(pageIdx, out res))
                {
                    if (pageIdx >= ViewModel.StripPages.Count || ViewModel.StripPages[pageIdx].Image is null)
                    {
                        continue;
                    }

                    res = await ViewModel
                        .RecognizeStripPageAsync(pageIdx, languageTagOverride: null, cts.Token)
                        .ConfigureAwait(true);
                    if (cts.IsCancellationRequested || !_ocrModeEnabled)
                    {
                        return;
                    }

                    if (res is not null)
                    {
                        _stripOcrResults[pageIdx] = res;
                    }
                }

                if (res is not null)
                {
                    detectedLang ??= res.LanguageTag;
                    visibleBlockCount += res.Blocks.Count;
                    if (StripListView.ContainerFromIndex(pageIdx) is DependencyObject container)
                    {
                        Image? img = FindDescendant<Image>(container);
                        Canvas? canvas = FindDescendant<Canvas>(container);
                        if (img is not null && canvas is not null)
                        {
                            canvas.Tag = pageIdx;
                            _stripCustomBlocks.TryGetValue(pageIdx, out List<OcrTextBlock>? customs);
                            RenderOcrOverlayBoxes(img, canvas, res, customs);
                        }
                    }
                }
            }

            StartOcrPrefetchForCurrentPage(lastVisible);

            string langLabel = detectedLang ?? ViewModel.ConfiguredOcrLanguage;
            ShowOcrStatus(
                visibleBlockCount > 0
                    ? $"长图模式已识别可见 {visibleBlockCount} 个气泡（{langLabel}）· 点击翻译或开启「自动连译」（按 O 退出）"
                    : "长图当前视口页面加载/识别完成，未检测到气泡（滚动页面可自动继续识别）",
                scanning: false);

            if (_autoTranslateEnabled && CollectActiveOcrBlocks().Any(b => !_bubbleTranslations.ContainsKey(b.Id)))
            {
                await TranslateActiveOcrBlocksAsync(bypassCache: false).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowOcrStatus($"长图识字失败：{ex.Message}", scanning: false);
        }
    }

    private void StartOcrPrefetchForCurrentPage(int basePageIndex)
    {
        CancellationTokenSource? prev = Interlocked.Exchange(ref _ocrPrefetchCts, null);
        prev?.Cancel();
        prev?.Dispose();

        if (!_ocrModeEnabled)
        {
            return;
        }

        var prefetchCts = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetimeCts.Token);
        _ocrPrefetchCts = prefetchCts;
        _ = ViewModel.PrefetchNextPageOcrAsync(basePageIndex, languageTagOverride: null, prefetchCts.Token);
    }

    private void RenderOcrOverlayBoxes(
        Image targetImage,
        Canvas canvas,
        OcrPageResult? result,
        IReadOnlyList<OcrTextBlock>? customBlocks = null)
    {
        ResetOcrDragState();
        canvas.Children.Clear();
        _activeOcrBox = null;
        _activeOcrBlock = null;

        if (!_ocrModeEnabled)
        {
            UpdateOcrActionButtons();
            return;
        }

        double imgW = targetImage.ActualWidth;
        double imgH = targetImage.ActualHeight;
        if (imgW <= 1 || imgH <= 1)
        {
            UpdateOcrActionButtons();
            return;
        }

        canvas.Width = imgW;
        canvas.Height = imgH;

        bool hasResultBlocks = result is not null && result.Blocks.Count > 0;
        bool hasCustomBlocks = customBlocks is not null && customBlocks.Count > 0;
        if (!hasResultBlocks && !hasCustomBlocks)
        {
            UpdateOcrActionButtons();
            return;
        }

        int pixelW = result?.ImageWidth ?? (targetImage.Source as BitmapSource)?.PixelWidth ?? 0;
        int pixelH = result?.ImageHeight ?? (targetImage.Source as BitmapSource)?.PixelHeight ?? 0;
        if (pixelW <= 0 || pixelH <= 0)
        {
            UpdateOcrActionButtons();
            return;
        }

        Rect rendered = ComputeRenderedBitmapRect(
            imgW,
            imgH,
            pixelW,
            pixelH,
            targetImage.Stretch);

        if (rendered.Width <= 1 || rendered.Height <= 1)
        {
            UpdateOcrActionButtons();
            return;
        }

        int boxIndex = 0;
        if (hasResultBlocks)
        {
            foreach (OcrTextBlock block in result!.Blocks)
            {
                AddOcrBoxButton(canvas, rendered, block, ++boxIndex, customBox: false);
            }
        }

        if (hasCustomBlocks)
        {
            foreach (OcrTextBlock custom in customBlocks!)
            {
                AddOcrBoxButton(canvas, rendered, custom, ++boxIndex, customBox: true);
            }
        }

        UpdateOcrActionButtons();
    }

    private Button AddOcrBoxButton(Canvas canvas, Rect rendered, OcrTextBlock block, int boxIndex, bool customBox)
    {
        double boxLeft = rendered.X + block.NormalizedX * rendered.Width;
        double boxTop = rendered.Y + block.NormalizedY * rendered.Height;
        double boxWidth = Math.Max(18, block.NormalizedWidth * rendered.Width);
        double boxHeight = Math.Max(18, block.NormalizedHeight * rendered.Height);

        var tagInfo = new OcrButtonTag(
            block,
            customBox,
            boxLeft,
            boxTop,
            boxWidth,
            boxHeight,
            canvas.Width > 1 ? canvas.Width : rendered.Width);

        var box = new Button
        {
            Width = boxWidth,
            Height = boxHeight,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            BorderThickness = new Thickness(customBox ? 2.0 : 1.5),
            CornerRadius = new CornerRadius(5),
            Tag = tagInfo
        };

        string label = customBox ? $"自由框选: {block.Text}" : $"OCR气泡 {boxIndex}: {block.Text}";
        AutomationProperties.SetName(box, label);
        box.Tapped += (_, te) => te.Handled = true;
        box.Click += OnOcrBoxClick;
        ApplyBubbleOverlayVisual(box, tagInfo, isActive: false);

        Canvas.SetLeft(box, boxLeft);
        Canvas.SetTop(box, boxTop);
        canvas.Children.Add(box);
        return box;
    }

    private async void OnOcrBoxClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button box || !TryGetButtonTag(box.Tag, out OcrButtonTag? tag))
        {
            return;
        }

        await SelectAndTranslateOcrBoxAsync(box, tag).ConfigureAwait(true);
    }

    private async Task SelectAndTranslateOcrBoxAsync(Button box, OcrButtonTag tag)
    {
        if (_activeOcrBox is not null && TryGetButtonTag(_activeOcrBox.Tag, out OcrButtonTag? prevTag))
        {
            ApplyBubbleOverlayVisual(_activeOcrBox, prevTag, isActive: false);
        }

        _activeOcrBox = box;
        _activeOcrBlock = tag.Block;
        ApplyBubbleOverlayVisual(box, tag, isActive: true);

        SyncOcrEngineComboSelection();
        UpdateOcrBubbleNavigationUi();
        OcrSourceTextBox.Text = tag.Block.Text;
        OcrTranslationCard.Visibility = Visibility.Visible;

        await TranslateOcrSourceTextAsync(bypassCache: false).ConfigureAwait(true);
    }

    private static bool TryGetButtonTag(object? rawTag, out OcrButtonTag tag)
    {
        if (rawTag is OcrButtonTag typed)
        {
            tag = typed;
            return true;
        }

        tag = null!;
        return false;
    }

    private void OnOcrCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_ocrModeEnabled || sender is not Canvas canvas)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject sourceObj && IsInsideOcrBoxButton(sourceObj, canvas))
        {
            return;
        }

        var pointerPoint = e.GetCurrentPoint(canvas);
        if (!pointerPoint.Properties.IsLeftButtonPressed || !CanStartOcrRubberBand(canvas))
        {
            return;
        }

        ResetOcrDragState();
        _ocrDragCanvas = canvas;
        _ocrDragStart = pointerPoint.Position;
        _ocrDragExceededThreshold = false;
        canvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnOcrCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_ocrDragCanvas is null || !ReferenceEquals(sender, _ocrDragCanvas))
        {
            return;
        }

        Point current = e.GetCurrentPoint(_ocrDragCanvas).Position;
        double dx = Math.Abs(current.X - _ocrDragStart.X);
        double dy = Math.Abs(current.Y - _ocrDragStart.Y);
        if (!_ocrDragExceededThreshold && Math.Max(dx, dy) < 12.0)
        {
            return;
        }

        _ocrDragExceededThreshold = true;
        if (_ocrDragRect is null)
        {
            _ocrDragRect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(245, 77, 182, 172)),
                StrokeThickness = 2,
                StrokeDashArray = [4, 2],
                Fill = new SolidColorBrush(Color.FromArgb(44, 0, 191, 165)),
                IsHitTestVisible = false
            };
            _ocrDragCanvas.Children.Add(_ocrDragRect);
        }

        double left = Math.Clamp(Math.Min(_ocrDragStart.X, current.X), 0, Math.Max(0, _ocrDragCanvas.ActualWidth));
        double top = Math.Clamp(Math.Min(_ocrDragStart.Y, current.Y), 0, Math.Max(0, _ocrDragCanvas.ActualHeight));
        double right = Math.Clamp(Math.Max(_ocrDragStart.X, current.X), 0, Math.Max(0, _ocrDragCanvas.ActualWidth));
        double bottom = Math.Clamp(Math.Max(_ocrDragStart.Y, current.Y), 0, Math.Max(0, _ocrDragCanvas.ActualHeight));

        Canvas.SetLeft(_ocrDragRect, left);
        Canvas.SetTop(_ocrDragRect, top);
        _ocrDragRect.Width = Math.Max(2, right - left);
        _ocrDragRect.Height = Math.Max(2, bottom - top);
        e.Handled = true;
    }

    private async void OnOcrCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_ocrDragCanvas is null || !ReferenceEquals(sender, _ocrDragCanvas))
        {
            return;
        }

        Canvas canvas = _ocrDragCanvas;
        Point endPoint = e.GetCurrentPoint(canvas).Position;
        bool dragged = _ocrDragExceededThreshold;
        double startX = _ocrDragStart.X;
        double startY = _ocrDragStart.Y;

        try
        {
            canvas.ReleasePointerCapture(e.Pointer);
        }
        catch
        {
        }

        ResetOcrDragState();

        if (!dragged)
        {
            return;
        }

        _suppressNextTapFromDrag = true;
        e.Handled = true;

        double left = Math.Min(startX, endPoint.X);
        double top = Math.Min(startY, endPoint.Y);
        double width = Math.Abs(endPoint.X - startX);
        double height = Math.Abs(endPoint.Y - startY);
        if (width < 12 || height < 12)
        {
            return;
        }

        await RecognizeDraggedOcrBoxAsync(canvas, new Rect(left, top, width, height)).ConfigureAwait(true);
    }

    private void OnOcrCanvasPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => ResetOcrDragState();

    private void ResetOcrDragState()
    {
        if (_ocrDragCanvas is not null && _ocrDragRect is not null)
        {
            _ocrDragCanvas.Children.Remove(_ocrDragRect);
        }

        _ocrDragRect = null;
        _ocrDragCanvas = null;
        _ocrDragExceededThreshold = false;
    }

    private bool CanStartOcrRubberBand(Canvas canvas)
    {
        bool shiftHeld = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);
        if (shiftHeld)
        {
            return true;
        }

        ScrollViewer? scroller = ReferenceEquals(canvas, SingleOcrCanvas)
            ? PageScroller
            : DualScroller;
        return scroller.ScrollableWidth <= 1 && scroller.ScrollableHeight <= 1;
    }

    private static bool IsInsideOcrBoxButton(DependencyObject source, DependencyObject stopAt)
    {
        DependencyObject? current = source;
        while (current is not null && !ReferenceEquals(current, stopAt))
        {
            if (current is Button)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async Task RecognizeDraggedOcrBoxAsync(Canvas canvas, Rect selectionRect)
    {
        bool rightSideInDual = ReferenceEquals(canvas, RightOcrCanvas);
        Image targetImage = ReferenceEquals(canvas, SingleOcrCanvas)
            ? PageImage
            : rightSideInDual
                ? RightPageImage
                : LeftPageImage;

        double imgW = targetImage.ActualWidth;
        double imgH = targetImage.ActualHeight;
        if (imgW <= 1 || imgH <= 1)
        {
            return;
        }

        int pixelWidth = 0;
        int pixelHeight = 0;
        if (targetImage.Source is BitmapSource bmp)
        {
            pixelWidth = bmp.PixelWidth;
            pixelHeight = bmp.PixelHeight;
        }
        else if (ReferenceEquals(canvas, SingleOcrCanvas) && _singleOcrResult is not null)
        {
            pixelWidth = _singleOcrResult.ImageWidth;
            pixelHeight = _singleOcrResult.ImageHeight;
        }
        else if (ReferenceEquals(canvas, LeftOcrCanvas) && _leftOcrResult is not null)
        {
            pixelWidth = _leftOcrResult.ImageWidth;
            pixelHeight = _leftOcrResult.ImageHeight;
        }
        else if (ReferenceEquals(canvas, RightOcrCanvas) && _rightOcrResult is not null)
        {
            pixelWidth = _rightOcrResult.ImageWidth;
            pixelHeight = _rightOcrResult.ImageHeight;
        }

        Rect rendered = pixelWidth > 0 && pixelHeight > 0
            ? ComputeRenderedBitmapRect(imgW, imgH, pixelWidth, pixelHeight, targetImage.Stretch)
            : new Rect(0, 0, imgW, imgH);

        if (rendered.Width <= 1 || rendered.Height <= 1)
        {
            return;
        }

        double normX = Math.Clamp((selectionRect.X - rendered.X) / rendered.Width, 0, 1);
        double normY = Math.Clamp((selectionRect.Y - rendered.Y) / rendered.Height, 0, 1);
        double normRight = Math.Clamp((selectionRect.Right - rendered.X) / rendered.Width, 0, 1);
        double normBottom = Math.Clamp((selectionRect.Bottom - rendered.Y) / rendered.Height, 0, 1);
        double normW = normRight - normX;
        double normH = normBottom - normY;
        if (normW < 0.008 || normH < 0.008)
        {
            return;
        }

        ShowOcrStatus("正在识别框选区域...", scanning: true);
        try
        {
            OcrTextBlock? block = await ViewModel
                .RecognizeCroppedRegionAsync(
                    rightSideInDual,
                    normX,
                    normY,
                    normW,
                    normH,
                    languageTagOverride: null,
                    _pageLifetimeCts.Token)
                .ConfigureAwait(true);

            if (!_ocrModeEnabled)
            {
                return;
            }

            if (block is null || string.IsNullOrWhiteSpace(block.Text))
            {
                ShowOcrStatus("框选区域未识别出文字 · 可重新拖拽框选或调整范围", scanning: false);
                return;
            }

            if (ReferenceEquals(canvas, SingleOcrCanvas))
            {
                _singleCustomBlocks.Add(block);
            }
            else if (ReferenceEquals(canvas, LeftOcrCanvas))
            {
                _leftCustomBlocks.Add(block);
            }
            else if (ReferenceEquals(canvas, RightOcrCanvas))
            {
                _rightCustomBlocks.Add(block);
            }

            Button customBtn = AddOcrBoxButton(canvas, rendered, block, canvas.Children.Count + 1, customBox: true);
            UpdateOcrActionButtons();
            ShowOcrStatus("已识别框选气泡 · 点击任意框可继续翻译（按 O 退出）", scanning: false);
            if (TryGetButtonTag(customBtn.Tag, out OcrButtonTag? tag))
            {
                await SelectAndTranslateOcrBoxAsync(customBtn, tag).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowOcrStatus($"框选识字失败：{ex.Message}", scanning: false);
        }
    }

    private async Task TranslateOcrSourceTextAsync(bool bypassCache)
    {
        string source = OcrSourceTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
        {
            OcrTranslatedTextBlock.Text = "请输入或选择要翻译的原文。";
            OcrProviderNoteText.Visibility = Visibility.Collapsed;
            return;
        }

        string? providerOverride = _selectedOcrProviderOverride;
        string effectiveProvider = string.IsNullOrWhiteSpace(providerOverride)
            ? ViewModel.ConfiguredTranslationProvider
            : providerOverride;

        bool matchesActiveBubble = _activeOcrBlock is not null &&
            string.Equals(source, _activeOcrBlock.Text.Trim(), StringComparison.Ordinal);

        if (!bypassCache && matchesActiveBubble)
        {
            if (_bubbleTranslations.TryGetValue(_activeOcrBlock!.Id, out TranslationResult? existing))
            {
                CancellationTokenSource? staleSingle = Interlocked.Exchange(ref _ocrTranslateCts, null);
                staleSingle?.Cancel();
                staleSingle?.Dispose();

                OcrTranslateProgress.Visibility = Visibility.Collapsed;
                DisplayOcrCardTranslationResult(existing);
                UpdateOcrActionButtons();
                UpdateOcrBubbleNavigationUi();
                return;
            }

            if (_ocrBatchTranslating)
            {
                CancellationTokenSource? staleSingle = Interlocked.Exchange(ref _ocrTranslateCts, null);
                staleSingle?.Cancel();
                staleSingle?.Dispose();

                OcrTranslateProgress.Visibility = Visibility.Visible;
                OcrTranslatedTextBlock.Text = "全页连续翻译进行中，等待当前气泡到达...";
                OcrProviderNoteText.Visibility = Visibility.Collapsed;
                return;
            }
        }

        if (matchesActiveBubble &&
            string.Equals(effectiveProvider, TranslationProviderIds.DeepSeek, StringComparison.OrdinalIgnoreCase))
        {
            List<OcrTextBlock> pageBlocks = CollectActiveOcrBlocks();
            if (pageBlocks.Count > 1 && (bypassCache || pageBlocks.Any(b => !_bubbleTranslations.ContainsKey(b.Id))))
            {
                OcrTranslateProgress.Visibility = Visibility.Visible;
                OcrTranslatedTextBlock.Text = "正在结合全页上下文连续翻译...";
                OcrProviderNoteText.Visibility = Visibility.Collapsed;
                await TranslateActiveOcrBlocksAsync(bypassCache, updateActiveCard: true).ConfigureAwait(true);
                return;
            }
        }

        CancellationTokenSource? prev = Interlocked.Exchange(ref _ocrTranslateCts, null);
        prev?.Cancel();
        prev?.Dispose();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetimeCts.Token);
        _ocrTranslateCts = cts;

        OcrTranslateProgress.Visibility = Visibility.Visible;
        OcrTranslatedTextBlock.Text = string.Equals(effectiveProvider, TranslationProviderIds.DeepSeek, StringComparison.OrdinalIgnoreCase)
            ? "正在结合全页上下文连续翻译..."
            : "正在翻译...";
        OcrProviderNoteText.Visibility = Visibility.Collapsed;

        try
        {
            IReadOnlyList<string>? contextLines = CollectContextLines();
            TranslationResult result = await ViewModel
                .TranslateOcrTextAsync(source, contextLines, providerOverride, bypassCache, cts.Token)
                .ConfigureAwait(true);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (_activeOcrBlock is not null)
            {
                _bubbleTranslations[_activeOcrBlock.Id] = result;
            }

            RefreshAllBubbleOverlayVisuals();
            UpdateOcrActionButtons();
            DisplayOcrCardTranslationResult(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OcrTranslatedTextBlock.Text = $"翻译失败：{ex.Message}";
            OcrProviderNoteText.Text = "提示：可在上方下拉切换“传统直译”或在设置中检查 DeepSeek / 本地 Sakura-7B / 自定义接口配置。";
            OcrProviderNoteText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_ocrTranslateCts, cts))
            {
                OcrTranslateProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void DisplayOcrCardTranslationResult(TranslationResult result)
    {
        OcrTranslatedTextBlock.Text = result.TranslatedText;
        OcrProviderNoteText.Text = result.UsedFallback && !string.IsNullOrWhiteSpace(result.FallbackNote)
            ? $"引擎：{result.ProviderDisplayName}（{result.FallbackNote}）"
            : $"引擎：{result.ProviderDisplayName}";
        OcrProviderNoteText.Visibility = Visibility.Visible;
    }

    private async void OnTranslateAllOcrClick(object sender, RoutedEventArgs e)
    {
        List<OcrTextBlock> blocks = CollectActiveOcrBlocks();
        bool allTranslated = blocks.Count > 0 && blocks.All(b => _bubbleTranslations.ContainsKey(b.Id));
        await TranslateActiveOcrBlocksAsync(
            bypassCache: allTranslated,
            updateActiveCard: OcrTranslationCard.Visibility == Visibility.Visible).ConfigureAwait(true);
    }

    private async Task TranslateActiveOcrBlocksAsync(bool bypassCache, bool updateActiveCard = false)
    {
        if (!_ocrModeEnabled)
        {
            return;
        }

        List<OcrTextBlock> blocks = CollectActiveOcrBlocks();
        if (blocks.Count == 0)
        {
            ShowOcrStatus("当前页没有可翻译的气泡文字", scanning: false);
            return;
        }

        CancellationTokenSource? prevBatch = Interlocked.Exchange(ref _ocrBatchCts, null);
        prevBatch?.Cancel();
        prevBatch?.Dispose();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetimeCts.Token);
        _ocrBatchCts = cts;
        _ocrBatchTranslating = true;
        UpdateOcrActionButtons();

        string effectiveProvider = string.IsNullOrWhiteSpace(_selectedOcrProviderOverride)
            ? ViewModel.ConfiguredTranslationProvider
            : _selectedOcrProviderOverride;
        bool isDeepSeek = string.Equals(effectiveProvider, TranslationProviderIds.DeepSeek, StringComparison.OrdinalIgnoreCase);
        var completedIds = new HashSet<string>(StringComparer.Ordinal);
        ShowOcrStatus(
            isDeepSeek
                ? $"DeepSeek 正在连续上下文翻译全页 (0/{blocks.Count})..."
                : $"正在批量翻译全页 (0/{blocks.Count})...",
            scanning: true);

        void OnBubbleReady(string blockId, TranslationResult tr)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (cts.IsCancellationRequested || !_ocrModeEnabled || !ReferenceEquals(_ocrBatchCts, cts))
                {
                    return;
                }

                _bubbleTranslations[blockId] = tr;
                completedIds.Add(blockId);
                InlineTranslationToggleButton.IsChecked = true;
                RefreshBubbleOverlayById(blockId);
                UpdateOcrActionButtons();
                ShowOcrStatus(
                    isDeepSeek
                        ? $"DeepSeek 正在连续上下文翻译全页 ({completedIds.Count}/{blocks.Count})..."
                        : $"正在批量翻译全页 ({completedIds.Count}/{blocks.Count})...",
                    scanning: true);

                if ((updateActiveCard || OcrTranslationCard.Visibility == Visibility.Visible) &&
                    _activeOcrBlock is not null &&
                    string.Equals(_activeOcrBlock.Id, blockId, StringComparison.Ordinal))
                {
                    OcrTranslateProgress.Visibility = Visibility.Collapsed;
                    DisplayOcrCardTranslationResult(tr);
                }
            });
        }

        try
        {
            IReadOnlyDictionary<string, TranslationResult> translated = await ViewModel
                .TranslateOcrPageBlocksAsync(
                    blocks,
                    _previousPageDialogue.Count > 0 ? _previousPageDialogue : null,
                    OnBubbleReady,
                    _selectedOcrProviderOverride,
                    bypassCache,
                    cts.Token)
                .ConfigureAwait(true);

            if (cts.IsCancellationRequested || !_ocrModeEnabled || !ReferenceEquals(_ocrBatchCts, cts))
            {
                return;
            }

            foreach ((string key, TranslationResult tr) in translated)
            {
                _bubbleTranslations[key] = tr;
            }

            InlineTranslationToggleButton.IsChecked = true;
            RefreshAllBubbleOverlayVisuals();

            if ((updateActiveCard || OcrTranslationCard.Visibility == Visibility.Visible) &&
                _activeOcrBlock is not null &&
                (translated.TryGetValue(_activeOcrBlock.Id, out TranslationResult? activeTr) ||
                 _bubbleTranslations.TryGetValue(_activeOcrBlock.Id, out activeTr)))
            {
                OcrTranslateProgress.Visibility = Visibility.Collapsed;
                DisplayOcrCardTranslationResult(activeTr);
            }

            string? providerSummary = translated.Values
                .Select(static v => v.ProviderDisplayName)
                .FirstOrDefault(static n => !string.IsNullOrWhiteSpace(n));
            string suffix = string.IsNullOrWhiteSpace(providerSummary) ? string.Empty : $"（{providerSummary}）";
            ShowOcrStatus($"已完成全页 {translated.Count}/{blocks.Count} 个气泡翻译{suffix}", scanning: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_ocrBatchCts, cts))
            {
                ShowOcrStatus($"全页翻译失败：{ex.Message}", scanning: false);
                if (updateActiveCard && OcrTranslationCard.Visibility == Visibility.Visible)
                {
                    OcrTranslateProgress.Visibility = Visibility.Collapsed;
                    OcrTranslatedTextBlock.Text = $"翻译失败：{ex.Message}";
                    OcrProviderNoteText.Text = "提示：可在上方下拉切换“传统直译”或在设置中检查 DeepSeek / 本地 Sakura-7B / 自定义接口配置。";
                    OcrProviderNoteText.Visibility = Visibility.Visible;
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_ocrBatchCts, cts))
            {
                _ocrBatchCts = null;
                _ocrBatchTranslating = false;
                if (updateActiveCard)
                {
                    OcrTranslateProgress.Visibility = Visibility.Collapsed;
                }

                UpdateOcrActionButtons();
            }
        }
    }

    private void OnInlineTranslationToggleClick(object sender, RoutedEventArgs e)
    {
        RefreshAllBubbleOverlayVisuals();
        UpdateOcrActionButtons();
    }

    private void OnInlineStyleToggleClick(object sender, RoutedEventArgs e)
    {
        _inlineStyleUserOverridden = true;
        _useMaskInlineStyle = InlineStyleToggleButton.IsChecked == true;
        SyncInlineStyleToggleUi();
        if (InlineTranslationToggleButton.IsChecked != true)
        {
            InlineTranslationToggleButton.IsChecked = true;
        }

        RefreshAllBubbleOverlayVisuals();
    }

    private async void OnAutoTranslateToggleClick(object sender, RoutedEventArgs e)
    {
        _autoTranslateUserOverridden = true;
        _autoTranslateEnabled = AutoTranslateToggleButton.IsChecked == true;
        if (_autoTranslateEnabled &&
            _ocrModeEnabled &&
            !_ocrBatchTranslating &&
            CollectActiveOcrBlocks().Any(b => !_bubbleTranslations.ContainsKey(b.Id)))
        {
            await TranslateActiveOcrBlocksAsync(bypassCache: false).ConfigureAwait(true);
        }
    }

    private void SyncInlineStyleToggleUi()
    {
        InlineStyleToggleButton.IsChecked = _useMaskInlineStyle;
        InlineStyleToggleButton.Content = _useMaskInlineStyle ? "填底嵌字" : "紧凑角标";
    }

    private bool IsRightToLeftLayout => ViewModel.LayoutIndex == (int)ReaderLayout.RightToLeft;

    private IEnumerable<Canvas> EnumerateActiveOcrCanvases()
    {
        if (ViewModel.IsContinuousVertical)
        {
            if (StripListView.ItemsPanelRoot is Panel itemsPanel)
            {
                foreach (UIElement itemElem in itemsPanel.Children)
                {
                    Canvas? stripCanvas = FindDescendant<Canvas>(itemElem);
                    if (stripCanvas is not null)
                    {
                        yield return stripCanvas;
                    }
                }
            }

            yield break;
        }

        if (ViewModel.ShowDualSurface)
        {
            if (IsRightToLeftLayout)
            {
                yield return RightOcrCanvas;
                yield return LeftOcrCanvas;
            }
            else
            {
                yield return LeftOcrCanvas;
                yield return RightOcrCanvas;
            }
        }
        else
        {
            yield return SingleOcrCanvas;
        }
    }

    private List<(Button Box, OcrButtonTag Tag)> CollectActiveOcrButtonEntries()
    {
        var seen = new HashSet<OcrTextBlock>();
        var entries = new List<(Button Box, OcrButtonTag Tag)>();

        foreach (Canvas canvas in EnumerateActiveOcrCanvases())
        {
            foreach (UIElement child in canvas.Children)
            {
                if (child is Button btn &&
                    TryGetButtonTag(btn.Tag, out OcrButtonTag? tag) &&
                    seen.Add(tag.Block))
                {
                    entries.Add((btn, tag));
                }
            }
        }

        return entries;
    }

    private List<OcrTextBlock> CollectActiveOcrBlocks()
    {
        List<(Button Box, OcrButtonTag Tag)> buttonEntries = CollectActiveOcrButtonEntries();
        if (buttonEntries.Count > 0)
        {
            return buttonEntries.Select(static e => e.Tag.Block).ToList();
        }

        var blocks = new List<OcrTextBlock>();
        if (ViewModel.IsContinuousVertical)
        {
            foreach (OcrPageResult res in _stripOcrResults.Values)
            {
                blocks.AddRange(res.Blocks);
            }

            return blocks;
        }

        if (ViewModel.ShowDualSurface)
        {
            void AddSide(OcrPageResult? ocr, List<OcrTextBlock> custom)
            {
                if (ocr is not null)
                {
                    blocks.AddRange(ocr.Blocks);
                }

                blocks.AddRange(custom);
            }

            if (IsRightToLeftLayout)
            {
                AddSide(_rightOcrResult, _rightCustomBlocks);
                AddSide(_leftOcrResult, _leftCustomBlocks);
            }
            else
            {
                AddSide(_leftOcrResult, _leftCustomBlocks);
                AddSide(_rightOcrResult, _rightCustomBlocks);
            }
        }
        else
        {
            if (_singleOcrResult is not null)
            {
                blocks.AddRange(_singleOcrResult.Blocks);
            }

            blocks.AddRange(_singleCustomBlocks);
        }

        return blocks;
    }

    private void UpdateOcrBubbleNavigationUi()
    {
        List<(Button Box, OcrButtonTag Tag)> entries = CollectActiveOcrButtonEntries();
        bool canStep = entries.Count > 1;
        PrevOcrBubbleButton.IsEnabled = canStep;
        NextOcrBubbleButton.IsEnabled = canStep;

        if (entries.Count == 0 || _activeOcrBlock is null)
        {
            OcrBubblePositionText.Text = entries.Count > 0 ? $"0/{entries.Count}" : "-";
            return;
        }

        int idx = entries.FindIndex(e => string.Equals(e.Tag.Block.Id, _activeOcrBlock.Id, StringComparison.Ordinal));
        OcrBubblePositionText.Text = idx >= 0 ? $"{idx + 1}/{entries.Count}" : $"-/{entries.Count}";
    }

    private IReadOnlyList<string>? CollectContextLines()
    {
        List<OcrTextBlock> blocks = CollectActiveOcrBlocks();
        if (blocks.Count <= 1)
        {
            return _previousPageDialogue.Count > 0 ? [.. _previousPageDialogue] : null;
        }

        var lines = new List<string>(blocks.Count + _previousPageDialogue.Count);
        foreach (string prev in _previousPageDialogue)
        {
            if (!string.IsNullOrWhiteSpace(prev))
            {
                lines.Add(prev.Trim());
            }
        }

        var seen = new HashSet<string>(lines, StringComparer.Ordinal);
        foreach (OcrTextBlock b in blocks)
        {
            string src = b.Text.Trim();
            if (string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            string entry = _bubbleTranslations.TryGetValue(b.Id, out TranslationResult? tr) &&
                           !string.IsNullOrWhiteSpace(tr.TranslatedText)
                ? $"{src} => {tr.TranslatedText.Trim()}"
                : src;

            if (seen.Add(entry))
            {
                lines.Add(entry);
            }
        }

        return lines.Count > 0 ? lines.Take(16).ToArray() : null;
    }

    private void RefreshAllBubbleOverlayVisuals()
    {
        foreach (Canvas canvas in EnumerateActiveOcrCanvases())
        {
            foreach (UIElement child in canvas.Children)
            {
                if (child is Button btn && TryGetButtonTag(btn.Tag, out OcrButtonTag? tag))
                {
                    ApplyBubbleOverlayVisual(btn, tag, ReferenceEquals(btn, _activeOcrBox));
                }
            }
        }
    }

    private void RefreshBubbleOverlayById(string blockId)
    {
        foreach (Canvas canvas in EnumerateActiveOcrCanvases())
        {
            foreach (UIElement child in canvas.Children)
            {
                if (child is Button btn &&
                    TryGetButtonTag(btn.Tag, out OcrButtonTag? tag) &&
                    string.Equals(tag.Block.Id, blockId, StringComparison.Ordinal))
                {
                    ApplyBubbleOverlayVisual(btn, tag, ReferenceEquals(btn, _activeOcrBox));
                }
            }
        }
    }

    private void ApplyBubbleOverlayVisual(Button box, OcrButtonTag tag, bool isActive)
    {
        OcrTextBlock block = tag.Block;
        _bubbleTranslations.TryGetValue(block.Id, out TranslationResult? tr);
        bool inlineEnabled = InlineTranslationToggleButton.IsChecked == true;
        string? inlineText = inlineEnabled &&
            tr is not null &&
            !string.IsNullOrWhiteSpace(tr.TranslatedText) &&
            tag.BaseWidth >= 20 &&
            tag.BaseHeight >= 18
            ? tr.TranslatedText
            : null;

        if (!string.IsNullOrEmpty(inlineText) && _useMaskInlineStyle)
        {
            double targetWidth = tag.BaseWidth;
            double targetLeft = tag.BaseLeft;
            if (tag.BaseWidth < 88 && tag.BaseHeight >= tag.BaseWidth * 1.35 && tag.CanvasWidth > 24)
            {
                double maxCap = inlineText.Length > 8 ? 136.0 : 112.0;
                double desiredW = Math.Clamp(Math.Max(84.0, tag.BaseHeight * 0.48), tag.BaseWidth, maxCap);
                targetWidth = Math.Min(desiredW, Math.Max(tag.BaseWidth, tag.CanvasWidth - 4.0));
                double center = tag.BaseLeft + tag.BaseWidth / 2.0;
                targetLeft = Math.Clamp(center - targetWidth / 2.0, 2.0, Math.Max(2.0, tag.CanvasWidth - targetWidth - 2.0));
            }

            box.Width = targetWidth;
            box.Height = tag.BaseHeight;
            Canvas.SetLeft(box, targetLeft);
            Canvas.SetTop(box, tag.BaseTop);

            Color fillColor = ResolveMaskFillColor(block.FillArgb);
            Color inkColor = ResolveMaskTextColor(fillColor, block.TextArgb);
            box.Background = new SolidColorBrush(fillColor);
            box.BorderBrush = isActive
                ? (_ocrMaskActiveBorderBrush ??= new SolidColorBrush(Color.FromArgb(255, 38, 166, 154)))
                : new SolidColorBrush(Color.FromArgb(36, inkColor.R, inkColor.G, inkColor.B));
            box.BorderThickness = new Thickness(isActive ? 2.0 : 0.75);
            double maskRadius = Math.Clamp(Math.Min(targetWidth, tag.BaseHeight) * 0.22, 6.0, 14.0);
            box.CornerRadius = new CornerRadius(maskRadius);

            double fontSize = SelectInpaintFontSize(targetWidth, tag.BaseHeight, inlineText);
            TextBlock textBlock = BuildOverlayTextBlock(inlineText, new SolidColorBrush(inkColor), fontSize, fontSize + 2.5);
            textBlock.VerticalAlignment = VerticalAlignment.Center;
            box.Content = new Border
            {
                Padding = new Thickness(4, 2, 4, 2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Child = textBlock
            };
            ToolTipService.SetToolTip(box, $"原文：{block.Text}\n译文：{inlineText}");
            AutomationProperties.SetHelpText(box, inlineText);
            return;
        }

        // Restore exact OCR bounding geometry when in badge mode or awaiting translation.
        box.Width = tag.BaseWidth;
        box.Height = tag.BaseHeight;
        Canvas.SetLeft(box, tag.BaseLeft);
        Canvas.SetTop(box, tag.BaseTop);
        box.Background = isActive
            ? (_ocrBadgeActiveFillBrush ??= new SolidColorBrush(Color.FromArgb(92, 0, 191, 165)))
            : (_ocrBadgeIdleFillBrush ??= new SolidColorBrush(Color.FromArgb(46, 0, 191, 165)));
        box.BorderBrush = isActive
            ? (_ocrBadgeActiveBorderBrush ??= new SolidColorBrush(Color.FromArgb(255, 128, 203, 196)))
            : (_ocrBadgeIdleBorderBrush ??= new SolidColorBrush(Color.FromArgb(215, 38, 166, 154)));
        box.BorderThickness = new Thickness(isActive || tag.CustomBox ? 2.0 : 1.5);
        box.CornerRadius = new CornerRadius(5);

        if (!string.IsNullOrEmpty(inlineText))
        {
            double fontSize = tag.BaseWidth < 58 || tag.BaseHeight < 46 || inlineText.Length > 18
                ? 10.5
                : (tag.BaseWidth < 100 || tag.BaseHeight < 76 ? 11.5 : 12.5);
            TextBlock textBlock = BuildOverlayTextBlock(
                inlineText,
                _ocrBadgeTextBrush ??= new SolidColorBrush(Color.FromArgb(255, 245, 250, 252)),
                fontSize,
                fontSize + 3);
            box.Content = new Border
            {
                Background = _ocrBadgeCardBgBrush ??= new SolidColorBrush(Color.FromArgb(218, 16, 24, 30)),
                BorderBrush = _ocrBadgeIdleBorderBrush ??= new SolidColorBrush(Color.FromArgb(215, 38, 166, 154)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 3, 5, 3),
                Margin = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = Math.Max(20, tag.BaseWidth - 4),
                MaxHeight = Math.Max(18, tag.BaseHeight - 4),
                IsHitTestVisible = false,
                Child = textBlock
            };
            ToolTipService.SetToolTip(box, $"原文：{block.Text}\n译文：{inlineText}");
            AutomationProperties.SetHelpText(box, inlineText);
            return;
        }

        box.Content = null;
        string tip = tr is not null && !string.IsNullOrWhiteSpace(tr.TranslatedText)
            ? $"原文：{block.Text}\n译文：{tr.TranslatedText}"
            : $"点击翻译：{block.Text}";
        ToolTipService.SetToolTip(box, tip);
        AutomationProperties.SetHelpText(box, tr?.TranslatedText ?? string.Empty);
    }

    private static TextBlock BuildOverlayTextBlock(string text, Brush foreground, double fontSize, double lineHeight)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontSize = fontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = lineHeight
        };
        AutomationProperties.SetName(tb, text);
        return tb;
    }

    private static Color ArgbToColor(uint argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF));

    private static double ComputeLuminance(Color c) =>
        0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    private static Color ResolveMaskFillColor(uint fillArgb)
    {
        if (fillArgb == 0)
        {
            return Color.FromArgb(255, 250, 250, 250);
        }

        Color sampled = ArgbToColor(fillArgb);
        return Color.FromArgb(255, sampled.R, sampled.G, sampled.B);
    }

    private static Color ResolveMaskTextColor(Color fill, uint textArgb)
    {
        double fillLuma = ComputeLuminance(fill);
        if (textArgb != 0)
        {
            Color sampled = ArgbToColor(textArgb);
            if (Math.Abs(fillLuma - ComputeLuminance(sampled)) >= 95.0)
            {
                return Color.FromArgb(255, sampled.R, sampled.G, sampled.B);
            }
        }

        return fillLuma >= 128.0
            ? Color.FromArgb(255, 22, 24, 28)
            : Color.FromArgb(255, 246, 248, 252);
    }

    private static double SelectInpaintFontSize(double boxWidth, double boxHeight, string text)
    {
        double usableW = Math.Max(16.0, boxWidth - 12.0);
        double usableH = Math.Max(16.0, boxHeight - 6.0);
        double[] candidates = [18.0, 16.0, 14.5, 13.0, 11.5, 10.0, 9.0];
        if (text.Length <= 6)
        {
            foreach (double fs in candidates)
            {
                int cols = Math.Max(1, (int)Math.Floor(usableW / (fs * 1.05)));
                int rows = Math.Max(1, (int)Math.Floor(usableH / (fs * 1.25)));
                if (cols >= text.Length && rows >= 1)
                {
                    return fs;
                }
            }
        }

        foreach (double fs in candidates)
        {
            int cols = Math.Max(1, (int)Math.Floor(usableW / (fs * 1.05)));
            int rows = Math.Max(1, (int)Math.Floor(usableH / (fs * 1.25)));
            if (cols * rows >= text.Length)
            {
                return fs;
            }
        }

        return 9.0;
    }

    private void UpdateOcrActionButtons()
    {
        List<OcrTextBlock> activeBlocks = _ocrModeEnabled ? CollectActiveOcrBlocks() : [];
        bool hasBlocks = activeBlocks.Count > 0;
        bool hasTranslations = _ocrModeEnabled && _bubbleTranslations.Count > 0;
        bool allTranslated = hasBlocks && activeBlocks.All(b => _bubbleTranslations.ContainsKey(b.Id));

        TranslateAllOcrButton.Visibility = hasBlocks ? Visibility.Visible : Visibility.Collapsed;
        TranslateAllOcrButton.IsEnabled = hasBlocks && !_ocrBatchTranslating;
        TranslateAllOcrButton.Content = _ocrBatchTranslating
            ? "正在翻译..."
            : allTranslated
                ? "重译全页"
                : "翻译全页";

        InlineTranslationToggleButton.Visibility = hasBlocks || hasTranslations
            ? Visibility.Visible
            : Visibility.Collapsed;
        InlineStyleToggleButton.Visibility = hasBlocks || hasTranslations
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutoTranslateToggleButton.Visibility = _ocrModeEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateOcrBubbleNavigationUi();
    }

    private void SyncOcrEngineComboSelection()
    {
        string targetId = string.IsNullOrWhiteSpace(_selectedOcrProviderOverride)
            ? ViewModel.ConfiguredTranslationProvider
            : _selectedOcrProviderOverride;

        _syncingOcrEngineCombo = true;
        try
        {
            SelectComboItemByTag(OcrEngineOverrideComboBox, targetId);
            SelectComboItemByTag(OcrPillEngineComboBox, targetId);
        }
        finally
        {
            _syncingOcrEngineCombo = false;
        }
    }

    private static void SelectComboItemByTag(ComboBox comboBox, string targetId)
    {
        foreach (object obj in comboBox.Items)
        {
            if (obj is ComboBoxItem item &&
                string.Equals(item.Tag as string, targetId, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private void ShowOcrStatus(string text, bool scanning)
    {
        OcrStatusText.Text = text;
        OcrScanProgressRing.IsActive = scanning;
        OcrScanProgressRing.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        OcrStatusPill.Visibility = Visibility.Visible;
        UpdateOcrActionButtons();
    }

    private void HideOcrTranslationPopup()
    {
        CancellationTokenSource? translateCts = Interlocked.Exchange(ref _ocrTranslateCts, null);
        translateCts?.Cancel();
        translateCts?.Dispose();

        OcrTranslateProgress.Visibility = Visibility.Collapsed;
        OcrTranslationCard.Visibility = Visibility.Collapsed;
        if (_activeOcrBox is not null && TryGetButtonTag(_activeOcrBox.Tag, out OcrButtonTag? prevTag))
        {
            Button prev = _activeOcrBox;
            _activeOcrBox = null;
            ApplyBubbleOverlayVisual(prev, prevTag, isActive: false);
        }

        _activeOcrBox = null;
        _activeOcrBlock = null;
        UpdateOcrBubbleNavigationUi();
        UpdateOcrActionButtons();
    }

    private void ClearOcrCanvases(bool captureOutgoingDialogue = false)
    {
        if (captureOutgoingDialogue)
        {
            var outgoing = new List<string>();
            void CaptureResult(OcrPageResult? r)
            {
                if (r is null)
                {
                    return;
                }

                foreach (OcrTextBlock b in r.Blocks)
                {
                    string t = b.Text.Trim();
                    if (string.IsNullOrWhiteSpace(t))
                    {
                        continue;
                    }

                    string entry = _bubbleTranslations.TryGetValue(b.Id, out TranslationResult? tr) &&
                                   !string.IsNullOrWhiteSpace(tr.TranslatedText)
                        ? $"{t} => {tr.TranslatedText.Trim()}"
                        : t;
                    outgoing.Add(entry);
                }
            }

            if (IsRightToLeftLayout)
            {
                CaptureResult(_rightOcrResult);
                CaptureResult(_leftOcrResult);
            }
            else
            {
                CaptureResult(_leftOcrResult);
                CaptureResult(_rightOcrResult);
            }

            CaptureResult(_singleOcrResult);
            if (outgoing.Count > 0)
            {
                _previousPageDialogue = outgoing.TakeLast(4).ToList();
            }
        }

        ResetOcrDragState();
        _singleOcrResult = null;
        _leftOcrResult = null;
        _rightOcrResult = null;
        _singleCustomBlocks.Clear();
        _leftCustomBlocks.Clear();
        _rightCustomBlocks.Clear();
        _activeOcrBox = null;
        _activeOcrBlock = null;
        _bubbleTranslations.Clear();
        _ocrBatchTranslating = false;
        SingleOcrCanvas.Children.Clear();
        LeftOcrCanvas.Children.Clear();
        RightOcrCanvas.Children.Clear();
        UpdateOcrBubbleNavigationUi();
        UpdateOcrActionButtons();
    }

    private void ClearOcrOverlaysAndCard()
    {
        CancelOcrWork();
        _stripOcrDebounceTimer.Stop();
        _previousPageDialogue.Clear();
        _stripOcrResults.Clear();
        _stripCustomBlocks.Clear();
        if (StripListView.ItemsPanelRoot is Panel itemsPanel)
        {
            foreach (UIElement itemElem in itemsPanel.Children)
            {
                FindDescendant<Canvas>(itemElem)?.Children.Clear();
            }
        }

        ClearOcrCanvases(captureOutgoingDialogue: false);
        HideOcrTranslationPopup();
        OcrStatusPill.Visibility = Visibility.Collapsed;
        OcrScanProgressRing.IsActive = false;
    }

    private void CancelOcrWork()
    {
        CancellationTokenSource? scanCts = Interlocked.Exchange(ref _ocrScanCts, null);
        scanCts?.Cancel();
        scanCts?.Dispose();

        CancellationTokenSource? batchCts = Interlocked.Exchange(ref _ocrBatchCts, null);
        batchCts?.Cancel();
        batchCts?.Dispose();
        _ocrBatchTranslating = false;

        CancellationTokenSource? translateCts = Interlocked.Exchange(ref _ocrTranslateCts, null);
        translateCts?.Cancel();
        translateCts?.Dispose();

        CancellationTokenSource? prefetchCts = Interlocked.Exchange(ref _ocrPrefetchCts, null);
        prefetchCts?.Cancel();
        prefetchCts?.Dispose();
    }

    private static void CopyTextToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch
        {
        }
    }

    private bool TryGetCachedThumbnailBitmap(int index, out WriteableBitmap? bitmap)
    {
        if (_thumbBitmapCache.TryGetValue(index, out LinkedListNode<(int Index, WriteableBitmap Bitmap)>? node))
        {
            _thumbBitmapLruOrder.Remove(node);
            _thumbBitmapLruOrder.AddFirst(node);
            bitmap = node.Value.Bitmap;
            return true;
        }

        bitmap = null;
        return false;
    }

    private void StoreCachedThumbnailBitmap(int index, WriteableBitmap bitmap)
    {
        if (_thumbBitmapCache.TryGetValue(index, out LinkedListNode<(int Index, WriteableBitmap Bitmap)>? existing))
        {
            _thumbBitmapLruOrder.Remove(existing);
            _thumbBitmapCache.Remove(index);
        }

        var node = _thumbBitmapLruOrder.AddFirst((index, bitmap));
        _thumbBitmapCache[index] = node;
        while (_thumbBitmapLruOrder.Count > MaxCachedThumbnails && _thumbBitmapLruOrder.Last is { } oldest)
        {
            _thumbBitmapLruOrder.RemoveLast();
            _thumbBitmapCache.Remove(oldest.Value.Index);
        }
    }

    private void ClearThumbnailBitmapCache()
    {
        _thumbBitmapCache.Clear();
        _thumbBitmapLruOrder.Clear();
    }

    internal static Rect ComputeRenderedBitmapRect(
        double controlWidth,
        double controlHeight,
        int pixelWidth,
        int pixelHeight,
        Stretch stretch)
    {
        if (controlWidth <= 0 || controlHeight <= 0 || pixelWidth <= 0 || pixelHeight <= 0)
        {
            return new Rect(0, 0, 0, 0);
        }

        if (stretch == Stretch.None)
        {
            double x = Math.Max(0, (controlWidth - pixelWidth) / 2.0);
            double y = Math.Max(0, (controlHeight - pixelHeight) / 2.0);
            return new Rect(x, y, Math.Min(controlWidth, pixelWidth), Math.Min(controlHeight, pixelHeight));
        }

        double scale = Math.Min(controlWidth / pixelWidth, controlHeight / pixelHeight);
        double renderW = pixelWidth * scale;
        double renderH = pixelHeight * scale;
        double offsetX = (controlWidth - renderW) / 2.0;
        double offsetY = (controlHeight - renderH) / 2.0;
        return new Rect(offsetX, offsetY, renderW, renderH);
    }
}

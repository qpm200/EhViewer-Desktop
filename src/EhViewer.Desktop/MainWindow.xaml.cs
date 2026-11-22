using EhViewer.Core.Client;
using EhViewer.Core.Client.Parser;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Theming;
using EhViewer.Desktop.ViewModels;
using EhViewer.Desktop.Views;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace EhViewer.Desktop;

public sealed partial class MainWindow : Window, IShellCoordinator
{
    private readonly AppServices _services;
    private readonly WindowManager _windowManager;
    private readonly ILogger<MainWindow> _logger;
    private readonly HashSet<long> _favoriteOverrides = [];
    private readonly CancellationTokenSource _lifetime = new();

    private static readonly Thickness ZeroThickness = new(0);
    private static readonly Thickness SliderThumbBlackBorderThickness = new(1.5);
    private static readonly Thickness NavHeaderExpandedPadding = new(16, 14, 14, 14);
    private static readonly Thickness NavHeaderCompactPadding = new(8, 10, 8, 10);
    private static readonly Thickness NavHeaderNoBannerBorderThickness = new(0, 0, 1, 1);
    private static readonly Thickness DrawerFooterExpandedPadding = new(12, 10, 12, 10);
    private static readonly Thickness DrawerFooterCompactPadding = new(8, 8, 8, 8);
    private static readonly CornerRadius AvatarBannerExpandedRadius = new(28);
    private static readonly CornerRadius AvatarNoBannerExpandedRadius = new(20);
    private static readonly CornerRadius AvatarCompactRadius = new(18);
    private static readonly System.Numerics.Vector3 HoverOverlayTranslation = new(0, 0, 24);

    private bool _initialized;
    private bool _servicesDisposed;
    private bool _suppressNavChange;
    private bool _isReaderFullscreen;
    private bool _isDrawerHoverExpanded;
    private bool _wasDrawerHoverOverlay;
    private bool _wasDrawerVisuallyExpanded = true;
    private bool _suppressNextHoverExpand;
    private bool _themeTransitionHostLoaded;
    private int _themeTransitionVersion;
    private double _currentThemeThumbX;
    private bool _isDraggingThemeSlider;
    private bool _themeDragMoved;
    private double _themeDragStartPointerX;
    private double _themeDragStartThumbX;
    private double _cachedThemeSliderTrackWidth;
    private double _lastThumbTrackWidth;
    private int _lastThumbStopIndex = -1;
    private int _lastSliderStopTheme = -1;
    private Brush? _lastSliderPrimaryBrush;
    private Brush? _lastSliderSecondaryBrush;
    private SolidColorBrush? _whiteBrush;
    private SolidColorBrush? _blackSliderThumbBgBrush;
    private SolidColorBrush? _bannerToggleBgBrush;
    private SolidColorBrush? _bannerAvatarBgBrush;
    private SolidColorBrush? _bannerAvatarBorderBrush;
    private SolidColorBrush? _bannerUserSubFgBrush;
    private bool _drawerVisualInitialized;
    private bool _lastDrawerHasBanner;
    private bool _lastAppliedPinnedCollapsed;
    private bool _lastAppliedHoverOverlay;
    private bool _lastAppliedVisuallyExpanded;
    private double _lastAppliedSpacerWidth = -1;
    private double _lastAppliedDrawerWidth = -1;
    private double _lastAppliedHeaderHeight = -1;
    private double _lastAppliedNavUserInfoMaxWidth = -1;
    private Brush? _lastAppliedHeaderBgBrush;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _drawerHoverExpandTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _drawerCollapseTimer;

    public MainWindow(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
        _windowManager = services.GetRequiredService<WindowManager>();
        _logger = services.GetRequiredService<ILogger<MainWindow>>();

        InitializeComponent();

        // Populate exact 8 Android drawer items while preserving ShellViewModel.Entries intact for tests
        List<NavigationEntry> drawerItems = _services.GetRequiredService<ShellViewModel>()
            .Entries
            .Where(static e => e.Kind != NavigationKind.Login)
            .ToList();
        NavigationItems.ItemsSource = drawerItems;

        var settingsAccelerator = new KeyboardAccelerator
        {
            Key = (Windows.System.VirtualKey)188,
            Modifiers = Windows.System.VirtualKeyModifiers.Control
        };
        settingsAccelerator.Invoked += OnShortcutSettingsInvoked;
        RootGrid.KeyboardAccelerators.Add(settingsAccelerator);
        RootGrid.PointerPressed += (_, _) => MarkUserActivity();
        RootGrid.KeyDown += (_, _) => MarkUserActivity();

        Activated += OnFirstActivated;
        Closed += OnWindowClosed;

        _drawerHoverExpandTimer = DispatcherQueue.CreateTimer();
        _drawerHoverExpandTimer.Interval = TimeSpan.FromMilliseconds(MaterialThemeService.ActiveBehavior.DrawerHoverExpandDelayMs);
        _drawerHoverExpandTimer.IsRepeating = false;
        _drawerHoverExpandTimer.Tick += (_, _) =>
        {
            if (_services.Settings.CollapseNavigationDrawer &&
                _services.Settings.AutoExpandSidebarOnHover &&
                !_isDrawerHoverExpanded)
            {
                _isDrawerHoverExpanded = true;
                UpdateNavigationDrawerVisualState();
            }
        };

        _drawerCollapseTimer = DispatcherQueue.CreateTimer();
        _drawerCollapseTimer.Interval = TimeSpan.FromMilliseconds(MaterialThemeService.ActiveBehavior.DrawerHoverCollapseDelayMs);
        _drawerCollapseTimer.IsRepeating = false;
        _drawerCollapseTimer.Tick += (_, _) =>
        {
            _isDrawerHoverExpanded = false;
            UpdateNavigationDrawerVisualState();
        };

        _services.GetRequiredService<GalleryBrowseService>().EventPaneReceived += OnSiteEventPaneReceived;
        ApplyAppearance();
        UpdateSiteSwitchBadge();
        RefreshAccountAndQuota(fetchQuotaIfSignedIn: false);

        _ = _services.GetRequiredService<TagTranslationSource>().EnsureLoadedAsync(_lifetime.Token);
    }

    public AppServices Services => _services;
    public Window HostWindow => this;
    public CancellationToken LifetimeToken => _lifetime.Token;

    /// <summary>Applies Material Light/Dark/AMOLED Black palette + optional Mica backdrop and titlebar color.</summary>
    internal void ApplyAppearance(bool animateThemeThumb = true)
    {
        DesktopSettings settings = _services.Settings;
        bool dark = WindowAppearance.IsEffectivelyDark(settings.Theme);

        if (settings.UseMica)
        {
            WindowAppearance.TryApplyBackdrop(this, 0x000000, dark, applyTitleBarColors: false);
        }
        else if (SystemBackdrop is not null)
        {
            SystemBackdrop = null;
        }

        MaterialThemeService.ApplyPalette(settings.UiTheme, settings.Theme, this);
        UiThemeBehavior behavior = MaterialThemeService.ActiveBehavior;
        if (_drawerHoverExpandTimer is not null)
        {
            _drawerHoverExpandTimer.Interval = TimeSpan.FromMilliseconds(behavior.DrawerHoverExpandDelayMs);
        }
        if (_drawerCollapseTimer is not null)
        {
            _drawerCollapseTimer.Interval = TimeSpan.FromMilliseconds(behavior.DrawerHoverCollapseDelayMs);
        }

        UpdateThemeFooterBadge(settings.Theme, animateThemeThumb);
        if (!settings.CollapseNavigationDrawer || !settings.AutoExpandSidebarOnHover)
        {
            _drawerHoverExpandTimer?.Stop();
            _drawerCollapseTimer?.Stop();
            _isDrawerHoverExpanded = false;
            _suppressNextHoverExpand = false;
        }
        UpdateNavigationDrawerVisualState();
    }

    private static int ResolveSliderStopTheme(int theme)
    {
        if (theme == MaterialThemeService.ThemeBlack)
        {
            return MaterialThemeService.ThemeBlack;
        }

        return WindowAppearance.IsEffectivelyDark(theme)
            ? MaterialThemeService.ThemeDark
            : MaterialThemeService.ThemeLight;
    }

    private static int StopIndexFromTheme(int theme) => ResolveSliderStopTheme(theme) switch
    {
        MaterialThemeService.ThemeDark => 1,
        MaterialThemeService.ThemeBlack => 2,
        _ => 0
    };

    private static int ThemeFromStopIndex(int stopIndex) => Math.Clamp(stopIndex, 0, 2) switch
    {
        1 => MaterialThemeService.ThemeDark,
        2 => MaterialThemeService.ThemeBlack,
        _ => MaterialThemeService.ThemeLight
    };

    private SolidColorBrush GetWhiteBrush() => _whiteBrush ??= UiHelpers.FromArgb(unchecked((int)0xFFFFFFFF));
    private SolidColorBrush GetBlackSliderThumbBgBrush() => _blackSliderThumbBgBrush ??= UiHelpers.FromArgb(unchecked((int)0xFF050608));
    private SolidColorBrush GetBannerToggleBgBrush() => _bannerToggleBgBrush ??= UiHelpers.FromArgb(0x24FFFFFF);
    private SolidColorBrush GetBannerAvatarBgBrush() => _bannerAvatarBgBrush ??= UiHelpers.FromArgb(0x33FFFFFF);
    private SolidColorBrush GetBannerAvatarBorderBrush() => _bannerAvatarBorderBrush ??= UiHelpers.FromArgb(0x66FFFFFF);
    private SolidColorBrush GetBannerUserSubFgBrush() => _bannerUserSubFgBrush ??= UiHelpers.FromArgb(unchecked((int)0xCCFFFFFF));

    private void UpdateThemeFooterBadge(int theme, bool animateThumb = true)
    {
        int stopTheme = ResolveSliderStopTheme(theme);
        string displayName = MaterialThemeService.GetThemeDisplayName(theme);

        ApplyThemeSliderVisualStop(stopTheme);

        ToolTipService.SetToolTip(ThemeCompactBtn, $"切换主题（当前：{displayName}）");
        ThemeCompactIcon.Glyph = theme switch
        {
            MaterialThemeService.ThemeLight => "\uE706",
            MaterialThemeService.ThemeDark => "\uE708",
            MaterialThemeService.ThemeBlack => "\uE771",
            _ => "\uE790"
        };

        if (!_isDraggingThemeSlider && ThemeSliderContainer.Visibility == Visibility.Visible)
        {
            UpdateThemeThumbPosition(animate: animateThumb);
        }
    }

    private void ApplyThemeSliderVisualStop(int stopTheme)
    {
        ResourceDictionary res = Application.Current.Resources;
        Brush primaryBrush = (Brush)res["EhPrimaryBrush"];
        Brush secondaryBrush = (Brush)res["EhTextSecondaryBrush"];

        if (_lastSliderStopTheme == stopTheme &&
            ReferenceEquals(_lastSliderPrimaryBrush, primaryBrush) &&
            ReferenceEquals(_lastSliderSecondaryBrush, secondaryBrush))
        {
            return;
        }

        _lastSliderStopTheme = stopTheme;
        _lastSliderPrimaryBrush = primaryBrush;
        _lastSliderSecondaryBrush = secondaryBrush;

        bool isLight = stopTheme == MaterialThemeService.ThemeLight;
        bool isDark = stopTheme == MaterialThemeService.ThemeDark;
        bool isBlack = stopTheme == MaterialThemeService.ThemeBlack;
        Brush whiteBrush = GetWhiteBrush();

        if (isBlack)
        {
            ThemeSliderThumb.Background = GetBlackSliderThumbBgBrush();
            ThemeSliderThumb.BorderBrush = primaryBrush;
            ThemeSliderThumb.BorderThickness = SliderThumbBlackBorderThickness;
        }
        else
        {
            ThemeSliderThumb.Background = primaryBrush;
            ThemeSliderThumb.BorderThickness = ZeroThickness;
        }

        ThemeLightIcon.Foreground = isLight ? whiteBrush : secondaryBrush;
        ThemeLightText.Foreground = isLight ? whiteBrush : secondaryBrush;
        ThemeDarkIcon.Foreground = isDark ? whiteBrush : secondaryBrush;
        ThemeDarkText.Foreground = isDark ? whiteBrush : secondaryBrush;
        ThemeBlackIcon.Foreground = isBlack ? primaryBrush : secondaryBrush;
        ThemeBlackText.Foreground = isBlack ? whiteBrush : secondaryBrush;
    }

    private double GetThemeSliderTrackWidth()
    {
        if (_cachedThemeSliderTrackWidth > 0)
        {
            return _cachedThemeSliderTrackWidth;
        }

        double actual = ThemeSliderTrackGrid.ActualWidth;
        if (actual > 0)
        {
            _cachedThemeSliderTrackWidth = actual;
        }

        return actual;
    }

    private void OnThemeSliderTrackSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
        {
            return;
        }

        if (Math.Abs(_cachedThemeSliderTrackWidth - e.NewSize.Width) <= 0.01)
        {
            return;
        }

        _cachedThemeSliderTrackWidth = e.NewSize.Width;
        ThemeSliderThumb.Width = Math.Max(0, e.NewSize.Width / 3.0);
        if (!_isDraggingThemeSlider && ThemeSliderContainer.Visibility == Visibility.Visible)
        {
            UpdateThemeThumbPosition(animate: false);
        }
    }

    private void UpdateThemeThumbPosition(bool animate)
    {
        if (ThemeSliderContainer.Visibility != Visibility.Visible)
        {
            return;
        }

        double trackWidth = GetThemeSliderTrackWidth();
        if (trackWidth <= 0)
        {
            return;
        }

        double segWidth = trackWidth / 3.0;
        if (Math.Abs(ThemeSliderThumb.Width - segWidth) > 0.01)
        {
            ThemeSliderThumb.Width = segWidth;
        }

        int stopIndex = StopIndexFromTheme(_services.Settings.Theme);
        double targetX = stopIndex * segWidth;

        if (!animate &&
            _lastThumbTrackWidth > 0 &&
            Math.Abs(_lastThumbTrackWidth - trackWidth) <= 0.01 &&
            _lastThumbStopIndex == stopIndex &&
            Math.Abs(_currentThemeThumbX - targetX) <= 0.01)
        {
            return;
        }

        _lastThumbTrackWidth = trackWidth;
        _lastThumbStopIndex = stopIndex;
        _currentThemeThumbX = targetX;

        if (!animate)
        {
            ThemeMotionHelper.SetSliderThumbPositionImmediate(ThemeSliderThumb, targetX);
            return;
        }

        ThemeMotionHelper.AnimateSliderThumbPosition(
            ThemeSliderThumb,
            targetX,
            MaterialThemeService.ActiveBehavior);
    }

    private void UpdateNavigationDrawerVisualState()
    {
        bool isPinnedCollapsed = _services.Settings.CollapseNavigationDrawer;
        bool isHoverOverlay = isPinnedCollapsed && _isDrawerHoverExpanded;
        bool isVisuallyExpanded = !isPinnedCollapsed || isHoverOverlay;
        Services.Theming.UiThemeMetrics metrics = MaterialThemeService.ActiveMetrics;

        double spacerWidth = isPinnedCollapsed ? metrics.DrawerCompactWidth : metrics.DrawerExpandedWidth;
        if (!_drawerVisualInitialized || Math.Abs(_lastAppliedSpacerWidth - spacerWidth) > 0.01)
        {
            _lastAppliedSpacerWidth = spacerWidth;
            DrawerSpacerColumn.Width = new GridLength(spacerWidth);
        }

        double drawerWidth = isVisuallyExpanded ? metrics.DrawerExpandedWidth : metrics.DrawerCompactWidth;
        if (!_drawerVisualInitialized || Math.Abs(_lastAppliedDrawerWidth - drawerWidth) > 0.01)
        {
            _lastAppliedDrawerWidth = drawerWidth;
            LeftDrawerBorder.Width = drawerWidth;
        }

        if (!_drawerVisualInitialized || _lastAppliedHoverOverlay != isHoverOverlay)
        {
            Grid.SetColumnSpan(LeftDrawerBorder, isHoverOverlay ? 2 : 1);
            LeftDrawerBorder.HorizontalAlignment = isHoverOverlay ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            Canvas.SetZIndex(LeftDrawerBorder, isHoverOverlay ? 20 : 0);
            LeftDrawerBorder.Translation = isHoverOverlay
                ? HoverOverlayTranslation
                : System.Numerics.Vector3.Zero;
        }

        if (isHoverOverlay != _wasDrawerHoverOverlay || isVisuallyExpanded != _wasDrawerVisuallyExpanded)
        {
            _wasDrawerHoverOverlay = isHoverOverlay;
            _wasDrawerVisuallyExpanded = isVisuallyExpanded;
            ThemeMotionHelper.AnimateDrawerHoverTransition(LeftDrawerBorder, isHoverOverlay, MaterialThemeService.ActiveBehavior);
        }

        bool hasBanner = metrics.ShowDrawerHeaderBanner;
        ResourceDictionary res = Application.Current.Resources;
        if (!_drawerVisualInitialized)
        {
            NavHeaderBorder.Visibility = Visibility.Visible;
            NavUserInfoPanel.VerticalAlignment = VerticalAlignment.Center;
        }

        double headerHeight = isVisuallyExpanded ? metrics.DrawerHeaderExpandedHeight : metrics.DrawerHeaderCompactHeight;
        if (!_drawerVisualInitialized || Math.Abs(_lastAppliedHeaderHeight - headerHeight) > 0.01)
        {
            _lastAppliedHeaderHeight = headerHeight;
            NavHeaderBorder.Height = headerHeight;
        }

        bool expandedChanged = !_drawerVisualInitialized || _lastAppliedVisuallyExpanded != isVisuallyExpanded;
        bool bannerChanged = !_drawerVisualInitialized || _lastDrawerHasBanner != hasBanner;
        Brush headerBgBrush = (Brush)res[hasBanner ? "EhDrawerHeaderBrush" : "EhDrawerSurfaceBrush"];
        bool brushesChanged = bannerChanged || !ReferenceEquals(_lastAppliedHeaderBgBrush, headerBgBrush);

        if (expandedChanged)
        {
            NavHeaderBorder.Padding = isVisuallyExpanded
                ? NavHeaderExpandedPadding
                : NavHeaderCompactPadding;
        }

        if (brushesChanged)
        {
            _lastAppliedHeaderBgBrush = headerBgBrush;
            NavHeaderBorder.Background = headerBgBrush;
            NavHeaderBorder.BorderBrush = (Brush)res["EhDividerBrush"];
            NavHeaderBorder.BorderThickness = hasBanner ? ZeroThickness : NavHeaderNoBannerBorderThickness;

            if (hasBanner)
            {
                Brush white = GetWhiteBrush();
                DrawerToggleBtn.Background = GetBannerToggleBgBrush();
                DrawerToggleIcon.Foreground = white;
                NavSiteSwitchBtn.Background = (Brush)res["EhSiteBadgeBackgroundBrush"];
                NavSiteSwitchBtn.BorderBrush = (Brush)res["EhSiteBadgeBorderBrush"];
                NavSiteSwitchBtn.Foreground = white;
                NavAvatarContainer.Background = GetBannerAvatarBgBrush();
                NavAvatarContainer.BorderBrush = GetBannerAvatarBorderBrush();
                NavAvatarFallbackIcon.Foreground = white;
                NavUserNameText.Foreground = white;
                NavUserSubText.Foreground = GetBannerUserSubFgBrush();
            }
            else
            {
                Brush subtle = (Brush)res["EhSubtleSurfaceBrush"];
                Brush textPrimary = (Brush)res["EhTextPrimaryBrush"];
                DrawerToggleBtn.Background = subtle;
                DrawerToggleIcon.Foreground = textPrimary;
                NavSiteSwitchBtn.Background = subtle;
                NavSiteSwitchBtn.BorderBrush = (Brush)res["EhCardBorderBrush"];
                NavSiteSwitchBtn.Foreground = textPrimary;
                NavAvatarContainer.Background = (Brush)res["EhTagBackgroundBrush"];
                NavAvatarContainer.BorderBrush = (Brush)res["EhTagBorderBrush"];
                NavAvatarFallbackIcon.Foreground = (Brush)res["EhPrimaryBrush"];
                NavUserNameText.Foreground = textPrimary;
                NavUserSubText.Foreground = (Brush)res["EhTextSecondaryBrush"];
            }
        }

        if (bannerChanged || expandedChanged)
        {
            if (hasBanner)
            {
                NavHeaderUserStack.Orientation = Orientation.Vertical;
                NavHeaderUserStack.Spacing = 8;
            }
            else
            {
                NavHeaderUserStack.Orientation = isVisuallyExpanded ? Orientation.Horizontal : Orientation.Vertical;
                NavHeaderUserStack.Spacing = isVisuallyExpanded ? 10 : 4;
            }

            double avatarSize = isVisuallyExpanded ? (hasBanner ? 56 : 40) : 36;
            NavAvatarContainer.Width = avatarSize;
            NavAvatarContainer.Height = avatarSize;
            NavAvatarContainer.CornerRadius = isVisuallyExpanded
                ? (hasBanner ? AvatarBannerExpandedRadius : AvatarNoBannerExpandedRadius)
                : AvatarCompactRadius;
            NavAvatarEllipse.Width = avatarSize;
            NavAvatarEllipse.Height = avatarSize;
            NavAvatarFallbackIcon.FontSize = isVisuallyExpanded ? (hasBanner ? 24 : 18) : 17;
        }

        double maxInfoWidth = isVisuallyExpanded && !hasBanner
            ? Math.Max(80, metrics.DrawerExpandedWidth - 82)
            : double.PositiveInfinity;
        if (!_drawerVisualInitialized || _lastAppliedNavUserInfoMaxWidth != maxInfoWidth)
        {
            _lastAppliedNavUserInfoMaxWidth = maxInfoWidth;
            NavUserInfoPanel.MaxWidth = maxInfoWidth;
        }

        if (!_drawerVisualInitialized ||
            _lastAppliedPinnedCollapsed != isPinnedCollapsed ||
            _lastAppliedHoverOverlay != isHoverOverlay)
        {
            if (!isPinnedCollapsed)
            {
                DrawerToggleIcon.Glyph = "\uE8A0";
                ToolTipService.SetToolTip(DrawerToggleBtn, "收缩左侧导航栏（仅保留图标）");
            }
            else if (isHoverOverlay)
            {
                DrawerToggleIcon.Glyph = "\uE840";
                ToolTipService.SetToolTip(DrawerToggleBtn, "恢复并固定展开导航栏");
            }
            else
            {
                DrawerToggleIcon.Glyph = "\uE89F";
                ToolTipService.SetToolTip(DrawerToggleBtn, "恢复展开左侧导航栏");
            }
        }

        if (expandedChanged)
        {
            Visibility expandedVisibility = isVisuallyExpanded ? Visibility.Visible : Visibility.Collapsed;
            NavSiteSwitchBtn.Visibility = expandedVisibility;
            NavUserInfoPanel.Visibility = expandedVisibility;
            NavAvatarContainer.HorizontalAlignment = isVisuallyExpanded ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            NavHeaderUserStack.HorizontalAlignment = isVisuallyExpanded ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            DrawerToggleBtn.HorizontalAlignment = isVisuallyExpanded ? HorizontalAlignment.Left : HorizontalAlignment.Center;

            bool compact = !isVisuallyExpanded;
            if (NavigationItems.ItemsSource is IReadOnlyList<NavigationEntry> navEntries)
            {
                for (int i = 0; i < navEntries.Count; i++)
                {
                    navEntries[i].IsCompact = compact;
                }
            }
            else
            {
                ItemCollection items = NavigationItems.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] is NavigationEntry entry)
                    {
                        entry.IsCompact = compact;
                    }
                }
            }

            DrawerFooterBorder.Padding = isVisuallyExpanded
                ? DrawerFooterExpandedPadding
                : DrawerFooterCompactPadding;
            ClipboardBtn.HorizontalContentAlignment = isVisuallyExpanded ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            ClipboardBtnText.Visibility = expandedVisibility;
            LimitsCardBorder.Visibility = expandedVisibility;
            ThemeSliderContainer.Visibility = expandedVisibility;
            ThemeCompactBtn.Visibility = isVisuallyExpanded ? Visibility.Collapsed : Visibility.Visible;
        }

        _drawerVisualInitialized = true;
        _lastDrawerHasBanner = hasBanner;
        _lastAppliedPinnedCollapsed = isPinnedCollapsed;
        _lastAppliedHoverOverlay = isHoverOverlay;
        _lastAppliedVisuallyExpanded = isVisuallyExpanded;

        if (isVisuallyExpanded)
        {
            UpdateThemeThumbPosition(animate: false);
        }
    }

    private void OnDrawerToggleClick(object sender, RoutedEventArgs e)
    {
        _drawerHoverExpandTimer?.Stop();
        _drawerCollapseTimer?.Stop();
        _isDrawerHoverExpanded = false;

        bool collapse = !_services.Settings.CollapseNavigationDrawer;
        _suppressNextHoverExpand = collapse;
        _services.UpdateSettings(s => s.CollapseNavigationDrawer = collapse);
        if (SceneContentHost.Content is Views.SettingsPage settingsPage)
        {
            settingsPage.SyncNavigationDrawerCollapsed(collapse);
        }

        UpdateNavigationDrawerVisualState();
        SetStatus(collapse ? "已收缩左侧导航栏（点击左上角按钮可恢复展开）" : "已恢复展开左侧导航栏");
    }

    private void OnLeftDrawerPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _drawerCollapseTimer?.Stop();
        if (_suppressNextHoverExpand)
        {
            return;
        }

        if (_services.Settings.CollapseNavigationDrawer &&
            _services.Settings.AutoExpandSidebarOnHover &&
            !_isDrawerHoverExpanded)
        {
            int expandDelayMs = MaterialThemeService.ActiveBehavior.DrawerHoverExpandDelayMs;
            if (expandDelayMs <= 0)
            {
                _isDrawerHoverExpanded = true;
                UpdateNavigationDrawerVisualState();
            }
            else if (_drawerHoverExpandTimer is not null)
            {
                _drawerHoverExpandTimer.Interval = TimeSpan.FromMilliseconds(expandDelayMs);
                _drawerHoverExpandTimer.Start();
            }
            else
            {
                _isDrawerHoverExpanded = true;
                UpdateNavigationDrawerVisualState();
            }
        }
    }

    private void OnLeftDrawerPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _suppressNextHoverExpand = false;
        _drawerHoverExpandTimer?.Stop();
        if (_isDrawerHoverExpanded && _drawerCollapseTimer is not null)
        {
            _drawerCollapseTimer.Interval = TimeSpan.FromMilliseconds(MaterialThemeService.ActiveBehavior.DrawerHoverCollapseDelayMs);
            _drawerCollapseTimer.Start();
        }
    }

    private void UpdateSiteSwitchBadge()
    {
        NavSiteSwitchBtn.Content = _services.Settings.GallerySite == EhSite.Ex ? "ExHentai" : "E-Hentai";
    }

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            DismissHoverPreview();
        }

        if (_initialized)
        {
            return;
        }

        _initialized = true;
        InitializeShellIntegration();
        WindowPlacementHelper.Restore(AppWindow, _services.Settings);

        if (!await EnsureUnlockedAsync().ConfigureAwait(true))
        {
            return;
        }

        _services.GetRequiredService<UConfigSyncService>().ApplySettings(_services.Settings);

        int resumed = _services.GetRequiredService<DownloadManager>().ResumePending();
        if (resumed > 0)
        {
            _logger.LogInformation("已自动恢复 {Count} 个未完成下载任务", resumed);
        }

        RefreshAccountAndQuota(fetchQuotaIfSignedIn: true);
        _ = CheckUpdateSilentlyIfEnabledAsync();

        await SelectAndNavigateRootAsync(NavigationKind.Home).ConfigureAwait(true);
        if (WindowsAppLockService.IsLockEnabled(_services.Settings))
        {
            RestoreForegroundAfterUnlock();
        }
    }

    private void OnSiteEventPaneReceived(string html)
    {
        string plain = EhHtmlHelper.HtmlToPlainText(html);
        if (string.IsNullOrWhiteSpace(plain))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() => ShowBanner("站点通知 (EventPane)", plain, InfoBarSeverity.Informational));
    }

    private void OnNavigationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavChange || NavigationItems.SelectedItem is not NavigationEntry entry)
        {
            return;
        }

        _ = NavigateToRootKindAsync(entry.Kind);
    }

    private void OnThemeSliderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        double trackWidth = GetThemeSliderTrackWidth();
        if (trackWidth <= 0)
        {
            return;
        }

        e.Handled = true;
        _isDraggingThemeSlider = true;
        _themeDragMoved = false;
        _themeDragStartPointerX = e.GetCurrentPoint(ThemeSliderTrackGrid).Position.X;
        _themeDragStartThumbX = _lastThumbStopIndex >= 0
            ? _currentThemeThumbX
            : ThemeMotionHelper.GetCurrentTranslationX(ThemeSliderThumb);
        ThemeMotionHelper.SetSliderThumbPositionImmediate(ThemeSliderThumb, _themeDragStartThumbX);
        ThemeSliderTrackGrid.CapturePointer(e.Pointer);
        UiThemeBehavior behavior = MaterialThemeService.ActiveBehavior;
        if (behavior.SliderThumbActiveScale > 1.001f)
        {
            ThemeMotionHelper.AnimateElementScale(ThemeSliderThumb, behavior.SliderThumbActiveScale, behavior);
        }
    }

    private void OnThemeSliderPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingThemeSlider)
        {
            return;
        }

        double trackWidth = GetThemeSliderTrackWidth();
        if (trackWidth <= 0)
        {
            return;
        }

        double currentX = e.GetCurrentPoint(ThemeSliderTrackGrid).Position.X;
        double delta = currentX - _themeDragStartPointerX;
        if (!_themeDragMoved && Math.Abs(delta) >= 4.0)
        {
            _themeDragMoved = true;
        }

        if (!_themeDragMoved)
        {
            return;
        }

        double segWidth = trackWidth / 3.0;
        double maxX = segWidth * 2.0;
        double nextX = Math.Clamp(_themeDragStartThumbX + delta, 0.0, maxX);
        _currentThemeThumbX = nextX;
        ThemeMotionHelper.SetSliderThumbPositionImmediate(ThemeSliderThumb, nextX);

        int hoveredStop = Math.Clamp((int)Math.Round(nextX / segWidth), 0, 2);
        ApplyThemeSliderVisualStop(ThemeFromStopIndex(hoveredStop));
    }

    private void OnThemeSliderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingThemeSlider)
        {
            return;
        }

        double releaseX = e.GetCurrentPoint(ThemeSliderTrackGrid).Position.X;
        CommitThemeSliderInteraction(releaseX, e.Pointer);
    }

    private void OnThemeSliderPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        CancelThemeSliderDrag(e.Pointer);
    }

    private void OnThemeSliderPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        CancelThemeSliderDrag(null);
    }

    private void CommitThemeSliderInteraction(double pointerX, Pointer? pointer)
    {
        bool wasDragged = _themeDragMoved;
        _isDraggingThemeSlider = false;
        _themeDragMoved = false;
        if (pointer is not null)
        {
            try
            {
                ThemeSliderTrackGrid.ReleasePointerCapture(pointer);
            }
            catch
            {
            }
        }

        ThemeMotionHelper.AnimateElementScale(ThemeSliderThumb, 1.0f, MaterialThemeService.ActiveBehavior);
        double trackWidth = GetThemeSliderTrackWidth();
        if (trackWidth <= 0)
        {
            return;
        }

        double segWidth = trackWidth / 3.0;
        int targetStop = wasDragged
            ? Math.Clamp((int)Math.Round(_currentThemeThumbX / segWidth), 0, 2)
            : Math.Clamp((int)(pointerX / segWidth), 0, 2);

        int nextTheme = ThemeFromStopIndex(targetStop);
        if (nextTheme != _services.Settings.Theme)
        {
            FrameworkElement anchor = wasDragged ? ThemeSliderThumb : GetStopAnchorElement(targetStop);
            _ = SwitchThemeWithCircularRevealAsync(nextTheme, anchor);
        }
        else
        {
            ApplyThemeSliderVisualStop(ResolveSliderStopTheme(_services.Settings.Theme));
            UpdateThemeThumbPosition(animate: true);
        }
    }

    private void CancelThemeSliderDrag(Pointer? pointer)
    {
        if (!_isDraggingThemeSlider)
        {
            return;
        }

        _isDraggingThemeSlider = false;
        _themeDragMoved = false;
        if (pointer is not null)
        {
            try
            {
                ThemeSliderTrackGrid.ReleasePointerCapture(pointer);
            }
            catch
            {
            }
        }

        ApplyThemeSliderVisualStop(ResolveSliderStopTheme(_services.Settings.Theme));
        ThemeMotionHelper.AnimateElementScale(ThemeSliderThumb, 1.0f, MaterialThemeService.ActiveBehavior);
        UpdateThemeThumbPosition(animate: true);
    }

    private void OnThemeSliderKeyDown(object sender, KeyRoutedEventArgs e)
    {
        int stopIndex = StopIndexFromTheme(_services.Settings.Theme);
        int nextStop = e.Key switch
        {
            Windows.System.VirtualKey.Left => Math.Max(0, stopIndex - 1),
            Windows.System.VirtualKey.Right => Math.Min(2, stopIndex + 1),
            _ => stopIndex
        };

        if (nextStop == stopIndex)
        {
            return;
        }

        e.Handled = true;
        int nextTheme = ThemeFromStopIndex(nextStop);
        if (nextTheme != _services.Settings.Theme)
        {
            _ = SwitchThemeWithCircularRevealAsync(nextTheme, GetStopAnchorElement(nextStop));
        }
    }

    private FrameworkElement GetStopAnchorElement(int stopIndex) => stopIndex switch
    {
        0 => ThemeLightBtn,
        1 => ThemeDarkBtn,
        _ => ThemeBlackBtn
    };

    private void OnThemeCompactClick(object sender, RoutedEventArgs e)
    {
        int next = MaterialThemeService.NextQuickTheme(_services.Settings.Theme);
        _ = SwitchThemeWithCircularRevealAsync(next, ThemeCompactBtn);
    }

    private void SetThemeAndSync(int next, bool animateThumb = true)
    {
        _services.UpdateSettings(s => s.Theme = next);
        if (SceneContentHost.Content is Views.SettingsPage settingsPage)
        {
            settingsPage.SyncThemeIndex(next);
        }

        ApplyAppearance(animateThemeThumb: animateThumb);
    }

    private bool EnsureThemeTransitionHostLoaded()
    {
        if (_themeTransitionHostLoaded && ThemeTransitionHost is not null)
        {
            return true;
        }

        if (RootGrid.FindName(nameof(ThemeTransitionHost)) is Grid host &&
            ThemeOldSnapshotImage is not null &&
            ThemeNewSnapshotImage is not null)
        {
            host.IsHitTestVisible = false;
            _themeTransitionHostLoaded = true;
            return true;
        }

        return false;
    }

    private async Task SwitchThemeWithCircularRevealAsync(int nextTheme, FrameworkElement anchor)
    {
        int version = ++_themeTransitionVersion;
        if (_servicesDisposed)
        {
            return;
        }

        ResetThemeTransitionOverlay();

        if (!ThemeMotionHelper.IsAnimationsEnabled)
        {
            SetThemeAndSync(nextTheme, animateThumb: false);
            return;
        }

        Point origin;
        try
        {
            GeneralTransform transform = anchor.TransformToVisual(RootGrid);
            origin = transform.TransformPoint(new Point(anchor.ActualWidth / 2.0, anchor.ActualHeight / 2.0));
        }
        catch
        {
            origin = new Point(40, Math.Max(0, RootGrid.ActualHeight - 24));
        }

        double rootW = RootGrid.ActualWidth;
        double rootH = RootGrid.ActualHeight;
        if (rootW <= 1.0 || rootH <= 1.0)
        {
            SetThemeAndSync(nextTheme);
            return;
        }

        if (version != _themeTransitionVersion || _servicesDisposed)
        {
            return;
        }

        var oldBitmap = new RenderTargetBitmap();
        try
        {
            await oldBitmap.RenderAsync(ShellContentGrid).AsTask().ConfigureAwait(true);
        }
        catch
        {
            if (version == _themeTransitionVersion && !_servicesDisposed)
            {
                SetThemeAndSync(nextTheme);
            }
            return;
        }

        if (version != _themeTransitionVersion || _servicesDisposed)
        {
            return;
        }

        if (!EnsureThemeTransitionHostLoaded() ||
            ThemeTransitionHost is null ||
            ThemeOldSnapshotImage is null ||
            ThemeNewSnapshotImage is null)
        {
            SetThemeAndSync(nextTheme);
            return;
        }

        ThemeTransitionHost.IsHitTestVisible = false;
        ThemeOldSnapshotImage.Source = oldBitmap;
        ThemeNewSnapshotImage.Source = null;
        ThemeTransitionHost.Visibility = Visibility.Visible;

        SetThemeAndSync(nextTheme, animateThumb: false);

        if (version != _themeTransitionVersion || _servicesDisposed)
        {
            return;
        }

        await WaitForCompositionFrameAsync().ConfigureAwait(true);
        if (version != _themeTransitionVersion || _servicesDisposed)
        {
            return;
        }

        var newBitmap = new RenderTargetBitmap();
        try
        {
            await newBitmap.RenderAsync(ShellContentGrid).AsTask().ConfigureAwait(true);
        }
        catch
        {
            if (version == _themeTransitionVersion && !_servicesDisposed)
            {
                ResetThemeTransitionOverlay();
            }
            return;
        }

        if (version != _themeTransitionVersion || _servicesDisposed)
        {
            return;
        }

        ThemeNewSnapshotImage.Source = newBitmap;

        double dx = Math.Max(origin.X, rootW - origin.X);
        double dy = Math.Max(origin.Y, rootH - origin.Y);
        float maxRadius = (float)Math.Sqrt(dx * dx + dy * dy) + 8f;

        Visual newVisual = ElementCompositionPreview.GetElementVisual(ThemeNewSnapshotImage);
        Compositor compositor = newVisual.Compositor;

        CompositionEllipseGeometry ellipse = compositor.CreateEllipseGeometry();
        ellipse.Center = new System.Numerics.Vector2((float)origin.X, (float)origin.Y);
        ellipse.Radius = new System.Numerics.Vector2(0f, 0f);

        CompositionGeometricClip clip = compositor.CreateGeometricClip(ellipse);
        newVisual.Clip = clip;

        UiThemeBehavior behavior = MaterialThemeService.ActiveBehavior;
        CubicBezierEasingFunction easing = ThemeMotionHelper.CreateEasingFunction(compositor, behavior);
        TimeSpan revealDuration = TimeSpan.FromMilliseconds(Math.Max(160, behavior.ThemeRevealDurationMs));
        Vector2KeyFrameAnimation radiusAnim = compositor.CreateVector2KeyFrameAnimation();
        radiusAnim.InsertKeyFrame(0f, new System.Numerics.Vector2(0f, 0f));
        radiusAnim.InsertKeyFrame(1f, new System.Numerics.Vector2(maxRadius, maxRadius), easing);
        radiusAnim.Duration = revealDuration;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        ellipse.StartAnimation(nameof(CompositionEllipseGeometry.Radius), radiusAnim);
        if (behavior.ThemeSwitchEffect == ThemeSwitchEffectStyle.HyperRadialSoft)
        {
            newVisual.CenterPoint = new System.Numerics.Vector3((float)rootW * 0.5f, (float)rootH * 0.5f, 0f);
            Vector3KeyFrameAnimation settleScale = compositor.CreateVector3KeyFrameAnimation();
            settleScale.InsertKeyFrame(0f, new System.Numerics.Vector3(0.985f, 0.985f, 1f));
            settleScale.InsertKeyFrame(1f, System.Numerics.Vector3.One, easing);
            settleScale.Duration = revealDuration;
            newVisual.StartAnimation(nameof(Visual.Scale), settleScale);
        }
        batch.End();
        batch.Completed += (_, _) =>
        {
            batch.Dispose();
            DispatcherQueue.TryEnqueue(() => tcs.TrySetResult());
        };

        await tcs.Task.ConfigureAwait(true);
        if (version == _themeTransitionVersion && !_servicesDisposed)
        {
            ResetThemeTransitionOverlay();
        }
    }

    private async Task WaitForCompositionFrameAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int remainingFrames = 2;
        void OnRendering(object? sender, object e)
        {
            if (Interlocked.Decrement(ref remainingFrames) <= 0)
            {
                CompositionTarget.Rendering -= OnRendering;
                tcs.TrySetResult();
            }
        }

        CompositionTarget.Rendering += OnRendering;
        try
        {
            await Task.WhenAny(tcs.Task, Task.Delay(64)).ConfigureAwait(true);
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void ResetThemeTransitionOverlay()
    {
        if (!_themeTransitionHostLoaded ||
            ThemeTransitionHost is null ||
            ThemeOldSnapshotImage is null ||
            ThemeNewSnapshotImage is null)
        {
            return;
        }

        Visual newVisual = ElementCompositionPreview.GetElementVisual(ThemeNewSnapshotImage);
        newVisual.Clip = null;
        newVisual.Scale = System.Numerics.Vector3.One;
        ThemeTransitionHost.Visibility = Visibility.Collapsed;
        ThemeOldSnapshotImage.Source = null;
        ThemeNewSnapshotImage.Source = null;
    }

    private void OnToggleSiteClick(object sender, RoutedEventArgs e)
    {
        EhSite next = _services.Settings.GallerySite == EhSite.Ex ? EhSite.E : EhSite.Ex;
        _services.UpdateSettings(s => s.GallerySite = next);
        UpdateSiteSwitchBadge();
        SetStatus($"已切换站点为 {(next == EhSite.Ex ? "ExHentai" : "E-Hentai")}");

        if (SceneContentHost.Content is Views.GalleryListScenePage listPage)
        {
            _ = listPage.RefreshAsync();
        }
    }

    private void OnNavHeaderTapped(object sender, TappedRoutedEventArgs e)
    {
        // Do not open Login if user tapped the site switcher pill or collapse/restore button
        DependencyObject? cur = e.OriginalSource as DependencyObject;
        while (cur is not null)
        {
            if (ReferenceEquals(cur, NavSiteSwitchBtn) || ReferenceEquals(cur, DrawerToggleBtn))
            {
                return;
            }
            cur = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cur);
        }

        _ = NavigateToRootKindAsync(NavigationKind.Login, deselectLeftDrawer: true);
    }

    private void OnRefreshLimitsClick(object sender, RoutedEventArgs e)
    {
        RefreshAccountAndQuota(fetchQuotaIfSignedIn: true);
    }

    private void OnShortcutSearchInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (SceneContentHost.Content is Views.GalleryListScenePage listPage)
        {
            listPage.FocusSearchInput();
        }
        else
        {
            _ = SelectAndNavigateRootAsync(NavigationKind.Home);
        }
    }

    private void OnShortcutClipboardInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OnOpenFromClipboardClick(sender, new RoutedEventArgs());
    }

    private void OnShortcutBackInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        HandleBackAction();
    }

    private void OnShortcutEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (RightDrawerOverlay.Visibility == Visibility.Visible)
        {
            args.Handled = true;
            CloseRightDrawer();
            return;
        }

        if (_sceneStack.Count > 0 || _isReaderFullscreen)
        {
            args.Handled = true;
            HandleBackAction();
        }
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(RootGrid).Properties.IsXButton1Pressed)
        {
            e.Handled = true;
            HandleBackAction();
        }
    }

    private void OnCloseRightDrawerClick(object sender, RoutedEventArgs e) => CloseRightDrawer();
    private void OnRightDrawerScrimPressed(object sender, PointerRoutedEventArgs e) => CloseRightDrawer();

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_servicesDisposed)
        {
            return;
        }

        _servicesDisposed = true;
        _themeTransitionVersion++;
        ResetThemeTransitionOverlay();
        _drawerHoverExpandTimer?.Stop();
        _drawerCollapseTimer?.Stop();
        _idleLockTimer?.Stop();
        DisposeHoverPreviewResources();

        try
        {
            _services.UpdateSettings(s => WindowPlacementHelper.Capture(AppWindow, s));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "保存窗口位置失败");
        }

        try
        {
            _services.GetRequiredService<GalleryBrowseService>().EventPaneReceived -= OnSiteEventPaneReceived;
        }
        catch
        {
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _hoverCts?.Dispose();
        ClearSceneStack();
        ReleaseScenePage(SceneContentHost.Content as Page);
        SceneContentHost.Content = null;
        _currentRightDrawerContent = null;
        RightDrawerContentHost.Content = null;

        try
        {
            _windowManager.CloseAll();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "关闭独立窗口失败");
        }

        TeardownShellIntegration();

        try
        {
            _services.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "关闭时释放服务容器失败");
        }

        _lifetime.Dispose();
    }

    public void SetStatus(string message)
    {
        // Status footer bar removed to keep the reading/browsing canvas clean;
        // retain IShellCoordinator.SetStatus hook cleanly for callers.
        _ = message;
    }

    public void ShowBanner(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        GlobalBannerInfoBar.Title = title;
        GlobalBannerInfoBar.Message = message;
        GlobalBannerInfoBar.Severity = severity;
        GlobalBannerInfoBar.Visibility = Visibility.Visible;
        GlobalBannerInfoBar.IsOpen = true;
    }

    private void OnGlobalBannerClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        sender.Visibility = Visibility.Collapsed;
    }

    private void SetReaderFullscreen(bool fullscreen)
    {
        if (_isReaderFullscreen == fullscreen)
        {
            return;
        }

        _isReaderFullscreen = fullscreen;
        if (fullscreen)
        {
            _drawerHoverExpandTimer?.Stop();
            _drawerCollapseTimer?.Stop();
            _isDrawerHoverExpanded = false;
        }
        Visibility chrome = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        LeftDrawerBorder.Visibility = chrome;

        try
        {
            AppWindow.SetPresenter(fullscreen
                ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
                : Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "切换窗口全屏展示模式失败");
        }
    }

    private void OnShortcutSettingsInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = NavigateToRootKindAsync(NavigationKind.Settings);
    }
}

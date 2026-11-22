using System.Runtime.CompilerServices;
using EhViewer.Desktop.Services.Theming;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Applies the active UI design theme (<see cref="IUiDesignTheme"/>) and color mode
/// (System, Light, Dark, AMOLED Black) across application resources and window chrome.
/// Mutating shared named brushes updates all open pages and templates live.
/// </summary>
public static class MaterialThemeService
{
    internal readonly record struct AppliedPaletteKey(string DesignThemeId, bool IsDark, bool IsBlack);

    internal readonly record struct AppliedTitleBarKey(
        uint Background,
        uint InactiveBackground,
        uint Foreground,
        uint InactiveForeground);

    private sealed class AppliedTitleBarBox
    {
        public AppliedTitleBarKey Key;
    }

    public const int ThemeSystem = 0;
    public const int ThemeLight = 1;
    public const int ThemeDark = 2;
    public const int ThemeBlack = 3;

    private static string? _loadedThemeDictionaryUri = ClassicMaterialDesignTheme.Instance.Metadata.StyleDictionaryUri;
    private static AppliedPaletteKey? _lastAppliedPaletteKey;
    private static string? _resolvedAppIconPath;
    private static bool _appIconPathResolved;
    private static readonly ConditionalWeakTable<Window, string> AppliedWindowIcons = new();
    private static readonly ConditionalWeakTable<Window, AppliedTitleBarBox> AppliedWindowTitleBars = new();

    internal static readonly (string Key, Func<UiThemePalette, uint> Selector)[] PaletteBrushMap =
    [
        ("EhPrimaryBrush", static p => p.Primary),
        ("EhPrimaryDarkBrush", static p => p.PrimaryDark),
        ("EhPrimaryHoverBrush", static p => p.PrimaryHover),
        ("EhPrimaryPressedBrush", static p => p.PrimaryPressed),
        ("SystemControlHighlightAccentBrush", static p => p.Primary),
        ("SystemControlHighlightListAccentLowBrush", static p => p.AccentLow),
        ("SystemControlHighlightListAccentMediumBrush", static p => p.AccentMedium),
        ("SystemControlHighlightListAccentHighBrush", static p => p.Primary),
        ("AccentFillColorDefaultBrush", static p => p.Primary),
        ("AccentFillColorSecondaryBrush", static p => p.PrimaryHover),
        ("AccentFillColorTertiaryBrush", static p => p.PrimaryPressed),

        ("ToggleSwitchFillOn", static p => p.Primary),
        ("ToggleSwitchFillOnPointerOver", static p => p.PrimaryHover),
        ("ToggleSwitchFillOnPressed", static p => p.PrimaryPressed),
        ("ToggleSwitchKnobFillOn", static p => p.SwitchKnobOn),
        ("ToggleSwitchKnobFillOnPointerOver", static p => p.SwitchKnobOn),
        ("ToggleSwitchKnobFillOnPressed", static p => p.SwitchKnobOn),

        ("CheckBoxCheckBackgroundFillChecked", static p => p.Primary),
        ("CheckBoxCheckBackgroundFillCheckedPointerOver", static p => p.PrimaryHover),
        ("CheckBoxCheckBackgroundFillCheckedPressed", static p => p.PrimaryPressed),
        ("RadioButtonOuterEllipseCheckedStroke", static p => p.Primary),
        ("RadioButtonOuterEllipseCheckedStrokePointerOver", static p => p.PrimaryHover),
        ("RadioButtonOuterEllipseCheckedStrokePressed", static p => p.PrimaryPressed),
        ("RadioButtonOuterEllipseCheckedFill", static p => p.Primary),
        ("RadioButtonOuterEllipseCheckedFillPointerOver", static p => p.PrimaryHover),
        ("RadioButtonOuterEllipseCheckedFillPressed", static p => p.PrimaryPressed),

        ("SliderTrackValueFill", static p => p.Primary),
        ("SliderTrackValueFillPointerOver", static p => p.PrimaryHover),
        ("SliderTrackValueFillPressed", static p => p.PrimaryPressed),
        ("SliderThumbBackground", static p => p.Primary),
        ("SliderThumbBackgroundPointerOver", static p => p.PrimaryHover),
        ("SliderThumbBackgroundPressed", static p => p.PrimaryPressed),

        ("ProgressBarForeground", static p => p.Primary),
        ("ProgressBarBackground", static p => p.ProgressBackground),
        ("ProgressRingForegroundThemeBrush", static p => p.Primary),
        ("ListViewItemSelectionIndicatorBrush", static p => p.Primary),
        ("ListViewItemSelectionIndicatorPointerOverBrush", static p => p.PrimaryHover),
        ("ListViewItemSelectionIndicatorPressedBrush", static p => p.PrimaryPressed),
        ("TextControlBorderBrushFocused", static p => p.Primary),

        ("EhPageBackgroundBrush", static p => p.PageBackground),
        ("EhCardSurfaceBrush", static p => p.CardSurface),
        ("EhFloatingSurfaceBrush", static p => p.FloatingSurface),
        ("EhDrawerSurfaceBrush", static p => p.DrawerSurface),
        ("EhDrawerHeaderBrush", static p => p.DrawerHeader),
        ("EhToolbarSurfaceBrush", static p => p.ToolbarSurface),
        ("EhToolbarForegroundBrush", static p => p.ToolbarForeground),
        ("EhToolbarSubtitleForegroundBrush", static p => p.ToolbarSubtitleForeground),
        ("EhToolbarIconHoverBrush", static p => p.ToolbarIconHover),
        ("EhToolbarIconPressedBrush", static p => p.ToolbarIconPressed),
        ("EhToolbarToggleCheckedBrush", static p => p.ToolbarToggleChecked),
        ("EhSectionTitleBrush", static p => p.SectionTitleForeground),
        ("EhSecondaryButtonSurfaceBrush", static p => p.SecondaryButtonBackground),
        ("EhSecondaryButtonBorderBrush", static p => p.SecondaryButtonBorder),
        ("EhSecondaryButtonForegroundBrush", static p => p.SecondaryButtonForeground),
        ("EhSubtleSurfaceBrush", static p => p.SubtleSurface),
        ("EhCardBorderBrush", static p => p.CardBorder),
        ("EhFloatingBorderBrush", static p => p.FloatingBorder),
        ("EhDividerBrush", static p => p.Divider),
        ("EhTextPrimaryBrush", static p => p.TextPrimary),
        ("EhTextSecondaryBrush", static p => p.TextSecondary),
        ("EhNavSelectedBackgroundBrush", static p => p.NavSelectedBackground),
        ("EhHoverSurfaceBrush", static p => p.HoverSurface),
        ("EhPressedSurfaceBrush", static p => p.PressedSurface),
        ("EhTagBackgroundBrush", static p => p.TagBackground),
        ("EhTagBorderBrush", static p => p.TagBorder)
    ];

    /// <summary>Currently active UI design theme preset.</summary>
    public static IUiDesignTheme ActiveDesignTheme { get; private set; } = UiDesignThemeRegistry.Default;

    /// <summary>Geometry and layout metrics of the active UI design theme.</summary>
    public static UiThemeMetrics ActiveMetrics => ActiveDesignTheme.GetMetrics();

    /// <summary>Motion curves and tactile interaction profile of the active UI design theme.</summary>
    public static UiThemeBehavior ActiveBehavior => ActiveDesignTheme.GetBehavior();

    /// <summary>Raised whenever a design theme or color mode palette has been applied.</summary>
    public static event EventHandler? DesignThemeApplied;

    public static bool IsAmoleBlack(int themeSetting) => themeSetting == ThemeBlack;

    public static int NextQuickTheme(int currentThemeSetting) => currentThemeSetting switch
    {
        ThemeLight => ThemeDark,
        ThemeDark => ThemeBlack,
        ThemeBlack => ThemeLight,
        _ => Application.Current.RequestedTheme == ApplicationTheme.Dark ? ThemeLight : ThemeDark
    };

    public static string GetThemeDisplayName(int themeSetting) => themeSetting switch
    {
        ThemeLight => "浅色主题",
        ThemeDark => "深色主题",
        ThemeBlack => "纯黑主题",
        _ => "跟随系统"
    };

    public static void ApplyPalette(int themeSetting, Window? window = null)
        => ApplyPalette(ActiveDesignTheme.Metadata.Id, themeSetting, window);

    internal static bool ShouldApplyGlobalPalette(AppliedPaletteKey? lastApplied, AppliedPaletteKey next)
        => lastApplied is null || lastApplied.Value != next;

    internal static bool ShouldApplyWindowIcon(string? lastAppliedIconPath, string? resolvedIconPath)
        => !string.IsNullOrEmpty(resolvedIconPath) &&
           !string.Equals(lastAppliedIconPath, resolvedIconPath, StringComparison.Ordinal);

    public static void ApplyPalette(string? uiThemeId, int themeSetting, Window? window = null)
    {
        IUiDesignTheme designTheme = UiDesignThemeRegistry.Resolve(uiThemeId);
        ActiveDesignTheme = designTheme;

        bool isDark = WindowAppearance.IsEffectivelyDark(themeSetting);
        bool isBlack = themeSetting == ThemeBlack;
        var nextKey = new AppliedPaletteKey(designTheme.Metadata.Id, isDark, isBlack);
        bool paletteChanged = ShouldApplyGlobalPalette(_lastAppliedPaletteKey, nextKey);
        UiThemePalette palette = designTheme.GetPalette(isDark, isBlack);

        if (paletteChanged)
        {
            UiThemeMetrics metrics = designTheme.GetMetrics();
            ResourceDictionary? res = Application.Current?.Resources;
            if (res is not null)
            {
                TrySyncThemeResourceDictionary(res, designTheme.Metadata.StyleDictionaryUri);
                ApplyPaletteBrushes(res, palette);
                ThemeMetricsBinder.RefreshAll(metrics);
                _lastAppliedPaletteKey = nextKey;
            }
        }

        if (window is not null)
        {
            WindowAppearance.ApplyTheme(window, themeSetting);
            ApplyThemeTitleBar(window, palette);
            ApplyWindowIcon(window);
        }

        if (paletteChanged)
        {
            DesignThemeApplied?.Invoke(null, EventArgs.Empty);
        }
    }

    private static void TrySyncThemeResourceDictionary(ResourceDictionary rootResources, string? targetUri)
    {
        if (string.Equals(_loadedThemeDictionaryUri, targetUri, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            for (int i = rootResources.MergedDictionaries.Count - 1; i >= 0; i--)
            {
                Uri? src = rootResources.MergedDictionaries[i].Source;
                if (src is not null
                    && src.OriginalString.Contains("Styles/Themes/", StringComparison.OrdinalIgnoreCase))
                {
                    rootResources.MergedDictionaries.RemoveAt(i);
                }
            }

            if (!string.IsNullOrWhiteSpace(targetUri))
            {
                rootResources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(targetUri, UriKind.RelativeOrAbsolute)
                });
            }

            _loadedThemeDictionaryUri = targetUri;
        }
        catch
        {
            // Keep existing shared brush dictionary intact if a future optional XAML layer fails to load.
        }
    }

    private static void ApplyPaletteBrushes(ResourceDictionary res, UiThemePalette p)
    {
        List<ResourceDictionary> secondaryDicts = CollectSecondaryDictionaries(
            res,
            static d => d.MergedDictionaries,
            EnumerateThemeDictionaries);

        foreach ((string key, Func<UiThemePalette, uint> selector) in PaletteBrushMap)
        {
            Color color = FromArgb(selector(p));
            SetBrushInDictionary(res, key, color, createIfMissing: true);

            for (int i = 0; i < secondaryDicts.Count; i++)
            {
                SetBrushInDictionary(secondaryDicts[i], key, color, createIfMissing: false);
            }
        }
    }

    private static string? GetResolvedAppIconPath()
    {
        if (_appIconPathResolved)
        {
            return _resolvedAppIconPath;
        }

        string candidate = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _resolvedAppIconPath = File.Exists(candidate) ? candidate : null;
        _appIconPathResolved = true;
        return _resolvedAppIconPath;
    }

    public static void ApplyWindowIcon(Window window)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            string? iconPath = GetResolvedAppIconPath();
            AppliedWindowIcons.TryGetValue(window, out string? appliedPath);
            if (!ShouldApplyWindowIcon(appliedPath, iconPath))
            {
                return;
            }

            window.AppWindow.SetIcon(iconPath!);
            AppliedWindowIcons.AddOrUpdate(window, iconPath!);
        }
        catch
        {
            // Window icon assignment is cosmetic.
        }
    }

    internal static bool ShouldApplyTitleBarColors(AppliedTitleBarKey? lastApplied, AppliedTitleBarKey next)
        => lastApplied is null || lastApplied.Value != next;

    public static void ApplyMaterialTitleBar(Window window, bool isDark, bool isBlack)
        => ApplyThemeTitleBar(window, ActiveDesignTheme.GetPalette(isDark, isBlack));

    public static void ApplyThemeTitleBar(Window window, UiThemePalette palette)
    {
        if (window is null)
        {
            return;
        }

        var nextKey = new AppliedTitleBarKey(
            palette.TitleBarBackground,
            palette.TitleBarInactiveBackground,
            palette.TitleBarForeground,
            palette.TitleBarInactiveForeground);

        if (AppliedWindowTitleBars.TryGetValue(window, out AppliedTitleBarBox? cached) &&
            !ShouldApplyTitleBarColors(cached.Key, nextKey))
        {
            return;
        }

        try
        {
            AppWindow appWindow = window.AppWindow;
            if (!AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            AppWindowTitleBar titleBar = appWindow.TitleBar;
            Color bg = FromArgb(palette.TitleBarBackground);
            Color inactiveBg = FromArgb(palette.TitleBarInactiveBackground);
            Color fg = FromArgb(palette.TitleBarForeground);
            Color inactiveFg = FromArgb(palette.TitleBarInactiveForeground);

            titleBar.BackgroundColor = bg;
            titleBar.InactiveBackgroundColor = inactiveBg;
            titleBar.ForegroundColor = fg;
            titleBar.InactiveForegroundColor = inactiveFg;
            titleBar.ButtonBackgroundColor = bg;
            titleBar.ButtonInactiveBackgroundColor = inactiveBg;
            titleBar.ButtonForegroundColor = fg;
            titleBar.ButtonInactiveForegroundColor = inactiveFg;
            bool isLightTitleBar = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) > 140.0;
            titleBar.ButtonHoverBackgroundColor = isLightTitleBar
                ? Color.FromArgb(20, 0, 0, 0)
                : Color.FromArgb(40, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = isLightTitleBar
                ? Color.FromArgb(36, 0, 0, 0)
                : Color.FromArgb(64, 255, 255, 255);
            titleBar.ButtonHoverForegroundColor = fg;
            titleBar.ButtonPressedForegroundColor = fg;

            AppliedWindowTitleBars.GetOrCreateValue(window).Key = nextKey;
        }
        catch
        {
            // Title bar customization is cosmetic.
        }
    }

    private static Color FromArgb(uint argb) => Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    private static IEnumerable<ResourceDictionary?> EnumerateThemeDictionaries(ResourceDictionary dict)
    {
        if (dict.ThemeDictionaries.Count == 0)
        {
            return [];
        }

        var list = new List<ResourceDictionary?>(dict.ThemeDictionaries.Count);
        foreach (object? dictObj in dict.ThemeDictionaries.Values)
        {
            if (dictObj is ResourceDictionary themeDict)
            {
                list.Add(themeDict);
            }
        }

        return list;
    }

    /// <summary>
    /// Collects and deduplicates secondary dictionaries (root theme dictionaries, merged dictionaries,
    /// and each merged dictionary's theme dictionaries) in a single pass, excluding <paramref name="root"/>.
    /// </summary>
    internal static List<TDict> CollectSecondaryDictionaries<TDict>(
        TDict root,
        Func<TDict, IEnumerable<TDict?>> getMerged,
        Func<TDict, IEnumerable<TDict?>> getThemeDictionaries)
        where TDict : class
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(getMerged);
        ArgumentNullException.ThrowIfNull(getThemeDictionaries);

        var seen = new HashSet<TDict>(ReferenceEqualityComparer.Instance) { root };
        var result = new List<TDict>();

        foreach (TDict? themeDict in getThemeDictionaries(root))
        {
            if (themeDict is not null && seen.Add(themeDict))
            {
                result.Add(themeDict);
            }
        }

        foreach (TDict? merged in getMerged(root))
        {
            if (merged is null)
            {
                continue;
            }

            if (seen.Add(merged))
            {
                result.Add(merged);
            }

            foreach (TDict? mergedTheme in getThemeDictionaries(merged))
            {
                if (mergedTheme is not null && seen.Add(mergedTheme))
                {
                    result.Add(mergedTheme);
                }
            }
        }

        return result;
    }

    internal static bool TryUpdateColor<TBrush>(
        TBrush brush,
        Color targetColor,
        Func<TBrush, Color> getColor,
        Action<TBrush, Color> setColor)
    {
        if (getColor(brush) == targetColor)
        {
            return false;
        }

        setColor(brush, targetColor);
        return true;
    }

    private static void SetBrushInDictionary(
        ResourceDictionary dict,
        string key,
        Color color,
        bool createIfMissing)
    {
        if (dict.TryGetValue(key, out object? existing) && existing is SolidColorBrush brush)
        {
            TryUpdateColor(
                brush,
                color,
                static b => b.Color,
                static (b, c) => b.Color = c);
        }
        else if (createIfMissing)
        {
            dict[key] = new SolidColorBrush(color);
        }
    }
}

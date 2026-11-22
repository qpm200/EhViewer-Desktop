namespace EhViewer.Desktop.Services.Theming;

/// <summary>
/// The classic Android EhViewer Material Teal design theme: colored header banner drawer, crisp
/// rounded cards, teal accent controls, and Light / Dark / AMOLED Black color schemes.
/// </summary>
public sealed class ClassicMaterialDesignTheme : IUiDesignTheme
{
    public const string ThemeId = "classic-material";

    public static ClassicMaterialDesignTheme Instance { get; } = new();

    private static readonly UiThemeMetrics Metrics = new()
    {
        DrawerExpandedWidth = 256.0,
        DrawerCompactWidth = 64.0,
        DrawerHeaderExpandedHeight = 156.0,
        DrawerHeaderCompactHeight = 104.0,
        CardCornerRadius = 6.0,
        FloatingCornerRadius = 8.0,
        NavItemCornerRadius = 8.0,
        ButtonCornerRadius = 6.0,
        SearchBarCornerRadius = 8.0,
        CardBorderThickness = 1.0,
        ShowDrawerHeaderBanner = true
    };

    private static readonly UiThemePalette LightPalette = new()
    {
        Primary = 0xFF009688u,
        PrimaryDark = 0xFF00796Bu,
        PrimaryHover = 0xFF00897Bu,
        PrimaryPressed = 0xFF00796Bu,
        AccentLow = 0x29009688u,
        AccentMedium = 0x44009688u,
        PageBackground = 0xFFF3F5F6u,
        CardSurface = 0xFFFFFFFFu,
        FloatingSurface = 0xFFFFFFFFu,
        DrawerSurface = 0xFFFFFFFFu,
        DrawerHeader = 0xFF00796Bu,
        ToolbarSurface = 0xFF009688u,
        ToolbarForeground = 0xFFFFFFFFu,
        SubtleSurface = 0xFFEFF2F3u,
        CardBorder = 0x22000000u,
        FloatingBorder = 0x2E000000u,
        Divider = 0x22000000u,
        TextPrimary = 0xDE000000u,
        TextSecondary = 0x8A000000u,
        NavSelectedBackground = 0x1C009688u,
        HoverSurface = 0x14009688u,
        PressedSurface = 0x26009688u,
        TagBackground = 0xFFE8F5F3u,
        TagBorder = 0x33009688u,
        ProgressBackground = 0x24000000u,
        TitleBarBackground = 0xFF00796Bu,
        TitleBarInactiveBackground = 0xFF00796Bu,
        TitleBarForeground = 0xFFFFFFFFu,
        TitleBarInactiveForeground = 0xC8FFFFFFu,
        SectionTitleForeground = 0xFF009688u,
        SecondaryButtonBackground = 0x00000000u,
        SecondaryButtonBorder = 0x22000000u,
        SecondaryButtonForeground = 0xDE000000u
    };

    private static readonly UiThemePalette DarkPalette = new()
    {
        Primary = 0xFF26A69Au,
        PrimaryDark = 0xFF00796Bu,
        PrimaryHover = 0xFF4DB6ACu,
        PrimaryPressed = 0xFF00897Bu,
        AccentLow = 0x29009688u,
        AccentMedium = 0x44009688u,
        PageBackground = 0xFF131518u,
        CardSurface = 0xFF1F2329u,
        FloatingSurface = 0xFF2B3038u,
        DrawerSurface = 0xFF191C20u,
        DrawerHeader = 0xFF0F1215u,
        ToolbarSurface = 0xFF111417u,
        ToolbarForeground = 0xFFFFFFFFu,
        SubtleSurface = 0xFF353B45u,
        CardBorder = 0x26FFFFFFu,
        FloatingBorder = 0x45FFFFFFu,
        Divider = 0x26FFFFFFu,
        TextPrimary = 0xF2FFFFFFu,
        TextSecondary = 0xB8FFFFFFu,
        NavSelectedBackground = 0x3826A69Au,
        HoverSurface = 0x2E26A69Au,
        PressedSurface = 0x4726A69Au,
        TagBackground = 0xFF1E3533u,
        TagBorder = 0x5526A69Au,
        ProgressBackground = 0x33FFFFFFu,
        TitleBarBackground = 0xFF111417u,
        TitleBarInactiveBackground = 0xFF111417u,
        TitleBarForeground = 0xFFFFFFFFu,
        TitleBarInactiveForeground = 0xC8FFFFFFu,
        SectionTitleForeground = 0xFF26A69Au,
        SecondaryButtonBackground = 0x00000000u,
        SecondaryButtonBorder = 0x26FFFFFFu,
        SecondaryButtonForeground = 0xF2FFFFFFu
    };

    private static readonly UiThemePalette BlackPalette = new()
    {
        Primary = 0xFF26A69Au,
        PrimaryDark = 0xFF00796Bu,
        PrimaryHover = 0xFF4DB6ACu,
        PrimaryPressed = 0xFF00897Bu,
        AccentLow = 0x29009688u,
        AccentMedium = 0x44009688u,
        PageBackground = 0xFF000000u,
        CardSurface = 0xFF14171Au,
        FloatingSurface = 0xFF21262Du,
        DrawerSurface = 0xFF0C0E10u,
        DrawerHeader = 0xFF080A0Cu,
        ToolbarSurface = 0xFF0A0C0Eu,
        ToolbarForeground = 0xFFFFFFFFu,
        SubtleSurface = 0xFF2C323Bu,
        CardBorder = 0x2EFFFFFFu,
        FloatingBorder = 0x4AFFFFFFu,
        Divider = 0x2AFFFFFFu,
        TextPrimary = 0xF5FFFFFFu,
        TextSecondary = 0xB8FFFFFFu,
        NavSelectedBackground = 0x3A26A69Au,
        HoverSurface = 0x3026A69Au,
        PressedSurface = 0x4A26A69Au,
        TagBackground = 0xFF1A2F2Du,
        TagBorder = 0x5526A69Au,
        ProgressBackground = 0x33FFFFFFu,
        TitleBarBackground = 0xFF000000u,
        TitleBarInactiveBackground = 0xFF000000u,
        TitleBarForeground = 0xFFFFFFFFu,
        TitleBarInactiveForeground = 0xC8FFFFFFu,
        SectionTitleForeground = 0xFF26A69Au,
        SecondaryButtonBackground = 0x00000000u,
        SecondaryButtonBorder = 0x2EFFFFFFu,
        SecondaryButtonForeground = 0xF5FFFFFFu
    };

    private static readonly UiThemeBehavior Behavior = new()
    {
        MotionPreset = ThemeMotionPreset.MaterialCrisp,
        SceneTransition = SceneTransitionStyle.MaterialFadeSlide,
        CardFeedback = CardFeedbackStyle.ElevationBorder,
        ThemeSwitchEffect = ThemeSwitchEffectStyle.MaterialCircularClip,
        SceneTransitionDurationMs = 170,
        ScenePushOffsetPx = 16f,
        SceneRootOffsetPx = 12f,
        OverlayPanelDurationMs = 160,
        OverlayScaleFrom = 1.0f,
        SliderThumbDurationMs = 160,
        SliderThumbActiveScale = 1.0f,
        SliderThumbUsesSpringOvershoot = false,
        ThemeRevealDurationMs = 420,
        DrawerSlideDurationMs = 150,
        DrawerHoverExpandDelayMs = 120,
        DrawerHoverCollapseDelayMs = 160,
        CardHoverScale = 1.0f,
        CardPressScale = 1.0f,
        CardFeedbackDurationMs = 120,
        EasingControlPoint1X = 0.20f,
        EasingControlPoint1Y = 0.0f,
        EasingControlPoint2X = 0.0f,
        EasingControlPoint2Y = 1.0f
    };

    private ClassicMaterialDesignTheme()
    {
    }

    public UiThemeMetadata Metadata { get; } = new()
    {
        Id = ThemeId,
        DisplayName = "经典 Material",
        BadgeText = "经典设计",
        Description = "Android EhViewer 经典 Teal 配色、顶部抽屉横幅、利落纵向淡入切换与标准边框悬浮反馈",
        HeaderPreviewArgb = 0xFF00796Bu,
        LightAccentArgb = 0xFF009688u,
        LightSurfaceArgb = 0xFFF3F5F6u,
        LightCardArgb = 0xFFFFFFFFu,
        DarkAccentArgb = 0xFF26A69Au,
        DarkSurfaceArgb = 0xFF131518u,
        DarkCardArgb = 0xFF1F2329u,
        StyleDictionaryUri = "ms-appx:///Styles/Themes/ClassicMaterialTheme.xaml"
    };

    public UiThemeMetrics GetMetrics() => Metrics;

    public UiThemeBehavior GetBehavior() => Behavior;

    public UiThemePalette GetPalette(bool isDark, bool isBlack) => isBlack
        ? BlackPalette
        : isDark
            ? DarkPalette
            : LightPalette;
}

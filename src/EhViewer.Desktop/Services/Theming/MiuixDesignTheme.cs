namespace EhViewer.Desktop.Services.Theming;

/// <summary>
/// Xiaomi HyperOS / MIUIX design theme inspired by compose-miuix-ui/miuix:
/// crisp HyperOS Blue accents, high-contrast neutral surface hierarchy,
/// bannerless navigation rail with 16px capsule selection, and 16px rounded cards.
/// </summary>
public sealed class MiuixDesignTheme : IUiDesignTheme
{
    public const string ThemeId = "miuix";

    public static MiuixDesignTheme Instance { get; } = new();

    private static readonly UiThemeMetrics Metrics = new()
    {
        DrawerExpandedWidth = 240.0,
        DrawerCompactWidth = 76.0,
        DrawerHeaderExpandedHeight = 118.0,
        DrawerHeaderCompactHeight = 98.0,
        CardCornerRadius = 16.0,
        FloatingCornerRadius = 16.0,
        NavItemCornerRadius = 16.0,
        ButtonCornerRadius = 16.0,
        SearchBarCornerRadius = 22.0,
        CardBorderThickness = 1.0,
        ShowDrawerHeaderBanner = false
    };

    private static readonly UiThemePalette LightPalette = new()
    {
        Primary = 0xFF3482FFu,
        PrimaryDark = 0xFF277AF7u,
        PrimaryHover = 0xFF5D9BFFu,
        PrimaryPressed = 0xFF277AF7u,
        AccentLow = 0x263482FFu,
        AccentMedium = 0x423482FFu,
        PageBackground = 0xFFF7F7F7u,
        CardSurface = 0xFFFFFFFFu,
        FloatingSurface = 0xFFFFFFFFu,
        DrawerSurface = 0xFFF7F7F7u,
        DrawerHeader = 0xFFF7F7F7u,
        ToolbarSurface = 0xFFF7F7F7u,
        ToolbarForeground = 0xFF000000u,
        ToolbarSubtitleForeground = 0x99000000u,
        ToolbarIconHover = 0x12000000u,
        ToolbarIconPressed = 0x20000000u,
        ToolbarToggleChecked = 0x1E3482FFu,
        SubtleSurface = 0xFFF0F0F0u,
        CardBorder = 0x12000000u,
        FloatingBorder = 0x1A000000u,
        Divider = 0xFFE0E0E0u,
        TextPrimary = 0xFF000000u,
        TextSecondary = 0x99000000u,
        NavSelectedBackground = 0xFFE8E8E8u,
        HoverSurface = 0x0F000000u,
        PressedSurface = 0x1A000000u,
        TagBackground = 0xFFEAF2FFu,
        TagBorder = 0x443482FFu,
        ProgressBackground = 0x0F000000u,
        TitleBarBackground = 0xFFF7F7F7u,
        TitleBarInactiveBackground = 0xFFF7F7F7u,
        TitleBarForeground = 0xFF000000u,
        TitleBarInactiveForeground = 0x99000000u,
        SectionTitleForeground = 0xFF8C93B0u,
        SecondaryButtonBackground = 0xFFF0F0F0u,
        SecondaryButtonBorder = 0x00000000u,
        SecondaryButtonForeground = 0xFF303030u,
        SwitchTrackOff = 0xFFE6E6E6u
    };

    private static readonly UiThemePalette DarkPalette = new()
    {
        Primary = 0xFF277AF7u,
        PrimaryDark = 0xFF0073DDu,
        PrimaryHover = 0xFF4788FFu,
        PrimaryPressed = 0xFF0073DDu,
        AccentLow = 0x2E277AF7u,
        AccentMedium = 0x48277AF7u,
        PageBackground = 0xFF141416u,
        CardSurface = 0xFF242424u,
        FloatingSurface = 0xFF2D2D2Du,
        DrawerSurface = 0xFF161618u,
        DrawerHeader = 0xFF161618u,
        ToolbarSurface = 0xFF141416u,
        ToolbarForeground = 0xFFF2F2F2u,
        ToolbarSubtitleForeground = 0x80FFFFFFu,
        ToolbarIconHover = 0x1EFFFFFFu,
        ToolbarIconPressed = 0x30FFFFFFu,
        ToolbarToggleChecked = 0x3D277AF7u,
        SubtleSurface = 0xFF333336u,
        CardBorder = 0x24FFFFFFu,
        FloatingBorder = 0x38FFFFFFu,
        Divider = 0xFF393939u,
        TextPrimary = 0xFFF2F2F2u,
        TextSecondary = 0x80FFFFFFu,
        NavSelectedBackground = 0xFF2D2D2Du,
        HoverSurface = 0x18FFFFFFu,
        PressedSurface = 0x28FFFFFFu,
        TagBackground = 0xFF2B3B54u,
        TagBorder = 0x664788FFu,
        ProgressBackground = 0x26FFFFFFu,
        TitleBarBackground = 0xFF141416u,
        TitleBarInactiveBackground = 0xFF141416u,
        TitleBarForeground = 0xFFF2F2F2u,
        TitleBarInactiveForeground = 0x99FFFFFFu,
        SectionTitleForeground = 0xFF787E96u,
        SecondaryButtonBackground = 0xFF434343u,
        SecondaryButtonBorder = 0x00000000u,
        SecondaryButtonForeground = 0xFFD9D9D9u,
        SwitchTrackOff = 0xFF505050u
    };

    private static readonly UiThemePalette BlackPalette = new()
    {
        Primary = 0xFF277AF7u,
        PrimaryDark = 0xFF0073DDu,
        PrimaryHover = 0xFF4788FFu,
        PrimaryPressed = 0xFF0073DDu,
        AccentLow = 0x30277AF7u,
        AccentMedium = 0x4A277AF7u,
        PageBackground = 0xFF000000u,
        CardSurface = 0xFF1E1E20u,
        FloatingSurface = 0xFF28282Bu,
        DrawerSurface = 0xFF000000u,
        DrawerHeader = 0xFF000000u,
        ToolbarSurface = 0xFF000000u,
        ToolbarForeground = 0xFFF2F2F2u,
        ToolbarSubtitleForeground = 0x80FFFFFFu,
        ToolbarIconHover = 0x22FFFFFFu,
        ToolbarIconPressed = 0x34FFFFFFu,
        ToolbarToggleChecked = 0x3D277AF7u,
        SubtleSurface = 0xFF2D2D30u,
        CardBorder = 0x2BFFFFFFu,
        FloatingBorder = 0x40FFFFFFu,
        Divider = 0xFF393939u,
        TextPrimary = 0xFFF5F5F5u,
        TextSecondary = 0x88FFFFFFu,
        NavSelectedBackground = 0xFF242424u,
        HoverSurface = 0x1AFFFFFFu,
        PressedSurface = 0x2BFFFFFFu,
        TagBackground = 0xFF2B3B54u,
        TagBorder = 0x664788FFu,
        ProgressBackground = 0x26FFFFFFu,
        TitleBarBackground = 0xFF000000u,
        TitleBarInactiveBackground = 0xFF000000u,
        TitleBarForeground = 0xFFF2F2F2u,
        TitleBarInactiveForeground = 0x99FFFFFFu,
        SectionTitleForeground = 0xFF787E96u,
        SecondaryButtonBackground = 0xFF3A3A3Du,
        SecondaryButtonBorder = 0x00000000u,
        SecondaryButtonForeground = 0xFFE6E6E6u,
        SwitchTrackOff = 0xFF4A4A4Du
    };

    private static readonly UiThemeBehavior Behavior = new()
    {
        MotionPreset = ThemeMotionPreset.MiuixFolmeSpring,
        SceneTransition = SceneTransitionStyle.HyperPushSpring,
        CardFeedback = CardFeedbackStyle.SquircleSink,
        ThemeSwitchEffect = ThemeSwitchEffectStyle.HyperRadialSoft,
        SceneTransitionDurationMs = 320,
        ScenePushOffsetPx = 52f,
        SceneRootOffsetPx = 14f,
        OverlayPanelDurationMs = 280,
        OverlayScaleFrom = 0.96f,
        SliderThumbDurationMs = 300,
        SliderThumbActiveScale = 1.08f,
        SliderThumbUsesSpringOvershoot = true,
        ThemeRevealDurationMs = 520,
        DrawerSlideDurationMs = 290,
        DrawerHoverExpandDelayMs = 45,
        DrawerHoverCollapseDelayMs = 280,
        CardHoverScale = 1.008f,
        CardPressScale = 0.972f,
        CardFeedbackDurationMs = 240,
        EasingControlPoint1X = 0.16f,
        EasingControlPoint1Y = 1.0f,
        EasingControlPoint2X = 0.30f,
        EasingControlPoint2Y = 1.0f
    };

    private MiuixDesignTheme()
    {
    }

    public UiThemeMetadata Metadata { get; } = new()
    {
        Id = ThemeId,
        DisplayName = "MIUIX (HyperOS)",
        BadgeText = "HyperOS 风格",
        Description = "HyperOS / MIUIX 风格：16px 连续圆角、无横幅胶囊侧栏、弹簧按压下沉(Sink)与横向推进动效",
        HeaderPreviewArgb = 0xFFF7F7F7u,
        LightAccentArgb = 0xFF3482FFu,
        LightSurfaceArgb = 0xFFF7F7F7u,
        LightCardArgb = 0xFFFFFFFFu,
        DarkAccentArgb = 0xFF277AF7u,
        DarkSurfaceArgb = 0xFF141416u,
        DarkCardArgb = 0xFF242424u,
        StyleDictionaryUri = "ms-appx:///Styles/Themes/MiuixTheme.xaml"
    };

    public UiThemeMetrics GetMetrics() => Metrics;

    public UiThemeBehavior GetBehavior() => Behavior;

    public UiThemePalette GetPalette(bool isDark, bool isBlack) => isBlack
        ? BlackPalette
        : isDark
            ? DarkPalette
            : LightPalette;
}

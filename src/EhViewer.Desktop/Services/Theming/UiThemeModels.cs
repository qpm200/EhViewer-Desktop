namespace EhViewer.Desktop.Services.Theming;

/// <summary>
/// Semantic color tokens for one color variant (Light / Dark / Black) of a <see cref="IUiDesignTheme"/>.
/// Every UI design theme maps its visual palette onto this uniform token set so pages and controls
/// update live without page reloads.
/// </summary>
public sealed record UiThemePalette
{
    public required uint Primary { get; init; }
    public required uint PrimaryDark { get; init; }
    public required uint PrimaryHover { get; init; }
    public required uint PrimaryPressed { get; init; }
    public required uint AccentLow { get; init; }
    public required uint AccentMedium { get; init; }
    public required uint PageBackground { get; init; }
    public required uint CardSurface { get; init; }
    public required uint FloatingSurface { get; init; }
    public required uint DrawerSurface { get; init; }
    public required uint DrawerHeader { get; init; }
    public required uint ToolbarSurface { get; init; }
    public required uint ToolbarForeground { get; init; }
    public required uint SubtleSurface { get; init; }
    public required uint CardBorder { get; init; }
    public required uint FloatingBorder { get; init; }
    public required uint Divider { get; init; }
    public required uint TextPrimary { get; init; }
    public required uint TextSecondary { get; init; }
    public required uint NavSelectedBackground { get; init; }
    public required uint HoverSurface { get; init; }
    public required uint PressedSurface { get; init; }
    public required uint TagBackground { get; init; }
    public required uint TagBorder { get; init; }
    public required uint ProgressBackground { get; init; }
    public required uint TitleBarBackground { get; init; }
    public required uint TitleBarInactiveBackground { get; init; }
    public required uint TitleBarForeground { get; init; }
    public required uint TitleBarInactiveForeground { get; init; }
    public uint SwitchKnobOn { get; init; } = 0xFFFFFFFFu;
    public uint ToolbarSubtitleForeground { get; init; } = 0xD8FFFFFFu;
    public uint ToolbarIconHover { get; init; } = 0x28FFFFFFu;
    public uint ToolbarIconPressed { get; init; } = 0x40FFFFFFu;
    public uint ToolbarToggleChecked { get; init; } = 0x3FFFFFFFu;
    public uint SectionTitleForeground { get; init; } = 0xFF009688u;
    public uint SecondaryButtonBackground { get; init; } = 0x00000000u;
    public uint SecondaryButtonBorder { get; init; } = 0x22000000u;
    public uint SecondaryButtonForeground { get; init; } = 0xDE000000u;
    public uint SwitchTrackOff { get; init; } = 0x00000000u;
}

/// <summary>
/// Structural and geometry metrics defined by a UI design theme (drawer width, header height,
/// card/floating corner radii, border thickness). Future themes can alter shell geometry without
/// rewriting navigation or gallery business logic.
/// </summary>
public sealed record UiThemeMetrics
{
    public double DrawerExpandedWidth { get; init; } = 256.0;
    public double DrawerCompactWidth { get; init; } = 64.0;
    public double DrawerHeaderExpandedHeight { get; init; } = 156.0;
    public double DrawerHeaderCompactHeight { get; init; } = 104.0;
    public double CardCornerRadius { get; init; } = 6.0;
    public double FloatingCornerRadius { get; init; } = 8.0;
    public double NavItemCornerRadius { get; init; } = 8.0;
    public double ButtonCornerRadius { get; init; } = 6.0;
    public double SearchBarCornerRadius { get; init; } = 8.0;
    public double CardBorderThickness { get; init; } = 1.0;
    public bool ShowDrawerHeaderBanner { get; init; } = true;
}

public enum ThemeMotionPreset
{
    MaterialCrisp = 0,
    MiuixFolmeSpring = 1
}

public enum SceneTransitionStyle
{
    MaterialFadeSlide = 0,
    HyperPushSpring = 1
}

public enum CardFeedbackStyle
{
    ElevationBorder = 0,
    SquircleSink = 1
}

public enum ThemeSwitchEffectStyle
{
    MaterialCircularClip = 0,
    HyperRadialSoft = 1
}

/// <summary>
/// Motion curves, interaction timings, and tactile feedback parameters owned by a UI design theme.
/// </summary>
public sealed record UiThemeBehavior
{
    public ThemeMotionPreset MotionPreset { get; init; } = ThemeMotionPreset.MaterialCrisp;
    public SceneTransitionStyle SceneTransition { get; init; } = SceneTransitionStyle.MaterialFadeSlide;
    public CardFeedbackStyle CardFeedback { get; init; } = CardFeedbackStyle.ElevationBorder;
    public ThemeSwitchEffectStyle ThemeSwitchEffect { get; init; } = ThemeSwitchEffectStyle.MaterialCircularClip;
    public int SceneTransitionDurationMs { get; init; } = 170;
    public float ScenePushOffsetPx { get; init; } = 14f;
    public float SceneRootOffsetPx { get; init; } = 12f;
    public int OverlayPanelDurationMs { get; init; } = 160;
    public float OverlayScaleFrom { get; init; } = 1.0f;
    public int SliderThumbDurationMs { get; init; } = 160;
    public float SliderThumbActiveScale { get; init; } = 1.0f;
    public bool SliderThumbUsesSpringOvershoot { get; init; }
    public int ThemeRevealDurationMs { get; init; } = 420;
    public int DrawerSlideDurationMs { get; init; } = 150;
    public int DrawerHoverExpandDelayMs { get; init; } = 120;
    public int DrawerHoverCollapseDelayMs { get; init; } = 160;
    public float CardHoverScale { get; init; } = 1.0f;
    public float CardPressScale { get; init; } = 1.0f;
    public int CardFeedbackDurationMs { get; init; } = 120;
    public float EasingControlPoint1X { get; init; } = 0.20f;
    public float EasingControlPoint1Y { get; init; } = 0.0f;
    public float EasingControlPoint2X { get; init; } = 0.0f;
    public float EasingControlPoint2Y { get; init; } = 1.0f;
}

/// <summary>
/// Descriptive metadata and swatch preview colors for displaying a UI design theme in Settings.
/// </summary>
public sealed record UiThemeMetadata
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string BadgeText { get; init; }
    public required string Description { get; init; }
    public required uint HeaderPreviewArgb { get; init; }
    public required uint LightAccentArgb { get; init; }
    public required uint LightSurfaceArgb { get; init; }
    public required uint LightCardArgb { get; init; }
    public required uint DarkAccentArgb { get; init; }
    public required uint DarkSurfaceArgb { get; init; }
    public required uint DarkCardArgb { get; init; }
    public string? StyleDictionaryUri { get; init; }
}

/// <summary>
/// Contract for a pluggable desktop UI design theme. Each implementation encapsulates a distinct
/// visual design system (metrics, Light/Dark/Black color palettes, motion/interaction behavior, and optional XAML style layer).
/// </summary>
public interface IUiDesignTheme
{
    UiThemeMetadata Metadata { get; }
    UiThemeMetrics GetMetrics();
    UiThemeBehavior GetBehavior();
    UiThemePalette GetPalette(bool isDark, bool isBlack);
}

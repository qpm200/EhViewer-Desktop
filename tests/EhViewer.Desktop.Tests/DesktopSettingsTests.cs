using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Network;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Theming;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class DesktopSettingsTests : DesktopTestBase
{
    [Fact]
    public void DesktopSettingsStore_RoundTripsExtendedSettingsAndTabs()
    {
        string tempRoot = CreateTempDirectory("eh-settings");
        AppPaths paths = AppPaths.CreateAt(tempRoot);
        try
        {
            var store = new DesktopSettingsStore(paths.SettingsFilePath);
            var settings = new DesktopSettings
            {
                PreloadCount = 5,
                DualPageMode = true,
                ContinuousScroll = true,
                ForceSrgb = true,
                KeepScreenOn = true,
                CollapseNavigationDrawer = true,
                AutoExpandSidebarOnHover = true,
                CustomDownloadDirectory = Path.Combine(tempRoot, "custom-dl"),
                ExcludedLanguages = "0x1025",
                ExcludedNamespaces = EhConfig.NamespacesFemale,
                SavedWindowX = 120,
                SavedWindowY = 80,
                SavedWindowWidth = 1440,
                SavedWindowHeight = 900,
                OpenTabs = [new PersistedTabState { Title = "热门", Kind = "WhatsHot", Mode = ListUrlBuilder.ModeWhatsHot }]
            };
            store.Save(settings);

            DesktopSettings reloaded = store.Load();
            Assert.Equal(5, reloaded.PreloadCount);
            Assert.True(reloaded.DualPageMode);
            Assert.True(reloaded.ContinuousScroll);
            Assert.True(reloaded.ForceSrgb);
            Assert.True(reloaded.KeepScreenOn);
            Assert.True(reloaded.CollapseNavigationDrawer);
            Assert.True(reloaded.AutoExpandSidebarOnHover);
            Assert.Equal("0x1025", reloaded.ExcludedLanguages);
            Assert.Equal(EhConfig.NamespacesFemale, reloaded.ExcludedNamespaces);
            Assert.Equal(1440, reloaded.SavedWindowWidth);
            Assert.Single(reloaded.OpenTabs);
            Assert.Equal(paths.GetEffectiveDownloadDirectory(reloaded), Path.GetFullPath(Path.Combine(tempRoot, "custom-dl")));
        }
        finally
        {
            
        }
    }

    [Fact]
    public void UConfigSyncService_AppliesAndPullsSettings()
    {
        string tempRoot = CreateTempDirectory("eh-settings");
        AppPaths paths = AppPaths.CreateAt(tempRoot);
        try
        {
            using AppServices services = AppServices.Create(paths);
            UConfigSyncService sync = services.GetRequiredService<UConfigSyncService>();
            var settings = new DesktopSettings
            {
                ExcludedLanguages = "10x1034",
                ExcludedNamespaces = EhConfig.NamespacesLanguage,
                FavoritesSortOrder = EhConfig.OrderByPubTime,
                CommentsSortOrder = EhConfig.CommentsSortHighestScoreFirst,
                EnablePopularOnHome = false,
                ListPageSize = 100
            };

            sync.ApplySettings(settings);

            var copy = new DesktopSettings();
            Assert.True(sync.PullFromCookieStore(copy));
            Assert.Equal("10x1034", copy.ExcludedLanguages);
            Assert.Equal(EhConfig.NamespacesLanguage, copy.ExcludedNamespaces);
            Assert.Equal(EhConfig.OrderByPubTime, copy.FavoritesSortOrder);
            Assert.Equal(EhConfig.CommentsSortHighestScoreFirst, copy.CommentsSortOrder);
            Assert.False(copy.EnablePopularOnHome);
        }
        finally
        {
            
        }
    }

    [Fact]
    public void SettingsViewModel_AutoSavesNavigationDrawerPreferences()
    {
        string tempRoot = CreateTempDirectory("eh-settings");
        AppPaths paths = AppPaths.CreateAt(tempRoot);
        try
        {
            using AppServices services = AppServices.Create(paths);
            var vm = new EhViewer.Desktop.ViewModels.SettingsViewModel(services)
            {
                AutoSaveOnChange = true
            };

            int appliedCount = 0;
            vm.SettingsApplied += (_, _) => appliedCount++;

            vm.CollapseNavigationDrawer = true;
            vm.AutoExpandSidebarOnHover = true;

            Assert.True(services.Settings.CollapseNavigationDrawer);
            Assert.True(services.Settings.AutoExpandSidebarOnHover);
            Assert.Equal(2, appliedCount);

            vm.SyncNavigationDrawerCollapsed(false);
            Assert.False(vm.CollapseNavigationDrawer);
            Assert.Equal(2, appliedCount);
        }
        finally
        {
            
        }
    }

    [Fact]
    public void UiDesignThemeRegistry_ProvidesClassicMaterialDefaultAndSupportsExtensionThemes()
    {
        IUiDesignTheme classic = UiDesignThemeRegistry.Resolve(null);
        Assert.Equal(ClassicMaterialDesignTheme.ThemeId, classic.Metadata.Id);
        Assert.Equal(classic, UiDesignThemeRegistry.Resolve("nonexistent-theme"));

        UiThemeMetrics metrics = classic.GetMetrics();
        Assert.Equal(256.0, metrics.DrawerExpandedWidth);
        Assert.Equal(64.0, metrics.DrawerCompactWidth);
        Assert.Equal(156.0, metrics.DrawerHeaderExpandedHeight);
        Assert.Equal(104.0, metrics.DrawerHeaderCompactHeight);

        Assert.Equal(0xFF009688u, classic.GetPalette(isDark: false, isBlack: false).Primary);
        Assert.Equal(0xFF26A69Au, classic.GetPalette(isDark: true, isBlack: false).Primary);
        Assert.Equal(0xFF000000u, classic.GetPalette(isDark: true, isBlack: true).PageBackground);

        var custom = new TestDesignTheme();
        try
        {
            UiDesignThemeRegistry.Register(custom);
            IUiDesignTheme resolved = UiDesignThemeRegistry.Resolve("TEST-CUSTOM");
            Assert.Same(custom, resolved);
            Assert.Contains(UiDesignThemeRegistry.All, t => t.Metadata.Id == "test-custom");
        }
        finally
        {
            UiDesignThemeRegistry.Unregister("test-custom");
        }
    }

    [Fact]
    public void SettingsViewModel_PersistsUiThemeSelectionAndFallsBackWhenMissing()
    {
        string tempRoot = CreateTempDirectory("eh-settings");
        AppPaths paths = AppPaths.CreateAt(tempRoot);
        try
        {
            Directory.CreateDirectory(paths.RootDirectory);
            File.WriteAllText(paths.SettingsFilePath, """{ "theme": 2 }""");

            using var services = AppServices.Create(paths);
            Assert.Equal(ClassicMaterialDesignTheme.ThemeId, services.Settings.UiTheme);

            var vm = new EhViewer.Desktop.ViewModels.SettingsViewModel(services);
            Assert.Contains(vm.AvailableUiThemes, t => t.Id == ClassicMaterialDesignTheme.ThemeId);
            Assert.Equal(ClassicMaterialDesignTheme.ThemeId, vm.UiThemeId);
            Assert.Equal(0, vm.UiThemeIndex);

            vm.AutoSaveOnChange = true;
            int appliedCount = 0;
            vm.SettingsApplied += (_, _) => appliedCount++;

            var custom = new TestDesignTheme();
            UiDesignThemeRegistry.Register(custom);
            try
            {
                var vmWithCustom = new EhViewer.Desktop.ViewModels.SettingsViewModel(services)
                {
                    AutoSaveOnChange = true
                };
                int customApplied = 0;
                vmWithCustom.SettingsApplied += (_, _) => customApplied++;

                vmWithCustom.UiThemeId = "test-custom";
                Assert.Equal(1, customApplied);
                Assert.Equal("test-custom", services.Settings.UiTheme);
            }
            finally
            {
                UiDesignThemeRegistry.Unregister("test-custom");
            }

            Assert.Equal(0, appliedCount);
        }
        finally
        {
            
        }
    }

    [Fact]
    public void MiuixDesignTheme_IsRegisteredWithExpectedPalettesAndMetrics()
    {
        IUiDesignTheme resolved = UiDesignThemeRegistry.Resolve(MiuixDesignTheme.ThemeId);
        Assert.Equal(MiuixDesignTheme.ThemeId, resolved.Metadata.Id);
        Assert.Contains(UiDesignThemeRegistry.All, t => t.Metadata.Id == ClassicMaterialDesignTheme.ThemeId);
        Assert.Contains(UiDesignThemeRegistry.All, t => t.Metadata.Id == MiuixDesignTheme.ThemeId);
        Assert.Equal(ClassicMaterialDesignTheme.ThemeId, UiDesignThemeRegistry.DefaultThemeId);

        UiThemeMetrics metrics = resolved.GetMetrics();
        Assert.False(metrics.ShowDrawerHeaderBanner);
        Assert.Equal(16.0, metrics.CardCornerRadius);
        Assert.Equal(16.0, metrics.NavItemCornerRadius);
        Assert.Equal(16.0, metrics.ButtonCornerRadius);
        Assert.Equal(22.0, metrics.SearchBarCornerRadius);
        Assert.Equal(240.0, metrics.DrawerExpandedWidth);
        Assert.Equal(76.0, metrics.DrawerCompactWidth);

        UiThemePalette light = resolved.GetPalette(isDark: false, isBlack: false);
        Assert.Equal(0xFF3482FFu, light.Primary);
        Assert.Equal(0xFFF7F7F7u, light.PageBackground);
        Assert.Equal(0xFFFFFFFFu, light.CardSurface);
        Assert.Equal(0xFF000000u, light.ToolbarForeground);

        UiThemePalette dark = resolved.GetPalette(isDark: true, isBlack: false);
        Assert.Equal(0xFF277AF7u, dark.Primary);
        Assert.Equal(0xFF141416u, dark.PageBackground);
        Assert.Equal(0xFF242424u, dark.CardSurface);

        UiThemePalette black = resolved.GetPalette(isDark: true, isBlack: true);
        Assert.Equal(0xFF000000u, black.PageBackground);
        Assert.Equal(0xFF1E1E20u, black.CardSurface);

        var innerLeft = ThemeMetricsBinder.ResolveCornerRadius(ThemeMetricsBinder.RoleCardInnerLeft, metrics);
        Assert.Equal(15.0, innerLeft.TopLeft);
        Assert.Equal(0.0, innerLeft.TopRight);
        Assert.Equal(0.0, innerLeft.BottomRight);
        Assert.Equal(15.0, innerLeft.BottomLeft);

        var searchRadius = ThemeMetricsBinder.ResolveCornerRadius(ThemeMetricsBinder.RoleSearchBar, metrics);
        Assert.Equal(22.0, searchRadius.TopLeft);
    }

    [Fact]
    public void SettingsViewModel_PersistsMiuixThemeRoundTrip()
    {
        string tempRoot = CreateTempDirectory("eh-settings");
        AppPaths paths = AppPaths.CreateAt(tempRoot);
        try
        {
            using (var services = AppServices.Create(paths))
            {
                var vm = new EhViewer.Desktop.ViewModels.SettingsViewModel(services)
                {
                    AutoSaveOnChange = true
                };
                int miuixIndex = UiDesignThemeRegistry.IndexOf(MiuixDesignTheme.ThemeId);
                Assert.True(miuixIndex >= 1);

                vm.UiThemeIndex = miuixIndex;
                Assert.Equal(MiuixDesignTheme.ThemeId, vm.UiThemeId);
                Assert.Equal(MiuixDesignTheme.ThemeId, services.Settings.UiTheme);
            }

            using (var reloaded = AppServices.Create(paths))
            {
                Assert.Equal(MiuixDesignTheme.ThemeId, reloaded.Settings.UiTheme);
            }
        }
        finally
        {
            
        }
    }

    [Fact]
    public void DesignThemes_ProvideDistinctMotionAndInteractionBehaviors()
    {
        UiThemeBehavior classic = ClassicMaterialDesignTheme.Instance.GetBehavior();
        UiThemeBehavior miuix = MiuixDesignTheme.Instance.GetBehavior();

        Assert.Equal(ThemeMotionPreset.MaterialCrisp, classic.MotionPreset);
        Assert.Equal(SceneTransitionStyle.MaterialFadeSlide, classic.SceneTransition);
        Assert.Equal(CardFeedbackStyle.ElevationBorder, classic.CardFeedback);
        Assert.Equal(ThemeSwitchEffectStyle.MaterialCircularClip, classic.ThemeSwitchEffect);
        Assert.False(classic.SliderThumbUsesSpringOvershoot);
        Assert.Equal(1.0f, classic.CardPressScale);
        Assert.Equal(1.0f, classic.OverlayScaleFrom);

        Assert.Equal(ThemeMotionPreset.MiuixFolmeSpring, miuix.MotionPreset);
        Assert.Equal(SceneTransitionStyle.HyperPushSpring, miuix.SceneTransition);
        Assert.Equal(CardFeedbackStyle.SquircleSink, miuix.CardFeedback);
        Assert.Equal(ThemeSwitchEffectStyle.HyperRadialSoft, miuix.ThemeSwitchEffect);
        Assert.True(miuix.SliderThumbUsesSpringOvershoot);
        Assert.True(miuix.SliderThumbActiveScale > 1.0f);
        Assert.True(miuix.CardPressScale < 1.0f);
        Assert.True(miuix.CardHoverScale > 1.0f);
        Assert.True(miuix.OverlayScaleFrom < 1.0f);
        Assert.True(miuix.DrawerHoverExpandDelayMs < classic.DrawerHoverExpandDelayMs);
        Assert.True(miuix.DrawerHoverCollapseDelayMs > classic.DrawerHoverCollapseDelayMs);
        Assert.True(miuix.SceneTransitionDurationMs > classic.SceneTransitionDurationMs);
        Assert.NotEqual(classic.EasingControlPoint1X, miuix.EasingControlPoint1X);
    }

    [Fact]
    public void ActiveBehavior_MatchesResolvedDesignTheme()
    {
        IUiDesignTheme classicTheme = UiDesignThemeRegistry.Resolve(ClassicMaterialDesignTheme.ThemeId);
        IUiDesignTheme miuixTheme = UiDesignThemeRegistry.Resolve(MiuixDesignTheme.ThemeId);

        Assert.Equal(classicTheme.GetBehavior(), ClassicMaterialDesignTheme.Instance.GetBehavior());
        Assert.Equal(miuixTheme.GetBehavior(), MiuixDesignTheme.Instance.GetBehavior());
    }

    [Fact]
    public void BossKeyBinding_ParsesAndRoundTripsAllPresets()
    {
        Assert.Equal(5, BossKeyBinding.Presets.Count);

        foreach (BossKeyPresetOption preset in BossKeyBinding.Presets)
        {
            bool parsed = BossKeyBinding.TryParse(preset.Gesture, out uint mods, out uint vk, out string normalized);
            Assert.True(parsed);
            Assert.True(vk > 0);
            Assert.Equal(preset.Gesture, normalized);

            int idx = BossKeyBinding.IndexOfPreset(normalized);
            Assert.Equal(normalized, BossKeyBinding.GestureAt(idx));
            if (normalized == "F9")
            {
                Assert.Equal(0u, mods);
            }
            else
            {
                Assert.True(mods != 0u);
            }
        }

        Assert.False(BossKeyBinding.TryParse("Unknown+Key", out uint fallbackMods, out uint fallbackVk, out string fallbackGesture));
        Assert.Equal(BossKeyBinding.DefaultGesture, fallbackGesture);
        Assert.Equal(BossKeyBinding.ModControl | BossKeyBinding.ModAlt, fallbackMods);
        Assert.Equal(BossKeyBinding.VkH, fallbackVk);
    }

    [Fact]
    public void SettingsViewModel_PersistsBossKeyAndAutoLockRoundTrip()
    {
        string tempRoot = CreateTempDirectory("eh-settings");
        AppPaths paths = AppPaths.CreateAt(tempRoot);
        try
        {
            using (var services = AppServices.Create(paths))
            {
                var vm = new EhViewer.Desktop.ViewModels.SettingsViewModel(services)
                {
                    AutoSaveOnChange = true
                };

                vm.EnableBossKey = false;
                vm.BossKeyGestureIndex = 1; // Alt+~
                vm.RequireUnlockOnBossRestore = false;
                vm.StealthTrayTooltip = true;
                vm.AutoLockMinutesIndex = 2; // 3 minutes

                Assert.False(services.Settings.EnableBossKey);
                Assert.Equal("Alt+~", services.Settings.BossKeyGesture);
                Assert.False(services.Settings.RequireUnlockOnBossRestore);
                Assert.True(services.Settings.StealthTrayTooltip);
                Assert.Equal(3, services.Settings.AutoLockMinutes);
            }

            using (var reloaded = AppServices.Create(paths))
            {
                Assert.False(reloaded.Settings.EnableBossKey);
                Assert.Equal("Alt+~", reloaded.Settings.BossKeyGesture);
                Assert.False(reloaded.Settings.RequireUnlockOnBossRestore);
                Assert.True(reloaded.Settings.StealthTrayTooltip);
                Assert.Equal(3, reloaded.Settings.AutoLockMinutes);
            }
        }
        finally
        {
            
        }
    }

    private sealed class TestDesignTheme : IUiDesignTheme
    {
        public UiThemeMetadata Metadata { get; } = new()
        {
            Id = "test-custom",
            DisplayName = "测试主题",
            BadgeText = "Test",
            Description = "Unit test theme stub",
            HeaderPreviewArgb = 0xFF334455u,
            LightAccentArgb = 0xFF334455u,
            LightSurfaceArgb = 0xFFFFFFFFu,
            LightCardArgb = 0xFFF5F5F5u,
            DarkAccentArgb = 0xFF667788u,
            DarkSurfaceArgb = 0xFF101010u,
            DarkCardArgb = 0xFF202020u
        };

        public UiThemeMetrics GetMetrics() => new();

        public UiThemeBehavior GetBehavior() => new();

        public UiThemePalette GetPalette(bool isDark, bool isBlack) =>
            ClassicMaterialDesignTheme.Instance.GetPalette(isDark, isBlack);
    }
}

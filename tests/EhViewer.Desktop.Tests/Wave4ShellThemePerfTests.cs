using System.Runtime.CompilerServices;
using System.Xml.Linq;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Theming;
using Windows.UI;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class Wave4ShellThemePerfTests
{
    private sealed class FakeResourceDictionary
    {
        public List<FakeResourceDictionary?> MergedDictionaries { get; } = [];
        public List<FakeResourceDictionary?> ThemeDictionaries { get; } = [];
    }

    private sealed class FakeBrush
    {
        public FakeBrush(Color color)
        {
            Color = color;
        }

        public Color Color { get; private set; }
        public int WriteCount { get; private set; }

        public void SetColor(Color value)
        {
            Color = value;
            WriteCount++;
        }
    }

    [Fact]
    public void ThemeMetricsBinder_RefreshAll_DeduplicatesEqualMetrics()
    {
        ThemeMetricsBinder.ResetRefreshTrackingForTests();
        try
        {
            UiThemeMetrics classic = ClassicMaterialDesignTheme.Instance.GetMetrics();
            UiThemeMetrics classicCopy = classic with { };
            UiThemeMetrics miuix = MiuixDesignTheme.Instance.GetMetrics();

            ThemeMetricsBinder.RefreshAll(classic);
            Assert.Equal(1, ThemeMetricsBinder.RefreshExecutionCount);
            Assert.Equal(classic, ThemeMetricsBinder.LastAppliedMetrics);

            // Distinct instance with identical record values must skip redundant traversal.
            ThemeMetricsBinder.RefreshAll(classicCopy);
            Assert.Equal(1, ThemeMetricsBinder.RefreshExecutionCount);

            // Switching to different metrics triggers refresh again.
            ThemeMetricsBinder.RefreshAll(miuix);
            Assert.Equal(2, ThemeMetricsBinder.RefreshExecutionCount);
            Assert.Equal(miuix, ThemeMetricsBinder.LastAppliedMetrics);

            // Repeated call with same Miuix metrics is skipped.
            ThemeMetricsBinder.RefreshAll(miuix with { });
            Assert.Equal(2, ThemeMetricsBinder.RefreshExecutionCount);
        }
        finally
        {
            ThemeMetricsBinder.ResetRefreshTrackingForTests();
        }
    }

    [Fact]
    public void ThemeMetricsBinder_RefreshAll_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => ThemeMetricsBinder.RefreshAll(null!));
    }

    [Fact]
    public void MaterialThemeService_ShouldApplyGlobalPalette_DeduplicatesIdenticalKeys()
    {
        var classicLight = new MaterialThemeService.AppliedPaletteKey(
            ClassicMaterialDesignTheme.ThemeId,
            IsDark: false,
            IsBlack: false);
        var classicLightSame = new MaterialThemeService.AppliedPaletteKey(
            ClassicMaterialDesignTheme.ThemeId,
            IsDark: false,
            IsBlack: false);
        var classicDark = new MaterialThemeService.AppliedPaletteKey(
            ClassicMaterialDesignTheme.ThemeId,
            IsDark: true,
            IsBlack: false);
        var classicBlack = new MaterialThemeService.AppliedPaletteKey(
            ClassicMaterialDesignTheme.ThemeId,
            IsDark: true,
            IsBlack: true);
        var miuixLight = new MaterialThemeService.AppliedPaletteKey(
            MiuixDesignTheme.ThemeId,
            IsDark: false,
            IsBlack: false);

        Assert.True(MaterialThemeService.ShouldApplyGlobalPalette(null, classicLight));
        Assert.False(MaterialThemeService.ShouldApplyGlobalPalette(classicLight, classicLightSame));
        Assert.True(MaterialThemeService.ShouldApplyGlobalPalette(classicLight, classicDark));
        Assert.True(MaterialThemeService.ShouldApplyGlobalPalette(classicDark, classicBlack));
        Assert.True(MaterialThemeService.ShouldApplyGlobalPalette(classicLight, miuixLight));
    }

    [Fact]
    public void MaterialThemeService_ShouldApplyWindowIcon_DeduplicatesByResolvedPath()
    {
        const string pathA = @"C:\app\Assets\AppIcon.ico";
        const string pathB = @"C:\app\Assets\OtherIcon.ico";

        Assert.False(MaterialThemeService.ShouldApplyWindowIcon(null, null));
        Assert.False(MaterialThemeService.ShouldApplyWindowIcon(null, string.Empty));
        Assert.True(MaterialThemeService.ShouldApplyWindowIcon(null, pathA));
        Assert.False(MaterialThemeService.ShouldApplyWindowIcon(pathA, pathA));
        Assert.True(MaterialThemeService.ShouldApplyWindowIcon(pathA, pathB));
    }

    [Fact]
    public void MaterialThemeService_TryUpdateColor_SkipsUnchangedColorAndWritesChangedColor()
    {
        var initial = Color.FromArgb(255, 0, 150, 136);
        var same = Color.FromArgb(255, 0, 150, 136);
        var changed = Color.FromArgb(255, 52, 130, 255);
        var brush = new FakeBrush(initial);

        bool updatedSame = MaterialThemeService.TryUpdateColor(
            brush,
            same,
            static b => b.Color,
            static (b, c) => b.SetColor(c));

        Assert.False(updatedSame);
        Assert.Equal(0, brush.WriteCount);

        bool updatedDifferent = MaterialThemeService.TryUpdateColor(
            brush,
            changed,
            static b => b.Color,
            static (b, c) => b.SetColor(c));

        Assert.True(updatedDifferent);
        Assert.Equal(1, brush.WriteCount);
        Assert.Equal(changed, brush.Color);
    }

    [Fact]
    public void MaterialThemeService_CollectSecondaryDictionaries_FlattensAndDeduplicates()
    {
        var root = new FakeResourceDictionary();
        var rootThemeLight = new FakeResourceDictionary();
        var merged1 = new FakeResourceDictionary();
        var merged2 = new FakeResourceDictionary();
        var sharedThemeDict = new FakeResourceDictionary();

        root.ThemeDictionaries.Add(rootThemeLight);
        root.ThemeDictionaries.Add(rootThemeLight); // duplicate in root theme dicts
        root.ThemeDictionaries.Add(root);           // self-reference must be excluded
        root.ThemeDictionaries.Add(null);

        merged1.ThemeDictionaries.Add(sharedThemeDict);
        merged1.ThemeDictionaries.Add(null);
        merged2.ThemeDictionaries.Add(sharedThemeDict); // shared across merged dicts
        merged2.ThemeDictionaries.Add(rootThemeLight);  // already seen on root

        root.MergedDictionaries.Add(merged1);
        root.MergedDictionaries.Add(null);
        root.MergedDictionaries.Add(merged1); // duplicate merged dict
        root.MergedDictionaries.Add(merged2);
        root.MergedDictionaries.Add(root);    // self-reference must be excluded

        List<FakeResourceDictionary> collected = MaterialThemeService.CollectSecondaryDictionaries(
            root,
            static d => d.MergedDictionaries,
            static d => d.ThemeDictionaries);

        Assert.Equal(4, collected.Count);
        Assert.DoesNotContain(root, collected);
        Assert.Contains(rootThemeLight, collected);
        Assert.Contains(merged1, collected);
        Assert.Contains(merged2, collected);
        Assert.Contains(sharedThemeDict, collected);
    }

    [Fact]
    public void MaterialThemeService_PaletteBrushMap_HasDistinctKeys()
    {
        Assert.NotEmpty(MaterialThemeService.PaletteBrushMap);
        int distinctCount = MaterialThemeService.PaletteBrushMap
            .Select(static entry => entry.Key)
            .Distinct(StringComparer.Ordinal)
            .Count();
        Assert.Equal(MaterialThemeService.PaletteBrushMap.Length, distinctCount);
    }

    [Fact]
    public void ThemeMotionHelper_ResolveCardPointerAction_SkipsNonSquircleWithoutHistoryAndResetsOnceWithHistory()
    {
        Assert.Equal(
            ThemeMotionHelper.CardPointerAction.Skip,
            ThemeMotionHelper.ResolveCardPointerAction(CardFeedbackStyle.ElevationBorder, hasHistoricalState: false));

        Assert.Equal(
            ThemeMotionHelper.CardPointerAction.ResetAndClear,
            ThemeMotionHelper.ResolveCardPointerAction(CardFeedbackStyle.ElevationBorder, hasHistoricalState: true));

        Assert.Equal(
            ThemeMotionHelper.CardPointerAction.Animate,
            ThemeMotionHelper.ResolveCardPointerAction(CardFeedbackStyle.SquircleSink, hasHistoricalState: false));

        Assert.Equal(
            ThemeMotionHelper.CardPointerAction.Animate,
            ThemeMotionHelper.ResolveCardPointerAction(CardFeedbackStyle.SquircleSink, hasHistoricalState: true));
    }

    [Fact]
    public void ThemeMotionHelper_TryHandleNonSquirclePointer_DoesNotWriteStateAndClearsHistoryOnce()
    {
        var table = new ConditionalWeakTable<object, ThemeMotionHelper.CardPointerState>();
        var card = new object();
        int resetCount = 0;

        // Non-squircle with no historical state: skips without inserting into table.
        bool handledFresh = ThemeMotionHelper.TryHandleNonSquirclePointer(
            table,
            card,
            CardFeedbackStyle.ElevationBorder,
            _ => resetCount++);

        Assert.True(handledFresh);
        Assert.Equal(0, resetCount);
        Assert.False(table.TryGetValue(card, out _));

        // Simulate prior Squircle hover state left before switching to ElevationBorder.
        table.AddOrUpdate(card, new ThemeMotionHelper.CardPointerState { IsHovered = true });

        bool handledWithHistory = ThemeMotionHelper.TryHandleNonSquirclePointer(
            table,
            card,
            CardFeedbackStyle.ElevationBorder,
            _ => resetCount++);

        Assert.True(handledWithHistory);
        Assert.Equal(1, resetCount);
        Assert.False(table.TryGetValue(card, out _));

        // Subsequent non-squircle events must skip cleanly without extra reset.
        bool handledSecondPass = ThemeMotionHelper.TryHandleNonSquirclePointer(
            table,
            card,
            CardFeedbackStyle.ElevationBorder,
            _ => resetCount++);

        Assert.True(handledSecondPass);
        Assert.Equal(1, resetCount);

        // Squircle theme delegates to normal pointer handling.
        bool handledSquircle = ThemeMotionHelper.TryHandleNonSquirclePointer(
            table,
            card,
            CardFeedbackStyle.SquircleSink,
            _ => resetCount++);

        Assert.False(handledSquircle);
        Assert.Equal(1, resetCount);
    }

    [Fact]
    public void MainWindow_OverlaysUseDeferredLoadingAndUnloadedGuards()
    {
        string repoRoot = FindDesktopRepoRoot();
        string mainWindowXamlPath = Path.Combine(repoRoot, "src", "EhViewer.Desktop", "MainWindow.xaml");
        string mainWindowCsPath = Path.Combine(repoRoot, "src", "EhViewer.Desktop", "MainWindow.xaml.cs");
        string mainWindowShellPath = Path.Combine(repoRoot, "src", "EhViewer.Desktop", "MainWindow.Shell.cs");

        XDocument xaml = XDocument.Load(mainWindowXamlPath);
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement? themeTransitionHost = xaml.Descendants()
            .FirstOrDefault(el => el.Attribute(xNs + "Name")?.Value == "ThemeTransitionHost");
        XElement? privacyLockOverlay = xaml.Descendants()
            .FirstOrDefault(el => el.Attribute(xNs + "Name")?.Value == "PrivacyLockOverlay");

        Assert.NotNull(themeTransitionHost);
        Assert.Equal("False", themeTransitionHost!.Attribute(xNs + "Load")?.Value);

        Assert.NotNull(privacyLockOverlay);
        Assert.Equal("False", privacyLockOverlay!.Attribute(xNs + "Load")?.Value);

        string mainWindowCs = File.ReadAllText(mainWindowCsPath);
        Assert.Contains("RootGrid.FindName(nameof(ThemeTransitionHost))", mainWindowCs, StringComparison.Ordinal);
        Assert.DoesNotContain(".OfType<NavigationEntry>()", mainWindowCs, StringComparison.Ordinal);

        int resetStart = mainWindowCs.IndexOf("private void ResetThemeTransitionOverlay()", StringComparison.Ordinal);
        Assert.True(resetStart >= 0, "ResetThemeTransitionOverlay method should exist.");
        int nextMethod = mainWindowCs.IndexOf("private void OnToggleSiteClick", resetStart, StringComparison.Ordinal);
        Assert.True(nextMethod > resetStart);
        string resetBody = mainWindowCs[resetStart..nextMethod];

        int loadedGuardIndex = resetBody.IndexOf("!_themeTransitionHostLoaded", StringComparison.Ordinal);
        int returnIndex = resetBody.IndexOf("return;", StringComparison.Ordinal);
        int visualAccessIndex = resetBody.IndexOf("ElementCompositionPreview.GetElementVisual", StringComparison.Ordinal);

        Assert.True(loadedGuardIndex >= 0, "ResetThemeTransitionOverlay must guard on _themeTransitionHostLoaded.");
        Assert.True(returnIndex > loadedGuardIndex, "Guard must return early before accessing visual.");
        Assert.True(visualAccessIndex > returnIndex, "Visual access must come after the unloaded guard.");

        string mainWindowShellCs = File.ReadAllText(mainWindowShellPath);
        Assert.Contains("RootGrid.FindName(nameof(PrivacyLockOverlay))", mainWindowShellCs, StringComparison.Ordinal);
        Assert.Contains("_privacyLockOverlayLoaded && PrivacyLockOverlay is not null", mainWindowShellCs, StringComparison.Ordinal);
    }

    private static string FindDesktopRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "EhViewer.Desktop.slnx")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate EhViewer.Desktop.slnx above AppContext.BaseDirectory.");
    }
}

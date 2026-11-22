using System.Xml.Linq;
using EhViewer.Desktop.Views;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class Wave6ControlsMotionTests
{
    [Fact]
    public void ReaderPage_ComputeHudChromeTargets_CoversAllBooleanCombinations()
    {
        for (int mask = 0; mask < 32; mask++)
        {
            bool showHeaderBar = (mask & 1) != 0;
            bool hudHeaderVisible = (mask & 2) != 0;
            bool showFooterBar = (mask & 4) != 0;
            bool hudFooterVisible = (mask & 8) != 0;
            bool showPageNumber = (mask & 16) != 0;

            ReaderPage.HudChromeTargets actual = ReaderPage.ComputeHudChromeTargets(
                showHeaderBar,
                hudHeaderVisible,
                showFooterBar,
                hudFooterVisible,
                showPageNumber);

            bool expectedHeader = showHeaderBar && hudHeaderVisible;
            bool expectedFooter = showFooterBar && hudFooterVisible;
            bool expectedCorner = showPageNumber && !expectedFooter;

            Assert.Equal(expectedHeader, actual.HeaderVisible);
            Assert.Equal(expectedFooter, actual.FooterVisible);
            Assert.Equal(expectedCorner, actual.CornerBadgeVisible);
        }
    }

    [Fact]
    public void ReaderPage_ComputeHudChromeTargets_CornerBadgeMutuallyExclusiveWithVisibleFooter()
    {
        // Visible footer hides corner badge even when page numbers are enabled.
        ReaderPage.HudChromeTargets withVisibleFooter = ReaderPage.ComputeHudChromeTargets(
            showHeaderBar: true,
            hudHeaderVisible: true,
            showFooterBar: true,
            hudFooterVisible: true,
            showPageNumber: true);
        Assert.True(withVisibleFooter.FooterVisible);
        Assert.False(withVisibleFooter.CornerBadgeVisible);

        // Hidden footer (user toggled off) reveals corner badge when page numbers are enabled.
        ReaderPage.HudChromeTargets withHiddenFooter = ReaderPage.ComputeHudChromeTargets(
            showHeaderBar: false,
            hudHeaderVisible: false,
            showFooterBar: true,
            hudFooterVisible: false,
            showPageNumber: true);
        Assert.False(withHiddenFooter.FooterVisible);
        Assert.True(withHiddenFooter.CornerBadgeVisible);

        // Fullscreen suppressed footer (showFooterBar == false) also reveals corner badge.
        ReaderPage.HudChromeTargets fullscreenSuppressedFooter = ReaderPage.ComputeHudChromeTargets(
            showHeaderBar: false,
            hudHeaderVisible: true,
            showFooterBar: false,
            hudFooterVisible: true,
            showPageNumber: true);
        Assert.False(fullscreenSuppressedFooter.HeaderVisible);
        Assert.False(fullscreenSuppressedFooter.FooterVisible);
        Assert.True(fullscreenSuppressedFooter.CornerBadgeVisible);

        // Disabled page numbers keep corner badge hidden regardless of footer state.
        ReaderPage.HudChromeTargets disabledPageNumber = ReaderPage.ComputeHudChromeTargets(
            showHeaderBar: false,
            hudHeaderVisible: false,
            showFooterBar: true,
            hudFooterVisible: false,
            showPageNumber: false);
        Assert.False(disabledPageNumber.CornerBadgeVisible);
    }

    [Fact]
    public void FabLayoutControl_UsesGpuMotionAndRotationWithoutGlyphSwap()
    {
        string repoRoot = FindDesktopRepoRoot();
        string xamlPath = Path.Combine(repoRoot, "src", "EhViewer.Desktop", "Controls", "FabLayoutControl.xaml");
        string csPath = Path.Combine(repoRoot, "src", "EhViewer.Desktop", "Controls", "FabLayoutControl.xaml.cs");

        XDocument xaml = XDocument.Load(xamlPath);
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement? primaryIcon = xaml.Descendants()
            .FirstOrDefault(el => el.Attribute(xNs + "Name")?.Value == "PrimaryFabIcon");

        Assert.NotNull(primaryIcon);
        Assert.Equal("\uE710", primaryIcon!.Attribute("Glyph")?.Value);
        Assert.Equal("28", primaryIcon.Attribute("Width")?.Value);
        Assert.Equal("28", primaryIcon.Attribute("Height")?.Value);

        string cs = File.ReadAllText(csPath);
        Assert.Contains("ThemeMotionHelper.AnimateScrimOpacity", cs, StringComparison.Ordinal);
        Assert.Contains("ThemeMotionHelper.AnimatePopoverShow", cs, StringComparison.Ordinal);
        Assert.Contains("ThemeMotionHelper.AnimateStaggeredShow", cs, StringComparison.Ordinal);
        Assert.Contains("ThemeMotionHelper.AnimateRotation", cs, StringComparison.Ordinal);
        Assert.Contains("ThemeMotionHelper.AnimatePopoverHide", cs, StringComparison.Ordinal);
        Assert.DoesNotContain("\\uE711", cs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EnableDependentAnimation", cs, StringComparison.Ordinal);
        Assert.DoesNotContain("Storyboard", cs, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchLayoutControl_UsesGpuMotionForOverlayTabsAndAdvancePanel()
    {
        string repoRoot = FindDesktopRepoRoot();
        string csPath = Path.Combine(repoRoot, "src", "EhViewer.Desktop", "Controls", "SearchLayoutControl.xaml.cs");

        string cs = File.ReadAllText(csPath);
        Assert.Contains("ThemeMotionHelper.AnimateOverlayShow", cs, StringComparison.Ordinal);
        Assert.Contains("ThemeMotionHelper.AnimateOverlayHide", cs, StringComparison.Ordinal);
        Assert.Contains("ThemeMotionHelper.AnimatePopoverShow", cs, StringComparison.Ordinal);
        Assert.Contains("ThemeMotionHelper.AnimatePopoverHide", cs, StringComparison.Ordinal);
        Assert.Contains("_isOpen", cs, StringComparison.Ordinal);
        Assert.Contains("_suppressAdvanceAnimation", cs, StringComparison.Ordinal);

        int tabNormalStart = cs.IndexOf("private void OnTabNormalClick", StringComparison.Ordinal);
        int tabImageStart = cs.IndexOf("private void OnTabImageClick", StringComparison.Ordinal);
        int advanceStart = cs.IndexOf("private void OnAdvanceToggled", StringComparison.Ordinal);
        int advanceEnd = cs.IndexOf("private async void OnPickSearchImageClick", StringComparison.Ordinal);

        Assert.True(tabNormalStart >= 0 && tabImageStart > tabNormalStart && advanceStart > tabImageStart && advanceEnd > advanceStart);

        string tabNormalBody = cs[tabNormalStart..tabImageStart];
        string tabImageBody = cs[tabImageStart..advanceStart];
        string advanceBody = cs[advanceStart..advanceEnd];

        Assert.Contains("ThemeMotionHelper.AnimatePopoverShow", tabNormalBody, StringComparison.Ordinal);
        Assert.Contains("ThemeMotionHelper.AnimatePopoverShow", tabImageBody, StringComparison.Ordinal);
        Assert.Contains("ThemeMotionHelper.AnimatePopoverShow", advanceBody, StringComparison.Ordinal);
        Assert.Contains("ThemeMotionHelper.AnimatePopoverHide", advanceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableDependentAnimation", cs, StringComparison.Ordinal);
        Assert.DoesNotContain("Storyboard", cs, StringComparison.Ordinal);
    }

    [Fact]
    public void ReaderPage_UsesGpuHudTransitionsWithoutDependentAnimationsOrCpuStoryboards()
    {
        string repoRoot = FindDesktopRepoRoot();
        string csPath = Path.Combine(repoRoot, "src", "EhViewer.Desktop", "Views", "ReaderPage.xaml.cs");

        string cs = File.ReadAllText(csPath);
        Assert.Contains("SyncHudChromeState(animate: false);", cs, StringComparison.Ordinal);
        Assert.Contains("ThemeMotionHelper.AnimateBarSlideShow", cs, StringComparison.Ordinal);
        Assert.Contains("ThemeMotionHelper.AnimateBarSlideHide", cs, StringComparison.Ordinal);
        Assert.Contains("ThemeMotionHelper.AnimatePopoverShow", cs, StringComparison.Ordinal);
        Assert.Contains("ThemeMotionHelper.AnimatePopoverHide", cs, StringComparison.Ordinal);
        Assert.Contains("_hudHideTimer.Stop();", cs, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableDependentAnimation", cs, StringComparison.Ordinal);
        Assert.DoesNotContain("Storyboard", cs, StringComparison.Ordinal);
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

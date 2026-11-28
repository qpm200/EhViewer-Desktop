using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Theming;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class ThemeMotionCoreTests : DesktopTestBase
{
    [Fact]
    public void AnimationTokens_InvalidatePreviousTokenAcrossInterruptions()
    {
        var key1 = new object();
        var key2 = new object();
        var unseededKey = new object();

        Assert.False(ThemeMotionHelper.IsCurrentAnimationTokenForKey(unseededKey, 0));
        Assert.False(ThemeMotionHelper.IsCurrentAnimationTokenForKey(unseededKey, 1));

        int hideToken = ThemeMotionHelper.NextAnimationTokenForKey(key1);
        Assert.True(ThemeMotionHelper.IsCurrentAnimationTokenForKey(key1, hideToken));

        // Simulate rapid re-open (Show) interrupting an in-flight Hide on the same element key.
        int showToken = ThemeMotionHelper.NextAnimationTokenForKey(key1);
        Assert.NotEqual(hideToken, showToken);
        Assert.False(ThemeMotionHelper.IsCurrentAnimationTokenForKey(key1, hideToken));
        Assert.True(ThemeMotionHelper.IsCurrentAnimationTokenForKey(key1, showToken));

        // Tokens for distinct keys remain completely independent.
        int key2Token = ThemeMotionHelper.NextAnimationTokenForKey(key2);
        Assert.True(ThemeMotionHelper.IsCurrentAnimationTokenForKey(key2, key2Token));
        Assert.True(ThemeMotionHelper.IsCurrentAnimationTokenForKey(key1, showToken));

        Assert.Throws<ArgumentNullException>(() => ThemeMotionHelper.NextAnimationTokenForKey(null!));
        Assert.Throws<ArgumentNullException>(() => ThemeMotionHelper.IsCurrentAnimationTokenForKey(null!, 1));
        Assert.Throws<ArgumentNullException>(() => ThemeMotionHelper.NextAnimationToken(null!));
        Assert.Throws<ArgumentNullException>(() => ThemeMotionHelper.IsCurrentAnimationToken(null!, 1));
    }

    [Fact]
    public void ReducedMotionOverride_AndComputeSceneStartOpacity_RespectBoundsAndOverrides()
    {
        bool? previous = ThemeMotionHelper.AnimationsEnabledOverrideForTesting;
        try
        {
            ThemeMotionHelper.AnimationsEnabledOverrideForTesting = false;
            Assert.False(ThemeMotionHelper.IsAnimationsEnabled);

            ThemeMotionHelper.AnimationsEnabledOverrideForTesting = true;
            Assert.True(ThemeMotionHelper.IsAnimationsEnabled);

            ThemeMotionHelper.AnimationsEnabledOverrideForTesting = null;
            _ = ThemeMotionHelper.IsAnimationsEnabled;
        }
        finally
        {
            ThemeMotionHelper.AnimationsEnabledOverrideForTesting = previous;
        }

        // Reduced motion always forces immediate opaque frame (1f).
        Assert.Equal(1f, ThemeMotionHelper.ComputeSceneStartOpacity(0f, isReducedMotion: true));
        Assert.Equal(1f, ThemeMotionHelper.ComputeSceneStartOpacity(0.5f, isReducedMotion: true));
        Assert.Equal(1f, ThemeMotionHelper.ComputeSceneStartOpacity(1f, isReducedMotion: true));
        Assert.Equal(1f, ThemeMotionHelper.ComputeSceneStartOpacity(float.NaN, isReducedMotion: true));

        // Normal transitions clamp to [0.35f, 0.85f] so rapid back-to-back switches never jump from 0f black.
        Assert.Equal(0.35f, ThemeMotionHelper.ComputeSceneStartOpacity(0f, isReducedMotion: false));
        Assert.Equal(0.35f, ThemeMotionHelper.ComputeSceneStartOpacity(0.20f, isReducedMotion: false));
        Assert.Equal(0.60f, ThemeMotionHelper.ComputeSceneStartOpacity(0.60f, isReducedMotion: false));
        Assert.Equal(0.85f, ThemeMotionHelper.ComputeSceneStartOpacity(0.85f, isReducedMotion: false));
        Assert.Equal(0.85f, ThemeMotionHelper.ComputeSceneStartOpacity(1.0f, isReducedMotion: false));

        float nanFallback = ThemeMotionHelper.ComputeSceneStartOpacity(float.NaN, isReducedMotion: false);
        Assert.InRange(nanFallback, 0.35f, 0.85f);
    }

    [Theory]
    [InlineData(true, false, true, false, false, false)]  // Mica Base already active -> skip
    [InlineData(true, false, true, true, true, false)]    // Mica BaseAlt already active -> skip
    [InlineData(true, false, true, false, true, true)]    // Mica Kind Base -> BaseAlt -> recreate
    [InlineData(true, false, true, true, false, true)]    // Mica Kind BaseAlt -> Base -> recreate
    [InlineData(false, false, true, false, false, true)]  // None -> Mica -> recreate
    [InlineData(false, true, true, false, true, true)]    // Acrylic -> Mica -> recreate
    [InlineData(false, true, false, false, false, false)] // Acrylic already active -> skip
    [InlineData(false, false, false, false, false, true)] // None -> Acrylic -> recreate
    [InlineData(true, false, false, true, true, true)]    // Mica -> Acrylic -> recreate
    public void WindowAppearance_ShouldRecreateBackdrop_OnlyWhenTypeOrKindChanges(
        bool hasMica,
        bool hasAcrylic,
        bool wantMica,
        bool currentMicaBaseAlt,
        bool wantMicaBaseAlt,
        bool expectedRecreate)
    {
        bool actual = WindowAppearance.ShouldRecreateBackdrop(
            hasMica,
            hasAcrylic,
            wantMica,
            currentMicaBaseAlt,
            wantMicaBaseAlt);

        Assert.Equal(expectedRecreate, actual);
    }

    [Fact]
    public void MaterialThemeService_ShouldApplyTitleBarColors_DeduplicatesIdenticalKeys()
    {
        var key1 = new MaterialThemeService.AppliedTitleBarKey(0xFF112233, 0xFF223344, 0xFFFFFFFF, 0xFFCCCCCC);
        var key1Same = new MaterialThemeService.AppliedTitleBarKey(0xFF112233, 0xFF223344, 0xFFFFFFFF, 0xFFCCCCCC);
        var keyDifferentBg = new MaterialThemeService.AppliedTitleBarKey(0xFF000000, 0xFF223344, 0xFFFFFFFF, 0xFFCCCCCC);
        var keyDifferentFg = new MaterialThemeService.AppliedTitleBarKey(0xFF112233, 0xFF223344, 0xFF111111, 0xFFCCCCCC);

        Assert.True(MaterialThemeService.ShouldApplyTitleBarColors(null, key1));
        Assert.False(MaterialThemeService.ShouldApplyTitleBarColors(key1, key1Same));
        Assert.True(MaterialThemeService.ShouldApplyTitleBarColors(key1, keyDifferentBg));
        Assert.True(MaterialThemeService.ShouldApplyTitleBarColors(key1, keyDifferentFg));
    }

    [Fact]
    public void StaticSourceInvariants_EnsureGpuSliderAndSingleTitleBarWrite()
    {
        string motionHelperPath = ResolveDesktopSourceFile("Services", "Theming", "ThemeMotionHelper.cs");
        string mainWindowCsPath = ResolveDesktopSourceFile("MainWindow.xaml.cs");

        Assert.True(File.Exists(motionHelperPath), $"Missing {motionHelperPath}");
        Assert.True(File.Exists(mainWindowCsPath), $"Missing {mainWindowCsPath}");

        string motionSource = File.ReadAllText(motionHelperPath);
        Assert.DoesNotContain("new Storyboard", motionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DoubleAnimation", motionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TranslateTransform", motionSource, StringComparison.Ordinal);
        Assert.Contains("AnimationDelayBehavior.SetInitialValueBeforeDelay", motionSource, StringComparison.Ordinal);
        Assert.Contains("AnimateBarSlideShow", motionSource, StringComparison.Ordinal);
        Assert.Contains("AnimateBarSlideHide", motionSource, StringComparison.Ordinal);
        Assert.Contains("AnimatePopoverShow", motionSource, StringComparison.Ordinal);
        Assert.Contains("AnimatePopoverHide", motionSource, StringComparison.Ordinal);
        Assert.Contains("AnimateScrimOpacity", motionSource, StringComparison.Ordinal);
        Assert.Contains("AnimateRotation", motionSource, StringComparison.Ordinal);
        Assert.Contains("AnimateStaggeredShow", motionSource, StringComparison.Ordinal);

        string mainWindowSource = File.ReadAllText(mainWindowCsPath);
        Assert.DoesNotContain("_themeThumbStoryboard", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains("applyTitleBarColors: false", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains("SetSliderThumbPositionImmediate", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains("AnimateSliderThumbPosition", mainWindowSource, StringComparison.Ordinal);
    }
}

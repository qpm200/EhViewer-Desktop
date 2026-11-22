using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace EhViewer.Desktop.Services.Theming;

public enum SceneNavigationDirection
{
    RootSwitch = 0,
    ForwardPush = 1,
    BackPop = 2
}

/// <summary>
/// Shared GPU-composited motion and tactile interaction helper driven by <see cref="UiThemeBehavior"/>.
/// Centralizes scene transitions, overlay animations, drawer hover glides, card press/sink physics,
/// reduced-motion adaptation, generation-token cancellation, and shared bar/popover/scrim/stagger primitives.
/// </summary>
public static class ThemeMotionHelper
{
    private static readonly ConditionalWeakTable<Compositor, Dictionary<(float, float, float, float), CubicBezierEasingFunction>> EasingCache = new();
    private static readonly ConditionalWeakTable<object, StrongBox<int>> AnimationTokens = new();
    private static readonly object UiSettingsGate = new();
    private static Windows.UI.ViewManagement.UISettings? _uiSettings;
    private static bool _uiSettingsInitialized;
    private static volatile bool _systemAnimationsEnabled = true;

    internal static bool? AnimationsEnabledOverrideForTesting { get; set; }

    public static bool IsAnimationsEnabled
    {
        get
        {
            bool? testOverride = AnimationsEnabledOverrideForTesting;
            if (testOverride.HasValue)
            {
                return testOverride.Value;
            }

            EnsureUiSettingsInitialized();
            return _systemAnimationsEnabled;
        }
    }

    private static void EnsureUiSettingsInitialized()
    {
        if (_uiSettingsInitialized)
        {
            return;
        }

        lock (UiSettingsGate)
        {
            if (_uiSettingsInitialized)
            {
                return;
            }

            try
            {
                var settings = new Windows.UI.ViewManagement.UISettings();
                _systemAnimationsEnabled = settings.AnimationsEnabled;
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                {
                    settings.AnimationsEnabledChanged += static (sender, _) =>
                    {
                        try
                        {
                            _systemAnimationsEnabled = sender.AnimationsEnabled;
                        }
                        catch
                        {
                        }
                    };
                }

                _uiSettings = settings;
            }
            catch
            {
                _systemAnimationsEnabled = true;
            }
            finally
            {
                _uiSettingsInitialized = true;
            }
        }
    }

    internal static int NextAnimationTokenForKey(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        StrongBox<int> box = AnimationTokens.GetOrCreateValue(key);
        return Interlocked.Increment(ref box.Value);
    }

    internal static bool IsCurrentAnimationTokenForKey(object key, int token)
    {
        ArgumentNullException.ThrowIfNull(key);
        return AnimationTokens.TryGetValue(key, out StrongBox<int>? box) &&
               Volatile.Read(ref box.Value) == token;
    }

    internal static int NextAnimationToken(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return NextAnimationTokenForKey(element);
    }

    internal static bool IsCurrentAnimationToken(UIElement element, int token)
    {
        ArgumentNullException.ThrowIfNull(element);
        return IsCurrentAnimationTokenForKey(element, token);
    }

    internal static float ComputeSceneStartOpacity(float currentOpacity, bool isReducedMotion)
    {
        if (isReducedMotion)
        {
            return 1f;
        }

        if (float.IsNaN(currentOpacity) || float.IsInfinity(currentOpacity))
        {
            return 0.45f;
        }

        return Math.Clamp(currentOpacity, 0.35f, 0.85f);
    }

    private static void SetCenteredAnchor(Visual visual, UIElement element, float fallbackCenter = 0f)
    {
        if (element is FrameworkElement fe)
        {
            float width = fe.ActualWidth > 1
                ? (float)fe.ActualWidth
                : !double.IsNaN(fe.Width) && fe.Width > 1
                    ? (float)fe.Width
                    : fe.DesiredSize.Width > 1
                        ? (float)fe.DesiredSize.Width
                        : 0f;
            float height = fe.ActualHeight > 1
                ? (float)fe.ActualHeight
                : !double.IsNaN(fe.Height) && fe.Height > 1
                    ? (float)fe.Height
                    : fe.DesiredSize.Height > 1
                        ? (float)fe.DesiredSize.Height
                        : 0f;

            if (width > 1f && height > 1f)
            {
                visual.CenterPoint = new Vector3(width * 0.5f, height * 0.5f, 0f);
                return;
            }
        }

        if (fallbackCenter > 0f)
        {
            visual.CenterPoint = new Vector3(fallbackCenter, fallbackCenter, 0f);
        }
    }

    private static float ResolveResumeOpacity(Visual visual)
    {
        float current = visual.Opacity;
        return current is >= 0.05f and <= 0.99f ? current : 0f;
    }

    public static CubicBezierEasingFunction CreateEasingFunction(Compositor compositor, UiThemeBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(behavior);

        Dictionary<(float, float, float, float), CubicBezierEasingFunction> byControlPoints =
            EasingCache.GetOrCreateValue(compositor);

        var key = (
            behavior.EasingControlPoint1X,
            behavior.EasingControlPoint1Y,
            behavior.EasingControlPoint2X,
            behavior.EasingControlPoint2Y);

        lock (byControlPoints)
        {
            if (byControlPoints.TryGetValue(key, out CubicBezierEasingFunction? cached))
            {
                return cached;
            }

            CubicBezierEasingFunction created = compositor.CreateCubicBezierEasingFunction(
                new Vector2(key.Item1, key.Item2),
                new Vector2(key.Item3, key.Item4));
            byControlPoints[key] = created;
            return created;
        }
    }

    public static void AnimateSceneTransition(
        UIElement host,
        SceneNavigationDirection direction,
        UiThemeBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(behavior);

        _ = NextAnimationToken(host);
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(host, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(host);
            float originalZ = host.Translation.Z;

            if (!IsAnimationsEnabled)
            {
                visual.StopAnimation("Translation");
                visual.StopAnimation("Translation.X");
                visual.StopAnimation("Translation.Y");
                visual.StopAnimation(nameof(Visual.Opacity));
                visual.StopAnimation(nameof(Visual.Scale));
                host.Translation = new Vector3(0f, 0f, originalZ);
                visual.Opacity = 1f;
                visual.Scale = Vector3.One;
                return;
            }

            Compositor compositor = visual.Compositor;
            CubicBezierEasingFunction easing = CreateEasingFunction(compositor, behavior);
            TimeSpan duration = TimeSpan.FromMilliseconds(Math.Max(60, behavior.SceneTransitionDurationMs));

            Vector3 startOffset;
            float startScale = 1.0f;

            if (behavior.SceneTransition == SceneTransitionStyle.HyperPushSpring)
            {
                startOffset = direction switch
                {
                    SceneNavigationDirection.ForwardPush => new Vector3(behavior.ScenePushOffsetPx, 0f, originalZ),
                    SceneNavigationDirection.BackPop => new Vector3(-behavior.ScenePushOffsetPx * 0.78f, 0f, originalZ),
                    _ => new Vector3(0f, behavior.SceneRootOffsetPx, originalZ)
                };
                if (direction == SceneNavigationDirection.RootSwitch)
                {
                    startScale = 0.985f;
                }
            }
            else
            {
                startOffset = direction switch
                {
                    SceneNavigationDirection.ForwardPush => new Vector3(0f, behavior.ScenePushOffsetPx, originalZ),
                    SceneNavigationDirection.BackPop => new Vector3(0f, -behavior.SceneRootOffsetPx * 0.65f, originalZ),
                    _ => new Vector3(0f, behavior.SceneRootOffsetPx, originalZ)
                };
            }

            Vector3KeyFrameAnimation translationAnim = compositor.CreateVector3KeyFrameAnimation();
            translationAnim.InsertKeyFrame(0f, startOffset);
            translationAnim.InsertKeyFrame(1f, new Vector3(0f, 0f, originalZ), easing);
            translationAnim.Duration = duration;

            float startOpacity = ComputeSceneStartOpacity(visual.Opacity, isReducedMotion: false);
            ScalarKeyFrameAnimation opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(0f, startOpacity);
            opacityAnim.InsertKeyFrame(1f, 1f, easing);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(Math.Max(60, (int)(behavior.SceneTransitionDurationMs * 0.85)));

            visual.StartAnimation("Translation", translationAnim);
            visual.StartAnimation(nameof(Visual.Opacity), opacityAnim);

            if (startScale < 0.999f && host is FrameworkElement fe && fe.ActualWidth > 1 && fe.ActualHeight > 1)
            {
                visual.CenterPoint = new Vector3((float)fe.ActualWidth * 0.5f, (float)fe.ActualHeight * 0.5f, 0f);
                Vector3KeyFrameAnimation scaleAnim = compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(0f, new Vector3(startScale, startScale, 1f));
                scaleAnim.InsertKeyFrame(1f, Vector3.One, easing);
                scaleAnim.Duration = duration;
                visual.StartAnimation(nameof(Visual.Scale), scaleAnim);
            }
            else
            {
                visual.StopAnimation(nameof(Visual.Scale));
                visual.Scale = Vector3.One;
            }
        }
        catch
        {
            // Composition animations require a live visual tree; ignore during teardown or headless tests.
        }
    }

    public static void AnimateDrawerHoverTransition(
        UIElement drawer,
        bool isHoverOverlay,
        UiThemeBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(drawer);
        ArgumentNullException.ThrowIfNull(behavior);

        _ = NextAnimationToken(drawer);
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(drawer, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(drawer);
            float zElevation = isHoverOverlay ? 24f : 0f;

            if (!IsAnimationsEnabled)
            {
                visual.StopAnimation("Translation");
                drawer.Translation = new Vector3(0f, 0f, zElevation);
                return;
            }

            Compositor compositor = visual.Compositor;
            CubicBezierEasingFunction easing = CreateEasingFunction(compositor, behavior);

            float startX = isHoverOverlay ? -18f : -8f;
            Vector3KeyFrameAnimation anim = compositor.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(0f, new Vector3(startX, 0f, zElevation));
            anim.InsertKeyFrame(1f, new Vector3(0f, 0f, zElevation), easing);
            anim.Duration = TimeSpan.FromMilliseconds(Math.Max(60, behavior.DrawerSlideDurationMs));

            visual.StartAnimation("Translation", anim);
        }
        catch
        {
            // Best-effort composition glide.
        }
    }

    public static void AnimateOverlayShow(
        UIElement panel,
        UIElement? scrim,
        UiThemeBehavior behavior,
        bool fromRight = false)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(behavior);

        _ = NextAnimationToken(panel);
        if (scrim is not null)
        {
            _ = NextAnimationToken(scrim);
        }

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(panel, true);
            Visual panelVisual = ElementCompositionPreview.GetElementVisual(panel);
            float originalZ = panel.Translation.Z;

            if (!IsAnimationsEnabled)
            {
                panelVisual.StopAnimation("Translation");
                panelVisual.StopAnimation("Translation.X");
                panelVisual.StopAnimation("Translation.Y");
                panelVisual.StopAnimation(nameof(Visual.Opacity));
                panelVisual.StopAnimation(nameof(Visual.Scale));
                panel.Translation = new Vector3(0f, 0f, originalZ);
                panelVisual.Opacity = 1f;
                panelVisual.Scale = Vector3.One;
                if (scrim is not null)
                {
                    Visual scrimVisual = ElementCompositionPreview.GetElementVisual(scrim);
                    scrimVisual.StopAnimation(nameof(Visual.Opacity));
                    scrimVisual.Opacity = 1f;
                }

                return;
            }

            Compositor compositor = panelVisual.Compositor;
            CubicBezierEasingFunction easing = CreateEasingFunction(compositor, behavior);
            TimeSpan duration = TimeSpan.FromMilliseconds(Math.Max(60, behavior.OverlayPanelDurationMs));

            if (scrim is not null)
            {
                Visual scrimVisual = ElementCompositionPreview.GetElementVisual(scrim);
                ScalarKeyFrameAnimation scrimOpacity = compositor.CreateScalarKeyFrameAnimation();
                scrimOpacity.InsertKeyFrame(0f, ResolveResumeOpacity(scrimVisual));
                scrimOpacity.InsertKeyFrame(1f, 1f, easing);
                scrimOpacity.Duration = duration;
                scrimVisual.StartAnimation(nameof(Visual.Opacity), scrimOpacity);
            }

            Vector3 startOffset = fromRight
                ? new Vector3(behavior.MotionPreset == ThemeMotionPreset.MiuixFolmeSpring ? 56f : 28f, 0f, originalZ)
                : new Vector3(0f, behavior.MotionPreset == ThemeMotionPreset.MiuixFolmeSpring ? -18f : -10f, originalZ);

            Vector3KeyFrameAnimation slideAnim = compositor.CreateVector3KeyFrameAnimation();
            slideAnim.InsertKeyFrame(0f, startOffset);
            slideAnim.InsertKeyFrame(1f, new Vector3(0f, 0f, originalZ), easing);
            slideAnim.Duration = duration;
            panelVisual.StartAnimation("Translation", slideAnim);

            ScalarKeyFrameAnimation panelOpacity = compositor.CreateScalarKeyFrameAnimation();
            panelOpacity.InsertKeyFrame(0f, ResolveResumeOpacity(panelVisual));
            panelOpacity.InsertKeyFrame(1f, 1f, easing);
            panelOpacity.Duration = duration;
            panelVisual.StartAnimation(nameof(Visual.Opacity), panelOpacity);

            if (behavior.OverlayScaleFrom < 0.999f && panel is FrameworkElement fe && fe.ActualWidth > 1 && fe.ActualHeight > 1)
            {
                panelVisual.CenterPoint = new Vector3((float)fe.ActualWidth * 0.5f, (float)fe.ActualHeight * 0.5f, 0f);
                Vector3KeyFrameAnimation scaleAnim = compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(0f, new Vector3(behavior.OverlayScaleFrom, behavior.OverlayScaleFrom, 1f));
                scaleAnim.InsertKeyFrame(1f, Vector3.One, easing);
                scaleAnim.Duration = duration;
                panelVisual.StartAnimation(nameof(Visual.Scale), scaleAnim);
            }
            else
            {
                panelVisual.StopAnimation(nameof(Visual.Scale));
                panelVisual.Scale = Vector3.One;
            }
        }
        catch
        {
            // Ignore when host visual tree is unavailable.
        }
    }

    public static void AnimateOverlayHide(
        UIElement panel,
        UIElement? scrim,
        UiThemeBehavior behavior,
        Action onCompleted,
        bool toRight = false)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(behavior);
        ArgumentNullException.ThrowIfNull(onCompleted);

        int hideToken = NextAnimationToken(panel);
        if (scrim is not null)
        {
            _ = NextAnimationToken(scrim);
        }

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(panel, true);
            Visual panelVisual = ElementCompositionPreview.GetElementVisual(panel);
            float originalZ = panel.Translation.Z;

            if (!IsAnimationsEnabled)
            {
                panelVisual.StopAnimation("Translation");
                panelVisual.StopAnimation("Translation.X");
                panelVisual.StopAnimation("Translation.Y");
                panelVisual.StopAnimation(nameof(Visual.Opacity));
                panelVisual.StopAnimation(nameof(Visual.Scale));
                panel.Translation = new Vector3(0f, 0f, originalZ);
                panelVisual.Opacity = 1f;
                panelVisual.Scale = Vector3.One;
                if (scrim is not null)
                {
                    Visual scrimVisual = ElementCompositionPreview.GetElementVisual(scrim);
                    scrimVisual.StopAnimation(nameof(Visual.Opacity));
                    scrimVisual.Opacity = 1f;
                }

                onCompleted();
                return;
            }

            Compositor compositor = panelVisual.Compositor;
            CubicBezierEasingFunction easing = CreateEasingFunction(compositor, behavior);
            TimeSpan duration = TimeSpan.FromMilliseconds(Math.Max(60, (int)(behavior.OverlayPanelDurationMs * 0.72)));

            CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            if (scrim is not null)
            {
                Visual scrimVisual = ElementCompositionPreview.GetElementVisual(scrim);
                ScalarKeyFrameAnimation scrimOpacity = compositor.CreateScalarKeyFrameAnimation();
                scrimOpacity.InsertKeyFrame(1f, 0f, easing);
                scrimOpacity.Duration = duration;
                scrimVisual.StartAnimation(nameof(Visual.Opacity), scrimOpacity);
            }

            Vector3 endOffset = toRight
                ? new Vector3(behavior.MotionPreset == ThemeMotionPreset.MiuixFolmeSpring ? 44f : 24f, 0f, originalZ)
                : new Vector3(0f, -12f, originalZ);

            Vector3KeyFrameAnimation slideAnim = compositor.CreateVector3KeyFrameAnimation();
            slideAnim.InsertKeyFrame(1f, endOffset, easing);
            slideAnim.Duration = duration;
            panelVisual.StartAnimation("Translation", slideAnim);

            ScalarKeyFrameAnimation panelOpacity = compositor.CreateScalarKeyFrameAnimation();
            panelOpacity.InsertKeyFrame(1f, 0f, easing);
            panelOpacity.Duration = duration;
            panelVisual.StartAnimation(nameof(Visual.Opacity), panelOpacity);

            if (behavior.OverlayScaleFrom < 0.999f && panel is FrameworkElement fe && fe.ActualWidth > 1 && fe.ActualHeight > 1)
            {
                panelVisual.CenterPoint = new Vector3((float)fe.ActualWidth * 0.5f, (float)fe.ActualHeight * 0.5f, 0f);
                Vector3KeyFrameAnimation scaleAnim = compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(1f, new Vector3(behavior.OverlayScaleFrom, behavior.OverlayScaleFrom, 1f), easing);
                scaleAnim.Duration = duration;
                panelVisual.StartAnimation(nameof(Visual.Scale), scaleAnim);
            }

            batch.End();
            batch.Completed += (_, _) =>
            {
                batch.Dispose();
                var dq = panel.DispatcherQueue;
                if (dq is null)
                {
                    return;
                }

                dq.TryEnqueue(() =>
                {
                    if (!IsCurrentAnimationToken(panel, hideToken))
                    {
                        return;
                    }

                    panelVisual.StopAnimation("Translation");
                    panelVisual.StopAnimation(nameof(Visual.Opacity));
                    panelVisual.StopAnimation(nameof(Visual.Scale));
                    panel.Translation = new Vector3(0f, 0f, originalZ);
                    panelVisual.Opacity = 1f;
                    panelVisual.Scale = Vector3.One;
                    if (scrim is not null)
                    {
                        Visual scrimVisual = ElementCompositionPreview.GetElementVisual(scrim);
                        scrimVisual.StopAnimation(nameof(Visual.Opacity));
                        scrimVisual.Opacity = 1f;
                    }

                    onCompleted();
                });
            };
        }
        catch
        {
            if (IsCurrentAnimationToken(panel, hideToken))
            {
                onCompleted();
            }
        }
    }

    internal enum CardPointerAction
    {
        Skip = 0,
        ResetAndClear = 1,
        Animate = 2
    }

    internal sealed class CardPointerState
    {
        public bool IsPressed;
        public bool IsHovered;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, CardPointerState> CardPointerStates = new();

    internal static CardPointerAction ResolveCardPointerAction(CardFeedbackStyle feedbackStyle, bool hasHistoricalState)
    {
        if (feedbackStyle == CardFeedbackStyle.SquircleSink)
        {
            return CardPointerAction.Animate;
        }

        return hasHistoricalState ? CardPointerAction.ResetAndClear : CardPointerAction.Skip;
    }

    internal static bool TryHandleNonSquirclePointer<TKey>(
        ConditionalWeakTable<TKey, CardPointerState> states,
        TKey key,
        CardFeedbackStyle feedbackStyle,
        Action<TKey> onReset)
        where TKey : class
    {
        if (feedbackStyle == CardFeedbackStyle.SquircleSink)
        {
            return false;
        }

        if (states.TryGetValue(key, out _))
        {
            states.Remove(key);
            onReset(key);
        }

        return true;
    }

    public static void HandleCardPointerPressed(FrameworkElement card, bool isPrimaryPress, UiThemeBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(behavior);

        if (TryHandleNonSquirclePointer(CardPointerStates, card, behavior.CardFeedback, static c => ResetCardScaleVisual(c)))
        {
            return;
        }

        CardPointerState state = CardPointerStates.GetOrCreateValue(card);
        state.IsPressed = isPrimaryPress;
        ApplyCardPointerState(card, state.IsPressed, state.IsHovered, behavior);
    }

    public static void HandleCardPointerReleased(FrameworkElement card, UiThemeBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(behavior);

        if (TryHandleNonSquirclePointer(CardPointerStates, card, behavior.CardFeedback, static c => ResetCardScaleVisual(c)))
        {
            return;
        }

        if (CardPointerStates.TryGetValue(card, out CardPointerState? state))
        {
            state.IsPressed = false;
            ApplyCardPointerState(card, state.IsPressed, state.IsHovered, behavior);
        }
        else
        {
            ApplyCardPointerState(card, isPressed: false, isHovered: false, behavior);
        }
    }

    public static void HandleCardPointerEntered(FrameworkElement card, UiThemeBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(behavior);

        if (TryHandleNonSquirclePointer(CardPointerStates, card, behavior.CardFeedback, static c => ResetCardScaleVisual(c)))
        {
            return;
        }

        CardPointerState state = CardPointerStates.GetOrCreateValue(card);
        state.IsHovered = true;
        ApplyCardPointerState(card, state.IsPressed, state.IsHovered, behavior);
    }

    public static void HandleCardPointerExited(FrameworkElement card, UiThemeBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(behavior);

        if (TryHandleNonSquirclePointer(CardPointerStates, card, behavior.CardFeedback, static c => ResetCardScaleVisual(c)))
        {
            return;
        }

        if (CardPointerStates.TryGetValue(card, out CardPointerState? state))
        {
            state.IsHovered = false;
            state.IsPressed = false;
        }

        ApplyCardPointerState(card, isPressed: false, isHovered: false, behavior);
    }

    public static void ApplyCardPointerState(
        FrameworkElement card,
        bool isPressed,
        bool isHovered,
        UiThemeBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(behavior);

        CardPointerAction action = ResolveCardPointerAction(
            behavior.CardFeedback,
            CardPointerStates.TryGetValue(card, out _));
        if (action == CardPointerAction.Skip)
        {
            return;
        }

        if (action == CardPointerAction.ResetAndClear)
        {
            CardPointerStates.Remove(card);
            ResetCardScaleVisual(card);
            return;
        }

        if (!IsAnimationsEnabled)
        {
            ResetCardScaleVisual(card);
            return;
        }

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(card);
            Compositor compositor = visual.Compositor;

            if (card.ActualWidth > 1 && card.ActualHeight > 1)
            {
                visual.CenterPoint = new Vector3((float)card.ActualWidth * 0.5f, (float)card.ActualHeight * 0.5f, 0f);
            }

            float targetScale = isPressed
                ? behavior.CardPressScale
                : isHovered
                    ? behavior.CardHoverScale
                    : 1.0f;

            SpringVector3NaturalMotionAnimation spring = compositor.CreateSpringVector3Animation();
            spring.FinalValue = new Vector3(targetScale, targetScale, 1f);
            spring.DampingRatio = isPressed ? 0.78f : 0.65f;
            spring.Period = TimeSpan.FromMilliseconds(isPressed ? 36 : 52);
            visual.StartAnimation(nameof(Visual.Scale), spring);
        }
        catch
        {
            // Ignore when visual is not yet realized.
        }
    }

    private static void ResetCardScaleVisual(FrameworkElement card)
    {
        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(card);
            visual.StopAnimation(nameof(Visual.Scale));
            visual.Scale = Vector3.One;
        }
        catch
        {
            // Ignore when visual is not yet realized.
        }
    }

    public static void AnimateElementScale(
        UIElement element,
        float targetScale,
        UiThemeBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(behavior);

        _ = NextAnimationToken(element);
        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            SetCenteredAnchor(visual, element);

            if (!IsAnimationsEnabled)
            {
                visual.StopAnimation(nameof(Visual.Scale));
                visual.Scale = new Vector3(targetScale, targetScale, 1f);
                return;
            }

            Compositor compositor = visual.Compositor;

            if (behavior.SliderThumbUsesSpringOvershoot)
            {
                SpringVector3NaturalMotionAnimation spring = compositor.CreateSpringVector3Animation();
                spring.FinalValue = new Vector3(targetScale, targetScale, 1f);
                spring.DampingRatio = 0.62f;
                spring.Period = TimeSpan.FromMilliseconds(45);
                visual.StartAnimation(nameof(Visual.Scale), spring);
            }
            else
            {
                CubicBezierEasingFunction easing = CreateEasingFunction(compositor, behavior);
                Vector3KeyFrameAnimation anim = compositor.CreateVector3KeyFrameAnimation();
                anim.InsertKeyFrame(1f, new Vector3(targetScale, targetScale, 1f), easing);
                anim.Duration = TimeSpan.FromMilliseconds(Math.Max(60, behavior.SliderThumbDurationMs));
                visual.StartAnimation(nameof(Visual.Scale), anim);
            }
        }
        catch
        {
            // Best-effort scale animation.
        }
    }

    public static void SetSliderThumbPositionImmediate(UIElement thumb, double targetX)
    {
        ArgumentNullException.ThrowIfNull(thumb);

        _ = NextAnimationToken(thumb);
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(thumb, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(thumb);
            visual.StopAnimation("Translation.X");
            visual.StopAnimation("Translation");
            thumb.Translation = new Vector3((float)targetX, 0f, thumb.Translation.Z);
        }
        catch
        {
            // Ignore when host visual tree is unavailable.
        }
    }

    public static void AnimateSliderThumbPosition(
        UIElement thumb,
        double targetX,
        UiThemeBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(thumb);
        ArgumentNullException.ThrowIfNull(behavior);

        if (!IsAnimationsEnabled)
        {
            SetSliderThumbPositionImmediate(thumb, targetX);
            return;
        }

        int token = NextAnimationToken(thumb);
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(thumb, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(thumb);
            Compositor compositor = visual.Compositor;
            float originalZ = thumb.Translation.Z;
            float target = (float)targetX;

            CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            if (behavior.MotionPreset == ThemeMotionPreset.MiuixFolmeSpring)
            {
                SpringScalarNaturalMotionAnimation spring = compositor.CreateSpringScalarAnimation();
                spring.FinalValue = target;
                spring.DampingRatio = behavior.SliderThumbUsesSpringOvershoot ? 0.62f : 0.80f;
                spring.Period = TimeSpan.FromMilliseconds(48);
                visual.StartAnimation("Translation.X", spring);
            }
            else
            {
                CubicBezierEasingFunction easing = CreateEasingFunction(compositor, behavior);
                ScalarKeyFrameAnimation anim = compositor.CreateScalarKeyFrameAnimation();
                anim.InsertKeyFrame(1f, target, easing);
                anim.Duration = TimeSpan.FromMilliseconds(Math.Max(60, behavior.SliderThumbDurationMs));
                visual.StartAnimation("Translation.X", anim);
            }

            batch.End();
            batch.Completed += (_, _) =>
            {
                batch.Dispose();
                var dq = thumb.DispatcherQueue;
                if (dq is null)
                {
                    return;
                }

                dq.TryEnqueue(() =>
                {
                    if (!IsCurrentAnimationToken(thumb, token))
                    {
                        return;
                    }

                    visual.StopAnimation("Translation.X");
                    thumb.Translation = new Vector3(target, 0f, originalZ);
                });
            };
        }
        catch
        {
            // Ignore when host visual tree is unavailable.
        }
    }

    public static float GetCurrentTranslationX(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            if (visual.Properties.TryGetVector3("Translation", out Vector3 vec) == CompositionGetValueStatus.Succeeded)
            {
                return vec.X;
            }

            return element.Translation.X;
        }
        catch
        {
            return 0f;
        }
    }

    public static void AnimateBarSlideShow(UIElement element, float fromOffsetY, int durationMs = 200)
    {
        ArgumentNullException.ThrowIfNull(element);

        _ = NextAnimationToken(element);
        element.Visibility = Visibility.Visible;
        element.IsHitTestVisible = true;

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            float originalZ = element.Translation.Z;

            if (!IsAnimationsEnabled)
            {
                visual.StopAnimation("Translation");
                visual.StopAnimation("Translation.Y");
                visual.StopAnimation(nameof(Visual.Opacity));
                element.Translation = new Vector3(0f, 0f, originalZ);
                visual.Opacity = 1f;
                return;
            }

            Compositor compositor = visual.Compositor;
            CubicBezierEasingFunction easing = CreateEasingFunction(compositor, MaterialThemeService.ActiveBehavior);
            TimeSpan duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMs));

            Vector3KeyFrameAnimation slideAnim = compositor.CreateVector3KeyFrameAnimation();
            slideAnim.InsertKeyFrame(0f, new Vector3(0f, fromOffsetY, originalZ));
            slideAnim.InsertKeyFrame(1f, new Vector3(0f, 0f, originalZ), easing);
            slideAnim.Duration = duration;
            visual.StartAnimation("Translation", slideAnim);

            ScalarKeyFrameAnimation opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(0f, ResolveResumeOpacity(visual));
            opacityAnim.InsertKeyFrame(1f, 1f, easing);
            opacityAnim.Duration = duration;
            visual.StartAnimation(nameof(Visual.Opacity), opacityAnim);
        }
        catch
        {
            // Ignore when host visual tree is unavailable.
        }
    }

    public static void AnimateBarSlideHide(
        UIElement element,
        float toOffsetY,
        int durationMs = 160,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(element);

        int hideToken = NextAnimationToken(element);
        element.IsHitTestVisible = false;

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            float originalZ = element.Translation.Z;

            if (!IsAnimationsEnabled)
            {
                visual.StopAnimation("Translation");
                visual.StopAnimation("Translation.Y");
                visual.StopAnimation(nameof(Visual.Opacity));
                element.Translation = new Vector3(0f, 0f, originalZ);
                visual.Opacity = 1f;
                element.Visibility = Visibility.Collapsed;
                element.IsHitTestVisible = true;
                onCompleted?.Invoke();
                return;
            }

            Compositor compositor = visual.Compositor;
            CubicBezierEasingFunction easing = CreateEasingFunction(compositor, MaterialThemeService.ActiveBehavior);
            TimeSpan duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMs));

            CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            Vector3KeyFrameAnimation slideAnim = compositor.CreateVector3KeyFrameAnimation();
            slideAnim.InsertKeyFrame(1f, new Vector3(0f, toOffsetY, originalZ), easing);
            slideAnim.Duration = duration;
            visual.StartAnimation("Translation", slideAnim);

            ScalarKeyFrameAnimation opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(1f, 0f, easing);
            opacityAnim.Duration = duration;
            visual.StartAnimation(nameof(Visual.Opacity), opacityAnim);

            batch.End();
            batch.Completed += (_, _) =>
            {
                batch.Dispose();
                var dq = element.DispatcherQueue;
                if (dq is null)
                {
                    return;
                }

                dq.TryEnqueue(() =>
                {
                    if (!IsCurrentAnimationToken(element, hideToken))
                    {
                        return;
                    }

                    visual.StopAnimation("Translation");
                    visual.StopAnimation(nameof(Visual.Opacity));
                    element.Translation = new Vector3(0f, 0f, originalZ);
                    visual.Opacity = 1f;
                    element.Visibility = Visibility.Collapsed;
                    element.IsHitTestVisible = true;
                    onCompleted?.Invoke();
                });
            };
        }
        catch
        {
            if (IsCurrentAnimationToken(element, hideToken))
            {
                element.Visibility = Visibility.Collapsed;
                element.IsHitTestVisible = true;
                onCompleted?.Invoke();
            }
        }
    }

    public static void AnimatePopoverShow(
        UIElement element,
        float fromOffsetY = 8f,
        float fromScale = 0.96f,
        int durationMs = 170)
    {
        ArgumentNullException.ThrowIfNull(element);

        _ = NextAnimationToken(element);
        element.Visibility = Visibility.Visible;

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            float originalZ = element.Translation.Z;

            if (!IsAnimationsEnabled)
            {
                visual.StopAnimation("Translation");
                visual.StopAnimation("Translation.Y");
                visual.StopAnimation(nameof(Visual.Opacity));
                visual.StopAnimation(nameof(Visual.Scale));
                element.Translation = new Vector3(0f, 0f, originalZ);
                visual.Opacity = 1f;
                visual.Scale = Vector3.One;
                return;
            }

            Compositor compositor = visual.Compositor;
            CubicBezierEasingFunction easing = CreateEasingFunction(compositor, MaterialThemeService.ActiveBehavior);
            TimeSpan duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMs));

            Vector3KeyFrameAnimation slideAnim = compositor.CreateVector3KeyFrameAnimation();
            slideAnim.InsertKeyFrame(0f, new Vector3(0f, fromOffsetY, originalZ));
            slideAnim.InsertKeyFrame(1f, new Vector3(0f, 0f, originalZ), easing);
            slideAnim.Duration = duration;
            visual.StartAnimation("Translation", slideAnim);

            ScalarKeyFrameAnimation opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(0f, ResolveResumeOpacity(visual));
            opacityAnim.InsertKeyFrame(1f, 1f, easing);
            opacityAnim.Duration = duration;
            visual.StartAnimation(nameof(Visual.Opacity), opacityAnim);

            if (fromScale < 0.999f)
            {
                SetCenteredAnchor(visual, element);
                Vector3KeyFrameAnimation scaleAnim = compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(0f, new Vector3(fromScale, fromScale, 1f));
                scaleAnim.InsertKeyFrame(1f, Vector3.One, easing);
                scaleAnim.Duration = duration;
                visual.StartAnimation(nameof(Visual.Scale), scaleAnim);
            }
            else
            {
                visual.StopAnimation(nameof(Visual.Scale));
                visual.Scale = Vector3.One;
            }
        }
        catch
        {
            // Ignore when host visual tree is unavailable.
        }
    }

    public static void AnimatePopoverHide(
        UIElement element,
        float toOffsetY = 6f,
        float toScale = 0.96f,
        int durationMs = 130,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(element);

        int hideToken = NextAnimationToken(element);
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            float originalZ = element.Translation.Z;

            if (!IsAnimationsEnabled)
            {
                visual.StopAnimation("Translation");
                visual.StopAnimation("Translation.Y");
                visual.StopAnimation(nameof(Visual.Opacity));
                visual.StopAnimation(nameof(Visual.Scale));
                element.Translation = new Vector3(0f, 0f, originalZ);
                visual.Opacity = 1f;
                visual.Scale = Vector3.One;
                element.Visibility = Visibility.Collapsed;
                onCompleted?.Invoke();
                return;
            }

            Compositor compositor = visual.Compositor;
            CubicBezierEasingFunction easing = CreateEasingFunction(compositor, MaterialThemeService.ActiveBehavior);
            TimeSpan duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMs));

            CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            Vector3KeyFrameAnimation slideAnim = compositor.CreateVector3KeyFrameAnimation();
            slideAnim.InsertKeyFrame(1f, new Vector3(0f, toOffsetY, originalZ), easing);
            slideAnim.Duration = duration;
            visual.StartAnimation("Translation", slideAnim);

            ScalarKeyFrameAnimation opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(1f, 0f, easing);
            opacityAnim.Duration = duration;
            visual.StartAnimation(nameof(Visual.Opacity), opacityAnim);

            if (toScale < 0.999f)
            {
                SetCenteredAnchor(visual, element);
                Vector3KeyFrameAnimation scaleAnim = compositor.CreateVector3KeyFrameAnimation();
                scaleAnim.InsertKeyFrame(1f, new Vector3(toScale, toScale, 1f), easing);
                scaleAnim.Duration = duration;
                visual.StartAnimation(nameof(Visual.Scale), scaleAnim);
            }

            batch.End();
            batch.Completed += (_, _) =>
            {
                batch.Dispose();
                var dq = element.DispatcherQueue;
                if (dq is null)
                {
                    return;
                }

                dq.TryEnqueue(() =>
                {
                    if (!IsCurrentAnimationToken(element, hideToken))
                    {
                        return;
                    }

                    visual.StopAnimation("Translation");
                    visual.StopAnimation(nameof(Visual.Opacity));
                    visual.StopAnimation(nameof(Visual.Scale));
                    element.Translation = new Vector3(0f, 0f, originalZ);
                    visual.Opacity = 1f;
                    visual.Scale = Vector3.One;
                    element.Visibility = Visibility.Collapsed;
                    onCompleted?.Invoke();
                });
            };
        }
        catch
        {
            if (IsCurrentAnimationToken(element, hideToken))
            {
                element.Visibility = Visibility.Collapsed;
                onCompleted?.Invoke();
            }
        }
    }

    public static void AnimateScrimOpacity(
        UIElement scrim,
        bool show,
        int durationMs = 180,
        Action? onHidden = null)
    {
        ArgumentNullException.ThrowIfNull(scrim);

        int token = NextAnimationToken(scrim);
        if (show)
        {
            scrim.Visibility = Visibility.Visible;
            scrim.IsHitTestVisible = true;

            try
            {
                Visual visual = ElementCompositionPreview.GetElementVisual(scrim);
                if (!IsAnimationsEnabled)
                {
                    visual.StopAnimation(nameof(Visual.Opacity));
                    visual.Opacity = 1f;
                    return;
                }

                Compositor compositor = visual.Compositor;
                CubicBezierEasingFunction easing = CreateEasingFunction(compositor, MaterialThemeService.ActiveBehavior);
                ScalarKeyFrameAnimation opacityAnim = compositor.CreateScalarKeyFrameAnimation();
                opacityAnim.InsertKeyFrame(0f, ResolveResumeOpacity(visual));
                opacityAnim.InsertKeyFrame(1f, 1f, easing);
                opacityAnim.Duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMs));
                visual.StartAnimation(nameof(Visual.Opacity), opacityAnim);
            }
            catch
            {
                // Ignore when host visual tree is unavailable.
            }

            return;
        }

        scrim.IsHitTestVisible = false;
        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(scrim);
            if (!IsAnimationsEnabled)
            {
                visual.StopAnimation(nameof(Visual.Opacity));
                visual.Opacity = 1f;
                scrim.Visibility = Visibility.Collapsed;
                scrim.IsHitTestVisible = true;
                onHidden?.Invoke();
                return;
            }

            Compositor compositor = visual.Compositor;
            CubicBezierEasingFunction easing = CreateEasingFunction(compositor, MaterialThemeService.ActiveBehavior);
            CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            ScalarKeyFrameAnimation opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(1f, 0f, easing);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMs));
            visual.StartAnimation(nameof(Visual.Opacity), opacityAnim);

            batch.End();
            batch.Completed += (_, _) =>
            {
                batch.Dispose();
                var dq = scrim.DispatcherQueue;
                if (dq is null)
                {
                    return;
                }

                dq.TryEnqueue(() =>
                {
                    if (!IsCurrentAnimationToken(scrim, token))
                    {
                        return;
                    }

                    visual.StopAnimation(nameof(Visual.Opacity));
                    visual.Opacity = 1f;
                    scrim.Visibility = Visibility.Collapsed;
                    scrim.IsHitTestVisible = true;
                    onHidden?.Invoke();
                });
            };
        }
        catch
        {
            if (IsCurrentAnimationToken(scrim, token))
            {
                scrim.Visibility = Visibility.Collapsed;
                scrim.IsHitTestVisible = true;
                onHidden?.Invoke();
            }
        }
    }

    public static void AnimateRotation(UIElement element, float toAngleDegrees, int durationMs = 200)
    {
        ArgumentNullException.ThrowIfNull(element);

        _ = NextAnimationToken(element);
        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            SetCenteredAnchor(visual, element, fallbackCenter: 12f);

            if (!IsAnimationsEnabled)
            {
                visual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
                visual.RotationAngleInDegrees = toAngleDegrees;
                return;
            }

            UiThemeBehavior behavior = MaterialThemeService.ActiveBehavior;
            Compositor compositor = visual.Compositor;

            if (behavior.MotionPreset == ThemeMotionPreset.MiuixFolmeSpring)
            {
                SpringScalarNaturalMotionAnimation spring = compositor.CreateSpringScalarAnimation();
                spring.FinalValue = toAngleDegrees;
                spring.DampingRatio = 0.68f;
                spring.Period = TimeSpan.FromMilliseconds(46);
                visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), spring);
            }
            else
            {
                CubicBezierEasingFunction easing = CreateEasingFunction(compositor, behavior);
                ScalarKeyFrameAnimation anim = compositor.CreateScalarKeyFrameAnimation();
                anim.InsertKeyFrame(1f, toAngleDegrees, easing);
                anim.Duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMs));
                visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), anim);
            }
        }
        catch
        {
            // Ignore when host visual tree is unavailable.
        }
    }

    public static void AnimateStaggeredShow(
        IReadOnlyList<UIElement> elements,
        float fromOffsetY = 14f,
        int staggerMs = 28,
        int durationMs = 180)
    {
        ArgumentNullException.ThrowIfNull(elements);
        if (elements.Count == 0)
        {
            return;
        }

        if (!IsAnimationsEnabled)
        {
            for (int i = 0; i < elements.Count; i++)
            {
                UIElement el = elements[i];
                if (el is null)
                {
                    continue;
                }

                _ = NextAnimationToken(el);
                el.Visibility = Visibility.Visible;
                try
                {
                    ElementCompositionPreview.SetIsTranslationEnabled(el, true);
                    Visual v = ElementCompositionPreview.GetElementVisual(el);
                    v.StopAnimation("Translation");
                    v.StopAnimation("Translation.Y");
                    v.StopAnimation(nameof(Visual.Opacity));
                    el.Translation = new Vector3(0f, 0f, el.Translation.Z);
                    v.Opacity = 1f;
                }
                catch
                {
                }
            }

            return;
        }

        UiThemeBehavior behavior = MaterialThemeService.ActiveBehavior;
        TimeSpan duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMs));

        for (int i = 0; i < elements.Count; i++)
        {
            UIElement el = elements[i];
            if (el is null)
            {
                continue;
            }

            _ = NextAnimationToken(el);
            el.Visibility = Visibility.Visible;

            try
            {
                ElementCompositionPreview.SetIsTranslationEnabled(el, true);
                Visual v = ElementCompositionPreview.GetElementVisual(el);
                Compositor compositor = v.Compositor;
                CubicBezierEasingFunction easing = CreateEasingFunction(compositor, behavior);
                float z = el.Translation.Z;
                TimeSpan delay = TimeSpan.FromMilliseconds(Math.Max(0, i * staggerMs));

                Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
                slide.InsertKeyFrame(0f, new Vector3(0f, fromOffsetY, z));
                slide.InsertKeyFrame(1f, new Vector3(0f, 0f, z), easing);
                slide.Duration = duration;
                slide.DelayTime = delay;
                slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                v.StartAnimation("Translation", slide);

                ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(0f, 0f);
                fade.InsertKeyFrame(1f, 1f, easing);
                fade.Duration = duration;
                fade.DelayTime = delay;
                fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                v.StartAnimation(nameof(Visual.Opacity), fade);
            }
            catch
            {
            }
        }
    }
}

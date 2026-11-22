using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EhViewer.Desktop.Services.Theming;

/// <summary>
/// Attaches dynamic theme geometry (<see cref="UiThemeMetrics"/>) to XAML <see cref="Border"/> and
/// <see cref="Control"/> instances so CornerRadius values update live when switching UI design themes
/// without reloading pages or duplicating DataTemplates.
/// </summary>
public static class ThemeMetricsBinder
{
    public const string RoleCard = "Card";
    public const string RoleCardInner = "CardInner";
    public const string RoleCardInnerLeft = "CardInnerLeft";
    public const string RoleFloating = "Floating";
    public const string RoleSearchBar = "SearchBar";
    public const string RoleNavItem = "NavItem";
    public const string RoleButton = "Button";

    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
        "Role",
        typeof(string),
        typeof(ThemeMetricsBinder),
        new PropertyMetadata(null, OnRoleChanged));

    internal static UiThemeMetrics? LastAppliedMetrics => RefreshState.LastAppliedMetrics;
    internal static long RefreshExecutionCount => RefreshState.RefreshExecutionCount;

    internal static void ResetRefreshTrackingForTests() => RefreshState.Reset();

    public static string? GetRole(DependencyObject obj) => (string?)obj.GetValue(RoleProperty);

    public static void SetRole(DependencyObject obj, string? value) => obj.SetValue(RoleProperty, value);

    public static void RefreshAll(UiThemeMetrics metrics) => RefreshState.RefreshAll(metrics);

    private static void OnRoleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        string? role = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(role))
        {
            RefreshState.Remove(d);
            return;
        }

        RefreshState.Set(d, role);
        ApplyMetric(d, role, MaterialThemeService.ActiveMetrics);
    }

    // Kept in a nested type without DependencyProperty fields so headless tests calling RefreshAll
    // never trigger DependencyProperty.RegisterAttached when no WinUI XAML runtime is active.
    private static class RefreshState
    {
        private static readonly ConditionalWeakTable<DependencyObject, RoleRegistration> Tracked = new();
        private static UiThemeMetrics? _lastAppliedMetrics;
        private static long _refreshExecutionCount;

        public static UiThemeMetrics? LastAppliedMetrics => _lastAppliedMetrics;
        public static long RefreshExecutionCount => Interlocked.Read(ref _refreshExecutionCount);

        public static void Reset()
        {
            _lastAppliedMetrics = null;
            Interlocked.Exchange(ref _refreshExecutionCount, 0);
        }

        public static void Set(DependencyObject d, string role)
        {
            RoleRegistration reg = Tracked.GetOrCreateValue(d);
            reg.Role = role;
        }

        public static void Remove(DependencyObject d) => Tracked.Remove(d);

        public static void RefreshAll(UiThemeMetrics metrics)
        {
            ArgumentNullException.ThrowIfNull(metrics);
            if (Equals(_lastAppliedMetrics, metrics))
            {
                return;
            }

            _lastAppliedMetrics = metrics;
            Interlocked.Increment(ref _refreshExecutionCount);

            foreach (KeyValuePair<DependencyObject, RoleRegistration> entry in Tracked)
            {
                ApplyMetric(entry.Key, entry.Value.Role, metrics);
            }
        }
    }

    private static void ApplyMetric(DependencyObject target, string? role, UiThemeMetrics metrics)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return;
        }

        try
        {
            if (target.DispatcherQueue is { } dq && !dq.HasThreadAccess)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        CornerRadius radius = ResolveCornerRadius(role, metrics);
        switch (target)
        {
            case Border border:
                border.CornerRadius = radius;
                break;
            case Control control:
                control.CornerRadius = radius;
                break;
        }
    }

    internal static CornerRadius ResolveCornerRadius(string role, UiThemeMetrics metrics) => role switch
    {
        RoleCard => new CornerRadius(metrics.CardCornerRadius),
        RoleCardInner => new CornerRadius(ComputeConcentricInnerRadius(metrics)),
        RoleCardInnerLeft => new CornerRadius(ComputeFlushInnerRadius(metrics), 0, 0, ComputeFlushInnerRadius(metrics)),
        RoleFloating => new CornerRadius(metrics.FloatingCornerRadius),
        RoleSearchBar => new CornerRadius(metrics.SearchBarCornerRadius),
        RoleNavItem => new CornerRadius(metrics.NavItemCornerRadius),
        RoleButton => new CornerRadius(metrics.ButtonCornerRadius),
        _ => new CornerRadius(metrics.CardCornerRadius)
    };

    private static double ComputeFlushInnerRadius(UiThemeMetrics metrics)
        => Math.Max(0.0, metrics.CardCornerRadius - metrics.CardBorderThickness);

    private static double ComputeConcentricInnerRadius(UiThemeMetrics metrics)
        => metrics.CardCornerRadius >= 12.0
            ? Math.Max(4.0, metrics.CardCornerRadius - 5.0)
            : ComputeFlushInnerRadius(metrics);

    private sealed class RoleRegistration
    {
        public string? Role { get; set; }
    }
}

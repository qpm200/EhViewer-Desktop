using EhViewer.Core.Client;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EhViewer.Desktop.Views;

/// <summary>
/// Shared static x:Bind helpers for WinUI 3 templates and pages.
/// Every visibility helper returns <see cref="Visibility"/> directly so compiled XAML bindings
/// never trigger WMC1506 on boolean values.
/// </summary>
public static class UiHelpers
{
    private static readonly Dictionary<int, SolidColorBrush> ColorBrushCache = [];
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);

    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfNotEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility CollapsedIfNotEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfPositive(int value)
        => value > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleWhenZero(int count)
        => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleWhenEmpty(int count, bool isLoading)
        => !isLoading && count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static SolidColorBrush CategoryBrush(int category)
        => FromArgb(EhUtils.GetCategoryColor(category));

    public static SolidColorBrush FromArgb(int argb)
    {
        if (ColorBrushCache.TryGetValue(argb, out SolidColorBrush? cached))
        {
            return cached;
        }

        var color = Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));

        var brush = new SolidColorBrush(color);
        ColorBrushCache[argb] = brush;
        return brush;
    }

    public static SolidColorBrush FromUintArgb(uint argb) => FromArgb(unchecked((int)argb));

    public static SolidColorBrush NavRowBackground(bool selected)
        => selected
            ? (Application.Current?.Resources["EhNavSelectedBackgroundBrush"] as SolidColorBrush ?? TransparentBrush)
            : TransparentBrush;
}

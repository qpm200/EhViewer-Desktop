using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EhViewer.Desktop.Views;

/// <summary>
/// Helpers that translate the Android <c>RecyclerView.Adapter</c> contract onto
/// <see cref="ItemsRepeater"/>.
/// </summary>
/// <remarks>
/// <see cref="ItemsRepeater"/> does <b>not</b> assign the item to the template element's
/// <c>DataContext</c>: <c>x:Bind</c> data templates are compiled against the item type and never
/// populate <c>DataContext</c>. Code-behind handlers attached inside such a template therefore see
/// a null <c>DataContext</c>. The equivalent of <c>onBindViewHolder(holder, position)</c> is
/// <see cref="ItemsRepeater.ElementPrepared"/>, which supplies the realized element and its index;
/// the item must then be resolved from the repeater's <c>ItemsSource</c>.
/// </remarks>
internal static class RepeaterSupport
{
    private static readonly ConditionalWeakTable<DependencyObject, Border> CachedThumbHosts = new();
    private static readonly ConditionalWeakTable<Border, CancellationTokenSource> ActiveBorderLoadCts = new();

    /// <summary>Resolves the item bound at <paramref name="index"/>, or null when out of range.</summary>
    public static TItem? ItemAt<TItem>(ItemsRepeater? repeater, int index)
        where TItem : class
    {
        if (repeater?.ItemsSource is IReadOnlyList<TItem> list && index >= 0 && index < list.Count)
        {
            return list[index];
        }

        return null;
    }

    /// <summary>Depth-first search for the first descendant of the requested type.</summary>
    public static T? FindDescendant<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        int count;
        try
        {
            count = VisualTreeHelper.GetChildrenCount(root);
        }
        catch (Exception)
        {
            // A element that is not yet in the live tree has no children.
            return null;
        }

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// Cached lookup for the realized card's thumbnail <see cref="Border"/> (<c>ThumbHost</c>),
    /// avoiding repeated recursive visual-tree walks on every virtualized recycle.
    /// </summary>
    public static Border? FindThumbHost(DependencyObject? root, string name = "ThumbHost")
    {
        if (root is null)
        {
            return null;
        }

        if (string.Equals(name, "ThumbHost", StringComparison.Ordinal) &&
            CachedThumbHosts.TryGetValue(root, out Border? cached))
        {
            return cached;
        }

        Border? found = FindDescendantByName<Border>(root, name);
        if (found is not null && string.Equals(name, "ThumbHost", StringComparison.Ordinal))
        {
            CachedThumbHosts.AddOrUpdate(root, found);
        }

        return found;
    }

    /// <summary>
    /// Cancels any in-flight thumbnail load bound to <paramref name="target"/> when a virtualized
    /// card is recycled out of view.
    /// </summary>
    public static void CancelBorderImage(Border? target)
    {
        if (target is null)
        {
            return;
        }

        if (ActiveBorderLoadCts.TryGetValue(target, out CancellationTokenSource? prevCts))
        {
            ActiveBorderLoadCts.Remove(target);
            try
            {
                prevCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            prevCts.Dispose();
        }

        target.Tag = null;
        target.Background = null;
    }

    /// <summary>
    /// Loads a thumbnail into a <see cref="Border"/> background as an <see cref="ImageBrush"/> so
    /// WinUI 3 respects <see cref="Border.CornerRadius"/> clipping on every corner.
    /// </summary>
    public static Task SetBorderImageAsync<TSource>(Border? target, object token, Func<Task<TSource?>> load)
        where TSource : ImageSource
        => SetBorderImageAsync(target, token, _ => load());

    /// <summary>
    /// Cancellable variant of <see cref="SetBorderImageAsync{TSource}(Border?, object, Func{Task{TSource?}})"/>
    /// that cancels any previously in-flight thumbnail load on the same recycled container.
    /// </summary>
    public static async Task SetBorderImageAsync<TSource>(
        Border? target,
        object token,
        Func<CancellationToken, Task<TSource?>> load)
        where TSource : ImageSource
    {
        if (target is null)
        {
            return;
        }

        CancelBorderImage(target);
        target.Tag = token;

        var cts = new CancellationTokenSource();
        ActiveBorderLoadCts.AddOrUpdate(target, cts);
        CancellationToken ct = cts.Token;

        TSource? source;
        try
        {
            source = await load(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            return;
        }
        finally
        {
            if (ActiveBorderLoadCts.TryGetValue(target, out CancellationTokenSource? current) && ReferenceEquals(current, cts))
            {
                ActiveBorderLoadCts.Remove(target);
            }

            cts.Dispose();
        }

        if (source is not null && !ct.IsCancellationRequested && ReferenceEquals(target.Tag, token))
        {
            if (target.Background is ImageBrush existingBrush)
            {
                existingBrush.ImageSource = source;
            }
            else
            {
                target.Background = new ImageBrush
                {
                    ImageSource = source,
                    Stretch = Stretch.UniformToFill
                };
            }
        }
    }

    /// <summary>Finds a named child element in the visual subtree.</summary>
    public static T? FindDescendantByName<T>(DependencyObject? root, string name)
        where T : FrameworkElement
    {
        if (root is null)
        {
            return null;
        }

        int count;
        try
        {
            count = VisualTreeHelper.GetChildrenCount(root);
        }
        catch (Exception)
        {
            return null;
        }

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && string.Equals(match.Name, name, StringComparison.Ordinal))
            {
                return match;
            }

            if (FindDescendantByName<T>(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// Loads a thumbnail into the card's <see cref="Image"/> without letting a recycled container
    /// show a late result meant for a different gallery.
    /// </summary>
    public static async Task SetImageAsync<TSource>(Image? image, object token, Func<Task<TSource?>> load)
        where TSource : ImageSource
    {
        if (image is null)
        {
            return;
        }

        image.Tag = token;

        TSource? source;
        try
        {
            source = await load().ConfigureAwait(true);
        }
        catch (Exception)
        {
            return;
        }

        if (source is not null && ReferenceEquals(image.Tag, token))
        {
            image.Source = source;
        }
    }
}

using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Pure-logic bounds clamping and WinUI <see cref="AppWindow"/> save/restore helper so window
/// geometry stays visible across DPI or monitor unplug changes.
/// </summary>
public static class WindowPlacementHelper
{
    public const int MinWidth = 760;
    public const int MinHeight = 520;

    public static RectInt32 ClampToWorkArea(RectInt32 desired, RectInt32 workArea)
    {
        int width = Math.Clamp(desired.Width, MinWidth, Math.Max(MinWidth, workArea.Width));
        int height = Math.Clamp(desired.Height, MinHeight, Math.Max(MinHeight, workArea.Height));

        int maxX = workArea.X + Math.Max(0, workArea.Width - width);
        int maxY = workArea.Y + Math.Max(0, workArea.Height - height);
        int x = Math.Clamp(desired.X, workArea.X, maxX);
        int y = Math.Clamp(desired.Y, workArea.Y, maxY);

        return new RectInt32(x, y, width, height);
    }

    public static void Restore(AppWindow appWindow, DesktopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(appWindow);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.RememberWindowPlacement)
        {
            return;
        }

        if (settings.SavedWindowX is int x &&
            settings.SavedWindowY is int y &&
            settings.SavedWindowWidth is int w &&
            settings.SavedWindowHeight is int h &&
            w >= MinWidth &&
            h >= MinHeight)
        {
            var desired = new RectInt32(x, y, w, h);
            DisplayArea? display = DisplayArea.GetFromRect(desired, DisplayAreaFallback.Primary);
            if (display is not null)
            {
                RectInt32 clamped = ClampToWorkArea(desired, display.WorkArea);
                appWindow.MoveAndResize(clamped);
            }
        }

        if (settings.SavedWindowMaximized && appWindow.Presenter is OverlappedPresenter overlapped)
        {
            overlapped.Maximize();
        }
    }

    public static void Capture(AppWindow appWindow, DesktopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(appWindow);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.RememberWindowPlacement)
        {
            return;
        }

        bool isMaximized = appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        settings.SavedWindowMaximized = isMaximized;

        if (!isMaximized)
        {
            PointInt32 pos = appWindow.Position;
            SizeInt32 size = appWindow.Size;
            if (size.Width >= MinWidth && size.Height >= MinHeight)
            {
                settings.SavedWindowX = pos.X;
                settings.SavedWindowY = pos.Y;
                settings.SavedWindowWidth = size.Width;
                settings.SavedWindowHeight = size.Height;
            }
        }
    }
}

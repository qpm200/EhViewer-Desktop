using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Windows appearance integration: Mica backdrop on Windows 11 with a graceful fallback, plus
/// per-window dark/light theme selection. Everything is best-effort — a missing backdrop must never
/// stop the window from showing.
/// </summary>
public static class WindowAppearance
{
    internal static bool ShouldRecreateBackdrop(
        bool hasMica,
        bool hasAcrylic,
        bool wantMica,
        bool currentMicaBaseAlt,
        bool wantMicaBaseAlt)
    {
        if (wantMica)
        {
            return !(hasMica && currentMicaBaseAlt == wantMicaBaseAlt);
        }

        return !hasAcrylic;
    }

    /// <summary>
    /// Applies the Mica (or Acrylic fallback) backdrop when supported. Returns the backdrop actually
    /// applied, or <see langword="null"/> when the system does not support one.
    /// </summary>
    public static string? TryApplyBackdrop(
        Window window,
        uint titleBarColor,
        bool useDarkTheme,
        bool applyTitleBarColors = true)
    {
        ArgumentNullException.ThrowIfNull(window);

        try
        {
            SystemBackdrop? current = window.SystemBackdrop;
            bool hasMica = current is MicaBackdrop;
            bool hasAcrylic = current is DesktopAcrylicBackdrop;
            bool currentMicaBaseAlt = current is MicaBackdrop { Kind: MicaKind.BaseAlt };

            if (MicaController.IsSupported())
            {
                if (ShouldRecreateBackdrop(
                    hasMica,
                    hasAcrylic,
                    wantMica: true,
                    currentMicaBaseAlt,
                    wantMicaBaseAlt: useDarkTheme))
                {
                    window.SystemBackdrop = new MicaBackdrop
                    {
                        Kind = useDarkTheme ? MicaKind.BaseAlt : MicaKind.Base
                    };
                }

                if (applyTitleBarColors)
                {
                    ApplyTitleBarColors(window, titleBarColor, useDarkTheme);
                }

                return "Mica";
            }

            if (DesktopAcrylicController.IsSupported())
            {
                if (ShouldRecreateBackdrop(
                    hasMica,
                    hasAcrylic,
                    wantMica: false,
                    currentMicaBaseAlt,
                    wantMicaBaseAlt: useDarkTheme))
                {
                    window.SystemBackdrop = new DesktopAcrylicBackdrop();
                }

                if (applyTitleBarColors)
                {
                    ApplyTitleBarColors(window, titleBarColor, useDarkTheme);
                }

                return "Acrylic";
            }
        }
        catch (Exception)
        {
            // Backdrop APIs can fail on remote sessions or older drivers; the window still works.
        }

        return null;
    }

    private static void ApplyTitleBarColors(Window window, uint titleBarColor, bool useDarkTheme)
    {
        try
        {
            AppWindow appWindow = window.AppWindow;
            if (!AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            AppWindowTitleBar titleBar = appWindow.TitleBar;
            Color background = Color.FromArgb(255, (byte)(titleBarColor >> 16), (byte)(titleBarColor >> 8), (byte)titleBarColor);

            titleBar.BackgroundColor = background;
            titleBar.ForegroundColor = useDarkTheme ? Colors.White : Colors.Black;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = useDarkTheme ? Colors.White : Colors.Black;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(40, 255, 255, 255);
            titleBar.ButtonHoverForegroundColor = useDarkTheme ? Colors.White : Colors.Black;
        }
        catch (Exception)
        {
            // Title bar customization is purely cosmetic.
        }
    }

    /// <summary>Resolves whether a theme setting evaluates to dark mode (including AMOLED Black).</summary>
    public static bool IsEffectivelyDark(int themeSetting) => themeSetting switch
    {
        1 => false,
        2 or 3 => true,
        _ => Application.Current?.RequestedTheme == ApplicationTheme.Dark
    };

    /// <summary>Resolves the effective theme from a 0/1/2/3 setting (system/light/dark/black).</summary>
    public static ElementTheme ResolveTheme(int themeSetting) => themeSetting switch
    {
        1 => ElementTheme.Light,
        2 or 3 => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    /// <summary>Applies a theme to the window's root element when it is a framework element.</summary>
    public static void ApplyTheme(Window window, int themeSetting)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = ResolveTheme(themeSetting);
        }
    }

    /// <summary>
    /// Brings an active window reliably to the top of the Win32 Z-order and restores foreground
    /// activation after out-of-process prompts (e.g. Windows Hello) or File Explorer launches
    /// paired with a startup PIN dialog.
    /// </summary>
    public static void BringToForeground(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        try
        {
            AppWindow appWindow = window.AppWindow;
            if (appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } overlapped)
            {
                overlapped.Restore();
            }

            appWindow.Show();
            appWindow.MoveInZOrderAtTop();
            window.Activate();
        }
        catch
        {
        }

        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == 0)
            {
                return;
            }

            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SwRestore);
            }

            nint fg = GetForegroundWindow();
            uint fgThread = fg != 0 ? GetWindowThreadProcessId(fg, out _) : 0;
            uint curThread = GetCurrentThreadId();
            bool attached = false;

            if (fgThread != 0 && fgThread != curThread)
            {
                attached = AttachThreadInput(fgThread, curThread, true);
            }

            try
            {
                SetWindowPos(hwnd, 0, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpShowwindow);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(fgThread, curThread, false);
                }
            }
        }
        catch
        {
            // Best-effort OS foreground assertion.
        }
    }

    private const int SwRestore = 9;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpShowwindow = 0x0040;

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}

using System.Runtime.InteropServices;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Unpackaged-safe Win32 system tray (<c>Shell_NotifyIconW</c>) integration with context menu,
/// double-click restore, and balloon/toast notification fallback without requiring MSIX identity.
/// </summary>
public sealed class WindowsTrayIcon : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint WmApp = 0x8000;
    private const uint WmTrayIcon = WmApp + 101;
    private const uint WmDestroy = 0x0002;
    private const uint WmNcdestroy = 0x0082;
    private const uint WmHotkey = 0x0312;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;

    private const nuint SubclassId = 1;
    private const int BossHotKeyId = 0x4548;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;
    private const uint NiifError = 0x00000003;

    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;

    private const uint CmdShowWindow = 1001;
    private const uint CmdPauseAll = 1002;
    private const uint CmdExitApp = 1003;
    private const uint CmdBossHide = 1004;

    private delegate nint SubclassProcDelegate(
        nint hwnd,
        uint msg,
        nint wParam,
        nint lParam,
        nuint uIdSubclass,
        nuint dwRefData);

    private nint _hwnd;
    private bool _subclassed;
    private nint _iconHandle;
    private bool _ownsIconHandle;
    private bool _added;
    private bool _hotKeyRegistered;
    private bool _disposed;
    private string _tooltip = "EhViewer Desktop";
    private SubclassProcDelegate? _subclassProcDelegate;

    public bool IsAttached => _added && _hwnd != 0;
    public bool IsHotKeyRegistered => _hotKeyRegistered;

    public event Action? ShowRequested;
    public event Action? BossHideRequested;
    public event Action? BossKeyPressed;
    public event Action? PauseAllRequested;
    public event Action? ExitRequested;

    public bool Attach(nint hwnd, string tooltip = "EhViewer Desktop")
    {
        if (_disposed || hwnd == 0)
        {
            return false;
        }

        if (_added && _hwnd == hwnd)
        {
            return true;
        }

        _hwnd = hwnd;
        _tooltip = string.IsNullOrWhiteSpace(tooltip) ? "EhViewer Desktop" : tooltip;

        if (!_subclassed)
        {
            _subclassProcDelegate = WindowProc;
            _subclassed = SetWindowSubclass(hwnd, _subclassProcDelegate, SubclassId, 0);
        }

        _iconHandle = ResolveAppIcon(out _ownsIconHandle);

        var nid = CreateData(_tooltip);
        nid.uFlags = NifMessage | NifIcon | NifTip;
        nid.uCallbackMessage = WmTrayIcon;
        nid.hIcon = _iconHandle;

        _added = Shell_NotifyIconW(NimAdd, ref nid);
        if (!_added)
        {
            ReleaseIcon();
        }

        return _added;
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = string.IsNullOrWhiteSpace(tooltip) ? "EhViewer Desktop" : tooltip;
        if (_disposed || !_added || _hwnd == 0)
        {
            return;
        }

        var nid = CreateData(_tooltip);
        nid.uFlags = NifTip;
        try
        {
            Shell_NotifyIconW(NimModify, ref nid);
        }
        catch
        {
        }
    }

    public bool RegisterGlobalHotKey(uint modifiers, uint virtualKey)
    {
        if (_disposed || _hwnd == 0)
        {
            return false;
        }

        UnregisterGlobalHotKey();
        _hotKeyRegistered = RegisterHotKey(_hwnd, BossHotKeyId, modifiers | BossKeyBinding.ModNoRepeat, virtualKey);
        if (!_hotKeyRegistered)
        {
            _hotKeyRegistered = RegisterHotKey(_hwnd, BossHotKeyId, modifiers, virtualKey);
        }

        return _hotKeyRegistered;
    }

    public void UnregisterGlobalHotKey()
    {
        if (_hwnd == 0 || !_hotKeyRegistered)
        {
            return;
        }

        _hotKeyRegistered = false;
        try
        {
            UnregisterHotKey(_hwnd, BossHotKeyId);
        }
        catch
        {
        }
    }

    public void ShowNotification(string title, string message, bool isError = false)
    {
        if (_disposed || !_added || _hwnd == 0)
        {
            return;
        }

        var nid = CreateData(_tooltip);
        nid.uFlags = NifInfo;
        nid.szInfoTitle = Truncate(title, 63);
        nid.szInfo = Truncate(message, 255);
        nid.dwInfoFlags = isError ? NiifError : NiifInfo;

        try
        {
            Shell_NotifyIconW(NimModify, ref nid);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterGlobalHotKey();
        DeleteTrayIcon();
        RestoreWndProc();
        ReleaseIcon();
        _subclassProcDelegate = null;
        _hwnd = 0;
    }

    private nint WindowProc(
        nint hwnd,
        uint msg,
        nint wParam,
        nint lParam,
        nuint uIdSubclass,
        nuint dwRefData)
    {
        if (msg == WmHotkey && wParam.ToInt64() == BossHotKeyId)
        {
            BossKeyPressed?.Invoke();
            return 0;
        }

        if (msg == WmTrayIcon)
        {
            uint mouseMsg = unchecked((uint)(lParam.ToInt64() & 0xFFFF));
            if (mouseMsg == WmLButtonDblClk)
            {
                ShowRequested?.Invoke();
                return 0;
            }

            if (mouseMsg is WmRButtonUp or WmContextMenu)
            {
                ShowContextMenu(hwnd);
                return 0;
            }
        }
        else if (msg is WmDestroy or WmNcdestroy)
        {
            UnregisterGlobalHotKey();
            DeleteTrayIcon();
            RestoreWndProc();
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu(nint hwnd)
    {
        nint menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            AppendMenuW(menu, MfString, (nuint)CmdShowWindow, "显示主窗口");
            AppendMenuW(menu, MfString, (nuint)CmdBossHide, "立即隐身并锁定");
            AppendMenuW(menu, MfString, (nuint)CmdPauseAll, "全部暂停下载");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, (nuint)CmdExitApp, "退出应用");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(hwnd);
            uint cmd = TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton, pt.X, pt.Y, 0, hwnd, 0);
            switch (cmd)
            {
                case CmdShowWindow:
                    ShowRequested?.Invoke();
                    break;
                case CmdBossHide:
                    BossHideRequested?.Invoke();
                    break;
                case CmdPauseAll:
                    PauseAllRequested?.Invoke();
                    break;
                case CmdExitApp:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private NOTIFYICONDATAW CreateData(string tooltip) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = TrayIconId,
        szTip = Truncate(tooltip, 127)
    };

    private void DeleteTrayIcon()
    {
        if (!_added || _hwnd == 0)
        {
            return;
        }

        _added = false;
        try
        {
            var nid = CreateData(string.Empty);
            Shell_NotifyIconW(NimDelete, ref nid);
        }
        catch
        {
        }
    }

    private void RestoreWndProc()
    {
        if (_hwnd != 0 && _subclassed && _subclassProcDelegate is not null)
        {
            _subclassed = false;
            try
            {
                RemoveWindowSubclass(_hwnd, _subclassProcDelegate, SubclassId);
            }
            catch
            {
            }
        }
    }

    private void ReleaseIcon()
    {
        if (_ownsIconHandle && _iconHandle != 0)
        {
            DestroyIcon(_iconHandle);
        }

        _iconHandle = 0;
        _ownsIconHandle = false;
    }

    private static nint ResolveAppIcon(out bool ownsHandle)
    {
        ownsHandle = false;
        string? exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var smallIcons = new nint[1];
            uint count = ExtractIconExW(exePath, 0, null, smallIcons, 1);
            if (count > 0 && smallIcons[0] != 0)
            {
                ownsHandle = true;
                return smallIcons[0];
            }
        }

        return LoadIconW(0, (nint)32512); // IDI_APPLICATION
    }

    private static string Truncate(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLen ? value : value[..(maxLen - 1)] + "…";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, nint[]? phiconLarge, nint[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint LoadIconW(nint hInstance, nint lpIconName);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint hWnd,
        SubclassProcDelegate pfnSubclass,
        nuint uIdSubclass,
        nuint dwRefData);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint hWnd,
        SubclassProcDelegate pfnSubclass,
        nuint uIdSubclass);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}

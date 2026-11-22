using Microsoft.Extensions.Logging;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Unpackaged-safe desktop notification and jump-list dispatcher. Routes alerts through the
/// active Win32 <see cref="WindowsTrayIcon"/> balloon/Notification Center when available and
/// simultaneously notifies any attached in-app banner listener.
/// </summary>
public sealed class WindowsNotifications
{
    private readonly WindowsTrayIcon? _trayIcon;
    private readonly Func<DesktopSettings> _resolveSettings;
    private readonly ILogger<WindowsNotifications> _logger;

    public WindowsNotifications(
        WindowsTrayIcon? trayIcon,
        Func<DesktopSettings> resolveSettings,
        ILogger<WindowsNotifications> logger)
    {
        _trayIcon = trayIcon;
        _resolveSettings = resolveSettings ?? throw new ArgumentNullException(nameof(resolveSettings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event Action<string, string, bool>? BannerNotificationRequested;

    public void Notify(string title, string message, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        DesktopSettings settings;
        try
        {
            settings = _resolveSettings();
        }
        catch
        {
            settings = new DesktopSettings();
        }

        if (!settings.ShowNotifications)
        {
            return;
        }

        try
        {
            _trayIcon?.ShowNotification(title, message, isError);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "系统托盘通知发送失败：{Title}", title);
        }

        BannerNotificationRequested?.Invoke(title, message, isError);
    }

    /// <summary>
    /// Safely refreshes Windows Jump List shortcuts when supported (skipped cleanly on unpackaged
    /// processes or shells that reject JumpList COM activation).
    /// </summary>
    public static async Task TryConfigureJumpListAsync()
    {
        try
        {
            if (!Windows.UI.StartScreen.JumpList.IsSupported())
            {
                return;
            }

            Windows.UI.StartScreen.JumpList jumpList = await Windows.UI.StartScreen.JumpList.LoadCurrentAsync();
            jumpList.SystemGroupKind = Windows.UI.StartScreen.JumpListSystemGroupKind.Recent;
            await jumpList.SaveAsync();
        }
        catch
        {
            // Unpackaged WinUI apps without an AUMID registration throw COMException here; ignore gracefully.
        }
    }
}

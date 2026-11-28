using EhViewer.Core.Util;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EhViewer.Desktop;

public sealed partial class MainWindow
{
    private bool _shellHooked;
    private bool _forceExit;
    private bool _isPrivacyHidden;
    private bool _isPrivacyLocked;
    private bool _isUnlockDialogOpen;
    private bool _privacyLockOverlayLoaded;
    private DateTimeOffset _lastUserActivityUtc = DateTimeOffset.UtcNow;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _idleLockTimer;
    private readonly HashSet<string> _notifiedTerminalTasks = new(StringComparer.Ordinal);

    private void InitializeShellIntegration()
    {
        if (_shellHooked)
        {
            return;
        }

        _shellHooked = true;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        _services.GetRequiredService<WindowsTaskbarProgress>().Initialize(hwnd);

        WindowsTrayIcon tray = _services.GetRequiredService<WindowsTrayIcon>();
        tray.ShowRequested += OnTrayShowRequested;
        tray.BossKeyPressed += OnBossKeyPressed;
        tray.BossHideRequested += OnBossHideRequested;
        tray.PauseAllRequested += OnTrayPauseAllRequested;
        tray.ExitRequested += OnTrayExitRequested;
        tray.Attach(hwnd, "EhViewer Desktop");

        _idleLockTimer = DispatcherQueue.CreateTimer();
        _idleLockTimer.Interval = TimeSpan.FromSeconds(15);
        _idleLockTimer.IsRepeating = true;
        _idleLockTimer.Tick += (_, _) => CheckIdlePrivacyLock();

        AppWindow.Closing += OnAppWindowClosing;

        _services.GetRequiredService<DownloadManager>().ProgressChanged += OnDownloadProgressForShell;
        _services.GetRequiredService<ArchiverDownloadManager>().TasksChanged += OnArchiverTasksChangedForShell;
        _services.GetRequiredService<WindowsNotifications>().BannerNotificationRequested += OnBannerNotificationRequested;

        ApplyBossKeyAndPrivacySettings();
        _ = WindowsNotifications.TryConfigureJumpListAsync();
    }

    private void TeardownShellIntegration()
    {
        if (!_shellHooked)
        {
            return;
        }

        _shellHooked = false;
        _idleLockTimer?.Stop();
        try
        {
            AppWindow.Closing -= OnAppWindowClosing;
        }
        catch
        {
        }

        try
        {
            _services.GetRequiredService<DownloadManager>().ProgressChanged -= OnDownloadProgressForShell;
        }
        catch
        {
        }

        try
        {
            _services.GetRequiredService<ArchiverDownloadManager>().TasksChanged -= OnArchiverTasksChangedForShell;
        }
        catch
        {
        }

        try
        {
            _services.GetRequiredService<WindowsNotifications>().BannerNotificationRequested -= OnBannerNotificationRequested;
        }
        catch
        {
        }

        try
        {
            WindowsTrayIcon tray = _services.GetRequiredService<WindowsTrayIcon>();
            tray.ShowRequested -= OnTrayShowRequested;
            tray.BossKeyPressed -= OnBossKeyPressed;
            tray.BossHideRequested -= OnBossHideRequested;
            tray.PauseAllRequested -= OnTrayPauseAllRequested;
            tray.ExitRequested -= OnTrayExitRequested;
            tray.Dispose();
        }
        catch
        {
        }

        try
        {
            _services.GetRequiredService<WindowsTaskbarProgress>().Dispose();
        }
        catch
        {
        }
    }

    private void OnTrayShowRequested() =>
        DispatcherQueue.TryEnqueue(() => _ = TryRestoreFromPrivacyAsync());

    private void OnBossKeyPressed() =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_servicesDisposed)
            {
                return;
            }

            if (!_isPrivacyHidden && AppWindow.IsVisible)
            {
                ActivateBossPrivacyHide();
            }
            else
            {
                _ = TryRestoreFromPrivacyAsync();
            }
        });

    private void OnBossHideRequested() =>
        DispatcherQueue.TryEnqueue(ActivateBossPrivacyHide);

    public void TriggerBossHide() => ActivateBossPrivacyHide();

    public void ApplyBossKeyAndPrivacySettings()
    {
        if (_servicesDisposed)
        {
            return;
        }

        DesktopSettings settings = _services.Settings;
        WindowsTrayIcon tray = _services.GetRequiredService<WindowsTrayIcon>();

        if (settings.EnableBossKey && BossKeyBinding.TryParse(settings.BossKeyGesture, out uint modifiers, out uint vk, out _))
        {
            if (!tray.RegisterGlobalHotKey(modifiers, vk))
            {
                _logger.LogWarning("全局老板键注册失败（可能被其他程序占用）：{Gesture}", settings.BossKeyGesture);
            }
        }
        else
        {
            tray.UnregisterGlobalHotKey();
        }

        if (_idleLockTimer is not null)
        {
            _lastUserActivityUtc = DateTimeOffset.UtcNow;
            if (settings.AutoLockMinutes > 0)
            {
                _idleLockTimer.Start();
            }
            else
            {
                _idleLockTimer.Stop();
            }
        }
    }

    private void MarkUserActivity()
    {
        if (!_isPrivacyHidden && !_isPrivacyLocked)
        {
            _lastUserActivityUtc = DateTimeOffset.UtcNow;
        }
    }

    private void CheckIdlePrivacyLock()
    {
        if (_servicesDisposed || _isPrivacyHidden || _isPrivacyLocked)
        {
            return;
        }

        int minutes = _services.Settings.AutoLockMinutes;
        if (minutes <= 0)
        {
            return;
        }

        if (DateTimeOffset.UtcNow - _lastUserActivityUtc < TimeSpan.FromMinutes(minutes))
        {
            return;
        }

        ActivateIdlePrivacyLock();
    }

    private bool EnsurePrivacyLockOverlayLoaded()
    {
        if (_privacyLockOverlayLoaded && PrivacyLockOverlay is not null)
        {
            return true;
        }

        if (RootGrid.FindName(nameof(PrivacyLockOverlay)) is Grid)
        {
            _privacyLockOverlayLoaded = true;
            return true;
        }

        return false;
    }

    private void ActivateIdlePrivacyLock()
    {
        if (_servicesDisposed)
        {
            return;
        }

        _isPrivacyLocked = true;
        if (SceneContentHost.Content is Views.ReaderPage readerPage)
        {
            readerPage.PauseTransientPlayback();
        }

        RightDrawerOverlay.Visibility = Visibility.Collapsed;
        DismissHoverPreview();
        if (EnsurePrivacyLockOverlayLoaded())
        {
            if (PrivacyLockSubtitleText is not null)
            {
                PrivacyLockSubtitleText.Text = WindowsAppLockService.IsLockEnabled(_services.Settings)
                    ? "长时间无操作已自动锁定，完成验证后恢复显示。"
                    : "长时间无操作已拉起隐私遮罩，点击下方按钮恢复。";
            }

            if (PrivacyLockOverlay is not null)
            {
                PrivacyLockOverlay.Visibility = Visibility.Visible;
            }
        }
        _windowManager.HideAllForBossKey();
    }

    private void ActivateBossPrivacyHide()
    {
        if (_servicesDisposed)
        {
            return;
        }

        _isPrivacyHidden = true;
        bool hasAuthLock = WindowsAppLockService.IsLockEnabled(_services.Settings);
        _isPrivacyLocked = _services.Settings.RequireUnlockOnBossRestore && hasAuthLock;

        if (SceneContentHost.Content is Views.ReaderPage readerPage)
        {
            readerPage.PauseTransientPlayback();
        }

        RightDrawerOverlay.Visibility = Visibility.Collapsed;
        DismissHoverPreview();

        if (EnsurePrivacyLockOverlayLoaded())
        {
            if (PrivacyLockSubtitleText is not null)
            {
                PrivacyLockSubtitleText.Text = _isPrivacyLocked
                    ? "已启动防社死隐私遮罩，通过安全验证后恢复浏览。"
                    : "已隐藏窗口到托盘，点击恢复按钮继续浏览。";
            }

            if (PrivacyLockOverlay is not null)
            {
                PrivacyLockOverlay.Visibility = Visibility.Visible;
            }
        }

        _windowManager.HideAllForBossKey();

        WindowsTrayIcon tray = _services.GetRequiredService<WindowsTrayIcon>();
        if (tray.IsAttached || tray.IsHotKeyRegistered)
        {
            if (_services.Settings.StealthTrayTooltip)
            {
                tray.SetTooltip("Windows 桌面辅助服务");
            }

            AppWindow.Hide();
        }
    }

    private async Task TryRestoreFromPrivacyAsync()
    {
        if (_servicesDisposed)
        {
            return;
        }

        DesktopSettings settings = _services.Settings;
        bool requiresAuth = _isPrivacyLocked
            ? WindowsAppLockService.IsLockEnabled(settings)
            : (settings.RequireUnlockOnBossRestore && WindowsAppLockService.IsLockEnabled(settings));

        if (requiresAuth)
        {
            _isPrivacyLocked = true;
            if (EnsurePrivacyLockOverlayLoaded() && PrivacyLockOverlay is not null)
            {
                PrivacyLockOverlay.Visibility = Visibility.Visible;
            }
        }

        AppWindow.Show();
        WindowAppearance.BringToForeground(this);

        WindowsTrayIcon tray = _services.GetRequiredService<WindowsTrayIcon>();
        tray.SetTooltip("EhViewer Desktop");

        if (requiresAuth)
        {
            bool unlocked = await EnsureUnlockedAsync(hideToTrayOnCancel: true).ConfigureAwait(true);
            if (!unlocked || _servicesDisposed)
            {
                return;
            }
        }

        _isPrivacyHidden = false;
        _isPrivacyLocked = false;
        if (_privacyLockOverlayLoaded && PrivacyLockOverlay is not null)
        {
            PrivacyLockOverlay.Visibility = Visibility.Collapsed;
        }
        _lastUserActivityUtc = DateTimeOffset.UtcNow;
        _windowManager.RestoreBossHiddenWindows();
        RestoreForegroundAfterUnlock();
    }

    private void OnPrivacyUnlockClick(object sender, RoutedEventArgs e)
    {
        _ = TryRestoreFromPrivacyAsync();
    }

    private void OnPrivacyHideBackClick(object sender, RoutedEventArgs e)
    {
        ActivateBossPrivacyHide();
    }

    private void OnTrayPauseAllRequested() =>
        DispatcherQueue.TryEnqueue(() =>
        {
            _services.GetRequiredService<DownloadManager>().PauseAll();
            SetStatus("已通过系统托盘全部暂停下载。");
        });

    private void OnTrayExitRequested() =>
        DispatcherQueue.TryEnqueue(() =>
        {
            _forceExit = true;
            Close();
        });

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_forceExit &&
            _services.Settings.MinimizeToTray &&
            _services.GetRequiredService<WindowsTrayIcon>().IsAttached)
        {
            args.Cancel = true;
            AppWindow.Hide();
        }
    }

    private void OnDownloadProgressForShell(DownloadProgress progress) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_servicesDisposed)
            {
                return;
            }

            RefreshTaskbarProgress();
            MaybeNotifyDownloadProgress(progress);
        });

    private void OnArchiverTasksChangedForShell(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_servicesDisposed)
            {
                RefreshTaskbarProgress();
            }
        });

    private void RefreshTaskbarProgress()
    {
        DownloadManager dl = _services.GetRequiredService<DownloadManager>();
        ArchiverDownloadManager arch = _services.GetRequiredService<ArchiverDownloadManager>();
        TaskbarProgressReport report = WindowsTaskbarProgress.ComputeAggregateProgress([.. dl.Snapshot()], arch.GetSnapshots());
        _services.GetRequiredService<WindowsTaskbarProgress>().Update(report);
    }

    private void MaybeNotifyDownloadProgress(DownloadProgress progress)
    {
        if (progress.State is DownloadState.Wait or DownloadState.Download)
        {
            _notifiedTerminalTasks.Remove($"dl:ok:{progress.Gid}");
            _notifiedTerminalTasks.Remove($"dl:509:{progress.Gid}");
            return;
        }

        if (progress.State == DownloadState.Finish)
        {
            if (!_isPrivacyHidden && _notifiedTerminalTasks.Add($"dl:ok:{progress.Gid}"))
            {
                _services.GetRequiredService<WindowsNotifications>().Notify(
                    "下载完成",
                    $"{progress.Title}（{progress.Total} 页）已完成。");
            }
        }
        else if (progress.State == DownloadState.Failed &&
                 !string.IsNullOrEmpty(progress.Error) &&
                 (progress.Error.Contains("509", StringComparison.Ordinal) ||
                  progress.Error.Contains("配额", StringComparison.Ordinal)))
        {
            if (!_isPrivacyHidden && _notifiedTerminalTasks.Add($"dl:509:{progress.Gid}"))
            {
                _services.GetRequiredService<WindowsNotifications>().Notify(
                    "图片配额受限 (509)",
                    $"{progress.Title}：{progress.Error}",
                    isError: true);
            }
        }
    }

    private void OnBannerNotificationRequested(string title, string message, bool isError) =>
        DispatcherQueue.TryEnqueue(() =>
            ShowBanner(title, message, isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational));

    private async Task EnsureRootXamlAttachedAsync()
    {
        if (RootGrid.XamlRoot is not null || _servicesDisposed)
        {
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRootLoaded(object? s, RoutedEventArgs e)
        {
            RootGrid.Loaded -= OnRootLoaded;
            tcs.TrySetResult();
        }

        RootGrid.Loaded += OnRootLoaded;
        if (RootGrid.XamlRoot is not null)
        {
            RootGrid.Loaded -= OnRootLoaded;
            return;
        }

        await Task.WhenAny(tcs.Task, Task.Delay(1500)).ConfigureAwait(true);
        RootGrid.Loaded -= OnRootLoaded;
    }

    private async Task<bool> EnsureUnlockedAsync(bool hideToTrayOnCancel = false)
    {
        DesktopSettings settings = _services.Settings;
        if (!WindowsAppLockService.IsLockEnabled(settings))
        {
            return true;
        }

        if (_isUnlockDialogOpen)
        {
            return false;
        }

        _isUnlockDialogOpen = true;
        try
        {
            await EnsureRootXamlAttachedAsync().ConfigureAwait(true);
            if (_servicesDisposed)
            {
                return false;
            }

            string? helloStatusError = null;
            if (settings.UseWindowsHello)
            {
                WindowsHelloResult hello = await WindowsAppLockService
                    .TryVerifyWindowsHelloAsync("请通过 Windows Hello 解锁 EhViewer Desktop")
                    .ConfigureAwait(true);
                if (hello.Success)
                {
                    RestoreForegroundAfterUnlock();
                    return true;
                }

                helloStatusError = hello.ErrorMessage ?? "Windows Hello 验证未通过，请重试。";
                WindowAppearance.BringToForeground(this);
            }

            if (RootGrid.XamlRoot is null)
            {
                return false;
            }

            bool hasPin = !string.IsNullOrWhiteSpace(settings.SecurityPinHash);
            Microsoft.UI.Xaml.Media.Brush errorBrush =
                Application.Current?.Resources["SystemFillColorCriticalBrush"] as Microsoft.UI.Xaml.Media.Brush
                ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 211, 47, 47));

            while (!_servicesDisposed)
            {
                var pinBox = new PasswordBox
                {
                    Header = hasPin ? "请输入安全锁 PIN" : "此设备未配置本地 PIN，请点击「使用 Windows Hello 重试」",
                    IsEnabled = hasPin
                };
                var statusText = new TextBlock
                {
                    Foreground = errorBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Text = helloStatusError ?? string.Empty
                };
                var panel = new StackPanel { Width = 340, Spacing = 8 };
                panel.Children.Add(pinBox);
                panel.Children.Add(statusText);

                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = "应用安全锁",
                    PrimaryButtonText = hasPin ? "解锁" : null,
                    SecondaryButtonText = settings.UseWindowsHello ? "Windows Hello 重试" : null,
                    CloseButtonText = hideToTrayOnCancel ? "隐藏回托盘" : "退出应用",
                    DefaultButton = hasPin ? ContentDialogButton.Primary : ContentDialogButton.Secondary,
                    Content = panel
                };

                dialog.Opened += (_, _) =>
                {
                    WindowAppearance.BringToForeground(this);
                    if (hasPin)
                    {
                        pinBox.Focus(FocusState.Programmatic);
                    }
                };

                dialog.PrimaryButtonClick += (_, args) =>
                {
                    PinVerificationResult verification = WindowsAppLockService.VerifyPinDetailed(
                        pinBox.Password.Trim(),
                        settings.SecurityPinHash);

                    if (verification.IsSuccess)
                    {
                        if (verification.Status == PinVerifyStatus.ValidNeedsMigration
                            && !string.IsNullOrWhiteSpace(verification.MigratedProtectedHash))
                        {
                            string migratedHash = verification.MigratedProtectedHash;
                            _services.UpdateSettings(s => s.SecurityPinHash = migratedHash);
                        }

                        return;
                    }

                    statusText.Text = verification.Status switch
                    {
                        PinVerifyStatus.LockedOut =>
                            $"错误次数过多，请在 {Math.Max(1, (int)Math.Ceiling(verification.RetryAfter.TotalSeconds))} 秒后重试。",
                        PinVerifyStatus.ProtectionError =>
                            "安全锁凭据解密失败，请使用 Windows Hello 或重置本地 PIN。",
                        PinVerifyStatus.InvalidFormat =>
                            "安全锁凭据格式异常，请使用 Windows Hello 或重新设置 PIN。",
                        _ when verification.RetryAfter > TimeSpan.Zero =>
                            $"PIN 不正确，已触发冷却，请在 {Math.Max(1, (int)Math.Ceiling(verification.RetryAfter.TotalSeconds))} 秒后重试。",
                        _ => "PIN 不正确，请重试。"
                    };
                    args.Cancel = true;
                };

                ContentDialogResult result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    RestoreForegroundAfterUnlock();
                    return true;
                }

                if (result == ContentDialogResult.Secondary)
                {
                    WindowsHelloResult retry = await WindowsAppLockService
                        .TryVerifyWindowsHelloAsync("请通过 Windows Hello 解锁 EhViewer Desktop")
                        .ConfigureAwait(true);
                    if (retry.Success)
                    {
                        RestoreForegroundAfterUnlock();
                        return true;
                    }

                    helloStatusError = retry.ErrorMessage ?? "Windows Hello 验证未通过，请重试。";
                    WindowAppearance.BringToForeground(this);
                    continue;
                }

                if (hideToTrayOnCancel)
                {
                    ActivateBossPrivacyHide();
                    return false;
                }

                _forceExit = true;
                Close();
                return false;
            }

            return false;
        }
        finally
        {
            _isUnlockDialogOpen = false;
        }
    }

    private void RestoreForegroundAfterUnlock()
    {
        if (_servicesDisposed)
        {
            return;
        }

        WindowAppearance.BringToForeground(this);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_servicesDisposed)
            {
                WindowAppearance.BringToForeground(this);
                NavigationItems.Focus(FocusState.Programmatic);
            }
        });
    }

    private async Task CheckUpdateSilentlyIfEnabledAsync()
    {
        if (!_services.Settings.CheckUpdateOnLaunch)
        {
            return;
        }

        try
        {
            var vm = new SettingsViewModel(_services);
            AppUpdateCheckResult? result = await vm.CheckForUpdatesAsync(cancellationToken: _lifetime.Token)
                .ConfigureAwait(true);
            if (result is { HasUpdate: true, Latest: { } latest })
            {
                _services.GetRequiredService<WindowsNotifications>().Notify(
                    $"发现新版本 {latest.Version}",
                    latest.Title ?? "请前往「设置 -> 应用更新」查看新版发布说明。");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "启动静默检查更新失败");
        }
    }
}

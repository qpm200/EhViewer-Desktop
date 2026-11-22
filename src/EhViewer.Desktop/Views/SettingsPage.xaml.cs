using System.Diagnostics;
using EhViewer.Core.Client;
using EhViewer.Core.Network;
using EhViewer.Core.Storage;
using EhViewer.Core.Util;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EhViewer.Desktop.Views;

/// <summary>Application settings page, including uconfig matrix editors, Hosts/Blacklist CRUD, PIN lock, updates, and maintenance.</summary>
public sealed partial class SettingsPage : Page
{
    private readonly AppServices _services;
    private readonly Window? _owner;
    private CancellationTokenSource? _modelDownloadCts;
    private CancellationTokenSource? _ocrStatusCts;
    private bool _hasTriggeredDeferredOcrRefresh;
    private bool _ocrStatusRefreshCompleted;

    public SettingsPage(AppServices services, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
        _owner = owner;
        ViewModel = new SettingsViewModel(services, deferHeavyIo: true);
        InitializeComponent();
        ViewModel.SettingsApplied += (_, _) => SettingsSaved?.Invoke(this, EventArgs.Empty);
        ViewModel.AutoSaveOnChange = true;
        Loaded += OnSettingsPageLoaded;
        Unloaded += OnSettingsPageUnloaded;
    }

    private void OnSettingsPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_activeGroupIndex == 1 && !_ocrStatusRefreshCompleted)
        {
            EnsureDeferredOcrStatusRefreshed();
        }
    }

    private void OnSettingsPageUnloaded(object sender, RoutedEventArgs e)
    {
        _modelDownloadCts?.Cancel();
        _modelDownloadCts?.Dispose();
        _modelDownloadCts = null;

        _ocrStatusCts?.Cancel();
        _ocrStatusCts?.Dispose();
        _ocrStatusCts = null;
        if (!_ocrStatusRefreshCompleted)
        {
            _hasTriggeredDeferredOcrRefresh = false;
        }
    }

    private void EnsureDeferredOcrStatusRefreshed(bool force = false)
    {
        if (!force && _hasTriggeredDeferredOcrRefresh)
        {
            return;
        }

        _hasTriggeredDeferredOcrRefresh = true;
        _ocrStatusRefreshCompleted = false;

        _ocrStatusCts?.Cancel();
        _ocrStatusCts?.Dispose();
        var cts = new CancellationTokenSource();
        _ocrStatusCts = cts;

        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = DispatcherQueue;
        _ = RefreshOcrAndLocalModelStatusInBackgroundAsync(dispatcher, cts.Token);
    }

    private async Task RefreshOcrAndLocalModelStatusInBackgroundAsync(
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher,
        CancellationToken ct)
    {
        try
        {
            await ViewModel.RefreshOcrAndLocalModelStatusAsync(
                dispatch =>
                {
                    if (ct.IsCancellationRequested || dispatcher is null)
                    {
                        return;
                    }

                    dispatcher.TryEnqueue(() =>
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            dispatch();
                            _ocrStatusRefreshCompleted = true;
                        }
                    });
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when SettingsPage is unloaded before the background probe completes.
        }
    }

    public SettingsViewModel ViewModel { get; }

    public event EventHandler? SettingsSaved;

    internal void SyncNavigationDrawerCollapsed(bool collapsed) =>
        ViewModel.SyncNavigationDrawerCollapsed(collapsed);

    internal void SyncThemeIndex(int theme) =>
        ViewModel.SyncThemeIndex(theme);

    public bool HasText(string? value) => !string.IsNullOrEmpty(value);

    public Visibility TextToVisibility(string? value) =>
        string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Load(deferHeavyIo: true);
        if (_hasTriggeredDeferredOcrRefresh || _activeGroupIndex == 1)
        {
            EnsureDeferredOcrStatusRefreshed(force: true);
        }

        SettingsSaved?.Invoke(this, EventArgs.Empty);
        ViewModel.StatusMessage = "已重新载入设置。";
    }

    private async void OnChooseDownloadDirClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        if (_owner is not null)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(_owner));
        }

        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync().AsTask().ConfigureAwait(true);
        if (folder is not null)
        {
            ViewModel.CustomDownloadDirectory = folder.Path;
            ViewModel.EffectiveDownloadDirectory = folder.Path;
        }
    }

    private void OnClearDownloadDirClick(object sender, RoutedEventArgs e)
        => ViewModel.ClearCustomDownloadDirectory();

    private void OnApplyUConfigCookieClick(object sender, RoutedEventArgs e)
        => ViewModel.ApplyUConfigToCookie();

    private void OnPullUConfigCookieClick(object sender, RoutedEventArgs e)
        => ViewModel.PullUConfigFromCookie();

    private async void OnSyncUConfigOnlineClick(object sender, RoutedEventArgs e)
        => await ViewModel.SyncUConfigOnlineAsync().ConfigureAwait(true);

    private async void OnEditExcludedLanguagesClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        HashSet<string> selected = ViewModel.GetExcludedLanguageSelection();
        var boxes = new List<(string Code, CheckBox Box)>();
        var listPanel = new StackPanel { Spacing = 8 };

        var headerRow = new Grid { ColumnSpacing = 8 };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        AddHeaderText(headerRow, "语言", 0);
        AddHeaderText(headerRow, "原作", 1);
        AddHeaderText(headerRow, "翻译", 2);
        AddHeaderText(headerRow, "改写", 3);
        listPanel.Children.Add(headerRow);

        foreach (ExcludedLanguageGroup group in EhConfig.ExcludedLanguageCatalog)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

            var label = new TextBlock
            {
                Text = group.DisplayName,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            string?[] codes = [group.OriginalCode, group.TranslatedCode, group.RewriteCode];
            for (int col = 0; col < 3; col++)
            {
                string? code = codes[col];
                if (string.IsNullOrEmpty(code))
                {
                    continue;
                }

                var cb = new CheckBox
                {
                    Content = string.Empty,
                    IsChecked = selected.Contains(code),
                    MinWidth = 32
                };
                Grid.SetColumn(cb, col + 1);
                row.Children.Add(cb);
                boxes.Add((code, cb));
            }

            listPanel.Children.Add(row);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var btnAll = new Button { Content = "全选" };
        var btnNone = new Button { Content = "清空" };
        var btnInvert = new Button { Content = "反选" };
        btnAll.Click += (_, _) => { foreach ((_, CheckBox b) in boxes) b.IsChecked = true; };
        btnNone.Click += (_, _) => { foreach ((_, CheckBox b) in boxes) b.IsChecked = false; };
        btnInvert.Click += (_, _) => { foreach ((_, CheckBox b) in boxes) b.IsChecked = b.IsChecked != true; };
        actions.Children.Add(btnAll);
        actions.Children.Add(btnNone);
        actions.Children.Add(btnInvert);

        var rootPanel = new StackPanel { Spacing = 10 };
        rootPanel.Children.Add(actions);
        rootPanel.Children.Add(new ScrollViewer { MaxHeight = 420, Content = listPanel });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "排除语言矩阵（17×3）",
            PrimaryButtonText = "确定并应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = rootPanel
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.UpdateExcludedLanguages(boxes.Where(x => x.Box.IsChecked == true).Select(x => x.Code));
            ViewModel.ApplyUConfigToCookie();
        }

        static void AddHeaderText(Grid grid, string text, int col)
        {
            var tb = new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }
    }

    private async void OnEditExcludedNamespacesClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        int mask = ViewModel.ExcludedNamespaces;
        var items = new List<(int Bit, CheckBox Box)>();
        var panel = new StackPanel { Spacing = 8 };

        foreach (ExcludedNamespaceOption opt in EhConfig.ExcludedNamespaceOptions)
        {
            var cb = new CheckBox
            {
                Content = opt.DisplayName,
                IsChecked = (mask & opt.Mask) != 0
            };
            panel.Children.Add(cb);
            items.Add((opt.Mask, cb));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "排除标签命名空间（uconfig）",
            PrimaryButtonText = "确定并应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = panel
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            int nextMask = 0;
            foreach ((int bit, CheckBox box) in items)
            {
                if (box.IsChecked == true)
                {
                    nextMask |= bit;
                }
            }

            ViewModel.UpdateExcludedNamespaces(nextMask);
            ViewModel.ApplyUConfigToCookie();
        }
    }

    private async void OnManageHostsClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var hostBox = new TextBox { PlaceholderText = "主机名（例：e-hentai.org）" };
        var ipBox = new TextBox { PlaceholderText = "IP（例：104.20.18.168）" };
        var infoText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var listPanel = new StackPanel { Spacing = 6 };

        void Rebuild()
        {
            listPanel.Children.Clear();
            foreach (HostEntry entry in ViewModel.GetHostEntries())
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new TextBlock
                {
                    Text = $"{entry.Host} -> {entry.Ip}",
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(text, 0);

                var del = new Button { Content = "删除" };
                string h = entry.Host;
                del.Click += (_, _) =>
                {
                    ViewModel.DeleteHost(h);
                    infoText.Text = $"已删除 {h}";
                    Rebuild();
                };
                Grid.SetColumn(del, 1);

                row.Children.Add(text);
                row.Children.Add(del);
                listPanel.Children.Add(row);
            }
        }

        var addBtn = new Button { Content = "添加 / 覆盖" };
        addBtn.Click += (_, _) =>
        {
            if (ViewModel.AddOrUpdateHost(hostBox.Text, ipBox.Text, out string msg))
            {
                hostBox.Text = string.Empty;
                ipBox.Text = string.Empty;
                Rebuild();
            }

            infoText.Text = msg;
        };

        var root = new StackPanel { Width = 460, Spacing = 10 };
        root.Children.Add(hostBox);
        root.Children.Add(ipBox);
        root.Children.Add(addBtn);
        root.Children.Add(infoText);
        root.Children.Add(new ScrollViewer { MaxHeight = 280, Content = listPanel });
        Rebuild();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "自定义 Hosts 管理器",
            CloseButtonText = "关闭",
            Content = root
        };
        await dialog.ShowAsync();
    }

    private async void OnManageBlackListClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var nameBox = new TextBox { PlaceholderText = "用户名" };
        var reasonBox = new TextBox { PlaceholderText = "屏蔽原因（可选）" };
        var infoText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var listPanel = new StackPanel { Spacing = 6 };

        void Rebuild()
        {
            listPanel.Children.Clear();
            foreach (BlackListEntry entry in ViewModel.GetBlackListEntries())
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new TextBlock
                {
                    Text = $"{entry.BadGayName} · {entry.Reason ?? "手动屏蔽"}",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(text, 0);

                var del = new Button { Content = "移除" };
                del.Click += (_, _) =>
                {
                    ViewModel.DeleteBlackListEntry(entry);
                    infoText.Text = $"已移除 {entry.BadGayName}";
                    Rebuild();
                };
                Grid.SetColumn(del, 1);

                row.Children.Add(text);
                row.Children.Add(del);
                listPanel.Children.Add(row);
            }
        }

        var addBtn = new Button { Content = "加入黑名单" };
        addBtn.Click += (_, _) =>
        {
            if (ViewModel.AddBlackListEntry(nameBox.Text, reasonBox.Text, out string msg))
            {
                nameBox.Text = string.Empty;
                reasonBox.Text = string.Empty;
                Rebuild();
            }

            infoText.Text = msg;
        };

        var root = new StackPanel { Width = 460, Spacing = 10 };
        root.Children.Add(nameBox);
        root.Children.Add(reasonBox);
        root.Children.Add(addBtn);
        root.Children.Add(infoText);
        root.Children.Add(new ScrollViewer { MaxHeight = 280, Content = listPanel });
        Rebuild();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "评论黑名单管理",
            CloseButtonText = "关闭",
            Content = root
        };
        await dialog.ShowAsync();
    }

    private async void OnSetPinClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var pin1 = new PasswordBox { Header = "新安全锁 PIN（至少 4 位）" };
        var pin2 = new PasswordBox { Header = "再次确认 PIN" };
        var error = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap
        };
        var panel = new StackPanel { Width = 340, Spacing = 10 };
        panel.Children.Add(pin1);
        panel.Children.Add(pin2);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "设置应用安全锁 PIN",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = panel
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            string p1 = pin1.Password.Trim();
            string p2 = pin2.Password.Trim();
            if (p1.Length < 4)
            {
                error.Text = "PIN 长度至少 4 位。";
                args.Cancel = true;
                return;
            }

            if (!string.Equals(p1, p2, StringComparison.Ordinal))
            {
                error.Text = "两次输入的 PIN 不一致。";
                args.Cancel = true;
                return;
            }

            if (!ViewModel.SetSecurityPin(p1))
            {
                error.Text = ViewModel.ErrorMessage;
                args.Cancel = true;
            }
        };

        await dialog.ShowAsync();
    }

    private async void OnClearPinClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.UseWindowsHello || XamlRoot is null)
        {
            ViewModel.ClearSecurityPin(disableWindowsHello: true);
            return;
        }

        string message = ViewModel.HasSecurityPin
            ? "当前同时启用了「优先调用 Windows Hello 验证」。若仅清除本地 PIN 而保留 Windows Hello，应用在启动或从老板键恢复时仍会触发系统级 Windows Hello / 账户 PIN 解锁提示。"
            : "当前未配置本地 PIN，但仍启用了「优先调用 Windows Hello 验证」。因此应用在启动或从老板键恢复时仍会被系统级 Windows Hello / 账户 PIN 锁定。\n\n是否关闭 Windows Hello 并彻底停用应用安全锁？";

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清除应用安全锁",
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            PrimaryButtonText = ViewModel.HasSecurityPin ? "全部清除并停用锁定" : "关闭 Windows Hello 并停用锁定",
            SecondaryButtonText = ViewModel.HasSecurityPin ? "仅清除本地 PIN（保留 Hello）" : null,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.ClearSecurityPin(disableWindowsHello: true);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            ViewModel.ClearSecurityPin(disableWindowsHello: false);
        }
    }

    private void OnTestBossKeyClick(object sender, RoutedEventArgs e)
    {
        if (_owner is MainWindow mainWindow)
        {
            mainWindow.TriggerBossHide();
        }
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        AppUpdateCheckResult? result = await ViewModel.CheckForUpdatesAsync().ConfigureAwait(true);
        if (result is { HasUpdate: true, Latest: { } latest } &&
            XamlRoot is not null &&
            !string.IsNullOrWhiteSpace(latest.DownloadUrl))
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"发现新版本 {latest.Version}",
                Content = new TextBlock
                {
                    Text = $"{latest.Title}\n\n{latest.ReleaseNotes}",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "打开发布页",
                CloseButtonText = "稍后",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
                Uri.TryCreate(latest.DownloadUrl, UriKind.Absolute, out Uri? url))
            {
                _ = await Windows.System.Launcher.LaunchUriAsync(url);
            }
        }
    }

    private async void OnAboutClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        string text =
            $"""
            EhViewer Desktop v{SettingsViewModel.GetCurrentAppVersion()}
            基于上游 Ehviewer_CN_SXJ (v2.0.2.7) 移植构建于 Windows Desktop (.NET 10 + WinUI 3 / Windows App SDK)。

            核心开源组件与许可摘要：
            - AngleSharp (MIT License) — HTML DOM 解析
            - SkiaSharp (MIT License) — 跨平台图像解码与色彩管理
            - Microsoft.Data.Sqlite / SQLitePCLRaw (MIT / Apache-2.0) — 本地持久化存储
            - CommunityToolkit.Mvvm (MIT License) — MVVM 基础框架
            - Microsoft.Extensions.DependencyInjection / Logging (MIT License)
            """;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "关于与开源组件许可",
            CloseButtonText = "关闭",
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                Content = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            }
        };

        await dialog.ShowAsync();
    }

    private void OnExportDataClick(object sender, RoutedEventArgs e)
    {
        string? path = ViewModel.ExportData();
        ViewModel.MaintenanceMessage = Services.MaintenanceService.DescribeExportResult(path);
    }

    private async void OnImportDataClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(MaintenanceService.ExportExtension);

        if (_owner is not null)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(_owner));
        }

        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync().AsTask().ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        ViewModel.MaintenanceMessage = ViewModel.ImportData(file.Path)
            ? $"已合并导入 {file.Path}，请重启页面或刷新以加载最新书签/标签/黑名单。"
            : "导入失败：文件损坏或格式不兼容。";
    }

    private void OnClearImageCacheClick(object sender, RoutedEventArgs e)
    {
        long bytes = ViewModel.ClearImageCaches();
        ViewModel.MaintenanceMessage = $"已清理缩略图与网络图片缓存（约 {bytes / 1024.0 / 1024.0:F2} MB）。";
    }

    private void OnCleanRedundancyClick(object sender, RoutedEventArgs e)
    {
        int deleted = ViewModel.CleanRedundantRows();
        ViewModel.MaintenanceMessage = deleted == 0
            ? "未发现冗余下载项。"
            : $"已清理 {deleted} 条孤立下载记录。";
    }

    private void OnClearDownloadPathCacheClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearDownloadPathCache();
        ViewModel.MaintenanceMessage = "已清除下载路径缓存。";
    }

    private void OnOpenConfigDirectoryClick(object sender, RoutedEventArgs e)
    {
        string directory = _services.Paths.RootDirectory;
        Directory.CreateDirectory(directory);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"无法打开目录：{ex.Message}";
        }
    }

    private async void OnOpenWindowsLanguageSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:regionlanguage"));
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"无法打开系统语言设置：{ex.Message}";
        }
    }

    private void OnRefreshOcrStatusClick(object sender, RoutedEventArgs e)
        => EnsureDeferredOcrStatusRefreshed(force: true);

    private async void OnDownloadMangaOcrModelsClick(object sender, RoutedEventArgs e)
    {
        _modelDownloadCts?.Cancel();
        _modelDownloadCts?.Dispose();
        _modelDownloadCts = new CancellationTokenSource();
        await ViewModel.DownloadMangaOcrModelsAsync(_modelDownloadCts.Token).ConfigureAwait(true);
    }

    private async void OnDownloadSakuraModelClick(object sender, RoutedEventArgs e)
    {
        _modelDownloadCts?.Cancel();
        _modelDownloadCts?.Dispose();
        _modelDownloadCts = new CancellationTokenSource();
        await ViewModel.DownloadSakuraModelAsync(_modelDownloadCts.Token).ConfigureAwait(true);
    }

    private async void OnBrowseSakuraModelClick(object sender, RoutedEventArgs e)
    {
        string? path = await PickSingleFileAsync(".gguf").ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(path))
        {
            ViewModel.LocalSakuraModelPath = path;
            ViewModel.RefreshOcrAndLocalModelStatus();
        }
    }

    private async void OnDownloadLlamaCppClick(object sender, RoutedEventArgs e)
    {
        _modelDownloadCts?.Cancel();
        _modelDownloadCts?.Dispose();
        _modelDownloadCts = new CancellationTokenSource();
        await ViewModel.DownloadLlamaCppRuntimeAsync(_modelDownloadCts.Token).ConfigureAwait(true);
    }

    private async void OnBrowseLlamaServerClick(object sender, RoutedEventArgs e)
    {
        string? path = await PickSingleFileAsync(".exe").ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(path))
        {
            ViewModel.LocalLlamaServerPath = path;
            ViewModel.RefreshOcrAndLocalModelStatus();
        }
    }

    private async void OnToggleLlamaServerClick(object sender, RoutedEventArgs e)
        => await ViewModel.ToggleLocalLlamaServerAsync().ConfigureAwait(true);

    private void OnCancelModelDownloadClick(object sender, RoutedEventArgs e)
        => _modelDownloadCts?.Cancel();

    private async void OnTestTranslationClick(object sender, RoutedEventArgs e)
        => await ViewModel.TestTranslationPipelineAsync().ConfigureAwait(true);

    private void OnClearTranslationCacheClick(object sender, RoutedEventArgs e)
        => ViewModel.ClearPersistentTranslationCache();

    private void OnSaveCustomApiKeyClick(object sender, RoutedEventArgs e)
    {
        if (CustomApiKeyPasswordBox is null)
        {
            return;
        }

        string raw = CustomApiKeyPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(raw))
        {
            ViewModel.ErrorMessage = "请先输入 API Key。";
            return;
        }

        ViewModel.SaveCustomTranslationApiKey(raw);
        CustomApiKeyPasswordBox.Password = string.Empty;
    }

    private void OnClearCustomApiKeyClick(object sender, RoutedEventArgs e)
    {
        if (CustomApiKeyPasswordBox is not null)
        {
            CustomApiKeyPasswordBox.Password = string.Empty;
        }

        ViewModel.SaveCustomTranslationApiKey(null);
    }

    private void OnSaveDeepSeekApiKeyClick(object sender, RoutedEventArgs e)
    {
        if (DeepSeekApiKeyPasswordBox is null)
        {
            return;
        }

        string raw = DeepSeekApiKeyPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(raw))
        {
            ViewModel.ErrorMessage = "请先输入 DeepSeek API Key。";
            return;
        }

        ViewModel.SaveDeepSeekApiKey(raw);
        DeepSeekApiKeyPasswordBox.Password = string.Empty;
    }

    private void OnClearDeepSeekApiKeyClick(object sender, RoutedEventArgs e)
    {
        if (DeepSeekApiKeyPasswordBox is not null)
        {
            DeepSeekApiKeyPasswordBox.Password = string.Empty;
        }

        ViewModel.ClearDeepSeekApiKey();
    }

    private void OnSelectDeepSeekChatPresetClick(object sender, RoutedEventArgs e) =>
        ViewModel.DeepSeekModel = "deepseek-chat";

    private void OnSelectDeepSeekReasonerPresetClick(object sender, RoutedEventArgs e) =>
        ViewModel.DeepSeekModel = "deepseek-reasoner";

    private void OnCustomApiKeyBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnSaveCustomApiKeyClick(sender, new RoutedEventArgs());
        }
    }

    private void OnDeepSeekApiKeyBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnSaveDeepSeekApiKeyClick(sender, new RoutedEventArgs());
        }
    }

    private async Task<string?> PickSingleFileAsync(string extension)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(extension);

        if (_owner is not null)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(_owner));
        }

        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync().AsTask().ConfigureAwait(true);
        return file?.Path;
    }
}

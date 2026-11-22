using System.Diagnostics;
using System.Text;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EhViewer.Desktop.Views;

/// <summary>Downloads queue, archiver tasks, download labels, and local library page.</summary>
public sealed partial class DownloadsPage : Page
{
    private readonly AppServices _services;
    private readonly DownloadManager _manager;
    private readonly ArchiverDownloadManager _archiverManager;
    private readonly AndroidMigrationService _migration;
    private readonly ILogger<DownloadsPage> _logger;
    private readonly Func<LocalGallery, Task>? _openReader;
    private readonly Window? _owner;
    private bool _syncingSelection;

    public DownloadsPage(AppServices services)
        : this(services, openReader: null, owner: null)
    {
    }

    public DownloadsPage(AppServices services, Func<LocalGallery, Task>? openReader, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        InitializeComponent();

        _services = services;
        _manager = services.GetRequiredService<DownloadManager>();
        _archiverManager = services.GetRequiredService<ArchiverDownloadManager>();
        _migration = services.GetRequiredService<AndroidMigrationService>();
        _logger = services.GetRequiredService<ILogger<DownloadsPage>>();
        _openReader = openReader;
        _owner = owner;

        ViewModel = new DownloadsViewModel(
            _manager,
            services.GetRequiredService<LocalLibraryScanner>(),
            services.Paths,
            services.GetRequiredService<EhDatabase>(),
            _archiverManager,
            () => _services.Settings);

        ViewModel.ReloadLabels();
        ViewModel.ReloadDownloads();
        ViewModel.ReloadArchivers();
        _ = ViewModel.ReloadLibraryAsync();
        ViewModel.EnableDirectoryWatcher(action => DispatcherQueue.TryEnqueue(() => action()));

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        AttachDownloadManagerEvents();
    }

    private bool _eventsAttached;
    private bool _hasUnloadedOnce;

    public DownloadsViewModel ViewModel { get; }

    public bool HasText(string? value) => !string.IsNullOrEmpty(value);

    public Visibility HasAnyArchivers(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public ListViewSelectionMode ToSelectionMode(bool multiSelect) =>
        multiSelect ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;

    public string FormatSelectedCount(int count) => $"已选择 {count} 项";

    /// <summary>Progress arrives on worker threads, so it is marshalled onto the UI dispatcher.</summary>
    private void OnDownloadProgressChanged(DownloadProgress progress)
    {
        DispatcherQueue dispatcher = DispatcherQueue;
        if (dispatcher.HasThreadAccess)
        {
            ViewModel.ApplyProgress(progress);
            return;
        }

        if (!dispatcher.TryEnqueue(() => ViewModel.ApplyProgress(progress)))
        {
            _logger.LogDebug("无法投递下载进度更新");
        }
    }

    private void OnArchiverTaskUpdated(object? sender, ArchiverTaskSnapshot snapshot)
    {
        DispatcherQueue dispatcher = DispatcherQueue;
        if (dispatcher.HasThreadAccess)
        {
            ViewModel.ApplyArchiverSnapshot(snapshot);
            return;
        }

        dispatcher.TryEnqueue(() => ViewModel.ApplyArchiverSnapshot(snapshot));
    }

    private void OnArchiverTasksChanged(object? sender, EventArgs e)
    {
        DispatcherQueue dispatcher = DispatcherQueue;
        if (dispatcher.HasThreadAccess)
        {
            ViewModel.ReloadArchivers();
            return;
        }

        dispatcher.TryEnqueue(ViewModel.ReloadArchivers);
    }

    private void AttachDownloadManagerEvents()
    {
        if (_eventsAttached)
        {
            return;
        }

        _manager.ProgressChanged += OnDownloadProgressChanged;
        _archiverManager.TaskUpdated += OnArchiverTaskUpdated;
        _archiverManager.TasksChanged += OnArchiverTasksChanged;
        _eventsAttached = true;
    }

    private void DetachDownloadManagerEvents()
    {
        if (!_eventsAttached)
        {
            return;
        }

        _manager.ProgressChanged -= OnDownloadProgressChanged;
        _archiverManager.TaskUpdated -= OnArchiverTaskUpdated;
        _archiverManager.TasksChanged -= OnArchiverTasksChanged;
        _eventsAttached = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachDownloadManagerEvents();
        if (_hasUnloadedOnce)
        {
            _hasUnloadedOnce = false;
            ViewModel.ReloadLabels();
            ViewModel.ReloadDownloads();
            ViewModel.ReloadArchivers();
            _ = ViewModel.ReloadLibraryAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _hasUnloadedOnce = true;
        DetachDownloadManagerEvents();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ReloadLabels();
        ViewModel.ReloadDownloads();
        ViewModel.ReloadArchivers();
        _ = ViewModel.ReloadLibraryAsync();
    }

    private void OnStartAllClick(object sender, RoutedEventArgs e)
    {
        int started = ViewModel.StartAll();
        ViewModel.ReloadDownloads();
        ViewModel.StatusMessage = started > 0 ? $"已启动 {started} 个下载任务。" : "没有待启动的下载任务。";
    }

    private void OnPauseAllClick(object sender, RoutedEventArgs e)
    {
        int paused = ViewModel.PauseAll();
        ViewModel.ReloadDownloads();
        ViewModel.StatusMessage = paused > 0 ? $"已暂停 {paused} 个下载任务。" : "没有正在下载的任务。";
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: long gid })
        {
            ViewModel.Pause(gid);
        }
    }

    private void OnResumeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: long gid })
        {
            ViewModel.Resume(gid);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: long gid })
        {
            ViewModel.Cancel(gid);
        }
    }

    private async void OnDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: long gid })
        {
            return;
        }

        await ConfirmAndDeleteDownloadsAsync([gid]).ConfigureAwait(true);
    }

    private async void OnBatchDeleteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedGids.Count == 0)
        {
            return;
        }

        await ConfirmAndDeleteDownloadsAsync(ViewModel.SelectedGids.ToArray()).ConfigureAwait(true);
    }

    private async Task ConfirmAndDeleteDownloadsAsync(IReadOnlyList<long> gids)
    {
        try
        {
            var deleteFilesBox = new CheckBox
            {
                Content = "同时从磁盘删除已下载的画廊文件夹",
                IsChecked = true
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "删除下载任务",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = $"确定要移除选中的 {gids.Count} 个下载任务吗？" },
                        deleteFilesBox
                    }
                }
            };

            if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) == ContentDialogResult.Primary)
            {
                bool deleteFiles = deleteFilesBox.IsChecked == true;
                int count = ViewModel.DeleteDownloads(gids, deleteFiles);
                ViewModel.StatusMessage = $"已删除 {count} 个下载记录。";
            }
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"删除失败：{ex.Message}";
        }
    }

    private async void OnAssignLabelClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: long gid })
        {
            return;
        }

        await ShowLabelPickerDialogAsync(label => ViewModel.AssignLabel(gid, label)).ConfigureAwait(true);
    }

    private async void OnBatchAssignLabelClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedGids.Count == 0)
        {
            return;
        }

        await ShowLabelPickerDialogAsync(label =>
        {
            int count = ViewModel.AssignLabelToSelected(label);
            ViewModel.StatusMessage = $"已为 {count} 个任务分配标签：{(string.IsNullOrWhiteSpace(label) ? "未分类" : label)}";
        }).ConfigureAwait(true);
    }

    private async Task ShowLabelPickerDialogAsync(Action<string?> apply)
    {
        try
        {
            IReadOnlyList<DownloadLabel> labels = ViewModel.GetLabels();
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.Items.Add("（清除标签 / 未分类）");
            foreach (DownloadLabel label in labels)
            {
                if (!string.IsNullOrWhiteSpace(label.Label))
                {
                    combo.Items.Add(label.Label);
                }
            }

            combo.SelectedIndex = 0;

            var customBox = new TextBox
            {
                Header = "或输入新标签名",
                PlaceholderText = "输入后优先使用新标签…"
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "分配下载标签",
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = new StackPanel
                {
                    Spacing = 10,
                    MinWidth = 340,
                    Children = { combo, customBox }
                }
            };

            if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) == ContentDialogResult.Primary)
            {
                string? chosen = !string.IsNullOrWhiteSpace(customBox.Text)
                    ? customBox.Text.Trim()
                    : (combo.SelectedIndex > 0 ? combo.SelectedItem as string : null);
                apply(chosen);
            }
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"分配标签失败：{ex.Message}";
        }
    }

    private async void OnManageLabelsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var newLabelBox = new TextBox { PlaceholderText = "新标签名称…", MinWidth = 220 };
            var addButton = new Button { Content = "添加" };
            var list = new ListBox { MaxHeight = 240 };

            void RefreshList()
            {
                list.ItemsSource = ViewModel.GetLabels().ToList();
            }

            list.DisplayMemberPath = nameof(DownloadLabel.Label);
            RefreshList();

            addButton.Click += (_, _) =>
            {
                if (ViewModel.AddLabel(newLabelBox.Text))
                {
                    newLabelBox.Text = string.Empty;
                    RefreshList();
                }
            };

            var renameBox = new TextBox { PlaceholderText = "选中标签重命名为…", MinWidth = 220 };
            var renameButton = new Button { Content = "重命名" };
            var upButton = new Button { Content = "上移" };
            var downButton = new Button { Content = "下移" };
            var deleteButton = new Button { Content = "删除选中" };

            renameButton.Click += (_, _) =>
            {
                if (list.SelectedItem is DownloadLabel selected && ViewModel.RenameLabel(selected, renameBox.Text))
                {
                    renameBox.Text = string.Empty;
                    RefreshList();
                }
            };

            upButton.Click += (_, _) =>
            {
                int idx = list.SelectedIndex;
                if (idx > 0)
                {
                    ViewModel.MoveLabel(idx, idx - 1);
                    RefreshList();
                    list.SelectedIndex = idx - 1;
                }
            };

            downButton.Click += (_, _) =>
            {
                int idx = list.SelectedIndex;
                if (idx >= 0 && idx + 1 < list.Items.Count)
                {
                    ViewModel.MoveLabel(idx, idx + 1);
                    RefreshList();
                    list.SelectedIndex = idx + 1;
                }
            };

            deleteButton.Click += (_, _) =>
            {
                if (list.SelectedItem is DownloadLabel selected)
                {
                    ViewModel.DeleteLabel(selected);
                    RefreshList();
                }
            };

            var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { newLabelBox, addButton } };
            var editRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { renameBox, renameButton } };
            var opsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { upButton, downButton, deleteButton } };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "下载标签管理",
                CloseButtonText = "完成",
                Content = new StackPanel
                {
                    Spacing = 10,
                    MinWidth = 420,
                    Children = { addRow, list, editRow, opsRow }
                }
            };

            await dialog.ShowAsync().AsTask().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"管理标签失败：{ex.Message}";
        }
    }

    private async void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(
                _owner ?? throw new InvalidOperationException("缺少窗口引用，无法打开保存对话框。"));

            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"ehviewer-downloads-{DateTime.Now:yyyyMMdd}"
            };
            picker.FileTypeChoices.Add("CSV 文件", [".csv"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync().AsTask().ConfigureAwait(true);
            if (file is null)
            {
                return;
            }

            string csv = ViewModel.ExportCsv();
            await File.WriteAllTextAsync(file.Path, csv, Encoding.UTF8).ConfigureAwait(true);
            ViewModel.StatusMessage = $"已导出下载清单至：{file.Path}";
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"导出 CSV 失败：{ex.Message}";
        }
    }

    private async void OnImportCsvClick(object sender, RoutedEventArgs e)
    {
        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(
                _owner ?? throw new InvalidOperationException("缺少窗口引用，无法打开文件选择器。"));

            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".csv");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync().AsTask().ConfigureAwait(true);
            if (file is null)
            {
                return;
            }

            string csv = await File.ReadAllTextAsync(file.Path, Encoding.UTF8).ConfigureAwait(true);
            int imported = ViewModel.ImportCsv(csv);
            ViewModel.StatusMessage = $"已从 CSV 导入 {imported} 条下载记录。";
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"导入 CSV 失败：{ex.Message}";
        }
    }

    private async void OnCleanInvalidClick(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<InvalidDownloadCandidate> candidates = await Task
                .Run(ViewModel.FindInvalidDownloads)
                .ConfigureAwait(true);

            if (candidates.Count == 0)
            {
                ViewModel.StatusMessage = "未检测到空目录或不完整下载目录。";
                return;
            }

            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Multiple,
                MaxHeight = 280
            };

            foreach (InvalidDownloadCandidate item in candidates)
            {
                list.Items.Add($"{item.FolderName}  —  {item.Reason}");
                list.SelectedItems.Add(list.Items[^1]);
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"发现 {candidates.Count} 个无效/未完成下载目录",
                PrimaryButtonText = "清理选中目录",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                Content = new StackPanel
                {
                    Spacing = 8,
                    MinWidth = 460,
                    Children =
                    {
                        new TextBlock { Text = "选中的目录及其残余文件将被永久删除：" },
                        list
                    }
                }
            };

            if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) != ContentDialogResult.Primary)
            {
                return;
            }

            var selectedPaths = new List<string>();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (list.SelectedItems.Contains(list.Items[i]))
                {
                    selectedPaths.Add(candidates[i].DirectoryPath);
                }
            }

            int cleaned = ViewModel.CleanInvalidDownloads(selectedPaths);
            ViewModel.StatusMessage = $"已清理 {cleaned} 个无效下载目录。";
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"清理无效下载失败：{ex.Message}";
        }
    }

    private void OnQueueSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection)
        {
            return;
        }

        ViewModel.SetSelectedGids(QueueListView.SelectedItems.OfType<DownloadRow>().Select(static r => r.Gid));
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        _syncingSelection = true;
        try
        {
            QueueListView.SelectAll();
            ViewModel.SelectAllFiltered();
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnClearSelectionClick(object sender, RoutedEventArgs e)
    {
        _syncingSelection = true;
        try
        {
            QueueListView.SelectedItems.Clear();
            ViewModel.ClearSelection();
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnArchiverPauseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string taskId })
        {
            ViewModel.PauseArchiver(taskId);
        }
    }

    private void OnArchiverResumeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string taskId })
        {
            ViewModel.ResumeArchiver(taskId);
        }
    }

    private void OnArchiverCancelClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string taskId })
        {
            ViewModel.CancelArchiver(taskId);
        }
    }

    private void OnArchiverRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string taskId })
        {
            ViewModel.RemoveArchiver(taskId);
        }
    }

    /// <summary>
    /// Port of upstream <c>ArchiveGalleryProvider</c> import: extract the archive into a
    /// <c>{gid}-{title}</c> folder so it appears in the local library like a downloaded gallery.
    /// </summary>
    private async void OnImportArchiveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            foreach (string extension in ArchiveImporter.SupportedExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(
                    _owner ?? throw new InvalidOperationException("缺少窗口引用，无法打开文件选择器。")));

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync().AsTask().ConfigureAwait(true);
            if (file is null)
            {
                return;
            }

            string title = EhViewer.Core.Util.FileUtils.SanitizeFilename(Path.GetFileNameWithoutExtension(file.Name));
            string target = Path.Combine(ViewModel.DownloadRoot, $"0-{title}");

            int pages = await Task
                .Run(() => ArchiveImporter.Import(file.Path, target, _logger))
                .ConfigureAwait(true);

            ViewModel.ReloadLabels();
            ViewModel.ReloadDownloads();
            ViewModel.ReloadArchivers();
            await ViewModel.ReloadLibraryAsync().ConfigureAwait(true);
            ViewModel.ErrorMessage = string.Empty;
            ViewModel.StatusMessage = $"已导入 {pages} 页：{target}";
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"导入归档失败：{ex.Message}";
        }
    }

    private async void OnMigrateAndroidClick(object sender, RoutedEventArgs e)
    {
        try
        {
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(
                _owner ?? throw new InvalidOperationException("缺少窗口引用，无法打开文件选择器。"));

            var ehDbBox = new TextBox { Header = "eh.db 路径（可选）", PlaceholderText = "选择 Android 导出的 eh.db…" };
            var browseEhDb = new Button { Content = "浏览…", VerticalAlignment = VerticalAlignment.Bottom };
            browseEhDb.Click += async (_, _) =>
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".db");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync().AsTask().ConfigureAwait(true);
                if (file is not null)
                {
                    ehDbBox.Text = file.Path;
                }
            };

            var cookieDbBox = new TextBox { Header = "Cookie 数据库路径（可选）", PlaceholderText = "例如 okhttp3-cookie.db…" };
            var browseCookieDb = new Button { Content = "浏览…", VerticalAlignment = VerticalAlignment.Bottom };
            browseCookieDb.Click += async (_, _) =>
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".db");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync().AsTask().ConfigureAwait(true);
                if (file is not null)
                {
                    cookieDbBox.Text = file.Path;
                }
            };

            var downloadDirBox = new TextBox { Header = "Android 下载目录（可选）", PlaceholderText = "包含 {gid}-{title} 的目录…" };
            var browseDir = new Button { Content = "浏览…", VerticalAlignment = VerticalAlignment.Bottom };
            browseDir.Click += async (_, _) =>
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync().AsTask().ConfigureAwait(true);
                if (folder is not null)
                {
                    downloadDirBox.Text = folder.Path;
                }
            };

            static Grid BuildPickerRow(TextBox input, Button button)
            {
                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(input, 0);
                Grid.SetColumn(button, 1);
                grid.Children.Add(input);
                grid.Children.Add(button);
                return grid;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "从 Android EhViewer 迁移",
                PrimaryButtonText = "开始迁移",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = new StackPanel
                {
                    Spacing = 12,
                    MinWidth = 460,
                    Children =
                    {
                        BuildPickerRow(ehDbBox, browseEhDb),
                        BuildPickerRow(cookieDbBox, browseCookieDb),
                        BuildPickerRow(downloadDirBox, browseDir)
                    }
                }
            };

            if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) != ContentDialogResult.Primary)
            {
                return;
            }

            string? ehDb = string.IsNullOrWhiteSpace(ehDbBox.Text) ? null : ehDbBox.Text.Trim();
            string? cookieDb = string.IsNullOrWhiteSpace(cookieDbBox.Text) ? null : cookieDbBox.Text.Trim();
            string? downloadDir = string.IsNullOrWhiteSpace(downloadDirBox.Text) ? null : downloadDirBox.Text.Trim();

            AndroidMigrationReport report = await Task
                .Run(() => _migration.Migrate(ehDb, cookieDb, downloadDir))
                .ConfigureAwait(true);

            ViewModel.ReloadLabels();
            ViewModel.ReloadDownloads();
            ViewModel.ReloadArchivers();
            await ViewModel.ReloadLibraryAsync().ConfigureAwait(true);
            ViewModel.StatusMessage =
                $"迁移完成：数据库 {(report.DatabaseImported ? "已合并" : "未导入")} · Cookie {report.CookiesImported} 条（DPAPI 加密） · 画廊 {report.GalleriesCopied} 个 / {report.FilesCopied} 文件";
            ViewModel.ErrorMessage = report.Warnings.Count > 0 ? string.Join("；", report.Warnings) : string.Empty;
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"迁移失败：{ex.Message}";
        }
    }

    private async void OnLocalGalleryTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (_openReader is null || sender is not FrameworkElement { DataContext: LocalGallery gallery })
        {
            return;
        }

        try
        {
            await _openReader(gallery).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = ex.Message;
        }
    }

    private async void OnLocalGalleryReadMenuClick(object sender, RoutedEventArgs e)
    {
        if (_openReader is null || sender is not FrameworkElement { DataContext: LocalGallery gallery })
        {
            return;
        }

        try
        {
            await _openReader(gallery).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = ex.Message;
        }
    }

    private void OnLocalGalleryOpenFolderMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LocalGallery gallery } || !Directory.Exists(gallery.DirectoryPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = gallery.DirectoryPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"无法打开目录：{ex.Message}";
        }
    }

    private async void OnLocalGalleryDeleteMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LocalGallery gallery })
        {
            return;
        }

        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "删除本地画廊",
                PrimaryButtonText = "永久删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                Content = $"确定要永久删除 \"{gallery.Title}\" 及其目录 {gallery.DirectoryPath} 吗？"
            };

            if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) == ContentDialogResult.Primary)
            {
                ViewModel.DeleteLocalGallery(gallery);
                ViewModel.StatusMessage = $"已删除本地画廊：{gallery.Title}";
            }
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"删除本地画廊失败：{ex.Message}";
        }
    }

    private void OnOpenDirectoryClick(object sender, RoutedEventArgs e)
    {
        string directory = ViewModel.DownloadRoot;
        if (!Directory.Exists(directory))
        {
            ViewModel.ErrorMessage = "下载目录尚不存在。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"无法打开目录：{ex.Message}";
        }
    }
}

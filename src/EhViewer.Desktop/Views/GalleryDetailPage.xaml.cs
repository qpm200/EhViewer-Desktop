using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Network;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EhViewer.Desktop.Views;

/// <summary>Actions injected by MainWindow or DetachedGalleryWindowHost for a gallery detail scene.</summary>
public sealed record GalleryDetailActions(
    Func<GalleryDetail, int?, Task> ReadAsync,
    Func<GalleryDetail, Task> DownloadAsync,
    Func<GalleryDetail, Task> ToggleFavoriteAsync,
    Func<PreviewTileViewModel, CancellationToken, Task<WriteableBitmap?>> LoadPreviewAsync,
    Func<TagChip, Task> SearchTagAsync,
    Func<GalleryDetail, Task> OpenInNewWindowAsync,
    Func<GalleryDetail, float, Task<RateGalleryParser.Result>> RateAsync,
    Func<GalleryDetail, string, Task<GalleryCommentList>> CommentAsync,
    Func<GalleryDetail, int, string?, Task> SaveFavoriteAsync,
    Func<GalleryDetail, Task<ArchiverData>> GetArchiverAsync,
    Func<string, string, string, string, Task<string?>> DownloadArchiverAsync,
    Func<GalleryDetail, long, int, Task<VoteCommentParser.Result>> VoteCommentAsync,
    Func<GalleryDetail, long, Task<GetEditCommentParser.Result>> GetEditCommentAsync,
    Func<GalleryDetail, long, string, Task<GalleryCommentList>> EditCommentAsync,
    Func<GalleryDetail, Task<TorrentInfo[]>> GetTorrentListAsync,
    Func<GalleryDetail, TorrentInfo, Task<string?>> DownloadTorrentAsync,
    Func<GalleryDetail, string, string, bool, Task> StartArchiverDownloadAsync,
    Func<GalleryDetail, Task<ArchiveParser.ParseResult>> GetArchiveListAsync,
    Func<GalleryDetail, string, string, Task> DownloadHathArchiveAsync,
    Func<GalleryDetail, Task> SearchSimilarAsync,
    Func<GalleryInfo, Task> OpenGalleryDetailAsync,
    Func<string, Task> OpenUrlAsync);

/// <summary>
/// Android Material Design GalleryDetailScene:
/// Teal/Dark Hero header + overlapping Read/Download CTA card + clickable Info strip (GalleryInfoPage)
/// + horizontal action row + colored tag chips with filter/search/copy + previews (GalleryPreviewsPage)
/// + comments preview (GalleryCommentsPage).
/// </summary>
public sealed partial class GalleryDetailPage : Page, ISceneAware
{
    private readonly GalleryDetailViewModel _viewModel;
    private readonly GalleryDetailActions _actions;
    private readonly ObservableCollection<CommentViewModel> _commentPreviewItems = [];
    private readonly BoundedLruCache<int, WriteableBitmap> _previewTileCache = new(capacity: 64);
    private readonly ConditionalWeakTable<FrameworkElement, CancellationTokenSource> _previewLoadCts = new();
    private CancellationTokenSource? _previewPageCts;
    private IShellCoordinator? _shell;
    private DownloadManager? _subscribedDownloads;
    private long _loadedCoverGid = -1;
    private long _readProgressGid = -1;
    private int _readProgressVersion;
    private bool _dynamicSyncPending;
    private bool _commentPreviewSyncPending;

    public GalleryDetailPage(GalleryDetailViewModel viewModel, GalleryDetailActions actions)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(actions);

        _viewModel = viewModel;
        _actions = actions;

        InitializeComponent();

        CommentsPreviewRepeater.ItemsSource = _commentPreviewItems;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
        AttachViewModelEvents();

        SyncDynamicState();
        SyncCommentPreview();
    }

    public GalleryDetailViewModel ViewModel => _viewModel;

    public static Visibility VisibleWhenInitialLoading(bool isLoading, bool isLoaded)
        => (isLoading && !isLoaded) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleWhenFatalError(bool hasError, bool isLoaded)
        => (hasError && !isLoaded) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleWhenEmptyCount(int count)
        => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private bool _viewModelEventsAttached;

    public void AttachCoordinator(IShellCoordinator coordinator)
    {
        AttachDownloadSubscription(coordinator);
        SyncDynamicState();
        SyncCommentPreview();
    }

    private void AttachViewModelEvents()
    {
        if (_viewModelEventsAttached)
        {
            return;
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Comments.CollectionChanged += OnCommentsCollectionChanged;
        _viewModel.Previews.CollectionChanged += OnPreviewsCollectionChanged;
        _viewModelEventsAttached = true;
    }

    private void DetachViewModelEvents()
    {
        if (!_viewModelEventsAttached)
        {
            return;
        }

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Comments.CollectionChanged -= OnCommentsCollectionChanged;
        _viewModel.Previews.CollectionChanged -= OnPreviewsCollectionChanged;
        _viewModelEventsAttached = false;
    }

    private void AttachDownloadSubscription(IShellCoordinator? coordinator)
    {
        if (_subscribedDownloads is not null)
        {
            _subscribedDownloads.ProgressChanged -= OnDownloadProgressChanged;
            _subscribedDownloads = null;
        }

        _shell = coordinator;
        if (_shell is not null)
        {
            _subscribedDownloads = _shell.Services.GetRequiredService<DownloadManager>();
            _subscribedDownloads.ProgressChanged += OnDownloadProgressChanged;
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModelEvents();
        if (_subscribedDownloads is null && _shell is not null)
        {
            AttachDownloadSubscription(_shell);
        }

        _readProgressGid = -1;
        ScheduleSyncDynamicState();
        ScheduleSyncCommentPreview();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModelEvents();
        if (_subscribedDownloads is not null)
        {
            _subscribedDownloads.ProgressChanged -= OnDownloadProgressChanged;
            _subscribedDownloads = null;
        }

        _dynamicSyncPending = false;
        _commentPreviewSyncPending = false;
        _readProgressVersion++;
        ResetPreviewLoads();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        string? prop = e.PropertyName;
        if (string.IsNullOrEmpty(prop) ||
            string.Equals(prop, nameof(GalleryDetailViewModel.IsLoaded), StringComparison.Ordinal) ||
            string.Equals(prop, nameof(GalleryDetailViewModel.IsLoading), StringComparison.Ordinal) ||
            string.Equals(prop, nameof(GalleryDetailViewModel.CategoryName), StringComparison.Ordinal) ||
            string.Equals(prop, nameof(GalleryDetailViewModel.RatingText), StringComparison.Ordinal) ||
            string.Equals(prop, nameof(GalleryDetailViewModel.FavoriteButtonText), StringComparison.Ordinal) ||
            string.Equals(prop, nameof(GalleryDetailViewModel.PagesText), StringComparison.Ordinal))
        {
            ScheduleSyncDynamicState();
        }
    }

    private void OnCommentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ScheduleSyncCommentPreview();

    private void OnPreviewsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ResetPreviewLoads();
        }
    }

    private void ResetPreviewLoads()
    {
        CancellationTokenSource? cts = _previewPageCts;
        _previewPageCts = null;
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            cts.Dispose();
        }

        _previewTileCache.Clear();
    }

    private void CancelPreviewElementLoad(FrameworkElement? root)
    {
        if (root is null)
        {
            return;
        }

        if (_previewLoadCts.TryGetValue(root, out CancellationTokenSource? prevCts))
        {
            _previewLoadCts.Remove(root);
            try
            {
                prevCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            prevCts.Dispose();
        }
    }

    private void ScheduleSyncDynamicState()
    {
        if (_dynamicSyncPending)
        {
            return;
        }

        _dynamicSyncPending = true;
        if (DispatcherQueue is { } dq && dq.TryEnqueue(() =>
            {
                _dynamicSyncPending = false;
                SyncDynamicState();
            }))
        {
            return;
        }

        _dynamicSyncPending = false;
        SyncDynamicState();
    }

    private void ScheduleSyncCommentPreview()
    {
        if (_commentPreviewSyncPending)
        {
            return;
        }

        _commentPreviewSyncPending = true;
        if (DispatcherQueue is { } dq && dq.TryEnqueue(() =>
            {
                _commentPreviewSyncPending = false;
                SyncCommentPreview();
            }))
        {
            return;
        }

        _commentPreviewSyncPending = false;
        SyncCommentPreview();
    }

    private void OnDownloadProgressChanged(DownloadProgress progress)
    {
        if (_viewModel.Detail is not { } detail || progress.Gid != detail.Gid)
        {
            return;
        }

        DispatcherQueue?.TryEnqueue(() => UpdateDownloadButtonText(progress));
    }

    private void SyncDynamicState()
    {
        GalleryDetail? detail = _viewModel.Detail;
        if (detail is not null)
        {
            TopGidBadgeText.Text = $"#{detail.Gid} · {_viewModel.CategoryName}";
            HeroCategoryBadge.Background = UiHelpers.CategoryBrush(detail.Category);
            ActionsSimpleRating.Rating = detail.Rating;
            FavoriteActionIcon.Glyph = (detail.IsFavorited || detail.FavoriteSlot == FavListUrlBuilder.FavCatLocal || (_shell?.Services.GetRequiredService<EhDatabase>().ContainLocalFavorites(detail.Gid) ?? false))
                ? "\uEB52"
                : "\uEB51";

            UpdateDownloadButtonText(_subscribedDownloads?.GetProgress(detail.Gid));
            UpdateReadProgressLabel(detail);

            if (_loadedCoverGid != detail.Gid && !string.IsNullOrWhiteSpace(detail.Thumb))
            {
                _loadedCoverGid = detail.Gid;
                _ = LoadHeroCoverAsync(detail);
            }
        }
    }

    private async Task LoadHeroCoverAsync(GalleryDetail detail)
    {
        try
        {
            if (_shell is null)
            {
                return;
            }

            WriteableBitmap? bmp = await _shell.LoadThumbnailAsync(detail.Thumb, 400).ConfigureAwait(true);
            if (bmp is not null && _viewModel.Detail?.Gid == detail.Gid)
            {
                HeroCoverImage.Source = bmp;
                CoverPlaceholderIcon.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            // Keep placeholder when thumbnail fails.
        }
    }

    private async void UpdateReadProgressLabel(GalleryDetail detail)
    {
        if (_shell is null)
        {
            ReadCtaLabel.Text = "开始阅读";
            return;
        }

        if (_readProgressGid == detail.Gid)
        {
            return;
        }

        _readProgressGid = detail.Gid;
        int version = ++_readProgressVersion;
        string downloadRoot = _shell.Services.Paths.GetEffectiveDownloadDirectory(_shell.Services.Settings);
        string progressPath = ReaderViewModel.BuildProgressPath(downloadRoot, detail);

        int savedPage;
        try
        {
            savedPage = await Task.Run(() => ReaderViewModel.ReadSavedStartPage(progressPath)).ConfigureAwait(true);
        }
        catch
        {
            return;
        }

        if (_readProgressVersion != version || _viewModel.Detail?.Gid != detail.Gid)
        {
            return;
        }

        if (savedPage > 0 && (detail.Pages <= 0 || savedPage < detail.Pages))
        {
            ReadCtaLabel.Text = detail.Pages > 0
                ? $"继续阅读 · P{savedPage + 1}/{detail.Pages}"
                : $"继续阅读 · 第 {savedPage + 1} 页";
        }
        else
        {
            ReadCtaLabel.Text = "开始阅读";
        }
    }

    private void UpdateDownloadButtonText(DownloadProgress? progress)
    {
        if (progress is null)
        {
            DownloadCtaLabel.Text = "下载画廊";
            return;
        }

        DownloadCtaLabel.Text = progress.State switch
        {
            DownloadState.Wait => "等待下载...",
            DownloadState.Download when progress.IsPaused => progress.Total > 0 ? $"已暂停 ({progress.Done}/{progress.Total})" : "已暂停",
            DownloadState.Download or DownloadState.Update => progress.Total > 0 ? $"下载中 {progress.Done}/{progress.Total}" : "下载中...",
            DownloadState.Finish => "已下载（更新/检查）",
            DownloadState.Failed => "下载失败 · 点击重试",
            _ => "下载画廊"
        };
    }

    private void SyncCommentPreview()
    {
        _commentPreviewItems.Clear();
        int count = _viewModel.Comments.Count;
        int showCount = Math.Min(6, count);
        for (int i = 0; i < showCount; i++)
        {
            _commentPreviewItems.Add(_viewModel.Comments[i]);
        }

        CommentsHeaderText.Text = count > 0 ? $"评论 ({count})" : "评论";
        CommentsEmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- Navigation & Header Actions ------------------------------------------

    private void OnBackClick(object sender, RoutedEventArgs e) => _shell?.GoBack();

    private async void OnRefreshDetailClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is not { } detail)
        {
            return;
        }

        await _viewModel.LoadAsync(detail).ConfigureAwait(true);
    }

    private async void OnOpenInNewWindowClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is { } detail)
        {
            await _actions.OpenInNewWindowAsync(detail).ConfigureAwait(true);
        }
    }

    private void OnHeaderOverflowClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is not { } detail || sender is not FrameworkElement anchor)
        {
            return;
        }

        var flyout = new MenuFlyout();

        var infoItem = new MenuFlyoutItem { Text = "画廊详细信息", Icon = new FontIcon { Glyph = "\uE946" } };
        infoItem.Click += (_, _) => OpenInfoSubScene();
        flyout.Items.Add(infoItem);

        var copyUrlItem = new MenuFlyoutItem { Text = "复制画廊链接", Icon = new FontIcon { Glyph = "\uE8C8" } };
        copyUrlItem.Click += (_, _) => CopyGalleryUrl();
        flyout.Items.Add(copyUrlItem);

        var openBrowserItem = new MenuFlyoutItem { Text = "在外部浏览器打开", Icon = new FontIcon { Glyph = "\uE774" } };
        openBrowserItem.Click += (_, _) => OpenDetailUrlInBrowser();
        flyout.Items.Add(openBrowserItem);

        if (_shell is not null)
        {
            var newDetailWin = new MenuFlyoutItem { Text = "在新窗口打开详情", Icon = new FontIcon { Glyph = "\uE8A7" } };
            newDetailWin.Click += (_, _) => _ = _shell.OpenGalleryInNewWindowAsync(detail);
            flyout.Items.Add(newDetailWin);
        }

        flyout.ShowAt(anchor);
    }

    private void OnOpenInBrowserClick(object sender, RoutedEventArgs e) => OpenDetailUrlInBrowser();

    private void OpenDetailUrlInBrowser()
    {
        if (_viewModel.Detail is not { } detail)
        {
            return;
        }

        EhSite site = _shell?.Services.Settings.GallerySite ?? EhSite.E;
        string url = EhUrl.GetGalleryDetailUrl(detail.Gid, detail.Token ?? string.Empty, 0, false, site);
        _ = _actions.OpenUrlAsync(url);
    }

    private void CopyGalleryUrl()
    {
        if (_viewModel.Detail is not { } detail)
        {
            return;
        }

        EhSite site = _shell?.Services.Settings.GallerySite ?? EhSite.E;
        string url = EhUrl.GetGalleryDetailUrl(detail.Gid, detail.Token ?? string.Empty, 0, false, site);
        ClipboardService.CopyText(url);
        _shell?.SetStatus($"已复制画廊链接：{url}");
    }

    private async void OnCoverTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_viewModel.Detail is { } detail)
        {
            await _actions.ReadAsync(detail, 0).ConfigureAwait(true);
        }
    }

    private async void OnUploaderClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is not { Uploader.Length: > 0 } detail)
        {
            return;
        }

        string query = $"uploader:\"{detail.Uploader}\"";
        if (_shell is not null)
        {
            await _shell.OpenSearchKeywordAsync(query, pushToBackstack: true).ConfigureAwait(true);
        }
        else
        {
            await _actions.SearchTagAsync(new TagChip(query, null)).ConfigureAwait(true);
        }
    }

    private async void OnOpenParentClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.ParentUrl))
        {
            await _actions.OpenUrlAsync(_viewModel.ParentUrl).ConfigureAwait(true);
        }
    }

    private void OnVersionsPickerClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Versions.Count == 0 || sender is not FrameworkElement anchor)
        {
            return;
        }

        var flyout = new MenuFlyout();
        for (int i = 0; i < _viewModel.Versions.Count; i++)
        {
            NewVersion version = _viewModel.Versions[i];
            int capturedIndex = i;
            var item = new MenuFlyoutItem { Text = version.VersionName ?? $"版本 #{i + 1}" };
            item.Click += async (_, _) =>
            {
                if (_viewModel.Detail?.GetNewGalleryDetail(capturedIndex) is { } target)
                {
                    await _actions.OpenGalleryDetailAsync(target).ConfigureAwait(true);
                }
                else if (!string.IsNullOrWhiteSpace(version.VersionUrl))
                {
                    await _actions.OpenUrlAsync(version.VersionUrl!).ConfigureAwait(true);
                }
            };
            flyout.Items.Add(item);
        }

        flyout.ShowAt(anchor);
    }

    // --- CTA Card Actions -----------------------------------------------------

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is not { } detail)
        {
            return;
        }

        await _actions.DownloadAsync(detail).ConfigureAwait(true);
        UpdateDownloadButtonText(_subscribedDownloads?.GetProgress(detail.Gid));
    }

    private async void OnReadClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is { } detail)
        {
            await _actions.ReadAsync(detail, null).ConfigureAwait(true);
        }
    }

    // --- Sub-Scene Push Actions (Info / Previews / Comments) -----------------

    private void OnInfoCardTapped(object sender, TappedRoutedEventArgs e) => OpenInfoSubScene();

    private void OpenInfoSubScene()
    {
        if (_viewModel.Detail is not { } detail)
        {
            return;
        }

        if (_shell is not null)
        {
            var infoPage = new GalleryInfoPage(detail, _shell);
            _shell.PushSubScene(infoPage, "画廊详情信息");
        }
    }

    private void OnOpenAllPreviewsClick(object sender, RoutedEventArgs e)
    {
        if (_shell is not null)
        {
            _shell.PushSubScene(new GalleryPreviewsPage(_viewModel, _actions, _shell), $"全部预览 · {_viewModel.Title}");
        }
    }

    private void OnOpenAllCommentsClick(object sender, RoutedEventArgs e)
    {
        if (_shell is not null)
        {
            _shell.PushSubScene(new GalleryCommentsPage(_viewModel, _actions, _shell), $"全部评论 · {_viewModel.Title}");
        }
    }

    // --- Action Row Dialogs (Favorite / Rate / Torrent / Archiver / H@H / Similar / Cover / Share) ---

    private async void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is not { } detail)
        {
            return;
        }

        if (_shell is not null)
        {
            int? chosen = await _shell.PickFavoriteSlotDialogAsync(detail).ConfigureAwait(true);
            if (chosen is { } slot)
            {
                bool isFav = slot >= 0 || slot == FavListUrlBuilder.FavCatLocal;
                detail.IsFavorited = isFav;
                detail.FavoriteSlot = isFav ? slot : -1;
                _viewModel.UpdateFavoriteState(isFav, detail.FavoriteSlot);
                SyncDynamicState();
            }
            return;
        }

        await _actions.ToggleFavoriteAsync(detail).ConfigureAwait(true);
        _viewModel.UpdateFavoriteState(detail.IsFavorited, detail.FavoriteSlot);
        SyncDynamicState();
    }

    private async void OnFavoriteRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_viewModel.Detail is not { } detail)
        {
            return;
        }

        await _actions.ToggleFavoriteAsync(detail).ConfigureAwait(true);
        _viewModel.UpdateFavoriteState(detail.IsFavorited, detail.FavoriteSlot);
        SyncDynamicState();
    }

    private void OnRateClick(object sender, RoutedEventArgs e) => _ = ShowRateDialogAsync();
    private void OnRatingStripTapped(object sender, TappedRoutedEventArgs e) => _ = ShowRateDialogAsync();

    private async Task ShowRateDialogAsync()
    {
        if (_viewModel.Detail is not { } detail || XamlRoot is null)
        {
            return;
        }

        float initial = detail.Rating > 0
            ? (float)(Math.Round(Math.Clamp(detail.Rating, 0.5f, 5.0f) * 2.0, MidpointRounding.AwayFromZero) / 2.0)
            : 4.5f;

        var starPreview = new Controls.SimpleRatingView { StarSize = 28, Rating = initial, HorizontalAlignment = HorizontalAlignment.Center };
        var scoreLabel = new TextBlock
        {
            Text = $"{initial:0.0} 星",
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var slider = new Slider
        {
            Minimum = 0.5,
            Maximum = 5.0,
            StepFrequency = 0.5,
            TickFrequency = 0.5,
            Value = initial,
            MinWidth = 260
        };
        slider.ValueChanged += (_, args) =>
        {
            double v = Math.Round(args.NewValue * 2.0, MidpointRounding.AwayFromZero) / 2.0;
            starPreview.Rating = v;
            scoreLabel.Text = $"{v:0.0} 星";
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "为画廊评分",
            PrimaryButtonText = "提交评分",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 12,
                Children = { starPreview, scoreLabel, slider }
            }
        };

        if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            RateGalleryParser.Result res = await _actions.RateAsync(detail, (float)slider.Value).ConfigureAwait(true);
            _viewModel.UpdateRating(res);
            ActionsSimpleRating.Rating = res.Rating;
            _shell?.SetStatus($"评分已更新：{res.Rating:0.00} ({res.RatingCount})");
        }
        catch (Exception ex)
        {
            _shell?.SetStatus($"评分失败：{GalleryBrowseService.DescribeFailure(ex)}");
        }
    }

    private async void OnTorrentClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is not { } detail || XamlRoot is null)
        {
            return;
        }

        try
        {
            _shell?.SetStatus("正在获取种子列表...");
            TorrentInfo[] torrents = await _actions.GetTorrentListAsync(detail).ConfigureAwait(true);
            if (torrents.Length == 0)
            {
                _shell?.SetStatus("该画廊暂无种子。");
                return;
            }

            var list = new StackPanel { Spacing = 8 };
            ContentDialog? dialog = null;

            foreach (TorrentInfo torrent in torrents)
            {
                var btn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 8, 12, 8),
                    Content = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = torrent.Name ?? "未命名种子", TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                            new TextBlock { Text = torrent.Posted ?? string.Empty, FontSize = 11.5, Opacity = 0.72 }
                        }
                    }
                };
                btn.Click += async (_, _) =>
                {
                    btn.IsEnabled = false;
                    try
                    {
                        await _actions.DownloadTorrentAsync(detail, torrent).ConfigureAwait(true);
                        dialog?.Hide();
                    }
                    catch (Exception ex)
                    {
                        _shell?.SetStatus($"下载种子失败：{ex.Message}");
                        btn.IsEnabled = true;
                    }
                };
                list.Children.Add(btn);
            }

            dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"种子下载 ({torrents.Length})",
                CloseButtonText = "关闭",
                Content = new ScrollViewer { MaxHeight = 420, MinWidth = 440, Content = list }
            };

            await dialog.ShowAsync().AsTask().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _shell?.SetStatus($"加载种子列表失败：{GalleryBrowseService.DescribeFailure(ex)}");
        }
    }

    private async void OnArchiverClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is not { } detail || XamlRoot is null)
        {
            return;
        }

        try
        {
            _shell?.SetStatus("正在读取 Archiver 信息...");
            ArchiverData data = await _actions.GetArchiverAsync(detail).ConfigureAwait(true);
            string referer = detail.ArchiveUrl ?? string.Empty;

            var autoImportCheck = new CheckBox { Content = "下载完成后自动导入到本地画廊库", IsChecked = true };
            var infoPanel = new StackPanel { Spacing = 10 };

            if (!string.IsNullOrWhiteSpace(data.Funds))
            {
                infoPanel.Children.Add(new TextBlock { Text = $"当前余额：{data.Funds}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            }

            async Task TriggerArchiveAsync(string? postUrl, string dltype, string dlcheck, string resLabel)
            {
                if (string.IsNullOrWhiteSpace(postUrl))
                {
                    _shell?.SetStatus("Archiver 表单地址缺失。");
                    return;
                }

                string? dlUrl = await _actions.DownloadArchiverAsync(postUrl!, referer, dltype, dlcheck).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(dlUrl))
                {
                    _shell?.SetStatus("未返回归档下载链接（请检查 GP/Credits 余额）。");
                    return;
                }

                await _actions.StartArchiverDownloadAsync(detail, dlUrl!, resLabel, autoImportCheck.IsChecked == true).ConfigureAwait(true);
            }

            ContentDialog? dialog = null;

            var orgBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = $"下载原图归档 ({data.OriginalSize ?? "未知大小"} · {data.OriginalCost ?? "未知消耗"})",
                IsEnabled = !string.IsNullOrWhiteSpace(data.OriginalUrl)
            };
            orgBtn.Click += async (_, _) =>
            {
                orgBtn.IsEnabled = false;
                try
                {
                    await TriggerArchiveAsync(data.OriginalUrl, "org", "Download Original Archive", "original").ConfigureAwait(true);
                    dialog?.Hide();
                }
                catch (Exception ex)
                {
                    _shell?.SetStatus($"Archiver 请求失败：{ex.Message}");
                    orgBtn.IsEnabled = true;
                }
            };

            var resBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = $"下载重采样归档 ({data.ResampleSize ?? "未知大小"} · {data.ResampleCost ?? "未知消耗"})",
                IsEnabled = !string.IsNullOrWhiteSpace(data.ResampleUrl)
            };
            resBtn.Click += async (_, _) =>
            {
                resBtn.IsEnabled = false;
                try
                {
                    await TriggerArchiveAsync(data.ResampleUrl, "res", "Download Resample Archive", "resample").ConfigureAwait(true);
                    dialog?.Hide();
                }
                catch (Exception ex)
                {
                    _shell?.SetStatus($"Archiver 请求失败：{ex.Message}");
                    resBtn.IsEnabled = true;
                }
            };

            infoPanel.Children.Add(autoImportCheck);
            infoPanel.Children.Add(orgBtn);
            infoPanel.Children.Add(resBtn);

            dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Archiver 归档下载",
                CloseButtonText = "关闭",
                Content = infoPanel
            };

            await dialog.ShowAsync().AsTask().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _shell?.SetStatus($"打开 Archiver 失败：{GalleryBrowseService.DescribeFailure(ex)}");
        }
    }

    private async void OnHathClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is not { } detail || XamlRoot is null)
        {
            return;
        }

        try
        {
            _shell?.SetStatus("正在获取 H@H 分辨率列表...");
            ArchiveParser.ParseResult res = await _actions.GetArchiveListAsync(detail).ConfigureAwait(true);
            if (res.Items.Length == 0)
            {
                _shell?.SetStatus("当前账号或画廊没有可用的 H@H 下载选项。");
                return;
            }

            var panel = new StackPanel { Spacing = 8 };
            ContentDialog? dialog = null;

            foreach (ArchiveParser.ArchiveItem item in res.Items)
            {
                var btn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = $"{item.Name} ({item.Res})"
                };
                btn.Click += async (_, _) =>
                {
                    btn.IsEnabled = false;
                    try
                    {
                        await _actions.DownloadHathArchiveAsync(detail, res.ParamOr, item.Res).ConfigureAwait(true);
                        dialog?.Hide();
                    }
                    catch (Exception ex)
                    {
                        _shell?.SetStatus($"发送 H@H 任务失败：{GalleryBrowseService.DescribeFailure(ex)}");
                        btn.IsEnabled = true;
                    }
                };
                panel.Children.Add(btn);
            }

            dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "H@H 远程下载",
                CloseButtonText = "关闭",
                Content = panel
            };

            await dialog.ShowAsync().AsTask().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _shell?.SetStatus($"加载 H@H 选项失败：{GalleryBrowseService.DescribeFailure(ex)}");
        }
    }

    private async void OnSimilarClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is { } detail)
        {
            await _actions.SearchSimilarAsync(detail).ConfigureAwait(true);
        }
    }

    private async void OnSearchCoverClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Detail is not { Thumb.Length: > 0 } detail || _shell is null)
        {
            _shell?.SetStatus("当前画廊没有可用的封面地址。");
            return;
        }

        try
        {
            _shell.SetStatus("正在准备封面图片用于以图搜图...");
            if (!Uri.TryCreate(detail.Thumb, UriKind.Absolute, out Uri? _))
            {
                _shell.SetStatus("封面地址无效。");
                return;
            }

            byte[]? bytes = await _shell.Services
                .GetRequiredService<ThumbnailService>()
                .LoadRawBytesAsync(detail.Thumb, _shell.LifetimeToken)
                .ConfigureAwait(true);
            if (bytes is null || bytes.Length == 0)
            {
                _shell.SetStatus("无法获取封面图片。");
                return;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "EhViewerDesktopCovers");
            Directory.CreateDirectory(tempDir);
            string tempFile = Path.Combine(tempDir, $"{detail.Gid}_cover.jpg");
            await File.WriteAllBytesAsync(tempFile, bytes, _shell.LifetimeToken).ConfigureAwait(true);

            await _shell.OpenImageSearchAsync(
                tempFile,
                useSimilarityScan: true,
                onlySearchCovers: false,
                showExpunged: false,
                pushToBackstack: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _shell.SetStatus($"以封面搜图失败：{GalleryBrowseService.DescribeFailure(ex)}");
        }
    }

    private void OnShareClick(object sender, RoutedEventArgs e) => CopyGalleryUrl();

    // --- Tag Chips Menu (Search / Copy / Add Filter / Manage Filters) ---------

    private void OnTagChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TagChip chip } anchor)
        {
            return;
        }

        var flyout = new MenuFlyout();

        var searchItem = new MenuFlyoutItem
        {
            Text = $"搜索标签 ({chip.SearchQuery})",
            Icon = new FontIcon { Glyph = "\uE721" }
        };
        searchItem.Click += async (_, _) => await _actions.SearchTagAsync(chip).ConfigureAwait(true);
        flyout.Items.Add(searchItem);

        var copyItem = new MenuFlyoutItem
        {
            Text = "复制标签查询词",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyItem.Click += (_, _) =>
        {
            ClipboardService.CopyText(chip.SearchQuery);
            _shell?.SetStatus($"已复制标签：{chip.SearchQuery}");
        };
        flyout.Items.Add(copyItem);

        if (_shell is not null)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            string filterToken = string.IsNullOrWhiteSpace(chip.GroupName) || chip.Tag.Contains(':', StringComparison.Ordinal)
                ? chip.Tag
                : $"{chip.GroupName}:{chip.Tag}";

            var filterItem = new MenuFlyoutItem
            {
                Text = $"加入全局屏蔽规则 ({filterToken})",
                Icon = new FontIcon { Glyph = "\uE72E" }
            };
            filterItem.Click += (_, _) =>
            {
                var filter = _shell.Services.GetRequiredService<EhFilter>();
                filter.AddFilter(new FilterRule
                {
                    Mode = EhFilter.ModeTag,
                    Text = filterToken,
                    Enable = true
                });
                _shell.SetStatus($"已屏蔽标签：{filterToken}");
            };
            flyout.Items.Add(filterItem);

            var manageItem = new MenuFlyoutItem
            {
                Text = "管理过滤规则...",
                Icon = new FontIcon { Glyph = "\uE71C" }
            };
            manageItem.Click += (_, _) => _ = _shell.ShowFilterManagerDialogAsync();
            flyout.Items.Add(manageItem);
        }

        flyout.ShowAt(anchor);
    }

    // --- Preview Strip Handlers -----------------------------------------------

    private async void OnPreviewElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement root || root.Tag is not PreviewTileViewModel tile)
        {
            return;
        }

        if (root is not Border { Child: Grid grid })
        {
            return;
        }

        Image? img = grid.Children.OfType<Image>().FirstOrDefault();
        if (img is null)
        {
            return;
        }

        FontIcon? placeholder = grid.Children.OfType<FontIcon>().FirstOrDefault();

        CancelPreviewElementLoad(root);

        if (_previewTileCache.TryGet(tile.Position, out WriteableBitmap? cached))
        {
            img.Source = cached;
            if (placeholder is not null)
            {
                placeholder.Visibility = Visibility.Collapsed;
            }
            return;
        }

        img.Source = null;
        if (placeholder is not null)
        {
            placeholder.Visibility = Visibility.Visible;
        }

        _previewPageCts ??= new CancellationTokenSource();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_previewPageCts.Token);
        CancellationToken ct = cts.Token;
        _previewLoadCts.AddOrUpdate(root, cts);

        try
        {
            WriteableBitmap? bmp = await _actions.LoadPreviewAsync(tile, ct).ConfigureAwait(true);
            if (bmp is not null && !ct.IsCancellationRequested && ReferenceEquals(root.Tag, tile))
            {
                _previewTileCache.Set(tile.Position, bmp);
                img.Source = bmp;
                if (placeholder is not null)
                {
                    placeholder.Visibility = Visibility.Collapsed;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Keep subtle placeholder tile on network failure.
        }
        finally
        {
            if (_previewLoadCts.TryGetValue(root, out CancellationTokenSource? current) && ReferenceEquals(current, cts))
            {
                _previewLoadCts.Remove(root);
                cts.Dispose();
            }
        }
    }

    private void OnPreviewElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is FrameworkElement root)
        {
            CancelPreviewElementLoad(root);
        }

        if (args.Element is Border { Child: Grid grid })
        {
            if (grid.Children.OfType<Image>().FirstOrDefault() is { } img)
            {
                img.Source = null;
            }

            if (grid.Children.OfType<FontIcon>().FirstOrDefault() is { } placeholder)
            {
                placeholder.Visibility = Visibility.Visible;
            }
        }
    }

    private async void OnPreviewTileTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_viewModel.Detail is { } detail && sender is FrameworkElement { Tag: PreviewTileViewModel tile })
        {
            await _actions.ReadAsync(detail, tile.Position).ConfigureAwait(true);
        }
    }

    private async void OnPrevPreviewPageClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CanPreviousPreview)
        {
            await _viewModel.LoadPreviewPageAsync(_viewModel.CurrentPreviewPage - 1).ConfigureAwait(true);
        }
    }

    private async void OnNextPreviewPageClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CanNextPreview)
        {
            await _viewModel.LoadPreviewPageAsync(_viewModel.CurrentPreviewPage + 1).ConfigureAwait(true);
        }
    }
}

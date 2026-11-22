using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Network;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Controls;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Theming;
using EhViewer.Desktop.ViewModels;
using EhViewer.Desktop.Views;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EhViewer.Desktop;

public sealed partial class MainWindow
{
    private sealed record SceneRecord(
        Page Page,
        string Title,
        FrameworkElement? RightDrawerContent,
        string? RightDrawerTitle);

    private readonly Stack<SceneRecord> _sceneStack = new();
    private FrameworkElement? _currentRightDrawerContent;
    private string _currentRightDrawerTitle = "辅助栏";
    private string _currentSceneTitle = "首页";
    private long _quotaFetchGeneration;
    private int _rightDrawerAnimVersion;

    private async Task SelectAndNavigateRootAsync(NavigationKind kind)
    {
        if (NavigationItems.ItemsSource is IReadOnlyList<NavigationEntry> items)
        {
            int idx = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Kind == kind)
                {
                    idx = i;
                    break;
                }
            }

            if (idx >= 0)
            {
                _suppressNavChange = true;
                NavigationItems.SelectedIndex = idx;
                _suppressNavChange = false;
            }
        }

        await NavigateToRootKindAsync(kind).ConfigureAwait(true);
    }

    private async Task NavigateToRootKindAsync(NavigationKind kind, bool deselectLeftDrawer = false)
    {
        if (deselectLeftDrawer)
        {
            _suppressNavChange = true;
            NavigationItems.SelectedItem = null;
            _suppressNavChange = false;
        }

        CloseRightDrawer();
        DismissHoverPreview();
        SetReaderFullscreen(false);
        ClearSceneStack();
        ReleaseScenePage(SceneContentHost.Content as Page);

        try
        {
            switch (kind)
            {
                case NavigationKind.Home:
                {
                    var listPage = new GalleryListScenePage(
                        this,
                        new ListUrlBuilder { Mode = ListUrlBuilder.ModeNormal },
                        "首页");
                    SetRootScene(listPage, "首页", listPage.GetOrCreateQuickSearchDrawer(), "快速搜索");
                    await listPage.InitializeLoadAsync().ConfigureAwait(true);
                    break;
                }

                case NavigationKind.WhatsHot:
                {
                    var listPage = new GalleryListScenePage(
                        this,
                        new ListUrlBuilder { Mode = ListUrlBuilder.ModeWhatsHot },
                        "热门");
                    SetRootScene(listPage, "热门", listPage.GetOrCreateQuickSearchDrawer(), "快速搜索");
                    await listPage.InitializeLoadAsync().ConfigureAwait(true);
                    break;
                }

                case NavigationKind.Watched:
                {
                    var listPage = new GalleryListScenePage(
                        this,
                        new ListUrlBuilder { Mode = ListUrlBuilder.ModeSubscription },
                        "订阅");
                    SetRootScene(listPage, "订阅", listPage.GetOrCreateQuickSearchDrawer(), "快速搜索");
                    await listPage.InitializeLoadAsync().ConfigureAwait(true);
                    break;
                }

                case NavigationKind.Favorites:
                {
                    var favVm = SharedGalleryActions.CreateFavoritesViewModel(_services);
                    var page = new FavoritesPage(
                        favVm,
                        OpenDetailAsync,
                        item => LoadThumbnailAsync(item.ThumbUrl, 320),
                        EnqueueDownload);
                    AttachCoordinatorIfSupported(page);
                    SetRootScene(page, "收藏夹");
                    await favVm.LoadFirstPageAsync().ConfigureAwait(true);
                    break;
                }

                case NavigationKind.TopList:
                {
                    var vm = SharedGalleryActions.CreateTopListViewModel(_services);
                    var page = new TopListPage(vm, OpenTopListRowAsync);
                    AttachCoordinatorIfSupported(page);
                    SetRootScene(page, "排行榜");
                    await vm.LoadAsync().ConfigureAwait(true);
                    break;
                }

                case NavigationKind.History:
                {
                    var vm = new HistoryViewModel(_services.GetRequiredService<EhDatabase>());
                    var page = new HistoryPage(vm, row => OpenDetailAsync(row.Info));
                    AttachCoordinatorIfSupported(page);
                    SetRootScene(page, "历史记录");
                    break;
                }

                case NavigationKind.Downloads:
                {
                    var page = new DownloadsPage(_services, OpenLocalReaderAsync, this);
                    AttachCoordinatorIfSupported(page);
                    SetRootScene(page, "下载管理");
                    break;
                }

                case NavigationKind.Login:
                {
                    var vm = new LoginViewModel(
                        _services.GetRequiredService<EhEngine>(),
                        _services.GetRequiredService<ICookieStore>(),
                        _services.GetRequiredService<GalleryBrowseService>(),
                        _services.GetRequiredService<UConfigSyncService>(),
                        _services);
                    var page = new LoginPage(vm);
                    page.SignedIn += (_, _) =>
                    {
                        RefreshAccountAndQuota(fetchQuotaIfSignedIn: true);
                        SetStatus("登录成功，已同步账号与站点 Cookie。");
                    };
                    AttachCoordinatorIfSupported(page);
                    SetRootScene(page, "账号与配额");
                    break;
                }

                case NavigationKind.Settings:
                {
                    var page = new SettingsPage(_services, this);
                    page.SettingsSaved += (_, _) =>
                    {
                        ApplyAppearance();
                        UpdateSiteSwitchBadge();
                        ApplyBossKeyAndPrivacySettings();
                        SetStatus("设置已保存。");
                    };
                    AttachCoordinatorIfSupported(page);
                    SetRootScene(page, "设置");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导航失败：{Kind}", kind);
            SetStatus(GalleryBrowseService.DescribeFailure(ex));
        }
    }

    private void AttachCoordinatorIfSupported(Page page)
    {
        if (page is ISceneAware sceneAware)
        {
            sceneAware.AttachCoordinator(this);
        }
    }

    private void SetRootScene(
        Page page,
        string title,
        FrameworkElement? rightDrawerContent = null,
        string? rightDrawerTitle = null)
    {
        bool hadContent = SceneContentHost.Content is not null;
        _currentSceneTitle = title;
        SceneContentHost.Content = page;
        SetRightDrawer(rightDrawerContent, rightDrawerTitle);
        SetStatus(title);
        if (hadContent)
        {
            ThemeMotionHelper.AnimateSceneTransition(
                SceneContentHost,
                SceneNavigationDirection.RootSwitch,
                MaterialThemeService.ActiveBehavior);
        }
    }

    public void PushSubScene(
        Page page,
        string title,
        FrameworkElement? rightDrawerContent = null,
        string? rightDrawerTitle = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        CloseRightDrawer();
        DismissHoverPreview();

        if (SceneContentHost.Content is Page current && !ReferenceEquals(current, page))
        {
            _sceneStack.Push(new SceneRecord(current, _currentSceneTitle, _currentRightDrawerContent, _currentRightDrawerTitle));
        }

        _currentSceneTitle = title;
        AttachCoordinatorIfSupported(page);
        SceneContentHost.Content = page;
        SetRightDrawer(rightDrawerContent, rightDrawerTitle);
        SetStatus(title);
        ThemeMotionHelper.AnimateSceneTransition(
            SceneContentHost,
            SceneNavigationDirection.ForwardPush,
            MaterialThemeService.ActiveBehavior);
    }

    public void GoBack() => HandleBackAction();

    private void HandleBackAction()
    {
        CloseRightDrawer();
        DismissHoverPreview();
        SetReaderFullscreen(false);

        if (_sceneStack.Count == 0)
        {
            return;
        }

        Page? leaving = SceneContentHost.Content as Page;
        SceneRecord prev = _sceneStack.Pop();
        ReleaseScenePage(leaving);

        _currentSceneTitle = prev.Title;
        SceneContentHost.Content = prev.Page;
        SetRightDrawer(prev.RightDrawerContent, prev.RightDrawerTitle);
        SetStatus(prev.Title);
        ThemeMotionHelper.AnimateSceneTransition(
            SceneContentHost,
            SceneNavigationDirection.BackPop,
            MaterialThemeService.ActiveBehavior);
    }

    private void ClearSceneStack()
    {
        while (_sceneStack.Count > 0)
        {
            ReleaseScenePage(_sceneStack.Pop().Page);
        }
    }

    private static void ReleaseScenePage(Page? page)
    {
        if (page is ReaderPage reader)
        {
            reader.ViewModel.Dispose();
        }
        else if (page is GalleryListScenePage list)
        {
            list.ReleaseCts();
        }
        else if (page is DownloadsPage downloads)
        {
            downloads.ViewModel.Dispose();
        }
    }

    public void SetRightDrawer(FrameworkElement? content, string? title)
    {
        _currentRightDrawerContent = content;
        _currentRightDrawerTitle = string.IsNullOrWhiteSpace(title) ? "辅助栏" : title!;
        RightDrawerTitleText.Text = _currentRightDrawerTitle;
        RightDrawerContentHost.Content = content;
    }

    public void OpenRightDrawer()
    {
        if (_currentRightDrawerContent is null)
        {
            return;
        }

        _rightDrawerAnimVersion++;
        RightDrawerOverlay.IsHitTestVisible = true;
        RightDrawerOverlay.Visibility = Visibility.Visible;
        ThemeMotionHelper.AnimateOverlayShow(
            RightDrawerPanel,
            RightDrawerScrim,
            MaterialThemeService.ActiveBehavior,
            fromRight: true);
    }

    public void CloseRightDrawer()
    {
        if (RightDrawerOverlay.Visibility != Visibility.Visible || !RightDrawerOverlay.IsHitTestVisible)
        {
            return;
        }

        int version = ++_rightDrawerAnimVersion;
        RightDrawerOverlay.IsHitTestVisible = false;
        ThemeMotionHelper.AnimateOverlayHide(
            RightDrawerPanel,
            RightDrawerScrim,
            MaterialThemeService.ActiveBehavior,
            () =>
            {
                if (version == _rightDrawerAnimVersion && !_servicesDisposed)
                {
                    RightDrawerOverlay.Visibility = Visibility.Collapsed;
                    RightDrawerOverlay.IsHitTestVisible = true;
                }
            },
            toRight: true);
    }

    public void RefreshAccountAndQuota(bool fetchQuotaIfSignedIn = false)
    {
        bool signedIn = _services.GetRequiredService<ICookieStore>().HasSignedIn;
        if (!signedIn)
        {
            NavUserNameText.Text = "未登录 · 点击登录";
            NavUserSubText.Text = "登录以使用 ExHentai、云收藏与个人配额";
            NavAvatarBrush.ImageSource = null;
            NavAvatarFallbackIcon.Visibility = Visibility.Visible;
            LimitsProgressBar.Maximum = 100;
            LimitsProgressBar.Value = 0;
            LimitsValueText.Text = "未登录 (可点击刷新尝试查询 IP 配额)";
            if (!fetchQuotaIfSignedIn)
            {
                return;
            }
        }
        else
        {
            NavUserNameText.Text = "已登录 E-Hentai";
            NavUserSubText.Text = "点击管理账号、同步 uconfig 或切换站点";
            if (fetchQuotaIfSignedIn)
            {
                LimitsValueText.Text = "正在查询配额...";
            }
        }

        if (fetchQuotaIfSignedIn)
        {
            if (!signedIn)
            {
                LimitsValueText.Text = "正在查询 IP 配额...";
            }
            _ = FetchProfileAndQuotaAsync(signedIn);
        }
    }

    private async Task FetchProfileAndQuotaAsync(bool signedIn)
    {
        long gen = Interlocked.Increment(ref _quotaFetchGeneration);
        var browse = _services.GetRequiredService<GalleryBrowseService>();

        // 1. Fetch home.php quota independently so forum profile errors never block image quota lookup
        try
        {
            HomeDetail home = await browse.GetHomeDetailAsync(_lifetime.Token).ConfigureAwait(true);
            if (gen != _quotaFetchGeneration || _servicesDisposed)
            {
                return;
            }

            ToolTipService.SetToolTip(LimitsValueText, null);
            if (home.Total > 0)
            {
                LimitsProgressBar.Maximum = home.Total;
                LimitsProgressBar.Value = Math.Clamp(home.Used, 0, home.Total);
                string resetPart = home.ResetCostValue > 0 ? $" · 重置消耗 {home.ResetCost}" : string.Empty;
                LimitsValueText.Text = $"{home.Used} / {home.Total}{resetPart}";
            }
            else
            {
                LimitsProgressBar.Maximum = 100;
                LimitsProgressBar.Value = 0;
                LimitsValueText.Text = signedIn
                    ? "配额暂无限制或暂未返回上限（请检查 Cookie 是否有效）"
                    : "未检测到账户配额（可能仅 IP 限制或未登录）";
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询图片查看配额失败");
            if (gen == _quotaFetchGeneration && !_servicesDisposed)
            {
                LimitsValueText.Text = "配额查询失败，点击右侧刷新重试";
                ToolTipService.SetToolTip(LimitsValueText, GalleryBrowseService.DescribeFailure(ex));
            }
        }

        if (!signedIn || gen != _quotaFetchGeneration || _servicesDisposed)
        {
            return;
        }

        // 2. Fetch forum profile display name & avatar best-effort
        try
        {
            ProfileParser.Result profile = await browse.GetProfileAsync(_lifetime.Token).ConfigureAwait(true);
            if (gen != _quotaFetchGeneration || _servicesDisposed)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(profile.DisplayName))
            {
                NavUserNameText.Text = profile.DisplayName!;
            }

            if (!string.IsNullOrWhiteSpace(profile.Avatar))
            {
                WriteableBitmap? avatarBmp = await LoadThumbnailAsync(profile.Avatar, 128).ConfigureAwait(true);
                if (avatarBmp is not null && gen == _quotaFetchGeneration && !_servicesDisposed)
                {
                    NavAvatarBrush.ImageSource = avatarBmp;
                    NavAvatarFallbackIcon.Visibility = Visibility.Collapsed;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "后台刷新论坛用户资料失败");
        }
    }

    // --- IShellCoordinator Navigation & Actions ----------------------------

    public async Task OpenDetailAsync(GalleryInfo info)
    {
        try
        {
            DismissHoverPreview();
            RecordHistory(info);
            string tabTitle = EhUtils.GetSuitableTitle(info, _services.Settings.ShowJpnTitle) ?? "画廊详情";

            var browse = _services.GetRequiredService<GalleryBrowseService>();
            var tagSource = _services.GetRequiredService<TagTranslationSource>();
            var viewModel = new GalleryDetailViewModel(browse, tagSource.Current);
            var db = _services.GetRequiredService<EhDatabase>();
            viewModel.SetCommentBlacklistFilter(user => !string.IsNullOrWhiteSpace(user) && db.InBlackList(user));

            GalleryDetailActions actions = SharedGalleryActions.BuildDetailActions(this, _favoriteOverrides);
            var page = new GalleryDetailPage(viewModel, actions);
            PushSubScene(page, tabTitle);

            await viewModel.LoadAsync(info).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(viewModel.Title))
            {
                _currentSceneTitle = viewModel.Title;
                SetStatus(viewModel.Title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开详情页失败：{Gid}", info.Gid);
            SetStatus(GalleryBrowseService.DescribeFailure(ex));
        }
    }

    public Task OpenDetailByGidTokenAsync(long gid, string token)
        => OpenDetailAsync(new GalleryInfo { Gid = gid, Token = token });

    public async Task OpenSearchAsync(ListUrlBuilder builder, string? sceneTitle = null, bool pushToBackstack = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        string title = string.IsNullOrWhiteSpace(sceneTitle)
            ? (string.IsNullOrWhiteSpace(builder.Keyword) ? "画廊筛选" : $"搜索: {builder.Keyword}")
            : sceneTitle!;

        if (!pushToBackstack && SceneContentHost.Content is GalleryListScenePage currentList)
        {
            await currentList.ApplyListBuilderAsync(builder, title).ConfigureAwait(true);
            return;
        }

        var listPage = new GalleryListScenePage(this, builder, title, showBackButton: pushToBackstack);
        if (pushToBackstack)
        {
            PushSubScene(listPage, title, listPage.GetOrCreateQuickSearchDrawer(), "快速搜索");
        }
        else
        {
            SetRootScene(listPage, title, listPage.GetOrCreateQuickSearchDrawer(), "快速搜索");
        }

        await listPage.InitializeLoadAsync().ConfigureAwait(true);
    }

    public Task OpenSearchKeywordAsync(string keyword, bool pushToBackstack = true)
    {
        var builder = new ListUrlBuilder
        {
            Mode = ListUrlBuilder.ModeNormal,
            Keyword = keyword
        };
        return OpenSearchAsync(builder, string.IsNullOrWhiteSpace(keyword) ? "首页" : $"搜索: {keyword}", pushToBackstack);
    }

    public async Task OpenImageSearchAsync(
        string imagePath,
        bool useSimilarityScan,
        bool onlySearchCovers,
        bool showExpunged,
        bool pushToBackstack = true)
    {
        var builder = new ListUrlBuilder { Mode = ListUrlBuilder.ModeImageSearch };
        var req = new ImageSearchRequest(imagePath, useSimilarityScan, onlySearchCovers, showExpunged);
        var listPage = new GalleryListScenePage(this, builder, "以图搜图", showBackButton: pushToBackstack, initialImageSearch: req);
        if (pushToBackstack)
        {
            PushSubScene(listPage, "以图搜图", listPage.GetOrCreateQuickSearchDrawer(), "快速搜索");
        }
        else
        {
            SetRootScene(listPage, "以图搜图", listPage.GetOrCreateQuickSearchDrawer(), "快速搜索");
        }

        await listPage.InitializeLoadAsync().ConfigureAwait(true);
    }

    public async Task OpenReaderAsync(GalleryDetail detail, int? startPage = null)
    {
        try
        {
            ReaderViewModel viewModel = _services.CreateReaderViewModel();
            var page = new ReaderPage(viewModel);
            AttachReaderPageEvents(page);

            PushSubScene(page, $"{EhUtils.GetSuitableTitle(detail, _services.Settings.ShowJpnTitle) ?? "阅读器"}");
            await viewModel.OpenDetailAsync(detail, startPage, _lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开阅读器失败：{Gid}", detail.Gid);
            SetStatus(GalleryBrowseService.DescribeFailure(ex));
        }
    }

    public async Task OpenLocalReaderAsync(LocalGallery gallery)
    {
        try
        {
            ReaderViewModel viewModel = _services.CreateReaderViewModel();
            var page = new ReaderPage(viewModel);
            AttachReaderPageEvents(page);

            PushSubScene(page, $"阅读 · {gallery.Title}");
            await viewModel
                .OpenLocalAsync(gallery.Title, gallery.DirectoryPath, gallery.StartPage, gallery.ImageCount, _lifetime.Token)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开本地库阅读失败：{Directory}", gallery.DirectoryPath);
            SetStatus(GalleryBrowseService.DescribeFailure(ex));
        }
    }

    public Task OpenGalleryInNewWindowAsync(GalleryInfo info)
    {
        string key = $"detail:{info.Gid}";
        string title = EhUtils.GetSuitableTitle(info, _services.Settings.ShowJpnTitle) ?? $"画廊 {info.Gid}";
        _windowManager.OpenOrActivateAuxWindow(key, title, win =>
        {
            var host = new DetachedGalleryWindowHost(this, win, _services, _windowManager);
            _ = host.OpenDetailAsync(info);
            return host;
        });
        return Task.CompletedTask;
    }

    public Task OpenReaderInNewWindowAsync(GalleryDetail detail, int? startPage = null)
        => _windowManager.OpenGalleryAsync(detail, startPage);

    public Task OpenExternalOrInAppUrlAsync(string url)
        => SharedGalleryActions.OpenExternalOrInAppUrlAsync(this, url);

    public void EnqueueDownload(GalleryInfo info)
    {
        bool added = _services.GetRequiredService<DownloadManager>().Enqueue(info);
        SetStatus(added ? "已加入下载队列。" : "该画廊已在下载队列中。");
    }

    public Task<bool> QuickToggleFavoriteAsync(GalleryInfo info)
    {
        var db = _services.GetRequiredService<EhDatabase>();
        bool exists = db.ContainLocalFavorites(info.Gid);
        if (exists)
        {
            db.RemoveLocalFavorites(info.Gid);
            info.FavoriteSlot = -2;
            SetStatus("已从本地收藏移除。");
            return Task.FromResult(false);
        }

        db.PutLocalFavorite(info);
        info.FavoriteSlot = FavListUrlBuilder.FavCatLocal;
        SetStatus("已加入本地收藏。");
        return Task.FromResult(true);
    }

    public Task<int?> PickFavoriteSlotDialogAsync(GalleryInfo info)
        => SharedGalleryActions.PickFavoriteSlotDialogAsync(this, _favoriteOverrides, info);

    public Task SaveFavoriteAsync(GalleryDetail detail, int dstCat, string? note)
        => SharedGalleryActions.SaveFavoriteAsync(this, _favoriteOverrides, detail, dstCat, note);

    public Task ShowFilterManagerDialogAsync()
        => SharedGalleryActions.ShowFilterManagerDialogAsync(this);

    public async Task<WriteableBitmap?> LoadThumbnailAsync(
        string? thumbUrl,
        int decodeWidth = 320,
        CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await SharedGalleryActions.LoadThumbnailAsync(_services, thumbUrl, decodeWidth, _lifetime.Token).ConfigureAwait(true);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        return await SharedGalleryActions.LoadThumbnailAsync(_services, thumbUrl, decodeWidth, linked.Token).ConfigureAwait(true);
    }

    public Task<WriteableBitmap?> LoadPreviewTileAsync(PreviewTileViewModel tile)
        => SharedGalleryActions.LoadPreviewTileAsync(_services, tile, _lifetime.Token);

    private async Task OpenTopListRowAsync(TopListRow row)
    {
        GalleryDetailUrlParser.Result? detailTarget = GalleryDetailUrlParser.Parse(row.Href, strict: false);
        if (detailTarget is not null && detailTarget.Gid > 0)
        {
            await OpenDetailByGidTokenAsync(detailTarget.Gid, detailTarget.Token ?? string.Empty).ConfigureAwait(true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(row.Label))
        {
            string query = row.Href.Contains("/uploader/", StringComparison.OrdinalIgnoreCase)
                ? $"uploader:\"{row.Label}\""
                : row.Label;
            await OpenSearchKeywordAsync(query, pushToBackstack: true).ConfigureAwait(true);
        }
    }

    private void RecordHistory(GalleryInfo info)
    {
        try
        {
            _services.GetRequiredService<EhDatabase>().PutHistoryInfo(info);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "写入历史失败：{Gid}", info.Gid);
        }
    }

    private void AttachReaderPageEvents(ReaderPage page)
    {
        page.CloseRequested += (_, _) =>
        {
            SetReaderFullscreen(false);
            GoBack();
        };
        page.FullscreenChanged += (_, fullscreen) => SetReaderFullscreen(fullscreen);
        page.OpenBookmarkGalleryRequested += (_, bookmark) => _ = OpenReaderFromBookmarkAsync(bookmark);
    }

    private async Task OpenReaderFromBookmarkAsync(ReaderBookmarkItem bookmark)
    {
        try
        {
            SetReaderFullscreen(false);
            string token = bookmark.Token;
            var db = _services.GetRequiredService<EhDatabase>();
            string? dirname = db.GetDownloadDirname(bookmark.Gid);
            if (!string.IsNullOrWhiteSpace(dirname))
            {
                string root = _services.Paths.GetEffectiveDownloadDirectory(_services.Settings);
                string dir = Path.Combine(root, dirname);
                if (Directory.Exists(dir))
                {
                    LocalGallery? scanned = _services.GetRequiredService<LocalLibraryScanner>().ReadGallery(dir);
                    if (scanned is not null)
                    {
                        await OpenLocalReaderAsync(scanned with { StartPage = bookmark.Page }).ConfigureAwait(true);
                        return;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                DownloadInfo? dl = db.GetDownloadInfo(bookmark.Gid);
                token = dl?.Token ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                SetStatus("该书签缺少远程令牌或本地目录，无法跳转。");
                return;
            }

            ReaderViewModel viewModel = _services.CreateReaderViewModel();
            var page = new ReaderPage(viewModel);
            AttachReaderPageEvents(page);

            PushSubScene(page, $"阅读 · {bookmark.Title}");
            await viewModel.OpenAsync(
                new GalleryInfo { Gid = bookmark.Gid, Token = token, Title = bookmark.Title },
                bookmark.Page,
                _lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "从书签打开画廊失败：{Gid}", bookmark.Gid);
            SetStatus(GalleryBrowseService.DescribeFailure(ex));
        }
    }
}

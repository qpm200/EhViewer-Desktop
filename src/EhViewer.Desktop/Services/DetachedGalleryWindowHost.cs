using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Controls;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.ViewModels;
using EhViewer.Desktop.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Self-contained sub-scene host for detached gallery windows launched via middle-click or context menu.
/// Maintains an isolated Scene backstack so detail -> comments / previews / info / reader all work
/// independently inside the new window without cluttering MainWindow.
/// </summary>
internal sealed class DetachedGalleryWindowHost : Grid, IShellCoordinator
{
    private readonly IShellCoordinator _parentShell;
    private readonly Window _window;
    private readonly AppServices _services;
    private readonly WindowManager _windowManager;
    private readonly ContentPresenter _host = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };
    private readonly Stack<Page> _stack = new();
    private readonly HashSet<long> _favoriteOverrides = [];
    private readonly CancellationTokenSource _cts = new();

    public DetachedGalleryWindowHost(
        IShellCoordinator parentShell,
        Window window,
        AppServices services,
        WindowManager windowManager)
    {
        _parentShell = parentShell;
        _window = window;
        _services = services;
        _windowManager = windowManager;

        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(_host);

        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var backAccel = new KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.Left,
            Modifiers = Windows.System.VirtualKeyModifiers.Menu
        };
        backAccel.Invoked += (_, e) => { e.Handled = true; GoBack(); };
        KeyboardAccelerators.Add(backAccel);

        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed)
            {
                e.Handled = true;
                GoBack();
            }
        };

        _window.Closed += (_, _) =>
        {
            _cts.Cancel();
            while (_stack.Count > 0)
            {
                ReleasePage(_stack.Pop());
            }
            ReleasePage(_host.Content as Page);
            _cts.Dispose();
        };
    }

    public AppServices Services => _services;
    public Window HostWindow => _window;
    public CancellationToken LifetimeToken => _cts.Token;

    public void SetStatus(string message) => _parentShell.SetStatus(message);

    public void ShowBanner(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
        => _parentShell.ShowBanner(title, message, severity);

    public void RefreshAccountAndQuota(bool fetchQuotaIfSignedIn = false)
        => _parentShell.RefreshAccountAndQuota(fetchQuotaIfSignedIn);

    public void PushSubScene(
        Page page,
        string title,
        FrameworkElement? rightDrawerContent = null,
        string? rightDrawerTitle = null)
    {
        if (_host.Content is Page cur && !ReferenceEquals(cur, page))
        {
            _stack.Push(cur);
        }

        if (page is ISceneAware sceneAware)
        {
            sceneAware.AttachCoordinator(this);
        }

        _host.Content = page;
        _window.Title = $"{title} — EhViewer Desktop";
    }

    public void GoBack()
    {
        if (_stack.Count == 0)
        {
            _window.Close();
            return;
        }

        ReleasePage(_host.Content as Page);
        _host.Content = _stack.Pop();
    }

    public void SetRightDrawer(FrameworkElement? content, string? title) { }
    public void OpenRightDrawer() { }
    public void CloseRightDrawer() { }

    public async Task OpenDetailAsync(GalleryInfo info)
    {
        var browse = _services.GetRequiredService<GalleryBrowseService>();
        var tagSource = _services.GetRequiredService<TagTranslationSource>();
        var viewModel = new GalleryDetailViewModel(browse, tagSource.Current);
        var db = _services.GetRequiredService<EhDatabase>();
        viewModel.SetCommentBlacklistFilter(user => !string.IsNullOrWhiteSpace(user) && db.InBlackList(user));

        string title = EhUtils.GetSuitableTitle(info, _services.Settings.ShowJpnTitle) ?? $"画廊 {info.Gid}";
        var page = new GalleryDetailPage(viewModel, SharedGalleryActions.BuildDetailActions(this, _favoriteOverrides));
        PushSubScene(page, title);
        await viewModel.LoadAsync(info).ConfigureAwait(true);
    }

    public Task OpenDetailByGidTokenAsync(long gid, string token)
        => OpenDetailAsync(new GalleryInfo { Gid = gid, Token = token });

    public async Task OpenSearchAsync(ListUrlBuilder builder, string? sceneTitle = null, bool pushToBackstack = true)
    {
        string title = string.IsNullOrWhiteSpace(sceneTitle) ? "搜索" : sceneTitle!;
        var listPage = new GalleryListScenePage(this, builder, title, showBackButton: true);
        PushSubScene(listPage, title);
        await listPage.InitializeLoadAsync().ConfigureAwait(true);
    }

    public Task OpenSearchKeywordAsync(string keyword, bool pushToBackstack = true)
        => OpenSearchAsync(new ListUrlBuilder { Mode = ListUrlBuilder.ModeNormal, Keyword = keyword }, $"搜索: {keyword}", true);

    public async Task OpenImageSearchAsync(string imagePath, bool useSimilarityScan, bool onlySearchCovers, bool showExpunged, bool pushToBackstack = true)
    {
        var listPage = new GalleryListScenePage(
            this,
            new ListUrlBuilder { Mode = ListUrlBuilder.ModeImageSearch },
            "以图搜图",
            showBackButton: true,
            initialImageSearch: new ImageSearchRequest(imagePath, useSimilarityScan, onlySearchCovers, showExpunged));
        PushSubScene(listPage, "以图搜图");
        await listPage.InitializeLoadAsync().ConfigureAwait(true);
    }

    public async Task OpenReaderAsync(GalleryDetail detail, int? startPage = null)
    {
        ReaderViewModel vm = _services.CreateReaderViewModel();
        var page = new ReaderPage(vm);
        page.CloseRequested += (_, _) => GoBack();
        page.FullscreenChanged += (_, full) =>
        {
            try
            {
                _window.AppWindow.SetPresenter(full
                    ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
                    : Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
            }
            catch
            {
            }
        };
        PushSubScene(page, EhUtils.GetSuitableTitle(detail, _services.Settings.ShowJpnTitle) ?? "阅读器");
        await vm.OpenDetailAsync(detail, startPage, _cts.Token).ConfigureAwait(true);
    }

    public async Task OpenLocalReaderAsync(LocalGallery gallery)
    {
        ReaderViewModel vm = _services.CreateReaderViewModel();
        var page = new ReaderPage(vm);
        page.CloseRequested += (_, _) => GoBack();
        PushSubScene(page, gallery.Title);
        await vm.OpenLocalAsync(gallery.Title, gallery.DirectoryPath, gallery.StartPage, gallery.ImageCount, _cts.Token).ConfigureAwait(true);
    }

    public Task OpenGalleryInNewWindowAsync(GalleryInfo info) => _parentShell.OpenGalleryInNewWindowAsync(info);
    public Task OpenReaderInNewWindowAsync(GalleryDetail detail, int? startPage = null) => _windowManager.OpenGalleryAsync(detail, startPage);
    public Task OpenExternalOrInAppUrlAsync(string url) => SharedGalleryActions.OpenExternalOrInAppUrlAsync(this, url);
    public void EnqueueDownload(GalleryInfo info) => _parentShell.EnqueueDownload(info);
    public Task<bool> QuickToggleFavoriteAsync(GalleryInfo info) => _parentShell.QuickToggleFavoriteAsync(info);
    public Task<int?> PickFavoriteSlotDialogAsync(GalleryInfo info) => SharedGalleryActions.PickFavoriteSlotDialogAsync(this, _favoriteOverrides, info);
    public Task SaveFavoriteAsync(GalleryDetail detail, int dstCat, string? note) => SharedGalleryActions.SaveFavoriteAsync(this, _favoriteOverrides, detail, dstCat, note);
    public Task ShowFilterManagerDialogAsync() => SharedGalleryActions.ShowFilterManagerDialogAsync(this);
    public async Task<WriteableBitmap?> LoadThumbnailAsync(
        string? thumbUrl,
        int decodeWidth = 320,
        CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await SharedGalleryActions.LoadThumbnailAsync(_services, thumbUrl, decodeWidth, _cts.Token).ConfigureAwait(true);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        return await SharedGalleryActions.LoadThumbnailAsync(_services, thumbUrl, decodeWidth, linked.Token).ConfigureAwait(true);
    }
    public Task<WriteableBitmap?> LoadPreviewTileAsync(PreviewTileViewModel tile) => SharedGalleryActions.LoadPreviewTileAsync(_services, tile, _cts.Token);
    public void ShowHoverPreview(GalleryItemViewModel item, FrameworkElement? anchor = null) { }
    public void DismissHoverPreview() { }

    private static void ReleasePage(Page? page)
    {
        if (page is ReaderPage r) r.ViewModel.Dispose();
        else if (page is GalleryListScenePage l) l.ReleaseCts();
        else if (page is DownloadsPage d) d.ViewModel.Dispose();
    }
}

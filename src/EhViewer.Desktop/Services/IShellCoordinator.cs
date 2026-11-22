using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Scene-level navigation and shell coordinator interface implemented by MainWindow.
/// Mirrors Android MainActivity's Scene backstack + right contextual drawer contract cleanly.
/// </summary>
public interface IShellCoordinator
{
    AppServices Services { get; }
    Window HostWindow { get; }
    CancellationToken LifetimeToken { get; }

    void SetStatus(string message);
    void ShowBanner(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational);
    void RefreshAccountAndQuota(bool fetchQuotaIfSignedIn = false);
    void GoBack();

    void PushSubScene(
        Page page,
        string title,
        FrameworkElement? rightDrawerContent = null,
        string? rightDrawerTitle = null);

    void SetRightDrawer(FrameworkElement? content, string? title);
    void OpenRightDrawer();
    void CloseRightDrawer();

    Task OpenDetailAsync(GalleryInfo info);
    Task OpenDetailByGidTokenAsync(long gid, string token);
    Task OpenSearchAsync(ListUrlBuilder builder, string? sceneTitle = null, bool pushToBackstack = true);
    Task OpenSearchKeywordAsync(string keyword, bool pushToBackstack = true);
    Task OpenImageSearchAsync(string imagePath, bool useSimilarityScan, bool onlySearchCovers, bool showExpunged, bool pushToBackstack = true);

    Task OpenReaderAsync(GalleryDetail detail, int? startPage = null);
    Task OpenLocalReaderAsync(LocalGallery gallery);
    Task OpenGalleryInNewWindowAsync(GalleryInfo info);
    Task OpenReaderInNewWindowAsync(GalleryDetail detail, int? startPage = null);
    Task OpenExternalOrInAppUrlAsync(string url);

    void EnqueueDownload(GalleryInfo info);
    Task<bool> QuickToggleFavoriteAsync(GalleryInfo info);
    Task<int?> PickFavoriteSlotDialogAsync(GalleryInfo info);
    Task SaveFavoriteAsync(GalleryDetail detail, int dstCat, string? note);

    Task ShowFilterManagerDialogAsync();
    Task<WriteableBitmap?> LoadThumbnailAsync(string? thumbUrl, int decodeWidth = 320, CancellationToken cancellationToken = default);
    Task<WriteableBitmap?> LoadPreviewTileAsync(PreviewTileViewModel tile);

    void ShowHoverPreview(GalleryItemViewModel item, FrameworkElement? anchor = null);
    void DismissHoverPreview();
}

/// <summary>
/// Optional interface implemented by top-level Scene Pages so MainWindow or DetachedGalleryWindowHost
/// can inject the active scene coordinator cleanly after construction.
/// </summary>
public interface ISceneAware
{
    void AttachCoordinator(IShellCoordinator coordinator);
}

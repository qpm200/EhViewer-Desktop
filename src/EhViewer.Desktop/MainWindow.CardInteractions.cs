using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Theming;
using EhViewer.Desktop.ViewModels;
using EhViewer.Desktop.Views;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace EhViewer.Desktop;

public sealed partial class MainWindow
{
    private const int HoverPreviewDelayMs = 300;
    private const double HoverPreviewCardWidth = 280;
    private const double HoverPreviewEstimatedHeight = 408;
    private const double HoverPreviewMargin = 14;

    private readonly BoundedLruCache<(string Url, int Width), WriteableBitmap> _hoverBitmapCache = new(capacity: 6);
    private DispatcherQueueTimer? _hoverTimer;
    private GalleryItemViewModel? _hoverCandidate;
    private FrameworkElement? _hoverAnchor;
    private CancellationTokenSource? _hoverCts;
    private bool _isHoverPreviewHiding;

    public void ShowHoverPreview(GalleryItemViewModel item, FrameworkElement? anchor = null)
    {
        if (!_services.Settings.ShowHoverPreview || item is null || !item.HasThumb)
        {
            return;
        }

        _hoverCandidate = item;
        _hoverAnchor = anchor;
        if (_hoverTimer is null)
        {
            _hoverTimer = DispatcherQueue.CreateTimer();
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(HoverPreviewDelayMs);
            _hoverTimer.IsRepeating = false;
            _hoverTimer.Tick += OnHoverTimerTick;
        }

        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    public void DismissHoverPreview() => DismissHoverPreviewCore(immediate: false);

    private void DismissHoverPreviewCore(bool immediate)
    {
        _hoverTimer?.Stop();
        _hoverCandidate = null;
        _hoverAnchor = null;
        CancellationTokenSource? cts = _hoverCts;
        _hoverCts = null;
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

        if (immediate || HoverPreviewCard.Visibility != Visibility.Visible)
        {
            _isHoverPreviewHiding = false;
            ThemeMotionHelper.NextAnimationToken(HoverPreviewCard);
            HoverPreviewLoadingRing.IsActive = false;
            HoverPreviewLoadingRing.Visibility = Visibility.Collapsed;
            HoverPreviewImage.Source = null;
            HoverPreviewCard.Visibility = Visibility.Collapsed;
            return;
        }

        if (_isHoverPreviewHiding)
        {
            return;
        }

        _isHoverPreviewHiding = true;
        ThemeMotionHelper.AnimatePopoverHide(
            HoverPreviewCard,
            toOffsetY: 6f,
            toScale: 0.96f,
            durationMs: 120,
            onCompleted: () =>
            {
                if (_isHoverPreviewHiding)
                {
                    _isHoverPreviewHiding = false;
                    HoverPreviewLoadingRing.IsActive = false;
                    HoverPreviewLoadingRing.Visibility = Visibility.Collapsed;
                    HoverPreviewImage.Source = null;
                }
            });
    }

    private void DisposeHoverPreviewResources()
    {
        DismissHoverPreviewCore(immediate: true);
        _hoverBitmapCache.Clear();
        if (_hoverTimer is not null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }
    }

    private async void OnHoverTimerTick(DispatcherQueueTimer sender, object args)
    {
        GalleryItemViewModel? candidate = _hoverCandidate;
        FrameworkElement? anchor = _hoverAnchor;
        if (candidate is null || !candidate.HasThumb)
        {
            return;
        }

        double hostWidth = MainStageGrid.ActualWidth;
        double hostHeight = MainStageGrid.ActualHeight;
        if (hostWidth < 420 || hostHeight < 320)
        {
            // Skip hover preview when the content viewport is too cramped to show it without obstructing cards.
            return;
        }

        double scale = MainStageGrid.XamlRoot?.RasterizationScale ?? 1.0;
        int decodeWidth = Math.Clamp((int)Math.Ceiling(HoverPreviewCardWidth * scale), 280, 640);

        CancellationTokenSource? prevCts = _hoverCts;
        _hoverCts = null;
        if (prevCts is not null)
        {
            try
            {
                prevCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            prevCts.Dispose();
        }

        HoverPreviewTitle.Text = candidate.Title;
        HoverPreviewMeta.Text = $"{candidate.Uploader} · {candidate.RatingText} · {candidate.PagesText}".Trim(' ', '·');
        HoverPreviewCategoryText.Text = candidate.CategoryName;
        HoverPreviewCategoryBadge.Background = UiHelpers.CategoryBrush(candidate.Info.Category);

        bool shouldAnimateShow = HoverPreviewCard.Visibility != Visibility.Visible || _isHoverPreviewHiding;
        _isHoverPreviewHiding = false;

        if (_hoverBitmapCache.TryGet((candidate.ThumbUrl, decodeWidth), out WriteableBitmap? cachedBmp))
        {
            HoverPreviewLoadingRing.IsActive = false;
            HoverPreviewLoadingRing.Visibility = Visibility.Collapsed;
            HoverPreviewImage.Source = cachedBmp;
            PositionHoverPreviewCard(anchor, hostWidth, hostHeight);
            if (shouldAnimateShow)
            {
                ThemeMotionHelper.AnimatePopoverShow(HoverPreviewCard, fromOffsetY: 8f, fromScale: 0.96f, durationMs: 170);
            }
            return;
        }

        var cts = new CancellationTokenSource();
        _hoverCts = cts;
        CancellationToken ct = cts.Token;

        try
        {
            HoverPreviewImage.Source = null;
            HoverPreviewLoadingRing.IsActive = true;
            HoverPreviewLoadingRing.Visibility = Visibility.Visible;

            PositionHoverPreviewCard(anchor, hostWidth, hostHeight);
            if (shouldAnimateShow)
            {
                ThemeMotionHelper.AnimatePopoverShow(HoverPreviewCard, fromOffsetY: 8f, fromScale: 0.96f, durationMs: 170);
            }

            ThumbnailService thumbnails = _services.GetRequiredService<ThumbnailService>();
            DecodedPixelBuffer? pixels = await thumbnails
                .LoadPixelsAsync(candidate.ThumbUrl, decodeWidth, clip: null, ct)
                .ConfigureAwait(true);

            if (!ct.IsCancellationRequested && ReferenceEquals(_hoverCandidate, candidate))
            {
                HoverPreviewLoadingRing.IsActive = false;
                HoverPreviewLoadingRing.Visibility = Visibility.Collapsed;
                if (pixels is not null)
                {
                    WriteableBitmap bmp = SkiaBitmapHelper.ToWriteableBitmap(pixels);
                    _hoverBitmapCache.Set((candidate.ThumbUrl, decodeWidth), bmp);
                    HoverPreviewImage.Source = bmp;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HoverPreviewLoadingRing.IsActive = false;
            HoverPreviewLoadingRing.Visibility = Visibility.Collapsed;
            _logger.LogDebug(ex, "悬停大图预览加载失败");
        }
        finally
        {
            if (ReferenceEquals(_hoverCts, cts))
            {
                _hoverCts = null;
                cts.Dispose();
            }
        }
    }

    private void PositionHoverPreviewCard(FrameworkElement? anchor, double hostWidth, double hostHeight)
    {
        double cardW = HoverPreviewCardWidth;
        double estH = HoverPreviewEstimatedHeight;
        double maxLeft = Math.Max(HoverPreviewMargin, hostWidth - cardW - HoverPreviewMargin);
        double maxTop = Math.Max(HoverPreviewMargin, hostHeight - estH - HoverPreviewMargin - 28);

        double left = maxLeft;
        double top = Math.Clamp(68, HoverPreviewMargin, maxTop);

        if (anchor is not null && anchor.IsLoaded && anchor.ActualWidth > 0 && anchor.ActualHeight > 0)
        {
            try
            {
                Point origin = anchor.TransformToVisual(MainStageGrid).TransformPoint(new Point(0, 0));
                double cardRight = origin.X + anchor.ActualWidth;
                double cardLeft = origin.X;

                if (cardRight + 12 + cardW <= hostWidth - HoverPreviewMargin)
                {
                    left = cardRight + 12;
                }
                else if (cardLeft - 12 - cardW >= HoverPreviewMargin)
                {
                    left = cardLeft - 12 - cardW;
                }
                else
                {
                    left = maxLeft;
                }

                top = Math.Clamp(origin.Y - 12, HoverPreviewMargin, maxTop);
            }
            catch
            {
            }
        }

        Canvas.SetLeft(HoverPreviewCard, Math.Clamp(left, HoverPreviewMargin, maxLeft));
        Canvas.SetTop(HoverPreviewCard, Math.Clamp(top, HoverPreviewMargin, maxTop));
    }

    private static bool IsSupportedSearchImage(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        }
    }

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                return;
            }

            IReadOnlyList<Windows.Storage.IStorageItem> items =
                await e.DataView.GetStorageItemsAsync().AsTask().ConfigureAwait(true);
            if (items.Count == 0)
            {
                return;
            }

            var scanner = _services.GetRequiredService<LocalLibraryScanner>();
            string downloadRoot = _services.Paths.GetEffectiveDownloadDirectory(_services.Settings);
            LocalGallery? firstImported = null;
            int importedArchives = 0;
            string? droppedImagePath = null;

            foreach (Windows.Storage.IStorageItem item in items)
            {
                if (item is Windows.Storage.StorageFile file)
                {
                    if (ArchiveImporter.IsSupportedArchive(file.Path))
                    {
                        string title = Path.GetFileNameWithoutExtension(file.Name);
                        string? extractedDir = await Task
                            .Run(() => scanner.ImportArchive(file.Path, downloadRoot, 0, title))
                            .ConfigureAwait(true);

                        if (!string.IsNullOrEmpty(extractedDir))
                        {
                            importedArchives++;
                            firstImported ??= scanner.ReadGallery(extractedDir);
                        }
                    }
                    else if (IsSupportedSearchImage(file.Path))
                    {
                        droppedImagePath ??= file.Path;
                    }
                }
                else if (item is Windows.Storage.StorageFolder folder)
                {
                    LocalGallery? local = scanner.ReadGallery(folder.Path);
                    if (local is not null)
                    {
                        firstImported ??= local;
                    }
                }
            }

            if (firstImported is not null)
            {
                SetStatus(importedArchives > 0
                    ? $"已拖拽导入 {importedArchives} 个归档并打开：{firstImported.Title}"
                    : $"已打开本地画廊：{firstImported.Title}");
                await OpenLocalReaderAsync(firstImported).ConfigureAwait(true);
            }
            else if (!string.IsNullOrEmpty(droppedImagePath))
            {
                await OpenImageSearchAsync(droppedImagePath, useSimilarityScan: true, onlySearchCovers: false, showExpunged: false)
                    .ConfigureAwait(true);
            }
            else
            {
                SetStatus("拖入的项目中未找到受支持的归档、画廊目录或图片。");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拖拽导入失败");
            SetStatus($"拖拽导入失败：{ex.Message}");
        }
    }

    private async void OnOpenFromClipboardClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ClipboardTarget? target = ClipboardService.TryReadTarget();
            if (target is null)
            {
                SetStatus("剪贴板里没有可识别的 E-Hentai 画廊链接。");
                return;
            }

            switch (target)
            {
                case ClipboardTarget.GalleryDetailTarget detail:
                    await OpenDetailByGidTokenAsync(detail.Gid, detail.Token).ConfigureAwait(true);
                    break;

                case ClipboardTarget.GalleryPageTarget page:
                {
                    var engine = _services.GetRequiredService<EhEngine>();
                    string token = await engine
                        .GetGalleryTokenAsync(page.Gid, page.PToken, page.Page, _lifetime.Token)
                        .ConfigureAwait(true);
                    GalleryDetail gd = await _services.GetRequiredService<GalleryBrowseService>()
                        .GetDetailAsync(new GalleryInfo { Gid = page.Gid, Token = token }, _lifetime.Token)
                        .ConfigureAwait(true);
                    await OpenReaderAsync(gd, page.Page).ConfigureAwait(true);
                    break;
                }

                default:
                    SetStatus("无法识别的剪贴板内容。");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "从剪贴板打开失败");
            SetStatus(GalleryBrowseService.DescribeFailure(ex));
        }
    }
}

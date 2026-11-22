using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EhViewer.Desktop.Views;

/// <summary>
/// Full preview grid scene mirroring Android GalleryPreviewsScene.
/// Supports lazy tile decoding, page switching, jump-to-page dialog, and direct Reader launch at page index.
/// </summary>
public sealed partial class GalleryPreviewsPage : Page, ISceneAware
{
    private readonly GalleryDetailViewModel _viewModel;
    private readonly GalleryDetailActions _actions;
    private readonly BoundedLruCache<int, WriteableBitmap> _tileCache = new(capacity: 96);
    private readonly ConditionalWeakTable<FrameworkElement, CancellationTokenSource> _previewLoadCts = new();
    private CancellationTokenSource? _previewPageCts;
    private IShellCoordinator? _shell;
    private bool _headerUpdatePending;

    public GalleryPreviewsPage(
        GalleryDetailViewModel viewModel,
        GalleryDetailActions actions,
        IShellCoordinator? shell = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(actions);

        _viewModel = viewModel;
        _actions = actions;
        _shell = shell;

        InitializeComponent();

        PreviewsRepeater.ItemsSource = _viewModel.Previews;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        AttachViewModelEvents();

        UpdateHeaderAndPager();
    }

    private bool _eventsAttached;

    public void AttachCoordinator(IShellCoordinator coordinator) => _shell = coordinator;

    public static string FormatPageNumber(int zeroBasedPosition) => $"#{zeroBasedPosition + 1}";

    private void AttachViewModelEvents()
    {
        if (_eventsAttached)
        {
            return;
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Previews.CollectionChanged += OnPreviewsCollectionChanged;
        _eventsAttached = true;
    }

    private void DetachViewModelEvents()
    {
        if (!_eventsAttached)
        {
            return;
        }

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Previews.CollectionChanged -= OnPreviewsCollectionChanged;
        _eventsAttached = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModelEvents();
        ScheduleHeaderAndPagerUpdate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModelEvents();
        _headerUpdatePending = false;
        ResetPreviewLoads();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => ScheduleHeaderAndPagerUpdate();

    private void OnPreviewsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ResetPreviewLoads();
        }

        ScheduleHeaderAndPagerUpdate();
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

        _tileCache.Clear();
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

    private void ScheduleHeaderAndPagerUpdate()
    {
        if (_headerUpdatePending)
        {
            return;
        }

        _headerUpdatePending = true;
        if (DispatcherQueue is { } dq && dq.TryEnqueue(() =>
            {
                _headerUpdatePending = false;
                UpdateHeaderAndPager();
            }))
        {
            return;
        }

        _headerUpdatePending = false;
        UpdateHeaderAndPager();
    }

    private void UpdateHeaderAndPager()
    {
        Toolbar.Subtitle = $"{_viewModel.Title} · 第 {_viewModel.CurrentPreviewPage + 1} / {Math.Max(1, _viewModel.PreviewPages)} 页";
        PrevPageBtn.IsEnabled = _viewModel.CanPreviousPreview && !_viewModel.IsLoading;
        NextPageBtn.IsEnabled = _viewModel.CanNextPreview && !_viewModel.IsLoading;
        PageIndicatorBtn.Content = $"第 {_viewModel.CurrentPreviewPage + 1} / {Math.Max(1, _viewModel.PreviewPages)} 页";
        LoadingRing.IsActive = _viewModel.IsLoading;
        EmptyStateBorder.Visibility = !_viewModel.IsLoading && _viewModel.Previews.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnPreviewElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement root || root.Tag is not PreviewTileViewModel tile)
        {
            return;
        }

        if (!FindTileControls(root, out Image? img, out FontIcon? placeholder))
        {
            return;
        }

        CancelPreviewElementLoad(root);

        if (_tileCache.TryGet(tile.Position, out WriteableBitmap? cached))
        {
            img!.Source = cached;
            if (placeholder is not null)
            {
                placeholder.Visibility = Visibility.Collapsed;
            }
            return;
        }

        img!.Source = null;
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
                _tileCache.Set(tile.Position, bmp);
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
            // Keep clean empty placeholder when preview network fails.
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
            if (FindTileControls(root, out Image? img, out FontIcon? placeholder))
            {
                img!.Source = null;
                if (placeholder is not null)
                {
                    placeholder.Visibility = Visibility.Visible;
                }
            }
        }
    }

    private static bool FindTileControls(FrameworkElement root, out Image? img, out FontIcon? placeholder)
    {
        img = null;
        placeholder = null;
        if (root is Border { Child: Grid outer })
        {
            foreach (UIElement child in outer.Children)
            {
                if (child is Border { Child: Grid inner })
                {
                    img = inner.Children.OfType<Image>().FirstOrDefault();
                    placeholder = inner.Children.OfType<FontIcon>().FirstOrDefault();
                    return img is not null;
                }
            }
        }
        return false;
    }

    private void OnBackRequested(object sender, RoutedEventArgs e) => _shell?.GoBack();

    private async void OnPreviewTileTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_viewModel.Detail is { } detail && sender is FrameworkElement { Tag: PreviewTileViewModel tile })
        {
            await _actions.ReadAsync(detail, tile.Position).ConfigureAwait(true);
        }
    }

    private async void OnPrevPageClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CanPreviousPreview)
        {
            await _viewModel.LoadPreviewPageAsync(_viewModel.CurrentPreviewPage - 1).ConfigureAwait(true);
        }
    }

    private async void OnNextPageClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CanNextPreview)
        {
            await _viewModel.LoadPreviewPageAsync(_viewModel.CurrentPreviewPage + 1).ConfigureAwait(true);
        }
    }

    private async void OnGoToPageClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PreviewPages <= 1 || XamlRoot is null)
        {
            return;
        }

        var box = new NumberBox
        {
            Header = $"输入页码 (1 - {_viewModel.PreviewPages})",
            Minimum = 1,
            Maximum = _viewModel.PreviewPages,
            Value = _viewModel.CurrentPreviewPage + 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "跳转预览页",
            PrimaryButtonText = "跳转",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = box
        };

        if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) == ContentDialogResult.Primary)
        {
            int targetZeroBased = Math.Clamp((int)Math.Round(box.Value) - 1, 0, _viewModel.PreviewPages - 1);
            await _viewModel.LoadPreviewPageAsync(targetZeroBased).ConfigureAwait(true);
        }
    }
}

using System.Collections.Specialized;
using EhViewer.Core.Client.Data;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Theming;
using EhViewer.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EhViewer.Desktop.Views;

/// <summary>
/// Android FavoritesScene-aligned Favorites page with Right Contextual Slot Drawer,
/// quick slot bar, sort selector, batch management, hover preview, and middle-click detached window launch.
/// </summary>
public sealed partial class FavoritesPage : Page, ISceneAware
{
    private readonly Func<GalleryInfo, Task>? _openDetail;
    private readonly Func<GalleryItemViewModel, Task<WriteableBitmap?>>? _loadThumbnail;
    private readonly Action<GalleryInfo>? _queueDownload;
    private readonly bool _suppressSortEvent;
    private IShellCoordinator? _shell;
    private bool _autoLoadingNext;

    public FavoritesPage(
        FavoritesViewModel viewModel,
        Func<GalleryInfo, Task>? openDetail = null,
        Func<GalleryItemViewModel, Task<WriteableBitmap?>>? loadThumbnail = null,
        Action<GalleryInfo>? queueDownload = null)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _openDetail = openDetail;
        _loadThumbnail = loadThumbnail;
        _queueDownload = queueDownload;

        InitializeComponent();

        _suppressSortEvent = true;
        foreach (object item in SortBox.Items)
        {
            if (item is ComboBoxItem cbi && cbi.Tag is string tag &&
                string.Equals(tag, ViewModel.SortOrder, StringComparison.Ordinal))
            {
                SortBox.SelectedItem = cbi;
                break;
            }
        }
        if (SortBox.SelectedIndex < 0 && SortBox.Items.Count > 0)
        {
            SortBox.SelectedIndex = 0;
        }
        _suppressSortEvent = false;

        if (MoveTargetBox.Items.Count > 1)
        {
            MoveTargetBox.SelectedIndex = 1;
        }

        Loaded += OnFavoritesPageLoaded;
        Unloaded += OnFavoritesPageUnloaded;
        AttachFavoritesViewModelEvents();
    }

    public FavoritesViewModel ViewModel { get; }

    private bool _slotsSubscribed;
    private bool _slotRefreshPending;

    public Visibility EmptyHintVisibility(int count, bool isLoading)
        => UiHelpers.VisibleWhenEmpty(count, isLoading);

    public void AttachCoordinator(IShellCoordinator coordinator)
    {
        _shell = coordinator;
        RefreshSlotRightDrawer();
    }

    private void AttachFavoritesViewModelEvents()
    {
        if (_slotsSubscribed)
        {
            return;
        }

        ViewModel.Slots.CollectionChanged += OnSlotsChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _slotsSubscribed = true;
    }

    private void DetachFavoritesViewModelEvents()
    {
        if (!_slotsSubscribed)
        {
            return;
        }

        ViewModel.Slots.CollectionChanged -= OnSlotsChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _slotsSubscribed = false;
    }

    private void OnFavoritesPageLoaded(object sender, RoutedEventArgs e)
    {
        AttachFavoritesViewModelEvents();
        RefreshSlotRightDrawer();
        UpdateSlotButtonHighlights();
    }

    private void OnFavoritesPageUnloaded(object sender, RoutedEventArgs e)
    {
        _slotRefreshPending = false;
        DetachFavoritesViewModelEvents();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(FavoritesViewModel.SelectedSlot), StringComparison.Ordinal))
        {
            UpdateSlotButtonHighlights();
        }
    }

    private void OnSlotsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ScheduleSlotRefresh();

    private void ScheduleSlotRefresh()
    {
        if (_slotRefreshPending)
        {
            return;
        }

        _slotRefreshPending = true;
        if (DispatcherQueue is { } dq && dq.TryEnqueue(() =>
            {
                _slotRefreshPending = false;
                RefreshSlotRightDrawer();
                UpdateSlotButtonHighlights();
            }))
        {
            return;
        }

        _slotRefreshPending = false;
        RefreshSlotRightDrawer();
        UpdateSlotButtonHighlights();
    }

    private void RefreshSlotRightDrawer()
    {
        if (_shell is null)
        {
            return;
        }

        var panel = new StackPanel { Spacing = 4 };
        foreach (FavoriteSlot slot in ViewModel.Slots)
        {
            bool selected = slot.Index == ViewModel.SelectedSlot;
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 9, 12, 9),
                Background = UiHelpers.NavRowBackground(selected),
                BorderThickness = new Thickness(0),
                Content = slot.Display,
                Tag = slot.Index
            };
            btn.Click += (s, args) =>
            {
                if (s is FrameworkElement { Tag: int target })
                {
                    if (ViewModel.SelectedSlot == target)
                    {
                        _ = ReloadForSlotAsync();
                    }
                    else
                    {
                        ViewModel.SelectedSlot = target;
                    }
                    _shell?.CloseRightDrawer();
                }
            };
            panel.Children.Add(btn);
        }

        var scroll = new ScrollViewer { Content = panel };
        _shell.SetRightDrawer(scroll, "收藏夹分类");
    }

    private void OnRightDrawerRequested(object sender, RoutedEventArgs e)
    {
        RefreshSlotRightDrawer();
        _shell?.OpenRightDrawer();
    }

    public bool HasText(string? value) => !string.IsNullOrEmpty(value);

    public string FormatSelectedCount(int count) => $"已选 {count} 项";

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await ReloadForSlotAsync().ConfigureAwait(true);

    private async Task ReloadForSlotAsync()
    {
        try
        {
            await ViewModel.LoadFirstPageAsync().ConfigureAwait(true);
        }
        catch
        {
            // Surfaced via ViewModel.ErrorMessage.
        }
    }

    private void OnSlotElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is Button btn)
        {
            int? slotIndex = (args.Index >= 0 && args.Index < ViewModel.Slots.Count)
                ? ViewModel.Slots[args.Index].Index
                : (btn.Tag as int?);
            ApplySlotButtonVisualState(btn, slotIndex == ViewModel.SelectedSlot);
        }
    }

    private void UpdateSlotButtonHighlights()
    {
        if (SlotRepeater is null)
        {
            return;
        }

        for (int i = 0; i < ViewModel.Slots.Count; i++)
        {
            if (SlotRepeater.TryGetElement(i) is Button btn)
            {
                ApplySlotButtonVisualState(btn, ViewModel.Slots[i].Index == ViewModel.SelectedSlot);
            }
        }
    }

    private static void ApplySlotButtonVisualState(Button btn, bool selected)
    {
        var resources = Application.Current.Resources;
        if (selected)
        {
            btn.Background = (Microsoft.UI.Xaml.Media.Brush)resources["EhPrimaryBrush"];
            btn.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)resources["EhPrimaryBrush"];
            btn.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        }
        else
        {
            btn.Background = (Microsoft.UI.Xaml.Media.Brush)resources["EhSubtleSurfaceBrush"];
            btn.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)resources["EhCardBorderBrush"];
            btn.Foreground = (Microsoft.UI.Xaml.Media.Brush)resources["EhTextPrimaryBrush"];
        }
    }

    private void OnSlotScrollerPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(SlotScroller).Properties.MouseWheelDelta;
        if (delta == 0 || SlotScroller.ScrollableWidth <= 0)
        {
            return;
        }

        double next = Math.Clamp(SlotScroller.HorizontalOffset - (delta * 0.8), 0, SlotScroller.ScrollableWidth);
        SlotScroller.ChangeView(next, null, null, disableAnimation: true);
        e.Handled = true;
    }

    private void OnSlotClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int slot })
        {
            if (ViewModel.SelectedSlot == slot)
            {
                _ = ReloadForSlotAsync();
            }
            else
            {
                ViewModel.SelectedSlot = slot;
            }
        }
    }

    private async void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ViewModel.SearchKeyword = args.QueryText ?? string.Empty;
        await ViewModel.LoadFirstPageAsync().ConfigureAwait(true);
    }

    private async void OnSortSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSortEvent)
        {
            return;
        }

        if (SortBox.SelectedItem is ComboBoxItem { Tag: string code })
        {
            await ViewModel.ChangeSortOrderAsync(code).ConfigureAwait(true);
        }
    }

    private void OnSelectionModeToggled(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsSelectionMode)
        {
            ViewModel.ClearSelection();
        }
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => ViewModel.SelectAll();

    private void OnInvertSelectionClick(object sender, RoutedEventArgs e) => ViewModel.InvertSelection();

    private async void OnMoveSelectedClick(object sender, RoutedEventArgs e)
    {
        if (MoveTargetBox.SelectedItem is ComboBoxItem { Tag: string raw } && int.TryParse(raw, out int targetSlot))
        {
            await ViewModel.MoveSelectedAsync(targetSlot).ConfigureAwait(true);
        }
    }

    private async void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
        => await ViewModel.DeleteSelectedAsync().ConfigureAwait(true);

    private void OnDownloadSelectedClick(object sender, RoutedEventArgs e)
        => ViewModel.DownloadSelected();

    private void OnCardCheckboxClick(object sender, RoutedEventArgs e)
        => ViewModel.RecountSelected();

    private async void OnPrevPageClick(object sender, RoutedEventArgs e)
        => await ViewModel.LoadPrevPageAsync().ConfigureAwait(true);

    private async void OnLoadMoreClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.LoadNextPageAsync().ConfigureAwait(true);
        }
        catch
        {
            // Surfaced via ViewModel.ErrorMessage.
        }
    }

    private async void OnFavScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        _shell?.DismissHoverPreview();
        if (_autoLoadingNext || ViewModel.IsLoading || !ViewModel.HasMore)
        {
            return;
        }

        if (FavScroller.ScrollableHeight > 0 && FavScroller.VerticalOffset >= FavScroller.ScrollableHeight - 240)
        {
            _autoLoadingNext = true;
            try
            {
                await ViewModel.LoadNextPageAsync().ConfigureAwait(true);
            }
            catch
            {
            }
            finally
            {
                _autoLoadingNext = false;
            }
        }
    }

    private async void OnGalleryTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GalleryItemViewModel item })
        {
            return;
        }

        _shell?.DismissHoverPreview();

        if (ViewModel.IsSelectionMode)
        {
            ViewModel.ToggleItemSelection(item);
            return;
        }

        if (_openDetail is not null)
        {
            await _openDetail(item.Info).ConfigureAwait(true);
        }
    }

    private void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (element.Tag is GalleryItemViewModel item &&
                props.IsMiddleButtonPressed &&
                _shell is not null)
            {
                e.Handled = true;
                _shell.DismissHoverPreview();
                _ = _shell.OpenGalleryInNewWindowAsync(item.Info);
                return;
            }

            bool isPrimary = props.IsLeftButtonPressed || e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse;
            ThemeMotionHelper.HandleCardPointerPressed(element, isPrimary, MaterialThemeService.ActiveBehavior);
        }
    }

    private void OnCardPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            ThemeMotionHelper.HandleCardPointerReleased(element, MaterialThemeService.ActiveBehavior);
        }
    }

    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card)
        {
            card.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EhPrimaryBrush"];
        }

        if (sender is FrameworkElement element)
        {
            ThemeMotionHelper.HandleCardPointerEntered(element, MaterialThemeService.ActiveBehavior);
            if (element.Tag is GalleryItemViewModel item)
            {
                _shell?.ShowHoverPreview(item, element);
            }
        }
    }

    private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card)
        {
            card.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EhCardBorderBrush"];
        }

        if (sender is FrameworkElement element)
        {
            ThemeMotionHelper.HandleCardPointerExited(element, MaterialThemeService.ActiveBehavior);
        }

        _shell?.DismissHoverPreview();
    }

    private void OnGalleryRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GalleryItemViewModel item } element)
        {
            return;
        }

        _shell?.DismissHoverPreview();
        var flyout = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = "打开详情", Icon = new FontIcon { Glyph = "\uE8A7" } };
        openItem.Click += async (_, _) =>
        {
            if (_openDetail is not null)
            {
                await _openDetail(item.Info).ConfigureAwait(true);
            }
        };
        flyout.Items.Add(openItem);

        if (_shell is not null)
        {
            var newWinItem = new MenuFlyoutItem { Text = "在新窗口打开画廊", Icon = new FontIcon { Glyph = "\uE8A7" } };
            newWinItem.Click += (_, _) => _ = _shell.OpenGalleryInNewWindowAsync(item.Info);
            flyout.Items.Add(newWinItem);
        }

        if (_queueDownload is not null)
        {
            var dlItem = new MenuFlyoutItem { Text = "加入下载队列", Icon = new FontIcon { Glyph = "\uE896" } };
            dlItem.Click += (_, _) => _queueDownload(item.Info);
            flyout.Items.Add(dlItem);
        }

        var selectItem = new MenuFlyoutItem { Text = item.IsSelected ? "取消选中" : "选中此项", Icon = new FontIcon { Glyph = "\uE762" } };
        selectItem.Click += (_, _) =>
        {
            ViewModel.IsSelectionMode = true;
            ViewModel.ToggleItemSelection(item);
        };
        flyout.Items.Add(selectItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem { Text = "从当前收藏夹移除", Icon = new FontIcon { Glyph = "\uE74D" } };
        deleteItem.Click += async (_, _) =>
        {
            ViewModel.ClearSelection();
            item.IsSelected = true;
            await ViewModel.DeleteSelectedAsync().ConfigureAwait(true);
        };
        flyout.Items.Add(deleteItem);

        flyout.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private void OnGalleryElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement element)
        {
            return;
        }

        GalleryItemViewModel? item = RepeaterSupport.ItemAt<GalleryItemViewModel>(sender, args.Index);
        if (item is null)
        {
            return;
        }

        element.Tag = item;

        if (_loadThumbnail is null)
        {
            return;
        }

        if (RepeaterSupport.FindThumbHost(element) is { } thumbHost)
        {
            Func<GalleryItemViewModel, Task<WriteableBitmap?>> loadFallback = _loadThumbnail;
            _ = RepeaterSupport.SetBorderImageAsync(
                thumbHost,
                item,
                ct => _shell is not null
                    ? _shell.LoadThumbnailAsync(item.ThumbUrl, 320, ct)
                    : loadFallback(item));
        }
    }

    private void OnGalleryElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is Border card)
        {
            card.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EhCardBorderBrush"];
        }

        if (args.Element is FrameworkElement element)
        {
            element.Tag = null;
            RepeaterSupport.CancelBorderImage(RepeaterSupport.FindThumbHost(element));
        }
    }
}

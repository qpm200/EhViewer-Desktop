using System.Collections.ObjectModel;
using System.Collections.Specialized;
using EhViewer.Core.Client;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EhViewer.Desktop.Views;

/// <summary>
/// Android HistoryScene-aligned page: keyword search filter, horizontal gallery cards with
/// cover thumbnails, mode badges, middle-click detached window launch, and clear confirmation dialog.
/// </summary>
public sealed partial class HistoryPage : Page, ISceneAware
{
    private readonly Func<HistoryRow, Task>? _openGallery;
    private readonly ObservableCollection<HistoryRow> _filtered = [];
    private IShellCoordinator? _shell;

    public HistoryPage(HistoryViewModel viewModel, Func<HistoryRow, Task>? openGallery = null)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _openGallery = openGallery;

        InitializeComponent();

        HistoryRepeater.ItemsSource = _filtered;
        Loaded += OnHistoryPageLoaded;
        Unloaded += OnHistoryPageUnloaded;
        AttachHistoryViewModelEvents();

        ReloadAndFilter();
    }

    private bool _rowsSubscribed;
    private bool _hasUnloadedOnce;
    private bool _filterPending;
    private bool _suppressRowNotifications;

    public HistoryViewModel ViewModel { get; }

    private void AttachHistoryViewModelEvents()
    {
        if (_rowsSubscribed)
        {
            return;
        }

        ViewModel.Rows.CollectionChanged += OnRowsChanged;
        _rowsSubscribed = true;
    }

    private void DetachHistoryViewModelEvents()
    {
        if (!_rowsSubscribed)
        {
            return;
        }

        ViewModel.Rows.CollectionChanged -= OnRowsChanged;
        _rowsSubscribed = false;
    }

    private void OnHistoryPageLoaded(object sender, RoutedEventArgs e)
    {
        AttachHistoryViewModelEvents();
        if (_hasUnloadedOnce)
        {
            _hasUnloadedOnce = false;
            ReloadAndFilter();
        }
    }

    private void OnHistoryPageUnloaded(object sender, RoutedEventArgs e)
    {
        _hasUnloadedOnce = true;
        _filterPending = false;
        DetachHistoryViewModelEvents();
    }

    private void OnRowElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        RepeaterSupport.CancelBorderImage(RepeaterSupport.FindThumbHost(args.Element));
    }

    public void AttachCoordinator(IShellCoordinator coordinator) => _shell = coordinator;

    public static string CategoryNameOf(int category) => EhUtils.GetCategory(category);

    public bool HasError(string? message) => !string.IsNullOrEmpty(message);

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressRowNotifications)
        {
            ScheduleApplyFilter();
        }
    }

    private void ScheduleApplyFilter()
    {
        if (_filterPending)
        {
            return;
        }

        _filterPending = true;
        if (DispatcherQueue is { } dq && dq.TryEnqueue(() =>
            {
                _filterPending = false;
                ApplyFilter();
            }))
        {
            return;
        }

        _filterPending = false;
        ApplyFilter();
    }

    private void ReloadAndFilter()
    {
        _suppressRowNotifications = true;
        try
        {
            ViewModel.Reload();
        }
        finally
        {
            _suppressRowNotifications = false;
        }

        ApplyFilter();
    }

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        _filtered.Clear();
        string q = FilterBox.Text.Trim();
        foreach (HistoryRow row in ViewModel.Rows)
        {
            if (q.Length == 0 ||
                row.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                row.Uploader.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                row.Gid.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(q, StringComparison.Ordinal))
            {
                _filtered.Add(row);
            }
        }

        EmptyText.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => ReloadAndFilter();

    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Rows.Count == 0 || XamlRoot is null)
        {
            ViewModel.Clear();
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空历史记录",
            Content = $"确定要清除全部 {ViewModel.Rows.Count} 条本地浏览/阅读历史记录吗？",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) == ContentDialogResult.Primary)
        {
            ViewModel.Clear();
            _shell?.SetStatus("已清空全部历史记录。");
        }
    }

    private void OnDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: long gid })
        {
            ViewModel.Delete(gid);
        }
    }

    private async void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HistoryRow row } && _openGallery is not null)
        {
            await _openGallery(row).ConfigureAwait(true);
        }
    }

    private void OnRowPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HistoryRow row } &&
            e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed &&
            _shell is not null)
        {
            e.Handled = true;
            _ = _shell.OpenGalleryInNewWindowAsync(row.Info);
        }
    }

    private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HistoryRow row } anchor)
        {
            return;
        }

        var flyout = new MenuFlyout();

        if (_openGallery is not null)
        {
            var openItem = new MenuFlyoutItem { Text = "打开详情", Icon = new FontIcon { Glyph = "\uE8A7" } };
            openItem.Click += async (_, _) => await _openGallery(row).ConfigureAwait(true);
            flyout.Items.Add(openItem);
        }

        if (_shell is not null)
        {
            var newWin = new MenuFlyoutItem { Text = "在新窗口打开画廊", Icon = new FontIcon { Glyph = "\uE8A7" } };
            newWin.Click += (_, _) => _ = _shell.OpenGalleryInNewWindowAsync(row.Info);
            flyout.Items.Add(newWin);
        }

        var delItem = new MenuFlyoutItem { Text = "移除此条历史", Icon = new FontIcon { Glyph = "\uE74D" } };
        delItem.Click += (_, _) => ViewModel.Delete(row.Gid);
        flyout.Items.Add(delItem);

        flyout.ShowAt(anchor, e.GetPosition(anchor));
        e.Handled = true;
    }

    private void OnRowElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement element || element.Tag is not HistoryRow row || _shell is null)
        {
            return;
        }

        Border? thumbHost = RepeaterSupport.FindThumbHost(element);
        if (thumbHost is null || string.IsNullOrWhiteSpace(row.Info.Thumb))
        {
            return;
        }

        _ = RepeaterSupport.SetBorderImageAsync(
            thumbHost,
            row,
            ct => _shell.LoadThumbnailAsync(row.Info.Thumb, 240, ct));
    }
}

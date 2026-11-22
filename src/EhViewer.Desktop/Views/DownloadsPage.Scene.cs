using System.Collections.Specialized;
using EhViewer.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EhViewer.Desktop.Views;

/// <summary>
/// Scene integration partial for DownloadsPage: injects Android-style Right Contextual Label Drawer
/// and segment filter switching while keeping all download/CSV/Android-migration logic in DownloadsPage.xaml.cs.
/// </summary>
public sealed partial class DownloadsPage : ISceneAware
{
    private IShellCoordinator? _shell;

    public void AttachCoordinator(IShellCoordinator coordinator)
    {
        _shell = coordinator;
        ViewModel.LabelFilterOptions.CollectionChanged -= OnLabelFilterOptionsChanged;
        ViewModel.LabelFilterOptions.CollectionChanged += OnLabelFilterOptionsChanged;
        Unloaded -= OnSceneUnloaded;
        Unloaded += OnSceneUnloaded;
        RefreshLabelRightDrawer();
    }

    private void OnSceneUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.LabelFilterOptions.CollectionChanged -= OnLabelFilterOptionsChanged;
    }

    private void OnLabelFilterOptionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RefreshLabelRightDrawer();

    private void RefreshLabelRightDrawer()
    {
        if (_shell is null)
        {
            return;
        }

        var panel = new StackPanel { Spacing = 4 };

        var manageBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = "管理 / 新建 / 重命名标签...",
            Margin = new Thickness(0, 0, 0, 8)
        };
        manageBtn.Click += (s, args) => OnManageLabelsClick(s, args);
        panel.Children.Add(manageBtn);

        foreach (string option in ViewModel.LabelFilterOptions)
        {
            bool selected = string.Equals(ViewModel.SelectedLabelFilter, option, StringComparison.Ordinal);
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 9, 12, 9),
                Background = UiHelpers.NavRowBackground(selected),
                BorderThickness = new Thickness(0),
                Content = option,
                Tag = option
            };
            btn.Click += (s, _) =>
            {
                if (s is FrameworkElement { Tag: string target })
                {
                    ViewModel.SelectedLabelFilter = target;
                    RefreshLabelRightDrawer();
                    _shell?.CloseRightDrawer();
                }
            };
            panel.Children.Add(btn);
        }

        _shell.SetRightDrawer(new ScrollViewer { Content = panel }, "下载标签过滤");
    }

    private void OnRightDrawerRequested(object sender, RoutedEventArgs e)
    {
        RefreshLabelRightDrawer();
        _shell?.OpenRightDrawer();
    }

    private void OnSegmentAllClick(object sender, RoutedEventArgs e) => SetActiveSegment(0);
    private void OnSegmentQueueClick(object sender, RoutedEventArgs e) => SetActiveSegment(1);
    private void OnSegmentLocalClick(object sender, RoutedEventArgs e) => SetActiveSegment(2);

    private void SetActiveSegment(int mode)
    {
        QueueSectionCard.Visibility = mode is 0 or 1 ? Visibility.Visible : Visibility.Collapsed;
        LocalSectionCard.Visibility = mode is 0 or 2 ? Visibility.Visible : Visibility.Collapsed;

        Style? primary = Application.Current?.Resources["EhTealPrimaryButtonStyle"] as Style;
        TabAllBtn.Style = mode == 0 ? primary : null;
        TabQueueBtn.Style = mode == 1 ? primary : null;
        TabLocalBtn.Style = mode == 2 ? primary : null;
    }
}

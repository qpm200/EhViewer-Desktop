using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Parser;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace EhViewer.Desktop.Views;

/// <summary>
/// Android TopList scene displaying global gallery/uploader/tag/H@H leaderboards with Material rank badges.
/// </summary>
public sealed partial class TopListPage : Page, ISceneAware
{
    private readonly Func<TopListRow, Task>? _openRow;
    private IShellCoordinator? _shell;

    public TopListPage(TopListViewModel viewModel, Func<TopListRow, Task>? openRow = null)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _openRow = openRow;
        InitializeComponent();
    }

    public TopListViewModel ViewModel { get; }

    public void AttachCoordinator(IShellCoordinator coordinator) => _shell = coordinator;

    public bool HasError(string? message) => !string.IsNullOrEmpty(message);

    public static string FormatRank(int rank) => $"#{rank}";

    public static SolidColorBrush RankBrush(int rank) => rank switch
    {
        1 => UiHelpers.FromArgb(unchecked((int)0xFFE6A100)),
        2 => UiHelpers.FromArgb(unchecked((int)0xFF78909C)),
        3 => UiHelpers.FromArgb(unchecked((int)0xFFB87333)),
        _ => UiHelpers.FromArgb(unchecked((int)0xFF009688))
    };

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await ViewModel.LoadAsync().ConfigureAwait(true);

    private async void OnRowClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TopListRow row && _openRow is not null)
        {
            await _openRow(row).ConfigureAwait(true);
        }
    }

    private void OnRankCardPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TopListRow row } &&
            e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed &&
            _shell is not null &&
            GalleryDetailUrlParser.Parse(row.Href, strict: false) is { Gid: > 0 } target)
        {
            e.Handled = true;
            _ = _shell.OpenGalleryInNewWindowAsync(new GalleryInfo { Gid = target.Gid, Token = target.Token, Title = row.Label });
        }
    }
}

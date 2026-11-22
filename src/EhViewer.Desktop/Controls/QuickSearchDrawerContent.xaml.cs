using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EhViewer.Desktop.Controls;

/// <summary>
/// Right contextual drawer content for GalleryListScene: QuickSearch bookmarks CRUD + reorder + apply.
/// </summary>
public sealed partial class QuickSearchDrawerContent : UserControl
{
    private readonly EhDatabase _database;
    private readonly Func<ListUrlBuilder> _getCurrentBuilder;
    private List<QuickSearch> _items = [];

    public QuickSearchDrawerContent(EhDatabase database, Func<ListUrlBuilder> getCurrentBuilder)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _getCurrentBuilder = getCurrentBuilder ?? throw new ArgumentNullException(nameof(getCurrentBuilder));
        InitializeComponent();
        Reload();
    }

    public event EventHandler<QuickSearch>? QuickSearchSelected;

    public void Reload()
    {
        _items = _database.GetAllQuickSearch();
        QuickSearchRepeater.ItemsSource = _items;
        EmptyTipText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public async Task PromptSaveAsync(ListUrlBuilder builder, string? suggestedKeyword = null)
    {
        if (XamlRoot is null)
        {
            return;
        }

        string defaultName = !string.IsNullOrWhiteSpace(suggestedKeyword)
            ? suggestedKeyword.Trim()
            : !string.IsNullOrWhiteSpace(builder.Keyword)
                ? builder.Keyword.Trim()
                : builder.Mode == ListUrlBuilder.ModeSubscription
                    ? "订阅更新"
                    : builder.Mode == ListUrlBuilder.ModeWhatsHot
                        ? "热门列表"
                        : "画廊搜索";

        var nameBox = new TextBox
        {
            Header = "快速搜索名称",
            Text = defaultName
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "保存为快速搜索",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = nameBox
        };

        if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) != ContentDialogResult.Primary)
        {
            return;
        }

        string name = nameBox.Text.Trim();
        if (name.Length == 0)
        {
            name = defaultName;
        }

        var qs = new QuickSearch
        {
            Name = name,
            Mode = builder.Mode,
            Category = builder.Category,
            Keyword = suggestedKeyword ?? builder.Keyword,
            AdvanceSearch = builder.AdvanceSearch,
            MinRating = builder.MinRating,
            PageFrom = builder.PageFrom,
            PageTo = builder.PageTo,
            Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        _database.InsertQuickSearch(qs);
        Reload();
    }

    private async void OnAddCurrentClick(object sender, RoutedEventArgs e)
    {
        await PromptSaveAsync(_getCurrentBuilder()).ConfigureAwait(true);
    }

    private void OnQuickSearchRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && FindParentButton(src) is not null)
        {
            return;
        }

        if (FindQuickSearchFromSender(sender) is { } qs)
        {
            QuickSearchSelected?.Invoke(this, qs);
        }
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e)
    {
        if (FindQuickSearchFromSender(sender) is not { } qs)
        {
            return;
        }

        int index = _items.IndexOf(qs);
        if (index > 0)
        {
            _database.MoveQuickSearch(index, index - 1);
            Reload();
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (FindQuickSearchFromSender(sender) is not { } qs)
        {
            return;
        }

        _database.DeleteQuickSearch(qs);
        Reload();
    }

    private QuickSearch? FindQuickSearchFromSender(object sender)
    {
        if (sender is FrameworkElement fe)
        {
            int idx = QuickSearchRepeater.GetElementIndex(fe);
            if (idx >= 0 && idx < _items.Count)
            {
                return _items[idx];
            }

            // Walk up to direct child of QuickSearchRepeater if triggered from inner Button
            DependencyObject? cur = fe;
            while (cur is not null)
            {
                if (cur is FrameworkElement parentFe)
                {
                    int parentIdx = QuickSearchRepeater.GetElementIndex(parentFe);
                    if (parentIdx >= 0 && parentIdx < _items.Count)
                    {
                        return _items[parentIdx];
                    }
                }
                cur = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cur);
            }
        }

        return null;
    }

    private static Button? FindParentButton(DependencyObject element)
    {
        DependencyObject? cur = element;
        while (cur is not null)
        {
            if (cur is Button btn)
            {
                return btn;
            }
            cur = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cur);
        }

        return null;
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EhViewer.Core.Client.Data.TopList;

namespace EhViewer.Desktop.ViewModels;

/// <summary>
/// Top-list view model covering the seven list categories and four time windows each.
/// </summary>
public sealed partial class TopListViewModel : ObservableObject
{
    private static readonly string[] TimeWindowNames = ["昨日", "本月", "去年", "全时段"];

    private readonly Func<CancellationToken, Task<EhTopListDetail>> _loadTopList;

    public TopListViewModel(Func<CancellationToken, Task<EhTopListDetail>> loadTopList)
    {
        _loadTopList = loadTopList ?? throw new ArgumentNullException(nameof(loadTopList));
    }

    [ObservableProperty]
    public partial int SelectedListType { get; set; }

    [ObservableProperty]
    public partial int SelectedTimeWindow { get; set; } = 3;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string Headline { get; set; } = string.Empty;

    public ObservableCollection<string> ListTypeNames { get; } =
    [
        "画廊", "上传者", "标签", "Hentai@Home", "EH Tracker", "清理", "评分与评论"
    ];

    public ObservableCollection<string> TimeWindowNamesSource { get; } = [.. TimeWindowNames];

    public ObservableCollection<TopListRow> Rows { get; } = [];

    public EhTopListDetail? Detail { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            Detail = await _loadTopList(cancellationToken).ConfigureAwait(true);
            Rebuild();
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "已取消";
        }
        catch (Exception ex)
        {
            ErrorMessage = Services.GalleryBrowseService.DescribeFailure(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedListTypeChanged(int value) => Rebuild();

    partial void OnSelectedTimeWindowChanged(int value) => Rebuild();

    private void Rebuild()
    {
        Rows.Clear();
        Headline = string.Empty;

        TopListInfo? info = Detail?.Get(SelectedListType);
        if (info is null)
        {
            return;
        }

        Headline = info.Title ?? string.Empty;

        TopListItemArray? window = info.Get(SelectedTimeWindow);
        if (window is null)
        {
            return;
        }

        for (int i = 0; i < window.Length; i++)
        {
            TopListItem item = window.Get(i);
            Rows.Add(new TopListRow(
                i + 1,
                item.Href ?? string.Empty,
                item.Tag ?? item.Gid ?? string.Empty,
                item.Value ?? string.Empty));
        }
    }
}

/// <summary>One ranked row. <paramref name="Label"/> is whatever identifier the list type uses.</summary>
public sealed record TopListRow(int Rank, string Href, string Label, string Value);

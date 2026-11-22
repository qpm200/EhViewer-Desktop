namespace EhViewer.Core.Client.Data.TopList;

public enum TopListType
{
    Gallery,
    Uploader,
    Tagging,
    HentaiHome,
    EhTracker,
    Cleanup,
    RatingAndReviewing
}

public sealed class EhTopListDetail
{
    public string? Title { get; set; }
    public TopListInfo? GalleryTopListInfo { get; set; }
    public TopListInfo? UploaderTopListInfo { get; set; }
    public TopListInfo? TaggingTopListInfo { get; set; }
    public TopListInfo? HentaiHomeTopListInfo { get; set; }
    public TopListInfo? EhTrackerTopListInfo { get; set; }
    public TopListInfo? CleanUpTopListInfo { get; set; }
    public TopListInfo? RatingAndReviewingTopListInfo { get; set; }

    public TopListInfo? Get(int index) => index switch
    {
        0 => GalleryTopListInfo,
        1 => UploaderTopListInfo,
        2 => TaggingTopListInfo,
        3 => HentaiHomeTopListInfo,
        4 => EhTrackerTopListInfo,
        5 => CleanUpTopListInfo,
        6 => RatingAndReviewingTopListInfo,
        _ => null
    };
}

public sealed class TopListInfo
{
    public TopListItemArray? AllTimeTopList { get; set; }
    public TopListItemArray? PastYearTopList { get; set; }
    public TopListItemArray? PastMonthTopList { get; set; }
    public TopListItemArray? YesterdayTopList { get; set; }
    public string? Title { get; set; }
    public TopListType Type { get; set; }

    public TopListItemArray? Get(int index) => index switch
    {
        0 => YesterdayTopList,
        1 => PastMonthTopList,
        2 => PastYearTopList,
        3 => AllTimeTopList,
        _ => null
    };

    public int Size => 4;
}

public sealed class TopListItemArray
{
    public TopListItem[] ItemArray { get; set; } = [];

    public int Length => ItemArray.Length;
    public TopListItem Get(int index) => ItemArray[index];
}

public sealed class TopListItem
{
    public string? Href { get; set; }
    public string? Gid { get; set; }
    public string? Token { get; set; }
    public string? Tag { get; set; }
    public string? Value { get; set; }
}

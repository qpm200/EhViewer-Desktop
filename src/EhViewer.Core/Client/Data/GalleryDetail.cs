using EhViewer.Core.Client.Parser;

namespace EhViewer.Core.Client.Data;

/// <summary>
/// Full gallery metadata, tags, comments, preview set, and newer versions returned by detail parsing.
/// </summary>
public sealed class GalleryDetail : GalleryInfo
{
    public long ApiUid { get; set; } = -1L;
    public string? ApiKey { get; set; }
    public int TorrentCount { get; set; }
    public string? TorrentUrl { get; set; }
    public string? ArchiveUrl { get; set; }
    public string? Parent { get; set; }
    public string? Visible { get; set; }
    public string? Language { get; set; }
    public string? Size { get; set; }
    public int SpiderInfoPages { get; set; }
    public int FavoriteCount { get; set; }
    public bool IsFavorited { get; set; }
    public int RatingCount { get; set; }
    public GalleryTagGroup[]? Tags { get; set; }
    public GalleryCommentList? Comments { get; set; }
    public int PreviewPages { get; set; }
    public int SpiderInfoPreviewPages { get; set; }
    public PreviewSet? PreviewSet { get; set; }
    public PreviewSet? SpiderInfoPreviewSet { get; set; }
    public NewVersion[]? NewVersions { get; set; }

    public GalleryDetail()
    {
    }

    public GalleryDetail(GalleryInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Gid = info.Gid;
        Token = info.Token;
        Title = info.Title;
        TitleJpn = info.TitleJpn;
        Thumb = info.Thumb;
        Category = info.Category;
        Posted = info.Posted;
        Uploader = info.Uploader;
        Rating = info.Rating;
        Rated = info.Rated;
        SimpleTags = info.SimpleTags;
        ThumbWidth = info.ThumbWidth;
        ThumbHeight = info.ThumbHeight;
        SpanSize = info.SpanSize;
        SpanIndex = info.SpanIndex;
        SpanGroupIndex = info.SpanGroupIndex;
        FavoriteSlot = info.FavoriteSlot;
        FavoriteName = info.FavoriteName;
        Pages = info.Pages;
    }

    public GalleryDetail? GetNewGalleryDetail(int index)
    {
        if (NewVersions == null || index < 0 || index >= NewVersions.Length)
        {
            return null;
        }

        string? versionUrl = NewVersions[index].VersionUrl;
        if (versionUrl == null)
        {
            return null;
        }

        var result = GalleryDetailUrlParser.Parse(versionUrl);
        if (result == null)
        {
            return null;
        }

        return new GalleryDetail
        {
            Gid = result.Gid,
            Token = result.Token
        };
    }

    public string?[]? GetUpdateVersionNames()
    {
        if (NewVersions == null)
        {
            return null;
        }

        var result = new string?[NewVersions.Length];
        for (int i = 0; i < NewVersions.Length; i++)
        {
            result[i] = NewVersions[i].VersionName;
        }
        return result;
    }
}

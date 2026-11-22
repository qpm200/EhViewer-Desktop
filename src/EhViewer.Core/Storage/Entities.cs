using System.Text.Json.Serialization;
using EhViewer.Core.Client.Data;

namespace EhViewer.Core.Storage;

internal static class GalleryInfoCopyHelper
{
    public static void CopyFields(GalleryInfo source, GalleryInfo target)
    {
        target.Gid = source.Gid;
        target.Token = source.Token;
        target.Title = source.Title;
        target.TitleJpn = source.TitleJpn;
        target.Thumb = source.Thumb;
        target.Category = source.Category;
        target.Posted = source.Posted;
        target.Uploader = source.Uploader;
        target.Rating = source.Rating;
        target.SimpleTags = source.SimpleTags is null ? null : [.. source.SimpleTags];
        target.SimpleLanguage = source.SimpleLanguage;
    }
}

public sealed class DownloadInfo : GalleryInfo
{
    public const int StateInvalid = -1;
    public const int StateNone = 0;
    public const int StateWait = 1;
    public const int StateDownload = 2;
    public const int StateFinish = 3;
    public const int StateFailed = 4;
    public const int StateUpdate = 5;
    public const int GotoNew = 6;

    public DownloadInfo()
    {
    }

    public DownloadInfo(GalleryInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        GalleryInfoCopyHelper.CopyFields(source, this);
    }

    public int State { get; set; }
    public int Legacy { get; set; }
    public long Time { get; set; }
    public string? Label { get; set; }
    public string? ArchiveUri { get; set; }

    [JsonIgnore]
    public long Speed { get; set; }
    [JsonIgnore]
    public long Remaining { get; set; }
    [JsonIgnore]
    public int Finished { get; set; }
    [JsonIgnore]
    public int Downloaded { get; set; }
    [JsonIgnore]
    public int Total { get; set; }
    [JsonIgnore]
    public long FileSize { get; set; } = -1;

    public void UpdateInfo(GalleryInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Token = source.Token;
        Title = source.Title;
        TitleJpn = source.TitleJpn;
        Thumb = source.Thumb;
        Category = source.Category;
        Posted = source.Posted;
        Uploader = source.Uploader;
        Rating = source.Rating;
        SimpleTags = source.SimpleTags is null ? null : [.. source.SimpleTags];
        SimpleLanguage = source.SimpleLanguage;
    }
}

public sealed class DownloadLabel
{
    public long? Id { get; set; }
    public string? Label { get; set; }
    public long Time { get; set; }
}

public sealed class DownloadDirname
{
    public long Gid { get; set; }
    public string? Dirname { get; set; }
}

public sealed class HistoryInfo : GalleryInfo
{
    public HistoryInfo()
    {
    }

    public HistoryInfo(GalleryInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        GalleryInfoCopyHelper.CopyFields(source, this);
    }

    public int Mode { get; set; }
    public long Time { get; set; }
}

public sealed class LocalFavoriteInfo : GalleryInfo
{
    public LocalFavoriteInfo()
    {
    }

    public LocalFavoriteInfo(GalleryInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        GalleryInfoCopyHelper.CopyFields(source, this);
    }

    public long Time { get; set; }
}

public sealed class BookmarkInfo : GalleryInfo
{
    public BookmarkInfo()
    {
    }

    public BookmarkInfo(GalleryInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        GalleryInfoCopyHelper.CopyFields(source, this);
    }

    public int Page { get; set; }
    public long Time { get; set; }
}

public sealed class QuickSearch
{
    public long? Id { get; set; }
    public string? Name { get; set; }
    public int Mode { get; set; }
    public int Category { get; set; }
    public string? Keyword { get; set; }
    public int AdvanceSearch { get; set; }
    public int MinRating { get; set; }
    public int PageFrom { get; set; }
    public int PageTo { get; set; }
    public long Time { get; set; }
}

public sealed class FilterRule : IEquatable<FilterRule>
{
    public long? Id { get; set; }
    public int Mode { get; set; }
    public string? Text { get; set; }
    public bool? Enable { get; set; }

    public bool IsEnabled => Enable ?? true;

    public bool Equals(FilterRule? other)
    {
        if (other is null)
        {
            return false;
        }

        return Mode == other.Mode && string.Equals(Text, other.Text, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => Equals(obj as FilterRule);

    public override int GetHashCode() => HashCode.Combine(Mode, Text);
}

public sealed class BlackListEntry : IEquatable<BlackListEntry>
{
    public long? Id { get; set; }
    public string? BadGayName { get; set; }
    public string? Reason { get; set; }
    public string? AngryWith { get; set; }
    public string? AddTime { get; set; }
    public int? Mode { get; set; }

    public bool Equals(BlackListEntry? other)
    {
        if (other is null)
        {
            return false;
        }

        return string.Equals(BadGayName, other.BadGayName, StringComparison.Ordinal)
            && string.Equals(Reason, other.Reason, StringComparison.Ordinal)
            && string.Equals(AngryWith, other.AngryWith, StringComparison.Ordinal)
            && string.Equals(AddTime, other.AddTime, StringComparison.Ordinal)
            && Mode == other.Mode;
    }

    public override bool Equals(object? obj) => Equals(obj as BlackListEntry);

    public override int GetHashCode() => HashCode.Combine(BadGayName, Reason, AngryWith, AddTime, Mode);
    public override string ToString() => BadGayName ?? string.Empty;
}

public sealed class GalleryTagsRecord : IEquatable<GalleryTagsRecord>
{
    public long Gid { get; set; }
    public string? Rows { get; set; }
    public string? Artist { get; set; }
    public string? Cosplayer { get; set; }
    public string? Character { get; set; }
    public string? Female { get; set; }
    public string? Group { get; set; }
    public string? Language { get; set; }
    public string? Male { get; set; }
    public string? Misc { get; set; }
    public string? Mixed { get; set; }
    public string? Other { get; set; }
    public string? Parody { get; set; }
    public string? Reclass { get; set; }
    public long? CreateTime { get; set; }
    public long? UpdateTime { get; set; }
    public string? Location { get; set; }

    public bool Equals(GalleryTagsRecord? other) => other is not null && Gid == other.Gid;
    public override bool Equals(object? obj) => Equals(obj as GalleryTagsRecord);
    public override int GetHashCode() => Gid.GetHashCode();
}

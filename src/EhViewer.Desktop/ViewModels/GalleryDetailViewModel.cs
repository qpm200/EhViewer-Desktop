using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Parser;
using EhViewer.Desktop.Services;

namespace EhViewer.Desktop.ViewModels;

/// <summary>One tag with its optional Chinese translation.</summary>
public sealed record TagChip(string Tag, string? Translation, string? GroupName = null)
{
    public string Display => Translation is null ? Tag : $"{Tag} · {Translation}";

    public bool HasTranslation => Translation is not null;

    /// <summary>Canonical E-Hentai tag query token produced when clicking this tag chip.</summary>
    public string SearchQuery
    {
        get
        {
            string raw = string.IsNullOrWhiteSpace(GroupName) || Tag.Contains(':', StringComparison.Ordinal)
                ? Tag
                : $"{GroupName}:{Tag}";
            return TagTranslationSource.FormatTagQuery(raw);
        }
    }
}

/// <summary>One resolved tag group ready for a wrapping panel.</summary>
public sealed record TagGroupViewModel(string GroupName, IReadOnlyList<TagChip> Tags);

/// <summary>One preview tile bound to an asynchronously loaded thumbnail.</summary>
public sealed partial class PreviewTileViewModel : ObservableObject
{
    public PreviewTileViewModel(int position, string? pageUrl, string? imageUrl, int offsetX, int offsetY, int clipWidth, int clipHeight)
    {
        Position = position;
        PageUrl = pageUrl;
        ImageUrl = imageUrl;
        OffsetX = offsetX;
        OffsetY = offsetY;
        ClipWidth = clipWidth;
        ClipHeight = clipHeight;
    }

    public int Position { get; }
    public string? PageUrl { get; }
    public string? ImageUrl { get; }

    /// <summary>Non-negative clip rectangle for <see cref="NormalPreviewSet"/> tiles; -1 when absent.</summary>
    public int OffsetX { get; }
    public int OffsetY { get; }
    public int ClipWidth { get; }
    public int ClipHeight { get; }

    public bool IsClipped => OffsetX >= 0 && OffsetY >= 0 && ClipWidth > 0 && ClipHeight > 0;

    [ObservableProperty]
    public partial bool IsLoaded { get; set; }
}

/// <summary>One hyperlink extracted from a gallery comment.</summary>
public sealed record CommentLinkViewModel(string Label, string Url);

/// <summary>One comment row.</summary>
public sealed record CommentViewModel(
    long Id,
    string User,
    string Body,
    int Score,
    string TimeText,
    bool Editable = false,
    bool VoteUpAble = false,
    bool VoteUpEd = false,
    bool VoteDownAble = false,
    bool VoteDownEd = false,
    string? VoteState = null,
    string RawHtml = "",
    IReadOnlyList<CommentLinkViewModel>? Links = null,
    string LastEditedText = "")
{
    public bool HasLinks => Links is { Count: > 0 };
    public bool HasLastEdited => !string.IsNullOrEmpty(LastEditedText);
    public string VoteUpLabel => VoteUpEd ? "已赞 ▲" : "赞 ▲";
    public string VoteDownLabel => VoteDownEd ? "已踩 ▼" : "踩 ▼";
    public string ScoreText => Score > 0
        ? $"+{Score}"
        : Score.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public IReadOnlyList<CommentLinkViewModel> LinksList => Links ?? [];
}

/// <summary>
/// Gallery detail page view model: metadata, grouped tags with translations, preview tiles, and
/// comments. All failures surface as localized text through <see cref="GalleryBrowseService"/>.
/// </summary>
public sealed partial class GalleryDetailViewModel : ObservableObject
{
    private readonly GalleryBrowseService _browse;
    private readonly EhTagDatabase? _tagDatabase;

    public GalleryDetailViewModel(GalleryBrowseService browse, EhTagDatabase? tagDatabase = null)
    {
        _browse = browse ?? throw new ArgumentNullException(nameof(browse));
        _tagDatabase = tagDatabase;
    }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TitleJpn { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CategoryName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Uploader { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Posted { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RatingText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PagesText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FavoriteCountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Language { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FileSize { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty]
    public partial string TorrentButtonText { get; set; } = "种子 (0)";

    [ObservableProperty]
    public partial bool HasTorrents { get; set; }

    [ObservableProperty]
    public partial string ParentUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasParent { get; set; }

    [ObservableProperty]
    public partial bool HasVersions { get; set; }

    [ObservableProperty]
    public partial string FavoriteButtonText { get; set; } = "收藏";

    public ObservableCollection<TagGroupViewModel> TagGroups { get; } = [];

    public ObservableCollection<PreviewTileViewModel> Previews { get; } = [];

    public ObservableCollection<CommentViewModel> Comments { get; } = [];

    public ObservableCollection<NewVersion> Versions { get; } = [];

    private Func<string?, bool>? _isUserBlacklisted;

    public void SetCommentBlacklistFilter(Func<string?, bool>? filter)
    {
        _isUserBlacklisted = filter;
        if (Detail?.Comments is { } comments)
        {
            UpdateComments(comments);
        }
    }

    public void UpdateFavoriteState(bool isFavorited, int favoriteSlot)
    {
        if (!isFavorited)
        {
            FavoriteButtonText = "收藏";
        }
        else if (favoriteSlot == FavListUrlBuilder.FavCatLocal)
        {
            FavoriteButtonText = "已本地收藏";
        }
        else if (favoriteSlot >= 0)
        {
            FavoriteButtonText = $"已收藏 ({favoriteSlot})";
        }
        else
        {
            FavoriteButtonText = "已收藏";
        }
    }

    public GalleryDetail? Detail { get; private set; }

    /// <summary>Loads a detail page from a gallery row.</summary>
    public async Task LoadAsync(GalleryInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);

        await RunAsync(() => _browse.GetDetailAsync(info, cancellationToken), cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>Loads a detail page from an explicit detail url.</summary>
    public async Task LoadByUrlAsync(string detailUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detailUrl);

        await RunAsync(() => _browse.GetDetailByUrlAsync(detailUrl, cancellationToken), cancellationToken)
            .ConfigureAwait(true);
    }

    private async Task RunAsync(Func<Task<GalleryDetail>> loader, CancellationToken cancellationToken)
    {
        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;

        try
        {
            GalleryDetail detail = await loader().ConfigureAwait(true);
            Apply(detail);
            IsLoaded = true;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "已取消";
            HasError = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = GalleryBrowseService.DescribeFailure(ex);
            HasError = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal void Apply(GalleryDetail detail)
    {
        Detail = detail;

        Title = EhUtils.GetSuitableTitle(detail) ?? string.Empty;
        TitleJpn = detail.TitleJpn ?? string.Empty;
        CategoryName = EhUtils.GetCategory(detail.Category);
        Uploader = detail.Uploader ?? string.Empty;
        Posted = detail.Posted ?? string.Empty;
        RatingText = detail.Rating > 0
            ? $"{detail.Rating.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}（{detail.RatingCount}）"
            : "暂无评分";
        PagesText = detail.Pages > 0 ? $"{detail.Pages} 页" : string.Empty;
        FavoriteCountText = $"{detail.FavoriteCount} 人收藏";
        Language = detail.Language ?? string.Empty;
        FileSize = detail.Size ?? string.Empty;

        HasTorrents = detail.TorrentCount > 0 && !string.IsNullOrEmpty(detail.TorrentUrl);
        TorrentButtonText = $"种子 ({detail.TorrentCount})";
        ParentUrl = detail.Parent ?? string.Empty;
        HasParent = !string.IsNullOrWhiteSpace(detail.Parent);

        Versions.Clear();
        if (detail.NewVersions is { Length: > 0 } versions)
        {
            foreach (NewVersion v in versions)
            {
                Versions.Add(v);
            }
        }
        HasVersions = Versions.Count > 0;
        UpdateFavoriteState(detail.IsFavorited, detail.FavoriteSlot);

        BuildTagGroups(detail);
        BuildPreviews(detail);
        BuildComments(detail);
    }

    private void BuildTagGroups(GalleryDetail detail)
    {
        TagGroups.Clear();

        GalleryTagGroup[]? groups = detail.Tags;
        if (groups is null || groups.Length == 0)
        {
            return;
        }

        foreach (GalleryTagGroup group in groups)
        {
            if (group.Tags.Count == 0)
            {
                continue;
            }

            var chips = new List<TagChip>(group.Tags.Count);
            foreach (string tag in group.Tags)
            {
                chips.Add(new TagChip(tag, LookupTranslation(group.GroupName, tag), group.GroupName));
            }

            TagGroups.Add(new TagGroupViewModel(group.GroupName ?? string.Empty, chips));
        }
    }

    /// <summary>
    /// Looks up a translation for a namespaced tag. The tag database keys use the short prefix
    /// form (e.g. <c>f:</c>), while <see cref="GalleryTagGroup.GroupName"/> is the full namespace.
    /// </summary>
    private string? LookupTranslation(string? groupName, string tag)
    {
        if (_tagDatabase is null)
        {
            return null;
        }

        string? prefix = EhTagDatabase.NamespaceToPrefix(groupName);
        if (!string.IsNullOrEmpty(prefix) && tag.StartsWith(prefix, StringComparison.Ordinal))
        {
            string? direct = _tagDatabase.GetTranslation(tag);
            if (direct is not null)
            {
                return direct;
            }
        }

        return _tagDatabase.GetTranslation(tag);
    }

    private void BuildPreviews(GalleryDetail detail)
    {
        PreviewSet? set = detail.PreviewSet ?? detail.SpiderInfoPreviewSet;
        int previewPages = detail.PreviewPages > 0 ? detail.PreviewPages : detail.SpiderInfoPreviewPages;
        ApplyPreviewSet(detail.Gid, set, pageIndex: 0, Math.Max(set is null ? 0 : 1, previewPages));
    }

    /// <summary>Loads another preview page when a gallery spans multiple preview sheets.</summary>
    public async Task LoadPreviewPageAsync(int previewPage, CancellationToken cancellationToken = default)
    {
        if (Detail is not { } detail || previewPage < 0 || (PreviewPages > 0 && previewPage >= PreviewPages))
        {
            return;
        }

        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;

        try
        {
            (PreviewSet previewSet, int totalPreviewPages) = await _browse
                .GetPreviewSetAsync(detail, previewPage, cancellationToken)
                .ConfigureAwait(true);

            ApplyPreviewSet(detail.Gid, previewSet, previewPage, Math.Max(PreviewPages, totalPreviewPages));
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "已取消";
            HasError = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = GalleryBrowseService.DescribeFailure(ex);
            HasError = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void UpdateRating(RateGalleryParser.Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (Detail is { } detail)
        {
            detail.Rating = result.Rating;
            detail.RatingCount = result.RatingCount;
            detail.Rated = true;
        }

        RatingText = result.Rating > 0
            ? $"{result.Rating.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}（{result.RatingCount}）"
            : "暂无评分";
    }

    public void UpdateComments(GalleryCommentList comments)
    {
        ArgumentNullException.ThrowIfNull(comments);
        if (Detail is { } detail)
        {
            detail.Comments = comments;
        }

        Comments.Clear();
        if (comments.Comments is null)
        {
            return;
        }

        foreach (GalleryComment comment in comments.Comments)
        {
            if (_isUserBlacklisted is not null && _isUserBlacklisted(comment.User))
            {
                continue;
            }

            string rawHtml = comment.Comment ?? string.Empty;
            string plain = EhHtmlHelper.HtmlToPlainText(rawHtml);
            CommentLinkViewModel[] links = EhHtmlHelper.ExtractLinks(rawHtml)
                .Select(static span => new CommentLinkViewModel(span.Label, span.Url))
                .ToArray();
            string editedText = comment.LastEdited > 0
                ? FormatTime(comment.LastEdited)
                : string.Empty;
            if (!string.IsNullOrEmpty(editedText))
            {
                editedText = $"编辑于 {editedText}";
            }

            Comments.Add(new CommentViewModel(
                comment.Id,
                comment.User ?? "(匿名)",
                plain,
                comment.Score,
                FormatTime(comment.Time),
                Editable: comment.Editable,
                VoteUpAble: comment.VoteUpAble,
                VoteUpEd: comment.VoteUpEd,
                VoteDownAble: comment.VoteDownAble,
                VoteDownEd: comment.VoteDownEd,
                VoteState: comment.VoteState,
                RawHtml: rawHtml,
                Links: links,
                LastEditedText: editedText));
        }
    }

    public void ApplyCommentVote(VoteCommentParser.Result voteResult)
    {
        ArgumentNullException.ThrowIfNull(voteResult);

        if (Detail?.Comments?.Comments is { } rawComments)
        {
            foreach (GalleryComment raw in rawComments)
            {
                if (raw.Id == voteResult.Id)
                {
                    raw.Score = voteResult.Score;
                    raw.VoteUpEd = voteResult.Vote == 1;
                    raw.VoteDownEd = voteResult.Vote == -1;
                    raw.VoteState = voteResult.Vote switch
                    {
                        1 => "up",
                        -1 => "down",
                        _ => null
                    };
                    break;
                }
            }
        }

        for (int i = 0; i < Comments.Count; i++)
        {
            CommentViewModel existing = Comments[i];
            if (existing.Id == voteResult.Id)
            {
                Comments[i] = existing with
                {
                    Score = voteResult.Score,
                    VoteUpEd = voteResult.Vote == 1,
                    VoteDownEd = voteResult.Vote == -1,
                    VoteState = voteResult.Vote switch
                    {
                        1 => "up",
                        -1 => "down",
                        _ => null
                    }
                };
                break;
            }
        }
    }

    private void ApplyPreviewSet(long gid, PreviewSet? set, int pageIndex, int previewPages)
    {
        Previews.Clear();

        if (set is not null)
        {
            for (int i = 0; i < set.Size; i++)
            {
                GalleryPreview preview = set.GetGalleryPreview(gid, i);
                if (string.IsNullOrEmpty(preview.ImageUrl))
                {
                    continue;
                }

                Previews.Add(new PreviewTileViewModel(
                    preview.Position,
                    preview.PageUrl,
                    preview.ImageUrl,
                    preview.OffsetX,
                    preview.OffsetY,
                    preview.ClipWidth,
                    preview.ClipHeight));
            }
        }

        CurrentPreviewPage = pageIndex;
        PreviewPages = previewPages;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPageText))]
    [NotifyPropertyChangedFor(nameof(HasMultiplePreviewPages))]
    [NotifyPropertyChangedFor(nameof(CanPreviousPreview))]
    [NotifyPropertyChangedFor(nameof(CanNextPreview))]
    public partial int PreviewPages { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPageText))]
    [NotifyPropertyChangedFor(nameof(CanPreviousPreview))]
    [NotifyPropertyChangedFor(nameof(CanNextPreview))]
    public partial int CurrentPreviewPage { get; set; }

    public bool HasMultiplePreviewPages => PreviewPages > 1;
    public bool CanPreviousPreview => CurrentPreviewPage > 0;
    public bool CanNextPreview => PreviewPages > 1 && CurrentPreviewPage + 1 < PreviewPages;
    public string PreviewPageText => PreviewPages > 1
        ? $"第 {CurrentPreviewPage + 1} / {PreviewPages} 页"
        : string.Empty;

    private void BuildComments(GalleryDetail detail)
    {
        if (detail.Comments is { } comments)
        {
            UpdateComments(comments);
            return;
        }

        Comments.Clear();
    }

    /// <summary>Formats a unix-second timestamp as a local date-time string.</summary>
    internal static string FormatTime(long unixSeconds)
    {
        if (unixSeconds <= 0)
        {
            return string.Empty;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Empty;
        }
    }
}

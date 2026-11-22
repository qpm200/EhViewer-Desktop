using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Exceptions;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Network;
using EhViewer.Desktop.ViewModels;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Bridges the desktop shell to <see cref="EhEngine"/>: builds list URLs from UI-level search
/// state, materialises pages, and maps site-level failures to user-facing Chinese messages.
/// </summary>
public sealed class GalleryBrowseService
{
    private readonly EhEngine _engine;
    private readonly IEhClientSettings _settings;

    public GalleryBrowseService(EhEngine engine, IEhClientSettings settings)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public event Action<string>? EventPaneReceived
    {
        add => _engine.EventPaneReceived += value;
        remove => _engine.EventPaneReceived -= value;
    }

    /// <summary>
    /// Loads one list page. <paramref name="builder"/> drives the url; <paramref name="pageIndex"/>
    /// selects the page.
    /// </summary>
    public async Task<PageResult> LoadPageAsync(
        ListUrlBuilder builder,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.PageIndex = pageIndex;
        string url = builder.Build(_settings.GallerySite);

        try
        {
            GalleryListParser.Result result = await _engine
                .GetGalleryListAsync(url, builder.Mode, cancellationToken)
                .ConfigureAwait(false);

            return new PageResult(
                result.GalleryInfoList ?? [],
                result.ResultCount,
                HasMore: result.NextPage > 0 || !string.IsNullOrEmpty(result.NextHref),
                ErrorMessage: result.CustomErrorString);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PageResult([], null, HasMore: false, ErrorMessage: DescribeFailure(ex));
        }
    }

    /// <summary>Loads the raw gallery list result, preserving pagination hrefs for advanced paging.</summary>
    public Task<GalleryListParser.Result> LoadRawAsync(
        string url,
        int mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return _engine.GetGalleryListAsync(url, mode, cancellationToken);
    }

    /// <summary>Resolves a gallery detail page for the row the user activated.</summary>
    public Task<GalleryDetail> GetDetailAsync(GalleryInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(info.Token);

        string url = EhUrl.GetGalleryDetailUrl(info.Gid, info.Token!, 0, false, _settings.GallerySite);
        return _engine.GetGalleryDetailAsync(url, cancellationToken);
    }

    /// <summary>Resolves a gallery detail page when only the gallery page url is known.</summary>
    public Task<GalleryDetail> GetDetailByUrlAsync(string detailUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detailUrl);
        return _engine.GetGalleryDetailAsync(detailUrl, cancellationToken);
    }

    /// <summary>Fetches a specific zero-based preview page for an open gallery detail view.</summary>
    public Task<(PreviewSet PreviewSet, int PreviewPages)> GetPreviewSetAsync(
        GalleryDetail detail,
        int previewPage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail.Token);

        string url = EhUrl.GetGalleryDetailUrl(detail.Gid, detail.Token!, Math.Max(0, previewPage), false, _settings.GallerySite);
        return _engine.GetPreviewSetAsync(url, cancellationToken);
    }

    /// <summary>Submits a 0.5–5.0 star rating using the gallery's detail-page API credentials.</summary>
    public Task<RateGalleryParser.Result> RateGalleryAsync(
        GalleryDetail detail,
        float rating,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (detail.ApiUid <= 0 || string.IsNullOrEmpty(detail.ApiKey))
        {
            throw new InvalidOperationException("需要登录后才能评分。");
        }

        return _engine.RateGalleryAsync(
            detail.ApiUid,
            detail.ApiKey,
            detail.Gid,
            detail.Token ?? string.Empty,
            rating,
            cancellationToken);
    }

    /// <summary>Posts a new comment on <paramref name="detail"/> and returns the updated comment list.</summary>
    public Task<GalleryCommentList> CommentGalleryAsync(
        GalleryDetail detail,
        string comment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentException.ThrowIfNullOrWhiteSpace(comment);

        string url = EhUrl.GetGalleryDetailUrl(detail.Gid, detail.Token ?? string.Empty, 0, false, _settings.GallerySite);
        return _engine.CommentGalleryAsync(url, comment, commentId: null, cancellationToken);
    }

    /// <summary>Adds or moves a gallery into favorite slot <paramref name="dstCat"/> (0..9) or removes it (-1).</summary>
    public Task AddFavoritesAsync(
        GalleryDetail detail,
        int dstCat,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return _engine.AddFavoritesAsync(detail.Gid, detail.Token ?? string.Empty, dstCat, note, cancellationToken);
    }

    /// <summary>Fetches archiver options and GP/Credits balances for <paramref name="detail"/>.</summary>
    public Task<ArchiverData> GetArchiverAsync(GalleryDetail detail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (string.IsNullOrEmpty(detail.ArchiveUrl))
        {
            throw new InvalidOperationException("该画廊没有可用的归档下载地址。");
        }

        return _engine.GetArchiverAsync(detail.ArchiveUrl, detail.Gid, detail.Token ?? string.Empty, cancellationToken);
    }

    /// <summary>Requests a direct archive download URL from the archiver form action.</summary>
    public Task<string?> RequestArchiverDownloadUrlAsync(
        string postUrl,
        string refererUrl,
        string dltype,
        string dlcheck,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(refererUrl);

        return _engine.DownloadArchiverAsync(postUrl, refererUrl, dltype, dlcheck, cancellationToken);
    }

    /// <summary>Performs a reverse image lookup using <see cref="ListUrlBuilder.ImagePath"/>.</summary>
    public async Task<GalleryListParser.Result> ImageSearchAsync(
        string imagePath,
        bool useSimilarityScan,
        bool onlySearchCovers,
        bool showExpunged,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        return await _engine
            .ImageSearchAsync(imagePath, useSimilarityScan, onlySearchCovers, showExpunged, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Lists torrents uploaded for <paramref name="detail"/>.</summary>
    public Task<TorrentInfo[]> GetTorrentListAsync(GalleryDetail detail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (string.IsNullOrEmpty(detail.TorrentUrl))
        {
            return Task.FromResult<TorrentInfo[]>([]);
        }

        return _engine.GetTorrentListAsync(detail.TorrentUrl, detail.Gid, detail.Token ?? string.Empty, cancellationToken);
    }

    /// <summary>Retrieves editable comment source for <paramref name="commentId"/>.</summary>
    public Task<GetEditCommentParser.Result> GetEditCommentAsync(
        GalleryDetail detail,
        long commentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (detail.ApiUid <= 0 || string.IsNullOrEmpty(detail.ApiKey))
        {
            throw new InvalidOperationException("需要登录后才能编辑评论。");
        }

        return _engine.GetEditCommentAsync(
            detail.ApiUid,
            detail.ApiKey,
            detail.Gid,
            detail.Token ?? string.Empty,
            commentId,
            cancellationToken);
    }

    /// <summary>Submits edited text for <paramref name="commentId"/> and returns the updated comment list.</summary>
    public Task<GalleryCommentList> EditCommentAsync(
        GalleryDetail detail,
        long commentId,
        string comment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentException.ThrowIfNullOrWhiteSpace(comment);

        string url = EhUrl.GetGalleryDetailUrl(detail.Gid, detail.Token ?? string.Empty, 0, false, _settings.GallerySite);
        return _engine.CommentGalleryAsync(url, comment, commentId.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
    }

    /// <summary>Submits a comment upvote (+1) or downvote (-1).</summary>
    public Task<VoteCommentParser.Result> VoteCommentAsync(
        GalleryDetail detail,
        long commentId,
        int commentVote,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (detail.ApiUid <= 0 || string.IsNullOrEmpty(detail.ApiKey))
        {
            throw new InvalidOperationException("需要登录后才能对评论投票。");
        }

        return _engine.VoteCommentAsync(
            detail.ApiUid,
            detail.ApiKey,
            detail.Gid,
            detail.Token ?? string.Empty,
            commentId,
            commentVote,
            cancellationToken);
    }

    /// <summary>Batch-moves or deletes favorites in cloud favorites.</summary>
    public Task<FavoritesParser.Result> ModifyFavoritesAsync(
        string url,
        long[] gids,
        int dstCat,
        bool callApi = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(gids);
        return _engine.ModifyFavoritesAsync(url, gids, dstCat, callApi, cancellationToken);
    }

    /// <summary>Fetches H@H resolution options for <paramref name="detail"/>.</summary>
    public Task<ArchiveParser.ParseResult> GetArchiveListAsync(GalleryDetail detail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (string.IsNullOrEmpty(detail.ArchiveUrl))
        {
            throw new InvalidOperationException("该画廊没有可用的归档下载地址。");
        }

        return _engine.GetArchiveListAsync(detail.ArchiveUrl, detail.Gid, detail.Token ?? string.Empty, cancellationToken);
    }

    /// <summary>Triggers H@H client download for a resolution (<c>org</c>, <c>2400</c>, <c>1600</c>, etc.).</summary>
    public Task DownloadHathArchiveAsync(
        GalleryDetail detail,
        string paramOr,
        string res,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return _engine.DownloadArchiveAsync(detail.Gid, detail.Token ?? string.Empty, paramOr, res, cancellationToken);
    }

    /// <summary>Fetches the signed-in user's watched/hidden tags list.</summary>
    public Task<EhViewer.Core.Client.Data.UserTag.UserTagList?> GetWatchedListAsync(CancellationToken cancellationToken = default)
    {
        return _engine.GetWatchedListAsync(EhUrl.GetMyTagsUrl(_settings.GallerySite), cancellationToken);
    }

    /// <summary>Adds a watched or hidden tag.</summary>
    public Task<EhViewer.Core.Client.Data.UserTag.UserTagList?> AddWatchedTagAsync(
        EhViewer.Core.Client.Data.UserTag.TagPushParam param,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(param);
        return _engine.AddTagAsync(EhUrl.GetMyTagsUrl(_settings.GallerySite), param, cancellationToken);
    }

    /// <summary>Deletes a watched or hidden tag.</summary>
    public Task<EhViewer.Core.Client.Data.UserTag.UserTagList?> DeleteWatchedTagAsync(
        EhViewer.Core.Client.Data.UserTag.UserTag tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return _engine.DeleteWatchedTagAsync(EhUrl.GetMyTagsUrl(_settings.GallerySite), tag, cancellationToken);
    }

    /// <summary>Fetches E-Hentai news and any current EventPane banner.</summary>
    public Task<EhNewsDetail> GetEhNewsAsync(CancellationToken cancellationToken = default)
    {
        return _engine.GetEhNewsAsync(cancellationToken);
    }

    /// <summary>Fetches account home image quota and GP breakdown.</summary>
    public Task<HomeDetail> GetHomeDetailAsync(CancellationToken cancellationToken = default)
    {
        return _engine.GetHomeDetailAsync(cancellationToken);
    }

    /// <summary>Resets image viewing quota via GP on home.php.</summary>
    public Task<HomeDetail> ResetImageLimitAsync(CancellationToken cancellationToken = default)
    {
        return _engine.ResetLimitAsync(cancellationToken);
    }

    /// <summary>Fetches the signed-in user's forum display name and avatar URL.</summary>
    public Task<ProfileParser.Result> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        return _engine.GetProfileAsync(cancellationToken);
    }

    /// <summary>Translates core exceptions into short, actionable Chinese text.</summary>
    public static string DescribeFailure(Exception ex) => ex switch
    {
        NoHAtHClientException =>
            "需要 H@H Client 才能访问该画廊。",
        PiningException =>
            "该画廊已被删除（pining）。",
        OffensiveException =>
            "该画廊被标记为 offensive，需要登录并同意后查看。",
        GalleryUnavailableException =>
            "此画廊已下架。",
        EmptyGalleryException =>
            "画廊为空。",
        Image509Exception =>
            "带宽配额已用尽（509），请稍后再试。",
        StatusCodeException sce =>
            $"站点返回错误状态码 {sce.ResponseCode}（{sce.Message}）。",
        ParseException =>
            "页面解析失败，站点结构可能已变更。",
        HttpRequestException hre =>
            $"网络请求失败：{hre.Message}",
        TaskCanceledException =>
            "请求超时。",
        _ => ex.Message
    };
}

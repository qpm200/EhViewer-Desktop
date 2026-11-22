using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Data.TopList;
using EhViewer.Core.Client.Data.UserTag;
using EhViewer.Core.Client.Exceptions;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Network;
using EhViewer.Core.Storage;

namespace EhViewer.Core.Client;

/// <summary>
/// High-level async E-Hentai / ExHentai protocol client coordinating <see cref="HttpClient"/>,
/// <see cref="EhRequestBuilder"/>, HTML/JSON parsers, <see cref="EhFilter"/>, and <see cref="EhDatabase"/>.
/// </summary>
public sealed partial class EhEngine
{
    private const string SadPandaDisposition = "inline; filename=\"sadpanda.jpg\"";
    private const string SadPandaContentType = "image/gif";
    private const long SadPandaLength = 9615;
    private const string KokomadeUrl = "https://exhentai.org/img/kokomade.jpg";

    private static readonly Regex PatternNeedHathClient = CreateNeedHathClientRegex();
    private static readonly Regex PatternDocumentLocation = CreateDocumentLocationRegex();

    private readonly Func<HttpClient> _getHttpClient;
    private readonly ICookieStore _cookieStore;
    private readonly EhDatabase _database;
    private readonly EhFilter _filter;
    private readonly IEhClientSettings _settings;

    public EhEngine(
        EhHttpClientFactory clientFactory,
        ICookieStore cookieStore,
        EhDatabase database,
        EhFilter filter,
        IEhClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        _getHttpClient = () => clientFactory.StandardClient;
        _cookieStore = cookieStore ?? throw new ArgumentNullException(nameof(cookieStore));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public EhEngine(
        HttpClient httpClient,
        ICookieStore cookieStore,
        EhDatabase database,
        EhFilter filter,
        IEhClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _getHttpClient = () => httpClient;
        _cookieStore = cookieStore ?? throw new ArgumentNullException(nameof(cookieStore));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public event Action<string>? EventPaneReceived;

    private EhSite CurrentSite => _settings.GallerySite;

    // --- Authentication ---

    public Task<string> SignInAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("UserName", username),
            new KeyValuePair<string, string>("PassWord", password),
            new KeyValuePair<string, string>("submit", "Log me in"),
            new KeyValuePair<string, string>("CookieDate", "1"),
            new KeyValuePair<string, string>("temporary_https", "off")
        ]);

        const string referer = "https://forums.e-hentai.org/index.php?act=Login&CODE=00";
        const string origin = "https://forums.e-hentai.org";

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Post(EhUrl.ApiSignIn, form, referer, origin),
            static (body, _) => SignInParser.Parse(body),
            cancellationToken);
    }

    public async Task<ProfileParser.Result> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        string profileUrl = await ExecuteParsedAsync(
            () => EhRequestBuilder.Get(EhUrl.UrlForums),
            static (body, _) => ForumsParser.Parse(body),
            cancellationToken).ConfigureAwait(false);

        return await ExecuteParsedAsync(
            () => EhRequestBuilder.Get(profileUrl, EhUrl.UrlForums),
            static (body, _) => ProfileParser.Parse(body),
            cancellationToken).ConfigureAwait(false);
    }

    // --- Galleries & Search ---

    public async Task<GalleryListParser.Result> GetGalleryListAsync(
        string url,
        int mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        string referer = EhUrl.GetReferer(CurrentSite);
        string? capturedBody = null;
        int capturedCode = -1;

        GalleryListParser.Result result = await ExecuteParsedAsync(
            () => EhRequestBuilder.Get(url, referer),
            (body, meta) =>
            {
                capturedBody = body;
                capturedCode = meta.StatusCode;
                return GalleryListParser.Parse(body, mode, CreateListParserOptions());
            },
            cancellationToken).ConfigureAwait(false);

        await FillGalleryListAsync(result.GalleryInfoList, url, filter: true, cancellationToken).ConfigureAwait(false);

        if (capturedCode == 200
            && string.Equals(url, EhUrl.HostEx, StringComparison.Ordinal)
            && string.IsNullOrEmpty(capturedBody))
        {
            result.CustomErrorString = "Igneous cookie invalid";
        }

        return result;
    }

    public async Task<IList<GalleryInfo>> FillGalleryListByApiAsync(
        IList<GalleryInfo> galleryInfoList,
        string referer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(galleryInfoList);
        const int maxBatchSize = 25;

        var batch = new List<GalleryInfo>(maxBatchSize);
        for (int i = 0; i < galleryInfoList.Count; i++)
        {
            batch.Add(galleryInfoList[i]);
            if (batch.Count == maxBatchSize || i == galleryInfoList.Count - 1)
            {
                await DoFillGalleryListByApiAsync(batch, referer, cancellationToken).ConfigureAwait(false);
                batch.Clear();
            }
        }

        return galleryInfoList;
    }

    public async Task<IList<GalleryInfo>> FillGalleryListByApiBestEffortAsync(
        IList<GalleryInfo> galleryInfoList,
        string referer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(galleryInfoList);
        const int maxBatchSize = 25;

        var batch = new List<GalleryInfo>(maxBatchSize);
        for (int i = 0; i < galleryInfoList.Count; i++)
        {
            batch.Add(galleryInfoList[i]);
            if (batch.Count == maxBatchSize || i == galleryInfoList.Count - 1)
            {
                try
                {
                    await DoFillGalleryListByApiAsync(batch, referer, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (CancelledException)
                {
                    throw;
                }
                catch
                {
                    // Best-effort enrichment preserves partial results.
                }

                batch.Clear();
            }
        }

        return galleryInfoList;
    }

    private Task DoFillGalleryListByApiAsync(
        List<GalleryInfo> batch,
        string referer,
        CancellationToken cancellationToken)
    {
        string jsonPayload = JsonSerializer.Serialize(new
        {
            method = "gdata",
            gidlist = batch.Select(static g => new object?[] { g.Gid, g.Token }).ToArray(),
            @namespace = 1
        });

        string url = EhUrl.GetApiUrl(CurrentSite);
        string origin = EhUrl.GetOrigin(CurrentSite);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Post(url, CreateJsonContent(jsonPayload), referer, origin),
            (body, _) =>
            {
                GalleryApiParser.Parse(body, batch, _settings.ThumbResolution);
                return true;
            },
            cancellationToken);
    }

    private async Task FillGalleryListAsync(
        List<GalleryInfo> list,
        string refererUrl,
        bool filter,
        CancellationToken cancellationToken)
    {
        if (filter)
        {
            list.RemoveAll(info => !_filter.FilterTitle(info) || !_filter.FilterUploader(info));
        }

        bool hasTags = false;
        bool hasPages = false;
        bool hasRated = false;
        foreach (GalleryInfo gi in list)
        {
            if (gi.SimpleTags is not null)
            {
                hasTags = true;
            }

            if (gi.Pages != 0)
            {
                hasPages = true;
            }

            if (gi.Rated)
            {
                hasRated = true;
            }
        }

        bool needApi = (filter && _filter.NeedTags && !hasTags)
            || (_settings.ShowGalleryPages && !hasPages)
            || hasRated;

        if (needApi && list.Count > 0)
        {
            await FillGalleryListByApiAsync(list, refererUrl, cancellationToken).ConfigureAwait(false);
        }

        if (filter)
        {
            list.RemoveAll(info =>
                !_filter.FilterUploader(info)
                || !_filter.FilterTag(info)
                || !_filter.FilterTagNamespace(info));
        }

        if (_settings.FixThumbUrl)
        {
            foreach (GalleryInfo info in list)
            {
                if (!string.IsNullOrEmpty(info.Thumb))
                {
                    info.Thumb = EhUrl.GetFixedPreviewThumbUrl(info.Thumb);
                }
            }
        }
    }

    public Task<GalleryDetail> GetGalleryDetailAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        string referer = EhUrl.GetReferer(CurrentSite);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Get(url, referer),
            (body, _) =>
            {
                NotifyEventPane(body);
                return GalleryDetailParser.Parse(body, CreateDetailParserOptions());
            },
            cancellationToken);
    }

    public Task<(PreviewSet PreviewSet, int PreviewPages)> GetPreviewSetAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        string referer = EhUrl.GetReferer(CurrentSite);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Get(url, referer),
            (body, _) =>
            {
                GalleryDetailParserOptions options = CreateDetailParserOptions();
                return (GalleryDetailParser.ParsePreviewSet(body, options), GalleryDetailParser.ParsePreviewPages(body));
            },
            cancellationToken);
    }

    public async Task<GalleryListParser.Result> ImageSearchAsync(
        string imagePath,
        bool useSimilarityScan,
        bool onlySearchCovers,
        bool showExpunged,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        string rawFileName = Path.GetFileName(imagePath);
        string uploadFileName = rawFileName.Contains('.') ? rawFileName : rawFileName + ".jpg";
        string url = EhUrl.GetImageSearchUrl(CurrentSite);
        string referer = EhUrl.GetReferer(CurrentSite) + "/";
        string origin = EhUrl.GetOrigin(CurrentSite);

        HttpResponseMetadata? lastMeta = null;
        string body = string.Empty;

        try
        {
            await using FileStream fs = File.OpenRead(imagePath);
            using var multipart = new MultipartFormDataContent();
            var fileContent = new StreamContent(fs);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            multipart.Add(fileContent, "sfile", uploadFileName);

            if (useSimilarityScan)
            {
                multipart.Add(new StringContent("on"), "fs_similar");
            }

            if (onlySearchCovers)
            {
                multipart.Add(new StringContent("on"), "fs_covers");
            }

            if (showExpunged)
            {
                multipart.Add(new StringContent("on"), "fs_exp");
            }

            multipart.Add(new StringContent("File Search"), "f_sfile");

            using HttpRequestMessage request = EhRequestBuilder.Post(url, multipart, referer, origin);
            HttpResponseResult response = await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);
            lastMeta = response.Metadata;
            body = response.Body;

            // Handle explicit 302 when running against a non-redirecting test handler.
            if (response.Metadata.StatusCode == 302 && response.Metadata.Location is not null)
            {
                string redirectUrl = response.Metadata.Location.IsAbsoluteUri
                    ? response.Metadata.Location.ToString()
                    : new Uri(new Uri(url), response.Metadata.Location).ToString();
                using HttpRequestMessage redirectReq = EhRequestBuilder.Get(redirectUrl, referer);
                HttpResponseResult redirectResp = await SendCoreAsync(redirectReq, cancellationToken).ConfigureAwait(false);
                lastMeta = redirectResp.Metadata;
                body = redirectResp.Body;
            }

            ValidateResponseHeaders(lastMeta, body);
            GalleryListParser.Result result = GalleryListParser.Parse(body, ListUrlBuilder.ModeNormal, CreateListParserOptions());
            ThrowIfErrorStatusCode(lastMeta);
            await FillGalleryListAsync(result.GalleryInfoList, url, filter: true, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            throw MapException(ex, lastMeta, body, cancellationToken);
        }
    }

    // --- Ratings & Comments ---

    public Task<RateGalleryParser.Result> RateGalleryAsync(
        long apiUid,
        string apiKey,
        long gid,
        string token,
        float rating,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        ArgumentException.ThrowIfNullOrEmpty(token);

        string payload = JsonSerializer.Serialize(new
        {
            method = "rategallery",
            apiuid = apiUid,
            apikey = apiKey,
            gid,
            token,
            rating = (int)Math.Ceiling(rating * 2)
        });

        string url = EhUrl.GetApiUrl(CurrentSite);
        string referer = EhUrl.GetGalleryDetailUrl(gid, token, site: CurrentSite);
        string origin = EhUrl.GetOrigin(CurrentSite);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Post(url, CreateJsonContent(payload), referer, origin),
            static (body, _) => RateGalleryParser.Parse(body),
            cancellationToken);
    }

    public Task<GalleryCommentList> CommentGalleryAsync(
        string url,
        string comment,
        string? commentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(comment);

        var pairs = new List<KeyValuePair<string, string>>(2);
        if (commentId is null)
        {
            pairs.Add(new KeyValuePair<string, string>("commenttext_new", comment));
        }
        else
        {
            pairs.Add(new KeyValuePair<string, string>("commenttext_edit", comment));
            pairs.Add(new KeyValuePair<string, string>("edit_comment", commentId));
        }

        string origin = EhUrl.GetOrigin(CurrentSite);
        return ExecuteParsedAsync(
            () => EhRequestBuilder.Post(url, new FormUrlEncodedContent(pairs), url, origin),
            (body, _) =>
            {
                IHtmlDocument document = EhHtmlHelper.ParseDocument(body);
                IElement? errorElement = document.QuerySelector("#chd + p");
                if (errorElement is not null)
                {
                    throw new EhException(EhHtmlHelper.GetNormalizedText(errorElement));
                }

                return GalleryDetailParser.ParseComments(document, _database.InBlackList);
            },
            cancellationToken);
    }

    public Task<GetEditCommentParser.Result> GetEditCommentAsync(
        long apiUid,
        string apiKey,
        long gid,
        string token,
        long commentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        ArgumentException.ThrowIfNullOrEmpty(token);

        string payload = JsonSerializer.Serialize(new
        {
            method = "geteditcomment",
            apiuid = apiUid,
            apikey = apiKey,
            gid,
            token,
            comment_id = commentId
        });

        string url = EhUrl.GetApiUrl(CurrentSite);
        string referer = EhUrl.GetGalleryDetailUrl(gid, token, site: CurrentSite);
        string origin = EhUrl.GetOrigin(CurrentSite);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Post(url, CreateJsonContent(payload), referer, origin),
            static (body, _) => GetEditCommentParser.Parse(body),
            cancellationToken);
    }

    public Task<VoteCommentParser.Result> VoteCommentAsync(
        long apiUid,
        string apiKey,
        long gid,
        string token,
        long commentId,
        int commentVote,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        ArgumentException.ThrowIfNullOrEmpty(token);

        string payload = JsonSerializer.Serialize(new
        {
            method = "votecomment",
            apiuid = apiUid,
            apikey = apiKey,
            gid,
            token,
            comment_id = commentId,
            comment_vote = commentVote
        });

        string url = EhUrl.GetApiUrl(CurrentSite);
        string referer = EhUrl.GetReferer(CurrentSite);
        string origin = EhUrl.GetOrigin(CurrentSite);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Post(url, CreateJsonContent(payload), referer, origin),
            (body, _) => VoteCommentParser.Parse(body, commentVote),
            cancellationToken);
    }

    // --- Pages & Tokens ---

    public Task<string> GetGalleryTokenAsync(
        long gid,
        string gtoken,
        int page,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(gtoken);

        string payload = JsonSerializer.Serialize(new
        {
            method = "gtoken",
            pagelist = new[] { new object[] { gid, gtoken, page + 1 } }
        });

        string url = EhUrl.GetApiUrl(CurrentSite);
        string referer = EhUrl.GetReferer(CurrentSite);
        string origin = EhUrl.GetOrigin(CurrentSite);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Post(url, CreateJsonContent(payload), referer, origin),
            static (body, _) => GalleryTokenApiParser.Parse(body),
            cancellationToken);
    }

    public Task<GalleryPageParser.Result> GetGalleryPageAsync(
        string url,
        long gid,
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        string referer = EhUrl.GetGalleryDetailUrl(gid, token, site: CurrentSite);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Get(url, referer),
            static (body, _) => GalleryPageParser.Parse(body),
            cancellationToken);
    }

    public Task<GalleryPageApiParser.Result> GetGalleryPageApiAsync(
        long gid,
        int index,
        string pToken,
        string showKey,
        string? previousPToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pToken);
        ArgumentException.ThrowIfNullOrEmpty(showKey);

        string payload = JsonSerializer.Serialize(new
        {
            method = "showpage",
            gid,
            page = index + 1,
            imgkey = pToken,
            showkey = showKey
        });

        string url = EhUrl.GetApiUrl(CurrentSite);
        string? referer = index > 0 && previousPToken is not null
            ? EhUrl.GetPageUrl(gid, index - 1, previousPToken, CurrentSite)
            : null;
        string origin = EhUrl.GetOrigin(CurrentSite);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Post(url, CreateJsonContent(payload), referer, origin),
            static (body, _) => GalleryPageApiParser.Parse(body),
            cancellationToken);
    }

    // --- Favorites ---

    public Task<FavoritesParser.Result> GetAllFavoritesAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        string referer = EhUrl.GetReferer(CurrentSite);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Get(url, referer),
            (body, _) => FavoritesParser.Parse(body, CreateListParserOptions()),
            cancellationToken);
    }

    public async Task<FavoritesParser.Result> GetFavoritesAsync(
        string url,
        bool callApi = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        string referer = EhUrl.GetReferer(CurrentSite);

        FavoritesParser.Result result = await ExecuteParsedAsync(
            () => EhRequestBuilder.Get(url, referer),
            (body, _) => FavoritesParser.Parse(body, CreateListParserOptions()),
            cancellationToken).ConfigureAwait(false);

        await FillGalleryListAsync(result.GalleryInfoList, url, filter: false, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task AddFavoritesAsync(
        long gid,
        string token,
        int dstCat,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        string catStr = dstCat switch
        {
            -1 => "favdel",
            >= 0 and <= 9 => dstCat.ToString(CultureInfo.InvariantCulture),
            _ => throw new EhException($"Invalid dstCat: {dstCat}")
        };

        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("favcat", catStr),
            new KeyValuePair<string, string>("favnote", note ?? string.Empty),
            new KeyValuePair<string, string>("submit", "Apply Changes"),
            new KeyValuePair<string, string>("update", "1")
        ]);

        string url = EhUrl.GetAddFavorites(gid, token, CurrentSite);
        string origin = EhUrl.GetOrigin(CurrentSite);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Post(url, form, url, origin),
            static (_, _) => true,
            cancellationToken);
    }

    public async Task AddFavoritesRangeAsync(
        long[] gidArray,
        string[] tokenArray,
        int dstCat,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gidArray);
        ArgumentNullException.ThrowIfNull(tokenArray);
        if (gidArray.Length != tokenArray.Length)
        {
            throw new ArgumentException("GID and token arrays must have equal length.");
        }

        for (int i = 0; i < gidArray.Length; i++)
        {
            await AddFavoritesAsync(gidArray[i], tokenArray[i], dstCat, null, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<FavoritesParser.Result> ModifyFavoritesAsync(
        string url,
        long[] gidArray,
        int dstCat,
        bool callApi = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(gidArray);

        string catStr = dstCat switch
        {
            -1 => "delete",
            >= 0 and <= 9 => "fav" + dstCat.ToString(CultureInfo.InvariantCulture),
            _ => throw new EhException($"Invalid dstCat: {dstCat}")
        };

        var pairs = new List<KeyValuePair<string, string>>(gidArray.Length + 2)
        {
            new("ddact", catStr)
        };
        foreach (long gid in gidArray)
        {
            pairs.Add(new KeyValuePair<string, string>("modifygids[]", gid.ToString(CultureInfo.InvariantCulture)));
        }

        pairs.Add(new KeyValuePair<string, string>("apply", "Apply"));

        string origin = EhUrl.GetOrigin(CurrentSite);
        FavoritesParser.Result result = await ExecuteParsedAsync(
            () => EhRequestBuilder.Post(url, new FormUrlEncodedContent(pairs), url, origin),
            (body, _) => FavoritesParser.Parse(body, CreateListParserOptions()),
            cancellationToken).ConfigureAwait(false);

        await FillGalleryListAsync(result.GalleryInfoList, url, filter: false, cancellationToken).ConfigureAwait(false);
        return result;
    }

    // --- Torrents, TopLists, Archiver, H@H ---

    public Task<TorrentInfo[]> GetTorrentListAsync(
        string url,
        long gid,
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        string referer = EhUrl.GetGalleryDetailUrl(gid, token, site: CurrentSite);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Get(url, referer),
            static (body, _) => TorrentParser.Parse(body),
            cancellationToken);
    }

    public Task<EhTopListDetail> GetTopListAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        string referer = EhUrl.GetTopListUrl();

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Get(url, referer),
            static (body, _) => TopListParser.Parse(body),
            cancellationToken);
    }

    public Task<ArchiveParser.ParseResult> GetArchiveListAsync(
        string url,
        long gid,
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        string referer = EhUrl.GetGalleryDetailUrl(gid, token, site: CurrentSite);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Get(url, referer),
            static (body, _) => ArchiveParser.Parse(body),
            cancellationToken);
    }

    public Task<ArchiverData> GetArchiverAsync(
        string url,
        long gid,
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        string referer = EhUrl.GetGalleryDetailUrl(gid, token, site: CurrentSite);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Get(url, referer),
            (body, _) => ArchiveParser.ParseArchiver(body, CurrentSite),
            cancellationToken);
    }

    public Task DownloadArchiveAsync(
        long gid,
        string token,
        string or,
        string res,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(or))
        {
            throw new EhException($"Invalid form param or: {or}");
        }

        if (string.IsNullOrEmpty(res))
        {
            throw new EhException($"Invalid res: {res}");
        }

        var form = new FormUrlEncodedContent([new KeyValuePair<string, string>("hathdl_xres", res)]);
        string url = EhUrl.GetDownloadArchive(gid, token, or, CurrentSite);
        string referer = EhUrl.GetGalleryDetailUrl(gid, token, site: CurrentSite);
        string origin = EhUrl.GetOrigin(CurrentSite);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Post(url, form, referer, origin),
            static (body, _) =>
            {
                if (PatternNeedHathClient.IsMatch(body))
                {
                    throw new NoHAtHClientException("No H@H client");
                }

                return true;
            },
            cancellationToken);
    }

    public async Task<string?> DownloadArchiverAsync(
        string url,
        string referer,
        string dltype,
        string dlcheck,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(url))
        {
            throw new EhException($"Invalid form param url: {url}");
        }

        if (string.IsNullOrEmpty(referer))
        {
            throw new EhException($"Invalid form param referer: {referer}");
        }

        string origin = EhUrl.GetOrigin(CurrentSite);
        var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("dltype", dltype),
            new KeyValuePair<string, string>("dlcheck", dlcheck)
        ]);

        HttpResponseMetadata? lastMeta = null;
        string body = string.Empty;
        try
        {
            using HttpRequestMessage postReq = EhRequestBuilder.Post(url, form, referer, origin);
            HttpResponseResult postResp = await SendCoreAsync(postReq, cancellationToken).ConfigureAwait(false);
            lastMeta = postResp.Metadata;
            body = postResp.Body;
            ValidateResponseHeaders(postResp.Metadata, body);
            ThrowIfErrorStatusCode(postResp.Metadata);

            Match m = PatternDocumentLocation.Match(body);
            if (!m.Success)
            {
                return null;
            }

            string continueUrl = m.Groups[1].Value;
            using HttpRequestMessage continueReq = EhRequestBuilder.Get(continueUrl, origin);
            HttpResponseResult continueResp = await SendCoreAsync(continueReq, cancellationToken).ConfigureAwait(false);
            lastMeta = continueResp.Metadata;
            body = continueResp.Body;
            ValidateResponseHeaders(continueResp.Metadata, body);
            ThrowIfErrorStatusCode(continueResp.Metadata);

            string? downloadPath = ArchiveParser.ParseArchiverDownloadUrl(body);
            if (string.IsNullOrEmpty(downloadPath))
            {
                return null;
            }

            Uri finalUri = continueResp.Metadata.FinalUri ?? new Uri(continueUrl);
            return new Uri(finalUri, downloadPath).ToString();
        }
        catch (Exception ex)
        {
            throw MapException(ex, lastMeta, body, cancellationToken);
        }
    }

    // --- Watched Tags ---

    public Task<UserTagList?> GetWatchedListAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!_cookieStore.HasSignedIn)
        {
            return Task.FromResult<UserTagList?>(null);
        }

        return ExecuteParsedAsync<UserTagList?>(
            () => EhRequestBuilder.Get(url),
            static (body, _) => MyTagListParser.Parse(body),
            cancellationToken);
    }

    public Task<UserTagList?> AddTagAsync(
        string url,
        TagPushParam param,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(param);
        if (!_cookieStore.HasSignedIn)
        {
            return Task.FromResult<UserTagList?>(null);
        }

        return ExecuteParsedAsync<UserTagList?>(
            () => EhRequestBuilder.Post(url, new StringContent(param.AddTagParam(), Encoding.UTF8, "application/x-www-form-urlencoded")),
            static (body, _) => MyTagListParser.Parse(body),
            cancellationToken);
    }

    public Task<UserTagList?> DeleteWatchedTagAsync(
        string url,
        UserTag param,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(param);
        if (!_cookieStore.HasSignedIn)
        {
            return Task.FromResult<UserTagList?>(null);
        }

        return ExecuteParsedAsync<UserTagList?>(
            () => EhRequestBuilder.Post(url, new StringContent(param.DeleteParam(), Encoding.UTF8, "application/x-www-form-urlencoded")),
            static (body, _) => MyTagListParser.Parse(body),
            cancellationToken);
    }

    // --- News & Home Quota ---

    public Task<EhNewsDetail> GetEhNewsAsync(CancellationToken cancellationToken = default)
    {
        string url = EhUrl.GetEhNewsUrl();
        return ExecuteParsedAsync(
            () => EhRequestBuilder.Get(url),
            static (body, _) =>
            {
                var detail = new EhNewsDetail(body);
                detail.GetEventPane();
                return detail;
            },
            cancellationToken);
    }

    public Task<HomeDetail> GetHomeDetailAsync(CancellationToken cancellationToken = default)
    {
        string url = EhUrl.GetHomeUrl();

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Get(url, EhUrl.RefererE),
            (body, _) =>
            {
                NotifyEventPane(body);
                return EhHomeParser.Parse(body);
            },
            cancellationToken);
    }

    public Task<string> GetUConfigAsync(EhSite site, CancellationToken cancellationToken = default)
    {
        string url = EhUrl.GetUConfigUrl(site);
        string referer = EhUrl.GetReferer(site);
        return ExecuteParsedAsync(
            () => EhRequestBuilder.Get(url, referer),
            static (body, _) => body,
            cancellationToken);
    }

    public Task<HomeDetail> ResetLimitAsync(CancellationToken cancellationToken = default)
    {
        string url = EhUrl.GetHomeUrl();
        var form = new FormUrlEncodedContent([new KeyValuePair<string, string>("reset_imagelimit", "Reset Limit")]);

        return ExecuteParsedAsync(
            () => EhRequestBuilder.Post(url, form, EhUrl.RefererE),
            (body, _) =>
            {
                NotifyEventPane(body);
                return EhHomeParser.Parse(body);
            },
            cancellationToken);
    }

    // --- Shared Execution & Error Mapping Pipeline ---

    private async Task<T> ExecuteParsedAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        Func<string, HttpResponseMetadata, T> parser,
        CancellationToken cancellationToken)
    {
        HttpResponseMetadata? metadata = null;
        string body = string.Empty;

        try
        {
            using HttpRequestMessage request = requestFactory();
            HttpResponseResult response = await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);
            metadata = response.Metadata;
            body = response.Body;

            ValidateResponseHeaders(metadata, body);
            T parsed = parser(body, metadata);
            ThrowIfErrorStatusCode(metadata);
            return parsed;
        }
        catch (Exception ex)
        {
            throw MapException(ex, metadata, body, cancellationToken);
        }
    }

    private async Task<HttpResponseResult> SendCoreAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpClient client = _getHttpClient();
        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string? disposition = response.Content.Headers.ContentDisposition?.ToString();
        string? contentType = response.Content.Headers.ContentType?.MediaType;
        long? contentLength = response.Content.Headers.ContentLength;

        var meta = new HttpResponseMetadata(
            (int)response.StatusCode,
            disposition,
            contentType,
            contentLength,
            response.Headers.Location,
            response.RequestMessage?.RequestUri);

        return new HttpResponseResult(meta, body);
    }

    private static void ThrowIfErrorStatusCode(HttpResponseMetadata meta)
    {
        if (meta.StatusCode >= 400)
        {
            throw new StatusCodeException(meta.StatusCode);
        }
    }

    private static void ValidateResponseHeaders(HttpResponseMetadata meta, string? body)
    {
        if (string.Equals(meta.ContentDisposition, SadPandaDisposition, StringComparison.Ordinal)
            && string.Equals(meta.ContentType, SadPandaContentType, StringComparison.Ordinal)
            && meta.ContentLength == SadPandaLength)
        {
            throw new EhException("Sad Panda");
        }

        if (body is not null && body.Contains(KokomadeUrl, StringComparison.Ordinal))
        {
            throw new EhException("今回はここまで\n\nDaily quota exhausted");
        }
    }

    /// <summary>
    /// Translates a low-level failure into the exception the caller should observe.
    /// </summary>
    /// <remarks>
    /// This must NOT run inside a <c>catch ... when</c> filter: an exception thrown from a filter
    /// is swallowed by the CLR and the filter evaluates to <see langword="false"/>, which would
    /// silently disable every remapping branch below. The caller therefore catches plainly and
    /// rethrows the value returned here.
    /// </remarks>
    private Exception MapException(
        Exception ex,
        HttpResponseMetadata? metadata,
        string? body,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || ex is OperationCanceledException or CancelledException)
        {
            // Preserve the original cancellation as inner exception so timing/diagnostics survive.
            return new CancelledException(ex);
        }

        if (metadata is not null)
        {
            try
            {
                ValidateResponseHeaders(metadata, body);
            }
            catch (EhException headerEx)
            {
                return headerEx;
            }
        }

        if (ex is ParseException parseEx)
        {
            if (!string.IsNullOrEmpty(body) && !body.Contains('<'))
            {
                return new EhException(body, parseEx);
            }

            if (string.IsNullOrEmpty(body))
            {
                return new EhException("Empty HTML response", parseEx);
            }

            TrySaveParseErrorBody(parseEx);
            return new EhException("Failed to parse server response", parseEx);
        }

        if (ex is EhException)
        {
            // Already a domain exception with a meaningful message: propagate unchanged.
            return ex;
        }

        if (metadata is not null && metadata.StatusCode >= 400)
        {
            return new StatusCodeException(metadata.StatusCode);
        }

        return ex;
    }

    private void TrySaveParseErrorBody(ParseException ex)
    {
        if (!_settings.SaveParseErrorBody || string.IsNullOrWhiteSpace(_settings.ParseErrorDirectory) || string.IsNullOrEmpty(ex.Body))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_settings.ParseErrorDirectory);
            string fileName = $"parse_error_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}.html";
            File.WriteAllText(Path.Combine(_settings.ParseErrorDirectory, fileName), ex.Body, Encoding.UTF8);
        }
        catch
        {
            // Never mask parse error with logging I/O error.
        }
    }

    private void NotifyEventPane(string body)
    {
        string? html = EhEventParser.Parse(body);
        if (!string.IsNullOrEmpty(html))
        {
            EventPaneReceived?.Invoke(html);
        }
    }

    private GalleryListParserOptions CreateListParserOptions()
    {
        return new GalleryListParserOptions(
            ThumbResolution: _settings.ThumbResolution,
            IsLocalFavorite: _database.ContainLocalFavorites);
    }

    private GalleryDetailParserOptions CreateDetailParserOptions()
    {
        return new GalleryDetailParserOptions(
            ThumbResolution: _settings.ThumbResolution,
            IsBlacklistedUser: _database.InBlackList,
            FixThumbUrl: _settings.FixThumbUrl);
    }

    private static StringContent CreateJsonContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    [GeneratedRegex(@"(You must have a H@H client assigned to your account to use this feature\.)", RegexOptions.CultureInvariant)]
    private static partial Regex CreateNeedHathClientRegex();

    [GeneratedRegex(@"document\.location = ""(.*)""", RegexOptions.CultureInvariant)]
    private static partial Regex CreateDocumentLocationRegex();

    private sealed record HttpResponseMetadata(
        int StatusCode,
        string? ContentDisposition,
        string? ContentType,
        long? ContentLength,
        Uri? Location,
        Uri? FinalUri);

    private sealed record HttpResponseResult(HttpResponseMetadata Metadata, string Body);
}

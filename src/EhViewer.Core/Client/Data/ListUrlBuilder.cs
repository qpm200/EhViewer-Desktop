using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using EhViewer.Core.Network;
using EhViewer.Core.Util;

namespace EhViewer.Core.Client.Data;

public enum PageNavigationAction
{
    GotoFirstPage = 0,
    GotoPrevPage = 1,
    GotoNextPage = 2,
    GotoLastPage = 3,
    TypeSomewhere = 4
}

/// <summary>
/// Pagination href container returned by list/favorites parsers and consumed by URL builders.
/// </summary>
public sealed class PageHrefSet
{
    public string? FirstHref { get; set; }
    public string? PrevHref { get; set; }
    public string? NextHref { get; set; }
    public string? LastHref { get; set; }

    public string? ResolveAction(PageNavigationAction action) => action switch
    {
        PageNavigationAction.GotoFirstPage => FirstHref,
        PageNavigationAction.GotoPrevPage => PrevHref,
        PageNavigationAction.GotoNextPage or PageNavigationAction.TypeSomewhere => NextHref,
        PageNavigationAction.GotoLastPage => LastHref,
        _ => FirstHref
    };
}

/// <summary>
/// Builds gallery list / search / uploader / tag / popular / top-list URLs matching upstream <c>ListUrlBuilder</c>.
/// </summary>
public sealed partial class ListUrlBuilder
{
    public const int ModeNormal = 0x0;
    public const int ModeUploader = 0x1;
    public const int ModeTag = 0x2;
    public const int ModeWhatsHot = 0x3;
    public const int ModeImageSearch = 0x4;
    public const int ModeSubscription = 0x5;
    public const int ModeFilter = 0x6;
    public const int ModeTopList = 0x7;

    public const int DefaultAdvance = (int)(AdvanceSearchFlags.SName | AdvanceSearchFlags.STags);
    public const int DefaultMinRating = 2;

    private static readonly Regex PatternSeekDate = SeekDateRegex();
    private static readonly Regex PatternJumpNode = JumpNodeRegex();

    private static readonly (AdvanceSearchFlags Flag, string QueryKey)[] AdvanceQueryMap =
    [
        (AdvanceSearchFlags.SName, "f_sname"),
        (AdvanceSearchFlags.STags, "f_stags"),
        (AdvanceSearchFlags.SDesc, "f_sdesc"),
        (AdvanceSearchFlags.STorr, "f_storr"),
        (AdvanceSearchFlags.STo, "f_sto"),
        (AdvanceSearchFlags.SDt1, "f_sdt1"),
        (AdvanceSearchFlags.SDt2, "f_sdt2"),
        (AdvanceSearchFlags.SH, "f_sh"),
        (AdvanceSearchFlags.SFl, "f_sfl"),
        (AdvanceSearchFlags.SFu, "f_sfu"),
        (AdvanceSearchFlags.SFt, "f_sft")
    ];

    private static readonly IReadOnlyDictionary<string, int> AdvanceFlagLookup =
        AdvanceQueryMap.ToDictionary(pair => pair.QueryKey, pair => (int)pair.Flag, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, int> CategoryFlagLookup =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["f_doujinshi"] = EhConfig.Doujinshi,
            ["f_manga"] = EhConfig.Manga,
            ["f_artistcg"] = EhConfig.ArtistCg,
            ["f_gamecg"] = EhConfig.GameCg,
            ["f_western"] = EhConfig.Western,
            ["f_non-h"] = EhConfig.NonH,
            ["f_imageset"] = EhConfig.ImageSet,
            ["f_cosplay"] = EhConfig.Cosplay,
            ["f_asianporn"] = EhConfig.AsianPorn,
            ["f_misc"] = EhConfig.Misc
        };

    public int Mode { get; set; } = ModeNormal;
    public int PageIndex { get; set; }
    public int Category { get; set; } = EhUtils.None;
    public string? Keyword { get; set; }
    public string? Follow { get; set; }
    public int AdvanceSearch { get; set; } = -1;
    public int MinRating { get; set; } = -1;
    public int PageFrom { get; set; } = -1;
    public int PageTo { get; set; } = -1;
    public string? ImagePath { get; set; }
    public bool UseSimilarityScan { get; set; }
    public bool OnlySearchCovers { get; set; }
    public bool ShowExpunged { get; set; }

    public void Reset()
    {
        Mode = ModeNormal;
        PageIndex = 0;
        Category = EhUtils.None;
        Keyword = null;
        Follow = null;
        AdvanceSearch = -1;
        MinRating = -1;
        PageFrom = -1;
        PageTo = -1;
        ImagePath = null;
        UseSimilarityScan = false;
        OnlySearchCovers = false;
        ShowExpunged = false;
    }

    public ListUrlBuilder Clone() => new()
    {
        Mode = Mode,
        PageIndex = PageIndex,
        Category = Category,
        Keyword = Keyword,
        Follow = Follow,
        AdvanceSearch = AdvanceSearch,
        MinRating = MinRating,
        PageFrom = PageFrom,
        PageTo = PageTo,
        ImagePath = ImagePath,
        UseSimilarityScan = UseSimilarityScan,
        OnlySearchCovers = OnlySearchCovers,
        ShowExpunged = ShowExpunged
    };

    /// <summary>
    /// Populates builder fields from an E-Hentai/ExHentai search query string (without leading '?').
    /// </summary>
    public void SetQuery(string? query)
    {
        Reset();

        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        int category = 0;
        string? keyword = null;
        bool enableAdvanceSearch = false;
        int advanceSearch = 0;
        bool enableMinRating = false;
        int minRating = -1;
        bool enablePage = false;
        int pageFrom = -1;
        int pageTo = -1;

        foreach (string part in query.Split('&'))
        {
            int index = part.IndexOf('=');
            if (index < 0)
            {
                continue;
            }

            string key = part[..index];
            string value = part[(index + 1)..];

            if (key == "f_cats")
            {
                int cats = NumberUtils.ParseIntSafely(value, EhConfig.AllCategory);
                category |= (~cats) & EhConfig.AllCategory;
            }
            else if (CategoryFlagLookup.TryGetValue(key, out int catFlag))
            {
                if (value == "1")
                {
                    category |= catFlag;
                }
            }
            else if (AdvanceFlagLookup.TryGetValue(key, out int advFlag))
            {
                if (value == "on")
                {
                    advanceSearch |= advFlag;
                }
            }
            else
            {
                switch (key)
                {
                    case "f_search":
                        keyword = WebUtility.UrlDecode(value);
                        break;
                    case "advsearch":
                        if (value == "1")
                        {
                            enableAdvanceSearch = true;
                        }
                        break;
                    case "f_sr":
                        if (value == "on")
                        {
                            enableMinRating = true;
                        }
                        break;
                    case "f_srdd":
                        minRating = NumberUtils.ParseIntSafely(value, -1);
                        break;
                    case "f_sp":
                        if (value == "on")
                        {
                            enablePage = true;
                        }
                        break;
                    case "f_spf":
                        pageFrom = NumberUtils.ParseIntSafely(value, -1);
                        break;
                    case "f_spt":
                        pageTo = NumberUtils.ParseIntSafely(value, -1);
                        break;
                }
            }
        }

        Category = category;
        Keyword = keyword;
        if (enableAdvanceSearch)
        {
            AdvanceSearch = advanceSearch;
            MinRating = enableMinRating ? minRating : -1;
            if (enablePage)
            {
                PageFrom = pageFrom;
                PageTo = pageTo;
            }
            else
            {
                PageFrom = -1;
                PageTo = -1;
            }
        }
        else
        {
            AdvanceSearch = -1;
        }
    }

    public string? Build(PageNavigationAction pageAction, PageHrefSet helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        return helper.ResolveAction(pageAction);
    }

    public static string JumpHrefBuild(string urlOld, string appendParam)
    {
        ArgumentNullException.ThrowIfNull(urlOld);
        ArgumentNullException.ThrowIfNull(appendParam);

        Match seekMatch = PatternSeekDate.Match(urlOld);
        if (seekMatch.Success)
        {
            return urlOld.Replace(seekMatch.Value, appendParam, StringComparison.Ordinal);
        }

        Match jumpMatch = PatternJumpNode.Match(urlOld);
        if (jumpMatch.Success)
        {
            return urlOld.Replace(jumpMatch.Value, appendParam, StringComparison.Ordinal);
        }

        char separator = urlOld.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{urlOld}{separator}{appendParam}";
    }

    public string Build(EhSite site = EhSite.E)
    {
        switch (Mode)
        {
            default:
            case ModeNormal:
            case ModeSubscription:
                return BuildNormalOrSubscription(site);
            case ModeUploader:
                return BuildPathSegmentMode(EhUrl.GetHost(site), "uploader/");
            case ModeTag:
                return BuildPathSegmentMode(EhUrl.GetHost(site), "tag/");
            case ModeFilter:
                return BuildFilterMode(site);
            case ModeWhatsHot:
                return EhUrl.GetPopularUrl(site);
            case ModeImageSearch:
                return EhUrl.GetImageSearchUrl(site);
            case ModeTopList:
                return BuildTopListMode();
        }
    }

    private string BuildNormalOrSubscription(EhSite site)
    {
        string baseUrl = Mode == ModeNormal ? EhUrl.GetHost(site) : EhUrl.GetWatchedUrl(site);
        var ub = new UrlBuilder(baseUrl);

        if (Category != EhUtils.None)
        {
            ub.AddQuery("f_cats", (~Category) & EhConfig.AllCategory);
        }

        if (Keyword != null && !string.IsNullOrEmpty(Keyword.Trim()))
        {
            ub.AddQuery("f_search", UrlEncodeUtf8(Keyword));
        }

        if (PageIndex != 0)
        {
            ub.AddQuery("page", PageIndex);
        }

        if (AdvanceSearch != -1)
        {
            ub.AddQuery("advsearch", "1");
            var flags = (AdvanceSearchFlags)AdvanceSearch;
            foreach ((AdvanceSearchFlags flag, string key) in AdvanceQueryMap)
            {
                if (flags.HasFlag(flag))
                {
                    ub.AddQuery(key, "on");
                }
            }

            if (MinRating != -1)
            {
                ub.AddQuery("f_sr", "on");
                ub.AddQuery("f_srdd", MinRating);
            }

            if (PageFrom != -1 || PageTo != -1)
            {
                ub.AddQuery("f_sp", "on");
                ub.AddQuery("f_spf", PageFrom != -1 ? PageFrom.ToString() : string.Empty);
                ub.AddQuery("f_spt", PageTo != -1 ? PageTo.ToString() : string.Empty);
            }
        }

        return ub.Build();
    }

    private string BuildPathSegmentMode(string host, string prefix)
    {
        var sb = new StringBuilder(host).Append(prefix).Append(UrlEncodeUtf8(Keyword ?? string.Empty));
        if (PageIndex != 0)
        {
            sb.Append('/').Append(PageIndex);
        }
        return sb.ToString();
    }

    private string BuildFilterMode(EhSite site)
    {
        var sb = new StringBuilder(EhUrl.GetHost(site)).Append('?');
        if (PageIndex != 0)
        {
            sb.Append("page=").Append(PageIndex).Append('&');
        }
        sb.Append("f_search=").Append(UrlEncodeUtf8(Keyword ?? string.Empty));
        return sb.ToString();
    }

    private string BuildTopListMode()
    {
        var sb = new StringBuilder(EhUrl.GetTopListUrl()).Append('?').Append(Follow);
        if (PageIndex == 0)
        {
            return sb.ToString();
        }
        if (PageIndex is > 0 and < 200)
        {
            return sb.Append("&p=").Append(PageIndex).ToString();
        }
        return "127.0.0.1:8888";
    }

    internal static string UrlEncodeUtf8(string value) => WebUtility.UrlEncode(value) ?? string.Empty;

    [GeneratedRegex(@"seek=(\d+)-(\d+)-(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SeekDateRegex();

    [GeneratedRegex(@"jump=(\d)[ymwd]", RegexOptions.CultureInvariant)]
    private static partial Regex JumpNodeRegex();
}

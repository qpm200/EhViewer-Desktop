using EhViewer.Core.Network;

namespace EhViewer.Core.Client;

/// <summary>
/// Canonical E-Hentai and ExHentai endpoint builders decoupled from global static settings.
/// </summary>
public static class EhUrl
{
    public const int SiteE = 0;
    public const int SiteEx = 1;

    public const string DomainEx = "exhentai.org";
    public const string DomainE = "e-hentai.org";
    public const string DomainLofi = "lofi.e-hentai.org";
    public const string DomainForums = "forums.e-hentai.org";

    public const string RefererEx = "https://" + DomainEx;
    public const string RefererE = "https://" + DomainE;

    public const string HostEx = RefererEx + "/";
    public const string HostE = RefererE + "/";

    public const string ApiSignIn = "https://forums.e-hentai.org/index.php?act=Login&CODE=01";
    public const string UrlNewsE = HostE + "news.php";

    public const string ApiE = HostE + "api.php";
    public const string ApiEx = HostEx + "api.php";

    public const string HomeE = HostE + "home.php";
    public const string HomeEx = HostEx + "home.php";

    public const string UrlPopularE = "https://e-hentai.org/popular";
    public const string UrlPopularEx = "https://exhentai.org/popular";

    public const string UrlTopListE = HostE + "toplist.php";
    public const string UrlTopListEx = HostEx + "toplist.php";

    public const string UrlImageSearchE = "https://upld.e-hentai.org/image_lookup.php";
    public const string UrlImageSearchEx = "https://upld.exhentai.org/upld/image_lookup.php";

    public const string UrlSignIn = "https://forums.e-hentai.org/index.php?act=Login";
    public const string UrlRegister = "https://forums.e-hentai.org/index.php?act=Reg&CODE=00";
    public const string UrlFavoritesE = HostE + "favorites.php";
    public const string UrlFavoritesEx = HostEx + "favorites.php";
    public const string UrlForums = "https://forums.e-hentai.org/";

    public const string OriginEx = RefererEx;
    public const string OriginE = RefererE;

    public const string UrlUConfigE = HostE + "uconfig.php";
    public const string UrlUConfigEx = HostEx + "uconfig.php";

    public const string UrlMyTagsE = HostE + "mytags";
    public const string UrlMyTagsEx = HostEx + "mytags";

    public const string UrlWatchedE = HostE + "watched";
    public const string UrlWatchedEx = HostEx + "watched";

    private const string UrlPrefixThumbE = "https://ehgt.org/";
    private const string UrlPrefixThumbEx = "https://exhentai.org/t/";

    public static string GetHost(EhSite site = EhSite.E) => site == EhSite.Ex ? HostEx : HostE;
    public static string GetHomeUrl() => HomeE;
    public static string GetMyTag(EhSite site = EhSite.E) => site == EhSite.Ex ? UrlMyTagsEx : UrlMyTagsE;
    public static string GetFavoritesUrl(EhSite site = EhSite.E) => site == EhSite.Ex ? UrlFavoritesEx : UrlFavoritesE;
    public static string GetApiUrl(EhSite site = EhSite.E) => site == EhSite.Ex ? ApiEx : ApiE;
    public static string GetReferer(EhSite site = EhSite.E) => site == EhSite.Ex ? RefererEx : RefererE;
    public static string GetOrigin(EhSite site = EhSite.E) => site == EhSite.Ex ? OriginEx : OriginE;
    public static string GetUConfigUrl(EhSite site = EhSite.E) => site == EhSite.Ex ? UrlUConfigEx : UrlUConfigE;
    public static string GetMyTagsUrl(EhSite site = EhSite.E) => site == EhSite.Ex ? UrlMyTagsEx : UrlMyTagsE;
    public static string GetPopularUrl(EhSite site = EhSite.E) => site == EhSite.Ex ? UrlPopularEx : UrlPopularE;
    public static string GetTopListUrl() => UrlTopListE;
    public static string GetEhNewsUrl() => UrlNewsE;
    public static string GetImageSearchUrl(EhSite site = EhSite.E) => site == EhSite.Ex ? UrlImageSearchEx : UrlImageSearchE;
    public static string GetWatchedUrl(EhSite site = EhSite.E) => site == EhSite.Ex ? UrlWatchedEx : UrlWatchedE;
    public static string GetThumbUrlPrefix() => UrlPrefixThumbE;

    public static string GetGalleryDetailUrl(long gid, string token, int index = 0, bool allComment = false, EhSite site = EhSite.E)
    {
        var builder = new UrlBuilder($"{GetHost(site)}g/{gid}/{token}/");
        if (index != 0)
        {
            builder.AddQuery("p", index);
        }
        if (allComment)
        {
            builder.AddQuery("hc", 1);
        }
        return builder.Build();
    }

    public static string GetPageUrl(long gid, int index, string pToken, EhSite site = EhSite.E)
        => $"{GetHost(site)}s/{pToken}/{gid}-{index + 1}";

    public static string GetAddFavorites(long gid, string token, EhSite site = EhSite.E)
        => $"{GetHost(site)}gallerypopups.php?gid={gid}&t={token}&act=addfav";

    public static string GetDownloadArchive(long gid, string token, string? or, EhSite site = EhSite.E)
    {
        if (string.IsNullOrEmpty(or))
        {
            return $"{GetHost(site)}archiver.php?gid={gid}&token={token}";
        }
        return $"{GetHost(site)}archiver.php?gid={gid}&token={token}&or={or}";
    }

    public static string GetTagDefinitionUrl(string tag)
        => "https://ehwiki.org/wiki/" + tag.Replace(' ', '_');

    public static string GetFixedPreviewThumbUrl(string originUrl)
    {
        if (!Uri.TryCreate(originUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return originUrl;
        }

        string trimmed = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return originUrl;
        }

        string[] segments = trimmed.Split('/');
        if (segments.Length < 3)
        {
            return originUrl;
        }

        string last = segments[^1];
        string secondLast = segments[^2];
        string thirdLast = segments[^3];

        if (last.StartsWith(thirdLast, StringComparison.Ordinal) &&
            last.Length >= thirdLast.Length + secondLast.Length &&
            last.AsSpan(thirdLast.Length).StartsWith(secondLast, StringComparison.Ordinal))
        {
            return $"{GetThumbUrlPrefix()}{thirdLast}/{secondLast}/{last}";
        }

        return originUrl;
    }
}

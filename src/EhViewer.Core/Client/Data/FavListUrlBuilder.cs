using EhViewer.Core.Network;

namespace EhViewer.Core.Client.Data;

/// <summary>
/// Builds favorites list URLs matching upstream <c>FavListUrlBuilder</c>.
/// </summary>
public sealed class FavListUrlBuilder
{
    public const int FavCatAll = -1;
    public const int FavCatLocal = -2;

    public int Index { get; set; }
    public string? Keyword { get; set; }
    public int FavCat { get; set; } = FavCatAll;

    public static bool IsValidFavCat(int favCat) => favCat is >= 0 and <= 9;

    public bool IsLocalFavCat => FavCat == FavCatLocal;

    public string? Build(PageNavigationAction pageAction, PageHrefSet helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        return helper.ResolveAction(pageAction);
    }

    public string Build(EhSite site = EhSite.E)
    {
        var ub = new UrlBuilder(EhUrl.GetFavoritesUrl(site));
        if (IsValidFavCat(FavCat))
        {
            ub.AddQuery("favcat", FavCat.ToString());
        }

        if (!string.IsNullOrEmpty(Keyword))
        {
            ub.AddQuery("f_search", ListUrlBuilder.UrlEncodeUtf8(Keyword));
            ub.AddQuery("sn", "on");
            ub.AddQuery("st", "on");
            ub.AddQuery("sf", "on");
        }

        if (Index > 0)
        {
            ub.AddQuery("page", Index.ToString());
        }

        return ub.Build();
    }
}

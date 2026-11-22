using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Exceptions;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses favorite categories, counts, sort order, and gallery items from <c>favorites.php</c>.
/// </summary>
public static class FavoritesParser
{
    public sealed class Result
    {
        public string[] CatArray { get; set; } = new string[10];
        public int[] CountArray { get; set; } = new int[10];
        public int Pages { get; set; }
        public int NextPage { get; set; }
        public string? ResultCount { get; set; }
        public string? FirstHref { get; set; }
        public string? PrevHref { get; set; }
        public string? NextHref { get; set; }
        public string? LastHref { get; set; }
        public string? FavOrder { get; set; }
        public List<GalleryInfo> GalleryInfoList { get; set; } = [];
    }

    public static Result Parse(string body, GalleryListParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (body.Contains("This page requires you to log on.</p>", StringComparison.Ordinal))
        {
            throw new EhException("This page requires you to log on.");
        }

        var re = new Result();
        string[] catArray = new string[10];
        int[] countArray = new int[10];

        IHtmlDocument d = EhHtmlHelper.ParseDocument(body);
        IElement? ido = EhHtmlHelper.GetElementByClass(d, "ido");
        if (ido is null)
        {
            throw new ParseException("Parse favorites error", body);
        }

        IHtmlCollection<IElement> fps = ido.GetElementsByClassName("fp");
        // Last one is "fp fps" ("Show All Favorites").
        if (fps.Length != 11)
        {
            throw new ParseException("Parse favorites error", body);
        }

        for (int i = 0; i < 10; i++)
        {
            IElement fp = fps[i];
            if (fp.Children.Length < 3)
            {
                throw new ParseException("Parse favorites error", body);
            }

            countArray[i] = ParserUtils.ParseInt(EhHtmlHelper.GetNormalizedText(fp.Children[0]), 0);
            catArray[i] = ParserUtils.Trim(EhHtmlHelper.GetNormalizedText(fp.Children[2]));
        }

        IHtmlCollection<IElement> orders = d.GetElementsByClassName("searchnav");
        if (orders.Length > 0)
        {
            IHtmlCollection<IElement> selects = orders[0].GetElementsByTagName("select");
            if (selects.Length > 0)
            {
                IElement? selected = selects[0].QuerySelector("[selected]");
                re.FavOrder = selected?.GetAttribute("value") ?? string.Empty;
            }
        }

        GalleryListParser.Result listResult = GalleryListParser.Parse(d, body, ListUrlBuilder.ModeNormal, options);
        re.CatArray = catArray;
        re.CountArray = countArray;
        re.Pages = listResult.Pages;
        re.NextPage = listResult.NextPage;
        re.ResultCount = listResult.ResultCount;
        re.FirstHref = listResult.FirstHref;
        re.NextHref = listResult.NextHref;
        re.PrevHref = listResult.PrevHref;
        re.LastHref = listResult.LastHref;
        re.GalleryInfoList = listResult.GalleryInfoList;

        return re;
    }
}

using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using EhViewer.Core.Client.Data.TopList;
using EhViewer.Core.Client.Exceptions;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses the 7-category E-Hentai <c>toplist.php</c> HTML page.
/// </summary>
public static class TopListParser
{
    public static EhTopListDetail Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        EhHtmlHelper.ThrowIfSiteError(body, checkWarnings: true);

        IHtmlDocument document = EhHtmlHelper.ParseDocument(body);
        IElement? ido = EhHtmlHelper.GetElementByClass(document, "ido");
        if (ido is null || ido.Children.Length < 14)
        {
            throw new ParseException("Can't parse toplist", body);
        }

        IHtmlCollection<IElement> elements = ido.Children;
        return new EhTopListDetail
        {
            Title = EhHtmlHelper.GetNormalizedText(elements[0]),
            GalleryTopListInfo = ParseInfo(elements[1], TopListType.Gallery),
            UploaderTopListInfo = ParseInfo(elements[3], TopListType.Uploader),
            TaggingTopListInfo = ParseInfo(elements[5], TopListType.Tagging),
            HentaiHomeTopListInfo = ParseInfo(elements[7], TopListType.HentaiHome),
            EhTrackerTopListInfo = ParseInfo(elements[9], TopListType.EhTracker),
            CleanUpTopListInfo = ParseInfo(elements[11], TopListType.Cleanup),
            RatingAndReviewingTopListInfo = ParseInfo(elements[13], TopListType.RatingAndReviewing)
        };
    }

    private static TopListInfo ParseInfo(IElement element, TopListType type)
    {
        var info = new TopListInfo
        {
            Type = type
        };

        IHtmlCollection<IElement> children = element.Children;
        if (children.Length < 5)
        {
            return info;
        }

        info.AllTimeTopList = ParseArray(ResolveTableContainer(children[1]));
        info.PastYearTopList = ParseArray(ResolveTableContainer(children[2]));
        info.PastMonthTopList = ParseArray(ResolveTableContainer(children[3]));
        info.YesterdayTopList = ParseArray(ResolveTableContainer(children[4]));
        return info;
    }

    private static IElement? ResolveTableContainer(IElement sectionRow) =>
        EhHtmlHelper.GetChildElement(EhHtmlHelper.GetChildElement(sectionRow, 1), 0);

    private static TopListItemArray ParseArray(IElement? container)
    {
        var result = new TopListItemArray();
        if (container is null)
        {
            return result;
        }

        IHtmlCollection<IElement>? minItems = EhHtmlHelper.GetElementsByClass(container, "tun");
        if (minItems is null || minItems.Length == 0)
        {
            return result;
        }

        var items = new TopListItem[minItems.Length];
        for (int i = 0; i < minItems.Length; i++)
        {
            IElement? link = EhHtmlHelper.GetChildElement(minItems[i], 0);
            if (link is null)
            {
                continue;
            }

            items[i] = new TopListItem
            {
                Value = EhHtmlHelper.GetNormalizedText(link),
                Href = link.GetAttribute("href")
            };
        }

        result.ItemArray = items;
        return result;
    }
}

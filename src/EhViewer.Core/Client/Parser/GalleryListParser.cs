using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Exceptions;
using EhViewer.Core.Util;

namespace EhViewer.Core.Client.Parser;

public sealed record GalleryListParserOptions(
    int ThumbResolution = 0,
    Func<long, bool>? IsLocalFavorite = null,
    Action<IReadOnlyList<GalleryInfo>>? OnTagsParsed = null);

/// <summary>
/// Parses E-Hentai/ExHentai gallery list HTML in Minimal, Minimal+, Compact, Extended, and Thumbnail modes.
/// </summary>
public static partial class GalleryListParser
{
    private static readonly Regex PatternRating = CreateRatingPattern();
    private static readonly Regex PatternThumbSize = CreateThumbSizePattern();
    private static readonly Regex PatternFavoriteSlot = CreateFavoriteSlotPattern();
    private static readonly Regex PatternPages = CreatePagesPattern();
    private static readonly Regex PatternNextPage = CreateNextPagePattern();
    private static readonly Regex PatternResultCountPage = CreateResultCountPattern();

    private static readonly string[][] FavoriteSlotRgb =
    [
        ["0", "0", "0"],
        ["240", "0", "0"],
        ["240", "160", "0"],
        ["208", "208", "0"],
        ["0", "128", "0"],
        ["144", "240", "64"],
        ["64", "176", "240"],
        ["0", "0", "240"],
        ["80", "0", "128"],
        ["224", "128", "224"]
    ];

    public sealed class Result
    {
        public int Pages { get; set; }
        public int NextPage { get; set; }
        public string? ResultCount { get; set; }
        public string? FirstHref { get; set; }
        public string? PrevHref { get; set; }
        public string? NextHref { get; set; }
        public string? LastHref { get; set; }
        public string? CustomErrorString { get; set; }
        public bool NoWatchedTags { get; set; }
        public List<GalleryInfo> GalleryInfoList { get; set; } = [];
    }

    public static Result Parse(
        string body,
        int mode = ListUrlBuilder.ModeNormal,
        GalleryListParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            IHtmlDocument document = EhHtmlHelper.ParseDocument(body);
            return Parse(document, body, mode, options);
        }
        catch (ParseException)
        {
            return new Result();
        }
    }

    public static Result Parse(
        IHtmlDocument document,
        string body,
        int mode = ListUrlBuilder.ModeNormal,
        GalleryListParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(body);
        _ = mode;

        var result = new Result();
        if (!TryParsePagination(document, result))
        {
            result.NoWatchedTags = body.Contains("<p>You do not have any watched tags", StringComparison.Ordinal);
            if (body.Contains("No hits found</p>", StringComparison.Ordinal))
            {
                result.Pages = 0;
                result.GalleryInfoList = [];
                return result;
            }

            result.Pages = document.GetElementsByClassName("ptt").Length == 0 ? 1 : int.MaxValue;
        }

        IElement? itg = EhHtmlHelper.GetElementByClass(document, "itg");
        if (itg is null)
        {
            throw new ParseException("Can't parse gallery list", body);
        }

        IHtmlCollection<IElement> rows = string.Equals(itg.TagName, "table", StringComparison.OrdinalIgnoreCase)
            ? (EhHtmlHelper.GetChildElement(itg, 0)?.Children ?? throw new ParseException("Can't parse gallery list", body))
            : itg.Children;

        var list = new List<GalleryInfo>(rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            GalleryInfo? gi = ParseGalleryInfo(rows[i], options);
            if (gi is not null)
            {
                list.Add(gi);
            }
        }

        if (list.Count == 0 && !HasTableEmptyMessage(document))
        {
            throw new ParseException("No gallery", body);
        }

        result.GalleryInfoList = list;
        options?.OnTagsParsed?.Invoke(result.GalleryInfoList);
        return result;
    }

    private static bool TryParsePagination(IHtmlDocument document, Result result)
    {
        IElement? ptt = EhHtmlHelper.GetElementByClass(document, "ptt");
        if (ptt is null)
        {
            IElement? searchNav = EhHtmlHelper.GetElementByClass(document, "searchnav");
            result.Pages = -1;
            result.NextPage = -1;
            if (searchNav is null)
            {
                return false;
            }

            result.FirstHref = GetNavHref(document, searchNav, "uFirst");
            result.PrevHref = GetNavHref(document, searchNav, "uprev");
            result.NextHref = GetNavHref(document, searchNav, "unext");
            result.LastHref = GetNavHref(document, searchNav, "ulast");

            IElement? searchText = EhHtmlHelper.GetElementByClass(document, "searchtext");
            if (searchText is not null)
            {
                string text = EhHtmlHelper.GetNormalizedText(searchText);
                Match matcher = PatternResultCountPage.Match(text);
                if (matcher.Success)
                {
                    result.ResultCount = FormatResultCount(matcher.Value);
                }
            }
            else
            {
                result.ResultCount = string.Empty;
            }

            return true;
        }

        IElement? tr = EhHtmlHelper.GetChildElement(EhHtmlHelper.GetChildElement(ptt, 0), 0);
        if (tr is null || tr.Children.Length < 2)
        {
            return false;
        }

        IHtmlCollection<IElement> cells = tr.Children;
        if (!int.TryParse(EhHtmlHelper.GetNormalizedText(cells[^2]), out int pages))
        {
            return false;
        }

        result.Pages = pages;
        IElement? nextAnchor = EhHtmlHelper.GetChildElement(cells[^1], 0);
        if (nextAnchor is not null)
        {
            string href = nextAnchor.GetAttribute("href") ?? string.Empty;
            Match matcher = PatternNextPage.Match(href);
            if (matcher.Success)
            {
                result.NextPage = NumberUtils.ParseIntSafely(matcher.Groups[1].Value, 0);
            }
        }

        return true;
    }

    private static string GetNavHref(IHtmlDocument document, IElement searchNav, string id)
    {
        IElement? el = searchNav.QuerySelector("#" + id) ?? document.GetElementById(id);
        return el?.GetAttribute("href") ?? string.Empty;
    }

    private static string FormatResultCount(string foundMatch)
    {
        string[] parts = foundMatch.Split(' ');
        if (parts.Length > 3)
        {
            return parts[1] switch
            {
                "thousands" => "1,000+",
                "about" => parts[2] + "+",
                _ => string.Concat(parts[1..^1])
            };
        }

        return parts.Length == 3 ? parts[1] : string.Empty;
    }

    private static bool HasTableEmptyMessage(IHtmlDocument document)
    {
        IHtmlCollection<IElement> tables = document.GetElementsByClassName("itg gltc");
        if (tables.Length == 0)
        {
            return false;
        }

        IElement? cell = EhHtmlHelper.GetChildElement(
            EhHtmlHelper.GetChildElement(
                EhHtmlHelper.GetChildElement(tables[0], 0), 1), 0);
        return cell is not null && !string.IsNullOrEmpty(EhHtmlHelper.GetNormalizedText(cell));
    }

    private static GalleryInfo? ParseGalleryInfo(IElement e, GalleryListParserOptions? options)
    {
        var gi = new GalleryInfo();

        IElement? glname = EhHtmlHelper.GetElementByClass(e, "glname");
        if (glname is not null)
        {
            IElement? a = EhHtmlHelper.GetElementByTag(glname, "a");
            if (a is null && glname.ParentElement is { } parent &&
                string.Equals(parent.TagName, "a", StringComparison.OrdinalIgnoreCase))
            {
                a = parent;
            }

            if (a is not null)
            {
                GalleryDetailUrlParser.Result? detailUrl = GalleryDetailUrlParser.Parse(a.GetAttribute("href"));
                if (detailUrl is not null)
                {
                    gi.Gid = detailUrl.Gid;
                    gi.Token = detailUrl.Token;
                }
            }

            IElement child = glname;
            while (child.Children.Length != 0)
            {
                child = child.Children[0];
            }
            gi.Title = EhHtmlHelper.GetNormalizedText(child);

            IElement? tbody = EhHtmlHelper.GetElementByTag(glname, "tbody");
            if (tbody is not null)
            {
                var tags = new List<string>();
                GalleryTagGroup[] groups = GalleryTagParser.ParseTagGroups(tbody.Children);
                foreach (GalleryTagGroup group in groups)
                {
                    foreach (string tag in group.Tags)
                    {
                        tags.Add(group.GroupName + ":" + tag);
                    }
                }
                gi.SimpleTags = [.. tags];
            }
        }

        if (gi.Title is null)
        {
            return null;
        }

        gi.Category = EhUtils.Unknown;
        IElement? categoryEl = EhHtmlHelper.GetElementByClass(e, "cn") ?? EhHtmlHelper.GetElementByClass(e, "cs");
        if (categoryEl is not null)
        {
            gi.Category = EhUtils.GetCategory(EhHtmlHelper.GetNormalizedText(categoryEl));
        }

        int thumbResolution = options?.ThumbResolution ?? 0;

        IElement? glThumb = EhHtmlHelper.GetElementByClass(e, "glthumb");
        if (glThumb is not null)
        {
            IElement? img = glThumb.QuerySelector(":scope > div:nth-child(1) > img");
            if (img is not null)
            {
                ApplyThumbSize(gi, img.GetAttribute("style"));
                string? url = img.GetAttribute("data-src");
                if (string.IsNullOrEmpty(url))
                {
                    url = img.GetAttribute("src");
                }
                if (string.IsNullOrEmpty(url))
                {
                    url = null;
                }
                gi.Thumb = EhUtils.HandleThumbUrlResolution(url, thumbResolution);
            }

            IElement? pagesDiv = glThumb.QuerySelector(":scope > div:nth-child(2) > div:nth-child(2) > div:nth-child(2)");
            if (pagesDiv is not null)
            {
                gi.Pages = ExtractPagesCount(pagesDiv);
            }
        }

        if (gi.Thumb is null)
        {
            IElement? gl = EhHtmlHelper.GetElementByClass(e, "gl1e") ?? EhHtmlHelper.GetElementByClass(e, "gl3t");
            IElement? img = gl is not null ? EhHtmlHelper.GetElementByTag(gl, "img") : null;
            if (img is not null)
            {
                ApplyThumbSize(gi, img.GetAttribute("style"));
                gi.Thumb = EhUtils.HandleThumbUrlResolution(img.GetAttribute("src"), thumbResolution);
            }
        }

        gi.FavoriteSlot = -2;
        string postedId = $"posted_{gi.Gid}";
        IElement? posted = string.Equals(e.Id, postedId, StringComparison.Ordinal)
            ? e
            : e.QuerySelector("#" + postedId);
        if (posted is not null)
        {
            gi.Posted = EhHtmlHelper.GetNormalizedText(posted);
            gi.FavoriteSlot = ParseFavoriteSlot(posted.GetAttribute("style") ?? string.Empty);
        }

        if (gi.FavoriteSlot == -2 && options?.IsLocalFavorite?.Invoke(gi.Gid) == true)
        {
            gi.FavoriteSlot = -1;
        }

        ParseSimpleTagAttributes(gi, e);

        IElement? ir = EhHtmlHelper.GetElementByClass(e, "ir");
        if (ir is not null)
        {
            gi.Rating = NumberUtils.ParseFloatSafely(ParseRating(ir.GetAttribute("style") ?? string.Empty), -1.0f);
            gi.Rated = ir.ClassList.Contains("irr") || ir.ClassList.Contains("irg") || ir.ClassList.Contains("irb");
        }

        IElement? metaContainer = EhHtmlHelper.GetElementByClass(e, "glhide");
        int uploaderIndex = 0;
        int pagesIndex = 1;
        if (metaContainer is null)
        {
            metaContainer = EhHtmlHelper.GetElementByClass(e, "gl3e");
            uploaderIndex = 3;
            pagesIndex = 4;
        }

        if (metaContainer is not null)
        {
            IHtmlCollection<IElement> children = metaContainer.Children;
            if (children.Length > uploaderIndex)
            {
                IElement? a = EhHtmlHelper.GetChildElement(children[uploaderIndex], 0);
                if (a is not null)
                {
                    gi.Uploader = EhHtmlHelper.GetNormalizedText(a);
                }
            }
            if (children.Length > pagesIndex)
            {
                gi.Pages = ExtractPagesCount(children[pagesIndex]);
            }
        }

        IElement? gl5t = EhHtmlHelper.GetElementByClass(e, "gl5t");
        if (gl5t is not null)
        {
            IElement? div = gl5t.QuerySelector(":scope > div:nth-child(2) > div:nth-child(2)");
            if (div is not null)
            {
                gi.Pages = ExtractPagesCount(div);
            }
        }

        gi.GenerateSLang();
        return gi;
    }

    private static void ApplyThumbSize(GalleryInfo gi, string? style)
    {
        Match m = PatternThumbSize.Match(style ?? string.Empty);
        if (m.Success)
        {
            gi.ThumbWidth = NumberUtils.ParseIntSafely(m.Groups[2].Value, 0);
            gi.ThumbHeight = NumberUtils.ParseIntSafely(m.Groups[1].Value, 0);
        }
        else
        {
            gi.ThumbWidth = 0;
            gi.ThumbHeight = 0;
        }
    }

    private static int ExtractPagesCount(IElement element)
    {
        Match m = PatternPages.Match(EhHtmlHelper.GetNormalizedText(element));
        return m.Success ? NumberUtils.ParseIntSafely(m.Groups[1].Value, 0) : 0;
    }

    private static string? ParseRating(string ratingStyle)
    {
        MatchCollection matches = PatternRating.Matches(ratingStyle);
        if (matches.Count < 2)
        {
            return null;
        }

        int num1 = ParserUtils.ParseInt(matches[0].Value.Replace("px", string.Empty, StringComparison.Ordinal), int.MinValue);
        int num2 = ParserUtils.ParseInt(matches[1].Value.Replace("px", string.Empty, StringComparison.Ordinal), int.MinValue);
        if (num1 == int.MinValue || num2 == int.MinValue)
        {
            return null;
        }

        int rate = 5 - num1 / 16;
        if (num2 == 21)
        {
            rate--;
            return rate.ToString() + ".5";
        }

        return rate.ToString();
    }

    private static int ParseFavoriteSlot(string style)
    {
        Match m = PatternFavoriteSlot.Match(style);
        if (!m.Success)
        {
            return -2;
        }

        string r = m.Groups[1].Value;
        string g = m.Groups[2].Value;
        string b = m.Groups[3].Value;
        for (int slot = 0; slot < FavoriteSlotRgb.Length; slot++)
        {
            string[] rgb = FavoriteSlotRgb[slot];
            if (r == rgb[0] && g == rgb[1] && b == rgb[2])
            {
                return slot;
            }
        }

        return -2;
    }

    private static void ParseSimpleTagAttributes(GalleryInfo gi, IElement e)
    {
        AppendTitlesFromClass(gi, e, "gt");
        AppendTitlesFromClass(gi, e, "gtl");
    }

    private static void AppendTitlesFromClass(GalleryInfo gi, IElement e, string className)
    {
        IHtmlCollection<IElement>? elements = EhHtmlHelper.GetElementsByClass(e, className);
        if (elements is null)
        {
            return;
        }

        gi.TgList ??= [];
        foreach (IElement item in elements)
        {
            string? title = item.GetAttribute("title");
            if (title is not null)
            {
                gi.TgList.Add(title);
            }
        }
    }

    [GeneratedRegex(@"\d+px", RegexOptions.CultureInvariant)]
    private static partial Regex CreateRatingPattern();

    [GeneratedRegex(@"height:(\d+)px;width:(\d+)px", RegexOptions.CultureInvariant)]
    private static partial Regex CreateThumbSizePattern();

    [GeneratedRegex(@"background-color:rgba\((\d+),(\d+),(\d+),", RegexOptions.CultureInvariant)]
    private static partial Regex CreateFavoriteSlotPattern();

    [GeneratedRegex(@"(\d+) page", RegexOptions.CultureInvariant)]
    private static partial Regex CreatePagesPattern();

    [GeneratedRegex(@"page=(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex CreateNextPagePattern();

    [GeneratedRegex(@"Found .* results", RegexOptions.CultureInvariant)]
    private static partial Regex CreateResultCountPattern();
}

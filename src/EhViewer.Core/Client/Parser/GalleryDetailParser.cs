using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Exceptions;
using EhViewer.Core.Util;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Options controlling thumbnail resolution, comment blacklist filtering, and thumb host rewriting
/// in <see cref="GalleryDetailParser"/>.
/// </summary>
public sealed record GalleryDetailParserOptions(
    int ThumbResolution = 0,
    Func<string?, bool>? IsBlacklistedUser = null,
    bool FixThumbUrl = false);

/// <summary>
/// Parses gallery detail HTML pages, tags, comments, page counts, and preview sprite sets.
/// </summary>
public static partial class GalleryDetailParser
{
    private const string UnavailableString = "This gallery is unavailable";

    private static readonly string[] CommentDateFormats =
    [
        "dd MMMM yyyy, HH:mm",
        "d MMMM yyyy, HH:mm"
    ];

    private static readonly Regex PatternDetail = CreateDetailRegex();
    private static readonly Regex PatternTorrent = CreateTorrentRegex();
    private static readonly Regex PatternArchive = CreateArchiveRegex();
    private static readonly Regex PatternCover = CreateCoverRegex();
    private static readonly Regex PatternTagGroup = CreateTagGroupRegex();
    private static readonly Regex PatternTag = CreateTagRegex();
    private static readonly Regex PatternComment = CreateCommentRegex();
    private static readonly Regex PatternPages = CreatePagesRegex();
    private static readonly Regex PatternPreviewPages = CreatePreviewPagesRegex();
    private static readonly Regex PatternNormalPreview = CreateNormalPreviewRegex();
    private static readonly Regex PatternNormalPreviewNew = CreateNormalPreviewNewRegex();
    private static readonly Regex PatternNormalPreviewNewWithLabel = CreateNormalPreviewNewWithLabelRegex();
    private static readonly Regex PatternSmallPreview = CreateSmallPreviewRegex();
    private static readonly Regex PatternSmallPreviewWithLabel = CreateSmallPreviewWithLabelRegex();
    private static readonly Regex PatternLargePreview = CreateLargePreviewRegex();
    private static readonly Regex PatternLargePreviewNew = CreateLargePreviewNewRegex();

    public static GalleryDetail Parse(string body, GalleryDetailParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        options ??= new GalleryDetailParserOptions();

        if (body.Contains(UnavailableString, StringComparison.Ordinal))
        {
            // Check offensive/pining first to keep upstream precedence before unavailable.
            EhHtmlHelper.ThrowIfSiteError(body, checkWarnings: true);
            throw new GalleryUnavailableException();
        }

        EhHtmlHelper.ThrowIfSiteError(body, checkWarnings: true);

        IHtmlDocument document = EhHtmlHelper.ParseDocument(body);
        var galleryDetail = new GalleryDetail();
        ParseDetail(galleryDetail, document, body, options.ThumbResolution);
        galleryDetail.Tags = ParseTagGroups(document);
        galleryDetail.Comments = ParseComments(document, options.IsBlacklistedUser);
        galleryDetail.PreviewPages = ParsePreviewPages(document, body);
        galleryDetail.PreviewSet = ParsePreviewSet(document, body, options);
        galleryDetail.SpiderInfoPages = ParsePages(body);
        galleryDetail.SpiderInfoPreviewPages = ParsePreviewPages(body);
        galleryDetail.SpiderInfoPreviewSet = ParsePreviewSet(body, options);
        return galleryDetail;
    }

    public static GalleryTagGroup[] ParseTagGroups(IHtmlDocument document) =>
        GalleryTagParser.ParseTagGroups(document);

    public static GalleryTagGroup[] ParseTagGroups(IHtmlCollection<IElement> trs) =>
        GalleryTagParser.ParseTagGroups(trs);

    public static GalleryTagGroup[] ParseTagGroups(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var list = new List<GalleryTagGroup>();
        foreach (Match m in PatternTagGroup.Matches(body))
        {
            var group = new GalleryTagGroup
            {
                GroupName = ParserUtils.Trim(m.Groups[1].Value)
            };
            foreach (Match tagMatch in PatternTag.Matches(m.Groups[0].Value))
            {
                group.AddTag(ParserUtils.Trim(tagMatch.Groups[1].Value));
            }
            list.Add(group);
        }
        return [.. list];
    }

    public static GalleryComment? ParseComment(IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        IElement? prev = element.PreviousElementSibling;
        string? nameAttr = prev?.GetAttribute("name");
        if (prev is null)
        {
            return null;
        }

        var comment = new GalleryComment();
        if (!string.IsNullOrEmpty(nameAttr))
        {
            string trimmedName = StringUtils.Trim(nameAttr) ?? string.Empty;
            if (trimmedName.Length > 1 &&
                int.TryParse(trimmedName.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                comment.Id = id;
            }
        }

        IElement? c4 = EhHtmlHelper.GetElementByClass(element, "c4");
        if (c4 is not null)
        {
            foreach (IElement e in c4.Children)
            {
                string actionText = EhHtmlHelper.GetNormalizedText(e);
                bool hasStyle = !string.IsNullOrEmpty(StringUtils.Trim(e.GetAttribute("style")));
                switch (actionText)
                {
                    case "Vote+":
                        comment.VoteUpAble = true;
                        comment.VoteUpEd = hasStyle;
                        break;
                    case "Vote-":
                        comment.VoteDownAble = true;
                        comment.VoteDownEd = hasStyle;
                        break;
                    case "Edit":
                        comment.Editable = true;
                        break;
                }
            }
        }

        IElement? c7 = EhHtmlHelper.GetElementByClass(element, "c7");
        if (c7 is not null)
        {
            comment.VoteState = StringUtils.Trim(EhHtmlHelper.GetNormalizedText(c7));
        }

        IElement? c5 = EhHtmlHelper.GetElementByClass(element, "c5");
        if (c5 is not null && c5.Children.Length > 0)
        {
            comment.Score = NumberUtils.ParseIntSafely(
                StringUtils.Trim(EhHtmlHelper.GetNormalizedText(c5.Children[0])),
                0);
        }

        IElement? c3 = EhHtmlHelper.GetElementByClass(element, "c3");
        IElement? c6 = EhHtmlHelper.GetElementByClass(element, "c6");
        if (c3 is null || c6 is null)
        {
            return null;
        }

        string temp = EhHtmlHelper.GetOwnNormalizedText(c3);
        const string postedPrefix = "Posted on ";
        const string bySuffix = " by:";
        if (!temp.StartsWith(postedPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        temp = temp.Contains(bySuffix, StringComparison.Ordinal)
            ? temp[postedPrefix.Length..^bySuffix.Length]
            : temp[postedPrefix.Length..];

        if (!TryParseCommentDate(temp, out long timestampMs))
        {
            return null;
        }
        comment.Time = timestampMs;

        if (c3.Children.Length == 0)
        {
            if (c4 is null)
            {
                return null;
            }
            comment.User = EhHtmlHelper.GetNormalizedText(c4);
        }
        else
        {
            comment.User = EhHtmlHelper.GetNormalizedText(c3.Children[0]);
        }

        comment.Comment = c6.InnerHtml;

        IElement? c8 = EhHtmlHelper.GetElementByClass(element, "c8");
        if (c8 is not null && c8.Children.Length > 0)
        {
            // Preserve upstream behavior: c8 marks a comment as edited using its post-time string.
            comment.LastEdited = timestampMs;
        }

        return comment;
    }

    public static GalleryCommentList ParseComments(IHtmlDocument document, Func<string?, bool>? isBlacklistedUser = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        IElement? cdiv = document.GetElementById("cdiv");
        if (cdiv is null)
        {
            return new GalleryCommentList([], false);
        }

        IHtmlCollection<IElement> c1s = cdiv.GetElementsByClassName("c1");
        var list = new List<GalleryComment>(c1s.Length);
        for (int i = 0; i < c1s.Length; i++)
        {
            GalleryComment? comment = ParseComment(c1s[i]);
            if (comment is not null && isBlacklistedUser?.Invoke(comment.User) != true)
            {
                list.Add(comment);
            }
        }

        IElement? chd = cdiv.QuerySelector("#chd");
        if (chd is null)
        {
            return new GalleryCommentList([], false);
        }

        bool hasMore = false;
        if (EhHtmlHelper.GetNormalizedText(chd) == "click to show all")
        {
            hasMore = true;
        }
        else
        {
            foreach (IElement descendant in chd.QuerySelectorAll("*"))
            {
                if (EhHtmlHelper.GetNormalizedText(descendant) == "click to show all")
                {
                    hasMore = true;
                    break;
                }
            }
        }

        return new GalleryCommentList([.. list], hasMore);
    }

    public static GalleryComment[] ParseComments(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var list = new List<GalleryComment>();

        foreach (Match m in PatternComment.Matches(body))
        {
            string webDateString = ParserUtils.Trim(m.Groups[1].Value);
            TryParseCommentDate(webDateString, out long timeMs);
            list.Add(new GalleryComment
            {
                Time = timeMs,
                User = ParserUtils.Trim(m.Groups[2].Value),
                Comment = m.Groups[3].Value
            });
        }

        return [.. list];
    }

    public static int ParsePreviewPages(IHtmlDocument document, string body)
    {
        ArgumentNullException.ThrowIfNull(document);
        IElement? ptt = EhHtmlHelper.GetElementByClass(document, "ptt");
        IElement? tr = EhHtmlHelper.GetChildElement(EhHtmlHelper.GetChildElement(ptt, 0), 0);
        if (tr is null || tr.Children.Length < 2)
        {
            throw new ParseException("Can't parse preview pages", body);
        }

        string text = EhHtmlHelper.GetNormalizedText(tr.Children[^2]);
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pages))
        {
            throw new ParseException("Can't parse preview pages", body);
        }

        return pages;
    }

    public static int ParsePreviewPages(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        Match m = PatternPreviewPages.Match(body);
        int previewPages = m.Success ? ParserUtils.ParseInt(m.Groups[1].Value, -1) : -1;
        if (previewPages <= 0)
        {
            throw new ParseException("Parse preview page count error", body);
        }
        return previewPages;
    }

    public static int ParsePages(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        Match m = PatternPages.Match(body);
        int pages = m.Success ? ParserUtils.ParseInt(m.Groups[1].Value, -1) : -1;
        if (pages < 0)
        {
            throw new ParseException("Parse pages error", body);
        }
        return pages;
    }

    public static PreviewSet ParsePreviewSet(IHtmlDocument document, string body, GalleryDetailParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(body);
        options ??= new GalleryDetailParserOptions();

        IElement? previewContainer = EhHtmlHelper.GetElementByClass(document, "gt200")
            ?? EhHtmlHelper.GetElementByClass(document, "gt100");
        string previewClass = previewContainer?.InnerHtml ?? string.Empty;
        string source = string.IsNullOrEmpty(previewClass) ? body : previewClass;

        PreviewSet previewSet = ParseNormalPreviewSet(source);
        if (previewSet.Size == 0)
        {
            previewSet = ParseLargePreviewSet(source, options.FixThumbUrl);
        }

        return previewSet.Size == 0 ? new NormalPreviewSet() : previewSet;
    }

    public static PreviewSet ParsePreviewSet(string body, GalleryDetailParserOptions? options = null) =>
        ParsePreviewSet(EhHtmlHelper.ParseDocument(body), body, options);

    private static void ParseDetail(GalleryDetail gd, IHtmlDocument document, string body, int thumbResolution)
    {
        Match matcher = PatternDetail.Match(body);
        if (!matcher.Success)
        {
            throw new ParseException("Can't parse gallery detail", body);
        }

        gd.Gid = NumberUtils.ParseLongSafely(matcher.Groups[1].Value, -1L);
        gd.Token = matcher.Groups[2].Value;
        gd.ApiUid = NumberUtils.ParseLongSafely(matcher.Groups[3].Value, -1L);
        gd.ApiKey = matcher.Groups[4].Value;
        if (gd.Gid == -1L)
        {
            throw new ParseException("Can't parse gallery detail", body);
        }

        matcher = PatternTorrent.Match(body);
        if (matcher.Success)
        {
            gd.TorrentUrl = StringUtils.UnescapeXml(StringUtils.Trim(matcher.Groups[1].Value));
            gd.TorrentCount = NumberUtils.ParseIntSafely(matcher.Groups[2].Value, 0);
        }
        else
        {
            gd.TorrentCount = 0;
            gd.TorrentUrl = string.Empty;
        }

        matcher = PatternArchive.Match(body);
        gd.ArchiveUrl = matcher.Success
            ? StringUtils.UnescapeXml(StringUtils.Trim(matcher.Groups[1].Value))
            : string.Empty;

        IElement? gm = EhHtmlHelper.GetElementByClass(document, "gm");
        if (gm is null)
        {
            throw new ParseException("Can't parse gallery detail", body);
        }

        IElement? gd1 = gm.QuerySelector("#gd1");
        IElement? gd1Child = EhHtmlHelper.GetChildElement(gd1, 0);
        string? coverStyle = gd1Child?.GetAttribute("style");
        gd.Thumb = coverStyle is null ? string.Empty : ParseCoverStyle(StringUtils.Trim(coverStyle) ?? string.Empty, thumbResolution);

        IElement? gn = gm.QuerySelector("#gn");
        gd.Title = gn is not null ? StringUtils.Trim(EhHtmlHelper.GetNormalizedText(gn)) ?? string.Empty : string.Empty;

        IElement? gj = gm.QuerySelector("#gj");
        gd.TitleJpn = gj is not null ? StringUtils.Trim(EhHtmlHelper.GetNormalizedText(gj)) ?? string.Empty : string.Empty;

        IElement? gdc = gm.QuerySelector("#gdc");
        IElement? ce = gdc is null ? null : EhHtmlHelper.GetElementByClass(gdc, "cn") ?? EhHtmlHelper.GetElementByClass(gdc, "cs");
        gd.Category = ce is not null ? EhUtils.GetCategory(EhHtmlHelper.GetNormalizedText(ce)) : EhUtils.Unknown;

        IElement? gdn = gm.QuerySelector("#gdn");
        gd.Uploader = gdn is not null ? StringUtils.Trim(EhHtmlHelper.GetNormalizedText(gdn)) ?? string.Empty : string.Empty;

        IElement? gdd = gm.QuerySelector("#gdd");
        gd.Posted = string.Empty;
        gd.Parent = string.Empty;
        gd.Visible = string.Empty;
        gd.Size = string.Empty;
        gd.Pages = 0;
        gd.FavoriteCount = 0;
        IElement? gddTbody = EhHtmlHelper.GetChildElement(EhHtmlHelper.GetChildElement(gdd, 0), 0);
        if (gddTbody is not null)
        {
            foreach (IElement tr in gddTbody.Children)
            {
                ParseDetailInfo(gd, tr);
            }
        }

        IElement? ratingCountEl = gm.QuerySelector("#rating_count");
        gd.RatingCount = ratingCountEl is not null
            ? NumberUtils.ParseIntSafely(StringUtils.Trim(EhHtmlHelper.GetNormalizedText(ratingCountEl)), 0)
            : 0;

        IElement? ratingLabel = gm.QuerySelector("#rating_label");
        if (ratingLabel is not null)
        {
            string ratingStr = StringUtils.Trim(EhHtmlHelper.GetNormalizedText(ratingLabel)) ?? string.Empty;
            if (ratingStr == "Not Yet Rated")
            {
                gd.Rating = -1.0f;
            }
            else
            {
                int index = ratingStr.IndexOf(' ');
                gd.Rating = index == -1 || index >= ratingStr.Length
                    ? 0f
                    : NumberUtils.ParseFloatSafely(ratingStr[(index + 1)..], 0f);
            }
        }
        else
        {
            gd.Rating = -1.0f;
        }

        IElement? gdf = gm.QuerySelector("#gdf");
        if (gdf is not null)
        {
            string favoriteName = StringUtils.Trim(EhHtmlHelper.GetNormalizedText(gdf)) ?? string.Empty;
            bool notFavorited = favoriteName == "Add to Favorites";
            gd.IsFavorited = !notFavorited;
            gd.FavoriteName = notFavorited ? null : favoriteName;
        }
        else
        {
            gd.IsFavorited = false;
        }

        gd.NewVersions = ParseNewVersions(document);
    }

    private static NewVersion[]? ParseNewVersions(IHtmlDocument document)
    {
        IElement? updateElement = document.GetElementById("gnd");
        if (updateElement is null)
        {
            return null;
        }

        var versionList = new List<NewVersion>();
        List<IText> textNodes = updateElement.ChildNodes
            .OfType<IText>()
            .Where(static t => !string.IsNullOrWhiteSpace(t.Data))
            .ToList();

        foreach (IElement child in updateElement.Children)
        {
            string? url = GetAbsoluteHref(child);
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }

            if (versionList.Count >= textNodes.Count)
            {
                return null;
            }

            string nameS = EhHtmlHelper.GetNormalizedText(child);
            string timeS = textNodes[versionList.Count].Data;
            versionList.Add(new NewVersion
            {
                VersionUrl = url,
                VersionName = !string.IsNullOrEmpty(timeS) ? nameS + timeS : nameS
            });
        }

        return versionList.Count == 0 ? null : [.. versionList];
    }

    private static string? GetAbsoluteHref(IElement element)
    {
        string? href = element.GetAttribute("href");
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        return Uri.TryCreate(href, UriKind.Absolute, out _) ? href : null;
    }

    private static string ParseCoverStyle(string str, int thumbResolution)
    {
        Match matcher = PatternCover.Match(str);
        return matcher.Success
            ? EhUtils.HandleThumbUrlResolution(matcher.Groups[3].Value, thumbResolution) ?? string.Empty
            : string.Empty;
    }

    private static void ParseDetailInfo(GalleryDetail gd, IElement tr)
    {
        if (tr.Children.Length < 2)
        {
            return;
        }

        string key = StringUtils.Trim(EhHtmlHelper.GetNormalizedText(tr.Children[0])) ?? string.Empty;
        string value = StringUtils.Trim(EhHtmlHelper.GetOwnNormalizedText(tr.Children[1])) ?? string.Empty;
        if (key.StartsWith("Posted", StringComparison.Ordinal))
        {
            gd.Posted = value;
        }
        else if (key.StartsWith("Parent", StringComparison.Ordinal))
        {
            if (tr.Children[1].Children.Length > 0)
            {
                gd.Parent = tr.Children[1].Children[0].GetAttribute("href") ?? string.Empty;
            }
        }
        else if (key.StartsWith("Visible", StringComparison.Ordinal))
        {
            gd.Visible = value;
        }
        else if (key.StartsWith("Language", StringComparison.Ordinal))
        {
            gd.Language = value;
        }
        else if (key.StartsWith("File Size", StringComparison.Ordinal))
        {
            gd.Size = value;
        }
        else if (key.StartsWith("Length", StringComparison.Ordinal))
        {
            int index = value.IndexOf(' ');
            gd.Pages = index >= 0 ? NumberUtils.ParseIntSafely(value[..index], 1) : 1;
        }
        else if (key.StartsWith("Favorited", StringComparison.Ordinal))
        {
            switch (value)
            {
                case "Never":
                    gd.FavoriteCount = 0;
                    break;
                case "Once":
                    gd.FavoriteCount = 1;
                    break;
                default:
                    int index = value.IndexOf(' ');
                    gd.FavoriteCount = index == -1 ? 0 : NumberUtils.ParseIntSafely(value[..index], 0);
                    break;
            }
        }
    }

    private static bool TryParseCommentDate(string input, out long timestampMs)
    {
        if (DateTimeOffset.TryParseExact(
            input,
            CommentDateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset dto))
        {
            timestampMs = dto.ToUnixTimeMilliseconds();
            return true;
        }

        timestampMs = 0L;
        return false;
    }

    private static LargePreviewSet ParseLargePreviewSet(string body, bool fixThumbUrl)
    {
        var largePreviewSet = new LargePreviewSet();
        CollectLargePreviews(PatternLargePreviewNew.Matches(body), largePreviewSet, fixThumbUrl);
        if (largePreviewSet.Size == 0)
        {
            CollectLargePreviews(PatternLargePreview.Matches(body), largePreviewSet, fixThumbUrl);
        }
        return largePreviewSet;
    }

    private static void CollectLargePreviews(MatchCollection matches, LargePreviewSet largePreviewSet, bool fixThumbUrl)
    {
        foreach (Match m in matches)
        {
            int index = ParserUtils.ParseInt(m.Groups[2].Value, 0) - 1;
            if (index < 0)
            {
                continue;
            }

            string imageUrl = ParserUtils.Trim(m.Groups[3].Value);
            string pageUrl = ParserUtils.Trim(m.Groups[1].Value);
            if (fixThumbUrl)
            {
                imageUrl = EhUrl.GetFixedPreviewThumbUrl(imageUrl);
            }
            largePreviewSet.AddItem(index, imageUrl, pageUrl);
        }
    }

    private static NormalPreviewSet ParseNormalPreviewSet(string body)
    {
        var normalPreviewSet = new NormalPreviewSet();
        CollectNewNormalPreviews(PatternSmallPreview.Matches(body), normalPreviewSet, hasXOffsetGroup: true);

        if (normalPreviewSet.Size == 0)
        {
            CollectNewNormalPreviews(PatternNormalPreviewNew.Matches(body), normalPreviewSet, hasXOffsetGroup: false);
        }
        if (normalPreviewSet.Size == 0)
        {
            CollectNewNormalPreviews(PatternSmallPreviewWithLabel.Matches(body), normalPreviewSet, hasXOffsetGroup: true);
        }
        if (normalPreviewSet.Size == 0)
        {
            CollectNewNormalPreviews(PatternNormalPreviewNewWithLabel.Matches(body), normalPreviewSet, hasXOffsetGroup: false);
        }
        if (normalPreviewSet.Size == 0)
        {
            foreach (Match m in PatternNormalPreview.Matches(body))
            {
                int position = ParserUtils.ParseInt(m.Groups[6].Value, 0) - 1;
                if (position < 0)
                {
                    continue;
                }
                int width = ParserUtils.ParseInt(m.Groups[1].Value, 0);
                if (width <= 0)
                {
                    continue;
                }
                int height = ParserUtils.ParseInt(m.Groups[2].Value, 0);
                if (height <= 0)
                {
                    continue;
                }

                string imageUrl = ParserUtils.Trim(m.Groups[3].Value);
                int xOffset = ParserUtils.ParseInt(m.Groups[4].Value, 0);
                string pageUrl = ParserUtils.Trim(m.Groups[5].Value);
                normalPreviewSet.AddItem(position, imageUrl, xOffset, 0, width, height, pageUrl);
            }
        }

        return normalPreviewSet;
    }

    private static void CollectNewNormalPreviews(
        MatchCollection matches,
        NormalPreviewSet normalPreviewSet,
        bool hasXOffsetGroup)
    {
        foreach (Match m in matches)
        {
            int position = ParserUtils.ParseInt(m.Groups[2].Value, 0) - 1;
            if (position < 0)
            {
                continue;
            }
            int width = ParserUtils.ParseInt(m.Groups[3].Value, 0);
            if (width <= 0)
            {
                continue;
            }
            int height = ParserUtils.ParseInt(m.Groups[4].Value, 0);
            if (height <= 0)
            {
                continue;
            }

            string pageUrl = ParserUtils.Trim(m.Groups[1].Value);
            string imageUrl = ParserUtils.Trim(m.Groups[5].Value);
            int xOffset = hasXOffsetGroup ? ParserUtils.ParseInt(m.Groups[6].Value, 0) : 0;
            normalPreviewSet.AddItem(position, imageUrl, xOffset, 0, width, height, pageUrl);
        }
    }

    [GeneratedRegex(
        @"var gid = (\d+);.+?var token = ""([a-f0-9]+)"";.+?var apiuid = ([\-\d]+);.+?var apikey = ""([a-f0-9]+)"";",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CreateDetailRegex();

    [GeneratedRegex(
        @"<a[^<>]*onclick=""return popUp\('([^']+)'[^)]+\)"">Torrent Download \((\d+)\)</a>",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateTorrentRegex();

    [GeneratedRegex(
        @"<a[^<>]*onclick=""return popUp\('([^']+)'[^)]+\)"">Archive Download</a>",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateArchiveRegex();

    [GeneratedRegex(
        @"width:(\d+)px; height:(\d+)px.+?url\((.+?)\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateCoverRegex();

    [GeneratedRegex(
        @"<tr><td[^<>]+>([\w\s]+):</td><td>(?:<div[^<>]+><a[^<>]+>[\w\s]+</a></div>)+</td></tr>",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateTagGroupRegex();

    [GeneratedRegex(
        @"<div[^<>]+><a[^<>]+>([\w\s]+)</a></div>",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateTagRegex();

    [GeneratedRegex(
        @"<div class=""c3"">Posted on ([^<>]+) by: &nbsp; <a[^<>]+>([^<>]+)</a>.+?<div class=""c6""[^>]*>(.+?)</div><div class=""c[78]""",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateCommentRegex();

    [GeneratedRegex(
        @"<tr><td[^<>]*>Length:</td><td[^<>]*>([\d,]+) pages</td></tr>",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreatePagesRegex();

    [GeneratedRegex(
        @"<td[^>]+><a[^>]+>([\d,]+)</a></td><td[^>]+>(?:<a[^>]+>)?&gt;(?:</a>)?</td>",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreatePreviewPagesRegex();

    [GeneratedRegex(
        @"<div class=""gdtm""[^<>]*><div[^<>]*width:(\d+)[^<>]*height:(\d+)[^<>]*\((.+?)\)[^<>]*-(\d+)px[^<>]*><a[^<>]*href=""(.+?)""[^<>]*><img alt=""([\d,]+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateNormalPreviewRegex();

    [GeneratedRegex(
        @"<a href=""(.+?)"">[^<>]*<div[^<>]*title=""Page (\d+):[^<>]*width:(\d+)[^<>]*height:(\d+)[^<>]*\((.+?)\)[^<>]*""></div>[^<>]*</a>",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateNormalPreviewNewRegex();

    [GeneratedRegex(
        @"<a href=""(.+?)"">[^<>]*<div>[^<>]*<div[^<>]*title=""Page (\d+):[^<>]*width:(\d+)[^<>]*height:(\d+)[^<>]*\((.+?)\)[^<>]*"">",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateNormalPreviewNewWithLabelRegex();

    [GeneratedRegex(
        @"<a href=""(.+?)"">[^<>]*<div[^<>]*title=""Page (\d+):[^<>]*width:(\d+)[^<>]*height:(\d+)[^<>]*\((.+?)\)[^<>]*-(\d+)px[^<>]*>",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateSmallPreviewRegex();

    [GeneratedRegex(
        @"<a href=""(.+?)"">[^<>]*<div>[^<>]*<div[^<>]*title=""Page (\d+):[^<>]*width:(\d+)[^<>]*height:(\d+)[^<>]*\((.+?)\)[^<>]*-(\d+)px[^<>]*>",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateSmallPreviewWithLabelRegex();

    [GeneratedRegex(
        @"<div class=""gdtl"".+?<a href=""(.+?)""><img alt=""([\d,]+)"".+?src=""(.+?)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateLargePreviewRegex();

    [GeneratedRegex(
        @"<a href=""(.+?)"">[^<>]*<div title=""Page (\d+):[^<>]*\((.+?)\)[^<>]*0 0[^<>]*>",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateLargePreviewNewRegex();
}

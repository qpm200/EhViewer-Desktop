using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Exceptions;
using EhViewer.Core.Client.Parser;
using Xunit;

namespace EhViewer.Core.Tests.ParserTests;

public partial class GoldenParserTests
{
    public static TheoryData<string, int> GalleryListFixtures => new()
    {
        { "EmptyGalleryList.html", ListUrlBuilder.ModeNormal },
        { "GalleryListParserNew3.html", ListUrlBuilder.ModeNormal },
        { "GalleryListParserTestECompat.html", ListUrlBuilder.ModeNormal },
        { "GalleryListParserTestEExtended.html", ListUrlBuilder.ModeNormal },
        { "GalleryListParserTestEMinimal.html", ListUrlBuilder.ModeNormal },
        { "GalleryListParserTestEMinimalPlus.html", ListUrlBuilder.ModeNormal },
        { "GalleryListParserTestEThumbnail.html", ListUrlBuilder.ModeNormal },
        { "GalleryListParserTestExCompat.html", ListUrlBuilder.ModeNormal },
        { "GalleryListParserTestExExtended.html", ListUrlBuilder.ModeNormal },
        { "GalleryListParserTestExMinimal.html", ListUrlBuilder.ModeNormal },
        { "GalleryListParserTestExMinimalPlus.html", ListUrlBuilder.ModeNormal },
        { "GalleryListParserTestExThumbnail.html", ListUrlBuilder.ModeNormal },
        { "GalleryListUploader.html", ListUrlBuilder.ModeNormal },
        { "GalleryTopListEX.html", ListUrlBuilder.ModeNormal },
        { "test.html", ListUrlBuilder.ModeNormal },
        { "TopListGallary.html", ListUrlBuilder.ModeNormal },
        { "ImageSearch.html", ListUrlBuilder.ModeImageSearch }
    };

    public static TheoryData<string> GalleryDetailFixtures => new()
    {
        "GalleryDetail.html",
        "newPage.html",
        "spiderInfo.html"
    };

    private static string ReadInput(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "input", "parser", fileName);
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static JsonDocument ReadExpected(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "expected", "parser", fileName);
        return JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    [Theory]
    [MemberData(nameof(GalleryListFixtures))]
    public void GalleryListParser_MatchesGoldenFixture(string fixtureName, int mode)
    {
        string html = ReadInput(fixtureName);
        using JsonDocument expectedDoc = ReadExpected(fixtureName + ".expected.json");
        JsonElement root = expectedDoc.RootElement;

        if (!root.GetProperty("ok").GetBoolean())
        {
            string? expectedErrorType = root.GetProperty("errorType").GetString();
            string? expectedMessage = root.GetProperty("errorMessage").GetString();
            Exception ex = Assert.ThrowsAny<EhException>(() => GalleryListParser.Parse(html, mode));
            Assert.Equal(expectedErrorType, ex.GetType().Name);
            Assert.Equal(expectedMessage, ex.Message);
            return;
        }

        GalleryListParser.Result actual = GalleryListParser.Parse(html, mode);
        JsonElement expected = root.GetProperty("value");

        Assert.Equal(expected.GetProperty("pages").GetInt32(), actual.Pages);
        Assert.Equal(expected.GetProperty("nextPage").GetInt32(), actual.NextPage);
        Assert.Equal(expected.GetProperty("noWatchedTags").GetBoolean(), actual.NoWatchedTags);
        Assert.Equal(expected.GetProperty("firstHref").GetString(), actual.FirstHref);
        Assert.Equal(expected.GetProperty("prevHref").GetString(), actual.PrevHref);
        Assert.Equal(expected.GetProperty("nextHref").GetString(), actual.NextHref);
        Assert.Equal(expected.GetProperty("lastHref").GetString(), actual.LastHref);

        JsonElement expectedList = expected.GetProperty("galleryInfoList");
        Assert.Equal(expectedList.GetArrayLength(), actual.GalleryInfoList.Count);
        for (int i = 0; i < actual.GalleryInfoList.Count; i++)
        {
            MediumParserGoldenTests.AssertGalleryInfoMatches(expectedList[i], actual.GalleryInfoList[i]);
        }
    }

    [Fact]
    public void GalleryListParser_AppliesInjectedOptions()
    {
        string html = ReadInput("GalleryListParserNew3.html");
        IReadOnlyList<GalleryInfo>? syncedBatch = null;

        GalleryListParser.Result result = GalleryListParser.Parse(
            html,
            ListUrlBuilder.ModeNormal,
            new GalleryListParserOptions(
                ThumbResolution: 2,
                IsLocalFavorite: static gid => gid == 2426224L,
                OnTagsParsed: batch => syncedBatch = batch));

        Assert.NotEmpty(result.GalleryInfoList);
        Assert.Same(result.GalleryInfoList, syncedBatch);

        GalleryInfo first = result.GalleryInfoList[0];
        Assert.Equal(2426224L, first.Gid);
        Assert.Equal(-1, first.FavoriteSlot);
        Assert.Contains("_300.jpg", first.Thumb);
    }

    [Theory]
    [MemberData(nameof(GalleryDetailFixtures))]
    public void GalleryDetailParser_TopLevelParseMatchesGoldenFixture(string fixtureName)
    {
        string html = ReadInput(fixtureName);
        using JsonDocument expectedDoc = ReadExpected(fixtureName + ".expected.json");
        JsonElement root = expectedDoc.RootElement;

        Assert.False(root.GetProperty("ok").GetBoolean());
        ParseException ex = Assert.Throws<ParseException>(() => GalleryDetailParser.Parse(html));
        Assert.Equal(root.GetProperty("errorType").GetString(), ex.GetType().Name);
        Assert.Equal(root.GetProperty("errorMessage").GetString(), ex.Message);
    }

    [Fact]
    public void GalleryDetailParser_FixThumbFixtureMatchesGoldenFixture()
    {
        string html = ReadInput("GalleryDetail.html");
        using JsonDocument expectedDoc = ReadExpected("GalleryDetail.fixThumb.html.expected.json");
        JsonElement root = expectedDoc.RootElement;

        Assert.False(root.GetProperty("ok").GetBoolean());
        ParseException ex = Assert.Throws<ParseException>(() =>
            GalleryDetailParser.Parse(
                html,
                new GalleryDetailParserOptions(
                    IsBlacklistedUser: static user => user == "darknesstest",
                    FixThumbUrl: true)));
        Assert.Equal(root.GetProperty("errorType").GetString(), ex.GetType().Name);
        Assert.Equal(root.GetProperty("errorMessage").GetString(), ex.Message);
    }

    [Theory]
    [MemberData(nameof(GalleryDetailFixtures))]
    public void GalleryDetailParser_SubparsersMatchGoldenFixture(string fixtureName)
    {
        string html = ReadInput(fixtureName);
        using JsonDocument expectedDoc = ReadExpected(fixtureName + ".subparsers.expected.json");
        JsonElement root = expectedDoc.RootElement;
        IHtmlDocument document = EhHtmlHelper.ParseDocument(html);

        // parseTagGroups
        GalleryTagGroup[] actualTagGroups = GalleryDetailParser.ParseTagGroups(document);
        JsonElement expectedTagGroups = root.GetProperty("parseTagGroups").GetProperty("value");
        Assert.Equal(expectedTagGroups.GetArrayLength(), actualTagGroups.Length);
        for (int i = 0; i < actualTagGroups.Length; i++)
        {
            Assert.Equal(expectedTagGroups[i].GetProperty("groupName").GetString(), actualTagGroups[i].GroupName);
            Assert.Equal(
                expectedTagGroups[i].GetProperty("tags").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                actualTagGroups[i].Tags);
        }

        // parseCommentsDoc
        GalleryCommentList actualComments = GalleryDetailParser.ParseComments(document);
        JsonElement expectedCommentsRoot = root.GetProperty("parseCommentsDoc").GetProperty("value");
        Assert.Equal(expectedCommentsRoot.GetProperty("hasMore").GetBoolean(), actualComments.HasMore);
        JsonElement expectedCommentArray = expectedCommentsRoot.GetProperty("comments");
        Assert.NotNull(actualComments.Comments);
        Assert.Equal(expectedCommentArray.GetArrayLength(), actualComments.Comments!.Length);
        for (int i = 0; i < actualComments.Comments.Length; i++)
        {
            JsonElement ec = expectedCommentArray[i];
            GalleryComment ac = actualComments.Comments[i];
            Assert.Equal(ec.GetProperty("id").GetInt64(), ac.Id);
            Assert.Equal(ec.GetProperty("score").GetInt32(), ac.Score);
            Assert.Equal(ec.GetProperty("editable").GetBoolean(), ac.Editable);
            Assert.Equal(ec.GetProperty("voteUpAble").GetBoolean(), ac.VoteUpAble);
            Assert.Equal(ec.GetProperty("voteUpEd").GetBoolean(), ac.VoteUpEd);
            Assert.Equal(ec.GetProperty("voteDownAble").GetBoolean(), ac.VoteDownAble);
            Assert.Equal(ec.GetProperty("voteDownEd").GetBoolean(), ac.VoteDownEd);
            Assert.Equal(ec.GetProperty("voteState").GetString(), ac.VoteState);
            Assert.Equal(ec.GetProperty("time").GetInt64(), ac.Time);
            Assert.Equal(ec.GetProperty("user").GetString(), ac.User);
            Assert.Equal(
                NormalizeHtmlWhitespace(ec.GetProperty("comment").GetString()),
                NormalizeHtmlWhitespace(ac.Comment));
            Assert.Equal(ec.GetProperty("lastEdited").GetInt64(), ac.LastEdited);
        }

        // parsePreviewSet
        PreviewSet actualPreviewSet = GalleryDetailParser.ParsePreviewSet(document, html);
        JsonElement expectedPreviewRoot = root.GetProperty("parsePreviewSet").GetProperty("value");
        Assert.Equal(expectedPreviewRoot.GetProperty("type").GetString(), actualPreviewSet.GetType().Name);
        Assert.Equal(expectedPreviewRoot.GetProperty("size").GetInt32(), actualPreviewSet.Size);
        JsonElement expectedPreviewItems = expectedPreviewRoot.GetProperty("items");
        for (int i = 0; i < actualPreviewSet.Size; i++)
        {
            GalleryPreview gp = actualPreviewSet.GetGalleryPreview(0L, i);
            Assert.Equal(expectedPreviewItems[i].GetProperty("position").GetInt32(), actualPreviewSet.GetPosition(i));
            Assert.Equal(expectedPreviewItems[i].GetProperty("pageUrl").GetString(), actualPreviewSet.GetPageUrlAt(i));
            Assert.Equal(expectedPreviewItems[i].GetProperty("previewPosition").GetInt32(), gp.Position);
        }

        // parsePreviewPages
        JsonElement expectedPreviewPages = root.GetProperty("parsePreviewPages");
        if (expectedPreviewPages.GetProperty("ok").GetBoolean())
        {
            Assert.Equal(
                expectedPreviewPages.GetProperty("value").GetInt32(),
                GalleryDetailParser.ParsePreviewPages(document, html));
        }
        else
        {
            ParseException previewEx = Assert.Throws<ParseException>(() =>
                GalleryDetailParser.ParsePreviewPages(document, html));
            Assert.Equal(expectedPreviewPages.GetProperty("errorMessage").GetString(), previewEx.Message);
        }

        // parsePages
        JsonElement expectedPages = root.GetProperty("parsePages");
        Assert.False(expectedPages.GetProperty("ok").GetBoolean());
        ParseException pagesEx = Assert.Throws<ParseException>(() => GalleryDetailParser.ParsePages(html));
        Assert.Equal(expectedPages.GetProperty("errorMessage").GetString(), pagesEx.Message);
    }

    [Fact]
    public void GalleryDetailParser_ParsesCompactFullDetailNewVersionsAndOptions()
    {
        string compactNewPage = CompactHtmlBetweenTags(ReadInput("newPage.html"));
        GalleryDetail detail = GalleryDetailParser.Parse(
            compactNewPage,
            new GalleryDetailParserOptions(
                ThumbResolution: 1,
                IsBlacklistedUser: static user => string.Equals(user, "AdamAbbott", StringComparison.Ordinal)));

        Assert.Equal(2217617L, detail.Gid);
        Assert.Equal("36d9cc5391", detail.Token);
        Assert.Equal(394, detail.Pages);
        Assert.Equal(394, detail.SpiderInfoPages);
        Assert.Equal(10, detail.PreviewPages);
        Assert.Equal(10, detail.SpiderInfoPreviewPages);
        Assert.Equal(EhConfig.Misc, detail.Category);
        Assert.Equal("AdamAbbott", detail.Uploader);
        Assert.Equal("[Twitter] AkiyamaRyo (2019.11.09-2022.5.10)", detail.Title);
        Assert.Contains("_250.jpg", detail.Thumb, StringComparison.Ordinal);
        Assert.NotNull(detail.NewVersions);
        Assert.NotEmpty(detail.NewVersions!);
        Assert.StartsWith("https://e-hentai.org/g/", detail.NewVersions![0].VersionUrl, StringComparison.Ordinal);
        Assert.Contains(", added ", detail.NewVersions[0].VersionName, StringComparison.Ordinal);

        Assert.NotNull(detail.Comments);
        Assert.True(detail.Comments!.HasMore);
        Assert.DoesNotContain(detail.Comments.Comments!, static c => c.User == "AdamAbbott");
        Assert.NotEmpty(detail.Tags!);

        string largePreviewHtml = """
            <html><body>
            <div id="gdt" class="gt200">
              <a href="https://exhentai.org/s/a1b2c3d4e5/123-1"><div title="Page 1: 001.jpg" style="background:transparent url(https://s.exhentai.org/t/ab/cd/abcdef01-123-jpg_l.jpg) 0 0 no-repeat"></div></a>
            </div>
            </body></html>
            """;
        PreviewSet largeFixed = GalleryDetailParser.ParsePreviewSet(
            largePreviewHtml,
            new GalleryDetailParserOptions(FixThumbUrl: true));
        LargePreviewSet large = Assert.IsType<LargePreviewSet>(largeFixed);
        Assert.Equal(1, large.Size);
        Assert.Equal("https://ehgt.org/ab/cd/abcdef01-123-jpg_l.jpg", large.GetImageUrlAt(0));
        Assert.Equal("https://exhentai.org/s/a1b2c3d4e5/123-1", large.GetPageUrlAt(0));
    }

    [Fact]
    public void GalleryDetailParser_ThrowsSiteAndWarningExceptions()
    {
        Assert.Throws<OffensiveException>(() =>
            GalleryDetailParser.Parse("<p>(And if you choose to ignore this warning, you lose all rights to complain about it in the future.)</p>"));
        Assert.Throws<PiningException>(() =>
            GalleryDetailParser.Parse("<p>This gallery is pining for the fjords.</p>"));
        Assert.Throws<GalleryUnavailableException>(() =>
            GalleryDetailParser.Parse("This gallery is unavailable"));
        EhException ex = Assert.Throws<EhException>(() =>
            GalleryDetailParser.Parse("<div class=\"d\">\n<p>IP address blocked</p></div>"));
        Assert.Equal("IP address blocked", ex.Message);
    }

    private static string NormalizeHtmlWhitespace(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        // Collapse formatting newlines/indentation and normalize <br> forms between raw and Jsoup HTML outputs.
        string unified = html
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("<br/>", "<br>", StringComparison.OrdinalIgnoreCase);
        unified = WhitespaceBetweenTagsOrWordsRegex().Replace(unified, " ");
        unified = BrBreakSpacingRegex().Replace(unified, "<br>");
        return unified.Trim();
    }

    private static string CompactHtmlBetweenTags(string html) =>
        InterTagNewlineRegex().Replace(html, "><");

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceBetweenTagsOrWordsRegex();

    [GeneratedRegex(@"<br>\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BrBreakSpacingRegex();

    [GeneratedRegex(@">\s+<", RegexOptions.CultureInvariant)]
    private static partial Regex InterTagNewlineRegex();
}

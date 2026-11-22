using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Data.TopList;
using EhViewer.Core.Client.Data.UserTag;
using EhViewer.Core.Client.Exceptions;
using EhViewer.Core.Client.Parser;
using Xunit;

namespace EhViewer.Core.Tests.ParserTests;

public partial class MediumParserGoldenTests
{
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

    internal static void AssertGalleryInfoMatches(JsonElement expected, GalleryInfo actual)
    {
        Assert.Equal(expected.GetProperty("gid").GetInt64(), actual.Gid);
        Assert.Equal(expected.GetProperty("token").GetString(), actual.Token);
        Assert.Equal(expected.GetProperty("title").GetString(), actual.Title);
        Assert.Equal(expected.GetProperty("titleJpn").GetString(), actual.TitleJpn);
        Assert.Equal(expected.GetProperty("thumb").GetString(), actual.Thumb);
        Assert.Equal(expected.GetProperty("category").GetInt32(), actual.Category);
        Assert.Equal(expected.GetProperty("posted").GetString(), actual.Posted);
        Assert.Equal(expected.GetProperty("uploader").GetString(), actual.Uploader);
        Assert.Equal(expected.GetProperty("rating").GetSingle(), actual.Rating, 3);
        Assert.Equal(expected.GetProperty("rated").GetBoolean(), actual.Rated);
        Assert.Equal(expected.GetProperty("simpleLanguage").GetString(), actual.SimpleLanguage);

        JsonElement expectedTags = expected.GetProperty("simpleTags");
        if (expectedTags.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(actual.SimpleTags);
        }
        else
        {
            Assert.NotNull(actual.SimpleTags);
            Assert.Equal(
                expectedTags.EnumerateArray().Select(x => x.GetString()!).ToArray(),
                actual.SimpleTags);
        }

        Assert.Equal(expected.GetProperty("thumbWidth").GetInt32(), actual.ThumbWidth);
        Assert.Equal(expected.GetProperty("thumbHeight").GetInt32(), actual.ThumbHeight);
        Assert.Equal(expected.GetProperty("favoriteSlot").GetInt32(), actual.FavoriteSlot);
        Assert.Equal(expected.GetProperty("favoriteName").GetString(), actual.FavoriteName);
        Assert.Equal(expected.GetProperty("pages").GetInt32(), actual.Pages);
    }

    [Fact]
    public void ArchiveParser_MatchesGoldenFixtureAndExtractsFullArchiverOptions()
    {
        string html = ReadInput("ArchiveDownloadDialog.html");
        using JsonDocument expectedDoc = ReadExpected("ArchiveDownloadDialog.html.expected.json");

        ArchiveParser.ParseResult parsed = ArchiveParser.Parse(html);
        JsonElement expectedParseE = expectedDoc.RootElement.GetProperty("parseSiteE").GetProperty("value");
        Assert.Equal(expectedParseE.GetProperty("paramOr").GetString(), parsed.ParamOr);
        ArchiveParser.ArchiveItem[] expectedItems = expectedParseE
            .GetProperty("items")
            .EnumerateArray()
            .Select(x => new ArchiveParser.ArchiveItem(
                x.GetProperty("res").GetString()!,
                x.GetProperty("name").GetString()!))
            .ToArray();
        Assert.Equal(expectedItems, parsed.Items);

        ArchiverData archiver = ArchiveParser.ParseArchiver(html, EhSite.E);
        JsonElement expectedArchiver = expectedDoc.RootElement.GetProperty("parseArchiver").GetProperty("value");
        Assert.Equal(expectedArchiver.GetProperty("funds").GetString(), archiver.Funds);
        Assert.Equal(expectedArchiver.GetProperty("originalCost").GetString(), archiver.OriginalCost);
        Assert.Equal(expectedArchiver.GetProperty("originalSize").GetString(), archiver.OriginalSize);
        Assert.Equal(expectedArchiver.GetProperty("originalUrl").GetString(), archiver.OriginalUrl);
        Assert.Equal(expectedArchiver.GetProperty("resampleCost").GetString(), archiver.ResampleCost);
        Assert.Equal(expectedArchiver.GetProperty("resampleSize").GetString(), archiver.ResampleSize);
        Assert.Equal(expectedArchiver.GetProperty("resampleUrl").GetString(), archiver.ResampleUrl);

        const string validExArchiverHtml = """
            <html><body><div id="db">
              <p>Current Funds: 100 GP</p>
              <div>
                <div>
                  <div><strong>Free!</strong></div>
                  <form action="https://exhentai.org/archiver.php?or=1" method="post"></form>
                  <div><strong>18.46 MiB</strong></div>
                </div>
                <div>
                  <div><strong>12 GP</strong></div>
                  <form action="https://exhentai.org/archiver.php?or=2" method="post"></form>
                  <div><strong>2.17 MiB</strong></div>
                </div>
              </div>
            </div></body></html>
            """;
        ArchiverData exData = ArchiveParser.ParseArchiver(validExArchiverHtml, EhSite.Ex);
        Assert.Equal("Current Funds: 100 GP", exData.Funds);
        Assert.Equal("Free!", exData.OriginalCost);
        Assert.Equal("18.46 MiB", exData.OriginalSize);
        Assert.Equal("https://exhentai.org/archiver.php?or=1", exData.OriginalUrl);
        Assert.Equal("12 GP", exData.ResampleCost);
        Assert.Equal("2.17 MiB", exData.ResampleSize);
        Assert.Equal("https://exhentai.org/archiver.php?or=2", exData.ResampleUrl);

        Assert.Equal(
            "https://foo.bar/dl.zip",
            ArchiveParser.ParseArchiverDownloadUrl(@"<a href=""https://foo.bar/dl.zip"">Click Here To Start Downloading</a>"));
    }

    [Fact]
    public void EhEventParser_MatchesGoldenFixture()
    {
        string html = ReadInput("EhNews.html");
        using JsonDocument expectedDoc = ReadExpected("EhNews.html.expected.json");

        string? actual = EhEventParser.Parse(html);
        Assert.NotNull(actual);
        string expected = expectedDoc.RootElement.GetProperty("value").GetProperty("eventPane").GetString()!;

        Assert.Equal(NormalizeHtmlWhitespace(expected), NormalizeHtmlWhitespace(actual));
    }

    [Theory]
    [InlineData("Home.html", "Home.html.expected.json")]
    [InlineData("HomePageNew.html", "HomePageNew.html.expected.json")]
    [InlineData("MyHomePage.html", "MyHomePage.html.expected.json")]
    public void EhHomeParser_MatchesGoldenFixtures(string inputFile, string expectedFile)
    {
        string html = ReadInput(inputFile);
        using JsonDocument expectedDoc = ReadExpected(expectedFile);
        JsonElement v = expectedDoc.RootElement.GetProperty("value");

        HomeDetail detail = EhHomeParser.Parse(html);
        Assert.Equal(v.GetProperty("used").GetInt64(), detail.Used);
        Assert.Equal(v.GetProperty("total").GetInt64(), detail.Total);
        Assert.Equal(v.GetProperty("resetCost").GetInt64(), detail.ResetCostValue);
        Assert.Equal(v.GetProperty("resetCostText").GetString(), detail.ResetCost);
        Assert.Equal(v.GetProperty("fromGalleryVisits").GetString(), detail.FromGalleryVisits);
        Assert.Equal(v.GetProperty("fromTorrentCompletions").GetString(), detail.FromTorrentCompletions);
        Assert.Equal(v.GetProperty("fromArchiveDownloads").GetString(), detail.FromArchiveDownloads);
        Assert.Equal(v.GetProperty("fromHentaiAtHome").GetString(), detail.FromHentaiAtHome);
        Assert.Equal(v.GetProperty("currentModerationPower").GetString(), detail.CurrentModerationPower);
        Assert.Equal(v.GetProperty("imageLimits").GetString(), detail.GetImageLimits("res_2"));
    }

    [Fact]
    public void EhHomeParser_ParsesLimitWithoutResetCostLine()
    {
        const string html = """
            <html><body>
              <div class="homebox">
                <p>You are currently at <strong>1,234</strong> towards your account limit of <strong>5,000</strong>.</p>
                <p>If you want to increase your limit, please read the documentation.</p>
              </div>
            </body></html>
            """;

        HomeDetail detail = EhHomeParser.Parse(html);
        Assert.Equal(1234L, detail.Used);
        Assert.Equal(5000L, detail.Total);
        Assert.Equal(0L, detail.ResetCostValue);
    }

    [Fact]
    public void FavoritesParser_MatchesGoldenFixture()
    {
        string html = ReadInput("FavoritesListParser.html");
        using JsonDocument expectedDoc = ReadExpected("FavoritesListParser.html.expected.json");
        JsonElement v = expectedDoc.RootElement.GetProperty("value");

        FavoritesParser.Result result = FavoritesParser.Parse(html);
        Assert.Equal(v.GetProperty("pages").GetInt32(), result.Pages);
        Assert.Equal(v.GetProperty("nextPage").GetInt32(), result.NextPage);
        Assert.Equal(v.GetProperty("firstHref").GetString(), result.FirstHref);
        Assert.Equal(v.GetProperty("prevHref").GetString(), result.PrevHref);
        Assert.Equal(v.GetProperty("nextHref").GetString(), result.NextHref);
        Assert.Equal(v.GetProperty("lastHref").GetString(), result.LastHref);
        Assert.Equal(
            v.GetProperty("catArray").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            result.CatArray);
        Assert.Equal(
            v.GetProperty("countArray").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
            result.CountArray);

        JsonElement[] expectedList = v.GetProperty("galleryInfoList").EnumerateArray().ToArray();
        Assert.Equal(expectedList.Length, result.GalleryInfoList.Count);
        for (int i = 0; i < expectedList.Length; i++)
        {
            AssertGalleryInfoMatches(expectedList[i], result.GalleryInfoList[i]);
        }
    }

    [Fact]
    public void ForumsAndProfileParser_MatchesGoldenFixtureAndPositiveCases()
    {
        string html = ReadInput("LoginProfile.html");
        using JsonDocument expectedDoc = ReadExpected("LoginProfile.html.expected.json");

        var forumsEx = Assert.Throws<ParseException>(() => ForumsParser.Parse(html));
        Assert.Equal(expectedDoc.RootElement.GetProperty("forumsParser").GetProperty("errorMessage").GetString(), forumsEx.Message);

        var profileEx = Assert.Throws<ParseException>(() => ProfileParser.Parse(html));
        Assert.Equal(expectedDoc.RootElement.GetProperty("profileParser").GetProperty("errorMessage").GetString(), profileEx.Message);

        // Positive ForumsParser & ProfileParser cases
        const string validForums = """<div id="userlinks"><p><b><a href="https://forums.e-hentai.org/index.php?showuser=123">user</a></b></p></div>""";
        Assert.Equal("https://forums.e-hentai.org/index.php?showuser=123", ForumsParser.Parse(validForums));

        const string validProfile = """
            <div>
              <div id="profilename"><div>TestProfile</div></div>
              <div></div>
              <div><img src="uploads/av-1.png" /></div>
            </div>
            """;
        ProfileParser.Result profile = ProfileParser.Parse(validProfile);
        Assert.Equal("TestProfile", profile.DisplayName);
        Assert.Equal("https://forums.e-hentai.org/uploads/av-1.png", profile.Avatar);

        ProfileParser.Result parsedNew = ProfileParser.ParseNew(
            """<div id=\"userlinks\"><p><b>Logged in as: <a href=\"https://forums.e-hentai.org/index.php?showuser=6254716\">brokendog</a></b></p></div>""");
        Assert.Equal("brokendog", parsedNew.DisplayName);
    }

    [Fact]
    public void MyTagListParser_MatchesGoldenFixtureAndHandlesEmptyWeight()
    {
        string html = ReadInput("MyTagList.html");
        using JsonDocument expectedDoc = ReadExpected("MyTagList.html.expected.json");
        JsonElement v = expectedDoc.RootElement.GetProperty("value");

        UserTagList result = MyTagListParser.Parse(html);
        Assert.Equal(v.GetProperty("stageId").GetInt32(), result.StageId);

        JsonElement[] expectedTags = v.GetProperty("userTags").EnumerateArray().ToArray();
        Assert.Equal(expectedTags.Length, result.UserTags.Count);
        for (int i = 0; i < expectedTags.Length; i++)
        {
            JsonElement e = expectedTags[i];
            UserTag a = result.UserTags[i];
            Assert.Equal(e.GetProperty("userTagId").GetString(), a.UserTagId);
            Assert.Equal(e.GetProperty("tagName").GetString(), a.TagName);
            Assert.Equal(e.GetProperty("watched").GetBoolean(), a.Watched);
            Assert.Equal(e.GetProperty("hidden").GetBoolean(), a.Hidden);
            Assert.Equal(e.GetProperty("color").GetString(), a.Color);
            Assert.Equal(e.GetProperty("tagWeight").GetInt32(), a.TagWeight);
        }

        // Regression test: empty tagweight value must default to 0 instead of aborting later rows.
        const string emptyWeightHtml = """
            <div id="usertags_outer">
              <div id="usertag_0"></div>
              <div id="usertag_11">
                <div id="tagpreview_11" title="artist:foo">foo</div>
                <input id="tagwatch_11" type="checkbox" checked="checked" />
                <input id="taghide_11" type="checkbox" />
                <input id="tagcolor_11" placeholder="#112233" />
                <input id="tagweight_11" value="" />
              </div>
              <div id="usertag_22">
                <div id="tagpreview_22" title="female:bar">bar</div>
                <input id="tagweight_22" value="15" />
              </div>
            </div>
            """;
        UserTagList recovered = MyTagListParser.Parse(emptyWeightHtml);
        Assert.Equal(2, recovered.Count);
        Assert.Equal(0, recovered[0].TagWeight);
        Assert.Equal(15, recovered[1].TagWeight);
    }

    [Fact]
    public void TorrentParser_MatchesGoldenFixtureAndPositiveVector()
    {
        string html = ReadInput("torrentList.html");
        using JsonDocument expectedDoc = ReadExpected("torrentList.html.expected.json");

        TorrentInfo[] fixtureResult = TorrentParser.Parse(html);
        Assert.Equal(expectedDoc.RootElement.GetProperty("value").GetArrayLength(), fixtureResult.Length);

        // Positive vector matching upstream TorrentParserTest assertions on inline <a href=...> HTML
        const string positiveHtml = """
            <form method="post" action="https://e-hentai.org/gallerytorrents.php?gid=3906770&amp;t=4d49b5c7bf">
              <table>
                <tr><td><span>Posted:</span> <span>2026-04-26 05:14</span></td></tr>
                <tr><td colspan="5"> &nbsp; <a href="https://ehtracker.org/get/3905209/96d5199ddac01181000340a59bb84c218faf0b16.torrent?p=secret" onclick="return false">[Uniyaa] Test (Part 2).zip</a></td></tr>
              </table>
            </form>
            <form method="post" action="https://e-hentai.org/gallerytorrents.php?gid=3906770&amp;t=4d49b5c7bf">
              <table>
                <tr><td><span>Posted:</span> <span>2026-04-26 05:14</span></td></tr>
                <tr><td colspan="5"> &nbsp; <a href="https://ehtracker.org/get/3905209/713846c3ddf05826d4443790a5b6619eae734d71.torrent" onclick="return false">[Uniyaa] Test-1280x.zip</a></td></tr>
              </table>
            </form>
            """;
        TorrentInfo[] positive = TorrentParser.Parse(positiveHtml);
        Assert.Equal(2, positive.Length);
        Assert.Equal("2026-04-26 05:14", positive[0].Posted);
        Assert.Equal("2026-04-26 05:14", positive[1].Posted);
        Assert.Equal("https://ehtracker.org/get/3905209/96d5199ddac01181000340a59bb84c218faf0b16.torrent", positive[0].Url);
        Assert.Equal("https://ehtracker.org/get/3905209/713846c3ddf05826d4443790a5b6619eae734d71.torrent", positive[1].Url);
        Assert.Contains("Part 2", positive[0].Name);
        Assert.Contains("1280x", positive[1].Name);
    }

    [Fact]
    public void TopListParser_MatchesGoldenFixtureAndDynamicItemCountFix()
    {
        string html = ReadInput("GalleryTopList.html");
        using JsonDocument expectedDoc = ReadExpected("GalleryTopList.html.expected.json");
        JsonElement v = expectedDoc.RootElement.GetProperty("value");

        EhTopListDetail detail = TopListParser.Parse(html);
        Assert.Equal(v.GetProperty("title").GetString(), detail.Title);

        AssertTopListInfo(v.GetProperty("galleryTopListInfo"), detail.GalleryTopListInfo);
        AssertTopListInfo(v.GetProperty("uploaderTopListInfo"), detail.UploaderTopListInfo);
        AssertTopListInfo(v.GetProperty("taggingTopListInfo"), detail.TaggingTopListInfo);
        AssertTopListInfo(v.GetProperty("hentaiHomeTopListInfo"), detail.HentaiHomeTopListInfo);
        AssertTopListInfo(v.GetProperty("ehTrackerTopListInfo"), detail.EhTrackerTopListInfo);
        AssertTopListInfo(v.GetProperty("cleanUpTopListInfo"), detail.CleanUpTopListInfo);
        AssertTopListInfo(v.GetProperty("ratingAndReviewingTopListInfo"), detail.RatingAndReviewingTopListInfo);

        // Regression test: >10 items in a .tun list must not crash with IndexOutOfRangeException, and <10 leaves no trailing nulls.
        static string BuildRows(int count) =>
            string.Concat(Enumerable.Range(0, count).Select(i => $@"<tr><td><div class=""tun""><a href=""https://e-hentai.org/g/{i}/0123456789/"">Item {i}</a></div></td></tr>"));
        string dynamicSection(string inner) =>
            $@"<div><div>head</div><div><table><tbody>{inner}</tbody></table></div></div>";
        string dynamicHtml = $"""
            <div class="ido">
              <div>EHG Toplists</div>
              <div>
                <div>header</div>
                {dynamicSection(BuildRows(11))}
                {dynamicSection(BuildRows(3))}
                {dynamicSection("")}
                {dynamicSection(BuildRows(1))}
              </div>
              <div>sep1</div><div></div><div>sep2</div><div></div>
              <div>sep3</div><div></div><div>sep4</div><div></div>
              <div>sep5</div><div></div><div>sep6</div><div></div>
            </div>
            """;
        EhTopListDetail dynamicDetail = TopListParser.Parse(dynamicHtml);
        Assert.Equal(11, dynamicDetail.GalleryTopListInfo!.AllTimeTopList!.Length);
        Assert.Equal("Item 10", dynamicDetail.GalleryTopListInfo.AllTimeTopList.Get(10).Value);
        Assert.Equal(3, dynamicDetail.GalleryTopListInfo.PastYearTopList!.Length);
        Assert.Empty(dynamicDetail.GalleryTopListInfo.PastMonthTopList!.ItemArray);
    }

    [Fact]
    public void GalleryPageParser_MatchesGoldenFixture()
    {
        string html = ReadInput("GalleryPageParserTest.html");
        using JsonDocument expectedDoc = ReadExpected("GalleryPageParserTest.html.expected.json");
        JsonElement v = expectedDoc.RootElement.GetProperty("value");

        GalleryPageParser.Result result = GalleryPageParser.Parse(html);
        Assert.Equal(v.GetProperty("imageUrl").GetString(), result.ImageUrl);
        Assert.Equal(v.GetProperty("skipHathKey").GetString(), result.SkipHathKey);
        Assert.Equal(v.GetProperty("originImageUrl").GetString(), result.OriginImageUrl);
        Assert.Equal(v.GetProperty("showKey").GetString(), result.ShowKey);
    }

    [Fact]
    public void GalleryPageApiParser_MatchesGoldenFixtureAndErrorCase()
    {
        string json = ReadInput("GalleryPageApiParserTest.json");
        using JsonDocument expectedDoc = ReadExpected("GalleryPageApiParserTest.json.expected.json");
        JsonElement v = expectedDoc.RootElement.GetProperty("value");

        GalleryPageApiParser.Result result = GalleryPageApiParser.Parse(json);
        Assert.Equal(v.GetProperty("imageUrl").GetString(), result.ImageUrl);
        Assert.Equal(v.GetProperty("skipHathKey").GetString(), result.SkipHathKey);
        Assert.Equal(v.GetProperty("originImageUrl").GetString(), result.OriginImageUrl);
        Assert.NotNull(result.OtherImageUrl);

        var err = Assert.Throws<ParseException>(() => GalleryPageApiParser.Parse("""{"error":"Key error"}"""));
        Assert.Equal("Key error", err.Message);
    }

    private static void AssertTopListInfo(JsonElement expected, TopListInfo? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.GetProperty("title").GetString(), actual.Title);
        Assert.Equal(expected.GetProperty("type").GetString(), ToUpstreamEnumName(actual.Type));

        AssertTopListArray(expected.GetProperty("allTimeTopList"), actual.AllTimeTopList);
        AssertTopListArray(expected.GetProperty("pastYearTopList"), actual.PastYearTopList);
        AssertTopListArray(expected.GetProperty("pastMonthTopList"), actual.PastMonthTopList);
        AssertTopListArray(expected.GetProperty("yesterdayTopList"), actual.YesterdayTopList);
    }

    private static void AssertTopListArray(JsonElement expectedArray, TopListItemArray? actual)
    {
        Assert.NotNull(actual);
        JsonElement[] expectedItems = expectedArray.EnumerateArray().ToArray();
        Assert.Equal(expectedItems.Length, actual.Length);
        for (int i = 0; i < expectedItems.Length; i++)
        {
            JsonElement e = expectedItems[i];
            TopListItem a = actual.Get(i);
            Assert.Equal(e.GetProperty("href").GetString(), a.Href);
            Assert.Equal(e.GetProperty("gid").GetString(), a.Gid);
            Assert.Equal(e.GetProperty("token").GetString(), a.Token);
            Assert.Equal(e.GetProperty("tag").GetString(), a.Tag);
            Assert.Equal(e.GetProperty("value").GetString(), a.Value);
        }
    }

    private static string ToUpstreamEnumName(TopListType type) => type switch
    {
        TopListType.Gallery => "GALLERY",
        TopListType.Uploader => "UPLOADER",
        TopListType.Tagging => "TAGGING",
        TopListType.HentaiHome => "HENTAI_HOME",
        TopListType.EhTracker => "EH_TRACKER",
        TopListType.Cleanup => "CLEANUP",
        TopListType.RatingAndReviewing => "RATING_AND_REVIEWING",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static string NormalizeHtmlWhitespace(string html) =>
        CollapseWhitespaceRegex().Replace(html, " ").Replace("> <", "><", StringComparison.Ordinal).Trim();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex CollapseWhitespaceRegex();
}

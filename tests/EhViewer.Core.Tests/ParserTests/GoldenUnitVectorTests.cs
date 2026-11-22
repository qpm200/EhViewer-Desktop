using System.Text;
using System.Text.Json;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Exceptions;
using EhViewer.Core.Client.Parser;
using Xunit;

namespace EhViewer.Core.Tests.ParserTests;

public class GoldenUnitVectorTests
{
    private static JsonDocument LoadUnitVectors()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "expected", "unit_vectors.expected.json");
        return JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    [Fact]
    public void GalleryDetailUrlParser_MatchesGoldenVectors()
    {
        using JsonDocument doc = LoadUnitVectors();
        JsonElement cases = doc.RootElement.GetProperty("galleryDetailUrlParser");

        int count = 0;
        foreach (JsonElement item in cases.EnumerateArray())
        {
            string url = item.GetProperty("url").GetString()!;
            bool strict = item.GetProperty("strict").GetBoolean();
            JsonElement expected = item.GetProperty("result");

            GalleryDetailUrlParser.Result? actual = GalleryDetailUrlParser.Parse(url, strict);
            if (expected.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(actual);
            }
            else
            {
                Assert.NotNull(actual);
                Assert.Equal(expected.GetProperty("gid").GetInt64(), actual.Gid);
                Assert.Equal(expected.GetProperty("token").GetString(), actual.Token);
            }

            count++;
        }

        Assert.True(count > 0);
    }

    [Fact]
    public void GalleryPageUrlParser_MatchesGoldenVectors()
    {
        using JsonDocument doc = LoadUnitVectors();
        JsonElement cases = doc.RootElement.GetProperty("galleryPageUrlParser");

        int count = 0;
        foreach (JsonElement item in cases.EnumerateArray())
        {
            string url = item.GetProperty("url").GetString()!;
            bool strict = item.GetProperty("strict").GetBoolean();
            JsonElement expected = item.GetProperty("result");

            GalleryPageUrlParser.Result? actual = GalleryPageUrlParser.Parse(url, strict);
            if (expected.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(actual);
            }
            else
            {
                Assert.NotNull(actual);
                Assert.Equal(expected.GetProperty("gid").GetInt64(), actual.Gid);
                Assert.Equal(expected.GetProperty("pToken").GetString(), actual.PToken);
                Assert.Equal(expected.GetProperty("page").GetInt32(), actual.Page);
            }

            count++;
        }

        Assert.True(count > 0);
    }

    [Fact]
    public void GalleryListUrlParser_MatchesGoldenVectors()
    {
        using JsonDocument doc = LoadUnitVectors();
        JsonElement cases = doc.RootElement.GetProperty("galleryListUrlParser");

        int count = 0;
        foreach (JsonElement item in cases.EnumerateArray())
        {
            string url = item.GetProperty("url").GetString()!;
            JsonElement expected = item.GetProperty("result");

            ListUrlBuilder? actual = GalleryListUrlParser.Parse(url);
            if (expected.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(actual);
            }
            else
            {
                Assert.NotNull(actual);
                Assert.Equal(expected.GetProperty("mode").GetInt32(), actual.Mode);
                Assert.Equal(expected.GetProperty("pageIndex").GetInt32(), actual.PageIndex);
                Assert.Equal(expected.GetProperty("category").GetInt32(), actual.Category);
                Assert.Equal(expected.GetProperty("keyword").GetString(), actual.Keyword);
                Assert.Equal(expected.GetProperty("advanceSearch").GetInt32(), actual.AdvanceSearch);
                Assert.Equal(expected.GetProperty("minRating").GetInt32(), actual.MinRating);
                Assert.Equal(expected.GetProperty("pageFrom").GetInt32(), actual.PageFrom);
                Assert.Equal(expected.GetProperty("pageTo").GetInt32(), actual.PageTo);
                Assert.Equal(expected.GetProperty("imagePath").GetString(), actual.ImagePath);
                Assert.Equal(expected.GetProperty("useSimilarityScan").GetBoolean(), actual.UseSimilarityScan);
                Assert.Equal(expected.GetProperty("onlySearchCovers").GetBoolean(), actual.OnlySearchCovers);
                Assert.Equal(expected.GetProperty("showExpunged").GetBoolean(), actual.ShowExpunged);
            }

            count++;
        }

        Assert.True(count > 0);
    }

    [Fact]
    public void GalleryListUrlParser_ParsesNumericCategoryPathAndAdvanceQuery()
    {
        ListUrlBuilder? builder = GalleryListUrlParser.Parse(
            "https://e-hentai.org/6?advsearch=1&f_sname=on&f_stags=on&f_sr=on&f_srdd=4&f_sp=on&f_spf=5&f_spt=20");

        Assert.NotNull(builder);
        Assert.Equal(6, builder.Category);
        Assert.Equal((int)(AdvanceSearchFlags.SName | AdvanceSearchFlags.STags), builder.AdvanceSearch);
        Assert.Equal(4, builder.MinRating);
        Assert.Equal(5, builder.PageFrom);
        Assert.Equal(20, builder.PageTo);
    }

    [Fact]
    public void GalleryApiParser_MatchesGoldenVectors()
    {
        using JsonDocument doc = LoadUnitVectors();
        JsonElement expected = doc.RootElement.GetProperty("galleryApiParser");
        Assert.True(expected.GetProperty("ok").GetBoolean());
        JsonElement v = expected.GetProperty("value");

        const string apiJson = """
            {
              "gmetadata": [
                {
                  "gid": 1366222,
                  "token": "0123456789",
                  "archiver_key": "abc--123",
                  "title": "Sample &amp; Title",
                  "title_jpn": "サンプル &lt;タイトル&gt;",
                  "category": "Doujinshi",
                  "thumb": "https://ehgt.org/t/00/01/test_l.jpg",
                  "uploader": "tester",
                  "posted": "1700000000",
                  "filecount": "25",
                  "filesize": 12345678,
                  "expunged": false,
                  "rating": "4.56",
                  "torrentcount": "2",
                  "tags": ["language:japanese", "artist:foo"]
                },
                {
                  "gid": 9999999,
                  "error": "Key missing, or incorrect key provided."
                }
              ]
            }
            """;

        var giTarget = new GalleryInfo { Gid = 1366222 };
        var skippedTarget = new GalleryInfo { Gid = 9999999, Title = "keep" };
        GalleryApiParser.Parse(apiJson, [giTarget, skippedTarget]);

        Assert.Equal(v.GetProperty("gid").GetInt64(), giTarget.Gid);
        Assert.Equal(v.GetProperty("token").GetString(), giTarget.Token);
        Assert.Equal(v.GetProperty("title").GetString(), giTarget.Title);
        Assert.Equal(v.GetProperty("titleJpn").GetString(), giTarget.TitleJpn);
        Assert.Equal(v.GetProperty("thumb").GetString(), giTarget.Thumb);
        Assert.Equal(v.GetProperty("category").GetInt32(), giTarget.Category);
        Assert.Equal(v.GetProperty("posted").GetString(), giTarget.Posted);
        Assert.Equal(v.GetProperty("uploader").GetString(), giTarget.Uploader);
        Assert.Equal(v.GetProperty("rating").GetSingle(), giTarget.Rating, 3);
        Assert.Equal(v.GetProperty("simpleLanguage").GetString(), giTarget.SimpleLanguage);
        Assert.Equal(
            v.GetProperty("simpleTags").EnumerateArray().Select(e => e.GetString()!).ToArray(),
            giTarget.SimpleTags);
        Assert.Equal(v.GetProperty("favoriteSlot").GetInt32(), giTarget.FavoriteSlot);
        Assert.Equal(v.GetProperty("pages").GetInt32(), giTarget.Pages);
        Assert.Equal("keep", skippedTarget.Title);
    }

    [Fact]
    public void GalleryTokenApiParser_MatchesGoldenVectors()
    {
        using JsonDocument doc = LoadUnitVectors();
        JsonElement expected = doc.RootElement.GetProperty("galleryTokenApiParser");
        Assert.True(expected.GetProperty("ok").GetBoolean());

        const string tokenJson = """
            {
              "tokenlist": [
                { "gid": 1001, "token": "aaaa1111bb" },
                { "gid": 1002, "token": "cccc2222dd" }
              ]
            }
            """;

        Assert.Equal(expected.GetProperty("value").GetString(), GalleryTokenApiParser.Parse(tokenJson));

        const string errorJson = """
            {
              "tokenlist": [
                { "gid": 1001, "error": "Invalid gid" }
              ]
            }
            """;
        var ex = Assert.Throws<EhException>(() => GalleryTokenApiParser.Parse(errorJson));
        Assert.Equal("Invalid gid", ex.Message);
    }

    [Fact]
    public void RateGalleryParser_MatchesGoldenVectors()
    {
        using JsonDocument doc = LoadUnitVectors();
        JsonElement expected = doc.RootElement.GetProperty("rateGalleryParser");
        Assert.True(expected.GetProperty("ok").GetBoolean());
        JsonElement v = expected.GetProperty("value");

        const string rateJson = """
            {
              "rating_avg": 4.72,
              "rating_usr": 5,
              "rating_cnt": 128,
              "rating_cls": "irb"
            }
            """;

        RateGalleryParser.Result result = RateGalleryParser.Parse(rateJson);
        Assert.Equal(v.GetProperty("rating").GetSingle(), result.Rating, 3);
        Assert.Equal(v.GetProperty("ratingCount").GetInt32(), result.RatingCount);

        var ex = Assert.Throws<ParseException>(() => RateGalleryParser.Parse("{}"));
        Assert.Equal("Can't parse rate gallery", ex.Message);
    }

    [Fact]
    public void VoteCommentParser_MatchesGoldenVectors()
    {
        using JsonDocument doc = LoadUnitVectors();
        JsonElement expected = doc.RootElement.GetProperty("voteCommentParser");
        Assert.True(expected.GetProperty("ok").GetBoolean());
        JsonElement v = expected.GetProperty("value");

        const string voteJson = """
            {
              "comment_id": 555666,
              "comment_vote": 12,
              "comment_score": 42
            }
            """;

        VoteCommentParser.Result result = VoteCommentParser.Parse(voteJson, 1);
        Assert.Equal(v.GetProperty("id").GetInt64(), result.Id);
        Assert.Equal(v.GetProperty("score").GetInt32(), result.Score);
        Assert.Equal(v.GetProperty("vote").GetInt32(), result.Vote);
        Assert.Equal(v.GetProperty("expectVote").GetInt32(), result.ExpectVote);
    }

    [Fact]
    public void GetEditCommentParser_MatchesGoldenVectorsAndPositiveCases()
    {
        using JsonDocument doc = LoadUnitVectors();

        JsonElement missingIdExpected = doc.RootElement.GetProperty("getEditCommentParser");
        Assert.False(missingIdExpected.GetProperty("ok").GetBoolean());
        const string editCommentJson = """
            {
              "editable_comment": "<p>Hello &amp; Edited</p>"
            }
            """;
        var ex1 = Assert.Throws<ParseException>(() => GetEditCommentParser.Parse(editCommentJson));
        Assert.Equal(missingIdExpected.GetProperty("errorMessage").GetString(), ex1.Message);

        JsonElement emptyExpected = doc.RootElement.GetProperty("getEditCommentParserInvalid");
        Assert.False(emptyExpected.GetProperty("ok").GetBoolean());
        var ex2 = Assert.Throws<ParseException>(() => GetEditCommentParser.Parse("{}"));
        Assert.Equal(emptyExpected.GetProperty("errorMessage").GetString(), ex2.Message);

        const string validJson = """
            {
              "comment_id": 98765,
              "editable_comment": "<form><textarea name=\"commenttext_edit\">Line 1 &amp; Line 2\n&lt;tag&gt;</textarea></form>"
            }
            """;
        GetEditCommentParser.Result valid = GetEditCommentParser.Parse(validJson);
        Assert.Equal(98765L, valid.Id);
        Assert.Equal("Line 1 & Line 2\n<tag>", valid.Comment);

        var ehEx = Assert.Throws<EhException>(() => GetEditCommentParser.Parse("""{"error":"Cannot edit"}"""));
        Assert.Equal("Cannot edit", ehEx.Message);

        var noTextareaEx = Assert.Throws<ParseException>(() =>
            GetEditCommentParser.Parse("""{"comment_id":1,"editable_comment":"<div>empty</div>"}"""));
        Assert.Equal("Can't find editable comment", noTextareaEx.Message);
    }

    [Fact]
    public void SignInParser_MatchesGoldenVectors()
    {
        using JsonDocument doc = LoadUnitVectors();

        JsonElement okExpected = doc.RootElement.GetProperty("signInParserOk");
        Assert.True(okExpected.GetProperty("ok").GetBoolean());
        const string signInOkHtml = "<html><body><p>You are now logged in as: TestUser<br />Please wait</p></body></html>";
        Assert.Equal(okExpected.GetProperty("value").GetString(), SignInParser.Parse(signInOkHtml));

        JsonElement failExpected = doc.RootElement.GetProperty("signInParserFail");
        Assert.False(failExpected.GetProperty("ok").GetBoolean());
        const string signInFailHtml = "<html><body><span class=\"postcolor\">Username or password incorrect</span></body></html>";
        var failEx = Assert.Throws<EhException>(() => SignInParser.Parse(signInFailHtml));
        Assert.Equal(failExpected.GetProperty("errorMessage").GetString(), failEx.Message);

        const string h4FailHtml = "<html><body><h4>The error returned was:</h4><p>Too many attempts</p></body></html>";
        var h4Ex = Assert.Throws<EhException>(() => SignInParser.Parse(h4FailHtml));
        Assert.Equal("Too many attempts", h4Ex.Message);

        var parseEx = Assert.Throws<ParseException>(() => SignInParser.Parse("<html><body>unknown</body></html>"));
        Assert.Equal("Can't parse sign in", parseEx.Message);
    }

    [Fact]
    public void ComicInfoParser_MatchesGoldenVectorsAndStreamCap()
    {
        using JsonDocument doc = LoadUnitVectors();
        JsonElement expected = doc.RootElement.GetProperty("comicInfoParser");
        Assert.True(expected.GetProperty("ok").GetBoolean());
        JsonElement v = expected.GetProperty("value");

        const string comicInfoXml = "<ComicInfo><Web>https://exhentai.org/g/222333/deadbeef00/</Web></ComicInfo>";
        GalleryDetailUrlParser.Result? result = ComicInfoParser.Parse(comicInfoXml);
        Assert.NotNull(result);
        Assert.Equal(v.GetProperty("gid").GetInt64(), result.Gid);
        Assert.Equal(v.GetProperty("token").GetString(), result.Token);

        const int maxBytes = 256 * 1024;
        string header = "<ComicInfo><Web>https://e-hentai.org/g/222333/deadbeef00/</Web>";
        string footer = "</ComicInfo>";
        int padLen = maxBytes - Encoding.UTF8.GetByteCount(header) - Encoding.UTF8.GetByteCount(footer);
        byte[] exactLimitBytes = Encoding.UTF8.GetBytes(header + new string(' ', padLen) + footer);
        using var okStream = new MemoryStream(exactLimitBytes);
        GalleryDetailUrlParser.Result? fromExactStream = ComicInfoParser.Parse(okStream);
        Assert.NotNull(fromExactStream);
        Assert.Equal(222333L, fromExactStream.Gid);

        byte[] overLimitBytes = new byte[maxBytes + 1];
        Array.Copy(exactLimitBytes, overLimitBytes, exactLimitBytes.Length);
        overLimitBytes[^1] = (byte)' ';
        using var overStream = new MemoryStream(overLimitBytes);
        Assert.Null(ComicInfoParser.Parse(overStream));
    }
}

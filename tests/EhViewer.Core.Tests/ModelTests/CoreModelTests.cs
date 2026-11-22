using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Spider;
using Xunit;

namespace EhViewer.Core.Tests.ModelTests;

public class CoreModelTests
{
    private static JsonDocument LoadUnitVectors()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "expected", "unit_vectors.expected.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static (string BaseUrl, Dictionary<string, string?> Query) SplitUrl(string url)
    {
        int qIndex = url.IndexOf('?');
        if (qIndex < 0)
        {
            return (url, new Dictionary<string, string?>(StringComparer.Ordinal));
        }

        string baseUrl = url[..qIndex];
        string queryPart = url[(qIndex + 1)..];
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string pair in queryPart.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0)
            {
                dict[pair] = null;
            }
            else
            {
                dict[pair[..eq]] = WebUtility.UrlDecode(pair[(eq + 1)..]);
            }
        }
        return (baseUrl, dict);
    }

    [Fact]
    public void EhConfig_UConfig_MatchesGoldenVectors()
    {
        using var doc = LoadUnitVectors();
        JsonElement ehCfg = doc.RootElement.GetProperty("ehConfig");

        var cfgDefault = new EhConfig();
        Assert.Equal(ehCfg.GetProperty("defaultUconfig").GetString(), cfgDefault.UConfig());

        var cfgCustom = new EhConfig
        {
            LayoutMode = EhConfig.LayoutModeThumb,
            PreviewRow = EhConfig.PreviewRow10,
            PreviewSize = EhConfig.PreviewSizeLarge,
            DefaultCategories = EhConfig.NonH | EhConfig.Cosplay,
            ExcludedLanguages = "0,1024",
            FavoritesSort = EhConfig.OrderByPubTime,
            ResultCount = EhConfig.ResultCount100
        };
        Assert.Equal(ehCfg.GetProperty("customUconfig").GetString(), cfgCustom.BuildUConfig());

        cfgCustom.HahClientIp = "192.168.1.50";
        cfgCustom.HahClientPort = 8080;
        cfgCustom.HahClientPasskey = "secret";
        Assert.Contains("-hp_192.168.1.50:8080-hk_secret-", cfgCustom.BuildUConfig(), StringComparison.Ordinal);
    }

    [Fact]
    public void UrlBuilders_MatchGoldenVectors_AndAllModes()
    {
        using var doc = LoadUnitVectors();
        JsonElement builders = doc.RootElement.GetProperty("urlBuilders");

        var lub = new ListUrlBuilder
        {
            Keyword = "artist:test space",
            Category = EhConfig.Doujinshi | EhConfig.Manga,
            AdvanceSearch = (int)(AdvanceSearchFlags.SName | AdvanceSearchFlags.STags | AdvanceSearchFlags.SH),
            MinRating = 3,
            PageFrom = 10,
            PageTo = 50
        };

        (string expectedListBase, Dictionary<string, string?> expectedListQuery) =
            SplitUrl(builders.GetProperty("listNormal").GetString()!);
        (string actualListBase, Dictionary<string, string?> actualListQuery) = SplitUrl(lub.Build(EhSite.E));
        Assert.Equal(expectedListBase, actualListBase);
        Assert.Equal(expectedListQuery.Count, actualListQuery.Count);
        foreach ((string k, string? v) in expectedListQuery)
        {
            Assert.True(actualListQuery.TryGetValue(k, out string? actualV), $"Missing query key '{k}'");
            Assert.Equal(v, actualV);
        }

        var flub = new FavListUrlBuilder
        {
            FavCat = 2,
            Keyword = "test fav",
            Index = 1
        };

        (string expectedFavBase, Dictionary<string, string?> expectedFavQuery) =
            SplitUrl(builders.GetProperty("favList").GetString()!);
        (string actualFavBase, Dictionary<string, string?> actualFavQuery) = SplitUrl(flub.Build(EhSite.E));
        Assert.Equal(expectedFavBase, actualFavBase);
        Assert.Equal(expectedFavQuery.Count, actualFavQuery.Count);
        foreach ((string k, string? v) in expectedFavQuery)
        {
            Assert.True(actualFavQuery.TryGetValue(k, out string? actualV), $"Missing fav query key '{k}'");
            Assert.Equal(v, actualV);
        }

        Assert.Equal("https://exhentai.org/uploader/someone/2", new ListUrlBuilder { Mode = ListUrlBuilder.ModeUploader, Keyword = "someone", PageIndex = 2 }.Build(EhSite.Ex));
        Assert.Equal("https://e-hentai.org/tag/a+b/1", new ListUrlBuilder { Mode = ListUrlBuilder.ModeTag, Keyword = "a b", PageIndex = 1 }.Build(EhSite.E));
        Assert.Equal("https://e-hentai.org/popular", new ListUrlBuilder { Mode = ListUrlBuilder.ModeWhatsHot }.Build(EhSite.E));
        Assert.Equal("https://exhentai.org/watched", new ListUrlBuilder { Mode = ListUrlBuilder.ModeSubscription }.Build(EhSite.Ex));
        Assert.Equal("127.0.0.1:8888", new ListUrlBuilder { Mode = ListUrlBuilder.ModeTopList, Follow = "tl=15", PageIndex = 200 }.Build());
        Assert.Equal("https://e-hentai.org/?seek=2024-01-02&f_search=test", ListUrlBuilder.JumpHrefBuild("https://e-hentai.org/?jump=1w&f_search=test", "seek=2024-01-02"));
    }

    [Fact]
    public void GalleryInfo_SLangCsvJson_AndEhUtils_WorkAsExpected()
    {
        using var doc = LoadUnitVectors();
        foreach (JsonProperty prop in doc.RootElement.GetProperty("generateSLang").EnumerateObject())
        {
            var gi = new GalleryInfo { SimpleTags = prop.Name.Split(',') };
            gi.GenerateSLang();
            string? expected = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetString();
            Assert.Equal(expected, gi.SimpleLanguage);
        }

        var fromTitle = new GalleryInfo { Title = "[Author] Work [中国語] [Decensored]" };
        fromTitle.GenerateSLang();
        Assert.Equal(GalleryInfo.SLangZh, fromTitle.SimpleLanguage);

        fromTitle.Gid = 12345;
        fromTitle.Token = "abcdef1234";
        fromTitle.Category = EhConfig.Doujinshi;
        fromTitle.SimpleTags = ["language:chinese"];
        GalleryInfo? roundCsv = GalleryInfo.FromCsv(fromTitle.ToCsvLine());
        Assert.NotNull(roundCsv);
        Assert.Equal(12345, roundCsv!.Gid);
        Assert.Equal("abcdef1234", roundCsv.Token);
        Assert.Equal(EhConfig.Doujinshi, roundCsv.Category);

        JsonObject json = fromTitle.ToJson();
        GalleryInfo roundJson = GalleryInfo.FromJson(json);
        Assert.Equal(GalleryInfo.SLangZh, roundJson.SimpleLanguage);

        // Upstream JSON compatibility with "" empty key bug
        var legacyJson = JsonNode.Parse("""{"gid": 9, "": "EN"}""")!.AsObject();
        Assert.Equal("EN", GalleryInfo.FromJson(legacyJson).SimpleLanguage);

        Assert.Equal(EhConfig.ArtistCg, EhUtils.GetCategory("Artist CG Sets"));
        Assert.Equal("doujinshi", EhUtils.GetCategory(EhConfig.Doujinshi));
        Assert.Equal("Title ", EhUtils.ExtractTitle("[Group] Title | English (C99) [Chinese]"));
        Assert.Equal("Work", EhUtils.ExtractTitle("[Group] Work (C99) [Chinese]"));
        Assert.Equal("https://ehgt.org/t/00/01/test_250.jpg", EhUtils.HandleThumbUrlResolution("https://ehgt.org/t/00/01/test_l.jpg", 1));
        Assert.Equal(
            "https://ehgt.org/31/7a/317a1a254cd9c3269e71b2aa2671fe8d28c91097-260198-640-480-png_250.jpg",
            EhUrl.GetFixedPreviewThumbUrl("https://exhentai.org/t/31/7a/317a1a254cd9c3269e71b2aa2671fe8d28c91097-260198-640-480-png_250.jpg"));
    }

    [Fact]
    public void SpiderInfo_V2AndV1AndCbor_MatchGoldenVectors()
    {
        using var doc = LoadUnitVectors();
        JsonElement spiderVec = doc.RootElement.GetProperty("spiderInfo");
        string expectedV2Text = spiderVec.GetProperty("v2SerializedText").GetString()!;

        var si = new SpiderInfo
        {
            StartPage = 5,
            Gid = 1366222L,
            Token = "0123456789",
            PreviewPages = 2,
            PreviewPerPage = 20,
            Pages = 4,
            PTokenMap = new SortedDictionary<int, string>
            {
                [0] = "token00000",
                [1] = SpiderInfo.TokenFailed,
                [2] = "token22222"
            }
        };

        using var ms = new MemoryStream();
        si.Write(ms, leaveOpen: true);
        string actualText = Encoding.UTF8.GetString(ms.ToArray());
        Assert.Equal(expectedV2Text, actualText);

        ms.Position = 0;
        SpiderInfo? parsedBack = SpiderInfo.Read(ms);
        Assert.NotNull(parsedBack);
        JsonElement roundDto = spiderVec.GetProperty("v2RoundTrip");
        Assert.Equal(roundDto.GetProperty("startPage").GetInt32(), parsedBack!.StartPage);
        Assert.Equal(roundDto.GetProperty("gid").GetInt64(), parsedBack.Gid);
        Assert.Equal(roundDto.GetProperty("token").GetString(), parsedBack.Token);
        Assert.Equal(roundDto.GetProperty("previewPages").GetInt32(), parsedBack.PreviewPages);
        Assert.Equal(roundDto.GetProperty("previewPerPage").GetInt32(), parsedBack.PreviewPerPage);
        Assert.Equal(roundDto.GetProperty("pages").GetInt32(), parsedBack.Pages);
        Assert.Equal(2, parsedBack.PTokenMap.Count);
        Assert.Equal("token00000", parsedBack.PTokenMap[0]);
        Assert.Equal("token22222", parsedBack.PTokenMap[2]);

        // VERSION1 read compatibility
        const string v1Text = "00000002\n99999\nabcde12345\n1\n3\n12\n0 pt0\nbad line\n";
        using var v1Stream = new MemoryStream(Encoding.ASCII.GetBytes(v1Text));
        SpiderInfo? v1Parsed = SpiderInfo.Read(v1Stream);
        Assert.NotNull(v1Parsed);
        Assert.Equal(2, v1Parsed!.StartPage);
        Assert.Equal(99999L, v1Parsed.Gid);
        Assert.Equal(12, v1Parsed.Pages);
        Assert.Equal("pt0", v1Parsed.PTokenMap[0]);

        // CBOR golden vector
        byte[] cborBytes = Convert.FromHexString(spiderVec.GetProperty("cborHex").GetString()!);
        using var cborStream = new MemoryStream(cborBytes);
        SpiderInfo? cborParsed = SpiderInfo.ReadCborHeader(cborStream);
        Assert.NotNull(cborParsed);
        JsonElement expectedCbor = spiderVec.GetProperty("cborParsed");
        Assert.Equal(expectedCbor.GetProperty("gid").GetInt64(), cborParsed!.Gid);
        Assert.Equal(expectedCbor.GetProperty("token").GetString(), cborParsed.Token);
        Assert.Equal(-1, cborParsed.Pages);

        // CBOR indefinite-length map & indefinite UTF-8 string
        byte[] indefCbor =
        [
            0xBF, // indefinite map
            0x63, (byte)'g', (byte)'i', (byte)'d', 0x18, 42, // "gid": 42
            0x65, (byte)'t', (byte)'o', (byte)'k', (byte)'e', (byte)'n',
            0x7F, 0x62, (byte)'a', (byte)'b', 0x62, (byte)'c', (byte)'d', 0xFF, // "abcd"
            0xFF
        ];
        using var indefStream = new MemoryStream(indefCbor);
        SpiderInfo? indefParsed = SpiderInfo.ReadCborHeader(indefStream);
        Assert.NotNull(indefParsed);
        Assert.Equal(42L, indefParsed!.Gid);
        Assert.Equal("abcd", indefParsed.Token);
    }
}

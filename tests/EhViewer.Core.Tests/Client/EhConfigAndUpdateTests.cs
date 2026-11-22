using EhViewer.Core.Client;
using EhViewer.Core.Network;
using EhViewer.Core.Util;
using Xunit;

namespace EhViewer.Core.Tests.Client;

public sealed class EhConfigAndUpdateTests
{
    [Fact]
    public void ExcludedLanguageCatalog_ContainsAll17LanguagesAndUniqueCodes()
    {
        Assert.Equal(17, EhConfig.ExcludedLanguageCatalog.Count);

        var allCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (ExcludedLanguageGroup group in EhConfig.ExcludedLanguageCatalog)
        {
            Assert.True(allCodes.Add(group.OriginalCode), $"Duplicate original code {group.OriginalCode}");
            Assert.True(allCodes.Add(group.TranslatedCode), $"Duplicate translated code {group.TranslatedCode}");
            Assert.True(allCodes.Add(group.RewriteCode), $"Duplicate rewrite code {group.RewriteCode}");
        }

        Assert.Equal(51, allCodes.Count);
        Assert.Equal(8, EhConfig.ExcludedNamespaceOptions.Count);
    }

    [Fact]
    public void EhConfig_SetExcludedLanguageSet_AndApplyUConfig_RoundTripsCleanly()
    {
        var cfg = new EhConfig
        {
            ExcludedNamespaces = EhConfig.NamespacesFemale | EhConfig.NamespacesMale,
            FavoritesSort = EhConfig.OrderByPubTime,
            CommentSort = EhConfig.CommentsSortRecentFirst,
            Popular = EhConfig.PopularNo,
            HahClientIp = "10.0.0.5",
            HahClientPort = 443,
            HahClientPasskey = "abc"
        };
        cfg.SetExcludedLanguageSet(["0", "1034", "2303", "1034"]);
        Assert.Equal("0x1034x2303", cfg.ExcludedLanguages);

        string encoded = cfg.BuildUConfig();
        var parsed = new EhConfig();
        parsed.ApplyUConfig(encoded);

        Assert.Equal(cfg.ExcludedLanguages, parsed.ExcludedLanguages);
        Assert.Equal(cfg.ExcludedNamespaces, parsed.ExcludedNamespaces);
        Assert.Equal(EhConfig.OrderByPubTime, parsed.FavoritesSort);
        Assert.Equal(EhConfig.CommentsSortRecentFirst, parsed.CommentSort);
        Assert.Equal(EhConfig.PopularNo, parsed.Popular);
        Assert.Equal("10.0.0.5", parsed.HahClientIp);
        Assert.Equal(443, parsed.HahClientPort);
        Assert.Equal("abc", parsed.HahClientPasskey);
        Assert.Equal(new HashSet<string>(["0", "1034", "2303"]), parsed.GetExcludedLanguageSet());
    }

    [Fact]
    public void EhConfigCookieSync_WritesAndReadsBothSitesWithoutSigningIn()
    {
        var jar = new InMemoryCookieStore();
        var cfg = new EhConfig { ExcludedLanguages = "1x1025" };
        EhConfigCookieSync.ApplyToCookieStore(jar, cfg);

        Assert.False(jar.HasSignedIn);
        Assert.True(EhConfigCookieSync.TryReadFromCookieStore(jar, out string? readBack));
        Assert.Equal(cfg.BuildUConfig(), readBack);
        Assert.Contains("xl_1x1025", jar.GetCookieHeader(new Uri(EhUrl.HostEx)), StringComparison.Ordinal);
    }

    [Fact]
    public void AppUpdateHelper_ParsesUpstreamAndGitHubReleaseFormats()
    {
        const string upstreamJson = """
            {
              "version": "2.1.0",
              "versionCode": 210,
              "fileDownloadUrl": "https://example.org/eh.zip",
              "mustUpdate": true,
              "updateContent": { "title": "v2.1", "content": "Bug fixes" }
            }
            """;
        AppUpdateCheckResult r1 = AppUpdateHelper.ParseAndCheck(upstreamJson, "2.0.2.7", 200);
        Assert.True(r1.HasUpdate);
        Assert.NotNull(r1.Latest);
        Assert.Equal("2.1.0", r1.Latest!.Version);
        Assert.True(r1.Latest.MustUpdate);
        Assert.Equal("Bug fixes", r1.Latest.ReleaseNotes);

        const string githubJson = """
            {
              "tag_name": "v2.0.2.7",
              "name": "Release 2.0.2.7",
              "body": "Notes",
              "html_url": "https://github.com/example/releases/tag/v2.0.2.7",
              "assets": [
                { "browser_download_url": "https://github.com/example/eh-win-x64.zip" }
              ]
            }
            """;
        AppUpdateCheckResult r2 = AppUpdateHelper.ParseAndCheck(githubJson, "2.0.2.7", 1);
        Assert.False(r2.HasUpdate);
        Assert.Equal("https://github.com/example/eh-win-x64.zip", r2.Latest!.DownloadUrl);
    }

    private sealed class InMemoryCookieStore : ICookieStore
    {
        private readonly List<EhCookie> _cookies = [];
        public bool HasSignedIn => false;
        public void AddCookie(EhCookie cookie) => _cookies.Add(cookie);
        public void SaveFromResponse(Uri requestUrl, IEnumerable<string> setCookieHeaders) { }
        public IReadOnlyList<EhCookie> LoadForRequest(Uri requestUrl) => [.. _cookies.Where(c => c.Matches(requestUrl))];
        public IReadOnlyList<EhCookie> GetCookies(Uri url) => LoadForRequest(url);
        public string GetCookieHeader(Uri url) => string.Join("; ", LoadForRequest(url).Select(c => $"{c.Name}={c.Value}"));
        public bool Contains(Uri url, string name) => LoadForRequest(url).Any(c => c.Name == name);
        public void SignOut() => _cookies.Clear();
        public void Clear() => _cookies.Clear();
    }
}

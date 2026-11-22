using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Data.TopList;
using EhViewer.Core.Client.Data.UserTag;
using EhViewer.Core.Client.Exceptions;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Network;
using EhViewer.Core.Storage;
using Xunit;

namespace EhViewer.Core.Tests.ClientTests;

/// <summary>
/// Coverage for <see cref="EhTagDatabase"/> (upstream <c>EhTagDatabaseTest.java</c> parity plus
/// SHA-1 update semantics) and <see cref="EhEngine"/> (request shaping, response-header error
/// detection, error mapping, and fixture-backed parsing).
/// Every HTTP exchange is served by <see cref="RecordingMessageHandler"/>; no real network access.
/// </summary>
public sealed class EhTagDatabaseAndEngineTests
{
    private const string KokomadeMarker = "https://exhentai.org/img/kokomade.jpg";
    private const string UnparseableHtml = "<html><body><p>not a gallery page</p></body></html>";
    private const string GalleryPageUrl = "https://e-hentai.org/s/ghz0e5m98a4/1363978-10";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // ==================== EhTagDatabase: upstream parity ====================

    [Fact]
    public void ReadTheList_MatchesUpstreamExpectations()
    {
        // Mirrors upstream EhTagDatabaseTest#readTheList over the same fixture resource.
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "input", "EhTagDatabaseTest");
        using FileStream stream = File.OpenRead(path);
        var database = new EhTagDatabase("EhTagDatabaseTest", stream);

        Assert.Equal("EhTagDatabaseTest", database.Name);
        Assert.Equal("a", database.GetTranslation("1"));
        Assert.Equal("ab", database.GetTranslation("12"));
        Assert.Equal("abc", database.GetTranslation("123"));
        Assert.Equal("abcd", database.GetTranslation("1234"));
        Assert.Equal("1", database.GetTranslation("a"));
        Assert.Equal("12", database.GetTranslation("ab"));
        Assert.Equal("123", database.GetTranslation("abc"));
        Assert.Equal("1234", database.GetTranslation("abcd"));
        Assert.Null(database.GetTranslation("21"));
    }

    [Fact]
    public void NamespacePrefixMapping_RoundTripsKnownNamespaces()
    {
        // locationNamespaceMapsToLocPrefix (upstream).
        Assert.Equal("loc:", EhTagDatabase.NamespaceToPrefix("location"));
        Assert.Equal("location", EhTagDatabase.PrefixToNamespace("loc:"));

        // "misc" is the one namespace whose prefix is the empty string.
        Assert.Equal(string.Empty, EhTagDatabase.NamespaceToPrefix("misc"));
        Assert.Equal("misc", EhTagDatabase.PrefixToNamespace(string.Empty));

        Assert.Equal("f:", EhTagDatabase.NamespaceToPrefix("female"));
        Assert.Equal("female", EhTagDatabase.PrefixToNamespace("f:"));
    }

    [Fact]
    public void NamespacePrefixMapping_ReturnsNullForUnknownOrNullInput()
    {
        Assert.Null(EhTagDatabase.NamespaceToPrefix(null));
        Assert.Null(EhTagDatabase.NamespaceToPrefix("no-such-namespace"));
        Assert.Null(EhTagDatabase.NamespaceToPrefix("z"));

        Assert.Null(EhTagDatabase.PrefixToNamespace(null));
        Assert.Null(EhTagDatabase.PrefixToNamespace("z:"));
        Assert.Null(EhTagDatabase.PrefixToNamespace("loc"));
    }

    [Fact]
    public void LocPrefix_TranslatesAndSuggestsFullNamespace()
    {
        // locPrefixLooksUpAndSuggestsFullNamespace (upstream): one "loc:beach\r<base64>\n" record.
        using MemoryStream stream = CreateTagDatabaseStream(("loc:beach", "沙滩"));
        var database = new EhTagDatabase("location-test", stream);

        Assert.Equal("沙滩", database.GetTranslation("loc:beach"));

        // ParseTagLine expands the stored prefix back to the canonical namespace for suggestions.
        List<TagTranslationSuggestion> hints = database.Suggest("location:beach");
        TagTranslationSuggestion hint = Assert.Single(hints);
        Assert.Equal("沙滩", hint.Chinese);
        Assert.Equal("location:beach", hint.English);
    }

    [Fact]
    public void Suggest_MatchesEnglishTagsCaseInsensitively()
    {
        using MemoryStream stream = CreateTagDatabaseStream(("loc:beach", "沙滩"));
        var database = new EhTagDatabase("location-ci-test", stream);

        List<TagTranslationSuggestion> upper = database.Suggest("LOCATION:BEACH");
        Assert.Single(upper);
        Assert.Equal("location:beach", upper[0].English);

        List<TagTranslationSuggestion> mixed = database.Suggest("Beach");
        Assert.Single(mixed);
        Assert.Equal("沙滩", mixed[0].Chinese);
    }

    [Fact]
    public void Suggest_TruncatesToMaxResults_AndUsesDocumentedDefaultCap()
    {
        using MemoryStream stream = CreateTagDatabaseStream(
            ("tag_one", "一"),
            ("tag_two", "二"),
            ("tag_three", "三"));
        var database = new EhTagDatabase("suggest-limit", stream);

        // Suggest walks TagList in payload order, and the payload is emitted in ordinal key order.
        List<TagTranslationSuggestion> truncated = database.Suggest("tag_", maxResults: 2);
        Assert.Equal(2, truncated.Count);
        Assert.Equal("tag_one", truncated[0].English);
        Assert.Equal("一", truncated[0].Chinese);
        Assert.Equal("tag_three", truncated[1].English);
        Assert.Equal("三", truncated[1].Chinese);

        Assert.Empty(database.Suggest("tag_", maxResults: 0));
        Assert.Empty(database.Suggest("tag_", maxResults: -1));
        Assert.Empty(database.Suggest(null));

        (string, string)[] many = Enumerable.Range(0, 45)
            .Select(i => ($"tag_{i:000}", $"值{i}"))
            .ToArray();
        using MemoryStream manyStream = CreateTagDatabaseStream(many);
        var manyDatabase = new EhTagDatabase("suggest-default-cap", manyStream);
        Assert.Equal(40, manyDatabase.Suggest("tag_").Count);
    }

    [Fact]
    public void GetTranslation_ReturnsNullForEmptyKeyAndForZeroLengthPayload()
    {
        using MemoryStream stream = CreateTagDatabaseStream(("a", "1"));
        var database = new EhTagDatabase("null-keys", stream);

        Assert.Null(database.GetTranslation(null));
        Assert.Null(database.GetTranslation(string.Empty));
        Assert.Null(database.GetTranslation("missing"));

        // A zero-length payload is accepted and yields no translations instead of throwing.
        using MemoryStream empty = CreateTagDatabaseStream();
        var emptyDatabase = new EhTagDatabase("empty", empty);
        Assert.Null(emptyDatabase.GetTranslation("a"));
        Assert.DoesNotContain(emptyDatabase.TagList, static t => t.Chinese != "null");
    }

    [Fact]
    public void Constructor_RejectsNegativeLengthHeader()
    {
        using MemoryStream stream = new([0xFF, 0xFF, 0xFF, 0xFF]);

        var ex = Assert.Throws<InvalidDataException>(() => new EhTagDatabase("bad-header", stream));
        Assert.Contains("Invalid tag database length header", ex.Message, StringComparison.Ordinal);
    }

    // ==================== EhTagDatabase.LoadAndUpdateAsync / SHA-1 ====================

    [Fact]
    public async Task LoadAndUpdateAsync_InstallsFreshDatabaseFromDownloadedPayload()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] data = BuildTagDatabaseBytes(("a", "1"));
            byte[] sha1 = SHA1.HashData(data);
            const string sha1Url = "https://example.test/tag.sha1";
            const string dataUrl = "https://example.test/tag.data";

            using var handler = new RecordingMessageHandler(request =>
                IsUrl(request, sha1Url)
                    ? RawResponse(sha1)
                    : RawResponse(data));
            using var client = new HttpClient(handler);

            EhTagDatabase? database = await EhTagDatabase.LoadAndUpdateAsync(
                directory, client, "tag.sha1", sha1Url, "tag.data", dataUrl);

            Assert.NotNull(database);
            Assert.Equal("1", database!.GetTranslation("a"));

            // Both artifacts are promoted into the directory and no temp residue remains.
            Assert.True(File.Exists(Path.Combine(directory, "tag.data")));
            Assert.True(File.Exists(Path.Combine(directory, "tag.sha1")));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(directory, "tag.data")));
            Assert.Equal(sha1, await File.ReadAllBytesAsync(Path.Combine(directory, "tag.sha1")));

            Assert.Equal(2, handler.Requests.Count);
            Assert.All(handler.Requests, static r => Assert.Equal(HttpMethod.Get, r.Method));
            Assert.Equal(sha1Url, handler.Requests[0].Uri.AbsoluteUri);
            Assert.Equal(dataUrl, handler.Requests[1].Uri.AbsoluteUri);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAndUpdateAsync_ReturnsCurrentWithoutFetchingData_WhenRemoteSha1MatchesLocalData()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] data = BuildTagDatabaseBytes(("a", "1"));
            byte[] sha1 = SHA1.HashData(data);
            await File.WriteAllBytesAsync(Path.Combine(directory, "tag.data"), data);
            await File.WriteAllBytesAsync(Path.Combine(directory, "tag.sha1"), sha1);

            const string sha1Url = "https://example.test/tag.sha1";
            const string dataUrl = "https://example.test/tag.data";
            using var handler = new RecordingMessageHandler(request =>
                IsUrl(request, sha1Url)
                    ? RawResponse(sha1)
                    : RawResponse(BuildTagDatabaseBytes(("a", "9"))));
            using var client = new HttpClient(handler);

            EhTagDatabase? database = await EhTagDatabase.LoadAndUpdateAsync(
                directory, client, "tag.sha1", sha1Url, "tag.data", dataUrl);

            Assert.NotNull(database);
            Assert.Equal("1", database!.GetTranslation("a"));

            // The data endpoint is never touched when the advertised SHA-1 already matches.
            RecordedRequest request = Assert.Single(handler.Requests);
            Assert.Equal(sha1Url, request.Uri.AbsoluteUri);
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(directory, "tag.data")));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAndUpdateAsync_RejectsTamperedDownload_AndLeavesCurrentDataIntact()
    {
        string directory = CreateTempDirectory();
        try
        {
            // Locally valid (old) database that must survive the rejected update.
            byte[] oldData = BuildTagDatabaseBytes(("a", "1"));
            byte[] oldSha1 = SHA1.HashData(oldData);
            await File.WriteAllBytesAsync(Path.Combine(directory, "tag.data"), oldData);
            await File.WriteAllBytesAsync(Path.Combine(directory, "tag.sha1"), oldSha1);

            // The advertised SHA-1 belongs to bytes the data endpoint never returns.
            byte[] advertisedSha1 = SHA1.HashData(BuildTagDatabaseBytes(("a", "9")));
            byte[] tamperedData = BuildTagDatabaseBytes(("a", "7"));
            Assert.NotEqual(advertisedSha1, SHA1.HashData(tamperedData));

            const string sha1Url = "https://example.test/tag.sha1";
            const string dataUrl = "https://example.test/tag.data";
            using var handler = new RecordingMessageHandler(request =>
                IsUrl(request, sha1Url)
                    ? RawResponse(advertisedSha1)
                    : RawResponse(tamperedData));
            using var client = new HttpClient(handler);

            EhTagDatabase? database = await EhTagDatabase.LoadAndUpdateAsync(
                directory, client, "tag.sha1", sha1Url, "tag.data", dataUrl);

            // Verification failure returns the previous instance instead of the tampered payload.
            Assert.NotNull(database);
            Assert.Equal("1", database!.GetTranslation("a"));
            Assert.Null(database.GetTranslation("9"));
            Assert.Null(database.GetTranslation("7"));

            // On-disk artifacts are not polluted by the rejected download and no temp file leaks.
            Assert.Equal(oldData, await File.ReadAllBytesAsync(Path.Combine(directory, "tag.data")));
            Assert.Equal(oldSha1, await File.ReadAllBytesAsync(Path.Combine(directory, "tag.sha1")));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAndUpdateAsync_DegradesToCurrent_WhenSha1DownloadReturns500()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] data = BuildTagDatabaseBytes(("a", "1"));
            await File.WriteAllBytesAsync(Path.Combine(directory, "tag.data"), data);
            await File.WriteAllBytesAsync(Path.Combine(directory, "tag.sha1"), SHA1.HashData(data));

            using var handler = new RecordingMessageHandler(static _ =>
                new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("boom", Utf8NoBom, "text/plain")
                });
            using var client = new HttpClient(handler);

            EhTagDatabase? database = await EhTagDatabase.LoadAndUpdateAsync(
                directory, client, "tag.sha1", "https://example.test/tag.sha1", "tag.data", "https://example.test/tag.data");

            Assert.NotNull(database);
            Assert.Equal("1", database!.GetTranslation("a"));
            Assert.Single(handler.Requests);
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(directory, "tag.data")));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAndUpdateAsync_DegradesToCurrent_WhenDataDownloadFailsAfterSha1Changed()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] oldData = BuildTagDatabaseBytes(("a", "1"));
            byte[] oldSha1 = SHA1.HashData(oldData);
            await File.WriteAllBytesAsync(Path.Combine(directory, "tag.data"), oldData);
            await File.WriteAllBytesAsync(Path.Combine(directory, "tag.sha1"), oldSha1);

            byte[] newSha1 = SHA1.HashData(BuildTagDatabaseBytes(("a", "9")));
            const string sha1Url = "https://example.test/tag.sha1";
            using var handler = new RecordingMessageHandler(request =>
                IsUrl(request, sha1Url)
                    ? RawResponse(newSha1)
                    : new HttpResponseMessage(HttpStatusCode.BadGateway));
            using var client = new HttpClient(handler);

            EhTagDatabase? database = await EhTagDatabase.LoadAndUpdateAsync(
                directory, client, "tag.sha1", sha1Url, "tag.data", "https://example.test/tag.data");

            Assert.NotNull(database);
            Assert.Equal("1", database!.GetTranslation("a"));
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(oldData, await File.ReadAllBytesAsync(Path.Combine(directory, "tag.data")));
            Assert.Equal(oldSha1, await File.ReadAllBytesAsync(Path.Combine(directory, "tag.sha1")));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAndUpdateAsync_PropagatesCancellation_ForPreCancelledToken()
    {
        string directory = CreateTempDirectory();
        try
        {
            using var handler = new RecordingMessageHandler(static _ =>
                RawResponse(BuildTagDatabaseBytes(("a", "1"))));
            using var client = new HttpClient(handler);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                EhTagDatabase.LoadAndUpdateAsync(
                    directory,
                    client,
                    "tag.sha1",
                    "https://example.test/tag.sha1",
                    "tag.data",
                    "https://example.test/tag.data",
                    cts.Token));

            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    // ==================== EhEngine: SignInAsync ====================

    [Fact]
    public async Task SignInAsync_PostsFormToApiSignIn_WithRefererAndOrigin_AndDecodesName()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(
            "<html><body><p>You are now logged in as: TestUser<scr</p></body></html>"));

        string name = await scope.Engine.SignInAsync("TestUser", "secret");

        Assert.Equal("TestUser", name);

        RecordedRequest request = Assert.Single(scope.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri(EhUrl.ApiSignIn), request.Uri);
        Assert.Equal("https://forums.e-hentai.org/index.php?act=Login&CODE=00", request.Headers["Referer"]);
        Assert.Equal("https://forums.e-hentai.org", request.Headers["Origin"]);
        Assert.Equal("forums.e-hentai.org", request.Headers["Host"]);

        Assert.NotNull(request.Body);
        Assert.Contains("UserName=TestUser", request.Body!, StringComparison.Ordinal);
        Assert.Contains("PassWord=secret", request.Body!, StringComparison.Ordinal);
        Assert.Contains("submit=Log+me+in", request.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInAsync_DecodesUtf8DisplayName()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(
            "<html><body><p>You are now logged in as: 測試用戶<br /></p></body></html>"));

        string name = await scope.Engine.SignInAsync("測試用戶", "secret");

        Assert.Equal("測試用戶", name);
    }

    [Fact]
    public async Task SignInAsync_SurfacesForumErrorMessageAsEhException()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(
            "<html><body><h4>The error returned was:</h4><p>Username or password incorrect</p></body></html>"));

        var ex = await Assert.ThrowsAsync<EhException>(() => scope.Engine.SignInAsync("u", "p"));

        Assert.Equal("Username or password incorrect", ex.Message);
    }

    [Fact]
    public void EhTagDatabase_ZeroAllocIndex_TranslatesMultiByteUtf8AndToleratesMalformedLines()
    {
        string rawLines = string.Join('\n',
            $"f:sole female\r{Convert.ToBase64String(Encoding.UTF8.GetBytes("单女主"))}",
            $"l:chinese\r{Convert.ToBase64String(Encoding.UTF8.GetBytes("中文翻译 テスト"))}",
            "malformed_no_cr",
            "bad\r%%%not_base64%%%");
        byte[] payload = Encoding.UTF8.GetBytes(rawLines);
        byte[] blob = new byte[4 + payload.Length];
        blob[0] = (byte)(payload.Length >> 24);
        blob[1] = (byte)(payload.Length >> 16);
        blob[2] = (byte)(payload.Length >> 8);
        blob[3] = (byte)payload.Length;
        payload.CopyTo(blob, 4);

        using var ms = new MemoryStream(blob, writable: false);
        var db = new EhTagDatabase("utf8-malformed", ms);

        Assert.Equal("单女主", db.GetTranslation("f:sole female"));
        Assert.Equal("中文翻译 テスト", db.GetTranslation("l:chinese"));
        Assert.Equal("单女主", db.GetTranslation("f:sole female"));
        Assert.Null(db.GetTranslation("bad"));
        Assert.Null(db.GetTranslation("malformed_no_cr"));
        Assert.Contains(db.TagList, static t => t.English == "female:sole female" && t.Chinese == "单女主");
        Assert.Contains(db.TagList, static t => t.English == "malformed_no_cr" && t.Chinese == "null");
        Assert.Contains(db.TagList, static t => t.English == "bad\r%%%not_base64%%%" && t.Chinese == "null");
    }

    // ==================== EhEngine: EventPane ====================

    [Fact]
    public async Task GetHomeDetailAsync_RaisesEventPaneReceived_WithEventPaneInnerHtml()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(FixtureHtml("EhNews.html")));

        var payloads = new List<string>();
        scope.Engine.EventPaneReceived += payloads.Add;

        HomeDetail detail = await scope.Engine.GetHomeDetailAsync();

        string payload = Assert.Single(payloads);
        Assert.False(string.IsNullOrWhiteSpace(payload));
        Assert.Contains("dawn of a new day", payload, StringComparison.Ordinal);

        // EhNews.html carries no .homebox rows, so every quota field keeps its default.
        Assert.Equal(0L, detail.Used);
        Assert.Equal(0L, detail.Total);
        Assert.Equal("IP-based limits", detail.GetImageLimits());

        RecordedRequest request = Assert.Single(scope.Handler.Requests);
        Assert.Equal(new Uri(EhUrl.GetHomeUrl()), request.Uri);
        AssertReferer(request, EhUrl.RefererE);
    }

    [Fact]
    public async Task GetHomeDetailAsync_ParsesQuota_AndStaysSilentWithoutEventPane()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(FixtureHtml("HomePageNew.html")));

        int raised = 0;
        scope.Engine.EventPaneReceived += _ => raised++;

        HomeDetail detail = await scope.Engine.GetHomeDetailAsync();

        Assert.Equal(0, raised);
        Assert.Equal(538L, detail.Used);
        Assert.Equal(50000L, detail.Total);
        Assert.Equal(1076L, detail.ResetCostValue);
        Assert.Equal("306261 Gp", detail.FromGalleryVisits);
        Assert.Equal("538/50000", detail.GetImageLimits());
    }

    [Fact]
    public async Task GetEhNewsAsync_ReturnsEventPaneFromNewsPage()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(FixtureHtml("EhNews.html")));

        EhNewsDetail news = await scope.Engine.GetEhNewsAsync();

        Assert.False(string.IsNullOrEmpty(news.EventPane));
        Assert.Contains("dawn of a new day", news.EventPane!, StringComparison.Ordinal);
        Assert.Equal(EhUrl.GetEhNewsUrl(), Assert.Single(scope.Handler.Requests).Uri.AbsoluteUri);
    }

    // ==================== EhEngine: response-header error detection ====================

    [Fact]
    public async Task GetHomeDetailAsync_ThrowsEhExceptionSadPanda_OnSadPandaHeaders()
    {
        using var scope = EngineScope.Create(static _ =>
        {
            var content = new ByteArrayContent(new byte[9615]);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/gif");
            content.Headers.ContentDisposition =
                ContentDispositionHeaderValue.Parse("inline; filename=\"sadpanda.jpg\"");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        // Sad Panda is raised as EhException("Sad Panda"); NoHAtHClientException is unrelated.
        var ex = await Assert.ThrowsAsync<EhException>(() => scope.Engine.GetHomeDetailAsync());

        Assert.Equal("Sad Panda", ex.Message);
        Assert.IsNotType<NoHAtHClientException>(ex);
    }

    [Fact]
    public async Task GetHomeDetailAsync_ThrowsQuotaEhException_OnKokomadeMarker()
    {
        using var scope = EngineScope.Create(_ => HtmlResponse($"<html><body><img src=\"{KokomadeMarker}\" /></body></html>"));

        var ex = await Assert.ThrowsAsync<EhException>(() => scope.Engine.GetHomeDetailAsync());

        Assert.Contains("今回はここまで", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Daily quota exhausted", ex.Message, StringComparison.Ordinal);
    }

    // ==================== EhEngine: error mapping ====================

    [Fact]
    public async Task GetHomeDetailAsync_ThrowsStatusCodeException_WithIdentified404()
    {
        using var scope = EngineScope.Create(static _ =>
            HtmlResponse("<html><body></body></html>", HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<StatusCodeException>(() => scope.Engine.GetHomeDetailAsync());

        Assert.Equal(404, ex.ResponseCode);
        Assert.True(ex.IsIdentifiedResponseCode);
        Assert.Equal("Not Found", ex.Message);
    }

    [Fact]
    public void StatusCodeException_ReportsUnidentifiedCodeForUnmappedStatus()
    {
        var ex = new StatusCodeException(418);

        Assert.Equal(418, ex.ResponseCode);
        Assert.False(ex.IsIdentifiedResponseCode);
        Assert.Equal("Error response code", ex.Message);

        // An explicit message always counts as identified.
        var custom = new StatusCodeException(418, "I'm a teapot");
        Assert.True(custom.IsIdentifiedResponseCode);
        Assert.Equal("I'm a teapot", custom.Message);
    }

    [Fact]
    public async Task GetGalleryPageAsync_WrapsParseException_WithBody_WhenPageIsUnparseable()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(UnparseableHtml));

        // The engine remaps a ParseException carrying HTML into a domain EhException whose message
        // is "Failed to parse server response", keeping the parser failure as InnerException.
        var ex = await Assert.ThrowsAsync<EhException>(
            () => scope.Engine.GetGalleryPageAsync(GalleryPageUrl, 1363978L, "ghz0e5m98a4"));

        Assert.IsNotType<ParseException>(ex);
        Assert.Equal("Failed to parse server response", ex.Message);

        var inner = Assert.IsType<ParseException>(ex.InnerException);
        Assert.Equal(UnparseableHtml, inner.Body);
    }

    [Fact]
    public async Task GetGalleryPageAsync_SurfacesRawBody_ForNonHtmlBody()
    {
        const string plainTextBody = "You are not allowed to view this page";
        using var scope = EngineScope.Create(_ => HtmlResponse(plainTextBody));

        // A body with no '<' is not HTML, so the engine surfaces it verbatim as the message.
        var ex = await Assert.ThrowsAsync<EhException>(
            () => scope.Engine.GetGalleryPageAsync(GalleryPageUrl, 1363978L, "ghz0e5m98a4"));

        Assert.IsNotType<ParseException>(ex);
        Assert.Equal(plainTextBody, ex.Message);
        Assert.IsType<ParseException>(ex.InnerException);
    }

    [Fact]
    public async Task GetGalleryPageAsync_ReportsEmptyHtmlResponse_ForBlankBody()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(string.Empty));

        var ex = await Assert.ThrowsAsync<EhException>(
            () => scope.Engine.GetGalleryPageAsync(GalleryPageUrl, 1363978L, "ghz0e5m98a4"));

        Assert.IsNotType<ParseException>(ex);
        Assert.Equal("Empty HTML response", ex.Message);
        Assert.IsType<ParseException>(ex.InnerException);
    }

    [Fact]
    public async Task GetHomeDetailAsync_ThrowsCancelledException_ForPreCancelledToken()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(FixtureHtml("HomePageNew.html")));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Cancellation is normalised to the domain CancelledException (upstream contract), with the
        // original OperationCanceledException preserved as InnerException.
        var ex = await Assert.ThrowsAsync<CancelledException>(
            () => scope.Engine.GetHomeDetailAsync(cts.Token));

        Assert.Equal("CANCELED", ex.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    // ==================== EhEngine: fixture-backed happy paths ====================

    [Fact]
    public async Task GetGalleryListAsync_ParsesGoldenFixture_WithoutApiEnrichment()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(FixtureHtml("GalleryListParserNew3.html")));

        GalleryListParser.Result result = await scope.Engine.GetGalleryListAsync(
            "https://exhentai.org/?f_search=female%3Amother",
            ListUrlBuilder.ModeNormal);

        Assert.Equal(25, result.GalleryInfoList.Count);
        Assert.Equal(
            "https://exhentai.org/?f_search=female:mother++female:cheating&next=2416609",
            result.NextHref);

        GalleryInfo first = result.GalleryInfoList[0];
        Assert.Equal(2426224L, first.Gid);
        Assert.Equal("85f3da11e1", first.Token);
        Assert.StartsWith("[Shunjou Shuusuke]", first.Title, StringComparison.Ordinal);
        Assert.Equal("xcaliber9999", first.Uploader);
        Assert.NotEqual(0, first.Pages);

        // Fixture rows already carry page counts and no ratings, so no api.php call is needed.
        RecordedRequest request = Assert.Single(scope.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        AssertReferer(request, EhUrl.RefererE);
        Assert.Null(result.CustomErrorString);
    }

    [Fact]
    public async Task GetGalleryPageAsync_ParsesGoldenFixture()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(FixtureHtml("GalleryPageParserTest.html")));

        GalleryPageParser.Result result = await scope.Engine.GetGalleryPageAsync(
            "https://e-hentai.org/s/ghz0e5m98a4/1363978-10",
            1363978L,
            "ghz0e5m98a4");

        Assert.StartsWith("http://108.6.41.160:2688/h/5c63e9a5", result.ImageUrl, StringComparison.Ordinal);
        Assert.EndsWith("/10.jpg", result.ImageUrl, StringComparison.Ordinal);
        Assert.Equal("26664-430636", result.SkipHathKey);
        Assert.Equal(
            "https://e-hentai.org/fullimg.php?gid=1363978&page=10&key=qt2hwrx98a4",
            result.OriginImageUrl);
        Assert.Equal("ghz0e5m98a4", result.ShowKey);

        RecordedRequest request = Assert.Single(scope.Handler.Requests);
        Assert.Equal(EhUrl.GetGalleryDetailUrl(1363978L, "ghz0e5m98a4"), request.Headers["Referer"]);
    }    [Fact]
    public async Task GetTopListAsync_ParsesGoldenFixture()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(FixtureHtml("GalleryTopList.html")));

        EhTopListDetail detail = await scope.Engine.GetTopListAsync(EhUrl.GetTopListUrl());

        Assert.Equal("EHG Toplists", detail.Title);
        Assert.NotNull(detail.GalleryTopListInfo);
        Assert.Equal(TopListType.Gallery, detail.GalleryTopListInfo!.Type);

        TopListItemArray allTime = Assert.IsType<TopListItemArray>(detail.GalleryTopListInfo.AllTimeTopList);
        Assert.Equal(10, allTime.Length);
        Assert.Equal("https://e-hentai.org/g/596447/3894f02c20/", allTime.Get(0).Href);

        RecordedRequest request = Assert.Single(scope.Handler.Requests);
        Assert.Equal(EhUrl.GetTopListUrl(), request.Headers["Referer"]);
    }

    [Fact]
    public async Task GetGalleryTokenAsync_PostsGtokenPayload_AndParsesToken()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(
            """{"tokenlist":[{"token":"a1b2c3d4e5"}]}"""));

        string token = await scope.Engine.GetGalleryTokenAsync(1234L, "0123456789", page: 2);

        Assert.Equal("a1b2c3d4e5", token);

        RecordedRequest request = Assert.Single(scope.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(EhUrl.GetApiUrl(EhSite.E), request.Uri.AbsoluteUri);
        Assert.Equal(EhUrl.OriginE, request.Headers["Origin"]);
        Assert.Contains("\"method\":\"gtoken\"", request.Body!, StringComparison.Ordinal);
        // The public page parameter is zero-based and is emitted one-based on the wire.
        Assert.Contains("[1234,\"0123456789\",3]", request.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetGalleryTokenAsync_SurfacesApiErrorAsEhException()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(
            """{"tokenlist":[{"error":"Invalid token"}]}"""));

        var ex = await Assert.ThrowsAsync<EhException>(
            () => scope.Engine.GetGalleryTokenAsync(1234L, "0123456789", page: 0));

        Assert.Equal("Invalid token", ex.Message);
    }

    [Fact]
    public async Task GetWatchedListAsync_ReturnsNullWithoutAnyRequest_WhenNotSignedIn()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(FixtureHtml("MyTagList.html")));

        UserTagList? result = await scope.Engine.GetWatchedListAsync(EhUrl.GetWatchedUrl());

        // The engine short-circuits before issuing a request for anonymous callers.
        Assert.Null(result);
        Assert.Empty(scope.Handler.Requests);
    }

    [Fact]
    public async Task DownloadArchiveAsync_ThrowsNoHAtHClientException_WhenHathClientMissing()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse(
            "<p>You must have a H@H client assigned to your account to use this feature.</p>"));

        var ex = await Assert.ThrowsAsync<NoHAtHClientException>(
            () => scope.Engine.DownloadArchiveAsync(1L, "0123456789", "1", "org"));

        Assert.Equal("No H@H client", ex.Message);
    }

    [Fact]
    public async Task AddFavoritesAsync_RejectsOutOfRangeCategory_WithoutAnyRequest()
    {
        using var scope = EngineScope.Create(static _ => HtmlResponse("<html></html>"));

        var ex = await Assert.ThrowsAsync<EhException>(
            () => scope.Engine.AddFavoritesAsync(1L, "0123456789", dstCat: 99));

        Assert.Contains("Invalid dstCat: 99", ex.Message, StringComparison.Ordinal);
        Assert.Empty(scope.Handler.Requests);
    }

    // ==================== helpers: HTTP ====================

    private static bool IsUrl(HttpRequestMessage request, string expected) =>
        string.Equals(request.RequestUri!.AbsoluteUri, expected, StringComparison.Ordinal);

    private static HttpResponseMessage HtmlResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Utf8NoBom, "text/html") };

    private static HttpResponseMessage RawResponse(byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    // ==================== helpers: tag database payloads ====================

    private static MemoryStream CreateTagDatabaseStream(params (string Key, string Chinese)[] entries) =>
        new(BuildTagDatabaseBytes(entries), writable: false);

    private static byte[] BuildTagDatabaseBytes(params (string Key, string Chinese)[] entries)
    {
        // Payload layout: [int32 BE length][UTF-8 lines "key\rbase64(translation)\n"].
        // Rows are emitted in ordinal key order so the mid-payload binary search lands correctly.
        string payload = string.Concat(entries
            .OrderBy(static e => e.Key, StringComparer.Ordinal)
            .Select(static e =>
                e.Key + "\r" + Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Chinese)) + "\n"));

        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        byte[] output = new byte[4 + payloadBytes.Length];
        output[0] = (byte)(payloadBytes.Length >> 24);
        output[1] = (byte)(payloadBytes.Length >> 16);
        output[2] = (byte)(payloadBytes.Length >> 8);
        output[3] = (byte)payloadBytes.Length;
        payloadBytes.CopyTo(output, 4);
        return output;
    }

    // ==================== helpers: fixtures & temp dirs ====================

    private static string FixtureHtml(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "input", "parser", fileName), Encoding.UTF8);

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ehviewer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// The request builder stores the referer as <c>https://e-hentai.org</c> (no trailing slash),
    /// but .NET re-serializes the known <c>Referer</c> header as a canonical absolute URI, so
    /// assertions must compare normalized forms rather than raw strings.
    /// </summary>
    private static void AssertReferer(RecordedRequest request, string expectedReferer)
    {
        Assert.True(request.Headers.TryGetValue("Referer", out string? actual), "missing Referer header");
        Assert.Equal(new Uri(expectedReferer).AbsoluteUri, new Uri(actual!).AbsoluteUri);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; these directories are disposable temp fixtures.
        }
    }

    // ==================== test doubles ====================

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, string> Headers,
        string? Body);

    private sealed class RecordingMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responder = responder;

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }

            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, body));
            cancellationToken.ThrowIfCancellationRequested();
            return _responder(request);
        }
    }

    private sealed class InMemoryCookieStore : ICookieStore
    {
        private readonly List<EhCookie> _cookies = [];

        public bool HasSignedIn { get; set; }

        public void AddCookie(EhCookie cookie) => _cookies.Add(cookie);

        public void SaveFromResponse(Uri requestUrl, IEnumerable<string> setCookieHeaders)
        {
        }

        public IReadOnlyList<EhCookie> LoadForRequest(Uri requestUrl) =>
            _cookies.Where(c => c.Matches(requestUrl)).ToArray();

        public IReadOnlyList<EhCookie> GetCookies(Uri url) => LoadForRequest(url);

        public string GetCookieHeader(Uri url) =>
            string.Join("; ", LoadForRequest(url).Select(static c => $"{c.Name}={c.Value}"));

        public bool Contains(Uri url, string name) =>
            _cookies.Any(c => string.Equals(c.Name, name, StringComparison.Ordinal) && c.Matches(url));

        public void SignOut() => _cookies.Clear();

        public void Clear() => _cookies.Clear();
    }

    /// <summary>
    /// Disposable engine fixture owning a temp SQLite database, a filter, settings, and a mocked
    /// transport, so every test gets an isolated <see cref="EhEngine"/> with zero real network I/O.
    /// </summary>
    private sealed class EngineScope : IDisposable
    {
        private EngineScope(
            string directory,
            EhDatabase database,
            RecordingMessageHandler handler,
            HttpClient client,
            EhEngine engine)
        {
            Directory = directory;
            Database = database;
            Handler = handler;
            HttpClient = client;
            Engine = engine;
        }

        public string Directory { get; }
        public EhDatabase Database { get; }
        public RecordingMessageHandler Handler { get; }
        public HttpClient HttpClient { get; }
        public EhEngine Engine { get; }

        public static EngineScope Create(
            Func<HttpRequestMessage, HttpResponseMessage> responder,
            Action<EhClientSettings>? configure = null)
        {
            string directory = CreateTempDirectory();
            var database = new EhDatabase(Path.Combine(directory, "eh.db"));
            var handler = new RecordingMessageHandler(responder);
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            var settings = new EhClientSettings();
            configure?.Invoke(settings);

            var engine = new EhEngine(
                client,
                new InMemoryCookieStore(),
                database,
                new EhFilter(database),
                settings);

            return new EngineScope(directory, database, handler, client, engine);
        }

        public void Dispose()
        {
            HttpClient.Dispose();
            Handler.Dispose();
            Database.Dispose();
            TryDeleteDirectory(Directory);
        }
    }
}

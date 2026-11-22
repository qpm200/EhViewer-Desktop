using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Network;
using EhViewer.Core.Security;
using EhViewer.Core.Spider;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.Reader;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace EhViewer.Desktop.Tests;

/// <summary>
/// Regression and integration tests for desktop parity work:
/// multi-page lazy preview url resolution, H@H <c>?nl=</c> retry &amp; anti-hijack guard,
/// &gt;40MP scanline downsampling, RTL reader semantics &amp; scoped cache eviction,
/// startup pending-download resumption, archive import, Android migration,
/// tag query formatting, and multi-window deduplication.
/// </summary>
public sealed class M2ToM5CoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "EhViewerTests",
        "M2ToM5",
        Guid.NewGuid().ToString("N"));

    public M2ToM5CoverageTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void AppendSkipHathKey_HandlesPlainAndQueryUrls()
    {
        Assert.Equal(
            "https://e-hentai.org/s/abc/100-1?nl=26664-430636",
            GalleryPageUrlSource.AppendSkipHathKey("https://e-hentai.org/s/abc/100-1", "26664-430636"));

        Assert.Equal(
            "https://e-hentai.org/s/abc/100-1?foo=1&nl=26664-430636",
            GalleryPageUrlSource.AppendSkipHathKey("https://e-hentai.org/s/abc/100-1?foo=1", "26664-430636"));

        Assert.Equal(
            "https://e-hentai.org/s/abc/100-1",
            GalleryPageUrlSource.AppendSkipHathKey("https://e-hentai.org/s/abc/100-1", null));
    }

    [Fact]
    public async Task GalleryPageUrlSource_LazilyFetchesSecondPreviewPageForMultiPageGallery()
    {
        using var scope = MockEngineScope.Create(_root, req =>
        {
            string url = req.RequestUri!.ToString();
            if (url.Contains("?p=1", StringComparison.Ordinal))
            {
                return HtmlResponse(BuildPreviewPageHtml(gid: 100, startPageNumber: 21, count: 5, totalPreviewPages: 2));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var page0 = new NormalPreviewSet();
        for (int i = 0; i < 20; i++)
        {
            page0.AddItem(i, $"https://ehgt.org/t/{i}.jpg", 0, 0, 100, 100, $"https://e-hentai.org/s/p{i}/100-{i + 1}");
        }

        var detail = new GalleryDetail
        {
            Gid = 100,
            Token = "abcd1234",
            Pages = 25,
            PreviewPages = 2,
            PreviewSet = page0
        };

        var source = new GalleryPageUrlSource(scope.Engine, detail);

        // Index 0 resolves from seeded page 0 without HTTP requests.
        Assert.Equal("https://e-hentai.org/s/p0/100-1", await source.ResolvePageUrlAsync(0, CancellationToken.None));
        Assert.Empty(scope.RequestedUrls);

        // Index 22 (1-based page 23) lies on preview page 1 and triggers lazy preview fetch.
        string? page22 = await source.ResolvePageUrlAsync(22, CancellationToken.None);
        Assert.Equal("https://e-hentai.org/s/tok23/100-23", page22);
        Assert.Single(scope.RequestedUrls);
        Assert.Contains("p=1", scope.RequestedUrls[0], StringComparison.Ordinal);

        // Index 24 (1-based page 25) reuses cached preview page 1 without refetching.
        string? page24 = await source.ResolvePageUrlAsync(24, CancellationToken.None);
        Assert.Equal("https://e-hentai.org/s/tok25/100-25", page24);
        Assert.Single(scope.RequestedUrls);
    }

    [Fact]
    public async Task ReaderImageFetcher_SupportsMultiPageAndSkipHathRetry()
    {
        byte[] validJpeg = CreateTinyJpeg();

        using var scope = MockEngineScope.Create(_root, req =>
        {
            string url = req.RequestUri!.ToString();
            if (url.Contains("/g/200/tok200/?p=1", StringComparison.Ordinal))
            {
                return HtmlResponse(BuildPreviewPageHtml(gid: 200, startPageNumber: 21, count: 3, totalPreviewPages: 2));
            }

            if (url == "https://e-hentai.org/s/tok22/200-22")
            {
                return HtmlResponse(BuildGalleryPageHtml("https://cdn.example/bad-node.jpg", skipHathKey: "node-999"));
            }

            if (url == "https://e-hentai.org/s/tok22/200-22?nl=node-999")
            {
                return HtmlResponse(BuildGalleryPageHtml("https://cdn.example/good-node.jpg", skipHathKey: "node-888"));
            }

            if (url == "https://cdn.example/bad-node.jpg")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (url == "https://cdn.example/good-node.jpg")
            {
                return ImageResponse(validJpeg, "image/jpeg");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var page0 = new NormalPreviewSet();
        for (int i = 0; i < 20; i++)
        {
            page0.AddItem(i, $"https://ehgt.org/t/{i}.jpg", 0, 0, 100, 100, $"https://e-hentai.org/s/p{i}/200-{i + 1}");
        }

        var detail = new GalleryDetail
        {
            Gid = 200,
            Token = "tok200",
            Pages = 23,
            PreviewPages = 2,
            PreviewSet = page0
        };

        var fetcher = new ReaderImageFetcher(
            scope.Engine,
            () => scope.HttpClient,
            NullLogger<ReaderImageFetcher>.Instance);
        fetcher.Bind(detail, Array.Empty<string?>(), "https://e-hentai.org/g/200/tok200/");

        // Page index 21 (page 22) triggers preview ?p=1, hits 500 on first node, retries with ?nl=node-999, succeeds!
        byte[] result = await fetcher.FetchAsync(21, CancellationToken.None);
        Assert.Equal(validJpeg, result);
        Assert.Contains(scope.RequestedUrls, u => u.Contains("?nl=node-999", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReaderImageFetcher_StopsWhenSkipHathKeyRepeatsOrHtmlReturned()
    {
        using var scope = MockEngineScope.Create(_root, req =>
        {
            string url = req.RequestUri!.ToString();
            if (url.StartsWith("https://e-hentai.org/s/loop/300-1", StringComparison.Ordinal))
            {
                return HtmlResponse(BuildGalleryPageHtml("https://cdn.example/hijack.jpg", skipHathKey: "same-key"));
            }

            if (url == "https://cdn.example/hijack.jpg")
            {
                return HtmlResponse("<!doctype html><html><body><div>系统拦截</div></body></html>");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var detail = new GalleryDetail
        {
            Gid = 300,
            Token = "tok300",
            Pages = 1,
            PreviewPages = 1
        };

        var fetcher = new ReaderImageFetcher(
            scope.Engine,
            () => scope.HttpClient,
            NullLogger<ReaderImageFetcher>.Instance);
        fetcher.Bind(detail, ["https://e-hentai.org/s/loop/300-1"], "https://e-hentai.org/g/300/tok300/");

        byte[] result = await fetcher.FetchAsync(0, CancellationToken.None);
        Assert.Empty(result);

        // Same skipHathKey "same-key" must only trigger one ?nl= retry, then bail instead of looping 4 times.
        Assert.Equal(1, scope.RequestedUrls.Count(u => u.Contains("?nl=same-key", StringComparison.Ordinal)));
    }

    [Fact]
    public void ReaderImageDecoder_DownsamplesOver40MegaPixelPngViaScanlines()
    {
        // 8500 x 5000 = 42,500,000 pixels (> 40MP scanline downsample threshold).
        byte[] pngBytes = CreateBandedGrayscalePng(8500, 5000);
        Assert.Equal(new SKSizeI(8500, 5000), ReaderImageDecoder.ProbeSize(pngBytes));

        long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        using SKBitmap? decoded = ReaderImageDecoder.Decode(pngBytes, maxWidth: 4096, maxHeight: 4096);
        long allocDelta = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;

        Assert.NotNull(decoded);
        Assert.InRange(decoded!.Width, 1, 4096);
        Assert.InRange(decoded.Height, 1, 4096);
        Assert.Equal(4096, Math.Max(decoded.Width, decoded.Height));
        Assert.Equal(1.7, (double)decoded.Width / decoded.Height, precision: 1);

        // Top band was 0x20 and bottom band was 0xE0: verifies real scanline pixels were sampled, not empty buffer.
        SKColor topPixel = decoded.GetPixel(decoded.Width / 2, 0);
        SKColor bottomPixel = decoded.GetPixel(decoded.Width / 2, decoded.Height - 1);
        Assert.True(bottomPixel.Red > topPixel.Red + 100, $"Expected dark top ({topPixel.Red}) vs bright bottom ({bottomPixel.Red})");

        // Managed allocation should stay far below a naive full 8500x5000x4 (~170MB) managed copy.
        Assert.True(allocDelta < 32 * 1024 * 1024, $"Unexpected managed allocation: {allocDelta} bytes");
    }

    [Fact]
    public async Task ReaderViewModel_KeepsLogicalNextPreviousInRtlAndScopesCacheEviction()
    {
        using var cache = new ReaderPageCache(64 * 1024 * 1024);
        using var engine1 = new ReaderEngine(
            (idx, _) => Task.FromResult(Array.Empty<byte>()),
            cache,
            NullLogger<ReaderEngine>.Instance)
        {
            PageCount = 5
        };
        using var engine2 = new ReaderEngine(
            (idx, _) => Task.FromResult(Array.Empty<byte>()),
            cache,
            NullLogger<ReaderEngine>.Instance)
        {
            PageCount = 5
        };

        cache.Put(engine1.CacheKey(0), new SKBitmap(16, 16));
        cache.Put(engine1.CacheKey(1), new SKBitmap(16, 16));
        cache.Put(engine2.CacheKey(0), new SKBitmap(16, 16));
        Assert.Equal(3, cache.Count);

        engine1.ReleasePages();
        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryAcquire(engine1.CacheKey(0), out _));
        Assert.True(cache.TryAcquire(engine2.CacheKey(0), out ReaderPageCache.Lease? lease2));
        lease2?.Dispose();

        // Now verify RTL layout keeps NextPageAsync (+1) and PreviousPageAsync (-1).
        using var netScope = MockEngineScope.Create(_root, _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var fetcher = new ReaderImageFetcher(netScope.Engine, () => netScope.HttpClient, NullLogger<ReaderImageFetcher>.Instance);
        var browse = new GalleryBrowseService(netScope.Engine, netScope.Settings);
        using var vm = new ReaderViewModel(
            engine1,
            fetcher,
            browse,
            netScope.Settings,
            netScope.Paths,
            NullLogger<ReaderViewModel>.Instance,
            ownsEngine: false);

        vm.LayoutIndex = (int)ReaderLayout.RightToLeft;
        Assert.True(engine1.IsRightToLeft);

        engine1.GoTo(0);
        await vm.NextPageAsync();
        Assert.Equal(1, engine1.CurrentPage);

        await vm.PreviousPageAsync();
        Assert.Equal(0, engine1.CurrentPage);
    }

    [Fact]
    public async Task DownloadManager_ExpandsMultiPageRetriesSkipHathRejectsHtmlAndResumesPending()
    {
        byte[] pageBytes = CreateTinyJpeg();
        string galleryHtml = BuildPreviewPageHtml(gid: 400, startPageNumber: 1, count: 20, totalPreviewPages: 2, token: "aabbcc4000", totalGalleryPages: 22);
        string previewPage1Html = BuildPreviewPageHtml(gid: 400, startPageNumber: 21, count: 2, totalPreviewPages: 2, token: "aabbcc4000", totalGalleryPages: 22);

        using var scope = MockEngineScope.Create(_root, req =>
        {
            string url = req.RequestUri!.ToString();
            if (url == "https://e-hentai.org/g/400/aabbcc4000/")
            {
                return HtmlResponse(galleryHtml);
            }

            if (url == "https://e-hentai.org/g/400/aabbcc4000/?p=1")
            {
                return HtmlResponse(previewPage1Html);
            }

            if (url.StartsWith("https://e-hentai.org/s/tok22/400-22", StringComparison.Ordinal))
            {
                return url.Contains("?nl=node-a", StringComparison.Ordinal)
                    ? HtmlResponse(BuildGalleryPageHtml("https://cdn.example/400-22-good.jpg", "node-b"))
                    : HtmlResponse(BuildGalleryPageHtml("https://cdn.example/400-22-bad.jpg", "node-a"));
            }

            if (url.StartsWith("https://e-hentai.org/s/tok", StringComparison.Ordinal))
            {
                return HtmlResponse(BuildGalleryPageHtml($"https://cdn.example/ok-{url.GetHashCode(StringComparison.Ordinal)}.jpg", null));
            }

            if (url == "https://cdn.example/400-22-bad.jpg")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<!doctype html><html><body>blocked</body></html>", Encoding.UTF8, "text/html")
                };
            }

            if (url.StartsWith("https://cdn.example/", StringComparison.Ordinal))
            {
                return ImageResponse(pageBytes, "image/jpeg");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        // Pre-seed two persisted downloads simulating interrupted Wait & Download states before restart.
        scope.Database.PutDownloadInfo(new DownloadInfo
        {
            Gid = 400,
            Token = "aabbcc4000",
            Title = "MultiPage Test",
            State = DownloadInfo.StateWait,
            Time = 1000
        });
        scope.Database.PutDownloadInfo(new DownloadInfo
        {
            Gid = 401,
            Token = "aabbcc4001",
            Title = "Failing Second",
            State = DownloadInfo.StateDownload,
            Time = 2000
        });

        using var manager = new DownloadManager(
            scope.Engine,
            () => scope.HttpClient,
            scope.Database,
            scope.Paths,
            NullLogger<DownloadManager>.Instance,
            concurrency: 2,
            settings: scope.Settings);

        var tcs = new TaskCompletionSource<DownloadProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ProgressChanged += progress =>
        {
            if (progress.Gid == 400 && progress.State is DownloadState.Finish or DownloadState.Failed)
            {
                tcs.TrySetResult(progress);
            }
        };

        int resumed = manager.ResumePending();
        Assert.Equal(2, resumed);

        DownloadProgress completed = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(completed.State == DownloadState.Finish, $"Expected Finish but got {completed.State}: {completed.Error}");
        Assert.Equal(22, completed.Total);
        Assert.Equal(22, completed.Done);
        Assert.Contains(scope.RequestedUrls, u => u.Contains("400-22?nl=node-a", StringComparison.Ordinal));
    }

    [Fact]
    public void DownloadManager_LooksLikeHtmlPayload_DetectsUtf8BlockPagesAndPassesBinaryImages()
    {
        Assert.True(DownloadManager.LooksLikeHtmlPayload("  <!DOCTYPE html><html><body>拦截</body></html>"u8));
        Assert.True(DownloadManager.LooksLikeHtmlPayload("<html><head><title>Error</title></head></html>"u8));
        Assert.False(DownloadManager.LooksLikeHtmlPayload(CreateTinyJpeg()));
        Assert.False(DownloadManager.LooksLikeHtmlPayload(CreateBandedGrayscalePng(4, 4)));
    }

    [Fact]
    public void LocalLibraryScanner_ImportsArchiveWithNaturalPageOrder()
    {
        Assert.True(ArchiveImporter.IsSupportedArchive("sample.zip"));
        Assert.True(ArchiveImporter.IsSupportedArchive("sample.cbz"));
        Assert.True(ArchiveImporter.IsSupportedArchive("sample.rar"));
        Assert.True(ArchiveImporter.IsSupportedArchive("sample.cbr"));
        Assert.True(ArchiveImporter.IsSupportedArchive("sample.7z"));
        Assert.True(ArchiveImporter.IsSupportedArchive("sample.cb7"));
        Assert.False(ArchiveImporter.IsSupportedArchive("sample.pdf"));

        string zipPath = Path.Combine(_root, "natural-order.cbz");
        byte[] jpg1 = CreateTinyJpeg();
        byte[] png2 = CreateBandedGrayscalePng(4, 4);
        byte[] png10 = CreateBandedGrayscalePng(8, 8);

        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "sub/10.png", png10);
            WriteZipEntry(zip, "sub/2.png", png2);
            WriteZipEntry(zip, "sub/1.jpg", jpg1);
            WriteZipEntry(zip, "sub/notes.txt", "ignore"u8.ToArray());
        }

        string downloadRoot = Path.Combine(_root, "downloads");
        var scanner = new LocalLibraryScanner(NullLogger<LocalLibraryScanner>.Instance);
        string? importedFolder = scanner.ImportArchive(zipPath, downloadRoot, gid: 42, title: "(C104) [Circle] Title");

        Assert.NotNull(importedFolder);
        Assert.True(File.Exists(Path.Combine(importedFolder!, ".ehviewer")));
        Assert.Equal(jpg1, File.ReadAllBytes(Path.Combine(importedFolder, "00000000.jpg")));
        Assert.Equal(png2, File.ReadAllBytes(Path.Combine(importedFolder, "00000001.png")));
        Assert.Equal(png10, File.ReadAllBytes(Path.Combine(importedFolder, "00000002.png")));
        Assert.False(File.Exists(Path.Combine(importedFolder, "notes.txt")));

        SpiderInfo? spider = ReaderViewModel.ReadExistingProgress(Path.Combine(importedFolder, ".ehviewer"));
        Assert.NotNull(spider);
        Assert.Equal(42, spider!.Gid);
        Assert.Equal(3, spider.Pages);
    }

    [Fact]
    public void AndroidMigrationService_ImportsDbReEncryptsCookiesAndCopiesDownloads()
    {
        string srcDir = Path.Combine(_root, "android-export");
        Directory.CreateDirectory(srcDir);

        string srcEhDbPath = Path.Combine(srcDir, "eh.db");
        using (var srcDb = new EhDatabase(srcEhDbPath))
        {
            srcDb.PutDownloadInfo(new DownloadInfo
            {
                Gid = 777,
                Token = "tok777",
                Title = "Migrated Gallery",
                State = DownloadInfo.StateFinish,
                Time = 5000
            });
            srcDb.PutHistoryInfo(new HistoryInfo
            {
                Gid = 778,
                Token = "tok778",
                Title = "History Entry",
                Time = 6000
            });
        }

        string srcCookiePath = Path.Combine(srcDir, "okhttp3-cookie.db");
        SeedPlaintextOkHttpCookies(srcCookiePath, ("ipb_member_id", "123456"), ("ipb_pass_hash", "secret-hash-xyz"));

        string srcDownloadGallery = Path.Combine(srcDir, "download", "777-Migrated Gallery");
        Directory.CreateDirectory(srcDownloadGallery);
        File.WriteAllBytes(Path.Combine(srcDownloadGallery, "00000001.jpg"), CreateTinyJpeg());
        File.WriteAllText(Path.Combine(srcDownloadGallery, ".ehviewer"), "VERSION2\n777\ntok777\n0\n1\n0\n");

        AppPaths destPaths = AppPaths.CreateAt(Path.Combine(_root, "desktop-data"));
        destPaths.EnsureCreated();

        using var destDb = new EhDatabase(destPaths.EhDatabasePath);
        using var destCookies = new SqliteCookieStore(destPaths.CookieDatabasePath, DpapiCredentialProtector.Instance);
        var migration = new AndroidMigrationService(
            destDb,
            destCookies,
            destPaths,
            NullLogger<AndroidMigrationService>.Instance);

        AndroidMigrationReport report = migration.Migrate(
            ehDatabasePath: srcEhDbPath,
            cookieDatabasePath: srcCookiePath,
            androidDownloadDirectory: Path.Combine(srcDir, "download"));

        Assert.True(report.DatabaseImported);
        Assert.Equal(2, report.CookiesImported);
        Assert.Equal(1, report.GalleriesCopied);
        Assert.Equal(2, report.FilesCopied);

        Assert.Contains(destDb.GetAllDownloadInfo(), d => d.Gid == 777);
        Assert.Contains(destDb.GetAllHistoryInfo(), h => h.Gid == 778);
        Assert.True(destCookies.HasSignedIn);
        Assert.Contains("ipb_pass_hash=secret-hash-xyz", destCookies.GetCookieHeader(new Uri(EhUrl.HostE)));

        // Verify persisted cookie VALUE on disk is DPAPI-protected, never plaintext.
        string connStr = new SqliteConnectionStringBuilder { DataSource = destPaths.CookieDatabasePath, Pooling = false }.ToString();
        using (var verifyConn = new SqliteConnection(connStr))
        {
            verifyConn.Open();
            using SqliteCommand cmd = verifyConn.CreateCommand();
            cmd.CommandText = "SELECT VALUE FROM OK_HTTP_3_COOKIE WHERE NAME = 'ipb_pass_hash';";
            string? stored = cmd.ExecuteScalar() as string;
            Assert.NotNull(stored);
            Assert.NotEqual("secret-hash-xyz", stored);
        }

        Assert.True(File.Exists(Path.Combine(destPaths.DownloadDirectory, "777-Migrated Gallery", "00000001.jpg")));
    }

    [Fact]
    public async Task TagQueryFormatting_DetailViewModelUpdates_AndWindowKeyRegistryWork()
    {
        Assert.Equal("female:\"sole female\"$", TagTranslationSource.FormatTagQuery("female:sole female"));
        Assert.Equal("artist:rurudo$", TagTranslationSource.FormatTagQuery("artist:rurudo"));

        var chipWithSpace = new TagChip("sole female", "唯一女性", "female");
        Assert.Equal("female:\"sole female\"$", chipWithSpace.SearchQuery);

        var chipSingle = new TagChip("rurudo", null, "artist");
        Assert.Equal("artist:rurudo$", chipSingle.SearchQuery);

        using var scope = MockEngineScope.Create(_root, req =>
        {
            if (req.RequestUri!.ToString().Contains("?p=1", StringComparison.Ordinal))
            {
                return HtmlResponse(BuildPreviewPageHtml(gid: 500, startPageNumber: 21, count: 2, totalPreviewPages: 2));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var browse = new GalleryBrowseService(scope.Engine, scope.Settings);
        var vm = new GalleryDetailViewModel(browse);
        vm.UpdateRating(new RateGalleryParser.Result(4.75f, 18));
        Assert.Equal("4.8（18）", vm.RatingText);

        vm.UpdateComments(new GalleryCommentList(
            [new GalleryComment { Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), User = "tester", Comment = "Nice!" }],
            hasMore: false));
        Assert.Single(vm.Comments);
        Assert.Equal("tester", vm.Comments[0].User);

        await vm.LoadPreviewPageAsync(1); // Before loading a gallery, Gid <= 0 safely returns without throwing.
        Assert.Equal(0, vm.CurrentPreviewPage);

        var registry = new WindowKeyRegistry();
        Assert.True(registry.TryOpen("gid:500", out Guid firstId));
        Assert.False(registry.TryOpen("gid:500", out Guid duplicateId));
        Assert.Equal(firstId, duplicateId);
        Assert.Equal(1, registry.Count);
        Assert.True(registry.Close("gid:500"));
        Assert.Equal(0, registry.Count);
    }

    private static string BuildPreviewPageHtml(long gid, int startPageNumber, int count, int totalPreviewPages, string token = "aabbcc0000", int? totalGalleryPages = null)
    {
        int reportedPages = totalGalleryPages ?? (startPageNumber + count - 1);
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><title>Preview</title></head><body>");
        sb.AppendLine($"<script>var gid = {gid}; var token = \"{token}\"; var apiuid = 12345; var apikey = \"abcdef1234\";</script>");
        sb.AppendLine($@"<a onclick=""return popUp('https://e-hentai.org/archiver.php?gid={gid}&amp;token={token}', 400, 300)"">Archive Download</a>");
        sb.AppendLine("""
            <div class="gm">
              <div id="gd1"><div style="width:250px; height:350px; background:url(https://ehgt.org/cover.jpg)"></div></div>
              <h1 id="gn">Test Gallery</h1>
              <h1 id="gj"></h1>
              <div id="gdc"><div class="cn">Doujinshi</div></div>
              <div id="gdn">uploader</div>
            """);
        sb.AppendLine($@"  <div id=""gdd""><table><tbody><tr><td class=""gdt1"">Length:</td><td class=""gdt2"">{reportedPages} pages</td></tr></tbody></table></div>");
        sb.AppendLine("""
              <div id="rating_count">1</div>
              <div id="rating_label">Average: 4.50</div>
              <div id="taglist"><table><tbody></tbody></table></div>
            </div>
            """);
        sb.AppendLine($@"<table class=""ptt""><tbody><tr><td onclick=""sp(0)""><a href=""?p=0"">1</a></td><td onclick=""sp(1)""><a href=""?p=1"">{totalPreviewPages}</a></td><td onclick=""sp(1)"">&gt;</td></tr></tbody></table>");
        sb.AppendLine(@"<div id=""gdt"" class=""gt200"">");

        for (int i = 0; i < count; i++)
        {
            int pageNum = startPageNumber + i;
            sb.Append($"""
                <div class="gdtm" style="height:100px"><div style="width:100px; height:100px; background:transparent url(https://ehgt.org/t/{pageNum}.jpg) -0px 0 no-repeat"><a href="https://e-hentai.org/s/tok{pageNum}/{gid}-{pageNum}"><img alt="{pageNum}" src="https://ehgt.org/g/blank.gif" /></a></div></div>
                """);
        }

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static string BuildGalleryPageHtml(string imageUrl, string? skipHathKey)
    {
        string loadFailLink = string.IsNullOrEmpty(skipHathKey)
            ? string.Empty
            : $@"<a href=""#"" id=""loadfail"" onclick=""return nl('{skipHathKey}')"">Click here if the image fails loading</a>";
        return $"""
            <!DOCTYPE html><html><head><title>Page</title></head><body>
            <div id="i1"><div id="i3"><a href="https://e-hentai.org/s/next/1-2"><img id="img" src="{imageUrl}" style="width:1280px;height:879px" /></a></div><div id="i6" class="if">{loadFailLink}</div></div>
            <script type="text/javascript">var showkey="showkey123";</script>
            </body></html>
            """;
    }

    private static HttpResponseMessage HtmlResponse(string html) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        };

    private static HttpResponseMessage ImageResponse(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static byte[] CreateTinyJpeg()
    {
        using var bmp = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Bgra8888, SKAlphaType.Opaque));
        bmp.Erase(new SKColor(120, 160, 200));
        using SKData data = bmp.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private static byte[] CreateBandedGrayscalePng(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8; // 8-bit grayscale
        ihdr[9] = 0;
        WritePngChunk(output, "IHDR"u8, ihdr);

        byte[] idatPayload;
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                byte[] topRow = new byte[width + 1];
                byte[] bottomRow = new byte[width + 1];
                Array.Fill(topRow, (byte)0x20, 1, width);
                Array.Fill(bottomRow, (byte)0xE0, 1, width);
                int half = height / 2;
                for (int y = 0; y < height; y++)
                {
                    zlib.Write(y < half ? topRow : bottomRow);
                }
            }

            idatPayload = compressed.ToArray();
        }

        WritePngChunk(output, "IDAT"u8, idatPayload);
        WritePngChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, data.Length);
        stream.Write(lenBytes);
        stream.Write(type);
        stream.Write(data);

        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, crc ^ 0xFFFFFFFFu);
        stream.Write(lenBytes);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc;
    }

    private static void WriteZipEntry(ZipArchive zip, string entryName, byte[] payload)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName);
        using Stream s = entry.Open();
        s.Write(payload);
    }

    private static void SeedPlaintextOkHttpCookies(string dbPath, params (string Name, string Value)[] entries)
    {
        string connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using (SqliteCommand create = conn.CreateCommand())
        {
            create.CommandText = $"""
                CREATE TABLE {SqliteCookieStore.TableName} (
                    _id INTEGER PRIMARY KEY AUTOINCREMENT,
                    NAME TEXT,
                    VALUE TEXT,
                    EXPIRES_AT INTEGER,
                    DOMAIN TEXT,
                    PATH TEXT,
                    SECURE INTEGER,
                    HTTP_ONLY INTEGER,
                    PERSISTENT INTEGER,
                    HOST_ONLY INTEGER
                );
                """;
            create.ExecuteNonQuery();
        }

        long expires = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
        foreach ((string name, string value) in entries)
        {
            using SqliteCommand insert = conn.CreateCommand();
            insert.CommandText = $"""
                INSERT INTO {SqliteCookieStore.TableName}
                    (NAME, VALUE, EXPIRES_AT, DOMAIN, PATH, SECURE, HTTP_ONLY, PERSISTENT, HOST_ONLY)
                VALUES
                    ($name, $value, $expires, 'e-hentai.org', '/', 0, 0, 1, 0);
                """;
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$value", value);
            insert.Parameters.AddWithValue("$expires", expires);
            insert.ExecuteNonQuery();
        }
    }

    private sealed class MockEngineScope : IDisposable
    {
        private readonly ConcurrentQueue<string> _urls;

        private MockEngineScope(
            AppPaths paths,
            EhDatabase database,
            HttpClient httpClient,
            EhClientSettings settings,
            EhEngine engine,
            ConcurrentQueue<string> urls)
        {
            Paths = paths;
            Database = database;
            HttpClient = httpClient;
            Settings = settings;
            Engine = engine;
            _urls = urls;
        }

        public AppPaths Paths { get; }
        public EhDatabase Database { get; }
        public HttpClient HttpClient { get; }
        public EhClientSettings Settings { get; }
        public EhEngine Engine { get; }
        public string[] RequestedUrls => [.. _urls];

        public static MockEngineScope Create(string root, Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            AppPaths paths = AppPaths.CreateAt(Path.Combine(root, "app-" + Guid.NewGuid().ToString("N")));
            paths.EnsureCreated();

            var database = new EhDatabase(paths.EhDatabasePath);
            var settings = new EhClientSettings();
            var urls = new ConcurrentQueue<string>();
            var handler = new DelegateHandler(req =>
            {
                urls.Enqueue(req.RequestUri?.ToString() ?? string.Empty);
                return responder(req);
            });
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var engine = new EhEngine(client, new NullCookieStore(), database, new EhFilter(database), settings);

            return new MockEngineScope(paths, database, client, settings, engine, urls);
        }

        public void Dispose()
        {
            HttpClient.Dispose();
            Database.Dispose();
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }

    private sealed class NullCookieStore : ICookieStore
    {
        public bool HasSignedIn => false;
        public void AddCookie(EhCookie cookie) { }
        public void SaveFromResponse(Uri requestUrl, IEnumerable<string> setCookieHeaders) { }
        public IReadOnlyList<EhCookie> LoadForRequest(Uri requestUrl) => [];
        public IReadOnlyList<EhCookie> GetCookies(Uri url) => [];
        public string GetCookieHeader(Uri url) => string.Empty;
        public bool Contains(Uri url, string name) => false;
        public void SignOut() { }
        public void Clear() { }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Network;
using EhViewer.Core.Spider;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class Batch4DownloadsFullTests : IDisposable
{
    private readonly string _root;

    public Batch4DownloadsFullTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "EhViewerBatch4Tests", Guid.NewGuid().ToString("N"));
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
        catch
        {
        }
    }

    [Fact]
    public void PowerStatus_DoesNotThrowOnWindows()
    {
        _ = WindowsPowerStatus.IsOnBatteryOrSaver();
    }

    [Fact]
    public async Task DownloadManager_PauseOnBattery_PausesTaskAndPreservesState()
    {
        using var env = CreateEnv(static _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var settings = new DesktopSettings { DownloadPauseOnBattery = true };
        using var manager = new DownloadManager(
            env.Engine,
            () => env.HttpClient,
            env.Database,
            env.Paths,
            NullLogger<DownloadManager>.Instance,
            concurrency: 1,
            settings: env.ClientSettings,
            resolveDownloadRoot: () => env.Paths.GetEffectiveDownloadDirectory(settings),
            resolveDesktopSettings: () => settings,
            isOnBattery: static () => true);

        var pausedTcs = new TaskCompletionSource<DownloadProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ProgressChanged += progress =>
        {
            if (progress.Gid == 1001 && progress.IsPaused)
            {
                pausedTcs.TrySetResult(progress);
            }
        };

        Assert.True(manager.Enqueue(new GalleryInfo { Gid = 1001, Token = "tok1001", Title = "Battery Test", Pages = 3 }, "漫画"));

        DownloadProgress paused = await pausedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadState.None, paused.State);
        Assert.True(paused.IsPaused);
        Assert.Equal("电池供电已暂停", paused.Error);
        Assert.Equal("漫画", paused.Label);

        DownloadInfo? row = env.Database.GetAllDownloadInfo().FirstOrDefault(r => r.Gid == 1001);
        Assert.NotNull(row);
        Assert.Equal("漫画", row!.Label);
    }

    [Fact]
    public async Task DownloadManager_HttpRangeResumeAndCustomDirectory_ResumesPartFile()
    {
        byte[] fullJpeg = CreateJpegBytes(16, 16);
        Assert.True(fullJpeg.Length > 40);
        int splitOffset = fullJpeg.Length / 2;

        bool sawRangeHeader = false;
        using var env = CreateEnv(request =>
        {
            string url = request.RequestUri!.ToString();
            if (url.Contains("/g/2001/aaabbb2001/", StringComparison.Ordinal))
            {
                return HtmlResponse(BuildDetailHtml(2001, "aaabbb2001", "Custom Dir Gallery", 1));
            }

            if (url.Contains("/s/p0/2001-1", StringComparison.Ordinal))
            {
                return HtmlResponse(BuildPageHtml("https://img.example.test/2001-0.jpg"));
            }

            if (url == "https://img.example.test/2001-0.jpg")
            {
                if (request.Headers.Range?.Ranges.FirstOrDefault() is { From: long from } && from > 0)
                {
                    sawRangeHeader = true;
                    byte[] slice = fullJpeg[(int)from..];
                    var content = new ByteArrayContent(slice);
                    content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    content.Headers.ContentRange = new ContentRangeHeaderValue(from, fullJpeg.Length - 1, fullJpeg.Length);
                    return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
                }

                return ByteResponse(fullJpeg, "image/jpeg");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        string customDir = Path.Combine(_root, "custom-downloads");
        var settings = new DesktopSettings { CustomDownloadDirectory = customDir };

        // Pre-seed a half-downloaded .part file in the custom download directory.
        var galleryInfo = new GalleryInfo { Gid = 2001, Token = "aaabbb2001", Title = "Custom Dir Gallery", Pages = 1 };
        string galleryFolder = DownloadManager.BuildGalleryFolder(customDir, galleryInfo);
        Directory.CreateDirectory(galleryFolder);
        string partPath = Path.ChangeExtension(DownloadManager.BuildImagePath(galleryFolder, 0), ".part");
        await File.WriteAllBytesAsync(partPath, fullJpeg[..splitOffset]);

        // Pre-check: .part files must NOT count as completed images in either DownloadManager or LocalLibraryScanner.
        Assert.Equal(0, DownloadManager.CountExistingImages(galleryFolder));
        Assert.Equal(0, LocalLibraryScanner.CountImages(galleryFolder));

        using var manager = new DownloadManager(
            env.Engine,
            () => env.HttpClient,
            env.Database,
            env.Paths,
            NullLogger<DownloadManager>.Instance,
            concurrency: 1,
            settings: env.ClientSettings,
            resolveDownloadRoot: () => env.Paths.GetEffectiveDownloadDirectory(settings),
            resolveDesktopSettings: () => settings,
            isOnBattery: static () => false);

        var doneTcs = new TaskCompletionSource<DownloadProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ProgressChanged += p =>
        {
            if (p.Gid == 2001 && p.State is DownloadState.Finish or DownloadState.Failed)
            {
                doneTcs.TrySetResult(p);
            }
        };

        Assert.True(manager.Enqueue(galleryInfo, "精翻"));
        DownloadProgress done = await doneTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(done.State == DownloadState.Finish, $"Expected Finish but got {done.State}: {done.Error}");
        Assert.Equal(1, done.Done);
        Assert.Equal(1, done.Total);
        Assert.Equal("精翻", done.Label);
        Assert.True(sawRangeHeader, "Expected Range header when resuming existing .part file.");
        Assert.False(File.Exists(partPath));

        string finalImage = Path.Combine(galleryFolder, "00000000.jpg");
        Assert.True(File.Exists(finalImage));
        Assert.Equal(fullJpeg, await File.ReadAllBytesAsync(finalImage));
        Assert.Equal(1, DownloadManager.CountExistingImages(galleryFolder));
    }

    [Fact]
    public async Task DownloadManager_TimeoutSeconds_AbortsHungImageFetchCleanly()
    {
        using var env = CreateAsyncEnv(async (request, ct) =>
        {
            string url = request.RequestUri!.ToString();
            if (url.Contains("/g/3001/aaabbb3001/", StringComparison.Ordinal))
            {
                return HtmlResponse(BuildDetailHtml(3001, "aaabbb3001", "Slow Gallery", 1));
            }

            if (url.Contains("/s/p0/3001-1", StringComparison.Ordinal))
            {
                return HtmlResponse(BuildPageHtml("https://img.example.test/hang.jpg"));
            }

            if (url == "https://img.example.test/hang.jpg")
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var settings = new DesktopSettings { DownloadTimeoutSeconds = 5 };
        using var manager = new DownloadManager(
            env.Engine,
            () => env.HttpClient,
            env.Database,
            env.Paths,
            NullLogger<DownloadManager>.Instance,
            concurrency: 1,
            settings: env.ClientSettings,
            resolveDownloadRoot: () => env.Paths.DownloadDirectory,
            resolveDesktopSettings: () => settings,
            isOnBattery: static () => false);

        var tcs = new TaskCompletionSource<DownloadProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ProgressChanged += p =>
        {
            if (p.Gid == 3001 && p.State is DownloadState.Finish or DownloadState.Failed)
            {
                tcs.TrySetResult(p);
            }
        };

        Assert.True(manager.Enqueue(new GalleryInfo { Gid = 3001, Token = "aaabbb3001", Title = "Slow Gallery", Pages = 1 }));
        DownloadProgress finished = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(DownloadState.Finish, finished.State);
        Assert.Equal(0, finished.Done);
    }

    [Fact]
    public async Task DownloadManager_PauseResumeStartAllPauseAllAndDelete_WorkEndToEnd()
    {
        byte[] img = CreateJpegBytes(8, 8);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var env = CreateAsyncEnv(async (request, ct) =>
        {
            string url = request.RequestUri!.ToString();
            if (url.Contains("/g/4001/aaabbb4001/", StringComparison.Ordinal))
            {
                await gate.Task.WaitAsync(ct);
                return HtmlResponse(BuildDetailHtml(4001, "aaabbb4001", "Controllable", 1));
            }

            if (url.Contains("/s/p0/4001-1", StringComparison.Ordinal))
            {
                return HtmlResponse(BuildPageHtml("https://img.example.test/4001-0.jpg"));
            }

            if (url == "https://img.example.test/4001-0.jpg")
            {
                return ByteResponse(img, "image/jpeg");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var manager = new DownloadManager(
            env.Engine,
            () => env.HttpClient,
            env.Database,
            env.Paths,
            NullLogger<DownloadManager>.Instance,
            concurrency: 1,
            settings: env.ClientSettings,
            isOnBattery: static () => false);

        var pausedTcs = new TaskCompletionSource<DownloadProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishTcs = new TaskCompletionSource<DownloadProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ProgressChanged += p =>
        {
            if (p.Gid == 4001 && p.IsPaused)
            {
                pausedTcs.TrySetResult(p);
            }
            else if (p.Gid == 4001 && p.State == DownloadState.Finish)
            {
                finishTcs.TrySetResult(p);
            }
        };

        var info = new GalleryInfo { Gid = 4001, Token = "aaabbb4001", Title = "Controllable", Pages = 1 };
        Assert.True(manager.Enqueue(info, "待看"));

        // PauseAll while waiting/running
        Assert.Equal(1, manager.PauseAll());
        DownloadProgress paused = await pausedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(paused.IsPaused);
        Assert.Equal("待看", paused.Label);

        // AssignLabel preserves across pause and restart
        manager.AssignLabel(4001, "收藏级");
        gate.TrySetResult();

        Assert.Equal(1, manager.StartAll());
        DownloadProgress done = await finishTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(done.State == DownloadState.Finish, $"Expected Finish but got {done.State}: {done.Error}");
        Assert.Equal("收藏级", done.Label);

        string expectedFolder = DownloadManager.BuildGalleryFolder(env.Paths.DownloadDirectory, info);
        Assert.True(Directory.Exists(expectedFolder));

        // Delete with files = true removes folder + DB entry
        Assert.Equal(1, manager.DeleteDownloads([4001], deleteFiles: true));
        Assert.False(Directory.Exists(expectedFolder));
        Assert.Empty(env.Database.GetAllDownloadInfo());
    }

    [Fact]
    public void DownloadManager_CsvExportImportAndInvalidCleanup_BehaveAccurately()
    {
        using var env = CreateEnv(static _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var manager = new DownloadManager(
            env.Engine,
            () => env.HttpClient,
            env.Database,
            env.Paths,
            NullLogger<DownloadManager>.Instance,
            concurrency: 1,
            settings: env.ClientSettings,
            isOnBattery: static () => false);

        env.Database.PutDownloadInfo(new DownloadInfo
        {
            Gid = 5001,
            Token = "tok5001",
            Title = "Title, With \"Quotes\" & Comma",
            Category = 2,
            Pages = 12,
            State = DownloadInfo.StateFinish,
            Finished = 12,
            Total = 12,
            Time = 1700000000000L,
            Label = "精华"
        });

        string csv = manager.ExportDownloadsCsv();
        Assert.Contains("5001", csv);

        // Clear DB and re-import RFC-4180 CSV + upstream GalleryInfo.ToCsvLine row
        env.Database.RemoveDownloadInfo(5001);
        var upstreamGallery = new GalleryInfo
        {
            Gid = 5002,
            Token = "tok5002",
            Title = "Upstream CSV Row",
            Pages = 4
        };
        string combinedCsv = csv + Environment.NewLine + upstreamGallery.ToCsvLine() + Environment.NewLine;

        int imported = manager.ImportDownloadsCsv(combinedCsv);
        Assert.Equal(2, imported);

        List<DownloadInfo> rows = env.Database.GetAllDownloadInfo();
        DownloadInfo? r1 = rows.FirstOrDefault(r => r.Gid == 5001);
        DownloadInfo? r2 = rows.FirstOrDefault(r => r.Gid == 5002);
        Assert.NotNull(r1);
        Assert.Equal("Title, With \"Quotes\" & Comma", r1!.Title);
        Assert.Equal("精华", r1.Label);
        Assert.NotNull(r2);
        Assert.Contains(env.Database.GetAllDownloadLabels(), l => l.Label == "精华");

        // Invalid download detection: empty folder + incomplete folder + valid folder + _ignored folder
        string emptyDir = Path.Combine(env.Paths.DownloadDirectory, "6001-Empty");
        Directory.CreateDirectory(emptyDir);
        env.Database.PutDownloadDirname(6001, emptyDir);

        string incompleteDir = Path.Combine(env.Paths.DownloadDirectory, "6002-Partial");
        Directory.CreateDirectory(incompleteDir);
        File.WriteAllBytes(Path.Combine(incompleteDir, "00000000.jpg"), CreateJpegBytes(4, 4));
        using (FileStream s = File.Create(Path.Combine(incompleteDir, ".ehviewer")))
        {
            new SpiderInfo { Gid = 6002, Token = "tok", Pages = 5 }.Write(s);
        }

        string validDir = Path.Combine(env.Paths.DownloadDirectory, "6003-Complete");
        Directory.CreateDirectory(validDir);
        File.WriteAllBytes(Path.Combine(validDir, "00000000.jpg"), CreateJpegBytes(4, 4));
        using (FileStream s = File.Create(Path.Combine(validDir, ".ehviewer")))
        {
            new SpiderInfo { Gid = 6003, Token = "tok", Pages = 1 }.Write(s);
        }

        Directory.CreateDirectory(Path.Combine(env.Paths.DownloadDirectory, "_temp"));

        IReadOnlyList<InvalidDownloadCandidate> candidates = manager.FindInvalidDownloads();
        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, c => c.Gid == 6001 && c.ActualImages == 0);
        Assert.Contains(candidates, c => c.Gid == 6002 && c.ActualImages == 1 && c.ExpectedPages == 5);

        int cleaned = manager.CleanInvalidDownloads(candidates.Select(static c => c.DirectoryPath));
        Assert.Equal(2, cleaned);
        Assert.False(Directory.Exists(emptyDir));
        Assert.False(Directory.Exists(incompleteDir));
        Assert.True(Directory.Exists(validDir));
        Assert.Null(env.Database.GetDownloadDirname(6001));
    }

    [Fact]
    public void DownloadsViewModel_LabelsFilteringSortingAndBatchOps_WorkCorrectly()
    {
        using var env = CreateEnv(static _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var manager = new DownloadManager(
            env.Engine,
            () => env.HttpClient,
            env.Database,
            env.Paths,
            NullLogger<DownloadManager>.Instance,
            concurrency: 1,
            settings: env.ClientSettings,
            isOnBattery: static () => false);
        var scanner = new LocalLibraryScanner(NullLogger<LocalLibraryScanner>.Instance);

        string dirA = Path.Combine(env.Paths.DownloadDirectory, "7001-Beta Gallery");
        Directory.CreateDirectory(dirA);
        File.WriteAllBytes(Path.Combine(dirA, "00000000.jpg"), CreateJpegBytes(4, 4));

        string dirB = Path.Combine(env.Paths.DownloadDirectory, "7002-Alpha Gallery");
        Directory.CreateDirectory(dirB);
        File.WriteAllBytes(Path.Combine(dirB, "00000000.jpg"), CreateJpegBytes(4, 4));

        env.Database.PutDownloadInfo(new DownloadInfo
        {
            Gid = 7001,
            Token = "a",
            Title = "Beta Gallery",
            Pages = 1,
            Finished = 1,
            Total = 1,
            State = DownloadInfo.StateFinish,
            Time = 1000
        });
        env.Database.PutDownloadInfo(new DownloadInfo
        {
            Gid = 7002,
            Token = "b",
            Title = "Alpha Gallery",
            Pages = 1,
            Finished = 0,
            Total = 1,
            State = DownloadInfo.StateNone,
            Time = 2000
        });

        using var vm = new DownloadsViewModel(manager, scanner, env.Paths, env.Database);
        Assert.True(vm.AddLabel("标签A"));
        vm.Reload();

        Assert.Equal(2, vm.ActiveRows.Count);
        Assert.Equal(2, vm.LocalGalleries.Count);

        // Multi-select + batch assign label
        vm.SelectAllFiltered();
        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal(2, vm.AssignLabelToSelected("标签A"));
        Assert.All(vm.ActiveRows, r => Assert.Equal("标签A", r.Label));

        // Rename label cascades everywhere
        DownloadLabel? labelObj = vm.GetLabels().FirstOrDefault(l => l.Label == "标签A");
        Assert.NotNull(labelObj);
        Assert.True(vm.RenameLabel(labelObj!, "标签B"));
        Assert.All(vm.ActiveRows, r => Assert.Equal("标签B", r.Label));

        // TitleAscending sort puts Alpha (7002) before Beta (7001)
        vm.SelectedLibrarySortIndex = 2;
        Assert.Equal(7002, vm.LocalGalleries[0].Gid);
        Assert.Equal(7001, vm.LocalGalleries[1].Gid);

        // State filter isolates paused (7002)
        vm.SelectedStateFilter = "已暂停";
        Assert.Single(vm.ActiveRows);
        Assert.Equal(7002, vm.ActiveRows[0].Gid);

        // Delete label clears assignments
        vm.SelectedStateFilter = "全部状态";
        DownloadLabel updatedLabel = vm.GetLabels().First(l => l.Label == "标签B");
        vm.DeleteLabel(updatedLabel);
        Assert.All(vm.ActiveRows, r => Assert.Null(r.Label));
    }

    [Fact]
    public async Task DownloadsViewModel_FilterAndSortUseCachedLibraryWithoutDiskRescan_AndReloadLibraryAsyncPicksUpChanges()
    {
        using var env = CreateEnv(static _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var manager = new DownloadManager(
            env.Engine,
            () => env.HttpClient,
            env.Database,
            env.Paths,
            NullLogger<DownloadManager>.Instance,
            concurrency: 1,
            settings: env.ClientSettings,
            isOnBattery: static () => false);
        var scanner = new LocalLibraryScanner(NullLogger<LocalLibraryScanner>.Instance);

        string dirA = Path.Combine(env.Paths.DownloadDirectory, "7001-Beta Gallery");
        Directory.CreateDirectory(dirA);
        File.WriteAllBytes(Path.Combine(dirA, "00000000.jpg"), CreateJpegBytes(4, 4));

        string dirB = Path.Combine(env.Paths.DownloadDirectory, "7002-Alpha Gallery");
        Directory.CreateDirectory(dirB);
        File.WriteAllBytes(Path.Combine(dirB, "00000000.jpg"), CreateJpegBytes(4, 4));

        using var vm = new DownloadsViewModel(manager, scanner, env.Paths, env.Database);
        vm.Reload();
        Assert.Equal(2, vm.LocalGalleries.Count);

        // Add a 3rd gallery on disk after initial scan; filter/sort must operate strictly in memory.
        string dirC = Path.Combine(env.Paths.DownloadDirectory, "7003-Gamma Gallery");
        Directory.CreateDirectory(dirC);
        File.WriteAllBytes(Path.Combine(dirC, "00000000.jpg"), CreateJpegBytes(4, 4));

        vm.SelectedLibrarySortIndex = 2;
        Assert.Equal(2, vm.LocalGalleries.Count);
        Assert.Equal(7002, vm.LocalGalleries[0].Gid);

        vm.SearchKeyword = "Alpha";
        Assert.Single(vm.LocalGalleries);
        Assert.Equal(7002, vm.LocalGalleries[0].Gid);

        vm.SearchKeyword = string.Empty;
        Assert.Equal(2, vm.LocalGalleries.Count);

        // Explicit async reload scans off-thread and picks up the new gallery.
        await vm.ReloadLibraryAsync();
        Assert.Equal(3, vm.LocalGalleries.Count);
        Assert.Contains(vm.LocalGalleries, static g => g.Gid == 7003);
    }

    private TestEnv CreateEnv(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        CreateAsyncEnv((req, _) => Task.FromResult(handler(req)));

    private TestEnv CreateAsyncEnv(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        string appRoot = Path.Combine(_root, "app-" + Guid.NewGuid().ToString("N"));
        AppPaths paths = AppPaths.CreateAt(appRoot);
        var db = new EhDatabase(paths.EhDatabasePath);
        var filter = new EhFilter(db);
        var clientSettings = new EhClientSettings();
        var cookies = new SqliteCookieStore(paths.CookieDatabasePath);
        var httpClient = new HttpClient(new DelegateHandler(handler));
        var engine = new EhEngine(httpClient, cookies, db, filter, clientSettings);
        return new TestEnv(paths, db, cookies, filter, clientSettings, httpClient, engine);
    }

    private static string BuildDetailHtml(long gid, string token, string title, int pages) =>
        $"""
        <!DOCTYPE html><html><head><title>{title}</title></head><body>
        <script>var gid = {gid}; var token = "{token}"; var apiuid = 12345; var apikey = "abcdef1234";</script>
        <a onclick="return popUp('https://e-hentai.org/archiver.php?gid={gid}&amp;token={token}', 400, 300)">Archive Download</a>
        <div class="gm">
          <div id="gd1"><div style="width:250px;height:350px;background:url(https://ehgt.org/c.jpg)"></div></div>
          <h1 id="gn">{title}</h1><h1 id="gj"></h1>
          <div id="gdc"><div class="cn">Doujinshi</div></div>
          <div id="gdn">uploader</div>
          <div id="gdd"><table><tbody><tr><td class="gdt1">Length:</td><td class="gdt2">{pages} pages</td></tr></tbody></table></div>
          <div id="rating_count">1</div><div id="rating_label">Average: 4.50</div>
          <div id="taglist"><table><tbody></tbody></table></div>
        </div>
        <table class="ptt"><tbody><tr><td onclick="sp(0)"><a href="?p=0">1</a></td><td onclick="sp(0)">&gt;</td></tr></tbody></table>
        <div id="gdt" class="gt200">
          <div class="gdtm" style="height:100px"><div style="width:100px;height:100px;background:transparent url(https://ehgt.org/t/1.jpg) -0px 0 no-repeat"><a href="https://e-hentai.org/s/p0/{gid}-1"><img alt="1" src="https://ehgt.org/g/blank.gif" /></a></div></div>
        </div></body></html>
        """;

    private static string BuildPageHtml(string imageUrl) =>
        $"""
        <!DOCTYPE html><html><head><title>Page</title></head><body>
        <div id="i1"><div id="i3"><a href="https://e-hentai.org/s/next/1-2"><img id="img" src="{imageUrl}" style="width:1280px;height:879px" /></a></div><div id="i6" class="if"></div></div>
        <script type="text/javascript">var showkey="showkey123";</script>
        </body></html>
        """;

    private static HttpResponseMessage HtmlResponse(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage ByteResponse(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static byte[] CreateJpegBytes(int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        bmp.Erase(new SKColor(90, 140, 210));
        using SKData data = bmp.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private sealed class TestEnv(
        AppPaths paths,
        EhDatabase database,
        SqliteCookieStore cookies,
        EhFilter filter,
        EhClientSettings clientSettings,
        HttpClient httpClient,
        EhEngine engine) : IDisposable
    {
        public AppPaths Paths => paths;
        public EhDatabase Database => database;
        public EhFilter Filter => filter;
        public EhClientSettings ClientSettings => clientSettings;
        public HttpClient HttpClient => httpClient;
        public EhEngine Engine => engine;

        public void Dispose()
        {
            httpClient.Dispose();
            cookies.Dispose();
            database.Dispose();
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}

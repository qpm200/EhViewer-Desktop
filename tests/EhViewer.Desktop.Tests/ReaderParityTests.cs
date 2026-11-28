using System.Net;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Network;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Reader;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class ReaderParityTests : DesktopTestBase
{
    private readonly string _root;

    public ReaderParityTests()
    {
        _root = CreateTempDirectory("eh-reader-parity");
    }

    [Fact]
    public void ComputeDualPageSpread_HandlesCoverSoloAndRtlAndTrailingOddPage()
    {
        // Cover solo enabled: page 0 is standalone
        DualPageSpread cover = ReaderViewModel.ComputeDualPageSpread(0, 6, dualPageMode: true, firstAlone: true, isRightToLeft: false);
        Assert.False(cover.IsSplitSpread);
        Assert.Equal(0, cover.PrimaryPage);
        Assert.Equal(-1, cover.SecondaryPage);
        Assert.Equal(0, cover.LeftPage);
        Assert.Equal(-1, cover.RightPage);

        // LTR pages 1..2 -> Left=1, Right=2
        DualPageSpread ltr = ReaderViewModel.ComputeDualPageSpread(2, 6, dualPageMode: true, firstAlone: true, isRightToLeft: false);
        Assert.True(ltr.IsSplitSpread);
        Assert.Equal(1, ltr.PrimaryPage);
        Assert.Equal(2, ltr.SecondaryPage);
        Assert.Equal(1, ltr.LeftPage);
        Assert.Equal(2, ltr.RightPage);

        // RTL pages 1..2 -> Left=2, Right=1
        DualPageSpread rtl = ReaderViewModel.ComputeDualPageSpread(1, 6, dualPageMode: true, firstAlone: true, isRightToLeft: true);
        Assert.True(rtl.IsSplitSpread);
        Assert.Equal(1, rtl.PrimaryPage);
        Assert.Equal(2, rtl.SecondaryPage);
        Assert.Equal(2, rtl.LeftPage);
        Assert.Equal(1, rtl.RightPage);

        // Cover solo disabled: pages 0..1 pair up immediately
        DualPageSpread noCoverSolo = ReaderViewModel.ComputeDualPageSpread(0, 5, dualPageMode: true, firstAlone: false, isRightToLeft: false);
        Assert.True(noCoverSolo.IsSplitSpread);
        Assert.Equal(0, noCoverSolo.LeftPage);
        Assert.Equal(1, noCoverSolo.RightPage);

        // Trailing single odd page (index 4 in 5-page gallery with firstAlone=false) is unsplit
        DualPageSpread tailOdd = ReaderViewModel.ComputeDualPageSpread(4, 5, dualPageMode: true, firstAlone: false, isRightToLeft: false);
        Assert.False(tailOdd.IsSplitSpread);
        Assert.Equal(4, tailOdd.PrimaryPage);
        Assert.Equal(-1, tailOdd.SecondaryPage);
    }

    [Fact]
    public async Task NextPageAsync_PreservesSingleStepWhenDualDisabledAndStepsSpreadWhenDualEnabled()
    {
        using var env = CreateEnv();
        using var cache = new ReaderPageCache(1024 * 1024);
        using var engine = new ReaderEngine(
            (_, _) => Task.FromResult(Array.Empty<byte>()),
            cache,
            NullLogger<ReaderEngine>.Instance)
        {
            PageCount = 6
        };
        var fetcher = new ReaderImageFetcher(env.Engine, () => env.HttpClient, NullLogger<ReaderImageFetcher>.Instance);
        var browse = new GalleryBrowseService(env.Engine, env.ClientSettings);

        using var vm = new ReaderViewModel(
            engine,
            fetcher,
            browse,
            env.ClientSettings,
            env.Paths,
            NullLogger<ReaderViewModel>.Instance,
            ownsEngine: false,
            resolveDesktopSettings: static () => new DesktopSettings { KeepScreenOn = false })
        {
            PageCount = 6
        };

        // Even in RTL, DualPageMode=false keeps NextPageAsync (+1) and PreviousPageAsync (-1).
        vm.LayoutIndex = (int)ReaderLayout.RightToLeft;
        vm.DualPageMode = false;
        await vm.NextPageAsync();
        Assert.Equal(1, vm.CurrentPage);
        await vm.PreviousPageAsync();
        Assert.Equal(0, vm.CurrentPage);

        // With DualPageMode=true and DualPageFirstAlone=true: 0 -> 1 -> 3 -> 5 -> (null) and back 5 -> 3 -> 1 -> 0
        vm.DualPageMode = true;
        vm.DualPageFirstAlone = true;
        await vm.NextPageAsync();
        Assert.Equal(1, vm.CurrentPage);
        await vm.NextPageAsync();
        Assert.Equal(3, vm.CurrentPage);
        await vm.NextPageAsync();
        Assert.Equal(5, vm.CurrentPage);
        await vm.PreviousPageAsync();
        Assert.Equal(3, vm.CurrentPage);
        await vm.PreviousPageAsync();
        Assert.Equal(1, vm.CurrentPage);
        await vm.PreviousPageAsync();
        Assert.Equal(0, vm.CurrentPage);
    }

    [Fact]
    public async Task DualPageEngineLeases_SurviveCachePressureWithoutPrematureBitmapDispose()
    {
        byte[] img0 = CreatePngBytes(64, 64, new SKColor(200, 10, 10));
        byte[] img1 = CreatePngBytes(64, 64, new SKColor(10, 200, 10));
        byte[] img2 = CreatePngBytes(64, 64, new SKColor(10, 10, 200));

        // Cache budget fits only ~1 bitmap (64*64*4 = 16384 bytes) so eviction attempts immediately fire
        using var cache = new ReaderPageCache(20_000);
        using var engine = new ReaderEngine(
            (idx, _) => Task.FromResult(idx switch { 0 => img0, 1 => img1, _ => img2 }),
            cache,
            NullLogger<ReaderEngine>.Instance)
        {
            PageCount = 3
        };

        using ReaderPageContent? p0 = await engine.GetPageAsync(0);
        using ReaderPageContent? p1 = await engine.GetPageAsync(1);
        Assert.NotNull(p0);
        Assert.NotNull(p1);

        // Inserting page 2 overflows budget; pinned leases p0 and p1 must stay valid and readable!
        using ReaderPageContent? p2 = await engine.GetPageAsync(2);
        Assert.NotNull(p2);

        Assert.Equal(64, p0!.Bitmap.Width);
        Assert.Equal(64, p1!.Bitmap.Width);
        Assert.Equal(new SKColor(200, 10, 10), p0.Bitmap.GetPixel(4, 4));
        Assert.Equal(new SKColor(10, 200, 10), p1.Bitmap.GetPixel(4, 4));
    }

    [Fact]
    public void Decoder_SupportsForceSrgbAndDecodeRegionSubsampling()
    {
        // Wide bitmap spanning two distinct colored halves (4000x100)
        byte[] raw = CreateSplitPngBytes(4000, 100, new SKColor(220, 40, 40), new SKColor(40, 220, 40));

        using SKBitmap? srgbDecoded = ReaderImageDecoder.Decode(raw, maxWidth: 4096, maxHeight: 4096, forceSrgb: true);
        Assert.NotNull(srgbDecoded);
        Assert.Equal(SKColorType.Bgra8888, srgbDecoded!.ColorType);
        Assert.NotNull(srgbDecoded.ColorSpace);
        Assert.True(srgbDecoded.ColorSpace!.IsSrgb);

        // Region crop strictly on right half (x = 2200..3800) with downscale cap
        var crop = new SKRectI(2200, 0, 3800, 100);
        using SKBitmap? regionDecoded = ReaderImageDecoder.DecodeRegion(raw, crop, maxEdge: 800, forceSrgb: true);
        Assert.NotNull(regionDecoded);
        Assert.True(regionDecoded!.Width <= 800);
        SKColor rightSample = regionDecoded.GetPixel(regionDecoded.Width / 2, regionDecoded.Height / 2);
        Assert.True(rightSample.Green > rightSample.Red + 100);
    }

    [Fact]
    public void Bookmarks_ToggleAndRemovePersistInEhDatabase()
    {
        using var env = CreateEnv();
        using var cache = new ReaderPageCache(1024 * 1024);
        using var engine = new ReaderEngine((_, _) => Task.FromResult(Array.Empty<byte>()), cache, NullLogger<ReaderEngine>.Instance);
        var fetcher = new ReaderImageFetcher(env.Engine, () => env.HttpClient, NullLogger<ReaderImageFetcher>.Instance);
        var browse = new GalleryBrowseService(env.Engine, env.ClientSettings);

        using var vm = new ReaderViewModel(
            engine,
            fetcher,
            browse,
            env.ClientSettings,
            env.Paths,
            NullLogger<ReaderViewModel>.Instance,
            ownsEngine: false,
            resolveDesktopSettings: static () => new DesktopSettings { KeepScreenOn = false },
            database: env.Database);

        env.Database.PutBookmark(new BookmarkInfo
        {
            Gid = 9101,
            Token = "abc123def4",
            Title = "Bookmark Test Gallery",
            Page = 3,
            Time = 1700000000000
        });
        vm.RefreshBookmarks();

        ReaderBookmarkItem item = Assert.Single(vm.Bookmarks);
        Assert.Equal(9101, item.Gid);
        Assert.Equal(3, item.Page);
        Assert.Contains("P4", item.PageDisplay, StringComparison.Ordinal);

        vm.RemoveBookmark(9101);
        Assert.Empty(vm.Bookmarks);
    }

    [Fact]
    public void ReaderViewModel_AppliesDesktopSettingsDefaultsAndWakeLockLifecycle()
    {
        using var env = CreateEnv();
        using var cache = new ReaderPageCache(1024 * 1024);
        using var engine = new ReaderEngine((_, _) => Task.FromResult(Array.Empty<byte>()), cache, NullLogger<ReaderEngine>.Instance);
        var fetcher = new ReaderImageFetcher(env.Engine, () => env.HttpClient, NullLogger<ReaderImageFetcher>.Instance);
        var browse = new GalleryBrowseService(env.Engine, env.ClientSettings);

        int locksBefore = WindowsKeepAwake.ActiveLockCount;
        var customSettings = new DesktopSettings
        {
            PreloadCount = 7,
            DefaultReaderLayout = (int)ReaderLayout.RightToLeft,
            DefaultReaderScale = (int)ReaderScale.FitWidth,
            DualPageMode = true,
            DualPageFirstAlone = false,
            ContinuousScroll = false,
            MaxDecodeEdge = 6144,
            ForceSrgb = true,
            ShowPageNumber = false,
            KeepScreenOn = true,
            PageTurnByWheel = false,
            HideReaderChromeInFullscreen = false
        };

        var vm = new ReaderViewModel(
            engine,
            fetcher,
            browse,
            env.ClientSettings,
            env.Paths,
            NullLogger<ReaderViewModel>.Instance,
            ownsEngine: false,
            resolveDesktopSettings: () => customSettings);

        Assert.Equal(7, vm.PreloadCount);
        Assert.Equal(7, engine.PreloadCount);
        Assert.Equal((int)ReaderLayout.RightToLeft, vm.LayoutIndex);
        Assert.Equal((int)ReaderScale.FitWidth, vm.ScaleIndex);
        Assert.True(vm.DualPageMode);
        Assert.False(vm.DualPageFirstAlone);
        Assert.Equal(6144, vm.MaxDecodeEdge);
        Assert.Equal(6144, engine.MaxDecodeEdge);
        Assert.True(vm.ForceSrgb);
        Assert.True(engine.ForceSrgb);
        Assert.False(vm.ShowPageNumber);
        Assert.True(vm.KeepScreenOn);
        Assert.False(vm.PageTurnByWheel);
        Assert.False(vm.HideReaderChromeInFullscreen);
        Assert.Equal(locksBefore + 1, WindowsKeepAwake.ActiveLockCount);

        vm.KeepScreenOn = false;
        Assert.Equal(locksBefore, WindowsKeepAwake.ActiveLockCount);

        vm.KeepScreenOn = true;
        Assert.Equal(locksBefore + 1, WindowsKeepAwake.ActiveLockCount);

        vm.Dispose();
        Assert.Equal(locksBefore, WindowsKeepAwake.ActiveLockCount);
    }

    [Fact]
    public async Task RapidShowPageAsync_EntersLoadingPlaceholderImmediatelyAndIgnoresStaleCompletions()
    {
        using var env = CreateEnv();
        using var cache = new ReaderPageCache(1024 * 1024);
        var page2Tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var page4Tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] validPng = CreatePngBytes(16, 16, new SKColor(30, 180, 160));

        using var engine = new ReaderEngine(
            (idx, _) => idx switch
            {
                2 => page2Tcs.Task,
                4 => page4Tcs.Task,
                _ => Task.FromResult(Array.Empty<byte>())
            },
            cache,
            NullLogger<ReaderEngine>.Instance)
        {
            PageCount = 5
        };

        var fetcher = new ReaderImageFetcher(env.Engine, () => env.HttpClient, NullLogger<ReaderImageFetcher>.Instance);
        var browse = new GalleryBrowseService(env.Engine, env.ClientSettings);

        using var vm = new ReaderViewModel(
            engine,
            fetcher,
            browse,
            env.ClientSettings,
            env.Paths,
            NullLogger<ReaderViewModel>.Instance,
            ownsEngine: false,
            resolveDesktopSettings: static () => new DesktopSettings { KeepScreenOn = false })
        {
            PageCount = 5
        };

        // Start navigating to uncached page 2 -> must immediately enter loading placeholder without blocking.
        Task show2 = vm.ShowPageAsync(2);
        Assert.True(vm.IsPageLoading);
        Assert.Equal(2, vm.CurrentPageDisplay);
        Assert.Equal("第 3 / 5 页", vm.PageLoadingTitle);
        Assert.Equal(-1, vm.PinnedPageIndex);

        // Rapidly scrub/switch to page 4 before page 2 finishes -> page 2 wait cancels cleanly.
        vm.PreviewTargetPage(4);
        Assert.Equal(4, vm.CurrentPageDisplay);
        Assert.Equal("第 5 / 5 页", vm.PageLoadingTitle);

        Task show4 = vm.ShowPageAsync(4);
        page2Tcs.TrySetResult(validPng);
        await show2;

        // Stale page 2 completion must NOT overwrite page 4 target or raise "已取消" error.
        Assert.Equal(string.Empty, vm.ErrorMessage);
        Assert.Equal(4, vm.CurrentPageDisplay);
        Assert.Equal("第 5 / 5 页", vm.PageLoadingTitle);
        Assert.True(vm.IsPageLoading);

        // Complete page 4 with empty bytes -> clears loading placeholder cleanly without UI bitmap creation.
        page4Tcs.TrySetResult(Array.Empty<byte>());
        await show4;
        Assert.False(vm.IsPageLoading);
        Assert.Equal(4, vm.CurrentPage);

        // Live progress formatting check.
        (string status, double pct, bool indet) = ReaderViewModel.FormatProgressDisplay(
            new ReaderPageProgress(4, ReaderPageLoadStage.Downloading, 524_288, 1_048_576));
        Assert.Contains("50%", status, StringComparison.Ordinal);
        Assert.Equal(50.0, pct);
        Assert.False(indet);
    }

    [Fact]
    public async Task ReaderEngine_EmitsStageProgressAndSupportsCachedPageFastPath()
    {
        byte[] validPng = CreatePngBytes(16, 16, new SKColor(20, 120, 200));
        using var cache = new ReaderPageCache(1024 * 1024);
        var stages = new List<ReaderPageLoadStage>();

        using var engine = new ReaderEngine(
            (_, _) => Task.FromResult(validPng),
            cache,
            NullLogger<ReaderEngine>.Instance)
        {
            PageCount = 3
        };
        engine.PageProgressChanged += p => stages.Add(p.Stage);

        Assert.False(engine.TryGetCachedPage(0, out ReaderPageContent? before));
        Assert.Null(before);

        using ReaderPageContent? loaded = await engine.GetPageAsync(0);
        Assert.NotNull(loaded);
        Assert.Contains(ReaderPageLoadStage.ResolvingUrl, stages);
        Assert.Contains(ReaderPageLoadStage.Decoding, stages);
        Assert.Contains(ReaderPageLoadStage.Completed, stages);

        Assert.True(engine.TryGetCachedPage(0, out ReaderPageContent? cached));
        Assert.NotNull(cached);
        Assert.Equal(0, cached!.Index);
        cached.Dispose();
    }

    [Fact]
    public void ReaderImageDecoder_DecodeWithMetadata_MatchesStaticAndAnimatedContracts()
    {
        byte[] staticPng = CreatePngBytes(32, 24, new SKColor(15, 180, 90));
        ReaderImageDecoder.DecodedPageResult? staticDecoded = ReaderImageDecoder.DecodeWithMetadata(staticPng, 4096, 4096);
        Assert.NotNull(staticDecoded);
        using (staticDecoded!.Value.Bitmap)
        {
            Assert.False(staticDecoded.Value.IsAnimated);
            Assert.Empty(staticDecoded.Value.FrameDurations);
            Assert.Equal(32, staticDecoded.Value.Bitmap.Width);
            Assert.Equal(24, staticDecoded.Value.Bitmap.Height);
            Assert.True(SkiaBitmapHelper.CanCopyPixelsDirectly(staticDecoded.Value.Bitmap));
        }

        byte[] animGif = CreateTwoFrameGifBytes();
        ReaderImageDecoder.DecodedPageResult? animDecoded = ReaderImageDecoder.DecodeWithMetadata(animGif, 4096, 4096);
        Assert.NotNull(animDecoded);
        using (animDecoded!.Value.Bitmap)
        {
            Assert.True(animDecoded.Value.IsAnimated);
            Assert.Equal([100, 200], animDecoded.Value.FrameDurations);
            Assert.Equal(2, animDecoded.Value.Bitmap.Width);
            Assert.Equal(2, animDecoded.Value.Bitmap.Height);
        }

        using var frameDecoder = ReaderImageDecoder.AnimatedFrameDecoder.TryCreate(animGif);
        Assert.NotNull(frameDecoder);
        using SKBitmap? frame1 = frameDecoder!.DecodeFrame(frameIndex: 1, maxEdge: 4096, forceSrgb: false);
        Assert.NotNull(frame1);
        Assert.Equal(2, frame1!.Width);
        Assert.Equal(2, frame1.Height);
    }

    [Fact]
    public async Task ReaderEngine_DoesNotRetainStaticPayloadsAndCapsAnimatedPayloads()
    {
        byte[] staticPng = CreatePngBytes(16, 16, SKColors.CornflowerBlue);
        byte[] animGif = CreateTwoFrameGifBytes();
        using var cache = new ReaderPageCache(8 * 1024 * 1024);

        using var engine = new ReaderEngine(
            (idx, _) => Task.FromResult(idx < 10 ? staticPng : animGif),
            cache,
            NullLogger<ReaderEngine>.Instance)
        {
            PageCount = 20
        };

        // Static pages must never stay pinned in _payloads after decoding.
        for (int i = 0; i < 5; i++)
        {
            using ReaderPageContent? page = await engine.GetPageAsync(i);
            Assert.NotNull(page);
            Assert.False(page!.IsAnimated);
            Assert.False(engine.TryGetPayload(i, out _));
        }

        // Animated pages keep payloads up to the bounded LRU cap (6).
        for (int i = 10; i < 18; i++)
        {
            using ReaderPageContent? page = await engine.GetPageAsync(i);
            Assert.NotNull(page);
            Assert.True(page!.IsAnimated);
        }

        Assert.False(engine.TryGetPayload(10, out _));
        Assert.False(engine.TryGetPayload(11, out _));
        Assert.True(engine.TryGetPayload(17, out byte[]? latestPayload));
        Assert.Equal(animGif.Length, latestPayload!.Length);

        engine.ReleasePages();
        Assert.False(engine.TryGetPayload(17, out _));
    }

    [Fact]
    public async Task ReaderImageFetcher_ByteCache_EnforcesBoundsAndClearsOnUnbind()
    {
        using TestEnv env = CreateEnv();
        byte[] pageBytes = Enumerable.Repeat((byte)0xCD, 1024).ToArray();
        using var handler = new CallbackHandler(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            if (url.StartsWith("https://e-hentai.org/s/", StringComparison.Ordinal))
            {
                string pageNum = url.Split('-').Last();
                string html = $"<html><body><div id=\"i3\"><img id=\"img\" src=\"https://img.example.test/page-{pageNum}.jpg\" style=\"width:100px;height:100px\" /></div><script>var showkey=\"showkey123\";</script></body></html>";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
                };
            }

            var byteContent = new ByteArrayContent(pageBytes);
            byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = byteContent };
        });

        using var httpClient = new HttpClient(handler);
        using var cookies = new SqliteCookieStore(Path.Combine(_root, "fetcher-cookies.db"));
        var engine = new EhEngine(httpClient, cookies, env.Database, new EhFilter(env.Database), env.ClientSettings);
        var fetcher = new ReaderImageFetcher(
            engine,
            () => httpClient,
            NullLogger<ReaderImageFetcher>.Instance,
            maxCachedBytes: 2048,
            maxCachedEntries: 4);

        var detail = new GalleryDetail { Gid = 100, Token = "tok", Pages = 5 };
        string[] pageUrls = Enumerable.Range(0, 5)
            .Select(i => $"https://e-hentai.org/s/tok{i}/100-{i + 1}")
            .ToArray();
        fetcher.Bind(detail, pageUrls, EhUrl.RefererE);

        for (int i = 0; i < 5; i++)
        {
            byte[] payload = await fetcher.FetchAsync(i, CancellationToken.None);
            Assert.Equal(1024, payload.Length);
        }

        Assert.True(fetcher.CachedByteTotal <= 2048, $"Expected CachedByteTotal <= 2048, got {fetcher.CachedByteTotal}");
        Assert.True(fetcher.CachedByteEntryCount <= 2, $"Expected <= 2 cached entries, got {fetcher.CachedByteEntryCount}");

        fetcher.Unbind();
        Assert.Equal(0, fetcher.CachedByteTotal);
        Assert.Equal(0, fetcher.CachedByteEntryCount);
    }

    [Fact]
    public void ReaderZoomMath_ComputeAnchoredZoomOffsets_PreservesAnchorAndClampsBounds()
    {
        // Center anchor zoom 1.0 -> 2.0 on 1000x800 content/viewport keeps center fixed.
        (double centerH, double centerV) = ReaderZoomMath.ComputeAnchoredZoomOffsets(
            currentHorizontalOffset: 0,
            currentVerticalOffset: 0,
            currentZoom: 1.0f,
            targetZoom: 2.0f,
            anchorViewportX: 500,
            anchorViewportY: 400,
            viewportWidth: 1000,
            viewportHeight: 800,
            currentExtentWidth: 1000,
            currentExtentHeight: 800);
        Assert.Equal(500, centerH, precision: 3);
        Assert.Equal(400, centerV, precision: 3);

        // Off-center pointer (300, 200) keeps underlying content point (400, 250) stationary across 1.0 -> 2.5 zoom.
        (double ptrH, double ptrV) = ReaderZoomMath.ComputeAnchoredZoomOffsets(
            currentHorizontalOffset: 100,
            currentVerticalOffset: 50,
            currentZoom: 1.0f,
            targetZoom: 2.5f,
            anchorViewportX: 300,
            anchorViewportY: 200,
            viewportWidth: 800,
            viewportHeight: 600,
            currentExtentWidth: 1200,
            currentExtentHeight: 900);
        Assert.Equal(400.0, (ptrH + 300.0) / 2.5, precision: 5);
        Assert.Equal(250.0, (ptrV + 200.0) / 2.5, precision: 5);

        // Zooming out back to 1.0 clamps offsets to [0, maxOffset].
        (double outH, double outV) = ReaderZoomMath.ComputeAnchoredZoomOffsets(
            currentHorizontalOffset: 10,
            currentVerticalOffset: 10,
            currentZoom: 2.0f,
            targetZoom: 1.0f,
            anchorViewportX: 400,
            anchorViewportY: 300,
            viewportWidth: 800,
            viewportHeight: 600,
            currentExtentWidth: 1600,
            currentExtentHeight: 1200);
        Assert.Equal(0.0, outH);
        Assert.Equal(0.0, outV);

        // Non-positive zoom factors are guarded cleanly.
        Assert.Equal((0.0, 0.0), ReaderZoomMath.ComputeAnchoredZoomOffsets(50, 50, 0f, 2f, 100, 100, 800, 600, 800, 600));
        Assert.Equal((0.0, 0.0), ReaderZoomMath.ComputeAnchoredZoomOffsets(50, 50, 1f, 0f, 100, 100, 800, 600, 800, 600));
    }

    [Fact]
    public async Task ReaderEngine_PrefetchCancelsStaleWorkCapsConcurrencyAndSupportsInvalidatePage()
    {
        byte[] validPng = CreatePngBytes(16, 16, new SKColor(50, 140, 220));
        using var cache = new ReaderPageCache(8 * 1024 * 1024);

        var twoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdFirstWindow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maxActive = 0;
        int firstWindowStarted = 0;
        int firstWindowCancelled = 0;
        int page13Fetches = 0;

        using var engine = new ReaderEngine(
            async (idx, ct) =>
            {
                int cur = Interlocked.Increment(ref active);
                while (true)
                {
                    int observedMax = Volatile.Read(ref maxActive);
                    if (cur <= observedMax || Interlocked.CompareExchange(ref maxActive, cur, observedMax) == observedMax)
                    {
                        break;
                    }
                }

                try
                {
                    if (idx is >= 1 and <= 4)
                    {
                        if (Interlocked.Increment(ref firstWindowStarted) == 2)
                        {
                            twoStarted.TrySetResult();
                        }

                        try
                        {
                            await holdFirstWindow.Task.WaitAsync(ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            Interlocked.Increment(ref firstWindowCancelled);
                            throw;
                        }
                    }

                    if (idx == 13)
                    {
                        Interlocked.Increment(ref page13Fetches);
                    }

                    return validPng;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            cache,
            NullLogger<ReaderEngine>.Instance)
        {
            PageCount = 20,
            PreloadCount = 4
        };

        Task firstPrefetch = engine.PrefetchAroundAsync(0);
        await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Volatile.Read(ref maxActive) <= 2);

        // Jumping to page 12 must cancel stale prefetch around page 0 before pages 3/4 even start.
        Task secondPrefetch = engine.PrefetchAroundAsync(12);
        await Task.WhenAll(firstPrefetch, secondPrefetch).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(Volatile.Read(ref maxActive) <= 2, $"Expected max prefetch concurrency <= 2, got {maxActive}");
        Assert.Equal(2, Volatile.Read(ref firstWindowStarted));
        Assert.Equal(2, Volatile.Read(ref firstWindowCancelled));

        // Page 13 was prefetched by secondPrefetch; InvalidatePage must force a re-fetch.
        Assert.True(engine.TryGetCachedPage(13, out ReaderPageContent? cached13));
        cached13!.Dispose();
        Assert.Equal(1, Volatile.Read(ref page13Fetches));

        engine.InvalidatePage(13);
        Assert.False(engine.TryGetCachedPage(13, out _));

        using ReaderPageContent? reloaded13 = await engine.GetPageAsync(13);
        Assert.NotNull(reloaded13);
        Assert.Equal(2, Volatile.Read(ref page13Fetches));
    }

    [Fact]
    public async Task ReaderViewModel_StripVisiblePageSync_And_NonActiveProgressSuppression()
    {
        using var env = CreateEnv();
        using var cache = new ReaderPageCache(1024 * 1024);
        using var engine = new ReaderEngine((_, _) => Task.FromResult(Array.Empty<byte>()), cache, NullLogger<ReaderEngine>.Instance)
        {
            PageCount = 6
        };
        var fetcher = new ReaderImageFetcher(env.Engine, () => env.HttpClient, NullLogger<ReaderImageFetcher>.Instance);
        var browse = new GalleryBrowseService(env.Engine, env.ClientSettings);

        var countingSync = new CountingSynchronizationContext();
        SynchronizationContext? previousSync = SynchronizationContext.Current;
        ReaderViewModel? vm = null;
        try
        {
            SynchronizationContext.SetSynchronizationContext(countingSync);
            vm = new ReaderViewModel(
                engine,
                fetcher,
                browse,
                env.ClientSettings,
                env.Paths,
                NullLogger<ReaderViewModel>.Instance,
                ownsEngine: false,
                resolveDesktopSettings: static () => new DesktopSettings
                {
                    KeepScreenOn = false,
                    DefaultReaderLayout = (int)ReaderLayout.LeftToRight
                })
            {
                PageCount = 6
            };
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousSync);
        }

        using (vm)
        {
            // Background progress from a non-active prefetched page must NOT marshal onto the UI thread.
            await Task.Run(() => engine.ReportProgress(new ReaderPageProgress(5, ReaderPageLoadStage.Downloading, 1024, 2048)));
            Assert.Equal(0, Volatile.Read(ref countingSync.PostCount));

            // Continuous vertical scroll syncs CurrentPageDisplay, CurrentPage, and PositionText.
            vm.LayoutIndex = (int)ReaderLayout.TopToBottom;
            vm.UpdateStripVisiblePage(3);
            Assert.Equal(3, vm.CurrentPageDisplay);
            Assert.Equal(3, vm.CurrentPage);
            Assert.Contains("4 / 6", vm.PositionText, StringComparison.Ordinal);
        }
    }

    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        public int PostCount;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref PostCount);
            d(state);
        }
    }

    private static byte[] CreateTwoFrameGifBytes() =>
    [
        // Header: GIF89a
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61,
        // LSD: 2x2, GCT=1, 2 colors
        0x02, 0x00, 0x02, 0x00, 0x80, 0x00, 0x00,
        // GCT: black, white
        0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,
        // Frame 1 GCE (100ms delay) + Image Descriptor + LZW min=2 (4 px: 0,1,0,1)
        0x21, 0xF9, 0x04, 0x04, 0x0A, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x02, 0x00, 0x00,
        0x02, 0x03, 0x44, 0x10, 0x05, 0x00,
        // Frame 2 GCE (200ms delay) + Image Descriptor + LZW min=2 (4 px: 0,1,0,1)
        0x21, 0xF9, 0x04, 0x04, 0x14, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x02, 0x00, 0x00,
        0x02, 0x03, 0x44, 0x10, 0x05, 0x00,
        // Trailer
        0x3B
    ];

    private TestEnv CreateEnv()
    {
        string appRoot = Path.Combine(_root, "app-" + Guid.NewGuid().ToString("N"));
        AppPaths paths = AppPaths.CreateAt(appRoot);
        var db = new EhDatabase(paths.EhDatabasePath);
        var filter = new EhFilter(db);
        var clientSettings = new EhClientSettings();
        var cookies = new SqliteCookieStore(paths.CookieDatabasePath);
        var httpClient = new HttpClient(new StubHandler());
        var engine = new EhEngine(httpClient, cookies, db, filter, clientSettings);
        return new TestEnv(paths, db, cookies, clientSettings, httpClient, engine);
    }

    private static byte[] CreatePngBytes(int w, int h, SKColor color)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        bmp.Erase(color);
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreateSplitPngBytes(int w, int h, SKColor left, SKColor right)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bmp);
        using var leftPaint = new SKPaint { Color = left };
        using var rightPaint = new SKPaint { Color = right };
        canvas.DrawRect(0, 0, w / 2f, h, leftPaint);
        canvas.DrawRect(w / 2f, 0, w / 2f, h, rightPaint);
        using SKData data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class TestEnv(
        AppPaths paths,
        EhDatabase database,
        SqliteCookieStore cookies,
        EhClientSettings clientSettings,
        HttpClient httpClient,
        EhEngine engine) : IDisposable
    {
        public AppPaths Paths => paths;
        public EhDatabase Database => database;
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
}

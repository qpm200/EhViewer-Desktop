using EhViewer.Core.Client.Data;
using EhViewer.Core.Spider;
using EhViewer.Core.Util;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.Reader;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace EhViewer.Desktop.Tests;

/// <summary>
/// Guards the pixel-extraction contract the reader and every thumbnail surface depend on: the
/// helper copies exactly the requested rectangle and never aliases caller-owned bitmap memory.
/// </summary>
public sealed class SkiaBitmapHelperTests
{
    private static SKBitmap CreateBitmap(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(color);
        return bitmap;
    }

    [Fact]
    public void ExtractBgraPixels_FullBitmap_ReturnsEveryPixel()
    {
        using SKBitmap bitmap = CreateBitmap(4, 3, new SKColor(10, 20, 30, 255));

        DecodedPixelBuffer? buffer = SkiaBitmapHelper.ExtractBgraPixels(bitmap);

        Assert.NotNull(buffer);
        Assert.Equal(4, buffer.Width);
        Assert.Equal(3, buffer.Height);
        Assert.Equal(4 * 3 * 4, buffer.BgraPixels.Length);

        // BGRA order: blue, green, red, alpha.
        Assert.Equal(30, buffer.BgraPixels[0]);
        Assert.Equal(20, buffer.BgraPixels[1]);
        Assert.Equal(10, buffer.BgraPixels[2]);
        Assert.Equal(255, buffer.BgraPixels[3]);
    }

    [Fact]
    public void ExtractBgraPixels_Clip_ReturnsOnlyTheSubRectangle()
    {
        using SKBitmap bitmap = CreateBitmap(8, 8, new SKColor(1, 2, 3, 255));

        DecodedPixelBuffer? buffer = SkiaBitmapHelper.ExtractBgraPixels(bitmap, new SKRectI(2, 3, 5, 7));

        Assert.NotNull(buffer);
        Assert.Equal(3, buffer.Width);
        Assert.Equal(4, buffer.Height);
        Assert.Equal(3 * 4 * 4, buffer.BgraPixels.Length);
    }

    [Fact]
    public void ExtractBgraPixels_ClipBeyondBounds_IsClamped()
    {
        using SKBitmap bitmap = CreateBitmap(5, 5, SKColors.Black);

        DecodedPixelBuffer? buffer = SkiaBitmapHelper.ExtractBgraPixels(bitmap, new SKRectI(3, 3, 50, 50));

        Assert.NotNull(buffer);
        Assert.Equal(2, buffer.Width);
        Assert.Equal(2, buffer.Height);
    }

    [Fact]
    public void ExtractBgraPixels_FullyOutsideClip_ReturnsNull()
    {
        using SKBitmap bitmap = CreateBitmap(5, 5, SKColors.Black);

        Assert.Null(SkiaBitmapHelper.ExtractBgraPixels(bitmap, new SKRectI(10, 10, 20, 20)));
    }

    [Fact]
    public void ExtractBgraPixels_NullBitmap_ReturnsNull()
    {
        Assert.Null(SkiaBitmapHelper.ExtractBgraPixels(null));
    }

    [Fact]
    public void DecodedPixelBuffer_RejectsMismatchedPixelCount()
    {
        Assert.Throws<ArgumentException>(() => new DecodedPixelBuffer(new byte[10], 2, 2));
    }

    [Fact]
    public void DecodedPixelBuffer_RejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedPixelBuffer(Array.Empty<byte>(), 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedPixelBuffer(Array.Empty<byte>(), 2, 0));
    }
}

/// <summary>
/// The reader crash fixed in this pass came from disposing a cache-owned bitmap while the cache
/// still referenced it. These tests pin the lease contract that prevents that.
/// </summary>
public sealed class ReaderPageCacheLeaseTests
{
    private static SKBitmap CreateBitmap(int edge)
        => new(new SKImageInfo(edge, edge, SKColorType.Bgra8888, SKAlphaType.Premul));

    [Fact]
    public void Lease_KeepsBitmapAliveWhenBudgetForcesEviction()
    {
        using var cache = new ReaderPageCache(64 * 64 * 4);
        SKBitmap first = CreateBitmap(64);
        ReaderPageCache.Lease lease = cache.PutAndAcquire("page:0", first);

        // A second entry of the same size overflows the single-entry budget and evicts page:0.
        SKBitmap second = CreateBitmap(64);
        ReaderPageCache.Lease secondLease = cache.PutAndAcquire("page:1", second);

        Assert.Equal(1, cache.Count);

        // The evicted entry is still pinned by the outstanding lease, so the pixels remain valid.
        Assert.NotEqual(IntPtr.Zero, lease.Bitmap.Handle);
        Assert.Equal(64, lease.Bitmap.Width);

        lease.Dispose();
        secondLease.Dispose();
    }

    [Fact]
    public void TryAcquire_IncrementsIndependentlyForEachCaller()
    {
        using var cache = new ReaderPageCache(1024 * 1024);
        SKBitmap bitmap = CreateBitmap(16);
        cache.Put("page:0", bitmap);

        Assert.True(cache.TryAcquire("page:0", out ReaderPageCache.Lease? first));
        Assert.True(cache.TryAcquire("page:0", out ReaderPageCache.Lease? second));
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Same(first!.Bitmap, second!.Bitmap);

        // Removing the entry evicts it, but both leases still pin the bitmap.
        Assert.True(cache.Remove("page:0"));
        Assert.NotEqual(IntPtr.Zero, first.Bitmap.Handle);

        first.Dispose();
        Assert.NotEqual(IntPtr.Zero, second.Bitmap.Handle);

        second.Dispose();
    }

    [Fact]
    public void TryAcquire_MissingKey_ReturnsFalse()
    {
        using var cache = new ReaderPageCache(1024);

        Assert.False(cache.TryAcquire("page:9", out ReaderPageCache.Lease? lease));
        Assert.Null(lease);
    }

    [Fact]
    public void Clear_DropsEveryEntry()
    {
        using var cache = new ReaderPageCache(1024 * 1024);
        cache.Put("page:0", CreateBitmap(8));
        cache.Put("page:1", CreateBitmap(8));

        Assert.Equal(2, cache.Count);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CurrentBytes);
    }

    [Fact]
    public void AcquireOrWrap_UnpinnedLease_DisposesOnRelease()
    {
        var cache = new ReaderPageCache(1024);
        SKBitmap bitmap = CreateBitmap(8);

        ReaderPageCache.Lease lease = cache.AcquireOrWrap(bitmap);
        Assert.NotEqual(IntPtr.Zero, lease.Bitmap.Handle);

        lease.Dispose();
        Assert.Equal(0, cache.Count);
        Assert.Equal(IntPtr.Zero, bitmap.Handle);
    }
}

/// <summary>Resume-detection regression tests for the downloader.</summary>
public sealed class DownloadResumeTests
{
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".webp")]
    [InlineData(".gif")]
    public void TryFindExistingPageFile_RecognisesAnySupportedExtension(string extension)
    {
        string folder = CreateTempFolder();
        try
        {
            string expected = Path.Combine(folder, "00000007" + extension);
            File.WriteAllBytes(expected, [1, 2, 3]);

            Assert.True(DownloadManager.TryFindExistingPageFile(folder, 7, out string? found));
            Assert.Equal(expected, found);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TryFindExistingPageFile_EmptyFile_IsTreatedAsMissing()
    {
        string folder = CreateTempFolder();
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "00000003.png"), []);

            Assert.False(DownloadManager.TryFindExistingPageFile(folder, 3, out string? found));
            Assert.Null(found);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TryFindExistingPageFile_MissingIndex_ReturnsFalse()
    {
        string folder = CreateTempFolder();
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "00000001.jpg"), [1]);

            Assert.False(DownloadManager.TryFindExistingPageFile(folder, 2, out _));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void BuildImagePath_UsesZeroPaddedEightDigitName()
    {
        string path = DownloadManager.BuildImagePath(@"C:\root\1-title", 42);

        Assert.EndsWith("00000042.jpg", path, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGalleryFolder_SanitizesTitle()
    {
        var info = new GalleryInfo { Gid = 123, Title = "A/B:C*D" };

        string folder = DownloadManager.BuildGalleryFolder(@"C:\root", info);

        Assert.StartsWith("123-", Path.GetFileName(folder), StringComparison.Ordinal);
        Assert.DoesNotContain('/', Path.GetFileName(folder));
        Assert.DoesNotContain(':', Path.GetFileName(folder));
    }

    [Fact]
    public void IsPlainText_DetectsErrorPagePayload()
    {
        Assert.True(DownloadManager.IsPlainText("error"u8));
        Assert.False(DownloadManager.IsPlainText([0xFF, 0xD8, 0xFF, 0x00]));
        Assert.True(DownloadManager.IsPlainText([]));
    }

    private static string CreateTempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "EhViewerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}

/// <summary>Progress-file path and parse behaviour shared with Android's download layout.</summary>
public sealed class ReaderProgressTests
{
    [Fact]
    public void BuildProgressPath_UsesGidTitleFolderAndDotFile()
    {
        var detail = new GalleryDetail { Gid = 123456, Title = "(C99) [Circle] Title" };

        string path = ReaderViewModel.BuildProgressPath(@"C:\downloads", detail);

        // ExtractTitle drops the event/circle bracket prefixes, matching the Android folder naming.
        Assert.Equal(Path.Combine(@"C:\downloads", "123456-Title", ".ehviewer"), path);
    }

    [Fact]
    public void BuildProgressPath_SanitizesInvalidFilenameCharacters()
    {
        var detail = new GalleryDetail { Gid = 7, Title = "A/B:C*D?E" };

        string path = ReaderViewModel.BuildProgressPath(@"C:\downloads", detail);

        string folder = Path.GetFileName(Path.GetDirectoryName(path)!);
        Assert.StartsWith("7-", folder, StringComparison.Ordinal);
        Assert.DoesNotContain('/' , folder);
        Assert.DoesNotContain(':' , folder);
        Assert.DoesNotContain('*' , folder);
        Assert.DoesNotContain('?' , folder);
    }

    [Fact]
    public void ReadSavedStartPage_MissingFile_ReturnsZero()
    {
        Assert.Equal(0, ReaderViewModel.ReadSavedStartPage(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), ".ehviewer")));
    }

    [Fact]
    public void ReadSavedStartPage_ValidFile_ReturnsRecordedPage()
    {
        string folder = CreateTempFolder();
        try
        {
            string file = Path.Combine(folder, ".ehviewer");
            WriteProgress(file, startPage: 37);

            Assert.Equal(37, ReaderViewModel.ReadSavedStartPage(file));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ReadSavedStartPage_CorruptFile_ReturnsZero()
    {
        string folder = CreateTempFolder();
        try
        {
            string file = Path.Combine(folder, ".ehviewer");
            File.WriteAllText(file, "not a spider info document");

            Assert.Equal(0, ReaderViewModel.ReadSavedStartPage(file));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ReadExistingProgress_PreservesPTokenMap()
    {
        string folder = CreateTempFolder();
        try
        {
            string file = Path.Combine(folder, ".ehviewer");
            WriteProgress(file, startPage: 5, pTokens: new() { [0] = "https://img/0.jpg", [1] = "https://img/1.jpg" });

            SpiderInfo? info = ReaderViewModel.ReadExistingProgress(file);

            Assert.NotNull(info);
            Assert.Equal(5, info.StartPage);
            Assert.Equal(2, info.PTokenMap.Count);
            Assert.Equal("https://img/0.jpg", info.PTokenMap[0]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ReadExistingProgress_MissingFile_ReturnsNull()
    {
        Assert.Null(ReaderViewModel.ReadExistingProgress(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    private static void WriteProgress(string file, int startPage, SortedDictionary<int, string>? pTokens = null)
    {
        var info = new SpiderInfo
        {
            StartPage = startPage,
            Gid = 100,
            Token = "abcd",
            Pages = 10,
            PreviewPages = 0,
            PreviewPerPage = 0,
            PTokenMap = pTokens ?? []
        };

        using var stream = new FileStream(file, FileMode.Create, FileAccess.Write);
        info.Write(stream);
    }

    private static string CreateTempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "EhViewerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}

/// <summary>Thumbnail sampling and the optional tag database degrade without throwing.</summary>
public sealed class ThumbnailSamplingTests
{
    [Theory]
    [InlineData(4000, 2000, 1000, 1000, 500)]
    [InlineData(1000, 500, 1000, 1000, 500)]
    [InlineData(300, 600, 1000, 300, 600)]
    public void ComputeSampledSize_NeverUpscales(int width, int height, int target, int expectedWidth, int expectedHeight)
    {
        SKSizeI size = ThumbnailService.ComputeSampledSize(width, height, target);

        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
    }

    [Fact]
    public void Decode_InvalidPayload_ReturnsNull()
    {
        Assert.Null(ThumbnailService.Decode([1, 2, 3, 4], 128));
    }

    [Fact]
    public void CanonicalSamplingPreservesAspectRatio()
    {
        SKSizeI size = ThumbnailService.ComputeSampledSize(3000, 1000, 600);

        Assert.Equal(600, size.Width);
        Assert.Equal(200, size.Height);
    }

    [Fact]
    public void DecodeAndExtractBgraPixels_ClippedSpriteSheet_PreservesAllTiles()
    {
        using var sheet = new SKBitmap(400, 100, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(sheet))
        {
            SKColor[] tileColors = [SKColors.Red, SKColors.Green, SKColors.Blue, SKColors.Gold];
            for (int i = 0; i < tileColors.Length; i++)
            {
                using var paint = new SKPaint { Color = tileColors[i] };
                canvas.DrawRect(new SKRect(i * 100, 0, (i + 1) * 100, 100), paint);
            }
        }

        using SKData encoded = sheet.Encode(SKEncodedImageFormat.Png, 100);
        using SKBitmap? decoded = ThumbnailService.Decode(encoded.ToArray(), int.MaxValue);
        Assert.NotNull(decoded);
        Assert.Equal(400, decoded.Width);
        Assert.Equal(100, decoded.Height);

        (byte b, byte g, byte r)[] expectedBgr =
        [
            (0x00, 0x00, 0xFF),
            (0x00, 0x80, 0x00),
            (0xFF, 0x00, 0x00),
            (0x00, 0xD7, 0xFF)
        ];

        for (int i = 0; i < expectedBgr.Length; i++)
        {
            DecodedPixelBuffer? tile = SkiaBitmapHelper.ExtractBgraPixels(
                decoded,
                new SKRectI(i * 100, 0, (i + 1) * 100, 100));
            Assert.NotNull(tile);
            Assert.Equal(100, tile.Width);
            Assert.Equal(100, tile.Height);

            int centerOffset = (50 * tile.Width + 50) * 4;
            Assert.Equal(expectedBgr[i].b, tile.BgraPixels[centerOffset]);
            Assert.Equal(expectedBgr[i].g, tile.BgraPixels[centerOffset + 1]);
            Assert.Equal(expectedBgr[i].r, tile.BgraPixels[centerOffset + 2]);
        }

        Assert.True(new EhViewer.Desktop.ViewModels.ReaderThumbnailItem(0, 1, "https://example.com/s.jpg", 100, 0, 100, 140).IsClipped);
        Assert.False(new EhViewer.Desktop.ViewModels.ReaderThumbnailItem(0, 1, "https://example.com/s.jpg").IsClipped);
    }
}

/// <summary>The tag database is optional: a missing or unreachable file must not break search.</summary>
public sealed class TagTranslationSourceTests
{
    [Fact]
    public async Task EnsureLoadedAsync_UnreachableUpstream_StaysNullWithoutThrowing()
    {
        string root = Path.Combine(Path.GetTempPath(), "EhViewerTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = AppPaths.CreateAt(root);
        paths.EnsureCreated();

        try
        {
            using var handler = new FailingHandler();
            using var client = new HttpClient(handler);
            var source = new TagTranslationSource(
                paths,
                () => client,
                NullLogger<TagTranslationSource>.Instance);

            EhViewer.Core.Client.EhTagDatabase? database = await source.EnsureLoadedAsync();

            Assert.Null(database);
            Assert.True(source.IsInitialized);
            Assert.Empty(source.Suggest("glasses"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Suggest_WithoutDatabase_ReturnsEmpty()
    {
        AppPaths paths = AppPaths.CreateAt(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var source = new TagTranslationSource(paths, static () => new HttpClient(), NullLogger<TagTranslationSource>.Instance);

        Assert.Empty(source.Suggest("anything"));
        Assert.Empty(source.Suggest(null));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }
}

/// <summary>Disk thumbnail cache hysteresis eviction and fast-path direct pixel copy checks.</summary>
public sealed class PerformanceRegressionTests
{
    [Fact]
    public void ThumbnailDiskCache_EvictsWithHysteresisAndReplacesSizeCleanly()
    {
        string dir = Path.Combine(Path.GetTempPath(), "EhViewerTests", Guid.NewGuid().ToString("N"));
        try
        {
            // 1 MB budget -> 85% hysteresis target = ~870 KB.
            var cache = new ThumbnailDiskCache(dir, budgetMb: 1);
            byte[] chunk = new byte[300 * 1024];

            for (int i = 0; i < 4; i++)
            {
                string key = ThumbnailDiskCache.ComputeKey($"https://example.invalid/t{i}.jpg", 320);
                Assert.NotNull(cache.Set(key, chunk));
            }

            long budgetBytes = 1024L * 1024L;
            long targetMaxAfterEvict = (long)(budgetBytes * 0.85);
            long totalAfterInsert = cache.GetTotalBytes();
            Assert.True(
                totalAfterInsert <= targetMaxAfterEvict,
                $"Expected hysteresis eviction <= {targetMaxAfterEvict} bytes, got {totalAfterInsert}");

            string newestKey = ThumbnailDiskCache.ComputeKey("https://example.invalid/t3.jpg", 320);
            Assert.True(cache.TryGetPath(newestKey, out string newestPath));
            Assert.True(File.Exists(newestPath));

            // Overwriting the same key must subtract the old size instead of double-counting.
            Assert.NotNull(cache.Set(newestKey, new byte[100 * 1024]));
            long expectedDeltaAdjusted = totalAfterInsert - (200 * 1024);
            Assert.Equal(expectedDeltaAdjusted, cache.GetTotalBytes());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void SkiaBitmapHelper_CanCopyPixelsDirectly_ValidatesCompactBgra8888()
    {
        using var bgra = new SKBitmap(new SKImageInfo(16, 12, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var rgba = new SKBitmap(new SKImageInfo(16, 12, SKColorType.Rgba8888, SKAlphaType.Premul));

        Assert.True(SkiaBitmapHelper.CanCopyPixelsDirectly(bgra));
        Assert.False(SkiaBitmapHelper.CanCopyPixelsDirectly(rgba));
        Assert.False(SkiaBitmapHelper.CanCopyPixelsDirectly(null));
    }

    [Fact]
    public async Task ThumbnailDiskCache_ComputeUrlKey_AndConcurrentSets_RemainConsistent()
    {
        string dir = Path.Combine(Path.GetTempPath(), "EhViewerTests", Guid.NewGuid().ToString("N"));
        try
        {
            const string url = "https://ehgt.org/t/sample.jpg";
            string urlKey = ThumbnailDiskCache.ComputeUrlKey(url);
            Assert.Equal(urlKey, ThumbnailDiskCache.ComputeUrlKey(url));
            Assert.NotEqual(urlKey, ThumbnailDiskCache.ComputeKey(url, 320));

            var cache = new ThumbnailDiskCache(dir, budgetMb: 10);
            byte[] dataA = Enumerable.Repeat((byte)0x11, 2048).ToArray();
            byte[] dataB = Enumerable.Repeat((byte)0x22, 4096).ToArray();

            Task[] writes = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            {
                string key = ThumbnailDiskCache.ComputeUrlKey($"https://ehgt.org/t/{i}.jpg");
                Assert.NotNull(cache.Set(key, (i % 2 == 0) ? dataA : dataB));
            })).ToArray();
            await Task.WhenAll(writes);

            long diskBytes = Directory.EnumerateFiles(dir, "*.bin", SearchOption.AllDirectories)
                .Sum(static p => new FileInfo(p).Length);
            Assert.Equal(diskBytes, cache.GetTotalBytes());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ThumbnailService_CrossWidthLoads_ReuseSingleDiskDownload()
    {
        string dir = Path.Combine(Path.GetTempPath(), "EhViewerTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var bmp = new SKBitmap(640, 480, SKColorType.Bgra8888, SKAlphaType.Opaque);
            bmp.Erase(SKColors.Teal);
            using SKData jpg = bmp.Encode(SKEncodedImageFormat.Jpeg, 90);
            byte[] jpgBytes = jpg.ToArray();

            int requestCount = 0;
            using var handler = new CountingImageHandler(() =>
            {
                Interlocked.Increment(ref requestCount);
                return jpgBytes;
            });
            using var client = new HttpClient(handler);
            var diskCache = new ThumbnailDiskCache(dir, budgetMb: 8);
            var settings = new EhViewer.Core.Network.EhClientSettings();
            using var service = new ThumbnailService(
                diskCache,
                () => client,
                settings,
                NullLogger<ThumbnailService>.Instance,
                static () => 16L * 1024 * 1024);

            const string url = "https://ehgt.org/t/shared-cross-width.jpg";
            DecodedPixelBuffer? small = await service.LoadPixelsAsync(url, targetWidth: 160);
            Assert.NotNull(small);
            Assert.Equal(160, small!.Width);
            Assert.Equal(1, Volatile.Read(ref requestCount));

            // Requesting a different width must decode from the URL-keyed disk blob without re-downloading.
            DecodedPixelBuffer? medium = await service.LoadPixelsAsync(url, targetWidth: 320);
            Assert.NotNull(medium);
            Assert.Equal(320, medium!.Width);
            Assert.Equal(1, Volatile.Read(ref requestCount));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void BoundedLruCache_EvictsLeastRecentlyUsedAndPromotesHits()
    {
        var cache = new BoundedLruCache<int, string>(capacity: 2);
        cache.Set(1, "one");
        cache.Set(2, "two");
        Assert.Equal(2, cache.Count);

        // Touching key 1 promotes it ahead of key 2.
        Assert.True(cache.TryGet(1, out string val1));
        Assert.Equal("one", val1);

        // Adding key 3 must evict key 2 (now LRU), preserving key 1.
        cache.Set(3, "three");
        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(1, out _));
        Assert.True(cache.TryGet(3, out string val3));
        Assert.Equal("three", val3);

        // In-place update does not increase count or evict siblings.
        cache.Set(1, "one-updated");
        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet(1, out string updated));
        Assert.Equal("one-updated", updated);

        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(1, out _));
    }

    [Fact]
    public async Task ThumbnailService_LoadRawBytesAsync_ReusesDiskCacheAcrossCalls()
    {
        string dir = Path.Combine(Path.GetTempPath(), "EhViewerTests", Guid.NewGuid().ToString("N"));
        try
        {
            byte[] sampleBytes = Enumerable.Range(0, 256).Select(static i => (byte)i).ToArray();
            int requestCount = 0;
            using var handler = new CountingImageHandler(() =>
            {
                Interlocked.Increment(ref requestCount);
                return sampleBytes;
            });
            using var client = new HttpClient(handler);
            var diskCache = new ThumbnailDiskCache(dir, budgetMb: 8);
            var settings = new EhViewer.Core.Network.EhClientSettings();
            using var service = new ThumbnailService(
                diskCache,
                () => client,
                settings,
                NullLogger<ThumbnailService>.Instance,
                static () => 16L * 1024 * 1024);

            const string url = "https://ehgt.org/t/raw-cover.jpg";
            byte[]? first = await service.LoadRawBytesAsync(url);
            Assert.NotNull(first);
            Assert.Equal(sampleBytes, first);
            Assert.Equal(1, Volatile.Read(ref requestCount));

            byte[]? second = await service.LoadRawBytesAsync(url);
            Assert.NotNull(second);
            Assert.Equal(sampleBytes, second);
            Assert.Equal(1, Volatile.Read(ref requestCount));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private sealed class CountingImageHandler(Func<byte[]> bytesFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(bytesFactory());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }
}

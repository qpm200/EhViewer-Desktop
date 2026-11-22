using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Translation;
using Microsoft.ML.OnnxRuntime;
using SkiaSharp;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class Wave5OcrPerfTests : IDisposable
{
    private readonly string _root;

    public Wave5OcrPerfTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "EhViewerWave5OcrTests", Guid.NewGuid().ToString("N"));
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

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    [InlineData(8, 4)]
    [InlineData(12, 6)]
    [InlineData(32, 6)]
    public void CreateOptimizedSessionOptions_ClampsThreadsAndSetsExpectedFlags(int processorCount, int expectedIntraThreads)
    {
        using SessionOptions opts = ComicBubbleOcrPipeline.CreateOptimizedSessionOptions(processorCount);

        Assert.Equal(GraphOptimizationLevel.ORT_ENABLE_ALL, opts.GraphOptimizationLevel);
        Assert.Equal(ExecutionMode.ORT_SEQUENTIAL, opts.ExecutionMode);
        Assert.Equal(1, opts.InterOpNumThreads);
        Assert.Equal(expectedIntraThreads, opts.IntraOpNumThreads);
        Assert.True(opts.EnableMemoryPattern);
        Assert.True(opts.EnableCpuMemArena);
    }

    [Fact]
    public void CreateOptimizedSessionOptions_DefaultOverload_UsesValidThreadBounds()
    {
        using SessionOptions opts = ComicBubbleOcrPipeline.CreateOptimizedSessionOptions();

        Assert.Equal(GraphOptimizationLevel.ORT_ENABLE_ALL, opts.GraphOptimizationLevel);
        Assert.Equal(ExecutionMode.ORT_SEQUENTIAL, opts.ExecutionMode);
        Assert.Equal(1, opts.InterOpNumThreads);
        Assert.InRange(opts.IntraOpNumThreads, 1, 6);
        Assert.True(opts.EnableMemoryPattern);
        Assert.True(opts.EnableCpuMemArena);
    }

    [Fact]
    public async Task PreCancelledCancellationToken_ThrowsImmediatelyWithoutLeakingSessionGate()
    {
        AppPaths paths = AppPaths.CreateAt(Path.Combine(_root, "cancel-gate"));
        using var pipeline = new ComicBubbleOcrPipeline(paths);
        DecodedPixelBuffer pixels = CreateSyntheticPixelBuffer(8, 8);

        using var cancelledCts = new CancellationTokenSource();
        cancelledCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.DetectTextRegionsAsync(pixels, cancelledCts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.RecognizePageAsync(pixels, cancelledCts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.RecognizeNormalizedRegionAsync(pixels, 0.1, 0.1, 0.5, 0.5, cancelledCts.Token));

        // Subsequent non-cancelled calls must still acquire and release _sessionGate cleanly (no deadlock).
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            pipeline.DetectTextRegionsAsync(pixels, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            pipeline.RecognizePageAsync(pixels, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            pipeline.RecognizeNormalizedRegionAsync(pixels, 0.1, 0.1, 0.5, 0.5, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        // Pre-cancelled call after failed calls still throws promptly, and ResetSessionsAsync acquires gate cleanly.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.RecognizePageAsync(pixels, cancelledCts.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        await pipeline.ResetSessionsAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var ocrService = new MangaOcrService(pipeline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ocrService.RecognizeAsync("page-1", pixels, "en", null, cancelledCts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ocrService.RecognizeNormalizedRegionAsync(pixels, 0.1, 0.1, 0.5, 0.5, "en", null, cancelledCts.Token));
    }

    [Fact]
    public void CreateCroppedBitmapFromPixels_CopiesExactPixelsAndDefendsBounds()
    {
        const int width = 8;
        const int height = 6;
        DecodedPixelBuffer pixels = CreateSyntheticPixelBuffer(width, height);

        // Normal interior crop
        using (SKBitmap interior = MangaOcrService.CreateCroppedBitmapFromPixels(pixels, 2, 1, 4, 3))
        {
            AssertCropMatchesSource(pixels, interior, srcX: 2, srcY: 1, expectedW: 4, expectedH: 3);
        }

        // Four corners
        using (SKBitmap topLeft = MangaOcrService.CreateCroppedBitmapFromPixels(pixels, 0, 0, 2, 2))
        {
            AssertCropMatchesSource(pixels, topLeft, srcX: 0, srcY: 0, expectedW: 2, expectedH: 2);
        }

        using (SKBitmap topRight = MangaOcrService.CreateCroppedBitmapFromPixels(pixels, 6, 0, 2, 2))
        {
            AssertCropMatchesSource(pixels, topRight, srcX: 6, srcY: 0, expectedW: 2, expectedH: 2);
        }

        using (SKBitmap bottomLeft = MangaOcrService.CreateCroppedBitmapFromPixels(pixels, 0, 4, 2, 2))
        {
            AssertCropMatchesSource(pixels, bottomLeft, srcX: 0, srcY: 4, expectedW: 2, expectedH: 2);
        }

        using (SKBitmap bottomRight = MangaOcrService.CreateCroppedBitmapFromPixels(pixels, 6, 4, 2, 2))
        {
            AssertCropMatchesSource(pixels, bottomRight, srcX: 6, srcY: 4, expectedW: 2, expectedH: 2);
        }

        // Out-of-bounds partial clamping
        using (SKBitmap clampedTopLeft = MangaOcrService.CreateCroppedBitmapFromPixels(pixels, -2, -1, 5, 4))
        {
            AssertCropMatchesSource(pixels, clampedTopLeft, srcX: 0, srcY: 0, expectedW: 3, expectedH: 3);
        }

        using (SKBitmap clampedBottomRight = MangaOcrService.CreateCroppedBitmapFromPixels(pixels, 6, 4, 5, 5))
        {
            AssertCropMatchesSource(pixels, clampedBottomRight, srcX: 6, srcY: 4, expectedW: 2, expectedH: 2);
        }

        // 1x1 crop
        using (SKBitmap singlePixel = MangaOcrService.CreateCroppedBitmapFromPixels(pixels, 5, 3, 1, 1))
        {
            AssertCropMatchesSource(pixels, singlePixel, srcX: 5, srcY: 3, expectedW: 1, expectedH: 1);
        }

        // Illegal / empty dimensions and non-intersecting rectangles
        Assert.Throws<ArgumentNullException>(() => MangaOcrService.CreateCroppedBitmapFromPixels(null!, 0, 0, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => MangaOcrService.CreateCroppedBitmapFromPixels(pixels, 0, 0, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => MangaOcrService.CreateCroppedBitmapFromPixels(pixels, 0, 0, -2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => MangaOcrService.CreateCroppedBitmapFromPixels(pixels, 0, 0, 2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MangaOcrService.CreateCroppedBitmapFromPixels(pixels, 0, 0, 2, -3));
        Assert.Throws<ArgumentOutOfRangeException>(() => MangaOcrService.CreateCroppedBitmapFromPixels(pixels, width, 0, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => MangaOcrService.CreateCroppedBitmapFromPixels(pixels, -4, 0, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => MangaOcrService.CreateCroppedBitmapFromPixels(pixels, 0, height, 2, 2));
    }

    [Fact]
    public void PinnedBitmapScope_MatchesSourcePixelsZeroCopyAndSupportsIdempotentDispose()
    {
        DecodedPixelBuffer pixels = CreateSyntheticPixelBuffer(4, 3);

        var scope = new ComicBubbleOcrPipeline.PinnedBitmapScope(pixels);
        try
        {
            SKBitmap bmp = scope.Bitmap;
            Assert.Equal(4, bmp.Width);
            Assert.Equal(3, bmp.Height);
            Assert.Equal(SKColorType.Bgra8888, bmp.ColorType);
            Assert.True(bmp.GetPixelSpan().SequenceEqual(pixels.BgraPixels));

            // Verify zero-copy pinning: mutating the backing array is immediately visible through SKBitmap.
            pixels.BgraPixels[0] ^= 0x55;
            Assert.Equal(pixels.BgraPixels[0], bmp.GetPixelSpan()[0]);
        }
        finally
        {
            scope.Dispose();
        }

        // Idempotent repeat Dispose must not throw, and Bitmap getter must throw ObjectDisposedException afterwards.
        scope.Dispose();
        scope.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = scope.Bitmap);
    }

    private static DecodedPixelBuffer CreateSyntheticPixelBuffer(int width, int height)
    {
        byte[] bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = (y * width + x) * 4;
                bgra[idx + 0] = (byte)((x * 29 + y * 7 + 11) & 0xFF);   // B
                bgra[idx + 1] = (byte)((x * 13 + y * 41 + 23) & 0xFF);  // G
                bgra[idx + 2] = (byte)((x * 3 + y * 19 + 53) & 0xFF);   // R
                bgra[idx + 3] = 255;                                     // A
            }
        }

        return new DecodedPixelBuffer(bgra, width, height);
    }

    private static void AssertCropMatchesSource(
        DecodedPixelBuffer source,
        SKBitmap crop,
        int srcX,
        int srcY,
        int expectedW,
        int expectedH)
    {
        Assert.Equal(expectedW, crop.Width);
        Assert.Equal(expectedH, crop.Height);
        ReadOnlySpan<byte> cropSpan = crop.GetPixelSpan();
        int srcStride = source.Width * 4;
        int dstStride = crop.RowBytes;

        for (int row = 0; row < expectedH; row++)
        {
            for (int col = 0; col < expectedW; col++)
            {
                int srcOff = (srcY + row) * srcStride + (srcX + col) * 4;
                int dstOff = row * dstStride + col * 4;
                Assert.Equal(source.BgraPixels[srcOff + 0], cropSpan[dstOff + 0]);
                Assert.Equal(source.BgraPixels[srcOff + 1], cropSpan[dstOff + 1]);
                Assert.Equal(source.BgraPixels[srcOff + 2], cropSpan[dstOff + 2]);
                Assert.Equal(source.BgraPixels[srcOff + 3], cropSpan[dstOff + 3]);
            }
        }
    }
}

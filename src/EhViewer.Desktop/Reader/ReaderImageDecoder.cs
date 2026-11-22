using System.Runtime.InteropServices;
using SkiaSharp;

namespace EhViewer.Desktop.Reader;

/// <summary>
/// Decodes one page image at a bounded pixel budget. Sampling happens at decode time via
/// <see cref="SKCodec"/> so a 100-megapixel source never materialises as a full-size bitmap.
/// </summary>
public static class ReaderImageDecoder
{
    /// <summary>
    /// Upper bound on decoded pixels for a regular page. Beyond this the image is treated as a
    /// "large" page and handed to the tiled renderer instead.
    /// </summary>
    public const long LargeImagePixelThreshold = 40_000_000;

    /// <summary>Tile edge length used by the tiled renderer for oversized pages.</summary>
    public const int TileSize = 512;

    public readonly record struct DecodedPageResult(
        SKBitmap Bitmap,
        bool IsAnimated,
        IReadOnlyList<int> FrameDurations);

    /// <summary>
    /// Decodes <paramref name="payload"/> and reads animation metadata using a single
    /// <see cref="SKData"/> and <see cref="SKCodec"/> pass.
    /// </summary>
    public static DecodedPageResult? DecodeWithMetadata(
        ReadOnlySpan<byte> payload,
        int maxWidth,
        int maxHeight,
        bool forceSrgb = false)
    {
        if (payload.Length == 0 || maxWidth <= 0 || maxHeight <= 0)
        {
            return null;
        }

        using SKData data = SKData.CreateCopy(payload);
        using SKCodec? codec = SKCodec.Create(data);
        if (codec is null)
        {
            return null;
        }

        bool isAnimated = codec.FrameCount > 1;
        IReadOnlyList<int> durations = isAnimated ? ExtractFrameDurations(codec) : [];
        SKBitmap? bitmap = ConvertToSrgbIfRequested(DecodeWithCodec(codec, maxWidth, maxHeight), forceSrgb);
        if (bitmap is null)
        {
            return null;
        }

        return new DecodedPageResult(bitmap, isAnimated, durations);
    }

    /// <summary>
    /// Decodes <paramref name="payload"/> to fit inside <paramref name="maxWidth"/> /
    /// <paramref name="maxHeight"/>, preserving aspect ratio.
    /// </summary>
    public static SKBitmap? Decode(ReadOnlySpan<byte> payload, int maxWidth, int maxHeight, bool forceSrgb) =>
        ConvertToSrgbIfRequested(Decode(payload, maxWidth, maxHeight), forceSrgb);

    /// <summary>
    /// Decodes a rectangular region from <paramref name="payload"/> scaled within <paramref name="maxEdge"/>
    /// (used for high-resolution tile zoom / giant strip sampling).
    /// </summary>
    public static SKBitmap? DecodeRegion(ReadOnlySpan<byte> payload, SKRectI sourceRegion, int maxEdge, bool forceSrgb = false)
    {
        if (payload.Length == 0 || maxEdge <= 0 || sourceRegion.Width <= 0 || sourceRegion.Height <= 0)
        {
            return null;
        }

        try
        {
            using SKData data = SKData.CreateCopy(payload);
            using SKCodec? codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
            {
                return null;
            }

            int fullW = codec.Info.Width;
            int fullH = codec.Info.Height;
            var clamped = new SKRectI(
                Math.Clamp(sourceRegion.Left, 0, fullW),
                Math.Clamp(sourceRegion.Top, 0, fullH),
                Math.Clamp(sourceRegion.Right, 0, fullW),
                Math.Clamp(sourceRegion.Bottom, 0, fullH));
            if (clamped.Width <= 0 || clamped.Height <= 0)
            {
                return null;
            }

            SKSizeI targetSize = ComputeTargetSize(clamped.Width, clamped.Height, maxEdge, maxEdge);
            int dstW = targetSize.Width;
            int dstH = targetSize.Height;
            SKColorSpace? colorSpace = forceSrgb ? SKColorSpace.CreateSrgb() : codec.Info.ColorSpace;
            var dstInfo = new SKImageInfo(dstW, dstH, SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace);

            using SKImage? encodedImage = SKImage.FromEncodedData(data);
            if (encodedImage is not null)
            {
                using SKImage? subset = encodedImage.Subset(clamped);
                if (subset is not null)
                {
                    var result = new SKBitmap(dstInfo);
                    using (var canvas = new SKCanvas(result))
                    {
                        canvas.Clear(SKColors.Transparent);
                        canvas.DrawImage(
                            subset,
                            new SKRect(0, 0, dstW, dstH),
                            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    }

                    return result;
                }
            }

            // Fallback: decode full frame capped reasonably then sample the proportional sub-rect.
            using SKBitmap? full = Decode(payload, Math.Max(maxEdge * 2, 4096), Math.Max(maxEdge * 2, 4096), forceSrgb);
            if (full is null || full.Width <= 0 || full.Height <= 0)
            {
                return null;
            }

            float sx = (float)full.Width / fullW;
            float sy = (float)full.Height / fullH;
            var scaledSrc = new SKRect(clamped.Left * sx, clamped.Top * sy, clamped.Right * sx, clamped.Bottom * sy);
            var fallback = new SKBitmap(dstInfo);
            using (var canvas = new SKCanvas(fallback))
            using (SKImage img = SKImage.FromBitmap(full))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawImage(img, scaledSrc, new SKRect(0, 0, dstW, dstH), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            }

            return fallback;
        }
        catch
        {
            return null;
        }
    }

    internal static SKBitmap? ConvertToSrgbIfRequested(SKBitmap? bitmap, bool forceSrgb)
    {
        if (bitmap is null || !forceSrgb)
        {
            return bitmap;
        }

        if (bitmap.ColorSpace is { IsSrgb: true })
        {
            return bitmap;
        }

        var srgbInfo = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        var converted = new SKBitmap(srgbInfo);
        using (var canvas = new SKCanvas(converted))
        using (SKImage img = SKImage.FromBitmap(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(img, 0, 0, SKSamplingOptions.Default);
        }

        bitmap.Dispose();
        return converted;
    }

    /// <summary>
    /// Decodes <paramref name="payload"/> to fit inside <paramref name="maxWidth"/> /
    /// <paramref name="maxHeight"/>, preserving aspect ratio. Returns <see langword="null"/> when
    /// the bytes are not a decodable image.
    /// </summary>
    public static SKBitmap? Decode(ReadOnlySpan<byte> payload, int maxWidth, int maxHeight)
    {
        if (payload.Length == 0 || maxWidth <= 0 || maxHeight <= 0)
        {
            return null;
        }

        using SKData data = SKData.CreateCopy(payload);
        using SKCodec? codec = SKCodec.Create(data);
        if (codec is null)
        {
            return null;
        }

        return DecodeWithCodec(codec, maxWidth, maxHeight);
    }

    /// <summary>
    /// Decodes a single animation frame (GIF / APNG / animated WebP). Frame 0 is identical to
    /// <see cref="Decode"/>. Returns <see langword="null"/> for stills, out-of-range frame indexes,
    /// or undecodable payloads.
    /// </summary>
    public static SKBitmap? DecodeFrame(ReadOnlySpan<byte> payload, int frameIndex, int maxEdge)
    {
        if (payload.Length == 0 || frameIndex < 0 || maxEdge <= 0)
        {
            return null;
        }

        using SKData data = SKData.CreateCopy(payload);
        using SKCodec? codec = SKCodec.Create(data);
        return codec is null ? null : DecodeFrame(codec, frameIndex, maxEdge);
    }

    internal static SKBitmap? DecodeFrame(SKCodec codec, int frameIndex, int maxEdge, bool forceSrgb = false)
    {
        if (frameIndex < 0 || maxEdge <= 0 || frameIndex >= codec.FrameCount)
        {
            return null;
        }

        SKSizeI target = ComputeTargetSize(codec.Info.Width, codec.Info.Height, maxEdge, maxEdge);
        var info = new SKImageInfo(target.Width, target.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var options = new SKCodecOptions(frameIndex);

        SKCodecResult result;
        try
        {
            result = codec.GetPixels(info, bitmap.GetPixels(), options);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            bitmap.Dispose();
            return null;
        }

        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
        {
            return ConvertToSrgbIfRequested(bitmap, forceSrgb);
        }

        bitmap.Dispose();
        return null;
    }

    /// <summary>Reusable animated codec wrapper avoiding repeated payload copies across frame ticks.</summary>
    internal sealed class AnimatedFrameDecoder : IDisposable
    {
        private readonly SKData _data;
        private readonly SKCodec _codec;

        private AnimatedFrameDecoder(SKData data, SKCodec codec)
        {
            _data = data;
            _codec = codec;
        }

        public static AnimatedFrameDecoder? TryCreate(ReadOnlySpan<byte> payload)
        {
            if (payload.Length == 0)
            {
                return null;
            }

            SKData data = SKData.CreateCopy(payload);
            SKCodec? codec = SKCodec.Create(data);
            if (codec is null)
            {
                data.Dispose();
                return null;
            }

            return new AnimatedFrameDecoder(data, codec);
        }

        public SKBitmap? DecodeFrame(int frameIndex, int maxEdge, bool forceSrgb) =>
            ReaderImageDecoder.DecodeFrame(_codec, frameIndex, maxEdge, forceSrgb);

        public void Dispose()
        {
            _codec.Dispose();
            _data.Dispose();
        }
    }

    /// <summary>Probes intrinsic dimensions without decoding pixels.</summary>
    public static SKSizeI ProbeSize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return SKSizeI.Empty;
        }

        using SKData data = SKData.CreateCopy(payload);
        using SKCodec? codec = SKCodec.Create(data);
        return codec is null ? SKSizeI.Empty : codec.Info.Size;
    }

    /// <summary>True when the image exceeds the pixel budget and needs tiled rendering.</summary>
    public static bool RequiresTiling(SKSizeI size)
        => (long)size.Width * size.Height > LargeImagePixelThreshold;

    private static SKBitmap? DecodeWithCodec(SKCodec codec, int maxWidth, int maxHeight)
    {
        SKImageInfo source = codec.Info;
        if (source.Width <= 0 || source.Height <= 0)
        {
            return null;
        }

        SKSizeI target = ComputeTargetSize(source.Width, source.Height, maxWidth, maxHeight);
        var decodeInfo = new SKImageInfo(target.Width, target.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

        var bitmap = new SKBitmap(decodeInfo);
        SKCodecResult result = codec.GetPixels(decodeInfo, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
        {
            return bitmap;
        }

        bitmap.Dispose();

        // Try the codec's closest natively supported downscale factor (JPEG/WebP DCT scaling) before
        // falling back to scanline streaming for large non-scalable PNG/BMP images.
        float desiredScale = Math.Min((float)target.Width / source.Width, (float)target.Height / source.Height);
        SKSizeI codecScaled = codec.GetScaledDimensions(desiredScale);
        if (codecScaled.Width > 0
            && codecScaled.Height > 0
            && (codecScaled.Width != source.Width || codecScaled.Height != source.Height)
            && !RequiresTiling(codecScaled))
        {
            var scaledInfo = new SKImageInfo(codecScaled.Width, codecScaled.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var intermediate = new SKBitmap(scaledInfo);
            bool ownershipTransferred = false;
            try
            {
                if (codec.GetPixels(scaledInfo, intermediate.GetPixels()) is SKCodecResult.Success or SKCodecResult.IncompleteInput)
                {
                    if (codecScaled == target)
                    {
                        ownershipTransferred = true;
                        return intermediate;
                    }

                    SKBitmap? resized = intermediate.Resize(decodeInfo, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    if (resized is not null)
                    {
                        return resized;
                    }
                }
            }
            finally
            {
                if (!ownershipTransferred)
                {
                    intermediate.Dispose();
                }
            }
        }

        if (RequiresTiling(source.Size))
        {
            // Large non-scalable codecs (e.g. >40MP BMP/progressive) stream row-by-row when
            // StartScanlineDecode is supported; codecs like SkPngRustCodec in SkiaSharp 4 return
            // Unimplemented for scanline streaming, so fall through to native+dispose below up to
            // a safe memory ceiling instead of dropping the page.
            if (TryDecodeScanlineDownsampled(codec, source, target, out SKBitmap? streamed))
            {
                return streamed;
            }

            if ((long)source.Width * source.Height > 150_000_000L)
            {
                return null;
            }
        }

        var nativeInfo = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var native = new SKBitmap(nativeInfo);
        if (codec.GetPixels(nativeInfo, native.GetPixels()) is SKCodecResult.Success or SKCodecResult.IncompleteInput)
        {
            if (target.Width == source.Width && target.Height == source.Height)
            {
                return native;
            }

            SKBitmap? resized = native.Resize(decodeInfo, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            native.Dispose();
            return resized;
        }

        native.Dispose();
        return null;
    }

    private static bool TryDecodeScanlineDownsampled(
        SKCodec codec,
        SKImageInfo source,
        SKSizeI target,
        out SKBitmap? result)
    {
        result = null;
        SKAlphaType scanAlpha = source.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var scanInfo = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, scanAlpha);
        if (codec.StartScanlineDecode(scanInfo) != SKCodecResult.Success)
        {
            return false;
        }

        var dstInfo = new SKImageInfo(target.Width, target.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var dstBitmap = new SKBitmap(dstInfo);
        byte[] rowBuffer = new byte[checked(source.Width * 4)];
        GCHandle rowHandle = GCHandle.Alloc(rowBuffer, GCHandleType.Pinned);

        try
        {
            IntPtr rowPtr = rowHandle.AddrOfPinnedObject();
            Span<byte> dstPixels = dstBitmap.GetPixelSpan();
            int dstStride = dstBitmap.RowBytes;
            bool bottomUp = codec.ScanlineOrder == SKCodecScanlineOrder.BottomUp;

            int[] srcXIndices = new int[target.Width];
            for (int x = 0; x < target.Width; x++)
            {
                srcXIndices[x] = Math.Clamp((int)((long)x * source.Width / target.Width), 0, source.Width - 1);
            }

            if (bottomUp)
            {
                for (int srcY = source.Height - 1; srcY >= 0; srcY--)
                {
                    if (codec.GetScanlines(rowPtr, 1, rowBuffer.Length) <= 0)
                    {
                        dstBitmap.Dispose();
                        return false;
                    }

                    int dstY = (int)((long)srcY * target.Height / source.Height);
                    if (dstY < target.Height)
                    {
                        CopySampledRow(rowBuffer, dstPixels.Slice(dstY * dstStride, target.Width * 4), srcXIndices);
                    }
                }
            }
            else
            {
                int srcY = 0;
                int nextTargetY = 0;
                while (srcY < source.Height && nextTargetY < target.Height)
                {
                    int desiredSrcY = (int)((long)nextTargetY * source.Height / target.Height);
                    if (srcY < desiredSrcY)
                    {
                        int skip = desiredSrcY - srcY;
                        if (!codec.SkipScanlines(skip))
                        {
                            for (int s = 0; s < skip; s++)
                            {
                                codec.GetScanlines(rowPtr, 1, rowBuffer.Length);
                            }
                        }

                        srcY += skip;
                        continue;
                    }

                    if (codec.GetScanlines(rowPtr, 1, rowBuffer.Length) <= 0)
                    {
                        dstBitmap.Dispose();
                        return false;
                    }

                    CopySampledRow(rowBuffer, dstPixels.Slice(nextTargetY * dstStride, target.Width * 4), srcXIndices);
                    srcY++;
                    nextTargetY++;
                }
            }

            result = dstBitmap;
            return true;
        }
        catch
        {
            dstBitmap.Dispose();
            return false;
        }
        finally
        {
            rowHandle.Free();
        }
    }

    private static void CopySampledRow(ReadOnlySpan<byte> srcRow, Span<byte> dstRow, ReadOnlySpan<int> srcXIndices)
    {
        ReadOnlySpan<uint> srcPixels = MemoryMarshal.Cast<byte, uint>(srcRow);
        Span<uint> dstPixels = MemoryMarshal.Cast<byte, uint>(dstRow);
        for (int x = 0; x < srcXIndices.Length; x++)
        {
            dstPixels[x] = srcPixels[srcXIndices[x]];
        }
    }

    /// <summary>
    /// Computes the largest size that fits within the bounds while preserving aspect ratio.
    /// Upscaling is never performed.
    /// </summary>
    public static SKSizeI ComputeTargetSize(int width, int height, int maxWidth, int maxHeight)
    {
        if (width <= 0 || height <= 0)
        {
            return new SKSizeI(1, 1);
        }

        double scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
        if (scale >= 1.0)
        {
            return new SKSizeI(width, height);
        }

        return new SKSizeI(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>Frame count for animated formats (GIF, APNG, animated WebP); 1 for stills.</summary>
    public static int GetFrameCount(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return 0;
        }

        using SKData data = SKData.CreateCopy(payload);
        using SKCodec? codec = SKCodec.Create(data);
        return codec?.FrameCount ?? 0;
    }

    /// <summary>Per-frame durations in milliseconds; empty for stills.</summary>
    public static IReadOnlyList<int> GetFrameDurations(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return [];
        }

        using SKData data = SKData.CreateCopy(payload);
        using SKCodec? codec = SKCodec.Create(data);
        if (codec is null || codec.FrameCount <= 1)
        {
            return [];
        }

        return ExtractFrameDurations(codec);
    }

    private static List<int> ExtractFrameDurations(SKCodec codec)
    {
        SKCodecFrameInfo[] frames = codec.FrameInfo;
        var durations = new List<int>(frames.Length);
        foreach (SKCodecFrameInfo frame in frames)
        {
            // Skia reports durations in milliseconds already; clamp negatives (some encoders emit -1).
            durations.Add(Math.Max(0, frame.Duration));
        }

        return durations;
    }
}

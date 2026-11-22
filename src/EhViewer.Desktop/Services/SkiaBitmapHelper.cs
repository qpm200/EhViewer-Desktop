using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Off-thread-decodable BGRA8888 pixel snapshot ready for fast <see cref="WriteableBitmap"/> upload.
/// </summary>
public sealed class DecodedPixelBuffer
{
    public DecodedPixelBuffer(byte[] bgraPixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        int expectedLength = checked(width * height * 4);
        if (bgraPixels.Length != expectedLength)
        {
            throw new ArgumentException($"Expected {expectedLength} pixel bytes, got {bgraPixels.Length}.", nameof(bgraPixels));
        }

        BgraPixels = bgraPixels;
        Width = width;
        Height = height;
    }

    public byte[] BgraPixels { get; }
    public int Width { get; }
    public int Height { get; }
}

/// <summary>
/// Converts <see cref="SKBitmap"/> images (and sprite sheet sub-rectangles) to WinUI
/// <see cref="WriteableBitmap"/> instances via direct BGRA8888 pixel buffer copies instead of
/// re-encoding PNG streams on the UI thread.
/// </summary>
public static class SkiaBitmapHelper
{
    public static DecodedPixelBuffer? ExtractBgraPixels(SKBitmap? bitmap, SKRectI? clip = null)
    {
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return null;
        }

        SKRectI srcRect = clip.HasValue
            ? ClampClip(clip.Value, bitmap.Width, bitmap.Height)
            : new SKRectI(0, 0, bitmap.Width, bitmap.Height);

        if (srcRect.Width <= 0 || srcRect.Height <= 0)
        {
            return null;
        }

        bool isFullBitmap = srcRect.Left == 0
            && srcRect.Top == 0
            && srcRect.Width == bitmap.Width
            && srcRect.Height == bitmap.Height;

        int byteCount = checked(srcRect.Width * srcRect.Height * 4);
        byte[] pixels = new byte[byteCount];

        if (isFullBitmap
            && bitmap.ColorType == SKColorType.Bgra8888
            && bitmap.AlphaType is SKAlphaType.Premul or SKAlphaType.Opaque
            && bitmap.RowBytes == srcRect.Width * 4)
        {
            ReadOnlySpan<byte> sourceSpan = bitmap.GetPixelSpan();
            if (sourceSpan.Length >= byteCount)
            {
                sourceSpan[..byteCount].CopyTo(pixels);
                return new DecodedPixelBuffer(pixels, srcRect.Width, srcRect.Height);
            }
        }

        var targetInfo = new SKImageInfo(srcRect.Width, srcRect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var target = new SKBitmap(targetInfo);
        using (var canvas = new SKCanvas(target))
        using (SKImage image = SKImage.FromBitmap(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(
                image,
                new SKRect(srcRect.Left, srcRect.Top, srcRect.Right, srcRect.Bottom),
                new SKRect(0, 0, srcRect.Width, srcRect.Height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }

        ReadOnlySpan<byte> convertedSpan = target.GetPixelSpan();
        convertedSpan[..byteCount].CopyTo(pixels);
        return new DecodedPixelBuffer(pixels, srcRect.Width, srcRect.Height);
    }

    public static DecodedPixelBuffer? ExtractBgraPixels(WriteableBitmap? bitmap)
    {
        if (bitmap is null || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            return null;
        }

        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        int expectedBytes = checked(width * height * 4);
        byte[] pixels = bitmap.PixelBuffer.ToArray();
        if (pixels.Length < expectedBytes)
        {
            return null;
        }

        if (pixels.Length > expectedBytes)
        {
            pixels = pixels[..expectedBytes];
        }

        return new DecodedPixelBuffer(pixels, width, height);
    }

    public static bool CanCopyPixelsDirectly(SKBitmap? bitmap)
    {
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return false;
        }

        if (bitmap.ColorType != SKColorType.Bgra8888 ||
            bitmap.AlphaType is not (SKAlphaType.Premul or SKAlphaType.Opaque))
        {
            return false;
        }

        long expectedLong = (long)bitmap.Width * bitmap.Height * 4L;
        if (expectedLong > int.MaxValue || bitmap.RowBytes != bitmap.Width * 4)
        {
            return false;
        }

        return bitmap.GetPixelSpan().Length >= (int)expectedLong;
    }

    public static WriteableBitmap ToWriteableBitmap(DecodedPixelBuffer buffer) =>
        ToWriteableBitmap(buffer, reusable: null);

    public static WriteableBitmap ToWriteableBitmap(DecodedPixelBuffer buffer, WriteableBitmap? reusable)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        WriteableBitmap bitmap = reusable is not null
            && reusable.PixelWidth == buffer.Width
            && reusable.PixelHeight == buffer.Height
            ? reusable
            : new WriteableBitmap(buffer.Width, buffer.Height);
        using Stream stream = bitmap.PixelBuffer.AsStream();
        stream.Write(buffer.BgraPixels, 0, buffer.BgraPixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>
    /// Copies a compact BGRA8888 <see cref="SKBitmap"/> straight into a WinUI <see cref="WriteableBitmap"/>
    /// without allocating an intermediate managed pixel array, optionally reusing a same-sized bitmap in-place.
    /// </summary>
    public static WriteableBitmap? ToWriteableBitmapDirect(SKBitmap? bitmap, WriteableBitmap? reusable = null)
    {
        if (CanCopyPixelsDirectly(bitmap))
        {
            SKBitmap srcBitmap = bitmap!;
            int width = srcBitmap.Width;
            int height = srcBitmap.Height;
            int byteCount = checked(width * height * 4);
            WriteableBitmap target = reusable is not null
                && reusable.PixelWidth == width
                && reusable.PixelHeight == height
                ? reusable
                : new WriteableBitmap(width, height);

            using Stream stream = target.PixelBuffer.AsStream();
            stream.Write(srcBitmap.GetPixelSpan()[..byteCount]);
            target.Invalidate();
            return target;
        }

        DecodedPixelBuffer? buffer = ExtractBgraPixels(bitmap, clip: null);
        return buffer is null ? null : ToWriteableBitmap(buffer, reusable);
    }

    public static WriteableBitmap? ToWriteableBitmap(SKBitmap? bitmap, SKRectI? clip = null)
    {
        if (!clip.HasValue && CanCopyPixelsDirectly(bitmap))
        {
            return ToWriteableBitmapDirect(bitmap);
        }

        DecodedPixelBuffer? buffer = ExtractBgraPixels(bitmap, clip);
        return buffer is null ? null : ToWriteableBitmap(buffer);
    }

    private static SKRectI ClampClip(SKRectI clip, int bitmapWidth, int bitmapHeight)
    {
        int left = Math.Clamp(clip.Left, 0, bitmapWidth);
        int top = Math.Clamp(clip.Top, 0, bitmapHeight);
        int right = Math.Clamp(clip.Right, left, bitmapWidth);
        int bottom = Math.Clamp(clip.Bottom, top, bitmapHeight);
        return new SKRectI(left, top, right, bottom);
    }
}

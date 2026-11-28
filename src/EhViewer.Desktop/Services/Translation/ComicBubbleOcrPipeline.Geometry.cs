using SkiaSharp;

namespace EhViewer.Desktop.Services.Translation;

public sealed partial class ComicBubbleOcrPipeline
{
    internal static IReadOnlyList<DetectedBubbleRegion> PostProcessDetections(
        IReadOnlyList<DetectedBubbleRegion> rawDetections,
        int imageWidth,
        int imageHeight)
    {
        var textCandidates = new List<DetectedBubbleRegion>();
        var bubbles = new List<DetectedBubbleRegion>();

        foreach (DetectedBubbleRegion r in rawDetections)
        {
            float x1 = Math.Clamp((float)r.X1, 0, imageWidth);
            float y1 = Math.Clamp((float)r.Y1, 0, imageHeight);
            float x2 = Math.Clamp((float)r.X2, 0, imageWidth);
            float y2 = Math.Clamp((float)r.Y2, 0, imageHeight);
            if (x2 - x1 < 8 || y2 - y1 < 8)
            {
                continue;
            }

            var clamped = new DetectedBubbleRegion(x1, y1, x2, y2, r.Confidence, r.LabelId);
            if (r.LabelId == 1 && r.Confidence >= 0.35f)
            {
                textCandidates.Add(clamped);
            }
            else if (r.LabelId == 2 && r.Confidence >= 0.40f)
            {
                textCandidates.Add(clamped);
            }
            else if (r.LabelId == 0 && r.Confidence >= 0.55f)
            {
                bubbles.Add(clamped);
            }
        }

        List<DetectedBubbleRegion> kept = ApplyNmsWithContainment(textCandidates, iouThreshold: 0.50);

        // Recover any speech bubble that had no overlapping text_bubble detected inside it
        foreach (DetectedBubbleRegion bubble in ApplyNmsWithContainment(bubbles, iouThreshold: 0.55))
        {
            bool covered = kept.Any(t => IsCenterInside(t, bubble) || ComputeIoU(t, bubble) >= 0.25);
            if (!covered)
            {
                double insetX = (bubble.X2 - bubble.X1) * 0.12;
                double insetY = (bubble.Y2 - bubble.Y1) * 0.12;
                kept.Add(new DetectedBubbleRegion(
                    bubble.X1 + insetX,
                    bubble.Y1 + insetY,
                    bubble.X2 - insetX,
                    bubble.Y2 - insetY,
                    bubble.Confidence,
                    LabelId: 0));
            }
        }

        // Manga reading order: cluster overlapping vertical-tier bands (strict weak order),
        // then order top-to-bottom bands and right-to-left within each band.
        return SortInMangaReadingOrder(kept, imageHeight);
    }

    private static List<DetectedBubbleRegion> SortInMangaReadingOrder(
        List<DetectedBubbleRegion> regions,
        int imageHeight)
    {
        if (regions.Count <= 1)
        {
            return regions;
        }

        double centerTolerance = Math.Max(45.0, imageHeight * 0.06);
        var byTop = regions
            .OrderBy(static r => (r.Y1 + r.Y2) * 0.5)
            .ThenByDescending(static r => r.X2)
            .ToList();

        var banded = new List<(DetectedBubbleRegion Region, int Band)>(byTop.Count);
        int currentBand = 0;
        double bandMinY = byTop[0].Y1;
        double bandMaxY = byTop[0].Y2;
        double bandCenter = (byTop[0].Y1 + byTop[0].Y2) * 0.5;

        foreach (DetectedBubbleRegion region in byTop)
        {
            double cy = (region.Y1 + region.Y2) * 0.5;
            double overlapY = Math.Max(0, Math.Min(bandMaxY, region.Y2) - Math.Max(bandMinY, region.Y1));
            double minH = Math.Max(1.0, Math.Min(bandMaxY - bandMinY, region.Y2 - region.Y1));
            bool sameBand = overlapY / minH >= 0.40 || Math.Abs(cy - bandCenter) <= centerTolerance;

            if (!sameBand)
            {
                currentBand++;
                bandMinY = region.Y1;
                bandMaxY = region.Y2;
                bandCenter = cy;
            }
            else
            {
                bandMinY = Math.Min(bandMinY, region.Y1);
                bandMaxY = Math.Max(bandMaxY, region.Y2);
                bandCenter = (bandMinY + bandMaxY) * 0.5;
            }

            banded.Add((region, currentBand));
        }

        return banded
            .OrderBy(static item => item.Band)
            .ThenByDescending(static item => (item.Region.X1 + item.Region.X2) * 0.5)
            .ThenBy(static item => item.Region.Y1)
            .Select(static item => item.Region)
            .ToList();
    }

    internal static string MergeOverlappingRecognitions(string first, string second, out int overlapLength)
    {
        string a = NormalizeRecognizedText(first);
        string b = NormalizeRecognizedText(second);
        overlapLength = 0;

        if (string.IsNullOrEmpty(a))
        {
            return b;
        }

        if (string.IsNullOrEmpty(b))
        {
            return a;
        }

        int maxOv = Math.Min(a.Length, b.Length);
        for (int k = maxOv; k >= 2; k--)
        {
            if (a.AsSpan(a.Length - k).SequenceEqual(b.AsSpan(0, k)))
            {
                overlapLength = k;
                return NormalizeRecognizedText(a + b[k..]);
            }
        }

        // Allow dropping 1 edge-clipped noise char right on a cut boundary while still requiring >= 2 matched chars
        for (int dropA = 0; dropA <= 1 && dropA < a.Length; dropA++)
        {
            for (int dropB = 0; dropB <= 1 && dropB < b.Length; dropB++)
            {
                if (dropA == 0 && dropB == 0)
                {
                    continue;
                }

                ReadOnlySpan<char> sa = a.AsSpan(0, a.Length - dropA);
                ReadOnlySpan<char> sb = b.AsSpan(dropB);
                int lim = Math.Min(sa.Length, sb.Length);
                for (int k = lim; k >= 2; k--)
                {
                    if (sa[^k..].SequenceEqual(sb[..k]))
                    {
                        overlapLength = k;
                        return NormalizeRecognizedText(string.Concat(sa, sb[k..]));
                    }
                }
            }
        }

        return NormalizeRecognizedText(a + b);
    }

    internal static bool HasCentralVerticalColumnGutter(SKBitmap pageBitmap, SKRect rect)
    {
        int x1 = Math.Clamp((int)Math.Floor(rect.Left), 0, pageBitmap.Width - 1);
        int y1 = Math.Clamp((int)Math.Floor(rect.Top), 0, pageBitmap.Height - 1);
        int x2 = Math.Clamp((int)Math.Ceiling(rect.Right), x1 + 1, pageBitmap.Width);
        int y2 = Math.Clamp((int)Math.Ceiling(rect.Bottom), y1 + 1, pageBitmap.Height);
        int w = x2 - x1;
        int h = y2 - y1;
        if (w < 40 || h < 40)
        {
            return false;
        }

        ReadOnlySpan<byte> px = pageBitmap.GetPixelSpan();
        int rowBytes = pageBitmap.RowBytes;

        // Estimate background polarity so white text in dark/black bubbles is detected properly too
        int bgStepY = Math.Max(1, h / 20);
        int bgStepX = Math.Max(1, w / 20);
        var bgLumas = new List<int>(441);
        for (int y = y1; y < y2; y += bgStepY)
        {
            int rowOff = y * rowBytes;
            for (int x = x1; x < x2; x += bgStepX)
            {
                int p = rowOff + x * 4;
                bgLumas.Add(cbLuma(px[p + 2], px[p + 1], px[p + 0]));
            }
        }

        bgLumas.Sort();
        int medianBg = bgLumas.Count > 0 ? bgLumas[bgLumas.Count / 2] : 240;
        bool bgIsDark = medianBg < 110;
        int inkThreshold = bgIsDark
            ? Math.Clamp(medianBg + 55, 130, 210)
            : Math.Clamp(medianBg - 55, 70, 145);

        int yStep = Math.Max(1, h / 80);
        int samplesPerCol = Math.Max(1, (h + yStep - 1) / yStep);
        Span<int> colInkCounts = w <= 256 ? stackalloc int[w] : new int[w];

        for (int y = y1; y < y2; y += yStep)
        {
            int rowOff = y * rowBytes;
            for (int dx = 0; dx < w; dx++)
            {
                int p = rowOff + (x1 + dx) * 4;
                int luma = cbLuma(px[p + 2], px[p + 1], px[p + 0]);
                bool isInk = bgIsDark ? luma > inkThreshold : luma < inkThreshold;
                if (isInk)
                {
                    colInkCounts[dx]++;
                }
            }
        }

        int leftEnd = (int)(w * 0.35);
        int rightStart = (int)(w * 0.65);
        bool leftHasInk = false;
        bool rightHasInk = false;
        for (int dx = 2; dx < leftEnd; dx++)
        {
            if (colInkCounts[dx] >= Math.Max(2, samplesPerCol / 8))
            {
                leftHasInk = true;
                break;
            }
        }

        for (int dx = rightStart; dx < w - 2; dx++)
        {
            if (colInkCounts[dx] >= Math.Max(2, samplesPerCol / 8))
            {
                rightHasInk = true;
                break;
            }
        }

        if (!leftHasInk || !rightHasInk)
        {
            return false;
        }

        int quietWidth = 0;
        for (int dx = leftEnd; dx < rightStart; dx++)
        {
            if (colInkCounts[dx] <= 1)
            {
                quietWidth++;
            }
        }

        return quietWidth >= Math.Max(3, (rightStart - leftEnd) / 3);
    }

    internal static int FindCleanestHorizontalSplit(SKBitmap pageBitmap, SKRect rect, float minY, float maxY)
    {
        int x1 = Math.Clamp((int)Math.Floor(rect.Left), 0, Math.Max(0, pageBitmap.Width - 1));
        int x2 = Math.Clamp((int)Math.Ceiling(rect.Right), x1 + 1, pageBitmap.Width);
        int yStart = Math.Clamp((int)Math.Floor(minY), 0, Math.Max(0, pageBitmap.Height - 1));
        int yEnd = Math.Clamp((int)Math.Ceiling(maxY), yStart, Math.Max(0, pageBitmap.Height - 1));
        int midY = (yStart + yEnd) / 2;
        int bestY = midY;
        long bestEnergy = long.MaxValue;

        ReadOnlySpan<byte> px = pageBitmap.GetPixelSpan();
        int rowBytes = pageBitmap.RowBytes;
        for (int y = yStart; y <= yEnd; y++)
        {
            // Minimize intra-line luminance gradient so both black-on-white and white-on-black dark bubbles
            // split on stroke-free quiet scanlines instead of white text strokes.
            int rowOff = y * rowBytes;
            int p0 = rowOff + x1 * 4;
            int prev = cbLuma(px[p0 + 2], px[p0 + 1], px[p0 + 0]);
            long grad = 0;
            for (int x = x1 + 1; x < x2; x++)
            {
                int p = rowOff + x * 4;
                int cur = cbLuma(px[p + 2], px[p + 1], px[p + 0]);
                grad += Math.Abs(cur - prev);
                prev = cur;
            }

            long energy = grad + Math.Abs(y - midY) * 2L;
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                bestY = y;
            }
        }

        return bestY;
    }

    internal static int FindCleanestVerticalSplit(SKBitmap pageBitmap, SKRect rect, float minX, float maxX)
    {
        int y1 = Math.Clamp((int)Math.Floor(rect.Top), 0, Math.Max(0, pageBitmap.Height - 1));
        int y2 = Math.Clamp((int)Math.Ceiling(rect.Bottom), y1 + 1, pageBitmap.Height);
        int xStart = Math.Clamp((int)Math.Floor(minX), 0, Math.Max(0, pageBitmap.Width - 1));
        int xEnd = Math.Clamp((int)Math.Ceiling(maxX), xStart, Math.Max(0, pageBitmap.Width - 1));
        int midX = (xStart + xEnd) / 2;
        int bestX = midX;
        long bestEnergy = long.MaxValue;

        ReadOnlySpan<byte> px = pageBitmap.GetPixelSpan();
        int rowBytes = pageBitmap.RowBytes;
        for (int x = xStart; x <= xEnd; x++)
        {
            int p0 = y1 * rowBytes + x * 4;
            int prev = cbLuma(px[p0 + 2], px[p0 + 1], px[p0 + 0]);
            long grad = 0;
            for (int y = y1 + 1; y < y2; y++)
            {
                int p = y * rowBytes + x * 4;
                int cur = cbLuma(px[p + 2], px[p + 1], px[p + 0]);
                grad += Math.Abs(cur - prev);
                prev = cur;
            }

            long energy = grad + Math.Abs(x - midX) * 2L;
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                bestX = x;
            }
        }

        return bestX;
    }

    internal static (uint FillArgb, uint TextArgb) SampleRegionColors(
        DecodedPixelBuffer pixels,
        double normX,
        double normY,
        double normWidth,
        double normHeight)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        using PinnedBitmapScope scope = CreateBitmapFromPixels(pixels);
        SKBitmap bmp = scope.Bitmap;
        float x1 = Math.Clamp((float)(normX * bmp.Width), 0, Math.Max(0, bmp.Width - 2));
        float y1 = Math.Clamp((float)(normY * bmp.Height), 0, Math.Max(0, bmp.Height - 2));
        float x2 = Math.Clamp((float)((normX + normWidth) * bmp.Width), x1 + 2, bmp.Width);
        float y2 = Math.Clamp((float)((normY + normHeight) * bmp.Height), y1 + 2, bmp.Height);
        return SampleBubbleColors(bmp, new SKRect(x1, y1, x2, y2));
    }

    internal static (uint FillArgb, uint TextArgb) SampleBubbleColors(SKBitmap pageBitmap, SKRect rect)
    {
        int x1 = Math.Clamp((int)Math.Floor(rect.Left), 0, Math.Max(0, pageBitmap.Width - 1));
        int y1 = Math.Clamp((int)Math.Floor(rect.Top), 0, Math.Max(0, pageBitmap.Height - 1));
        int x2 = Math.Clamp((int)Math.Ceiling(rect.Right), x1 + 1, pageBitmap.Width);
        int y2 = Math.Clamp((int)Math.Ceiling(rect.Bottom), y1 + 1, pageBitmap.Height);

        int insetX = Math.Min(4, Math.Max(0, (x2 - x1) / 8));
        int insetY = Math.Min(4, Math.Max(0, (y2 - y1) / 8));
        x1 = Math.Min(x1 + insetX, x2 - 1);
        y1 = Math.Min(y1 + insetY, y2 - 1);
        x2 = Math.Max(x2 - insetX, x1 + 1);
        y2 = Math.Max(y2 - insetY, y1 + 1);

        int stepX = Math.Max(1, (x2 - x1) / 10);
        int stepY = Math.Max(1, (y2 - y1) / 10);

        ReadOnlySpan<byte> px = pageBitmap.GetPixelSpan();
        int rowBytes = pageBitmap.RowBytes;
        var lumas = new List<int>(128);
        var samples = new List<(byte R, byte G, byte B, int Luma)>(128);

        for (int y = y1; y < y2; y += stepY)
        {
            int rowOff = y * rowBytes;
            for (int x = x1; x < x2; x += stepX)
            {
                int p = rowOff + x * 4;
                byte b = px[p + 0];
                byte g = px[p + 1];
                byte r = px[p + 2];
                int luma = cbLuma(r, g, b);
                lumas.Add(luma);
                samples.Add((r, g, b, luma));
            }
        }

        if (lumas.Count == 0)
        {
            return (0xF7FAFAFCu, 0xFF16181Cu);
        }

        lumas.Sort();
        int median = lumas[lumas.Count / 2];

        if (median >= 145)
        {
            long sumR = 0, sumG = 0, sumB = 0;
            int count = 0;
            foreach ((byte r, byte g, byte b, int luma) in samples)
            {
                if (luma >= 140)
                {
                    sumR += r;
                    sumG += g;
                    sumB += b;
                    count++;
                }
            }

            if (count > 0)
            {
                byte ar = (byte)Math.Clamp((int)(sumR / count), 232, 255);
                byte ag = (byte)Math.Clamp((int)(sumG / count), 232, 255);
                byte ab = (byte)Math.Clamp((int)(sumB / count), 232, 255);
                uint fill = (0xF7u << 24) | ((uint)ar << 16) | ((uint)ag << 8) | ab;
                return (fill, 0xFF16181Cu);
            }

            return (0xF7FAFAFCu, 0xFF16181Cu);
        }

        if (median <= 95)
        {
            long sumR = 0, sumG = 0, sumB = 0;
            int count = 0;
            foreach ((byte r, byte g, byte b, int luma) in samples)
            {
                if (luma <= 105)
                {
                    sumR += r;
                    sumG += g;
                    sumB += b;
                    count++;
                }
            }

            if (count > 0)
            {
                byte ar = (byte)Math.Clamp((int)(sumR / count), 8, 42);
                byte ag = (byte)Math.Clamp((int)(sumG / count), 8, 42);
                byte ab = (byte)Math.Clamp((int)(sumB / count), 8, 46);
                uint fill = (0xF5u << 24) | ((uint)ar << 16) | ((uint)ag << 8) | ab;
                return (fill, 0xFFF5F7FAu);
            }

            return (0xF5141418u, 0xFFF5F7FAu);
        }

        return (0xF0181C24u, 0xFFF5F7FAu);
    }

    private static int cbLuma(byte r, byte g, byte b) => (77 * r + 150 * g + 29 * b) >> 8;

    internal static SKRect ExpandAndClampRect(DetectedBubbleRegion region, int pageWidth, int pageHeight)
    {
        float rw = (float)(region.X2 - region.X1);
        float rh = (float)(region.Y2 - region.Y1);
        // Keep horizontal padding tight on text_bubble so outer oval ink strokes don't bleed into the
        // 224x224 crop, while giving vertical height a slightly larger cushion so top/bottom punctuation
        // (!/?) is not clipped; recovered bubble regions (LabelId == 0) were already inset in PostProcessDetections.
        float padX = region.LabelId switch
        {
            1 => Math.Clamp(Math.Min(rw, rh) * 0.02f, 1f, 3f),
            2 => Math.Clamp(Math.Min(rw, rh) * 0.05f, 2f, 8f),
            _ => 0f
        };
        float padY = region.LabelId switch
        {
            1 => Math.Clamp(rh * 0.02f, 2f, 6f),
            2 => Math.Clamp(Math.Min(rw, rh) * 0.05f, 2f, 8f),
            _ => 0f
        };
        float x1 = Math.Clamp((float)region.X1 - padX, 0f, pageWidth - 1);
        float y1 = Math.Clamp((float)region.Y1 - padY, 0f, pageHeight - 1);
        float x2 = Math.Clamp((float)region.X2 + padX, x1 + 1, pageWidth);
        float y2 = Math.Clamp((float)region.Y2 + padY, y1 + 1, pageHeight);
        return new SKRect(x1, y1, x2, y2);
    }

    private static List<DetectedBubbleRegion> ApplyNmsWithContainment(
        List<DetectedBubbleRegion> items,
        double iouThreshold,
        double containmentThreshold = 0.80)
    {
        var sorted = items.OrderByDescending(static r => r.Confidence).ToList();
        var kept = new List<DetectedBubbleRegion>(sorted.Count);
        foreach (DetectedBubbleRegion candidate in sorted)
        {
            if (kept.Any(existing =>
                    !(candidate.LabelId == 1 &&
                      existing.LabelId == 2 &&
                      ComputeContainment(existing, candidate) >= containmentThreshold) &&
                    (ComputeIoU(existing, candidate) >= iouThreshold ||
                     ComputeContainment(candidate, existing) >= containmentThreshold)))
            {
                continue;
            }

            if (candidate.LabelId == 1)
            {
                kept.RemoveAll(existing =>
                    existing.LabelId == 2 &&
                    ComputeContainment(existing, candidate) >= containmentThreshold);
            }

            kept.Add(candidate);
        }

        return kept;
    }

    private static double ComputeContainment(DetectedBubbleRegion inner, DetectedBubbleRegion outer)
    {
        double innerArea = Math.Max(0, inner.X2 - inner.X1) * Math.Max(0, inner.Y2 - inner.Y1);
        if (innerArea <= 0)
        {
            return 0;
        }

        double ix1 = Math.Max(inner.X1, outer.X1);
        double iy1 = Math.Max(inner.Y1, outer.Y1);
        double ix2 = Math.Min(inner.X2, outer.X2);
        double iy2 = Math.Min(inner.Y2, outer.Y2);
        double intersection = Math.Max(0, ix2 - ix1) * Math.Max(0, iy2 - iy1);
        return intersection / innerArea;
    }

    private static bool IsCenterInside(DetectedBubbleRegion inner, DetectedBubbleRegion outer)
    {
        double cx = (inner.X1 + inner.X2) * 0.5;
        double cy = (inner.Y1 + inner.Y2) * 0.5;
        return cx >= outer.X1 && cx <= outer.X2 && cy >= outer.Y1 && cy <= outer.Y2;
    }

    private static double ComputeIoU(DetectedBubbleRegion a, DetectedBubbleRegion b)
    {
        double ix1 = Math.Max(a.X1, b.X1);
        double iy1 = Math.Max(a.Y1, b.Y1);
        double ix2 = Math.Min(a.X2, b.X2);
        double iy2 = Math.Min(a.Y2, b.Y2);
        double iw = Math.Max(0, ix2 - ix1);
        double ih = Math.Max(0, iy2 - iy1);
        double inter = iw * ih;
        if (inter <= 0)
        {
            return 0;
        }

        double areaA = Math.Max(1, (a.X2 - a.X1) * (a.Y2 - a.Y1));
        double areaB = Math.Max(1, (b.X2 - b.X1) * (b.Y2 - b.Y1));
        return inter / (areaA + areaB - inter);
    }
}

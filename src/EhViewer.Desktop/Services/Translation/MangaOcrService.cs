using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace EhViewer.Desktop.Services.Translation;

/// <summary>
/// Offline local OCR service combining:
/// 1) Two-stage ONNX manga pipeline (RT-DETR bubble/text detection + Manga-OCR Base ViT-Base/BERT INT8) when downloaded;
/// 2) Windows built-in <see cref="OcrEngine"/> fallback (with per-bubble upscaled crops when bubble detector is present).
/// Operates exclusively on in-memory <see cref="DecodedPixelBuffer"/> snapshots so images never leave the device.
/// </summary>
public sealed class MangaOcrService
{
    private const int MaxCacheEntries = 64;

    private readonly ComicBubbleOcrPipeline? _comicOcr;
    private readonly ILogger<MangaOcrService>? _logger;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, OcrPageResult> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();

    public MangaOcrService(
        ComicBubbleOcrPipeline? comicOcr = null,
        ILogger<MangaOcrService>? logger = null)
    {
        _comicOcr = comicOcr;
        _logger = logger;
    }

    public bool HasComicOnnxPipeline => _comicOcr?.AreAllModelsReady == true;
    public bool HasBubbleDetector => _comicOcr?.AreDetectorModelsReady == true;

    public bool IsCached(string pageKey, string? requestedLanguageTag, string? galleryTitle)
    {
        if (string.IsNullOrWhiteSpace(pageKey))
        {
            return false;
        }

        string resolvedTag = ResolveLanguageTag(requestedLanguageTag, galleryTitle);
        bool wantsJapanese = resolvedTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        bool useMangaOnnx = wantsJapanese && _comicOcr?.AreAllModelsReady == true;
        string modeKey = useMangaOnnx ? "manga-ocr-cpt-v2" : resolvedTag;
        return TryGetCached($"{pageKey}|{modeKey}", out _);
    }

    private static readonly Lazy<IReadOnlyList<string>> CachedAvailableLanguageTags = new(
        LoadAvailableLanguageTags,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<string> GetAvailableLanguageTags() => CachedAvailableLanguageTags.Value;

    private static IReadOnlyList<string> LoadAvailableLanguageTags()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(static lang => lang.LanguageTag)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static bool IsLanguagePackInstalled(string? prefixOrTag)
    {
        if (string.IsNullOrWhiteSpace(prefixOrTag))
        {
            return false;
        }

        string target = prefixOrTag.Trim();
        return GetAvailableLanguageTags().Any(tag =>
            tag.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith(target + "-", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsJapanesePackInstalled() => IsLanguagePackInstalled("ja");

    public static string ResolveLanguageTag(string? requestedTag, string? galleryTitle)
    {
        IReadOnlyList<string> available = GetAvailableLanguageTags();
        if (!string.IsNullOrWhiteSpace(requestedTag) &&
            !requestedTag.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            string? match = FindBestMatchingTag(available, requestedTag);
            return match ?? requestedTag.Trim();
        }

        if (!string.IsNullOrWhiteSpace(galleryTitle))
        {
            if (ContainsKana(galleryTitle))
            {
                return FindBestMatchingTag(available, "ja") ?? "ja-JP";
            }

            if (ContainsHangul(galleryTitle) ||
                galleryTitle.Contains("[Korean", StringComparison.OrdinalIgnoreCase))
            {
                return FindBestMatchingTag(available, "ko") ?? "ko-KR";
            }

            if (galleryTitle.Contains("[Chinese", StringComparison.OrdinalIgnoreCase) ||
                galleryTitle.Contains("汉化", StringComparison.Ordinal) ||
                galleryTitle.Contains("漢化", StringComparison.Ordinal))
            {
                return FindBestMatchingTag(available, "zh") ?? "zh-Hans-CN";
            }

            if (galleryTitle.Contains("[English", StringComparison.OrdinalIgnoreCase) ||
                galleryTitle.Contains("English", StringComparison.OrdinalIgnoreCase))
            {
                return FindBestMatchingTag(available, "en") ?? "en-US";
            }
        }

        return FindBestMatchingTag(available, "ja") ?? "ja-JP";
    }

    public async Task<OcrPageResult> RecognizeAsync(
        string pageKey,
        DecodedPixelBuffer pixels,
        string? requestedLanguageTag,
        string? galleryTitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        cancellationToken.ThrowIfCancellationRequested();

        string resolvedTag = ResolveLanguageTag(requestedLanguageTag, galleryTitle);
        bool wantsJapanese = resolvedTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        bool useMangaOnnx = wantsJapanese && _comicOcr?.AreAllModelsReady == true;
        string modeKey = useMangaOnnx ? "manga-ocr-cpt-v2" : resolvedTag;
        string cacheKey = $"{pageKey}|{modeKey}";

        if (TryGetCached(cacheKey, out OcrPageResult? cached))
        {
            return cached!;
        }

        if (useMangaOnnx && _comicOcr is not null)
        {
            IReadOnlyList<OcrTextBlock> onnxBlocks = await _comicOcr
                .RecognizePageAsync(pixels, cancellationToken)
                .ConfigureAwait(false);

            var onnxResult = new OcrPageResult(
                pageKey,
                "Manga-OCR CPT-v2 (ONNX 气泡级)",
                pixels.Width,
                pixels.Height,
                onnxBlocks);
            StoreCache(cacheKey, onnxResult);
            return onnxResult;
        }

        if (wantsJapanese && !IsJapanesePackInstalled())
        {
            throw new InvalidOperationException(
                "当前未安装「漫画专用离线 OCR 模型 (约 161 MB)」，且系统也未安装 Windows 日语 OCR 语言包。请到「设置 -> 阅读体验 -> 生肉识字与防拒答翻译」一键下载漫画专用 OCR 模型。");
        }

        OcrPageResult result = await RecognizeWithWindowsOcrAsync(
            pageKey,
            pixels,
            resolvedTag,
            cancellationToken).ConfigureAwait(false);

        StoreCache(cacheKey, result);
        return result;
    }

    public async Task<OcrTextBlock?> RecognizeNormalizedRegionAsync(
        DecodedPixelBuffer pixels,
        double normX,
        double normY,
        double normWidth,
        double normHeight,
        string? requestedLanguageTag,
        string? galleryTitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        cancellationToken.ThrowIfCancellationRequested();

        double cx = Math.Clamp(normX, 0.0, 0.995);
        double cy = Math.Clamp(normY, 0.0, 0.995);
        double cw = Math.Clamp(normWidth, 0.005, 1.0 - cx);
        double ch = Math.Clamp(normHeight, 0.005, 1.0 - cy);

        string resolvedTag = ResolveLanguageTag(requestedLanguageTag, galleryTitle);
        bool wantsJapanese = resolvedTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

        string text;
        if (wantsJapanese && _comicOcr?.AreAllModelsReady == true)
        {
            text = await _comicOcr
                .RecognizeNormalizedRegionAsync(pixels, cx, cy, cw, ch, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            if (wantsJapanese && !IsJapanesePackInstalled())
            {
                throw new InvalidOperationException(
                    "未检测到漫画专用 OCR 模型且缺少 Windows 日语 OCR 语言包，请先在设置中下载漫画专用 OCR 模型。");
            }

            text = await RecognizeCropWithWindowsOcrAsync(
                pixels, cx, cy, cw, ch, resolvedTag, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        (uint fillArgb, uint textArgb) = ComicBubbleOcrPipeline.SampleRegionColors(pixels, cx, cy, cw, ch);
        return new OcrTextBlock(
            Id: $"manual-{Guid.NewGuid():N}",
            Text: text.Trim(),
            NormalizedX: cx,
            NormalizedY: cy,
            NormalizedWidth: cw,
            NormalizedHeight: ch,
            IsVertical: ch * pixels.Height > cw * pixels.Width * 1.1,
            FillArgb: fillArgb,
            TextArgb: textArgb);
    }

    private async Task<OcrPageResult> RecognizeWithWindowsOcrAsync(
        string pageKey,
        DecodedPixelBuffer pixels,
        string resolvedTag,
        CancellationToken cancellationToken)
    {
        // Stage 1 bubble detection + upscaled crop significantly improves Windows OCR too
        if (_comicOcr?.AreDetectorModelsReady == true)
        {
            try
            {
                IReadOnlyList<DetectedBubbleRegion> regions = await _comicOcr
                    .DetectTextRegionsAsync(pixels, cancellationToken)
                    .ConfigureAwait(false);

                if (regions.Count > 0)
                {
                    OcrEngine? cropEngine = CreateEngine(resolvedTag, out _);
                    if (cropEngine is not null)
                    {
                        var blocks = new List<OcrTextBlock>(regions.Count);
                        int idx = 0;
                        foreach (DetectedBubbleRegion region in regions)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            double nx = Math.Clamp(region.X1 / pixels.Width, 0.0, 1.0);
                            double ny = Math.Clamp(region.Y1 / pixels.Height, 0.0, 1.0);
                            double nw = Math.Clamp((region.X2 - region.X1) / pixels.Width, 0.005, 1.0 - nx);
                            double nh = Math.Clamp((region.Y2 - region.Y1) / pixels.Height, 0.005, 1.0 - ny);

                            string cropText = await RecognizeCropWithWindowsOcrAsync(
                                pixels, nx, ny, nw, nh, resolvedTag, cancellationToken, cropEngine).ConfigureAwait(false);

                            if (!string.IsNullOrWhiteSpace(cropText))
                            {
                                (uint fillArgb, uint textArgb) = ComicBubbleOcrPipeline.SampleRegionColors(pixels, nx, ny, nw, nh);
                                blocks.Add(new OcrTextBlock(
                                    Id: $"det-win-{idx++}",
                                    Text: cropText.Trim(),
                                    NormalizedX: nx,
                                    NormalizedY: ny,
                                    NormalizedWidth: nw,
                                    NormalizedHeight: nh,
                                    IsVertical: (region.Y2 - region.Y1) > (region.X2 - region.X1) * 1.1,
                                    FillArgb: fillArgb,
                                    TextArgb: textArgb));
                            }
                        }

                        if (blocks.Count > 0)
                        {
                            return new OcrPageResult(
                                pageKey,
                                $"{resolvedTag} (气泡裁剪)",
                                pixels.Width,
                                pixels.Height,
                                blocks);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Bubble detector fallback to full-page Windows OCR");
            }
        }

        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            OcrEngine? engine = CreateEngine(resolvedTag, out string effectiveTag);
            if (engine is null)
            {
                throw new InvalidOperationException(
                    $"当前系统未安装可用于「{resolvedTag}」的 Windows 离线 OCR 语言包。请在「设置 -> 阅读体验」下载漫画专用 OCR 模型或添加对应系统语言。");
            }

            IBuffer ibuffer = pixels.BgraPixels.AsBuffer();
            using SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                ibuffer,
                BitmapPixelFormat.Bgra8,
                pixels.Width,
                pixels.Height,
                BitmapAlphaMode.Premultiplied);

            OcrResult ocrResult = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
            List<OcrRawLine> rawLines = ExtractRawLines(ocrResult);
            IReadOnlyList<OcrTextBlock> blocks = OcrRegionClusterer.Cluster(rawLines, pixels.Width, pixels.Height)
                .Select(b =>
                {
                    (uint fillArgb, uint textArgb) = ComicBubbleOcrPipeline.SampleRegionColors(
                        pixels, b.NormalizedX, b.NormalizedY, b.NormalizedWidth, b.NormalizedHeight);
                    return b with { FillArgb = fillArgb, TextArgb = textArgb };
                })
                .ToList();

            return new OcrPageResult(
                pageKey,
                effectiveTag,
                pixels.Width,
                pixels.Height,
                blocks);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static SKBitmap CreateCroppedBitmapFromPixels(
        DecodedPixelBuffer pixels,
        int x,
        int y,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Crop width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Crop height must be positive.");
        }

        int srcX1 = Math.Clamp(x, 0, pixels.Width);
        int srcY1 = Math.Clamp(y, 0, pixels.Height);
        int srcX2 = (int)Math.Clamp((long)x + width, 0L, pixels.Width);
        int srcY2 = (int)Math.Clamp((long)y + height, 0L, pixels.Height);
        int cropW = srcX2 - srcX1;
        int cropH = srcY2 - srcY1;
        if (cropW <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Cropped horizontal range does not intersect the pixel buffer.");
        }

        if (cropH <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Cropped vertical range does not intersect the pixel buffer.");
        }

        var cropBmp = new SKBitmap(cropW, cropH, SKColorType.Bgra8888, SKAlphaType.Premul);
        try
        {
            int srcStride = pixels.Width * 4;
            int dstStride = cropBmp.RowBytes;
            int copyRowBytes = cropW * 4;
            Span<byte> dstSpan = cropBmp.GetPixelSpan();
            ReadOnlySpan<byte> srcSpan = pixels.BgraPixels;

            for (int row = 0; row < cropH; row++)
            {
                int srcOffset = (srcY1 + row) * srcStride + srcX1 * 4;
                int dstOffset = row * dstStride;
                srcSpan.Slice(srcOffset, copyRowBytes).CopyTo(dstSpan.Slice(dstOffset, copyRowBytes));
            }

            return cropBmp;
        }
        catch
        {
            cropBmp.Dispose();
            throw;
        }
    }

    private async Task<string> RecognizeCropWithWindowsOcrAsync(
        DecodedPixelBuffer pixels,
        double normX,
        double normY,
        double normWidth,
        double normHeight,
        string resolvedTag,
        CancellationToken cancellationToken,
        OcrEngine? engineOverride = null)
    {
        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrEngine? engine = engineOverride ?? CreateEngine(resolvedTag, out _);
            if (engine is null)
            {
                return string.Empty;
            }

            float x1 = Math.Clamp((float)(normX * pixels.Width) - 4f, 0f, Math.Max(0, pixels.Width - 2));
            float y1 = Math.Clamp((float)(normY * pixels.Height) - 4f, 0f, Math.Max(0, pixels.Height - 2));
            float x2 = Math.Clamp((float)((normX + normWidth) * pixels.Width) + 4f, Math.Min(pixels.Width, x1 + 2), pixels.Width);
            float y2 = Math.Clamp((float)((normY + normHeight) * pixels.Height) + 4f, Math.Min(pixels.Height, y1 + 2), pixels.Height);
            float srcW = x2 - x1;
            float srcH = y2 - y1;
            if (srcW <= 0f || srcH <= 0f)
            {
                return string.Empty;
            }

            int cropX = (int)Math.Floor(x1);
            int cropY = (int)Math.Floor(y1);
            int cropRight = Math.Min(pixels.Width, (int)Math.Ceiling(x2));
            int cropBottom = Math.Min(pixels.Height, (int)Math.Ceiling(y2));
            int cropW = Math.Max(1, cropRight - cropX);
            int cropH = Math.Max(1, cropBottom - cropY);

            using SKBitmap cropBmp = CreateCroppedBitmapFromPixels(pixels, cropX, cropY, cropW, cropH);

            // Upscale small bubbles 2x with white padding so Windows OCR reads crisp edges
            float scale = Math.Clamp(Math.Min(420f / Math.Max(1f, srcW), 420f / Math.Max(1f, srcH)), 1.5f, 3.0f);
            int targetW = Math.Clamp((int)Math.Ceiling(srcW * scale) + 32, 64, 1800);
            int targetH = Math.Clamp((int)Math.Ceiling(srcH * scale) + 32, 64, 1800);

            using var upscaled = new SKBitmap(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (SKImage cropImg = SKImage.FromBitmap(cropBmp))
            using (var canvas = new SKCanvas(upscaled))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawImage(
                    cropImg,
                    new SKRect(x1 - cropX, y1 - cropY, x2 - cropX, y2 - cropY),
                    new SKRect(16, 16, targetW - 16, targetH - 16),
                    new SKSamplingOptions(SKCubicResampler.CatmullRom));
            }

            ReadOnlySpan<byte> span = upscaled.GetPixelSpan();
            int byteCount = span.Length;
            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            SoftwareBitmap softBmp;
            try
            {
                span.CopyTo(rented.AsSpan(0, byteCount));
                softBmp = SoftwareBitmap.CreateCopyFromBuffer(
                    rented.AsBuffer(0, byteCount),
                    BitmapPixelFormat.Bgra8,
                    targetW,
                    targetH,
                    BitmapAlphaMode.Premultiplied);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            using (softBmp)
            {
                OcrResult res = await engine.RecognizeAsync(softBmp).AsTask(cancellationToken).ConfigureAwait(false);
                List<OcrRawLine> lines = ExtractRawLines(res);
                IReadOnlyList<OcrTextBlock> clustered = OcrRegionClusterer.Cluster(lines, targetW, targetH);
                if (clustered.Count == 0)
                {
                    return string.Empty;
                }

                return string.Join("\n", clustered.Select(static b => b.Text));
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public void ClearCache()
    {
        lock (_cacheGate)
        {
            _cache.Clear();
            _lru.Clear();
        }
    }

    internal static List<OcrRawLine> ExtractRawLines(OcrResult ocrResult)
    {
        var rawLines = new List<OcrRawLine>(ocrResult.Lines.Count);
        foreach (OcrLine line in ocrResult.Lines)
        {
            if (line.Words.Count == 0)
            {
                continue;
            }

            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxRight = double.MinValue;
            double maxBottom = double.MinValue;

            foreach (OcrWord word in line.Words)
            {
                Rect rect = word.BoundingRect;
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    continue;
                }

                minX = Math.Min(minX, rect.X);
                minY = Math.Min(minY, rect.Y);
                maxRight = Math.Max(maxRight, rect.X + rect.Width);
                maxBottom = Math.Max(maxBottom, rect.Y + rect.Height);
            }

            if (minX == double.MaxValue || maxRight <= minX || maxBottom <= minY)
            {
                continue;
            }

            string joined = JoinWordsForManga(line.Words);
            if (!string.IsNullOrWhiteSpace(joined))
            {
                rawLines.Add(new OcrRawLine(
                    joined,
                    minX,
                    minY,
                    maxRight - minX,
                    maxBottom - minY));
            }
        }

        return rawLines;
    }

    internal static string JoinWordsForManga(IReadOnlyList<OcrWord> words)
    {
        if (words.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        char? prevLastChar = null;
        foreach (OcrWord word in words)
        {
            string token = word.Text?.Trim() ?? string.Empty;
            if (token.Length == 0)
            {
                continue;
            }

            char firstChar = token[0];
            if (sb.Length > 0 && prevLastChar is char prev && NeedsSpaceBetween(prev, firstChar))
            {
                sb.Append(' ');
            }

            sb.Append(token);
            prevLastChar = token[^1];
        }

        return sb.ToString();
    }

    private static bool NeedsSpaceBetween(char left, char right) =>
        IsAsciiWordChar(left) && IsAsciiWordChar(right);

    private static bool IsAsciiWordChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    private OcrEngine? CreateEngine(string resolvedTag, out string effectiveTag)
    {
        effectiveTag = resolvedTag;
        try
        {
            var lang = new Language(resolvedTag);
            if (OcrEngine.IsLanguageSupported(lang))
            {
                OcrEngine? engine = OcrEngine.TryCreateFromLanguage(lang);
                if (engine is not null)
                {
                    effectiveTag = engine.RecognizerLanguage.LanguageTag;
                    return engine;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Unable to initialize OcrEngine for {LanguageTag}", resolvedTag);
        }

        OcrEngine? fallback = OcrEngine.TryCreateFromUserProfileLanguages();
        if (fallback is not null)
        {
            effectiveTag = fallback.RecognizerLanguage.LanguageTag;
            return fallback;
        }

        foreach (Language installed in OcrEngine.AvailableRecognizerLanguages)
        {
            OcrEngine? engine = OcrEngine.TryCreateFromLanguage(installed);
            if (engine is not null)
            {
                effectiveTag = engine.RecognizerLanguage.LanguageTag;
                return engine;
            }
        }

        return null;
    }

    private static string? FindBestMatchingTag(IReadOnlyList<string> available, string prefixOrTag)
    {
        string target = prefixOrTag.Trim();
        foreach (string tag in available)
        {
            if (tag.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                return tag;
            }
        }

        foreach (string tag in available)
        {
            if (tag.StartsWith(target + "-", StringComparison.OrdinalIgnoreCase))
            {
                return tag;
            }
        }

        int dash = target.IndexOf('-');
        if (dash > 0)
        {
            string baseLang = target[..dash];
            foreach (string tag in available)
            {
                if (tag.Equals(baseLang, StringComparison.OrdinalIgnoreCase) ||
                    tag.StartsWith(baseLang + "-", StringComparison.OrdinalIgnoreCase))
                {
                    return tag;
                }
            }
        }

        return null;
    }

    private static bool ContainsKana(string text)
    {
        foreach (char c in text)
        {
            if ((c >= '\u3040' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF'))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsHangul(string text)
    {
        foreach (char c in text)
        {
            if ((c >= '\uAC00' && c <= '\uD7AF') || (c >= '\u1100' && c <= '\u11FF'))
            {
                return true;
            }
        }
        return false;
    }

    private bool TryGetCached(string key, out OcrPageResult? result)
    {
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(key, out result))
            {
                _lru.Remove(key);
                _lru.AddLast(key);
                return true;
            }
        }

        result = null;
        return false;
    }

    private void StoreCache(string key, OcrPageResult result)
    {
        lock (_cacheGate)
        {
            if (_cache.ContainsKey(key))
            {
                _lru.Remove(key);
            }
            else if (_cache.Count >= MaxCacheEntries && _lru.First is { } oldest)
            {
                _cache.Remove(oldest.Value);
                _lru.RemoveFirst();
            }

            _cache[key] = result;
            _lru.AddLast(key);
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace EhViewer.Desktop.Services.Translation;

public sealed record DetectedBubbleRegion(
    double X1,
    double Y1,
    double X2,
    double Y2,
    float Confidence,
    long LabelId);

/// <summary>
/// Two-stage offline manga OCR pipeline:
/// Stage 1: <c>comic-text-and-bubble-detector</c> (RT-DETR-v2 INT8) locates speech bubbles &amp; text blocks;
/// Stage 2: <c>manga-ocr-base-CPT-v2</c> (ViT + BERT with KV-cache, q8) recognizes printed, vertical,
/// furigana, and stylized/handwritten text per cropped block.
/// </summary>
public sealed class ComicBubbleOcrPipeline : IDisposable
{
    public const string BubbleDetectorUrl =
        "https://huggingface.co/ogkalu/comic-text-and-bubble-detector/resolve/main/detector_int8.onnx";
    public const string BubbleDetectorMirrorUrl =
        "https://hf-mirror.com/ogkalu/comic-text-and-bubble-detector/resolve/main/detector_int8.onnx";

    public const string MangaOcrEncoderUrl =
        "https://huggingface.co/Quien1/manga-ocr-base-CPT-v2-ONNX/resolve/main/onnx/encoder_model_quantized.onnx";
    public const string MangaOcrEncoderMirrorUrl =
        "https://hf-mirror.com/Quien1/manga-ocr-base-CPT-v2-ONNX/resolve/main/onnx/encoder_model_quantized.onnx";

    public const string MangaOcrDecoderUrl =
        "https://huggingface.co/Quien1/manga-ocr-base-CPT-v2-ONNX/resolve/main/onnx/decoder_model_merged_quantized.onnx";
    public const string MangaOcrDecoderMirrorUrl =
        "https://hf-mirror.com/Quien1/manga-ocr-base-CPT-v2-ONNX/resolve/main/onnx/decoder_model_merged_quantized.onnx";

    public const string MangaOcrVocabUrl =
        "https://huggingface.co/Quien1/manga-ocr-base-CPT-v2-ONNX/resolve/main/vocab.txt";
    public const string MangaOcrVocabMirrorUrl =
        "https://hf-mirror.com/Quien1/manga-ocr-base-CPT-v2-ONNX/resolve/main/vocab.txt";

    private static readonly string[] LegacyV1ModelFileNames =
    [
        "manga-ocr-encoder.onnx",
        "manga-ocr-decoder.onnx",
        "vocab.txt"
    ];

    private const int DetectorInputSize = 640;
    private const int RecognizerInputSize = 224;
    private const long StartTokenId = 2; // [CLS]
    private const long EndTokenId = 3;   // [SEP]
    private const int MaxDecodeTokens = 128;

    private static readonly DenseTensor<bool> FalseCacheTensor = new(new[] { false }, new[] { 1 });
    private static readonly DenseTensor<bool> TrueCacheTensor = new(new[] { true }, new[] { 1 });
    private static readonly NamedOnnxValue FalseCacheValue = NamedOnnxValue.CreateFromTensor("use_cache_branch", FalseCacheTensor);
    private static readonly NamedOnnxValue TrueCacheValue = NamedOnnxValue.CreateFromTensor("use_cache_branch", TrueCacheTensor);

    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly ILogger<ComicBubbleOcrPipeline>? _logger;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    private InferenceSession? _detectorSession;
    private InferenceSession? _encoderSession;
    private InferenceSession? _decoderSession;
    private bool _decoderUsesKvCache;
    private string[] _decoderPastInputNames = [];
    private Dictionary<string, DenseTensor<float>>? _reusableEmptyPastTensors;
    private string[]? _vocab;
    private DenseTensor<float>? _reusableDetectorInput;
    private long[]? _reusableOrigSizesBuffer;
    private DenseTensor<long>? _reusableOrigSizesTensor;
    private NamedOnnxValue[]? _reusableDetectorInputs;
    private SKBitmap? _reusableDetectorBitmap;
    private DenseTensor<float>? _reusableRecognizerInput;
    private NamedOnnxValue[]? _reusableEncoderInputs;
    private SKBitmap? _reusableCrop224;
    private byte[]? _reusableLuma224;
    private long[]? _reusableKvTokenBuf;
    private DenseTensor<long>? _reusableKvIdTensor;
    private NamedOnnxValue? _reusableKvIdValue;
    private List<NamedOnnxValue>? _reusableKvInputs;
    private Dictionary<string, Tensor<float>>? _reusableKvPastTensors;
    private long[]? _reusableStatelessTokenBuffer;
    private NamedOnnxValue[]? _reusableStatelessInputs;
    private bool _disposed;

    public ComicBubbleOcrPipeline(
        AppPaths paths,
        HttpClient? httpClient = null,
        ILogger<ComicBubbleOcrPipeline>? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _logger = logger;
    }

    public bool AreDetectorModelsReady => File.Exists(_paths.BubbleDetectorModelPath);

    public bool AreAllModelsReady =>
        AreDetectorModelsReady &&
        File.Exists(_paths.MangaOcrEncoderPath) &&
        File.Exists(_paths.MangaOcrDecoderPath) &&
        File.Exists(_paths.MangaOcrVocabPath);

    public bool HasLegacyV1Models =>
        !AreAllModelsReady &&
        (File.Exists(Path.Combine(_paths.OcrModelsDirectory, LegacyV1ModelFileNames[0])) ||
         File.Exists(Path.Combine(_paths.OcrModelsDirectory, LegacyV1ModelFileNames[1])));

    public async Task DownloadModelsAsync(
        bool useMirror,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Directory.CreateDirectory(_paths.OcrModelsDirectory);

        (string Name, string Url, string Dest, long ApproxBytes)[] items =
        [
            (
                "气泡文本检测器 (RT-DETR)",
                useMirror ? BubbleDetectorMirrorUrl : BubbleDetectorUrl,
                _paths.BubbleDetectorModelPath,
                43_838_857
            ),
            (
                "Manga-OCR CPT-v2 视觉编码器",
                useMirror ? MangaOcrEncoderMirrorUrl : MangaOcrEncoderUrl,
                _paths.MangaOcrEncoderPath,
                86_912_680
            ),
            (
                "Manga-OCR CPT-v2 日语解码器",
                useMirror ? MangaOcrDecoderMirrorUrl : MangaOcrDecoderUrl,
                _paths.MangaOcrDecoderPath,
                30_632_263
            ),
            (
                "Manga-OCR CPT-v2 词表",
                useMirror ? MangaOcrVocabMirrorUrl : MangaOcrVocabUrl,
                _paths.MangaOcrVocabPath,
                27_001
            )
        ];

        long totalExpected = items.Sum(i => i.ApproxBytes);
        long completedOffset = 0;

        foreach ((string name, string url, string dest, long approxBytes) in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(dest) && new FileInfo(dest).Length == approxBytes)
            {
                completedOffset += approxBytes;
                progress?.Report(new ModelDownloadProgress(completedOffset, totalExpected, $"{name} 已存在，跳过"));
                continue;
            }

            string partPath = dest + ".part";
            if (File.Exists(partPath) && new FileInfo(partPath).Length > approxBytes)
            {
                File.Delete(partPath);
            }

            long itemOffset = completedOffset;
            var subProgress = new Progress<ModelDownloadProgress>(p =>
            {
                long overallDone = Math.Min(totalExpected, itemOffset + p.BytesReceived);
                progress?.Report(new ModelDownloadProgress(overallDone, totalExpected, p.StatusText));
            });

            await DownloadFileWithResumeAsync(url, dest, $"正在下载 {name}", subProgress, cancellationToken)
                .ConfigureAwait(false);
            completedOffset += approxBytes;
        }

        await ResetSessionsAsync().ConfigureAwait(false);
        CleanupLegacyV1Models();
        progress?.Report(new ModelDownloadProgress(totalExpected, totalExpected, "Manga-OCR CPT-v2 离线识别模型已全部就绪"));
    }

    private void CleanupLegacyV1Models()
    {
        foreach (string legacyFileName in LegacyV1ModelFileNames)
        {
            foreach (string path in new[]
            {
                Path.Combine(_paths.OcrModelsDirectory, legacyFileName),
                Path.Combine(_paths.OcrModelsDirectory, legacyFileName + ".part")
            })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogDebug(ex, "Skipping cleanup of legacy OCR file {Path}", path);
                }
            }
        }
    }

    public async Task<IReadOnlyList<DetectedBubbleRegion>> DetectTextRegionsAsync(
        DecodedPixelBuffer pixels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                EnsureDetectorSessionLoaded();
                using PinnedBitmapScope pageScope = CreateBitmapFromPixels(pixels);
                return (IReadOnlyList<DetectedBubbleRegion>)DetectTextRegionsCore(pageScope.Bitmap, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<IReadOnlyList<OcrTextBlock>> RecognizePageAsync(
        DecodedPixelBuffer pixels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                EnsureAllSessionsLoaded();
                using PinnedBitmapScope pageScope = CreateBitmapFromPixels(pixels);
                SKBitmap pageBitmap = pageScope.Bitmap;
                using SKImage pageImage = SKImage.FromBitmap(pageBitmap);

                List<DetectedBubbleRegion> regions = DetectTextRegionsCore(pageBitmap, cancellationToken);
                if (regions.Count == 0)
                {
                    return (IReadOnlyList<OcrTextBlock>)[];
                }

                var blocks = new List<OcrTextBlock>(regions.Count);
                int idx = 0;

                foreach (DetectedBubbleRegion region in regions)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    SKRect padded = ExpandAndClampRect(region, pageBitmap.Width, pageBitmap.Height);
                    if (padded.Width < 8 || padded.Height < 8)
                    {
                        continue;
                    }

                    CropRecognitionResult result = RecognizeRegionSmart(pageBitmap, pageImage, padded, cancellationToken: cancellationToken);
                    if (string.IsNullOrWhiteSpace(result.Text))
                    {
                        continue;
                    }

                    // Filter out low-confidence non-bubble text_free false alarms (e.g. screentone/hatching)
                    // that decode only to isolated dots, slashes, or ellipsis punctuation.
                    if (region.LabelId == 2 && region.Confidence < 0.55f && IsPunctuationOrDotOnly(result.Text))
                    {
                        continue;
                    }

                    bool isVertical = (region.Y2 - region.Y1) > (region.X2 - region.X1) * 1.1;
                    (uint fillArgb, uint textArgb) = SampleBubbleColors(pageBitmap, padded);
                    blocks.Add(new OcrTextBlock(
                        Id: $"manga-ocr-{idx++}",
                        Text: result.Text,
                        NormalizedX: Math.Clamp(padded.Left / pageBitmap.Width, 0.0, 1.0),
                        NormalizedY: Math.Clamp(padded.Top / pageBitmap.Height, 0.0, 1.0),
                        NormalizedWidth: Math.Clamp(padded.Width / pageBitmap.Width, 0.005, 1.0),
                        NormalizedHeight: Math.Clamp(padded.Height / pageBitmap.Height, 0.005, 1.0),
                        IsVertical: isVertical,
                        FillArgb: fillArgb,
                        TextArgb: textArgb));
                }

                return (IReadOnlyList<OcrTextBlock>)blocks;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<string> RecognizeNormalizedRegionAsync(
        DecodedPixelBuffer pixels,
        double normX,
        double normY,
        double normWidth,
        double normHeight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                EnsureRecognizerSessionsLoaded();
                using PinnedBitmapScope pageScope = CreateBitmapFromPixels(pixels);
                SKBitmap pageBitmap = pageScope.Bitmap;
                using SKImage pageImage = SKImage.FromBitmap(pageBitmap);

                float x1 = Math.Clamp((float)(normX * pageBitmap.Width), 0, pageBitmap.Width - 2);
                float y1 = Math.Clamp((float)(normY * pageBitmap.Height), 0, pageBitmap.Height - 2);
                float x2 = Math.Clamp((float)((normX + normWidth) * pageBitmap.Width), x1 + 2, pageBitmap.Width);
                float y2 = Math.Clamp((float)((normY + normHeight) * pageBitmap.Height), y1 + 2, pageBitmap.Height);

                return RecognizeRegionSmart(pageBitmap, pageImage, new SKRect(x1, y1, x2, y2), cancellationToken: cancellationToken).Text;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task ResetSessionsAsync()
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeSessionsCore();
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeSessionsCore();
        _sessionGate.Dispose();
    }

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

    internal static bool IsDecoderTokenAllowed(int tokenId) =>
        tokenId is not (0 or 1 or 2 or 4);

    internal static bool IsRunawayRepetitionLoop(IReadOnlyList<long> generated, long candidateToken)
    {
        int totalLen = generated.Count + 1;
        for (int period = 1; period <= 4; period++)
        {
            int cycles = period == 1 ? 8 : 6;
            int span = period * cycles;
            if (totalLen < span)
            {
                continue;
            }

            int start = totalLen - span;
            bool matches = true;
            for (int offset = period; offset < span; offset++)
            {
                int aIdx = start + offset;
                int bIdx = start + (offset % period);
                long aTok = aIdx < generated.Count ? generated[aIdx] : candidateToken;
                long bTok = bIdx < generated.Count ? generated[bIdx] : candidateToken;
                if (aTok != bTok)
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsPunctuationOrDotOnly(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                return false;
            }

            if (c is '♥' or '♡' or '!' or '?' or '！' or '？')
            {
                return false;
            }
        }

        return true;
    }

    internal static double ScoreRecognitionCandidate(
        string text,
        double meanLogProb,
        float minProb,
        bool stoppedByLoopGuard)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return -100.0;
        }

        double score = meanLogProb + minProb * 0.15;
        if (stoppedByLoopGuard)
        {
            score -= 3.0;
        }

        if (IsPunctuationOrDotOnly(text))
        {
            score -= 1.5;
        }

        if (text.Contains('\uFFFD'))
        {
            score -= 1.0;
        }

        return score;
    }

    internal static string DecodeTokenSequence(IEnumerable<long> tokenIds, IReadOnlyList<string> vocab)
    {
        var sb = new StringBuilder();
        foreach (long id in tokenIds)
        {
            if (id < 5 || id >= vocab.Count)
            {
                continue;
            }

            string token = vocab[(int)id];
            if (token.StartsWith("##", StringComparison.Ordinal))
            {
                token = token[2..];
            }

            sb.Append(token);
        }

        return NormalizeRecognizedText(sb.ToString());
    }

    internal static string NormalizeRecognizedText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string trimmed = raw.Trim();
        var sb = new StringBuilder(trimmed.Length);
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (char.IsWhiteSpace(c))
            {
                char prev = sb.Length > 0 ? sb[^1] : '\0';
                char next = i + 1 < trimmed.Length ? trimmed[i + 1] : '\0';
                // Preserve spaces inside ASCII words (e.g., "Wait a second!"), strip spurious gaps between CJK/kana
                if (IsCjkOrKanaPunctuation(prev) || IsCjkOrKanaPunctuation(next))
                {
                    continue;
                }

                if (sb.Length > 0 && sb[^1] != ' ')
                {
                    sb.Append(' ');
                }

                continue;
            }

            sb.Append(c);
        }

        // Collapse runaway hallucinated trailing periods beyond 6 while keeping standard ellipses
        int dotRun = 0;
        for (int j = sb.Length - 1; j >= 0 && sb[j] == '.'; j--)
        {
            dotRun++;
        }

        if (dotRun > 6)
        {
            sb.Remove(sb.Length - (dotRun - 6), dotRun - 6);
        }

        return sb.ToString().Trim();
    }

    private static bool IsCjkOrKanaPunctuation(char c) =>
        (c >= '\u3000' && c <= '\u30FF') || // CJK punctuation, Hiragana, Katakana
        (c >= '\u4E00' && c <= '\u9FFF') || // Unified Kanji
        (c >= '\uFF01' && c <= '\uFF60');   // Fullwidth ASCII / punctuation

    private SKBitmap AllocateReusableDetectorBitmap()
    {
        _reusableDetectorBitmap?.Dispose();
        _reusableDetectorBitmap = new SKBitmap(
            DetectorInputSize,
            DetectorInputSize,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        return _reusableDetectorBitmap;
    }

    private List<DetectedBubbleRegion> DetectTextRegionsCore(
        SKBitmap pageBitmap,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SKBitmap resized = _reusableDetectorBitmap is { Width: DetectorInputSize, Height: DetectorInputSize } existing
            ? existing
            : AllocateReusableDetectorBitmap();

        if (!pageBitmap.ScalePixels(resized, new SKSamplingOptions(SKFilterMode.Linear)))
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();

        DenseTensor<float> detInput = _reusableDetectorInput ??= new DenseTensor<float>([1, 3, DetectorInputSize, DetectorInputSize]);
        ReadOnlySpan<byte> px = resized.GetPixelSpan();
        int hw = DetectorInputSize * DetectorInputSize;
        Span<float> detBuf = detInput.Buffer.Span;
        Span<float> rPlane = detBuf.Slice(0, hw);
        Span<float> gPlane = detBuf.Slice(hw, hw);
        Span<float> bPlane = detBuf.Slice(hw * 2, hw);
        for (int i = 0, p = 0; i < hw; i++, p += 4)
        {
            rPlane[i] = px[p + 2] / 255.0f;
            gPlane[i] = px[p + 1] / 255.0f;
            bPlane[i] = px[p + 0] / 255.0f;
        }

        // RT-DETR-v2 expects [width, height] in orig_target_sizes
        long[] origSizesBuf = _reusableOrigSizesBuffer ??= new long[2];
        origSizesBuf[0] = pageBitmap.Width;
        origSizesBuf[1] = pageBitmap.Height;
        DenseTensor<long> origSizes = _reusableOrigSizesTensor ??= new DenseTensor<long>(origSizesBuf, [1, 2]);
        NamedOnnxValue[] detInputs = _reusableDetectorInputs ??=
        [
            NamedOnnxValue.CreateFromTensor("images", detInput),
            NamedOnnxValue.CreateFromTensor("orig_target_sizes", origSizes)
        ];

        cancellationToken.ThrowIfCancellationRequested();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _detectorSession!.Run(detInputs);

        Tensor<long> labels = outputs.First(x => x.Name == "labels").AsTensor<long>();
        Tensor<float> boxes = outputs.First(x => x.Name == "boxes").AsTensor<float>();
        Tensor<float> scores = outputs.First(x => x.Name == "scores").AsTensor<float>();

        int count = (int)scores.Dimensions[1];
        var raw = new List<DetectedBubbleRegion>(count);
        for (int i = 0; i < count; i++)
        {
            float score = scores[0, i];
            if (score < 0.35f)
            {
                continue;
            }

            raw.Add(new DetectedBubbleRegion(
                boxes[0, i, 0],
                boxes[0, i, 1],
                boxes[0, i, 2],
                boxes[0, i, 3],
                score,
                labels[0, i]));
        }

        return [.. PostProcessDetections(raw, pageBitmap.Width, pageBitmap.Height)];
    }

    private CropRecognitionResult RecognizeRegionSmart(
        SKBitmap pageBitmap,
        SKImage pageImage,
        SKRect srcRect,
        int depth = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        float rw = srcRect.Width;
        float rh = srcRect.Height;

        // Ultra-tall/wide strips lose character stroke resolution when squeezed to 224x224 directly
        // (16x16 ViT patches cover >1 char). Recursively split (up to 2 levels -> 3-4 segments) at quiet
        // minimum-gradient scanlines with generous overlap when there is no two-column gutter.
        if (depth < 2 && rh >= rw * 3.2f && rh >= 260f && !HasCentralVerticalColumnGutter(pageBitmap, srcRect))
        {
            int cutTopEnd = FindCleanestHorizontalSplit(
                pageBitmap, srcRect, srcRect.Top + rh * 0.53f, srcRect.Top + rh * 0.64f);
            int cutBotStart = FindCleanestHorizontalSplit(
                pageBitmap, srcRect, srcRect.Top + rh * 0.36f, srcRect.Top + rh * 0.47f);
            if (cutTopEnd > srcRect.Top + 40 && cutBotStart < srcRect.Bottom - 40 && cutTopEnd > cutBotStart)
            {
                CropRecognitionResult topRes = RecognizeRegionSmart(pageBitmap, pageImage, new SKRect(srcRect.Left, srcRect.Top, srcRect.Right, cutTopEnd), depth + 1, cancellationToken);
                CropRecognitionResult botRes = RecognizeRegionSmart(pageBitmap, pageImage, new SKRect(srcRect.Left, cutBotStart, srcRect.Right, srcRect.Bottom), depth + 1, cancellationToken);
                string merged = MergeOverlappingRecognitions(topRes.Text, botRes.Text, out int overlapLen);
                if (overlapLen >= 2)
                {
                    return new CropRecognitionResult(
                        merged,
                        (topRes.MeanLogProb + botRes.MeanLogProb) * 0.5,
                        (topRes.AvgProb + botRes.AvgProb) * 0.5f,
                        Math.Min(topRes.MinProb, botRes.MinProb),
                        topRes.StoppedByLoopGuard || botRes.StoppedByLoopGuard);
                }
            }
        }
        else if (depth < 2 && rw >= rh * 3.2f && rw >= 260f)
        {
            int cutLeftEnd = FindCleanestVerticalSplit(
                pageBitmap, srcRect, srcRect.Left + rw * 0.53f, srcRect.Left + rw * 0.64f);
            int cutRightStart = FindCleanestVerticalSplit(
                pageBitmap, srcRect, srcRect.Left + rw * 0.36f, srcRect.Left + rw * 0.47f);
            if (cutLeftEnd > srcRect.Left + 40 && cutRightStart < srcRect.Right - 40 && cutLeftEnd > cutRightStart)
            {
                CropRecognitionResult leftRes = RecognizeRegionSmart(pageBitmap, pageImage, new SKRect(srcRect.Left, srcRect.Top, cutLeftEnd, srcRect.Bottom), depth + 1, cancellationToken);
                CropRecognitionResult rightRes = RecognizeRegionSmart(pageBitmap, pageImage, new SKRect(cutRightStart, srcRect.Top, srcRect.Right, srcRect.Bottom), depth + 1, cancellationToken);
                string merged = MergeOverlappingRecognitions(leftRes.Text, rightRes.Text, out int overlapLen);
                if (overlapLen >= 2)
                {
                    return new CropRecognitionResult(
                        merged,
                        (leftRes.MeanLogProb + rightRes.MeanLogProb) * 0.5,
                        (leftRes.AvgProb + rightRes.AvgProb) * 0.5f,
                        Math.Min(leftRes.MinProb, rightRes.MinProb),
                        leftRes.StoppedByLoopGuard || rightRes.StoppedByLoopGuard);
                }
            }
        }

        return RecognizeCropWithRecovery(pageBitmap, pageImage, srcRect, cancellationToken);
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

    private readonly record struct CropRecognitionResult(
        string Text,
        double MeanLogProb,
        float AvgProb,
        float MinProb,
        bool StoppedByLoopGuard);

    private CropRecognitionResult RecognizeCropWithRecovery(
        SKBitmap pageBitmap,
        SKImage pageImage,
        SKRect srcRect,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] luma = ExtractCropLuma224(pageImage, srcRect);
        (int p2, int p50, int p98) = ComputeLuminancePercentiles(luma);

        CropRecognitionResult best = DecodeGrayscaleCrop(luma, stretchLo: 0, stretchHi: 255, invert: false, cancellationToken);
        if (!NeedsRecoveryPass(best))
        {
            return best;
        }

        bool looksDarkOrInverted = p50 <= 125 || (p98 - p50) > (p50 - p2) + 25;
        if (looksDarkOrInverted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CropRecognitionResult inverted = DecodeGrayscaleCrop(luma, stretchLo: 0, stretchHi: 255, invert: true, cancellationToken);
            best = PickBetterCandidate(best, inverted);
        }

        int range = p98 - p2;
        if (range is >= 18 and <= 165)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CropRecognitionResult stretched = DecodeGrayscaleCrop(
                luma,
                stretchLo: p2,
                stretchHi: p98,
                invert: looksDarkOrInverted,
                cancellationToken);
            best = PickBetterCandidate(best, stretched);
        }

        if (best.MinProb < 0.45f || string.IsNullOrWhiteSpace(best.Text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            float margin = Math.Clamp(Math.Min(srcRect.Width, srcRect.Height) * 0.06f, 2f, 10f);
            SKRect expanded = new(
                Math.Max(0f, srcRect.Left - margin),
                Math.Max(0f, srcRect.Top - margin),
                Math.Min(pageBitmap.Width, srcRect.Right + margin),
                Math.Min(pageBitmap.Height, srcRect.Bottom + margin));

            if (expanded.Width >= srcRect.Width + 2f || expanded.Height >= srcRect.Height + 2f)
            {
                byte[] expandedLuma = ExtractCropLuma224(pageImage, expanded);
                CropRecognitionResult expandedNormal = DecodeGrayscaleCrop(
                    expandedLuma,
                    stretchLo: 0,
                    stretchHi: 255,
                    invert: false,
                    cancellationToken);
                best = PickBetterCandidate(best, expandedNormal);

                if (looksDarkOrInverted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CropRecognitionResult expandedInverted = DecodeGrayscaleCrop(
                        expandedLuma,
                        stretchLo: 0,
                        stretchHi: 255,
                        invert: true,
                        cancellationToken);
                    best = PickBetterCandidate(best, expandedInverted);
                }
            }
        }

        return best;
    }

    private static bool NeedsRecoveryPass(CropRecognitionResult r) =>
        string.IsNullOrWhiteSpace(r.Text) ||
        r.StoppedByLoopGuard ||
        r.AvgProb < 0.88f ||
        r.MinProb < 0.50f ||
        IsPunctuationOrDotOnly(r.Text) ||
        r.Text.Contains('\uFFFD');

    private static CropRecognitionResult PickBetterCandidate(CropRecognitionResult currentBest, CropRecognitionResult challenger) =>
        ScoreRecognitionCandidate(challenger.Text, challenger.MeanLogProb, challenger.MinProb, challenger.StoppedByLoopGuard) >
        ScoreRecognitionCandidate(currentBest.Text, currentBest.MeanLogProb, currentBest.MinProb, currentBest.StoppedByLoopGuard)
            ? challenger
            : currentBest;

    private byte[] ExtractCropLuma224(SKImage pageImage, SKRect srcRect)
    {
        SKBitmap crop224 = _reusableCrop224 ??= new SKBitmap(RecognizerInputSize, RecognizerInputSize, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(crop224))
        {
            canvas.Clear(SKColors.White);
            bool isDownscaling = srcRect.Width > RecognizerInputSize * 1.15f ||
                                 srcRect.Height > RecognizerInputSize * 1.15f;
            SKSamplingOptions sampling = isDownscaling
                ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
                : new SKSamplingOptions(SKCubicResampler.CatmullRom);
            canvas.DrawImage(
                pageImage,
                srcRect,
                new SKRect(0, 0, RecognizerInputSize, RecognizerInputSize),
                sampling);
        }

        ReadOnlySpan<byte> cb = crop224.GetPixelSpan();
        int hw = RecognizerInputSize * RecognizerInputSize;
        byte[] luma = _reusableLuma224 ??= new byte[hw];
        for (int k = 0, p = 0; k < hw; k++, p += 4)
        {
            int grayByte = Math.Clamp((int)MathF.Round(0.299f * cb[p + 2] + 0.587f * cb[p + 1] + 0.114f * cb[p + 0]), 0, 255);
            luma[k] = (byte)grayByte;
        }

        return luma;
    }

    private static (int P2, int P50, int P98) ComputeLuminancePercentiles(ReadOnlySpan<byte> luma)
    {
        Span<int> hist = stackalloc int[256];
        for (int k = 0; k < luma.Length; k++)
        {
            hist[luma[k]]++;
        }

        int loThresh = luma.Length / 50;
        int midThresh = luma.Length / 2;
        int hiThresh = luma.Length - loThresh;
        int cum = 0;
        int p2 = 0;
        int p50 = 128;
        int p98 = 255;
        bool foundLo = false;
        bool foundMid = false;

        for (int b = 0; b < 256; b++)
        {
            cum += hist[b];
            if (!foundLo && cum >= loThresh)
            {
                p2 = b;
                foundLo = true;
            }

            if (!foundMid && cum >= midThresh)
            {
                p50 = b;
                foundMid = true;
            }

            if (cum >= hiThresh)
            {
                p98 = b;
                break;
            }
        }

        return (p2, p50, p98);
    }

    private CropRecognitionResult DecodeGrayscaleCrop(
        byte[] luma,
        int stretchLo,
        int stretchHi,
        bool invert,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DenseTensor<float> encInput = _reusableRecognizerInput ??= new DenseTensor<float>([1, 3, RecognizerInputSize, RecognizerInputSize]);
        int hw = RecognizerInputSize * RecognizerInputSize;
        Span<float> encBuf = encInput.Buffer.Span;
        Span<float> rPlane = encBuf.Slice(0, hw);
        bool applyStretch = stretchHi > stretchLo && (stretchLo > 0 || stretchHi < 255);
        float stretchScale = applyStretch ? 255.0f / (stretchHi - stretchLo) : 1.0f;

        for (int k = 0; k < hw; k++)
        {
            float g = luma[k];
            if (applyStretch)
            {
                g = Math.Clamp((g - stretchLo) * stretchScale, 0f, 255f);
            }

            if (invert)
            {
                g = 255.0f - g;
            }

            rPlane[k] = g / 127.5f - 1.0f;
        }

        rPlane.CopyTo(encBuf.Slice(hw, hw));
        rPlane.CopyTo(encBuf.Slice(hw * 2, hw));

        NamedOnnxValue[] encInputs = _reusableEncoderInputs ??=
        [
            NamedOnnxValue.CreateFromTensor("pixel_values", encInput)
        ];

        cancellationToken.ThrowIfCancellationRequested();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> encOut = _encoderSession!.Run(encInputs);
        // Keep encOut alive across the decoder loop instead of cloning the encoder hidden states tensor.
        Tensor<float> hidden = encOut[0].AsTensor<float>();

        return _decoderUsesKvCache
            ? RunKvCachedDecoder(hidden, cancellationToken)
            : RunStatelessDecoder(hidden, cancellationToken);
    }

    private CropRecognitionResult RunKvCachedDecoder(Tensor<float> hidden, CancellationToken cancellationToken)
    {
        Dictionary<string, Tensor<float>> pastTensors = _reusableKvPastTensors ??=
            new Dictionary<string, Tensor<float>>(_decoderPastInputNames.Length, StringComparer.Ordinal);
        pastTensors.Clear();
        foreach (string pastName in _decoderPastInputNames)
        {
            if (_reusableEmptyPastTensors is not null &&
                _reusableEmptyPastTensors.TryGetValue(pastName, out DenseTensor<float>? cachedEmpty))
            {
                pastTensors[pastName] = cachedEmpty;
            }
            else
            {
                int[] dims = _decoderSession!.InputMetadata[pastName].Dimensions;
                int numHeads = dims.Length == 4 && dims[1] > 0 ? dims[1] : 12;
                int headDim = dims.Length == 4 && dims[3] > 0 ? dims[3] : 64;
                pastTensors[pastName] = new DenseTensor<float>([1, numHeads, 0, headDim]);
            }
        }

        var generatedIds = new List<long>(48);
        double sumLogProb = 0.0;
        double sumProb = 0.0;
        float minProb = 1.0f;
        int scoredCount = 0;
        bool stoppedByLoopGuard = false;
        int vocabSize = _vocab!.Length;

        long[] curTokenBuf = _reusableKvTokenBuf ??= new long[1];
        DenseTensor<long> idTensor = _reusableKvIdTensor ??= new DenseTensor<long>(curTokenBuf, [1, 1]);
        NamedOnnxValue idValue = _reusableKvIdValue ??= NamedOnnxValue.CreateFromTensor("input_ids", idTensor);
        NamedOnnxValue hiddenValue = NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden);
        List<NamedOnnxValue> inputs = _reusableKvInputs ??= new List<NamedOnnxValue>(_decoderPastInputNames.Length + 3);

        // Step 0 output holds immutable encoder KV projections; prevDecoderOut holds the previous step's
        // decoder KV tensors until the next Run() completes, avoiding per-step KV tensor .Clone() copies.
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? step0Out = null;
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? prevDecoderOut = null;
        try
        {
            for (int step = 0; step < MaxDecodeTokens; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                curTokenBuf[0] = step == 0 ? StartTokenId : generatedIds[^1];
                inputs.Clear();
                inputs.Add(idValue);
                inputs.Add(hiddenValue);
                inputs.Add(step > 0 ? TrueCacheValue : FalseCacheValue);
                foreach (string pastName in _decoderPastInputNames)
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(pastName, pastTensors[pastName]));
                }

                IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? decOut = _decoderSession!.Run(inputs);
                try
                {
                    prevDecoderOut?.Dispose();
                    prevDecoderOut = null;

                    Tensor<float> logits = decOut[0].AsTensor<float>();
                    (int bestTok, double logProb, float prob) = SelectBestTokenAndProb(logits, rowIndex: 0, vocabSize);

                    sumLogProb += logProb;
                    sumProb += prob;
                    minProb = Math.Min(minProb, prob);
                    scoredCount++;

                    if (bestTok == EndTokenId)
                    {
                        break;
                    }

                    if (IsRunawayRepetitionLoop(generatedIds, bestTok))
                    {
                        stoppedByLoopGuard = true;
                        break;
                    }

                    generatedIds.Add(bestTok);

                    foreach (DisposableNamedOnnxValue output in decOut)
                    {
                        const string presentPrefix = "present.";
                        if (!output.Name.StartsWith(presentPrefix, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        string suffix = output.Name[presentPrefix.Length..];
                        if (step > 0 && !suffix.Contains(".decoder.", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        string pastKey = "past_key_values." + suffix;
                        if (pastTensors.ContainsKey(pastKey))
                        {
                            pastTensors[pastKey] = output.AsTensor<float>();
                        }
                    }

                    if (step == 0)
                    {
                        step0Out = decOut;
                    }
                    else
                    {
                        prevDecoderOut = decOut;
                    }

                    decOut = null;
                }
                finally
                {
                    decOut?.Dispose();
                }
            }
        }
        finally
        {
            inputs.Clear();
            pastTensors.Clear();
            prevDecoderOut?.Dispose();
            step0Out?.Dispose();
        }

        return FinalizeDecodedTokens(generatedIds, sumLogProb, sumProb, minProb, scoredCount, stoppedByLoopGuard);
    }

    private CropRecognitionResult RunStatelessDecoder(Tensor<float> hidden, CancellationToken cancellationToken)
    {
        long[] tokenBuf = _reusableStatelessTokenBuffer ??= new long[MaxDecodeTokens + 1];
        tokenBuf[0] = StartTokenId;
        int tokenCount = 1;

        var generatedIds = new List<long>(48);
        double sumLogProb = 0.0;
        double sumProb = 0.0;
        float minProb = 1.0f;
        int scoredCount = 0;
        bool stoppedByLoopGuard = false;
        int vocabSize = _vocab!.Length;

        NamedOnnxValue[] inputs = _reusableStatelessInputs ??= new NamedOnnxValue[2];
        inputs[1] = NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden);

        try
        {
            for (int step = 0; step < MaxDecodeTokens; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (tokenCount >= tokenBuf.Length)
                {
                    Array.Resize(ref tokenBuf, Math.Max(tokenCount + 1, tokenBuf.Length * 2));
                    _reusableStatelessTokenBuffer = tokenBuf;
                }

                var idTensor = new DenseTensor<long>(tokenBuf.AsMemory(0, tokenCount), [1, tokenCount]);
                inputs[0] = NamedOnnxValue.CreateFromTensor("input_ids", idTensor);

                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> decOut = _decoderSession!.Run(inputs);

                Tensor<float> logits = decOut[0].AsTensor<float>();
                (int bestTok, double logProb, float prob) = SelectBestTokenAndProb(logits, rowIndex: tokenCount - 1, vocabSize);

                sumLogProb += logProb;
                sumProb += prob;
                minProb = Math.Min(minProb, prob);
                scoredCount++;

                if (bestTok == EndTokenId)
                {
                    break;
                }

                if (IsRunawayRepetitionLoop(generatedIds, bestTok))
                {
                    stoppedByLoopGuard = true;
                    break;
                }

                tokenBuf[tokenCount++] = bestTok;
                generatedIds.Add(bestTok);
            }
        }
        finally
        {
            Array.Clear(inputs);
        }

        return FinalizeDecodedTokens(generatedIds, sumLogProb, sumProb, minProb, scoredCount, stoppedByLoopGuard);
    }

    internal static (int TokenId, double LogProb, float Prob) SelectBestTokenAndProb(
        Tensor<float> logits,
        int rowIndex,
        int vocabSize)
    {
        int modelVocab = Math.Min(vocabSize, (int)logits.Dimensions[2]);
        int bestTok = (int)EndTokenId;
        float bestVal = float.NegativeInfinity;

        if (logits is DenseTensor<float> dense &&
            logits.Rank == 3 &&
            dense.Strides[2] == 1 &&
            dense.Strides[1] >= modelVocab)
        {
            ReadOnlySpan<float> row = dense.Buffer.Span.Slice(rowIndex * dense.Strides[1], modelVocab);
            for (int v = 0; v < modelVocab; v++)
            {
                if (!IsDecoderTokenAllowed(v))
                {
                    continue;
                }

                float lv = row[v];
                if (lv > bestVal)
                {
                    bestVal = lv;
                    bestTok = v;
                }
            }

            if (float.IsNegativeInfinity(bestVal))
            {
                return ((int)EndTokenId, 0.0, 1.0f);
            }

            double sumExp = 0.0;
            for (int v = 0; v < modelVocab; v++)
            {
                if (!IsDecoderTokenAllowed(v))
                {
                    continue;
                }

                float diff = row[v] - bestVal;
                // exp(-23) is ~1e-10, so even summed across 6,822 tokens the tail stays < 1e-6 relative to exp(0)=1.
                if (diff > -23f)
                {
                    sumExp += Math.Exp(diff);
                }
            }

            double logProb = -Math.Log(Math.Max(sumExp, 1.0));
            float prob = (float)Math.Exp(logProb);
            return (bestTok, logProb, prob);
        }

        for (int v = 0; v < modelVocab; v++)
        {
            if (!IsDecoderTokenAllowed(v))
            {
                continue;
            }

            float lv = logits[0, rowIndex, v];
            if (lv > bestVal)
            {
                bestVal = lv;
                bestTok = v;
            }
        }

        if (float.IsNegativeInfinity(bestVal))
        {
            return ((int)EndTokenId, 0.0, 1.0f);
        }

        double fallbackSumExp = 0.0;
        for (int v = 0; v < modelVocab; v++)
        {
            if (!IsDecoderTokenAllowed(v))
            {
                continue;
            }

            float diff = logits[0, rowIndex, v] - bestVal;
            if (diff > -23f)
            {
                fallbackSumExp += Math.Exp(diff);
            }
        }

        double fallbackLogProb = -Math.Log(Math.Max(fallbackSumExp, 1.0));
        float fallbackProb = (float)Math.Exp(fallbackLogProb);
        return (bestTok, fallbackLogProb, fallbackProb);
    }

    private CropRecognitionResult FinalizeDecodedTokens(
        List<long> generatedIds,
        double sumLogProb,
        double sumProb,
        float minProb,
        int scoredCount,
        bool stoppedByLoopGuard)
    {
        string text = DecodeTokenSequence(generatedIds, _vocab!);
        if (scoredCount <= 0)
        {
            return new CropRecognitionResult(text, -10.0, 0f, 0f, stoppedByLoopGuard);
        }

        double meanLogProb = sumLogProb / scoredCount;
        float avgProb = (float)(sumProb / scoredCount);
        return new CropRecognitionResult(text, meanLogProb, avgProb, minProb, stoppedByLoopGuard);
    }

    private void EnsureDetectorSessionLoaded()
    {
        if (!File.Exists(_paths.BubbleDetectorModelPath))
        {
            throw new FileNotFoundException("未找到气泡检测模型 bubble-detector.onnx。", _paths.BubbleDetectorModelPath);
        }

        _detectorSession ??= CreateSession(_paths.BubbleDetectorModelPath);
    }

    private void EnsureRecognizerSessionsLoaded()
    {
        if (!File.Exists(_paths.MangaOcrEncoderPath) ||
            !File.Exists(_paths.MangaOcrDecoderPath) ||
            !File.Exists(_paths.MangaOcrVocabPath))
        {
            throw new FileNotFoundException("未找到 Manga-OCR CPT-v2 识别模型文件。");
        }

        _vocab ??= File.ReadAllLines(_paths.MangaOcrVocabPath);
        _encoderSession ??= CreateSession(_paths.MangaOcrEncoderPath);
        if (_decoderSession is null)
        {
            _decoderSession = CreateSession(_paths.MangaOcrDecoderPath);
            _decoderUsesKvCache = _decoderSession.InputMetadata.ContainsKey("use_cache_branch");
            _decoderPastInputNames = _decoderSession.InputMetadata.Keys
                .Where(static k => k.StartsWith("past_key_values.", StringComparison.Ordinal))
                .OrderBy(static k => k, StringComparer.Ordinal)
                .ToArray();

            var emptyPast = new Dictionary<string, DenseTensor<float>>(_decoderPastInputNames.Length, StringComparer.Ordinal);
            foreach (string pastName in _decoderPastInputNames)
            {
                int[] dims = _decoderSession.InputMetadata[pastName].Dimensions;
                int numHeads = dims.Length == 4 && dims[1] > 0 ? dims[1] : 12;
                int headDim = dims.Length == 4 && dims[3] > 0 ? dims[3] : 64;
                emptyPast[pastName] = new DenseTensor<float>([1, numHeads, 0, headDim]);
            }

            _reusableEmptyPastTensors = emptyPast;
        }
    }

    private void EnsureAllSessionsLoaded()
    {
        EnsureDetectorSessionLoaded();
        EnsureRecognizerSessionsLoaded();
    }

    internal static SessionOptions CreateOptimizedSessionOptions() =>
        CreateOptimizedSessionOptions(Environment.ProcessorCount);

    internal static SessionOptions CreateOptimizedSessionOptions(int processorCount)
    {
        int intraThreads = processorCount <= 1 ? 1 : Math.Clamp(processorCount / 2, 2, 6);
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = intraThreads,
            EnableMemoryPattern = true,
            EnableCpuMemArena = true
        };
        opts.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        return opts;
    }

    private static InferenceSession CreateSession(string modelPath)
    {
        using SessionOptions opts = CreateOptimizedSessionOptions();
        return new InferenceSession(modelPath, opts);
    }

    private void DisposeSessionsCore()
    {
        _detectorSession?.Dispose();
        _detectorSession = null;
        _encoderSession?.Dispose();
        _encoderSession = null;
        _decoderSession?.Dispose();
        _decoderSession = null;
        _decoderUsesKvCache = false;
        _decoderPastInputNames = [];
        _reusableEmptyPastTensors = null;
        _vocab = null;
        _reusableCrop224?.Dispose();
        _reusableCrop224 = null;
        _reusableDetectorBitmap?.Dispose();
        _reusableDetectorBitmap = null;
        _reusableLuma224 = null;
        _reusableDetectorInput = null;
        _reusableOrigSizesBuffer = null;
        _reusableOrigSizesTensor = null;
        _reusableDetectorInputs = null;
        _reusableRecognizerInput = null;
        _reusableEncoderInputs = null;
        _reusableKvTokenBuf = null;
        _reusableKvIdTensor = null;
        _reusableKvIdValue = null;
        _reusableKvInputs?.Clear();
        _reusableKvInputs = null;
        _reusableKvPastTensors?.Clear();
        _reusableKvPastTensors = null;
        _reusableStatelessTokenBuffer = null;
        _reusableStatelessInputs = null;
    }

    internal sealed class PinnedBitmapScope : IDisposable
    {
        private GCHandle _handle;
        private SKBitmap? _bitmap;
        private bool _disposed;

        public PinnedBitmapScope(DecodedPixelBuffer pixels)
        {
            ArgumentNullException.ThrowIfNull(pixels);
            _handle = GCHandle.Alloc(pixels.BgraPixels, GCHandleType.Pinned);
            try
            {
                var bmp = new SKBitmap();
                var info = new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                if (!bmp.InstallPixels(info, _handle.AddrOfPinnedObject(), pixels.Width * 4))
                {
                    bmp.Dispose();
                    throw new InvalidOperationException("Failed to install pinned BGRA pixels into SKBitmap.");
                }

                _bitmap = bmp;
            }
            catch
            {
                if (_handle.IsAllocated)
                {
                    _handle.Free();
                }

                throw;
            }
        }

        public SKBitmap Bitmap
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _bitmap!;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // Dispose SKBitmap first so Skia releases pixel references before unpinning the managed buffer.
            _bitmap?.Dispose();
            _bitmap = null;
            if (_handle.IsAllocated)
            {
                _handle.Free();
            }
        }
    }

    private static PinnedBitmapScope CreateBitmapFromPixels(DecodedPixelBuffer pixels) =>
        new(pixels);

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

    private async Task DownloadFileWithResumeAsync(
        string url,
        string destinationPath,
        string label,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string partPath = destinationPath + ".part";
        long existingBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("EhViewer.Desktop/1.0");
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingBytes > 0)
        {
            File.Move(partPath, destinationPath, overwrite: true);
            progress?.Report(new ModelDownloadProgress(existingBytes, existingBytes, $"{label} 完成"));
            return;
        }

        response.EnsureSuccessStatusCode();

        bool append = response.StatusCode == HttpStatusCode.PartialContent && existingBytes > 0;
        long baseOffset = append ? existingBytes : 0;
        long? contentLength = response.Content.Headers.ContentLength;
        long? totalBytes = contentLength.HasValue ? baseOffset + contentLength.Value : null;

        await using (Stream remote = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var local = new FileStream(
            partPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true))
        {
            byte[] buffer = new byte[81920];
            long written = baseOffset;
            long lastTick = Environment.TickCount64;
            progress?.Report(new ModelDownloadProgress(written, totalBytes, $"{label}..."));

            int read;
            while ((read = await remote.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                long now = Environment.TickCount64;
                if (now - lastTick >= 200)
                {
                    lastTick = now;
                    progress?.Report(new ModelDownloadProgress(written, totalBytes, $"{label}..."));
                }
            }

            await local.FlushAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(new ModelDownloadProgress(written, totalBytes ?? written, $"{label} 完成"));
        }

        File.Move(partPath, destinationPath, overwrite: true);
    }
}

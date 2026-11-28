using System.Runtime.InteropServices;
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
public sealed partial class ComicBubbleOcrPipeline : IDisposable
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
    private const long MinValidOnnxBytes = 1024;
    private const long MinValidVocabBytes = 64;

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

    public bool AreDetectorModelsReady => IsValidModelFile(_paths.BubbleDetectorModelPath, isVocabFile: false);

    public bool AreAllModelsReady =>
        AreDetectorModelsReady &&
        IsValidModelFile(_paths.MangaOcrEncoderPath, isVocabFile: false) &&
        IsValidModelFile(_paths.MangaOcrDecoderPath, isVocabFile: false) &&
        IsValidModelFile(_paths.MangaOcrVocabPath, isVocabFile: true);

    public bool HasLegacyV1Models =>
        !AreAllModelsReady &&
        (File.Exists(Path.Combine(_paths.OcrModelsDirectory, LegacyV1ModelFileNames[0])) ||
         File.Exists(Path.Combine(_paths.OcrModelsDirectory, LegacyV1ModelFileNames[1])));

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
}

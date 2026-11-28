using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace EhViewer.Desktop.Services.Translation;

public sealed partial class ComicBubbleOcrPipeline
{
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
}

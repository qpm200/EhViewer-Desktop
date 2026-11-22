using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EhViewer.Core.Security;
using EhViewer.Desktop.Reader;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Translation;
using EhViewer.Desktop.ViewModels;
using SkiaSharp;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class MangaTranslationTests
{
    [Fact]
    public void OcrRegionClusterer_MergesVerticalJapaneseColumnsRightToLeftAndKeepsSeparateBubblesIsolated()
    {
        var lines = new List<OcrRawLine>
        {
            // Right bubble (two vertical columns inside one speech bubble: right col first, left col second)
            new("そんな", 820, 120, 32, 160),
            new("バカな", 778, 125, 32, 165),
            // Distant left-bottom bubble (horizontal English)
            new("Wait a", 110, 760, 120, 26),
            new("second!", 112, 792, 130, 26)
        };

        IReadOnlyList<OcrTextBlock> blocks = OcrRegionClusterer.Cluster(lines, 1000, 1400);

        Assert.Equal(2, blocks.Count);
        OcrTextBlock verticalBubble = Assert.Single(blocks, b => b.IsVertical);
        Assert.Equal("そんなバカな", verticalBubble.Text);
        Assert.InRange(verticalBubble.NormalizedX, 0.70, 0.85);

        OcrTextBlock horizontalBubble = Assert.Single(blocks, b => !b.IsVertical);
        Assert.Equal("Wait a second!", horizontalBubble.Text);
        Assert.InRange(horizontalBubble.NormalizedY, 0.50, 0.65);
    }

    [Fact]
    public void RefusalDetector_FlagsSafetyRefusalsWithoutFalsePositivesOnDialogue()
    {
        Assert.True(RefusalDetector.IsRefusalResponse("I'm sorry, I cannot translate explicit sexual content.", out _));
        Assert.True(RefusalDetector.IsRefusalResponse("抱歉，我无法为您翻译包含露骨内容的内容。", out _));
        Assert.True(RefusalDetector.IsRefusalResponse("As an AI language model, I must decline this request.", out _));
        Assert.True(RefusalDetector.IsRefusalResponse("   ", out _));

        Assert.False(RefusalDetector.IsRefusalResponse("对不起，刚才撞到你了！", out _));
        Assert.False(RefusalDetector.IsRefusalResponse("抱歉让你久等了，快进来吧。", out _));
        Assert.False(RefusalDetector.IsRefusalResponse("等、等一下，笨蛋！", out _));
    }

    [Fact]
    public void LlamaCppServerManager_PresetUrlsAndSakuraPayloadMatchSpecification()
    {
        Assert.Equal(
            "https://huggingface.co/SakuraLLM/Sakura-7B-Qwen2.5-v1.0-GGUF/resolve/main/sakura-7b-qwen2.5-v1.0-iq4xs.gguf",
            LlamaCppServerManager.SakuraModelUrl);
        Assert.Contains("hf-mirror.com", LlamaCppServerManager.SakuraModelMirrorUrl, StringComparison.OrdinalIgnoreCase);

        JsonObject payload = LlamaCppServerManager.BuildSakuraChatPayload("ちょっと待って");
        Assert.Equal(0.1, payload["temperature"]!.GetValue<double>(), 3);
        Assert.Equal(0.3, payload["top_p"]!.GetValue<double>(), 3);

        JsonArray messages = payload["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.Contains("轻小说和漫画", messages[0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("将下面的日文文本翻译成中文：\nちょっと待って", messages[1]!["content"]!.GetValue<string>());

        var assets = new List<(string Name, string Url)>
        {
            ("cudart-llama-bin-win-cuda-12.4-x64.zip", "https://example/cudart.zip"),
            ("llama-b5000-bin-win-vulkan-x64.zip", "https://example/vulkan.zip"),
            ("llama-b5000-bin-win-cuda-12.4-x64.zip", "https://example/cuda.zip"),
            ("llama-b5000-bin-win-cpu-x64.zip", "https://example/cpu.zip")
        };

        Assert.Equal("llama-b5000-bin-win-vulkan-x64.zip", LlamaCppServerManager.SelectLlamaCppAsset(assets, "vulkan")?.Name);
        Assert.Equal("llama-b5000-bin-win-cuda-12.4-x64.zip", LlamaCppServerManager.SelectLlamaCppAsset(assets, "cuda")?.Name);
        Assert.Equal("llama-b5000-bin-win-cpu-x64.zip", LlamaCppServerManager.SelectLlamaCppAsset(assets, "cpu")?.Name);
    }

    [Fact]
    public async Task LlamaCppServerManager_DownloadResumesFromExistingPartFile()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "EhViewer.Desktop.Tests", Guid.NewGuid().ToString("N"));
        AppPaths paths = AppPaths.CreateAt(tempRoot);
        Directory.CreateDirectory(paths.ModelsDirectory);
        string dest = Path.Combine(paths.ModelsDirectory, "sakura-test.gguf");
        await File.WriteAllBytesAsync(dest + ".part", Encoding.ASCII.GetBytes("HEADER_"));

        long? seenRangeStart = null;
        var handler = new StubHttpMessageHandler(req =>
        {
            seenRangeStart = req.Headers.Range?.Ranges.FirstOrDefault()?.From;
            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("BODY_PAYLOAD"))
            };
            resp.Content.Headers.ContentLength = 12;
            return resp;
        });

        try
        {
            using var http = new HttpClient(handler);
            using var mgr = new LlamaCppServerManager(paths, http);
            string saved = await mgr.DownloadSakuraModelAsync(useMirror: false, dest);

            Assert.Equal(7, seenRangeStart);
            Assert.Equal(dest, saved);
            Assert.False(File.Exists(dest + ".part"));
            Assert.Equal("HEADER_BODY_PAYLOAD", await File.ReadAllTextAsync(dest));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MangaTranslationPipeline_AutoFallsBackOnRefusalOrHttpFilterAndCachesResult()
    {
        int calls = 0;
        var handler = new StubHttpMessageHandler(req =>
        {
            calls++;
            string uri = req.RequestUri!.ToString();
            if (uri.Contains("custom.example", StringComparison.OrdinalIgnoreCase))
            {
                string refusalJson = """
                    {"choices":[{"finish_reason":"stop","message":{"content":"I'm sorry, I cannot translate explicit sexual content."}}]}
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(refusalJson, Encoding.UTF8, "application/json")
                };
            }

            string googleMtJson = """[[["笨蛋、等一下！","ちょっと待って",null,null,1]],null,"ja"]""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(googleMtJson, Encoding.UTF8, "application/json")
            };
        });

        var settings = new DesktopSettings
        {
            ReaderTranslationProvider = TranslationProviderIds.CustomOpenAiCompatible,
            ReaderTranslationAutoFallback = true,
            CustomTranslationEndpoint = "https://custom.example/v1",
            CustomTranslationModel = "gpt-test",
            CustomTranslationApiKeyProtected = DpapiCredentialProtector.Instance.Protect("sk-live-test")
        };

        using var http = new HttpClient(handler);
        var pipeline = new MangaTranslationPipeline(null, () => settings, DpapiCredentialProtector.Instance, http);

        TranslationResult r1 = await pipeline.TranslateAsync("ちょっと待って");
        Assert.True(r1.UsedFallback);
        Assert.Equal(TranslationProviderIds.DirectMachineTranslation, r1.ProviderId);
        Assert.Equal("笨蛋、等一下！", r1.TranslatedText);
        Assert.Contains("自定义 OpenAI 接口", r1.FallbackNote);
        Assert.Equal(2, calls);

        // Cached second invocation should not make additional HTTP calls
        TranslationResult r2 = await pipeline.TranslateAsync("ちょっと待って");
        Assert.Equal(r1.TranslatedText, r2.TranslatedText);
        Assert.Equal(2, calls);

        pipeline.ClearCache();
        settings.ReaderTranslationAutoFallback = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.TranslateAsync("ちょっと待って"));
    }

    [Fact]
    public async Task MangaTranslationPipeline_DirectMtCascadesThroughRateLimitsAndFormats()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            string uri = req.RequestUri!.ToString();
            if (uri.Contains("clients5.google.com", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }

            if (uri.Contains("translate.googleapis.com", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }

            string myMemoryJson = """{"responseData":{"translatedText":"等一下！","match":0.99},"responseStatus":200}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(myMemoryJson, Encoding.UTF8, "application/json")
            };
        });

        var settings = new DesktopSettings { ReaderTranslationProvider = TranslationProviderIds.DirectMachineTranslation };
        using var http = new HttpClient(handler);
        var pipeline = new MangaTranslationPipeline(null, () => settings, DpapiCredentialProtector.Instance, http);

        TranslationResult result = await pipeline.TranslateAsync("Wait a second!");
        Assert.Equal("等一下！", result.TranslatedText);
    }

    [Fact]
    public void SettingsViewModel_PersistsOcrSakuraAndProtectedCustomApiKeyRoundTrip()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "EhViewer.Desktop.Tests", Guid.NewGuid().ToString("N"));
        AppPaths paths = AppPaths.CreateAt(tempRoot);
        try
        {
            using (AppServices services = AppServices.Create(paths))
            {
                Assert.NotNull(services.GetRequiredService<ComicBubbleOcrPipeline>());
                Assert.NotNull(services.GetRequiredService<MangaOcrService>());
                Assert.NotNull(services.GetRequiredService<LlamaCppServerManager>());
                Assert.NotNull(services.GetRequiredService<MangaTranslationPipeline>());

                var vm = new SettingsViewModel(services);
                vm.ReaderOcrLanguageIndex = 1; // ja
                vm.ReaderTranslationProviderIndex = 1; // llama-sakura
                vm.ReaderTranslationAutoFallback = false;
                vm.EnableLocalSakuraModel = true;
                vm.UseHfMirrorForModelDownload = false;
                vm.LocalLlamaGpuLayers = 28;
                vm.LocalLlamaPort = 8899;
                vm.LocalLlamaBackendIndex = 1; // cuda
                vm.CustomTranslationEndpoint = "https://custom-llm.local/v1";
                vm.CustomTranslationModel = "sakura-custom";
                vm.DeepSeekEndpoint = "https://api.deepseek.com";
                vm.DeepSeekModel = "deepseek-reasoner";
                vm.DeepSeekReasoningEffortIndex = 0; // low
                vm.Save();
                vm.SaveCustomTranslationApiKey("sk-secret-key-999");
                vm.SaveDeepSeekApiKey("sk-deepseek-secret-777");

                Assert.NotNull(services.Settings.CustomTranslationApiKeyProtected);
                Assert.DoesNotContain("sk-secret-key-999", services.Settings.CustomTranslationApiKeyProtected, StringComparison.Ordinal);
                Assert.True(DpapiCredentialProtector.Instance.IsProtected(services.Settings.CustomTranslationApiKeyProtected));
                Assert.NotNull(services.Settings.DeepSeekApiKeyProtected);
                Assert.DoesNotContain("sk-deepseek-secret-777", services.Settings.DeepSeekApiKeyProtected, StringComparison.Ordinal);
                Assert.True(DpapiCredentialProtector.Instance.IsProtected(services.Settings.DeepSeekApiKeyProtected));
            }

            using (AppServices reloaded = AppServices.Create(paths))
            {
                var vm2 = new SettingsViewModel(reloaded);
                Assert.Equal(1, vm2.ReaderOcrLanguageIndex);
                Assert.Equal(1, vm2.ReaderTranslationProviderIndex);
                Assert.False(vm2.ReaderTranslationAutoFallback);
                Assert.True(vm2.EnableLocalSakuraModel);
                Assert.False(vm2.UseHfMirrorForModelDownload);
                Assert.Equal(28, vm2.LocalLlamaGpuLayers);
                Assert.Equal(8899, vm2.LocalLlamaPort);
                Assert.Equal(1, vm2.LocalLlamaBackendIndex);
                Assert.Equal("https://custom-llm.local/v1", vm2.CustomTranslationEndpoint);
                Assert.Equal("sakura-custom", vm2.CustomTranslationModel);
                Assert.Contains("DPAPI", vm2.CustomTranslationApiKeyStatus, StringComparison.Ordinal);
                Assert.Equal("https://api.deepseek.com", vm2.DeepSeekEndpoint);
                Assert.Equal("deepseek-reasoner", vm2.DeepSeekModel);
                Assert.Equal(0, vm2.DeepSeekReasoningEffortIndex);
                Assert.Contains("DPAPI", vm2.DeepSeekApiKeyStatus, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ComicBubbleOcrPipeline_PostProcessDetections_DedupesRecoversAndOrdersRightToLeft()
    {
        var raw = new List<DetectedBubbleRegion>
        {
            // Left bubble text (LabelId=1)
            new(90, 120, 210, 260, 0.90f, 1),
            // Overlapping duplicate text box on left bubble -> should be suppressed by NMS
            new(95, 125, 205, 255, 0.72f, 1),
            // Right bubble text (LabelId=1) -> higher X, same Y band -> ordered first (RTL manga)
            new(700, 115, 840, 270, 0.93f, 1),
            // Uncovered bottom speech bubble (LabelId=0) -> recovered with 12% inset
            new(380, 620, 580, 820, 0.88f, 0),
            // Low confidence free text -> filtered out
            new(20, 20, 60, 50, 0.25f, 2)
        };

        IReadOnlyList<DetectedBubbleRegion> ordered = ComicBubbleOcrPipeline.PostProcessDetections(raw, 1000, 1000);
        Assert.Equal(3, ordered.Count);
        Assert.True(ordered[0].X1 > 650); // Right top bubble comes first
        Assert.True(ordered[1].X1 < 150); // Left top bubble comes second
        Assert.True(ordered[2].Y1 > 600 && ordered[2].X1 > 390); // Recovered bottom bubble comes third
    }

    [Fact]
    public void ComicBubbleOcrPipeline_GuardAndTokenDecodingWork()
    {
        // Repeating Japanese SFX / natural reduplication (up to 7 consecutive tokens or <6 multi-token cycles)
        // must NOT be blocked; only runaway loops (>=8 identical or >=6 cyclical repeats) trigger the guard.
        long[] naturalSfx = [100, 100, 100, 100]; // 4 -> 5th allowed
        Assert.False(ComicBubbleOcrPipeline.IsRunawayRepetitionLoop(naturalSfx, candidateToken: 100));

        long[] runawaySingle = [100, 100, 100, 100, 100, 100, 100]; // 7 + 1 = 8 -> blocked
        Assert.True(ComicBubbleOcrPipeline.IsRunawayRepetitionLoop(runawaySingle, candidateToken: 100));

        long[] naturalPhraseRepeat = [10, 20, 10, 20, 10]; // 3rd cycle allowed
        Assert.False(ComicBubbleOcrPipeline.IsRunawayRepetitionLoop(naturalPhraseRepeat, candidateToken: 20));

        long[] runawayCycle2 = [10, 20, 10, 20, 10, 20, 10, 20, 10, 20, 10]; // 6th cycle -> blocked
        Assert.True(ComicBubbleOcrPipeline.IsRunawayRepetitionLoop(runawayCycle2, candidateToken: 20));

        // CPT-v2 predicts content/SEP directly on step 0 and must suppress [PAD]=0, [UNK]=1, [CLS]=2, [MASK]=4 at all steps.
        Assert.False(ComicBubbleOcrPipeline.IsDecoderTokenAllowed(0));
        Assert.False(ComicBubbleOcrPipeline.IsDecoderTokenAllowed(1));
        Assert.False(ComicBubbleOcrPipeline.IsDecoderTokenAllowed(2));
        Assert.False(ComicBubbleOcrPipeline.IsDecoderTokenAllowed(4));
        Assert.True(ComicBubbleOcrPipeline.IsDecoderTokenAllowed(3));
        Assert.True(ComicBubbleOcrPipeline.IsDecoderTokenAllowed(5));

        // Noise vs real text helpers
        Assert.True(ComicBubbleOcrPipeline.IsPunctuationOrDotOnly("......"));
        Assert.True(ComicBubbleOcrPipeline.IsPunctuationOrDotOnly("・―/…"));
        Assert.False(ComicBubbleOcrPipeline.IsPunctuationOrDotOnly("あー"));
        Assert.False(ComicBubbleOcrPipeline.IsPunctuationOrDotOnly("ちょっと"));
        Assert.False(ComicBubbleOcrPipeline.IsPunctuationOrDotOnly("嗚♥"));
        Assert.False(ComicBubbleOcrPipeline.IsPunctuationOrDotOnly("!?"));
        Assert.True(
            ComicBubbleOcrPipeline.ScoreRecognitionCandidate("なに!?", -0.25, 0.72f, stoppedByLoopGuard: false) >
            ComicBubbleOcrPipeline.ScoreRecognitionCandidate("...", -0.05, 0.90f, stoppedByLoopGuard: false));

        // Legacy v1 model detection
        string tempRoot = Path.Combine(Path.GetTempPath(), "EhViewer.Desktop.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths legacyPaths = AppPaths.CreateAt(tempRoot);
            Directory.CreateDirectory(legacyPaths.OcrModelsDirectory);
            File.WriteAllBytes(Path.Combine(legacyPaths.OcrModelsDirectory, "manga-ocr-encoder.onnx"), [1, 2, 3]);
            using var pipeline = new ComicBubbleOcrPipeline(legacyPaths);
            Assert.False(pipeline.AreAllModelsReady);
            Assert.True(pipeline.HasLegacyV1Models);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }

        // Tight text_bubble crop should not expand outward enough to drag oval borders in
        SKRect tightBubble = ComicBubbleOcrPipeline.ExpandAndClampRect(
            new DetectedBubbleRegion(100, 100, 220, 400, 0.9f, LabelId: 1),
            800,
            1000);
        Assert.InRange(100f - tightBubble.Left, 1f, 3f);

        string[] vocab = ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", "そ", "##ん", "な", "待", "##っ", "て"];
        string decoded = ComicBubbleOcrPipeline.DecodeTokenSequence([5, 6, 7, 8, 9, 10, 3], vocab);
        Assert.Equal("そんな待って", decoded);
    }

    [Fact]
    public void ComicBubbleOcrPipeline_HasCentralVerticalColumnGutter_SupportsDarkAndLightBubbles()
    {
        using var bmp = new SKBitmap(200, 320, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            // Dark two-column bubble (white strokes at left & right columns, pure dark gutter at x=75..125)
            canvas.Clear(new SKColor(18, 18, 22));
            using var whiteInk = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            for (int y = 30; y < 270; y += 36)
            {
                canvas.DrawRect(new SKRect(28, y, 54, y + 22), whiteInk);
                canvas.DrawRect(new SKRect(146, y, 172, y + 22), whiteInk);
            }
        }

        Assert.True(ComicBubbleOcrPipeline.HasCentralVerticalColumnGutter(bmp, new SKRect(0, 0, 200, 320)));

        // Single-column dark bubble (white strokes across central band) -> must return false
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(18, 18, 22));
            using var whiteInk = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            for (int y = 30; y < 270; y += 36)
            {
                canvas.DrawRect(new SKRect(86, y, 114, y + 22), whiteInk);
            }
        }

        Assert.False(ComicBubbleOcrPipeline.HasCentralVerticalColumnGutter(bmp, new SKRect(0, 0, 200, 320)));

        // Light two-column bubble -> must return true
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(248, 248, 248));
            using var blackInk = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
            for (int y = 30; y < 270; y += 36)
            {
                canvas.DrawRect(new SKRect(28, y, 54, y + 22), blackInk);
                canvas.DrawRect(new SKRect(146, y, 172, y + 22), blackInk);
            }
        }

        Assert.True(ComicBubbleOcrPipeline.HasCentralVerticalColumnGutter(bmp, new SKRect(0, 0, 200, 320)));
    }

    [Fact]
    public async Task MangaOcrService_WithoutModelsOrJaPack_ThrowsActionableErrorInsteadOfGarbage()
    {
        if (MangaOcrService.IsJapanesePackInstalled())
        {
            return;
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "EhViewer.Desktop.Tests", Guid.NewGuid().ToString("N"));
        AppPaths emptyPaths = AppPaths.CreateAt(tempRoot);
        try
        {
            using var pipeline = new ComicBubbleOcrPipeline(emptyPaths);
            Assert.False(pipeline.AreAllModelsReady);
            var ocr = new MangaOcrService(pipeline);

            using var bmp = new SKBitmap(200, 200, SKColorType.Bgra8888, SKAlphaType.Premul);
            DecodedPixelBuffer? pixels = SkiaBitmapHelper.ExtractBgraPixels(bmp);
            Assert.NotNull(pixels);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ocr.RecognizeAsync("empty:0", pixels!, "ja", "Test"));
            Assert.Contains("漫画专用离线 OCR", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ComicBubbleOcrPipeline_EndToEndVerticalMangaRecognition_WhenModelsExist()
    {
        AppPaths defaultPaths = AppPaths.CreateDefault();
        using var pipeline = new ComicBubbleOcrPipeline(defaultPaths);
        if (!pipeline.AreAllModelsReady)
        {
            return;
        }

        // Render a clean manga page with a white oval speech bubble and vertical Japanese text: ちょっと待って
        using var bmp = new SKBitmap(720, 960, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(235, 235, 235));
            using var fill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
            using var stroke = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 5, IsAntialias = true };
            var bubbleRect = new SKRect(410, 120, 620, 480);
            canvas.DrawOval(bubbleRect, fill);
            canvas.DrawOval(bubbleRect, stroke);

            using var typeface = SKTypeface.FromFamilyName("Yu Gothic", SKFontStyle.Bold)
                ?? SKTypeface.FromFamilyName("MS Gothic", SKFontStyle.Bold)
                ?? SKTypeface.Default;
            using var font = new SKFont(typeface, 42);
            using var textPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            string[] chars = ["ち", "ょ", "っ", "と", "待", "っ", "て"];
            float y = 180;
            foreach (string ch in chars)
            {
                canvas.DrawText(ch, 500, y, SKTextAlign.Left, font, textPaint);
                y += 44;
            }
        }

        DecodedPixelBuffer? pixels = SkiaBitmapHelper.ExtractBgraPixels(bmp);
        Assert.NotNull(pixels);

        var service = new MangaOcrService(pipeline);
        OcrPageResult result = await service.RecognizeAsync("integration-ja:0", pixels!, "ja", "Test");
        Assert.Single(result.Blocks);
        Assert.Equal("ちょっと待って", result.Blocks[0].Text);

        // Also test drag-crop normalized box directly on the speech bubble
        OcrTextBlock? cropped = await service.RecognizeNormalizedRegionAsync(
            pixels!,
            410.0 / 720.0,
            120.0 / 960.0,
            210.0 / 720.0,
            360.0 / 960.0,
            "ja",
            "Test");
        Assert.NotNull(cropped);
        Assert.Equal("ちょっと待って", cropped!.Text);
    }

    [Fact]
    public async Task ComicBubbleOcrPipeline_CptV2RepeatingSfxDarkBubbleAndWashedText_WhenModelsExist()
    {
        AppPaths defaultPaths = AppPaths.CreateDefault();
        using var pipeline = new ComicBubbleOcrPipeline(defaultPaths);
        if (!pipeline.AreAllModelsReady)
        {
            return;
        }

        using var typeface = SKTypeface.FromFamilyName("Yu Gothic", SKFontStyle.Bold)
            ?? SKTypeface.FromFamilyName("MS Gothic", SKFontStyle.Bold)
            ?? SKTypeface.Default;
        using var bmp = new SKBitmap(600, 420, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);

            // Region 1 (left): Repeating Japanese SFX "ゴゴゴゴゴ" (would be truncated by old 3-gram blocker!)
            using var darkFont = new SKFont(typeface, 40);
            using var darkPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            string[] sfx = ["ゴ", "ゴ", "ゴ", "ゴ", "ゴ"];
            for (int i = 0; i < sfx.Length; i++)
            {
                canvas.DrawText(sfx[i], 65, 95 + i * 48, SKTextAlign.Left, darkFont, darkPaint);
            }

            // Region 2 (middle): Dark inverted bubble "だめ"
            using var blackFill = new SKPaint { Color = new SKColor(16, 16, 20), Style = SKPaintStyle.Fill };
            canvas.DrawRect(new SKRect(210, 40, 380, 260), blackFill);
            using var whitePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawText("だ", 275, 130, SKTextAlign.Left, darkFont, whitePaint);
            canvas.DrawText("め", 275, 195, SKTextAlign.Left, darkFont, whitePaint);

            // Region 3 (right): Washed-out low-contrast gray ink on light gray paper "待って"
            using var washedBg = new SKPaint { Color = new SKColor(208, 208, 208), Style = SKPaintStyle.Fill };
            canvas.DrawRect(new SKRect(410, 40, 580, 320), washedBg);
            using var washedInk = new SKPaint { Color = new SKColor(128, 128, 128), IsAntialias = true };
            canvas.DrawText("待", 475, 125, SKTextAlign.Left, darkFont, washedInk);
            canvas.DrawText("っ", 475, 185, SKTextAlign.Left, darkFont, washedInk);
            canvas.DrawText("て", 475, 245, SKTextAlign.Left, darkFont, washedInk);
        }

        DecodedPixelBuffer? pixels = SkiaBitmapHelper.ExtractBgraPixels(bmp);
        Assert.NotNull(pixels);
        var service = new MangaOcrService(pipeline);

        OcrTextBlock? sfxBlock = await service.RecognizeNormalizedRegionAsync(
            pixels!, 20.0 / 600.0, 20.0 / 420.0, 150.0 / 600.0, 340.0 / 420.0, "ja", "Test");
        Assert.NotNull(sfxBlock);
        Assert.Equal("ゴゴゴゴゴ", sfxBlock!.Text);

        OcrTextBlock? darkBlock = await service.RecognizeNormalizedRegionAsync(
            pixels!, 210.0 / 600.0, 40.0 / 420.0, 170.0 / 600.0, 220.0 / 420.0, "ja", "Test");
        Assert.NotNull(darkBlock);
        Assert.Equal("だめ", darkBlock!.Text);

        OcrTextBlock? washedBlock = await service.RecognizeNormalizedRegionAsync(
            pixels!, 410.0 / 600.0, 40.0 / 420.0, 170.0 / 600.0, 280.0 / 420.0, "ja", "Test");
        Assert.NotNull(washedBlock);
        Assert.Equal("待って", washedBlock!.Text);
    }

    [Fact]
    public void ComicBubbleOcrPipeline_NormalizationAndOverlapStitchingWork()
    {
        Assert.Equal("ちょっと待って。", ComicBubbleOcrPipeline.NormalizeRecognizedText("ちょ っと 待っ て 。"));
        Assert.Equal("Wait a second!", ComicBubbleOcrPipeline.NormalizeRecognizedText("Wait   a  second!"));
        Assert.Equal("大丈夫......", ComicBubbleOcrPipeline.NormalizeRecognizedText("大丈夫............"));

        string merged = ComicBubbleOcrPipeline.MergeOverlappingRecognitions(
            "ちょっと待って今すぐ",
            "て今すぐ行くから",
            out int overlap);
        Assert.Equal("ちょっと待って今すぐ行くから", merged);
        Assert.True(overlap >= 4);

        string edgeRecovered = ComicBubbleOcrPipeline.MergeOverlappingRecognitions(
            "ちょっと待っぁ",
            "待って今すぐ",
            out int edgeOverlap);
        Assert.Equal("ちょっと待って今すぐ", edgeRecovered);
        Assert.True(edgeOverlap >= 2);
    }

    [Fact]
    public void ComicBubbleOcrPipeline_PostProcessDetections_ContainmentAndMultiBandRtl()
    {
        var raw = new List<DetectedBubbleRegion>
        {
            // Higher-confidence text_free fragment >=80% inside outer text_bubble -> must be suppressed
            new(515, 130, 645, 270, 0.94f, 2),
            new(490, 100, 670, 300, 0.86f, 1),
            // Top band left bubble (offset slightly lower, within band tolerance -> same top band, ordered after top-right)
            new(110, 140, 250, 310, 0.90f, 1),
            // Bottom band right & left bubbles
            new(650, 560, 810, 740, 0.88f, 1),
            new(130, 580, 270, 750, 0.85f, 1)
        };

        IReadOnlyList<DetectedBubbleRegion> ordered = ComicBubbleOcrPipeline.PostProcessDetections(raw, 1000, 1000);
        Assert.Equal(4, ordered.Count);
        Assert.All(ordered, r => Assert.Equal(1, r.LabelId));
        Assert.True(ordered[0].X1 > 450 && ordered[0].Y1 < 200);
        Assert.True(ordered[1].X1 < 200 && ordered[1].Y1 < 200);
        Assert.True(ordered[2].X1 > 600 && ordered[2].Y1 > 500);
        Assert.True(ordered[3].X1 < 200 && ordered[3].Y1 > 500);

        SKRect tallRect = ComicBubbleOcrPipeline.ExpandAndClampRect(
            new DetectedBubbleRegion(100, 100, 200, 500, 0.9f, LabelId: 1),
            800,
            1000);
        float padX = 100f - tallRect.Left;
        float padY = 100f - tallRect.Top;
        Assert.InRange(padX, 1f, 3f);
        Assert.InRange(padY, 2f, 6f);
        Assert.True(padY >= padX);
    }

    [Fact]
    public async Task MangaTranslationPipeline_SanitizeBatchAndContextPayloadsWork()
    {
        Assert.Equal("冷静点，快跑！", MangaTranslationPipeline.SanitizeModelOutput("<think>分析说话语气</think>\n冷静点，快跑！"));
        List<string>? parsed = MangaTranslationPipeline.TryParseNumberedBatch("<think>ok</think>\n[1] 笨蛋！\n[2] 等一下。", 2);
        Assert.NotNull(parsed);
        Assert.Equal(["笨蛋！", "等一下。"], parsed!);
        Assert.Null(MangaTranslationPipeline.TryParseNumberedBatch("[1] 只有一行", 2));

        System.Text.Json.Nodes.JsonObject ctxPayload = LlamaCppServerManager.BuildSakuraChatPayload(
            "お兄ちゃん",
            ["逃げよう", "お兄ちゃん"]);
        string ctxContent = ctxPayload["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("逃げよう", ctxContent, StringComparison.Ordinal);
        int duplicateCount = ctxContent.Split("お兄ちゃん", StringSplitOptions.None).Length - 1;
        Assert.Equal(1, duplicateCount);

        System.Text.Json.Nodes.JsonObject batchPayload = LlamaCppServerManager.BuildSakuraBatchPayload(["待って", "行くぞ"]);
        string batchContent = batchPayload["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("[1] 待って", batchContent, StringComparison.Ordinal);
        Assert.Contains("[2] 行くぞ", batchContent, StringComparison.Ordinal);

        int llmCalls = 0;
        var handler = new StubHttpMessageHandler(req =>
        {
            string uri = req.RequestUri!.ToString();
            if (uri.Contains("chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                llmCalls++;
                string responseJson = llmCalls == 1
                    ? """{"choices":[{"message":{"content":"<think>check</think>\n[1] 等一下\n[2] 别跑！"}}]}"""
                    : """{"choices":[{"message":{"content":"I cannot assist with that request."}}]}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            }

            string googleBody = """[["笨蛋快走"]]""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(googleBody, Encoding.UTF8, "application/json")
            };
        });

        var settings = new DesktopSettings
        {
            ReaderTranslationProvider = TranslationProviderIds.CustomOpenAiCompatible,
            ReaderTranslationAutoFallback = true,
            CustomTranslationEndpoint = "https://custom.example/v1",
            CustomTranslationModel = "sakura-test",
            CustomTranslationApiKeyProtected = DpapiCredentialProtector.Instance.Protect("sk-test-batch")
        };

        using var http = new HttpClient(handler);
        var pipeline = new MangaTranslationPipeline(null, () => settings, DpapiCredentialProtector.Instance, http);

        var blocks = new OcrTextBlock[]
        {
            new("b1", "ちょっと待って", 0.5, 0.1, 0.2, 0.3, IsVertical: true),
            new("b2", "逃げるな！", 0.1, 0.2, 0.2, 0.3, IsVertical: true)
        };

        IReadOnlyDictionary<string, TranslationResult> first = await pipeline.TranslatePageBlocksAsync(blocks);
        Assert.Equal(2, first.Count);
        Assert.Equal("等一下", first["b1"].TranslatedText);
        Assert.Equal("别跑！", first["b2"].TranslatedText);
        Assert.False(first["b1"].UsedFallback);
        Assert.Equal(1, llmCalls);

        // Second call must hit translation cache with 0 extra HTTP requests
        IReadOnlyDictionary<string, TranslationResult> cached = await pipeline.TranslatePageBlocksAsync(blocks);
        Assert.Equal("等一下", cached["b1"].TranslatedText);
        Assert.Equal(1, llmCalls);

        // Next call bypassing cache hits LLM call #2 (refusal) and falls back per-bubble to Direct MT
        IReadOnlyDictionary<string, TranslationResult> fallback = await pipeline.TranslatePageBlocksAsync(blocks, bypassCache: true);
        Assert.Equal(2, fallback.Count);
        Assert.True(fallback["b1"].UsedFallback);
        Assert.Equal(TranslationProviderIds.DirectMachineTranslation, fallback["b1"].ProviderId);
        Assert.Equal("笨蛋快走", fallback["b1"].TranslatedText);
    }

    [Fact]
    public async Task ComicBubbleOcrPipeline_EndToEndLongColumnAndDarkBubble_WhenModelsExist()
    {
        AppPaths defaultPaths = AppPaths.CreateDefault();
        using var pipeline = new ComicBubbleOcrPipeline(defaultPaths);
        if (!pipeline.AreAllModelsReady)
        {
            return;
        }

        using var bmp = new SKBitmap(760, 1100, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(235, 235, 235));
            using var typeface = SKTypeface.FromFamilyName("Yu Gothic", SKFontStyle.Bold)
                ?? SKTypeface.FromFamilyName("MS Gothic", SKFontStyle.Bold)
                ?? SKTypeface.Default;

            // Right bubble: ultra-tall single vertical column (aspect ratio > 4:1 -> triggers smart vertical split & stitch)
            using var whiteFill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
            using var blackStroke = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 5, IsAntialias = true };
            var tallBubble = new SKRect(510, 70, 680, 990);
            canvas.DrawOval(tallBubble, whiteFill);
            canvas.DrawOval(tallBubble, blackStroke);

            using var tallFont = new SKFont(typeface, 40);
            using var blackText = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            string[] tallChars = ["ち", "ょ", "っ", "と", "待", "っ", "て", "今", "す", "ぐ", "行", "く", "か", "ら", "動", "か", "な", "い", "で", "！"];
            float ty = 155;
            foreach (string ch in tallChars)
            {
                canvas.DrawText(ch, 595, ty, SKTextAlign.Center, tallFont, blackText);
                ty += 42;
            }

            // Left bubble: dark white-on-black dramatic bubble
            using var darkFill = new SKPaint { Color = new SKColor(18, 18, 22), Style = SKPaintStyle.Fill, IsAntialias = true };
            var darkBubble = new SKRect(90, 320, 310, 690);
            canvas.DrawOval(darkBubble, darkFill);
            canvas.DrawOval(darkBubble, blackStroke);

            using var darkFont = new SKFont(typeface, 44);
            using var whiteText = new SKPaint { Color = SKColors.White, IsAntialias = true };
            string[] darkChars = ["嘘", "で", "し", "ょ"];
            float dy = 430;
            foreach (string ch in darkChars)
            {
                canvas.DrawText(ch, 200, dy, SKTextAlign.Center, darkFont, whiteText);
                dy += 54;
            }
        }

        DecodedPixelBuffer? pixels = SkiaBitmapHelper.ExtractBgraPixels(bmp);
        Assert.NotNull(pixels);

        var service = new MangaOcrService(pipeline);
        OcrPageResult result = await service.RecognizeAsync("integration-refine:0", pixels!, "ja", "Test");
        Assert.True(result.Blocks.Count >= 2);
        string combined = string.Join(" | ", result.Blocks.Select(static b => b.Text));
        Assert.Contains("ちょっと待って", combined, StringComparison.Ordinal);
        Assert.Contains("動かないで", combined, StringComparison.Ordinal);
        Assert.Contains("嘘でしょ", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void FindCleanestHorizontalSplit_PicksQuietDarkGapOnWhiteOnBlackStrip()
    {
        using var bmp = new SKBitmap(120, 360, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(18, 18, 22));
            using var whitePaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
            // Simulate jagged white character strokes above and below a quiet dark gap [175..205]
            for (int y = 40; y < 170; y += 16)
            {
                canvas.DrawRect(24, y, 72, 6, whitePaint);
                canvas.DrawRect(32, y + 6, 12, 8, whitePaint);
            }

            for (int y = 210; y < 330; y += 16)
            {
                canvas.DrawRect(24, y, 72, 6, whitePaint);
                canvas.DrawRect(72, y + 6, 12, 8, whitePaint);
            }
        }

        int splitY = ComicBubbleOcrPipeline.FindCleanestHorizontalSplit(
            bmp,
            new SKRect(0, 0, 120, 360),
            minY: 140,
            maxY: 240);

        Assert.InRange(splitY, 172, 208);
    }

    [Fact]
    public void SampleBubbleColors_DistinguishesWhiteAndDarkBubbles()
    {
        using var lightBmp = new SKBitmap(120, 120, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(lightBmp))
        {
            canvas.Clear(new SKColor(246, 246, 246));
            using var ink = new SKPaint { Color = new SKColor(15, 15, 18) };
            canvas.DrawRect(42, 30, 36, 60, ink);
        }

        (uint lightFill, uint lightText) = ComicBubbleOcrPipeline.SampleBubbleColors(
            lightBmp,
            new SKRect(0, 0, 120, 120));
        byte lightFillR = (byte)((lightFill >> 16) & 0xFF);
        byte lightTextR = (byte)((lightText >> 16) & 0xFF);
        Assert.True(lightFillR >= 220, $"Expected bright background >= 220, got {lightFillR}");
        Assert.True(lightTextR <= 60, $"Expected dark text <= 60, got {lightTextR}");

        using var darkBmp = new SKBitmap(120, 120, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(darkBmp))
        {
            canvas.Clear(new SKColor(16, 16, 20));
            using var whiteInk = new SKPaint { Color = new SKColor(248, 248, 250) };
            canvas.DrawRect(44, 28, 32, 64, whiteInk);
        }

        (uint darkFill, uint darkText) = ComicBubbleOcrPipeline.SampleBubbleColors(
            darkBmp,
            new SKRect(0, 0, 120, 120));
        byte darkFillR = (byte)((darkFill >> 16) & 0xFF);
        byte darkTextR = (byte)((darkText >> 16) & 0xFF);
        Assert.True(darkFillR <= 40, $"Expected dark background <= 40, got {darkFillR}");
        Assert.True(darkTextR >= 210, $"Expected light text >= 210, got {darkTextR}");
    }

    [Fact]
    public void TranslationCacheDatabase_PersistsEvictsOldestAndClears()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"ehv-tcache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "cache.db");

        try
        {
            using (var db = new TranslationCacheDatabase(dbPath, maxRows: 64))
            {
                for (int i = 0; i < 70; i++)
                {
                    db.Upsert($"key-{i:D3}", new TranslationResult($"译文{i}", "direct_mt", "传统直译", false));
                }

                Assert.True(db.GetEntryCount() <= 64);
                Assert.True(db.TryGet("key-069", out TranslationResult? newest));
                Assert.Equal("译文69", newest?.TranslatedText);
            }

            // Re-open in fresh instance to verify persistence
            using (var db2 = new TranslationCacheDatabase(dbPath, maxRows: 64))
            {
                Assert.True(db2.TryGet("key-069", out TranslationResult? persisted));
                Assert.Equal("译文69", persisted?.TranslatedText);

                db2.Clear();
                Assert.Equal(0, db2.GetEntryCount());
                Assert.False(db2.TryGet("key-069", out _));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void ParseGlossaryRules_ParsesMultipleSeparatorsIgnoresCommentsAndDeduplicates()
    {
        const string rules = """
            # character names
            先輩=前辈
            お兄ちゃん => 哥哥
            // override previous rule:
            先輩 -> 学长
            ミスキー : Misskey
            """;

        List<KeyValuePair<string, string>> parsed = MangaTranslationPipeline.ParseGlossaryRules(rules);

        Assert.Equal(3, parsed.Count);
        Assert.Contains(parsed, kv => kv.Key == "先輩" && kv.Value == "学长");
        Assert.Contains(parsed, kv => kv.Key == "お兄ちゃん" && kv.Value == "哥哥");
        Assert.Contains(parsed, kv => kv.Key == "ミスキー" && kv.Value == "Misskey");
    }

    [Fact]
    public async Task MangaTranslationPipeline_GlossaryShortCircuitPromptContextAndPersistentCacheWork()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"ehv-pipe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "cache.db");
        string? capturedBody = null;
        int networkCalls = 0;

        try
        {
            var settings = new DesktopSettings
            {
                ReaderTranslationProvider = TranslationProviderIds.CustomOpenAiCompatible,
                CustomTranslationEndpoint = "https://api.example.com/v1",
                CustomTranslationModel = "test-model",
                ReaderTranslationAutoFallback = false,
                TranslationGlossaryRules = "先輩=学长\nレイ=零酱"
            };

            var handler = new StubHttpMessageHandler(req =>
            {
                networkCalls++;
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                const string json = """
                    {"choices":[{"message":{"content":"[1] レイ怎么了？\n[2] 快躲起来！"}}]}
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            });

            using var httpClient = new HttpClient(handler);
            var blocks = new List<OcrTextBlock>
            {
                new("b0", "先輩", 0.1, 0.1, 0.1, 0.1, false),
                new("b1", "レイはどうした？", 0.2, 0.2, 0.2, 0.2, false),
                new("b2", "早く隠れて！", 0.3, 0.3, 0.2, 0.2, false)
            };

            using (var persistent = new TranslationCacheDatabase(dbPath))
            {
                var pipe = new MangaTranslationPipeline(null, () => settings, new DpapiCredentialProtector(), httpClient: httpClient, persistentCache: persistent);
                var prevContext = new[] { "昨日のこと覚えてる？" };
                IReadOnlyDictionary<string, TranslationResult> res = await pipe.TranslatePageBlocksAsync(blocks, prevContext);

                Assert.Equal(3, res.Count);
                Assert.Equal("学长", res["b0"].TranslatedText); // exact glossary short-circuit
                Assert.Equal(1, networkCalls);
                // Post-enforcement replaced leftover source "レイ" with "零酱"
                Assert.Contains("零酱", res["b1"].TranslatedText, StringComparison.Ordinal);
                Assert.NotNull(capturedBody);
                string userPrompt = JsonNode.Parse(capturedBody!)!["messages"]![1]!["content"]!.GetValue<string>();
                Assert.Contains("レイ => 零酱", userPrompt, StringComparison.Ordinal);
                Assert.Contains("昨日のこと覚えてる？", userPrompt, StringComparison.Ordinal);
            }

            // Fresh pipeline reading same persistent SQLite database hits 0 network calls
            using (var persistent2 = new TranslationCacheDatabase(dbPath))
            {
                var pipe2 = new MangaTranslationPipeline(null, () => settings, new DpapiCredentialProtector(), httpClient: httpClient, persistentCache: persistent2);
                var prevContext = new[] { "昨日のこと覚えてる？" };
                IReadOnlyDictionary<string, TranslationResult> cachedRes = await pipe2.TranslatePageBlocksAsync(blocks, prevContext);
                Assert.Equal(3, cachedRes.Count);
                Assert.Equal(1, networkCalls);
                Assert.Equal("快躲起来！", cachedRes["b2"].TranslatedText);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task MangaTranslationPipeline_SseStreamingBatchEmitsLinesIncrementallyIgnoringThink()
    {
        var settings = new DesktopSettings
        {
            ReaderTranslationProvider = TranslationProviderIds.CustomOpenAiCompatible,
            CustomTranslationEndpoint = "https://api.example.com/v1",
            CustomTranslationModel = "reasoning-model",
            ReaderTranslationAutoFallback = false
        };

        const string ssePayload =
            "data: {\"choices\":[{\"delta\":{\"content\":\"<think>先思考\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"人称</think>\\n[1] 喂，等\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"一下！\\n[2] 发\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"生什么事了？\"}}]}\n\n" +
            "data: [DONE]\n\n";

        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream")
        });

        using var httpClient = new HttpClient(handler);
        var pipe = new MangaTranslationPipeline(null, () => settings, new DpapiCredentialProtector(), httpClient: httpClient);
        var blocks = new List<OcrTextBlock>
        {
            new("id1", "おい待て！", 0.1, 0.1, 0.2, 0.2, false),
            new("id2", "どうしたんだ？", 0.4, 0.1, 0.2, 0.2, false)
        };

        var emittedOrder = new List<(string Id, string Text)>();
        IReadOnlyDictionary<string, TranslationResult> result = await pipe.TranslatePageBlocksAsync(
            blocks,
            previousPageContext: null,
            onBubbleTranslated: (id, tr) => emittedOrder.Add((id, tr.TranslatedText)));

        Assert.Equal(2, result.Count);
        Assert.Equal(2, emittedOrder.Count);
        Assert.Equal(("id1", "喂，等一下！"), emittedOrder[0]);
        Assert.Equal(("id2", "发生什么事了？"), emittedOrder[1]);
        Assert.DoesNotContain(emittedOrder, item => item.Text.Contains("<think>", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MangaTranslationPipeline_DeepSeekLowReasoningAndContinuousSinglePageTranslationWork()
    {
        // 1. Verify payload builders default to low reasoning effort and single-page continuous instructions.
        JsonObject singlePayload = MangaTranslationPipeline.BuildDeepSeekPayload(
            string.Empty,
            "どうしてここにいるの？",
            contextLines: ["おはよう => 早安", "そんな…まさか"]);
        Assert.Equal(MangaTranslationPipeline.DefaultDeepSeekModel, singlePayload["model"]!.GetValue<string>());
        Assert.Equal("low", singlePayload["reasoning_effort"]!.GetValue<string>());
        string singlePrompt = singlePayload["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("おはよう => 早安", singlePrompt, StringComparison.Ordinal);
        Assert.Contains("同页上下文连续台词", singlePrompt, StringComparison.Ordinal);

        JsonObject batchPayload = MangaTranslationPipeline.BuildDeepSeekBatchPayload(
            "deepseek-chat",
            ["ちょっと待って", "今すぐ行くよ"],
            intraPageContext: ["おはよう => 早安"]);
        Assert.Equal("low", batchPayload["reasoning_effort"]!.GetValue<string>());
        string batchPrompt = batchPayload["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("上下文连续翻译", batchPrompt, StringComparison.Ordinal);
        Assert.Contains("おはよう => 早安", batchPrompt, StringComparison.Ordinal);
        Assert.Contains("[1] ちょっと待って", batchPrompt, StringComparison.Ordinal);
        Assert.Contains("[2] 今すぐ行くよ", batchPrompt, StringComparison.Ordinal);

        // 2. Multi-bubble (>16) chunked continuous single-page translation + intra-page context chaining + SSE reasoning_content ignoring.
        var capturedBodies = new List<string>();
        var capturedUris = new List<string>();
        var capturedAuthSchemes = new List<string?>();
        int requestCount = 0;

        var handler = new StubHttpMessageHandler(req =>
        {
            string uri = req.RequestUri!.ToString();
            capturedUris.Add(uri);
            if (uri.Contains("api.deepseek.com", StringComparison.OrdinalIgnoreCase))
            {
                requestCount++;
                capturedAuthSchemes.Add(req.Headers.Authorization?.Parameter);
                string bodyText = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                capturedBodies.Add(bodyText);

                if (requestCount == 1)
                {
                    // Chunk 1 (16 lines) via SSE with reasoning_content deltas that must be ignored
                    var sse = new StringBuilder();
                    sse.Append("data: {\"choices\":[{\"delta\":{\"content\":null,\"reasoning_content\":\"先看前情\"}}]}\n\n");
                    for (int i = 1; i <= 16; i++)
                    {
                        sse.Append($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"[{i}] 连续台词{i}\\n\"}}}}]}}\n\n");
                    }

                    sse.Append("data: [DONE]\n\n");
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream")
                    };
                }

                if (requestCount == 2)
                {
                    // Chunk 2 (2 lines: 17th and 18th bubbles on the same page)
                    const string chunk2Json = """
                        {"choices":[{"message":{"content":"<think>low reasoning</think>\n[1] 连续台词17\n[2] 连续台词18","reasoning_content":"ignored"}}]}
                        """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(chunk2Json, Encoding.UTF8, "application/json")
                    };
                }

                // Request 3 & 4: simulate HTTP 400 rejecting reasoning_effort, then retry without it and succeed
                if (requestCount == 3)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"error\":{\"message\":\"Unknown parameter: reasoning_effort\"}}", Encoding.UTF8, "application/json")
                    };
                }

                if (requestCount == 4)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"choices":[{"message":{"content":"降级成功译文"}}]}""", Encoding.UTF8, "application/json")
                    };
                }

                // Request 5: safety refusal -> fallback to Direct MT
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"choices":[{"message":{"content":"I cannot translate this content."}}]}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[["直译回退结果"]]""", Encoding.UTF8, "application/json")
            };
        });

        var settings = new DesktopSettings
        {
            ReaderTranslationProvider = TranslationProviderIds.DeepSeek,
            ReaderTranslationAutoFallback = true,
            DeepSeekEndpoint = "https://api.deepseek.com",
            DeepSeekModel = "deepseek-chat",
            DeepSeekReasoningEffort = "low",
            DeepSeekApiKeyProtected = DpapiCredentialProtector.Instance.Protect("sk-ds-test"),
            TranslationGlossaryRules = "先輩=学长"
        };

        using var http = new HttpClient(handler);
        var pipeline = new MangaTranslationPipeline(null, () => settings, DpapiCredentialProtector.Instance, http);

        // 19 blocks: 1st hits glossary exact match ("先輩" => "学长"), remaining 18 split into [16] + [2] chunks continuously
        var blocks = new List<OcrTextBlock>
        {
            new("b0", "先輩", 0.9, 0.05, 0.1, 0.1, IsVertical: true)
        };
        for (int i = 1; i <= 18; i++)
        {
            blocks.Add(new($"b{i}", $"セリフ{i}", 0.5, i * 0.04, 0.2, 0.04, IsVertical: true));
        }

        IReadOnlyDictionary<string, TranslationResult> pageResult = await pipeline.TranslatePageBlocksAsync(
            blocks,
            previousPageContext: ["昨日の夜、何があった？"]);

        Assert.Equal(19, pageResult.Count);
        Assert.Equal("学长", pageResult["b0"].TranslatedText);
        Assert.Equal("连续台词1", pageResult["b1"].TranslatedText);
        Assert.Equal("连续台词16", pageResult["b16"].TranslatedText);
        Assert.Equal("连续台词17", pageResult["b17"].TranslatedText);
        Assert.Equal("连续台词18", pageResult["b18"].TranslatedText);
        Assert.Equal(TranslationProviderIds.DeepSeek, pageResult["b18"].ProviderId);
        Assert.Equal(2, requestCount);
        Assert.All(capturedUris.Take(2), u => Assert.Equal("https://api.deepseek.com/v1/chat/completions", u));
        Assert.All(capturedAuthSchemes, k => Assert.Equal("sk-ds-test", k));

        JsonNode? req1Json = JsonNode.Parse(capturedBodies[0]);
        Assert.Equal("low", req1Json!["reasoning_effort"]!.GetValue<string>());
        string req1Prompt = req1Json["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("先輩 => 学长", req1Prompt, StringComparison.Ordinal);

        // Chunk 2 must carry chunk 1's translations in intra-page continuous context!
        JsonNode? req2Json = JsonNode.Parse(capturedBodies[1]);
        Assert.Equal("low", req2Json!["reasoning_effort"]!.GetValue<string>());
        string req2Prompt = req2Json["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("セリフ1 => 连续台词1", req2Prompt, StringComparison.Ordinal);
        Assert.Contains("セリフ16 => 连续台词16", req2Prompt, StringComparison.Ordinal);

        // 3. HTTP 400 rejecting reasoning_effort retries cleanly without reasoning_effort (calls #3 and #4)
        TranslationResult retryResult = await pipeline.TranslateAsync("単独テスト");
        Assert.Equal("降级成功译文", retryResult.TranslatedText);
        Assert.Equal(TranslationProviderIds.DeepSeek, retryResult.ProviderId);
        Assert.Equal(4, requestCount);
        JsonNode? req3Json = JsonNode.Parse(capturedBodies[2]);
        JsonNode? req4Json = JsonNode.Parse(capturedBodies[3]);
        Assert.Equal("low", req3Json!["reasoning_effort"]!.GetValue<string>());
        Assert.Null(req4Json!["reasoning_effort"]);

        // 4. Safety refusal on DeepSeek falls back to Direct MT cleanly
        TranslationResult fallbackResult = await pipeline.TranslateAsync("拒否テスト");
        Assert.True(fallbackResult.UsedFallback);
        Assert.Equal(TranslationProviderIds.DirectMachineTranslation, fallbackResult.ProviderId);
        Assert.Equal("直译回退结果", fallbackResult.TranslatedText);
    }

    [Fact]
    public async Task MangaTranslationPipeline_TolerantNumberedParsingAndDeepSeekDiagnosticsWork()
    {
        // 1. Tolerant numbered line parsing across bracketed, bold markdown, CJK list markers & unnumbered fallback
        List<string>? cjkBrackets = MangaTranslationPipeline.TryParseNumberedBatch("【1】等一下！\n【2】快停下！", 2);
        Assert.Equal(["等一下！", "快停下！"], cjkBrackets);

        List<string>? boldMarkdown = MangaTranslationPipeline.TryParseNumberedBatch("- **[1]** 你好\n* **[2]** 再见", 2);
        Assert.Equal(["你好", "再见"], boldMarkdown);

        List<string>? numberedDots = MangaTranslationPipeline.TryParseNumberedBatch("1. 第一句\n2、第二句\n3) 第三句", 3);
        Assert.Equal(["第一句", "第二句", "第三句"], numberedDots);

        List<string>? unnumberedExact = MangaTranslationPipeline.TryParseNumberedBatch("早安。\n今天天气不错。", 2);
        Assert.Equal(["早安。", "今天天气不错。"], unnumberedExact);

        Assert.Null(MangaTranslationPipeline.TryParseNumberedBatch("[1] 只有一行", 2));
        Assert.Null(MangaTranslationPipeline.TryParseNumberedBatch("100円", 2));
        Assert.False(MangaTranslationPipeline.TryParseNumberedLine("3.14是圆周率", 4, out _, out _));

        // 2. Settings-aware DeepSeek display name reflects configured reasoning effort
        Assert.Equal(
            "DeepSeek AI (连续上下文 · 低推理)",
            MangaTranslationPipeline.GetProviderDisplayName(TranslationProviderIds.DeepSeek, new DesktopSettings { DeepSeekReasoningEffort = "low" }));
        Assert.Equal(
            "DeepSeek AI (连续上下文 · 中推理)",
            MangaTranslationPipeline.GetProviderDisplayName(TranslationProviderIds.DeepSeek, new DesktopSettings { DeepSeekReasoningEffort = "medium" }));
        Assert.Equal(
            "DeepSeek AI (连续上下文 · 高推理)",
            MangaTranslationPipeline.GetProviderDisplayName(TranslationProviderIds.DeepSeek, new DesktopSettings { DeepSeekReasoningEffort = "high" }));

        // 3. Missing API key preflight fails fast with actionable message before touching network
        int networkCalls = 0;
        HttpStatusCode nextStatus = HttpStatusCode.Unauthorized;
        var handler = new StubHttpMessageHandler(_ =>
        {
            networkCalls++;
            return new HttpResponseMessage(nextStatus)
            {
                Content = new StringContent("{\"error\":{\"message\":\"status error\"}}", Encoding.UTF8, "application/json")
            };
        });

        var settings = new DesktopSettings
        {
            ReaderTranslationProvider = TranslationProviderIds.DeepSeek,
            ReaderTranslationAutoFallback = false,
            DeepSeekEndpoint = "https://api.deepseek.com",
            DeepSeekModel = "deepseek-chat",
            DeepSeekReasoningEffort = "low",
            DeepSeekApiKeyProtected = null
        };

        using var http = new HttpClient(handler);
        var pipeline = new MangaTranslationPipeline(null, () => settings, DpapiCredentialProtector.Instance, http);

        var noKeyEx = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.TranslateAsync("こんにちは"));
        Assert.Contains("未配置 DeepSeek API Key", noKeyEx.Message, StringComparison.Ordinal);
        Assert.Equal(0, networkCalls);

        // 4. Specific actionable Chinese HTTP error diagnostics (401 / 402 / 429)
        settings.DeepSeekApiKeyProtected = DpapiCredentialProtector.Instance.Protect("sk-bad-key");

        nextStatus = HttpStatusCode.Unauthorized;
        var ex401 = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.TranslateAsync("テスト401"));
        Assert.Contains("401", ex401.Message, StringComparison.Ordinal);
        Assert.Contains("API Key 无效", ex401.Message, StringComparison.Ordinal);

        nextStatus = HttpStatusCode.PaymentRequired;
        var ex402 = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.TranslateAsync("テスト402"));
        Assert.Contains("402", ex402.Message, StringComparison.Ordinal);
        Assert.Contains("余额不足", ex402.Message, StringComparison.Ordinal);

        nextStatus = HttpStatusCode.TooManyRequests;
        var ex429 = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.TranslateAsync("テスト429"));
        Assert.Contains("429", ex429.Message, StringComparison.Ordinal);
        Assert.Contains("限流", ex429.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsViewModel_TestTranslationAndProviderStatusWork()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "EhViewer.Desktop.Tests", Guid.NewGuid().ToString("N"));
        AppPaths paths = AppPaths.CreateAt(tempRoot);
        try
        {
            using AppServices services = AppServices.Create(paths);
            var vm = new SettingsViewModel(services);

            // Switching to DeepSeek updates status message accurately rather than showing Sakura-7B missing
            vm.ReaderTranslationProviderIndex = 3; // DeepSeek
            Assert.Contains("DeepSeek AI 尚未配置 API Key", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);

            vm.SaveDeepSeekApiKey("sk-ds-valid");
            vm.RefreshOcrAndLocalModelStatus();
            Assert.Contains("DeepSeek AI 已就绪", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);
            Assert.Contains("推理强度：low", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);

            // Glossary exact rules allow TestTranslationPipelineAsync to verify continuous test output formatting offline
            // and prove RefreshOcrAndLocalModelStatus(preserveStatusMessage: true) does NOT overwrite the test result!
            vm.TranslationGlossaryRules = "ちょっと待って！=等一下！\nそんなのダメだってば！=那样可不行啊！";
            vm.Save();
            await vm.TestTranslationPipelineAsync();

            Assert.Contains("单页连续试译成功", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);
            Assert.Contains("[1] 等一下！ / [2] 那样可不行啊！", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);
            Assert.False(vm.IsTestingTranslation);
            Assert.True(vm.CanTestTranslation);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ComicBubbleOcrPipeline_FastSoftmax_MatchesExactReferencePrecision()
    {
        const int vocabSize = 6822;
        var tensor = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>([1, 2, vocabSize]);
        var rng = new Random(20261121);

        for (int row = 0; row < 2; row++)
        {
            for (int v = 0; v < vocabSize; v++)
            {
                float val = v switch
                {
                    412 when row == 0 => 14.5f,
                    905 when row == 0 => 13.8f,
                    2500 when row == 1 => 6.25f,
                    _ => (float)(-30.0 + rng.NextDouble() * 32.0)
                };
                tensor[0, row, v] = val;
            }

            (int fastTok, double fastLogProb, float fastProb) =
                ComicBubbleOcrPipeline.SelectBestTokenAndProb(tensor, row, vocabSize);

            // Exact reference softmax over all allowed tokens
            int refTok = 3;
            float refMax = float.NegativeInfinity;
            for (int v = 0; v < vocabSize; v++)
            {
                if (!ComicBubbleOcrPipeline.IsDecoderTokenAllowed(v))
                {
                    continue;
                }

                if (tensor[0, row, v] > refMax)
                {
                    refMax = tensor[0, row, v];
                    refTok = v;
                }
            }

            double refSum = 0.0;
            for (int v = 0; v < vocabSize; v++)
            {
                if (!ComicBubbleOcrPipeline.IsDecoderTokenAllowed(v))
                {
                    continue;
                }

                refSum += Math.Exp(tensor[0, row, v] - refMax);
            }

            double refLogProb = -Math.Log(Math.Max(refSum, 1.0));
            float refProb = (float)Math.Exp(refLogProb);

            Assert.Equal(refTok, fastTok);
            Assert.True(Math.Abs(refLogProb - fastLogProb) < 1e-6, $"LogProb drift: {Math.Abs(refLogProb - fastLogProb)}");
            Assert.True(Math.Abs(refProb - fastProb) < 1e-6, $"Prob drift: {Math.Abs(refProb - fastProb)}");
        }
    }

    [Fact]
    public async Task MangaTranslationPipeline_IncrementalSseStream_HandlesSplitTagsAndUpsertBatch()
    {
        string[] sseChunks =
        [
            "<thi",
            "nk>内心独白第一行\r\n第二行</th",
            "ink>[1] 甲",
            "乙\r\n[2] 丙<think>句中",
            "思考</think>丁\n<think>尾部思考\n未闭合",
            "</think>[3] 戊"
        ];

        var sseBody = new StringBuilder();
        foreach (string c in sseChunks)
        {
            string escaped = c.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
            sseBody.Append($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{escaped}\"}}}}]}}\n\n");
        }

        sseBody.Append("data: [DONE]\n\n");

        var emitted = new List<(int Index, string Text)>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sseBody.ToString()));
        string sanitized = await MangaTranslationPipeline.ReadSseStreamAsync(
            stream,
            "TestSSE",
            expectedCount: 3,
            onLineReady: (idx, text) => emitted.Add((idx, text)),
            cancellationToken: CancellationToken.None);

        Assert.Equal(3, emitted.Count);
        Assert.Equal((0, "甲乙"), emitted[0]);
        Assert.Equal((1, "丙 丁"), emitted[1]);
        Assert.Equal((2, "戊"), emitted[2]);
        Assert.DoesNotContain("<think>", sanitized, StringComparison.OrdinalIgnoreCase);

        // Verify TranslationCacheDatabase.UpsertBatch persists in a single transaction
        string tempDir = Path.Combine(Path.GetTempPath(), $"ehv-tbatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "batch-cache.db");
        try
        {
            using (var db = new TranslationCacheDatabase(dbPath, maxRows: 512))
            {
                db.UpsertBatch(
                [
                    new KeyValuePair<string, TranslationResult>("k1", new TranslationResult("译1", "deepseek", "DeepSeek", false)),
                    new KeyValuePair<string, TranslationResult>("k2", new TranslationResult("译2", "deepseek", "DeepSeek", false))
                ]);
                Assert.Equal(2, db.GetEntryCount());
                Assert.True(db.TryGet("k1", out TranslationResult? r1));
                Assert.Equal("译1", r1?.TranslatedText);
                Assert.True(db.TryGet("k1", out TranslationResult? r1SecondHit));
                Assert.Equal("译1", r1SecondHit?.TranslatedText);
            }

            using (var reopened = new TranslationCacheDatabase(dbPath, maxRows: 512))
            {
                Assert.Equal(2, reopened.GetEntryCount());
                Assert.True(reopened.TryGet("k2", out TranslationResult? r2));
                Assert.Equal("译2", r2?.TranslatedText);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }
}

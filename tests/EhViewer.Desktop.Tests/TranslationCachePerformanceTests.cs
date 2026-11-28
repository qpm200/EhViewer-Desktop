using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EhViewer.Core.Security;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Translation;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class TranslationCachePerformanceTests : DesktopTestBase
{
    private sealed class TrackingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public TrackingHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public int DisposedCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _responder(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposedCount++;
            }

            base.Dispose(disposing);
        }
    }

    [Fact]
    public void TranslationCacheDatabase_ConfiguresExpectedPragmas_AndUsesO1EntryCount()
    {
        string tempDir = CreateTempDirectory("tcache-pragma");
        string dbPath = Path.Combine(tempDir, "cache.db");

        using var db = new TranslationCacheDatabase(dbPath, maxRows: 512);
        (string journalMode, long busyTimeout, long tempStore, long cacheSize) = db.ReadConnectionPragmasForTests();

        Assert.Equal("wal", journalMode, ignoreCase: true);
        Assert.Equal(5000L, busyTimeout);
        Assert.Equal(2L, tempStore); // 2 == MEMORY
        Assert.Equal(-8000L, cacheSize);

        // Lazy count initializes once and stays O(1) across subsequent reads and writes.
        Assert.Equal(0, db.ExactCountQueryCount);
        Assert.Equal(0, db.GetEntryCount());
        Assert.Equal(1, db.ExactCountQueryCount);
        Assert.Equal(0, db.GetEntryCount());
        Assert.Equal(0, db.GetEntryCount());
        Assert.Equal(1, db.ExactCountQueryCount);
    }

    [Fact]
    public void TranslationCacheDatabase_ExactRowCount_SurvivesOverwritesMixedBatchesAndClear()
    {
        string tempDir = CreateTempDirectory("tcache-count");
        string dbPath = Path.Combine(tempDir, "cache.db");

        using (var db = new TranslationCacheDatabase(dbPath, maxRows: 512))
        {
            Assert.Equal(0, db.GetEntryCount());
            int baselineQueries = db.ExactCountQueryCount;

            // Duplicate Upsert on same key must overwrite value without incrementing row count.
            db.Upsert("k1", new TranslationResult("初版", "deepseek", "DeepSeek", false));
            db.Upsert("k1", new TranslationResult("修订", "deepseek", "DeepSeek", false));
            Assert.Equal(1, db.GetEntryCount());
            Assert.True(db.TryGet("k1", out TranslationResult? k1));
            Assert.Equal("修订", k1?.TranslatedText);

            // Mixed new + overwrite + intra-batch duplicate keys must keep exact count.
            db.UpsertBatch(
            [
                new KeyValuePair<string, TranslationResult>("k1", new TranslationResult("批量覆盖", "deepseek", "DeepSeek", false)),
                new KeyValuePair<string, TranslationResult>("k2", new TranslationResult("二号", "deepseek", "DeepSeek", false)),
                new KeyValuePair<string, TranslationResult>("k2", new TranslationResult("二号修订", "deepseek", "DeepSeek", false)),
                new KeyValuePair<string, TranslationResult>("k3", new TranslationResult("三号", "deepseek", "DeepSeek", false))
            ]);

            Assert.Equal(3, db.GetEntryCount());
            Assert.Equal(baselineQueries, db.ExactCountQueryCount);

            Assert.True(db.TryGet("k1", out TranslationResult? r1));
            Assert.True(db.TryGet("k2", out TranslationResult? r2));
            Assert.True(db.TryGet("k3", out TranslationResult? r3));
            Assert.Equal("批量覆盖", r1?.TranslatedText);
            Assert.Equal("二号修订", r2?.TranslatedText);
            Assert.Equal("三号", r3?.TranslatedText);

            db.Clear();
            Assert.Equal(0, db.GetEntryCount());
            Assert.False(db.TryGet("k1", out _));
        }

        // Multi-round re-open and dispose verification
        using (var reopened = new TranslationCacheDatabase(dbPath, maxRows: 512))
        {
            Assert.Equal(0, reopened.GetEntryCount());
            reopened.Upsert("k4", new TranslationResult("四号", "direct_mt", "传统直译", false));
            Assert.Equal(1, reopened.GetEntryCount());
        }

        using (var reopened2 = new TranslationCacheDatabase(dbPath, maxRows: 512))
        {
            Assert.Equal(1, reopened2.GetEntryCount());
            Assert.True(reopened2.TryGet("k4", out TranslationResult? r4));
            Assert.Equal("四号", r4?.TranslatedText);
            reopened2.Dispose(); // idempotent dispose
        }
    }

    [Fact]
    public async Task MangaTranslationPipeline_ConcurrentIdenticalRequests_AreDeduplicated()
    {
        var settings = new DesktopSettings
        {
            ReaderTranslationProvider = TranslationProviderIds.CustomOpenAiCompatible,
            CustomTranslationEndpoint = "https://api.example.com/v1",
            CustomTranslationModel = "test-model",
            ReaderTranslationAutoFallback = false
        };

        int handlerCalls = 0;
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handler = new TrackingHttpMessageHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref handlerCalls);
            handlerEntered.TrySetResult();
            await releaseHandler.Task.WaitAsync(ct).ConfigureAwait(false);
            const string json = """{"choices":[{"message":{"content":"你好"}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        using var pipeline = new MangaTranslationPipeline(null, () => settings, DpapiCredentialProtector.Instance, http);

        Task<TranslationResult> first = pipeline.TranslateAsync("こんにちは");
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<TranslationResult> second = pipeline.TranslateAsync("こんにちは");
        Assert.Equal(1, Volatile.Read(ref handlerCalls));

        releaseHandler.TrySetResult();
        TranslationResult[] results = await Task.WhenAll(first, second);

        Assert.Equal(1, Volatile.Read(ref handlerCalls));
        Assert.Equal("你好", results[0].TranslatedText);
        Assert.Equal(results[0].TranslatedText, results[1].TranslatedText);
    }

    [Fact]
    public async Task MangaTranslationPipeline_PartialCancellation_CancelsOnlyCancelledWaiter()
    {
        var settings = new DesktopSettings
        {
            ReaderTranslationProvider = TranslationProviderIds.CustomOpenAiCompatible,
            CustomTranslationEndpoint = "https://api.example.com/v1",
            CustomTranslationModel = "test-model",
            ReaderTranslationAutoFallback = false
        };

        int handlerCalls = 0;
        bool upstreamCanceled = false;
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handler = new TrackingHttpMessageHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref handlerCalls);
            using CancellationTokenRegistration reg = ct.Register(() => Volatile.Write(ref upstreamCanceled, true));
            handlerEntered.TrySetResult();
            await releaseHandler.Task.WaitAsync(ct).ConfigureAwait(false);
            const string json = """{"choices":[{"message":{"content":"继续完成"}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        using var pipeline = new MangaTranslationPipeline(null, () => settings, DpapiCredentialProtector.Instance, http);
        using var ctsA = new CancellationTokenSource();
        using var ctsB = new CancellationTokenSource();

        Task<TranslationResult> taskA = pipeline.TranslateAsync("待取消A", cancellationToken: ctsA.Token);
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Synchronously registers waiter B onto the existing in-flight entry before canceling A.
        Task<TranslationResult> taskB = pipeline.TranslateAsync("待取消A", cancellationToken: ctsB.Token);

        ctsA.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskA);
        Assert.False(Volatile.Read(ref upstreamCanceled));

        releaseHandler.TrySetResult();
        TranslationResult resultB = await taskB;
        Assert.Equal("继续完成", resultB.TranslatedText);
        Assert.Equal(1, Volatile.Read(ref handlerCalls));
    }

    [Fact]
    public async Task MangaTranslationPipeline_AllWaitersCanceled_CancelsUpstreamTokenAndDoesNotCacheFailure()
    {
        var settings = new DesktopSettings
        {
            ReaderTranslationProvider = TranslationProviderIds.CustomOpenAiCompatible,
            CustomTranslationEndpoint = "https://api.example.com/v1",
            CustomTranslationModel = "test-model",
            ReaderTranslationAutoFallback = false
        };

        int callIndex = 0;
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstreamCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handler = new TrackingHttpMessageHandler(async (_, ct) =>
        {
            int idx = Interlocked.Increment(ref callIndex);
            if (idx == 1)
            {
                handlerEntered.TrySetResult();
                try
                {
                    await releaseBlocked.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    upstreamCanceled.TrySetResult();
                    throw;
                }
            }

            const string json = """{"choices":[{"message":{"content":"恢复成功"}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        using var pipeline = new MangaTranslationPipeline(null, () => settings, DpapiCredentialProtector.Instance, http);
        using var ctsA = new CancellationTokenSource();
        using var ctsB = new CancellationTokenSource();

        Task<TranslationResult> taskA = pipeline.TranslateAsync("全员取消", cancellationToken: ctsA.Token);
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<TranslationResult> taskB = pipeline.TranslateAsync("全员取消", cancellationToken: ctsB.Token);

        ctsA.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskA);
        Assert.False(upstreamCanceled.Task.IsCompleted);

        ctsB.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskB);
        await upstreamCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Subsequent request must not reuse cancelled/failed state and must succeed cleanly.
        TranslationResult recovered = await pipeline.TranslateAsync("全员取消");
        Assert.Equal("恢复成功", recovered.TranslatedText);
        Assert.Equal(2, Volatile.Read(ref callIndex));
    }

    [Fact]
    public async Task MangaTranslationPipeline_FallbackPerBubble_PersistsViaBatchAndHitsCacheOnReopen()
    {
        string tempDir = CreateTempDirectory("tcache-fallback");
        string dbPath = Path.Combine(tempDir, "fallback.db");

        var settings = new DesktopSettings
        {
            ReaderTranslationProvider = TranslationProviderIds.CustomOpenAiCompatible,
            CustomTranslationEndpoint = "https://api.example.com/v1",
            CustomTranslationModel = "test-model",
            ReaderTranslationAutoFallback = false
        };

        int networkCalls = 0;
        using var handler = new TrackingHttpMessageHandler(async (req, ct) =>
        {
            Interlocked.Increment(ref networkCalls);
            string body = await req.Content!.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonNode? payload = JsonNode.Parse(body);
            bool isStream = payload?["stream"]?.GetValue<bool>() == true;
            if (isStream)
            {
                // Return non-numbered output in SSE so batch chunk cleanly falls back to per-bubble pipeline.
                const string badBatch = "data: {\"choices\":[{\"delta\":{\"content\":\"无法编号输出\"}}]}\n\ndata: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(badBatch, Encoding.UTF8, "text/event-stream")
                };
            }

            string userPrompt = payload?["messages"]?[1]?["content"]?.GetValue<string>() ?? string.Empty;
            string targetLine = userPrompt
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault() ?? string.Empty;
            string reply = targetLine switch
            {
                "台词一" => "译文一",
                "台词二" => "译文二",
                _ => throw new InvalidOperationException($"Unexpected target line: {targetLine}")
            };
            string okJson = $$$"""{"choices":[{"message":{"content":"{{{reply}}}"}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(okJson, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var blocks = new List<OcrTextBlock>
        {
            new("b1", "台词一", 0.1, 0.1, 0.2, 0.2, false),
            new("b2", "台词二", 0.4, 0.1, 0.2, 0.2, false)
        };

        using (var db = new TranslationCacheDatabase(dbPath, maxRows: 512))
        using (var pipeline = new MangaTranslationPipeline(null, () => settings, DpapiCredentialProtector.Instance, http, persistentCache: db))
        {
            IReadOnlyDictionary<string, TranslationResult> res = await pipeline.TranslatePageBlocksAsync(blocks);
            Assert.Equal(2, res.Count);
            Assert.Equal("译文一", res["b1"].TranslatedText);
            Assert.Equal("译文二", res["b2"].TranslatedText);
            Assert.Equal(2, db.GetEntryCount());
        }

        // Re-open pipeline & DB: all entries persisted via batch without extra network calls.
        int callsBeforeReopen = Volatile.Read(ref networkCalls);
        using (var db2 = new TranslationCacheDatabase(dbPath, maxRows: 512))
        using (var pipeline2 = new MangaTranslationPipeline(null, () => settings, DpapiCredentialProtector.Instance, http, persistentCache: db2))
        {
            Assert.Equal(2, db2.GetEntryCount());
            TranslationResult hit1 = await pipeline2.TranslateAsync("台词一", ["台词二"]);
            TranslationResult hit2 = await pipeline2.TranslateAsync("台词二", ["台词一"]);
            Assert.Equal("译文一", hit1.TranslatedText);
            Assert.Equal("译文二", hit2.TranslatedText);
            Assert.Equal(callsBeforeReopen, Volatile.Read(ref networkCalls));
        }
    }

    [Fact]
    public void MangaTranslationPipeline_Dispose_DoesNotDisposeExternalDependenciesAndIsIdempotent()
    {
        string tempDir = CreateTempDirectory("tcache-own");
        string dbPath = Path.Combine(tempDir, "owned.db");

        using var handler = new TrackingHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var externalHttp = new HttpClient(handler);
        using var externalDb = new TranslationCacheDatabase(dbPath, maxRows: 256);

        var pipeline = new MangaTranslationPipeline(
            null,
            static () => new DesktopSettings(),
            DpapiCredentialProtector.Instance,
            httpClient: externalHttp,
            persistentCache: externalDb);

        pipeline.Dispose();
        pipeline.Dispose(); // idempotent

        Assert.Equal(0, handler.DisposedCount);
        externalDb.Upsert("alive", new TranslationResult("仍可用", "direct_mt", "传统直译", false));
        Assert.Equal(1, externalDb.GetEntryCount());

        using var ownedPipeline = new MangaTranslationPipeline(
            null,
            static () => new DesktopSettings(),
            DpapiCredentialProtector.Instance);
        ownedPipeline.Dispose();
        ownedPipeline.Dispose();
    }

    [Fact]
    public void LlamaCppServerManager_DisposeAndResolveExecutableCache_BehaveCorrectly()
    {
        AppPaths paths = CreateTempAppPaths("llama-cache", ensureCreated: false);

        using var handler = new TrackingHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var externalHttp = new HttpClient(handler);

        using var mgr = new LlamaCppServerManager(paths, externalHttp);
        var settings = new DesktopSettings();

        // Negative cache hit avoids repeated directory enumeration within TTL.
        Assert.Null(mgr.ResolveServerExecutablePath(settings));
        Assert.Equal(1, mgr.ServerPathScanCount);
        Assert.Null(mgr.ResolveServerExecutablePath(settings));
        Assert.Equal(1, mgr.ServerPathScanCount);

        // Simulate TTL expiry and create executable -> re-scans and hits positive cache.
        Directory.CreateDirectory(paths.DefaultLlamaCppDirectory);
        string exePath = Path.Combine(paths.DefaultLlamaCppDirectory, "llama-server.exe");
        File.WriteAllBytes(exePath, []);

        mgr.ExpireNegativeServerPathCacheForTests();
        Assert.Equal(exePath, mgr.ResolveServerExecutablePath(settings));
        Assert.Equal(2, mgr.ServerPathScanCount);
        Assert.Equal(exePath, mgr.ResolveServerExecutablePath(settings));
        Assert.Equal(2, mgr.ServerPathScanCount);

        // Deleting cached executable invalidates positive cache automatically.
        File.Delete(exePath);
        Assert.Null(mgr.ResolveServerExecutablePath(settings));
        Assert.Equal(3, mgr.ServerPathScanCount);

        // Changing configured LocalLlamaServerPath invalidates cache immediately.
        string customExe = Path.Combine(paths.RootDirectory, "custom-llama-server.exe");
        File.WriteAllBytes(customExe, []);
        settings.LocalLlamaServerPath = customExe;
        Assert.Equal(customExe, mgr.ResolveServerExecutablePath(settings));
        Assert.Equal(4, mgr.ServerPathScanCount);

        mgr.Dispose();
        mgr.Dispose(); // idempotent
        Assert.Equal(0, handler.DisposedCount);

        long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
        Assert.False(LlamaCppServerManager.IsWithinHealthCacheTtl(0, nowTs, TimeSpan.FromSeconds(5)));
        Assert.True(LlamaCppServerManager.IsWithinHealthCacheTtl(nowTs, nowTs + System.Diagnostics.Stopwatch.Frequency, TimeSpan.FromSeconds(5)));
        Assert.False(LlamaCppServerManager.IsWithinHealthCacheTtl(nowTs, nowTs + (System.Diagnostics.Stopwatch.Frequency * 6), TimeSpan.FromSeconds(5)));
    }
}

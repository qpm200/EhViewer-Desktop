using System.Collections.Concurrent;
using System.Net.Http.Headers;
using EhViewer.Core.Security;
using Microsoft.Extensions.Logging;

namespace EhViewer.Desktop.Services.Translation;

/// <summary>
/// Anti-refusal 3-tier manga translation orchestrator:
/// Tier 1 Direct MT (stateless machine translation) / Tier 2 Local Sakura-7B (llama.cpp) /
/// Tier 3 Custom OpenAI-compatible API, with automatic refusal/filter detection and fallback.
/// </summary>
public sealed partial class MangaTranslationPipeline : IDisposable
{
    public const string DefaultDeepSeekEndpoint = "https://api.deepseek.com";
    public const string DefaultDeepSeekModel = "deepseek-chat";
    public const string DefaultDeepSeekReasoningEffort = "low";

    private const int MaxCacheEntries = 256;
    private const int BatchChunkSize = 16;

    private static readonly ProductInfoHeaderValue DefaultUserAgent = new("Mozilla", "5.0");

    private const string DeepSeekSystemPrompt =
        "你是一个专业的日本漫画与轻小说汉化翻译专家。请结合同一页漫画的分镜阅读顺序（从右到左、从上到下）与前后气泡语境进行上下文连续翻译，保持人物称呼、人称代词、语气词、口癖和情绪连贯自然。只输出对应的简体中文译文，绝不输出任何思考过程、解释或额外注音。";

    private readonly LlamaCppServerManager? _llamaManager;
    private readonly Func<DesktopSettings> _settingsAccessor;
    private readonly ICredentialProtector _protector;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<MangaTranslationPipeline>? _logger;
    private readonly TranslationCacheDatabase? _persistentCache;

    private readonly object _cacheGate = new();
    private readonly Dictionary<string, TranslationResult> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly ConcurrentDictionary<string, InFlightEntry> _inFlight = new(StringComparer.Ordinal);
    private int _disposed;

    public MangaTranslationPipeline(
        LlamaCppServerManager? llamaManager,
        Func<DesktopSettings> settingsAccessor,
        ICredentialProtector? protector = null,
        HttpClient? httpClient = null,
        ILogger<MangaTranslationPipeline>? logger = null,
        TranslationCacheDatabase? persistentCache = null)
    {
        _llamaManager = llamaManager;
        _settingsAccessor = settingsAccessor ?? throw new ArgumentNullException(nameof(settingsAccessor));
        _protector = protector ?? DpapiCredentialProtector.Instance;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        _logger = logger;
        _persistentCache = persistentCache;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
        // _persistentCache lifecycle is managed externally (DI container / caller).
    }

    public int PersistentCacheCount => _persistentCache?.GetEntryCount() ?? 0;

    public static string GetProviderDisplayName(string providerId) =>
        GetProviderDisplayName(providerId, settings: null);

    public static string GetProviderDisplayName(string providerId, DesktopSettings? settings)
    {
        if (string.Equals(providerId, TranslationProviderIds.DeepSeek, StringComparison.OrdinalIgnoreCase))
        {
            string effortLabel = NormalizeReasoningEffort(settings?.DeepSeekReasoningEffort) switch
            {
                "medium" => "中推理",
                "high" => "高推理",
                _ => "低推理"
            };
            return $"DeepSeek AI (连续上下文 · {effortLabel})";
        }

        return providerId switch
        {
            TranslationProviderIds.LocalSakuraLlamaCpp => "本地 Sakura-7B (llama.cpp)",
            TranslationProviderIds.CustomOpenAiCompatible => "自定义 OpenAI 接口",
            _ => "传统直译 (免配置)"
        };
    }

    public static string NormalizeReasoningEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return DefaultDeepSeekReasoningEffort;
        }

        return effort.Trim().ToLowerInvariant() switch
        {
            "medium" => "medium",
            "high" => "high",
            _ => "low"
        };
    }

    public Task<TranslationResult> TranslateAsync(
        string sourceText,
        string? providerOverride = null,
        bool bypassCache = false,
        CancellationToken cancellationToken = default) =>
        TranslateAsync(sourceText, contextLines: null, providerOverride, bypassCache, cancellationToken);

    public async Task<TranslationResult> TranslateAsync(
        string sourceText,
        IReadOnlyList<string>? contextLines,
        string? providerOverride = null,
        bool bypassCache = false,
        CancellationToken cancellationToken = default)
    {
        (TranslationResult result, _) = await TranslateInternalAsync(
            sourceText,
            contextLines,
            providerOverride,
            bypassCache,
            persistImmediately: true,
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<(TranslationResult Result, string? PendingPersistKey)> TranslateInternalAsync(
        string sourceText,
        IReadOnlyList<string>? contextLines,
        string? providerOverride,
        bool bypassCache,
        bool persistImmediately,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return (new TranslationResult(
                string.Empty,
                TranslationProviderIds.DirectMachineTranslation,
                GetProviderDisplayName(TranslationProviderIds.DirectMachineTranslation),
                UsedFallback: false), null);
        }

        string trimmedSource = sourceText.Trim();
        DesktopSettings settings = _settingsAccessor();
        string primary = NormalizeProviderId(
            string.IsNullOrWhiteSpace(providerOverride) ? settings.ReaderTranslationProvider : providerOverride);

        IReadOnlyList<KeyValuePair<string, string>> allGlossary = GetCachedGlossaryRules(settings.TranslationGlossaryRules);
        foreach (KeyValuePair<string, string> exact in allGlossary)
        {
            if (string.Equals(exact.Key, trimmedSource, StringComparison.Ordinal))
            {
                return (new TranslationResult(
                    exact.Value,
                    primary,
                    $"{GetProviderDisplayName(primary, settings)} · 术语表",
                    UsedFallback: false), null);
            }
        }

        List<KeyValuePair<string, string>> matchedGlossary = MatchGlossaryEntries(allGlossary, [trimmedSource]);
        string contextSig = primary != TranslationProviderIds.DirectMachineTranslation && contextLines is { Count: > 0 }
            ? string.Join("\u001F", contextLines.Where(static s => !string.IsNullOrWhiteSpace(s)).Take(16))
            : string.Empty;
        string glossarySig = ComputeGlossarySignature(matchedGlossary);
        string cacheKey = BuildCacheKey(
            BuildProviderCacheKeyId(primary, settings),
            settings.ReaderTranslationAutoFallback,
            contextSig,
            prevContextSig: string.Empty,
            glossarySig,
            trimmedSource);

        if (!bypassCache && TryGetCached(cacheKey, out TranslationResult? cached))
        {
            return (cached!, null);
        }

        if (bypassCache)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TranslationResult directResult = await ExecuteProviderChainAsync(
                trimmedSource,
                contextLines,
                matchedGlossary,
                settings,
                primary,
                cancellationToken).ConfigureAwait(false);

            if (persistImmediately)
            {
                StoreCache(cacheKey, directResult);
                return (directResult, null);
            }

            StoreInMemoryOnly(cacheKey, directResult);
            return (directResult, _persistentCache is not null ? cacheKey : null);
        }

        InFlightEntry entry = AcquireInFlightEntry(
            cacheKey,
            (inFlight, ct) => ExecuteInFlightTranslationAsync(
                cacheKey,
                inFlight,
                trimmedSource,
                contextLines,
                matchedGlossary,
                settings,
                primary,
                persistImmediately,
                ct),
            cancellationToken);

        TranslationResult result;
        try
        {
            result = await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.ReleaseRef();
        }

        if (persistImmediately)
        {
            if (!entry.IsPersisted && _persistentCache is not null)
            {
                _persistentCache.Upsert(cacheKey, result);
                entry.MarkPersisted();
            }

            return (result, null);
        }

        return (result, !entry.IsPersisted && _persistentCache is not null ? cacheKey : null);
    }

    private InFlightEntry AcquireInFlightEntry(
        string cacheKey,
        Func<InFlightEntry, CancellationToken, Task<TranslationResult>> workFactory,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_inFlight.TryGetValue(cacheKey, out InFlightEntry? existing))
            {
                if (existing.TryAddRef())
                {
                    return existing;
                }

                _inFlight.TryRemove(new KeyValuePair<string, InFlightEntry>(cacheKey, existing));
                continue;
            }

            InFlightEntry? candidate = null;
            candidate = new InFlightEntry(ct => ExecuteAndCleanupAsync(cacheKey, candidate!, workFactory, ct));
            if (_inFlight.TryAdd(cacheKey, candidate))
            {
                return candidate;
            }

            candidate.DisposeUnused();
        }
    }

    private async Task<TranslationResult> ExecuteAndCleanupAsync(
        string cacheKey,
        InFlightEntry entry,
        Func<InFlightEntry, CancellationToken, Task<TranslationResult>> workFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await workFactory(entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            entry.MarkCompleted();
            _inFlight.TryRemove(new KeyValuePair<string, InFlightEntry>(cacheKey, entry));
        }
    }

    private async Task<TranslationResult> ExecuteInFlightTranslationAsync(
        string cacheKey,
        InFlightEntry entry,
        string trimmedSource,
        IReadOnlyList<string>? contextLines,
        List<KeyValuePair<string, string>> matchedGlossary,
        DesktopSettings settings,
        string primary,
        bool persistImmediately,
        CancellationToken cancellationToken)
    {
        TranslationResult result = await ExecuteProviderChainAsync(
            trimmedSource,
            contextLines,
            matchedGlossary,
            settings,
            primary,
            cancellationToken).ConfigureAwait(false);

        if (persistImmediately)
        {
            StoreCache(cacheKey, result);
            entry.MarkPersisted();
        }
        else
        {
            StoreInMemoryOnly(cacheKey, result);
        }

        return result;
    }

    private async Task<TranslationResult> ExecuteProviderChainAsync(
        string trimmedSource,
        IReadOnlyList<string>? contextLines,
        List<KeyValuePair<string, string>> matchedGlossary,
        DesktopSettings settings,
        string primary,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> chain = BuildProviderChain(settings, primary);
        List<string> failureReasons = [];

        for (int i = 0; i < chain.Count; i++)
        {
            string providerId = chain[i];
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string translated = providerId switch
                {
                    TranslationProviderIds.DeepSeek =>
                        await TranslateWithDeepSeekAsync(trimmedSource, contextLines, matchedGlossary, settings, cancellationToken).ConfigureAwait(false),
                    TranslationProviderIds.LocalSakuraLlamaCpp =>
                        await TranslateLocalSakuraAsync(trimmedSource, contextLines, matchedGlossary, settings, cancellationToken).ConfigureAwait(false),
                    TranslationProviderIds.CustomOpenAiCompatible =>
                        await TranslateCustomOpenAiAsync(trimmedSource, contextLines, matchedGlossary, settings, cancellationToken).ConfigureAwait(false),
                    _ => await TranslateDirectMtAsync(trimmedSource, cancellationToken).ConfigureAwait(false)
                };

                translated = ApplyPostGlossaryEnforcement(translated, matchedGlossary);
                bool usedFallback = i > 0;
                string? fallbackNote = usedFallback && failureReasons.Count > 0
                    ? $"{GetProviderDisplayName(chain[0], settings)} 未成功（{failureReasons[0]}），已自动回退到{GetProviderDisplayName(providerId, settings)}"
                    : null;

                return new TranslationResult(
                    translated,
                    providerId,
                    GetProviderDisplayName(providerId, settings),
                    usedFallback,
                    fallbackNote);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                string reason = ex.Message;
                failureReasons.Add(reason);
                _logger?.LogWarning(ex, "Translation provider {ProviderId} failed", providerId);

                if (!settings.ReaderTranslationAutoFallback || i == chain.Count - 1)
                {
                    throw new InvalidOperationException(
                        string.Join("；", failureReasons),
                        ex);
                }
            }
        }

        throw new InvalidOperationException("所有翻译引擎均不可用。");
    }

    private sealed class InFlightEntry
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Lazy<Task<TranslationResult>> _lazyTask;
        private int _refCount = 1;
        private bool _completed;
        private bool _persisted;

        public InFlightEntry(Func<CancellationToken, Task<TranslationResult>> taskFactory)
        {
            _lazyTask = new Lazy<Task<TranslationResult>>(
                () => taskFactory(_cts.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Task<TranslationResult> Task => _lazyTask.Value;
        public bool IsPersisted => Volatile.Read(ref _persisted);

        public void MarkPersisted() => Volatile.Write(ref _persisted, true);

        public bool TryAddRef()
        {
            lock (_gate)
            {
                if (_completed || _cts.IsCancellationRequested || _refCount <= 0)
                {
                    return false;
                }

                _refCount++;
                return true;
            }
        }

        public void ReleaseRef()
        {
            bool shouldCancel = false;
            bool shouldDispose = false;
            lock (_gate)
            {
                if (_refCount > 0)
                {
                    _refCount--;
                    if (_refCount == 0)
                    {
                        if (_completed)
                        {
                            shouldDispose = true;
                        }
                        else
                        {
                            shouldCancel = true;
                        }
                    }
                }
            }

            if (shouldCancel)
            {
                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (shouldDispose)
            {
                _cts.Dispose();
            }
        }

        public void MarkCompleted()
        {
            bool shouldDispose;
            lock (_gate)
            {
                _completed = true;
                shouldDispose = _refCount == 0;
            }

            if (shouldDispose)
            {
                _cts.Dispose();
            }
        }

        public void DisposeUnused() => _cts.Dispose();
    }

    public Task<IReadOnlyDictionary<string, TranslationResult>> TranslatePageBlocksAsync(
        IReadOnlyList<OcrTextBlock> blocks,
        string? providerOverride = null,
        bool bypassCache = false,
        CancellationToken cancellationToken = default) =>
        TranslatePageBlocksAsync(
            blocks,
            previousPageContext: null,
            onBubbleTranslated: null,
            providerOverride,
            bypassCache,
            cancellationToken);

    public async Task<IReadOnlyDictionary<string, TranslationResult>> TranslatePageBlocksAsync(
        IReadOnlyList<OcrTextBlock> blocks,
        IReadOnlyList<string>? previousPageContext,
        Action<string, TranslationResult>? onBubbleTranslated = null,
        string? providerOverride = null,
        bool bypassCache = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count == 0)
        {
            return new Dictionary<string, TranslationResult>(StringComparer.Ordinal);
        }

        List<OcrTextBlock> valid = blocks
            .Where(static b => !string.IsNullOrWhiteSpace(b.Text))
            .ToList();
        var results = new Dictionary<string, TranslationResult>(valid.Count, StringComparer.Ordinal);
        if (valid.Count == 0)
        {
            return results;
        }

        DesktopSettings settings = _settingsAccessor();
        string primary = NormalizeProviderId(
            string.IsNullOrWhiteSpace(providerOverride) ? settings.ReaderTranslationProvider : providerOverride);
        string[] contextLines = valid.Select(static b => b.Text.Trim()).ToArray();
        IReadOnlyList<KeyValuePair<string, string>> allGlossary = GetCachedGlossaryRules(settings.TranslationGlossaryRules);
        List<KeyValuePair<string, string>> pageGlossary = MatchGlossaryEntries(allGlossary, contextLines);
        string glossarySig = ComputeGlossarySignature(pageGlossary);
        string providerCacheKeyId = BuildProviderCacheKeyId(primary, settings);
        string contextSig = primary != TranslationProviderIds.DirectMachineTranslation
            ? string.Join("\u001F", contextLines.Take(16))
            : string.Empty;
        string prevContextSig = primary != TranslationProviderIds.DirectMachineTranslation && previousPageContext is { Count: > 0 }
            ? string.Join("\u001F", previousPageContext.Where(static s => !string.IsNullOrWhiteSpace(s)).Select(static s => s.Trim()).Take(4))
            : string.Empty;

        var resolvedBySource = new Dictionary<string, string>(StringComparer.Ordinal);
        var remaining = new List<OcrTextBlock>(valid.Count);
        foreach (OcrTextBlock b in valid)
        {
            string trimmed = b.Text.Trim();
            TranslationResult? directGlossary = null;
            foreach (KeyValuePair<string, string> exact in allGlossary)
            {
                if (string.Equals(exact.Key, trimmed, StringComparison.Ordinal))
                {
                    directGlossary = new TranslationResult(
                        exact.Value,
                        primary,
                        $"{GetProviderDisplayName(primary, settings)} · 术语表",
                        UsedFallback: false);
                    break;
                }
            }

            if (directGlossary is not null)
            {
                results[b.Id] = directGlossary;
                resolvedBySource[trimmed] = directGlossary.TranslatedText;
                onBubbleTranslated?.Invoke(b.Id, directGlossary);
                continue;
            }

            if (!bypassCache)
            {
                string key = BuildCacheKey(
                    providerCacheKeyId,
                    settings.ReaderTranslationAutoFallback,
                    contextSig,
                    prevContextSig,
                    glossarySig,
                    trimmed);
                if (TryGetCached(key, out TranslationResult? cached) && cached is not null)
                {
                    results[b.Id] = cached;
                    resolvedBySource[trimmed] = cached.TranslatedText;
                    onBubbleTranslated?.Invoke(b.Id, cached);
                    continue;
                }
            }

            remaining.Add(b);
        }

        if (remaining.Count == 0)
        {
            return results;
        }

        // For LLM engines (DeepSeek / Sakura-7B / Custom OpenAI) on multi-bubble pages, translate remaining bubbles
        // in sequential reading-order numbered batches so single-page dialogue context remains continuous even across >16 bubbles or pre-cached segments.
        if (primary != TranslationProviderIds.DirectMachineTranslation && remaining.Count >= 2)
        {
            string display = GetProviderDisplayName(primary, settings);
            for (int offset = 0; offset < remaining.Count; offset += BatchChunkSize)
            {
                int chunkCount = Math.Min(BatchChunkSize, remaining.Count - offset);
                if (chunkCount < 2)
                {
                    break;
                }

                List<OcrTextBlock> chunk = remaining.GetRange(offset, chunkCount);
                List<string> intraPageContext = BuildIntraPageContext(valid, chunk, resolvedBySource);
                try
                {
                    string[] chunkLines = chunk.Select(static b => b.Text.Trim()).ToArray();
                    List<KeyValuePair<string, string>> batchGlossary = MatchGlossaryEntries(allGlossary, chunkLines);
                    var emitted = new bool[chunk.Count];

                    var pendingPersist = _persistentCache is not null
                        ? new List<KeyValuePair<string, TranslationResult>>(chunkCount)
                        : null;

                    void EmitBatchIndex(int idx, string rawLine)
                    {
                        if ((uint)idx >= (uint)chunk.Count || emitted[idx])
                        {
                            return;
                        }

                        string enforced = ApplyPostGlossaryEnforcement(rawLine, batchGlossary);
                        if (string.IsNullOrWhiteSpace(enforced) || RefusalDetector.IsRefusalResponse(enforced, out _))
                        {
                            return;
                        }

                        emitted[idx] = true;
                        OcrTextBlock targetBlock = chunk[idx];
                        string srcTrimmed = targetBlock.Text.Trim();
                        var tr = new TranslationResult(enforced, primary, display, UsedFallback: false);
                        results[targetBlock.Id] = tr;
                        resolvedBySource[srcTrimmed] = enforced;
                        string key = BuildCacheKey(
                            providerCacheKeyId,
                            settings.ReaderTranslationAutoFallback,
                            contextSig,
                            prevContextSig,
                            glossarySig,
                            srcTrimmed);
                        StoreInMemoryOnly(key, tr);
                        pendingPersist?.Add(new KeyValuePair<string, TranslationResult>(key, tr));
                        onBubbleTranslated?.Invoke(targetBlock.Id, tr);
                    }

                    try
                    {
                        List<string>? batchTranslated = await TryTranslateBatchWithLlmAsync(
                            primary,
                            chunkLines,
                            previousPageContext,
                            batchGlossary,
                            intraPageContext,
                            settings,
                            EmitBatchIndex,
                            cancellationToken).ConfigureAwait(false);
                        if (batchTranslated is not null && batchTranslated.Count == chunk.Count)
                        {
                            for (int i = 0; i < chunk.Count; i++)
                            {
                                EmitBatchIndex(i, batchTranslated[i]);
                            }
                        }
                    }
                    finally
                    {
                        if (pendingPersist is { Count: > 0 })
                        {
                            _persistentCache?.UpsertBatch(pendingPersist);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Batch page translation chunk fell back to per-bubble pipeline");
                }
            }

            if (results.Count == valid.Count)
            {
                return results;
            }

            remaining = remaining.Where(b => !results.ContainsKey(b.Id)).ToList();
        }

        using var throttle = new SemaphoreSlim(3, 3);
        object? pendingGate = _persistentCache is not null ? new object() : null;
        Dictionary<string, TranslationResult>? fallbackPendingPersist = _persistentCache is not null
            ? new Dictionary<string, TranslationResult>(remaining.Count, StringComparer.Ordinal)
            : null;

        Task<(string Id, string SourceText, TranslationResult? Result)>[] tasks = remaining.Select(async block =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IReadOnlyList<string>? singleContext = primary == TranslationProviderIds.DirectMachineTranslation
                    ? contextLines
                    : BuildSingleBubbleContext(valid, block, resolvedBySource, previousPageContext);
                (TranslationResult r, string? pendingKey) = await TranslateInternalAsync(
                    block.Text,
                    singleContext,
                    providerOverride,
                    bypassCache,
                    persistImmediately: false,
                    cancellationToken).ConfigureAwait(false);

                if (pendingKey is not null && fallbackPendingPersist is not null && pendingGate is not null)
                {
                    lock (pendingGate)
                    {
                        fallbackPendingPersist[pendingKey] = r;
                    }
                }

                return (block.Id, block.Text.Trim(), (TranslationResult?)r);
            }
            finally
            {
                throttle.Release();
            }
        }).ToArray();

        (string Id, string SourceText, TranslationResult? Result)[] settled;
        try
        {
            settled = await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            if (fallbackPendingPersist is { Count: > 0 } && _persistentCache is not null)
            {
                _persistentCache.UpsertBatch(fallbackPendingPersist.ToArray());
            }
        }

        foreach ((string id, string srcText, TranslationResult? res) in settled)
        {
            if (res is not null)
            {
                results[id] = res;
                resolvedBySource[srcText] = res.TranslatedText;
                onBubbleTranslated?.Invoke(id, res);
            }
        }

        return results;
    }

    public void ClearCache()
    {
        lock (_cacheGate)
        {
            _cache.Clear();
            _lru.Clear();
        }

        _persistentCache?.Clear();
    }

    private static string BuildCacheKey(
        string primary,
        bool autoFallback,
        string contextSig,
        string prevContextSig,
        string glossarySig,
        string trimmedSource) =>
        $"{primary}|{autoFallback}|{contextSig}|{prevContextSig}|{glossarySig}|{trimmedSource}";

    private bool TryGetCached(string key, out TranslationResult? result)
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

        if (_persistentCache is not null && _persistentCache.TryGet(key, out result) && result is not null)
        {
            StoreInMemoryOnly(key, result);
            return true;
        }

        result = null;
        return false;
    }

    private void StoreCache(string key, TranslationResult result)
    {
        StoreInMemoryOnly(key, result);
        _persistentCache?.Upsert(key, result);
    }

    private void StoreInMemoryOnly(string key, TranslationResult result)
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

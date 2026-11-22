using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EhViewer.Core.Security;
using Microsoft.Extensions.Logging;

namespace EhViewer.Desktop.Services.Translation;

/// <summary>
/// Anti-refusal 3-tier manga translation orchestrator:
/// Tier 1 Direct MT (stateless machine translation) / Tier 2 Local Sakura-7B (llama.cpp) /
/// Tier 3 Custom OpenAI-compatible API, with automatic refusal/filter detection and fallback.
/// </summary>
public sealed class MangaTranslationPipeline : IDisposable
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

    internal static IReadOnlyList<string> BuildProviderChain(DesktopSettings settings, string primary)
    {
        var chain = new List<string> { primary };
        if (!settings.ReaderTranslationAutoFallback)
        {
            return chain;
        }

        if ((primary == TranslationProviderIds.DeepSeek ||
             primary == TranslationProviderIds.CustomOpenAiCompatible) &&
            settings.EnableLocalSakuraModel)
        {
            chain.Add(TranslationProviderIds.LocalSakuraLlamaCpp);
        }

        if (primary != TranslationProviderIds.DirectMachineTranslation)
        {
            chain.Add(TranslationProviderIds.DirectMachineTranslation);
        }

        return chain;
    }

    private async Task<string> TranslateDirectMtAsync(string sourceText, CancellationToken cancellationToken)
    {
        List<string> errors = [];

        // 1. Primary stateless MT: Chrome extension endpoint (rarely rate-limited)
        string clients5Url =
            $"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl=zh-CN&q={Uri.EscapeDataString(sourceText)}";
        try
        {
            string? r1 = await TryGoogleArrayTranslateAsync(clients5Url, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(r1))
            {
                return r1;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        // 2. Secondary stateless MT: googleapis translate_a/single
        string googleApisUrl =
            $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=zh-CN&dt=t&q={Uri.EscapeDataString(sourceText)}";
        try
        {
            string? r2 = await TryGoogleArrayTranslateAsync(googleApisUrl, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(r2))
            {
                return r2;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        // 3. Tertiary stateless MT fallback: MyMemory free REST API
        string srcLang = DetectSimpleSourceLanguage(sourceText);
        string myMemoryUrl =
            $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(sourceText)}&langpair={srcLang}|zh-CN";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, myMemoryUrl);
            req.Headers.UserAgent.Add(DefaultUserAgent);
            using HttpResponseMessage resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"MyMemory HTTP {(int)resp.StatusCode}");
            }

            string payload = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("responseData", out JsonElement data) &&
                data.TryGetProperty("translatedText", out JsonElement translatedElem))
            {
                string? text = translatedElem.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        throw new InvalidOperationException(errors.Count > 0 ? errors[0] : "传统直译暂不可用");
    }

    private async Task<string?> TryGoogleArrayTranslateAsync(string url, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.Add(DefaultUserAgent);

        using HttpResponseMessage resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"传统直译返回 HTTP {(int)resp.StatusCode}");
        }

        string payload = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(payload);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        return ParseGoogleArrayTranslation(doc.RootElement);
    }

    internal static string? ParseGoogleArrayTranslation(JsonElement rootArray)
    {
        if (rootArray.ValueKind != JsonValueKind.Array || rootArray.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement first = rootArray[0];
        if (first.ValueKind == JsonValueKind.String)
        {
            return first.GetString()?.Trim();
        }

        if (first.ValueKind != JsonValueKind.Array || first.GetArrayLength() == 0)
        {
            return null;
        }

        // clients5 format: [["sentence1", "en"], ["sentence2", "en"]]
        if (first[0].ValueKind == JsonValueKind.String)
        {
            var sb = new StringBuilder();
            foreach (JsonElement pair in rootArray.EnumerateArray())
            {
                if (pair.ValueKind == JsonValueKind.Array &&
                    pair.GetArrayLength() > 0 &&
                    pair[0].ValueKind == JsonValueKind.String)
                {
                    sb.Append(pair[0].GetString());
                }
            }

            string text = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        // translate_a/single format: [[["sentence1", "src1", ...], ["sentence2", "src2", ...]], null, "en"]
        var combined = new StringBuilder();
        foreach (JsonElement segment in first.EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array &&
                segment.GetArrayLength() > 0 &&
                segment[0].ValueKind == JsonValueKind.String)
            {
                combined.Append(segment[0].GetString());
            }
        }

        string result = combined.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string DetectSimpleSourceLanguage(string text)
    {
        foreach (char c in text)
        {
            if ((c >= '\u3040' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF'))
            {
                return "ja";
            }
            if ((c >= '\uAC00' && c <= '\uD7AF') || (c >= '\u1100' && c <= '\u11FF'))
            {
                return "ko";
            }
        }

        foreach (char c in text)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
            {
                return "en";
            }
        }

        return "ja";
    }

    private async Task<string> TranslateLocalSakuraAsync(
        string sourceText,
        IReadOnlyList<string>? contextLines,
        IReadOnlyList<KeyValuePair<string, string>>? matchedGlossary,
        DesktopSettings settings,
        CancellationToken cancellationToken)
    {
        if (_llamaManager is null)
        {
            throw new InvalidOperationException("未注册本地 llama.cpp 服务管理器");
        }

        Uri baseUri = await _llamaManager.EnsureRunningAsync(settings, cancellationToken).ConfigureAwait(false);
        Uri endpoint = new(baseUri, "/v1/chat/completions");

        JsonObject payload = LlamaCppServerManager.BuildSakuraChatPayload(sourceText, contextLines, matchedGlossary);
        return await SendChatCompletionAsync(
            endpoint.ToString(),
            apiKey: null,
            payload,
            "本地 Sakura-7B",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> TranslateCustomOpenAiAsync(
        string sourceText,
        IReadOnlyList<string>? contextLines,
        IReadOnlyList<KeyValuePair<string, string>>? matchedGlossary,
        DesktopSettings settings,
        CancellationToken cancellationToken)
    {
        string rawEndpoint = settings.CustomTranslationEndpoint?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rawEndpoint))
        {
            throw new InvalidOperationException("未配置自定义 OpenAI 接口地址");
        }

        string completionsUrl = BuildOpenAiChatCompletionsUrl(rawEndpoint);
        string modelName = string.IsNullOrWhiteSpace(settings.CustomTranslationModel)
            ? "gpt-4o-mini"
            : settings.CustomTranslationModel.Trim();

        string? apiKey = UnprotectApiKey(settings.CustomTranslationApiKeyProtected);

        var userContent = new StringBuilder();
        AppendCustomOpenAiPromptHeader(userContent, previousPageContext: null, matchedGlossary);
        if (contextLines is { Count: > 0 })
        {
            List<string> usable = contextLines
                .Where(static l => !string.IsNullOrWhiteSpace(l))
                .Select(static l => l.Trim())
                .Where(l => !string.Equals(l, sourceText.Trim(), StringComparison.Ordinal))
                .Take(6)
                .ToList();

            if (usable.Count > 0)
            {
                userContent.AppendLine("同页参考上下文（勿重复翻译）：");
                foreach (string ctx in usable)
                {
                    userContent.Append("- ").AppendLine(ctx);
                }

                userContent.AppendLine();
                userContent.Append("请仅翻译以下目标台词：\n");
            }
        }

        userContent.Append(sourceText);

        JsonObject payload = BuildCustomOpenAiPayload(modelName, userContent.ToString(), maxTokens: 512, stream: false);

        return await SendChatCompletionAsync(
            completionsUrl,
            apiKey,
            payload,
            "自定义 OpenAI 接口",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> TranslateWithDeepSeekAsync(
        string sourceText,
        IReadOnlyList<string>? contextLines,
        IReadOnlyList<KeyValuePair<string, string>>? matchedGlossary,
        DesktopSettings settings,
        CancellationToken cancellationToken)
    {
        string rawEndpoint = string.IsNullOrWhiteSpace(settings.DeepSeekEndpoint)
            ? DefaultDeepSeekEndpoint
            : settings.DeepSeekEndpoint.Trim();
        string completionsUrl = BuildOpenAiChatCompletionsUrl(rawEndpoint);
        string modelName = string.IsNullOrWhiteSpace(settings.DeepSeekModel)
            ? DefaultDeepSeekModel
            : settings.DeepSeekModel.Trim();
        string reasoningEffort = NormalizeReasoningEffort(settings.DeepSeekReasoningEffort);
        string? apiKey = UnprotectApiKey(settings.DeepSeekApiKeyProtected);
        ValidateDeepSeekApiKey(completionsUrl, apiKey);

        using HttpResponseMessage resp = await SendWithReasoningEffortFallbackAsync(
            completionsUrl,
            apiKey,
            includeReasoningEffort => BuildDeepSeekPayload(
                modelName,
                sourceText,
                reasoningEffort,
                contextLines,
                matchedGlossary,
                stream: false,
                includeReasoningEffort: includeReasoningEffort),
            HttpCompletionOption.ResponseContentRead,
            "DeepSeek AI",
            cancellationToken).ConfigureAwait(false);

        string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfChatHttpFailure(resp.StatusCode, body, "DeepSeek AI");
        return ExtractChatCompletionContent(body, "DeepSeek AI");
    }

    public static JsonObject BuildDeepSeekPayload(
        string model,
        string sourceText,
        string? reasoningEffort = DefaultDeepSeekReasoningEffort,
        IReadOnlyList<string>? contextLines = null,
        IReadOnlyList<KeyValuePair<string, string>>? glossaryRules = null,
        bool stream = false,
        bool includeReasoningEffort = true)
    {
        string resolvedModel = string.IsNullOrWhiteSpace(model) ? DefaultDeepSeekModel : model.Trim();
        string resolvedEffort = NormalizeReasoningEffort(reasoningEffort);
        string trimmedTarget = sourceText.Trim();

        var userContent = new StringBuilder();
        AppendDeepSeekGlossaryAndPrevContext(userContent, previousPageContext: null, glossaryRules, intraPageContext: null);

        if (contextLines is { Count: > 0 })
        {
            var usable = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string rawLine in contextLines)
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                string line = rawLine.ReplaceLineEndings(" ").Trim();
                if (string.Equals(line, trimmedTarget, StringComparison.Ordinal) || !seen.Add(line))
                {
                    continue;
                }

                usable.Add(line);
                if (usable.Count >= 16)
                {
                    break;
                }
            }

            if (usable.Count > 0)
            {
                userContent.AppendLine("同页上下文连续台词（按漫画阅读顺序排列，仅供语境参考，无需重复输出）：");
                foreach (string ctx in usable)
                {
                    userContent.Append("- ").AppendLine(ctx);
                }

                userContent.AppendLine();
            }
        }

        userContent.Append("请结合以上同页连续语境，仅将下面这一句当前气泡台词翻译成简体中文（只输出译文）：\n");
        userContent.Append(trimmedTarget);

        var payload = new JsonObject
        {
            ["model"] = resolvedModel
        };
        if (includeReasoningEffort)
        {
            payload["reasoning_effort"] = resolvedEffort;
        }

        payload["temperature"] = 0.2;
        payload["top_p"] = 0.85;
        payload["max_tokens"] = 360;
        payload["stream"] = stream;
        payload["messages"] = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = DeepSeekSystemPrompt
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = userContent.ToString()
            }
        };
        return payload;
    }

    public static JsonObject BuildDeepSeekBatchPayload(
        string model,
        IReadOnlyList<string> lines,
        string? reasoningEffort = DefaultDeepSeekReasoningEffort,
        IReadOnlyList<string>? previousPageContext = null,
        IReadOnlyList<KeyValuePair<string, string>>? glossaryRules = null,
        IReadOnlyList<string>? intraPageContext = null,
        bool stream = false,
        bool includeReasoningEffort = true)
    {
        ArgumentNullException.ThrowIfNull(lines);

        string resolvedModel = string.IsNullOrWhiteSpace(model) ? DefaultDeepSeekModel : model.Trim();
        string resolvedEffort = NormalizeReasoningEffort(reasoningEffort);

        var userContent = new StringBuilder();
        AppendDeepSeekGlossaryAndPrevContext(userContent, previousPageContext, glossaryRules, intraPageContext);
        userContent.AppendLine($"请对以下同一页漫画按阅读顺序排列的日文/外文气泡台词进行上下文连续翻译，确保前后句人称、语气与呼应关系连贯自然。严格按 [1]..[{lines.Count}] 编号逐行输出对应的简体中文译文（共 {lines.Count} 行，保持编号，不要合并或漏行，不要输出解释）：");
        for (int i = 0; i < lines.Count; i++)
        {
            userContent.Append('[').Append(i + 1).Append("] ").AppendLine(lines[i].ReplaceLineEndings(" ").Trim());
        }

        var payload = new JsonObject
        {
            ["model"] = resolvedModel
        };
        if (includeReasoningEffort)
        {
            payload["reasoning_effort"] = resolvedEffort;
        }

        payload["temperature"] = 0.2;
        payload["top_p"] = 0.85;
        payload["max_tokens"] = Math.Clamp(lines.Count * 150, 400, 2400);
        payload["stream"] = stream;
        payload["messages"] = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = DeepSeekSystemPrompt
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = userContent.ToString().TrimEnd()
            }
        };
        return payload;
    }

    private static void AppendDeepSeekGlossaryAndPrevContext(
        StringBuilder sb,
        IReadOnlyList<string>? previousPageContext,
        IReadOnlyList<KeyValuePair<string, string>>? glossaryRules,
        IReadOnlyList<string>? intraPageContext)
    {
        if (glossaryRules is { Count: > 0 })
        {
            sb.AppendLine("参考术语表（遇到对应日文时请优先采用指定译名）：");
            foreach (KeyValuePair<string, string> kv in glossaryRules.Take(12))
            {
                sb.Append("- ").Append(kv.Key).Append(" => ").AppendLine(kv.Value);
            }

            sb.AppendLine();
        }

        if (previousPageContext is { Count: > 0 })
        {
            List<string> prev = previousPageContext
                .Where(static l => !string.IsNullOrWhiteSpace(l))
                .Select(static l => l.ReplaceLineEndings(" ").Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(4)
                .ToList();
            if (prev.Count > 0)
            {
                sb.AppendLine("上一页末尾台词参考（仅供衔接语境，无需输出）：");
                foreach (string line in prev)
                {
                    sb.Append("- ").AppendLine(line);
                }

                sb.AppendLine();
            }
        }

        if (intraPageContext is { Count: > 0 })
        {
            List<string> intra = intraPageContext
                .Where(static l => !string.IsNullOrWhiteSpace(l))
                .Select(static l => l.ReplaceLineEndings(" ").Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(16)
                .ToList();
            if (intra.Count > 0)
            {
                sb.AppendLine("本页前序/同页关联台词参考（仅供保持单页上下文连贯，无需重复输出）：");
                foreach (string line in intra)
                {
                    sb.Append("- ").AppendLine(line);
                }

                sb.AppendLine();
            }
        }
    }

    private async Task<List<string>?> TryTranslateBatchWithLlmAsync(
        string providerId,
        IReadOnlyList<string> sourceLines,
        IReadOnlyList<string>? previousPageContext,
        IReadOnlyList<KeyValuePair<string, string>>? matchedGlossary,
        IReadOnlyList<string>? intraPageContext,
        DesktopSettings settings,
        Action<int, string>? onLineReady,
        CancellationToken cancellationToken)
    {
        string rawResponse;
        if (providerId == TranslationProviderIds.DeepSeek)
        {
            string rawEndpoint = string.IsNullOrWhiteSpace(settings.DeepSeekEndpoint)
                ? DefaultDeepSeekEndpoint
                : settings.DeepSeekEndpoint.Trim();
            string completionsUrl = BuildOpenAiChatCompletionsUrl(rawEndpoint);
            string modelName = string.IsNullOrWhiteSpace(settings.DeepSeekModel)
                ? DefaultDeepSeekModel
                : settings.DeepSeekModel.Trim();
            string reasoningEffort = NormalizeReasoningEffort(settings.DeepSeekReasoningEffort);
            string? apiKey = UnprotectApiKey(settings.DeepSeekApiKeyProtected);
            ValidateDeepSeekApiKey(completionsUrl, apiKey);

            using HttpResponseMessage resp = await SendWithReasoningEffortFallbackAsync(
                completionsUrl,
                apiKey,
                includeReasoningEffort => BuildDeepSeekBatchPayload(
                    modelName,
                    sourceLines,
                    reasoningEffort,
                    previousPageContext,
                    matchedGlossary,
                    intraPageContext,
                    stream: true,
                    includeReasoningEffort: includeReasoningEffort),
                HttpCompletionOption.ResponseHeadersRead,
                "DeepSeek AI",
                cancellationToken).ConfigureAwait(false);

            rawResponse = await ReadBatchResponseAsync(
                resp,
                "DeepSeek AI",
                sourceLines.Count,
                onLineReady,
                cancellationToken).ConfigureAwait(false);
        }
        else if (providerId == TranslationProviderIds.LocalSakuraLlamaCpp)
        {
            if (_llamaManager is null)
            {
                return null;
            }

            Uri baseUri = await _llamaManager.EnsureRunningAsync(settings, cancellationToken).ConfigureAwait(false);
            Uri endpoint = new(baseUri, "/v1/chat/completions");
            JsonObject payload = LlamaCppServerManager.BuildSakuraBatchPayload(
                sourceLines,
                previousPageContext,
                matchedGlossary,
                stream: true);
            rawResponse = await SendBatchChatCompletionAsync(
                endpoint.ToString(),
                apiKey: null,
                payload,
                "本地 Sakura-7B",
                sourceLines.Count,
                onLineReady,
                cancellationToken).ConfigureAwait(false);
        }
        else if (providerId == TranslationProviderIds.CustomOpenAiCompatible)
        {
            string rawEndpoint = settings.CustomTranslationEndpoint?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawEndpoint))
            {
                return null;
            }

            string completionsUrl = BuildOpenAiChatCompletionsUrl(rawEndpoint);
            string modelName = string.IsNullOrWhiteSpace(settings.CustomTranslationModel)
                ? "gpt-4o-mini"
                : settings.CustomTranslationModel.Trim();
            string? apiKey = UnprotectApiKey(settings.CustomTranslationApiKeyProtected);

            var sb = new StringBuilder();
            AppendCustomOpenAiPromptHeader(sb, previousPageContext, matchedGlossary, intraPageContext);
            sb.AppendLine("请按编号逐行将以下同页漫画气泡直接翻译成简体中文，保持 [1] [2]... 编号一一对应，不要合并或添加解释：");
            for (int i = 0; i < sourceLines.Count; i++)
            {
                sb.Append('[').Append(i + 1).Append("] ").AppendLine(sourceLines[i].ReplaceLineEndings(" ").Trim());
            }

            JsonObject payload = BuildCustomOpenAiPayload(
                modelName,
                sb.ToString().TrimEnd(),
                maxTokens: Math.Clamp(sourceLines.Count * 160, 512, 1800),
                stream: true);
            rawResponse = await SendBatchChatCompletionAsync(
                completionsUrl,
                apiKey,
                payload,
                "自定义 OpenAI 接口",
                sourceLines.Count,
                onLineReady,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return null;
        }

        List<string>? parsed = TryParseNumberedBatch(rawResponse, sourceLines.Count);
        if (parsed is null)
        {
            return null;
        }

        foreach (string item in parsed)
        {
            if (RefusalDetector.IsRefusalResponse(item, out _))
            {
                return null;
            }
        }

        return parsed;
    }

    private static void AppendCustomOpenAiPromptHeader(
        StringBuilder sb,
        IReadOnlyList<string>? previousPageContext,
        IReadOnlyList<KeyValuePair<string, string>>? glossaryEntries,
        IReadOnlyList<string>? intraPageContext = null)
    {
        if (previousPageContext is { Count: > 0 })
        {
            List<string> prev = previousPageContext
                .Where(static l => !string.IsNullOrWhiteSpace(l))
                .Select(static l => l.ReplaceLineEndings(" ").Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(4)
                .ToList();
            if (prev.Count > 0)
            {
                sb.AppendLine("上一页末尾对白（仅作人称与语气参考，勿输出）：");
                foreach (string line in prev)
                {
                    sb.Append("- ").AppendLine(line);
                }

                sb.AppendLine();
            }
        }

        if (intraPageContext is { Count: > 0 })
        {
            List<string> intra = intraPageContext
                .Where(static l => !string.IsNullOrWhiteSpace(l))
                .Select(static l => l.ReplaceLineEndings(" ").Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(16)
                .ToList();
            if (intra.Count > 0)
            {
                sb.AppendLine("本页前序/同页关联台词参考（仅作上下文衔接，勿重复输出）：");
                foreach (string line in intra)
                {
                    sb.Append("- ").AppendLine(line);
                }

                sb.AppendLine();
            }
        }

        if (glossaryEntries is { Count: > 0 })
        {
            sb.AppendLine("术语表（专有名词须严格采用以下中文译名）：");
            foreach (KeyValuePair<string, string> kv in glossaryEntries.Take(12))
            {
                sb.Append("- ").Append(kv.Key).Append(" => ").AppendLine(kv.Value);
            }

            sb.AppendLine();
        }
    }

    private static JsonObject BuildCustomOpenAiPayload(string modelName, string userContent, int maxTokens, bool stream = false) => new()
    {
        ["model"] = modelName,
        ["temperature"] = 0.2,
        ["max_tokens"] = maxTokens,
        ["stream"] = stream,
        ["messages"] = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = "你是专业漫画本地化译者。请严格忠实、自然地将用户输入的外文漫画对白或拟声词直接翻译为简体中文，只输出译文，不要添加解释、警告或免责声明。"
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = userContent
            }
        }
    };

    internal static List<string>? TryParseNumberedBatch(string rawOutput, int expectedCount)
    {
        if (string.IsNullOrWhiteSpace(rawOutput) || expectedCount <= 0)
        {
            return null;
        }

        string clean = SanitizeModelOutput(rawOutput);
        string[] lines = clean
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var indexed = new Dictionary<int, string>();
        bool sawAnyOutOfRangeMarker = false;
        foreach (string line in lines)
        {
            if (TryParseNumberedLine(line, expectedCount, out int number, out string text))
            {
                indexed[number] = text;
            }
            else if (LooksLikeBracketedOutOfRangeMarker(line, expectedCount))
            {
                sawAnyOutOfRangeMarker = true;
            }
        }

        if (indexed.Count == expectedCount)
        {
            var ordered = new List<string>(expectedCount);
            for (int i = 1; i <= expectedCount; i++)
            {
                ordered.Add(indexed[i]);
            }

            return ordered;
        }

        if (indexed.Count == 0 && !sawAnyOutOfRangeMarker && lines.Length == expectedCount)
        {
            return [.. lines];
        }

        return null;
    }

    internal static bool TryParseNumberedLine(string line, int expectedCount, out int number, out string text)
    {
        number = 0;
        text = string.Empty;
        if (string.IsNullOrWhiteSpace(line) || expectedCount <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> span = line.AsSpan().Trim();

        // Strip common bullet or markdown emphasis wrappers before the index marker
        while (span.Length > 0 && (span[0] is '-' or '*' or '•' or '·'))
        {
            span = span[1..].TrimStart();
        }

        bool boldWrapped = span.StartsWith("**", StringComparison.Ordinal);
        if (boldWrapped)
        {
            span = span[2..].TrimStart();
        }

        int closeIdx = -1;
        int startDigits = 0;
        bool isDotSeparator = false;
        if (span.Length >= 4 && span[0] == '[')
        {
            startDigits = 1;
            closeIdx = span.IndexOf(']');
        }
        else if (span.Length >= 4 && span[0] == '【')
        {
            startDigits = 1;
            closeIdx = span.IndexOf('】');
        }
        else
        {
            int idx = 0;
            while (idx < span.Length && char.IsAsciiDigit(span[idx]))
            {
                idx++;
            }

            if (idx > 0 && idx < span.Length && span[idx] is '.' or ')' or '）' or '、')
            {
                closeIdx = idx;
                isDotSeparator = span[idx] == '.';
            }
        }

        if (closeIdx <= startDigits ||
            !int.TryParse(span[startDigits..closeIdx], out number) ||
            number < 1 ||
            number > expectedCount)
        {
            return false;
        }

        ReadOnlySpan<char> tail = span[(closeIdx + 1)..].TrimStart();
        if (tail.StartsWith("**", StringComparison.Ordinal))
        {
            tail = tail[2..].TrimStart();
        }

        if (tail.IsEmpty)
        {
            return false;
        }

        // Guard against decimals like "3.14" being mistaken for numbered line 3
        if (isDotSeparator && char.IsAsciiDigit(tail[0]))
        {
            return false;
        }

        text = tail.Trim().ToString();
        return !string.IsNullOrWhiteSpace(text);
    }

    private static bool LooksLikeBracketedOutOfRangeMarker(string line, int expectedCount)
    {
        ReadOnlySpan<char> span = line.AsSpan().Trim();
        if (span.Length >= 4 && span[0] is '[' or '【')
        {
            char closeChar = span[0] == '[' ? ']' : '】';
            int closeIdx = span.IndexOf(closeChar);
            if (closeIdx > 1 && int.TryParse(span[1..closeIdx], out int number))
            {
                return number < 1 || number > expectedCount;
            }
        }

        return false;
    }

    private static void ValidateDeepSeekApiKey(string completionsUrl, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        if (Uri.TryCreate(completionsUrl, UriKind.Absolute, out Uri? uri) &&
            uri.Host.EndsWith("deepseek.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("未配置 DeepSeek API Key，请前往「设置 → 阅读体验」填写并加密保存密钥");
        }
    }

    internal static string SanitizeModelOutput(string? raw) =>
        StripThinkBlocks(raw, trimResult: true);

    private static string StripThinkBlocks(string? raw, bool trimResult)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string text = trimResult ? raw.Trim() : raw;
        // Strip <think>...</think> chain-of-thought tags if an OpenAI-compatible reasoning model emitted them
        while (true)
        {
            int start = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                break;
            }

            int end = text.IndexOf("</think>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                text = text[..start];
                if (trimResult)
                {
                    text = text.Trim();
                }

                break;
            }

            text = text[..start] + " " + text[(end + "</think>".Length)..];
            if (trimResult)
            {
                text = text.Trim();
            }
        }

        return trimResult ? text.Trim() : text;
    }

    private async Task<string> SendChatCompletionAsync(
        string endpointUrl,
        string? apiKey,
        JsonObject payload,
        string engineLabel,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage resp = await SendChatRequestAsync(
            endpointUrl,
            apiKey,
            payload,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfChatHttpFailure(resp, body, engineLabel);
        return ExtractChatCompletionContent(body, engineLabel);
    }

    private async Task<HttpResponseMessage> SendWithReasoningEffortFallbackAsync(
        string endpointUrl,
        string? apiKey,
        Func<bool, JsonObject> payloadFactory,
        HttpCompletionOption completionOption,
        string engineLabel,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage resp = await SendChatRequestAsync(
            endpointUrl,
            apiKey,
            payloadFactory(true),
            completionOption,
            cancellationToken).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.BadRequest)
        {
            string errBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (ShouldRetryWithoutReasoningEffort(resp.StatusCode, errBody))
            {
                resp.Dispose();
                return await SendChatRequestAsync(
                    endpointUrl,
                    apiKey,
                    payloadFactory(false),
                    completionOption,
                    cancellationToken).ConfigureAwait(false);
            }

            HttpStatusCode status = resp.StatusCode;
            resp.Dispose();
            ThrowIfChatHttpFailure(status, errBody, engineLabel);
        }

        return resp;
    }

    private static bool ShouldRetryWithoutReasoningEffort(HttpStatusCode statusCode, string? body) =>
        statusCode == HttpStatusCode.BadRequest &&
        !string.IsNullOrWhiteSpace(body) &&
        !ContainsSafetyFilterKeyword(body) &&
        (body.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase) ||
         body.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
         body.Contains("unknown", StringComparison.OrdinalIgnoreCase));

    private async Task<string> SendBatchChatCompletionAsync(
        string endpointUrl,
        string? apiKey,
        JsonObject payload,
        string engineLabel,
        int expectedCount,
        Action<int, string>? onLineReady,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage resp = await SendChatRequestAsync(
            endpointUrl,
            apiKey,
            payload,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        return await ReadBatchResponseAsync(
            resp,
            engineLabel,
            expectedCount,
            onLineReady,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadBatchResponseAsync(
        HttpResponseMessage resp,
        string engineLabel,
        int expectedCount,
        Action<int, string>? onLineReady,
        CancellationToken cancellationToken)
    {
        if (!resp.IsSuccessStatusCode)
        {
            string errBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfChatHttpFailure(resp.StatusCode, errBody, engineLabel);
        }

        string? mediaType = resp.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await using Stream stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await ReadSseStreamAsync(
                stream,
                engineLabel,
                expectedCount,
                onLineReady,
                cancellationToken).ConfigureAwait(false);
        }

        string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string content = ExtractChatCompletionContent(body, engineLabel);
        if (onLineReady is not null && expectedCount > 0)
        {
            var emitted = new bool[expectedCount];
            TryEmitCompletedStreamLines(content, expectedCount, emitted, onLineReady, includeLastUnterminated: true);
        }

        return content;
    }

    private async Task<HttpResponseMessage> SendChatRequestAsync(
        string endpointUrl,
        string? apiKey,
        JsonObject payload,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpointUrl)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return await _http.SendAsync(req, completionOption, cancellationToken).ConfigureAwait(false);
    }

    private static void ThrowIfChatHttpFailure(HttpResponseMessage resp, string body, string engineLabel)
    {
        if (resp.IsSuccessStatusCode)
        {
            return;
        }

        ThrowIfChatHttpFailure(resp.StatusCode, body, engineLabel);
    }

    private static void ThrowIfChatHttpFailure(HttpStatusCode statusCode, string body, string engineLabel)
    {
        if ((int)statusCode >= 200 && (int)statusCode < 300)
        {
            return;
        }

        if (statusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException($"{engineLabel} API Key 无效或未授权 (HTTP 401)，请检查密钥配置");
        }

        if (statusCode == HttpStatusCode.PaymentRequired)
        {
            throw new InvalidOperationException($"{engineLabel} 账户余额不足 (HTTP 402)，请充值后重试");
        }

        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException($"{engineLabel} 请求过于频繁或触发限流 (HTTP 429)");
        }

        if (statusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or (HttpStatusCode)451 ||
            ContainsSafetyFilterKeyword(body))
        {
            throw new InvalidOperationException($"{engineLabel} 内容过滤或安全拦截 (HTTP {(int)statusCode})");
        }

        if ((int)statusCode >= 500)
        {
            throw new InvalidOperationException($"{engineLabel} 服务端暂时不可用 (HTTP {(int)statusCode})");
        }

        throw new InvalidOperationException($"{engineLabel} 请求失败 (HTTP {(int)statusCode})");
    }

    private static string ExtractChatCompletionContent(string body, string engineLabel)
    {
        using JsonDocument doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out JsonElement errorElem))
        {
            string errorText = errorElem.ToString();
            if (ContainsSafetyFilterKeyword(errorText))
            {
                throw new InvalidOperationException($"{engineLabel} 返回安全策略错误");
            }

            throw new InvalidOperationException($"{engineLabel} 接口错误: {errorText}");
        }

        if (!doc.RootElement.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"{engineLabel} 未返回译文候选");
        }

        JsonElement firstChoice = choices[0];
        if (firstChoice.TryGetProperty("finish_reason", out JsonElement finishReason) &&
            string.Equals(finishReason.GetString(), "content_filter", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{engineLabel} 触发 content_filter 截断");
        }

        string? content = null;
        if (firstChoice.TryGetProperty("message", out JsonElement message) &&
            message.TryGetProperty("content", out JsonElement contentElem) &&
            contentElem.ValueKind == JsonValueKind.String)
        {
            content = contentElem.GetString();
        }
        else if (firstChoice.TryGetProperty("text", out JsonElement textElem) &&
                 textElem.ValueKind == JsonValueKind.String)
        {
            content = textElem.GetString();
        }

        content = SanitizeModelOutput(content);
        if (RefusalDetector.IsRefusalResponse(content, out string refusalReason))
        {
            throw new InvalidOperationException($"{engineLabel} {refusalReason}");
        }

        return content;
    }

    internal static async Task<string> ReadSseStreamAsync(
        Stream stream,
        string engineLabel,
        int expectedCount,
        Action<int, string>? onLineReady,
        CancellationToken cancellationToken)
    {
        var accumulated = new StringBuilder();
        var emitted = expectedCount > 0 ? new bool[expectedCount] : [];
        int rawCursor = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string data = line[5..].Trim();
            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            using JsonDocument doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("error", out JsonElement errorElem))
            {
                string errorText = errorElem.ToString();
                if (ContainsSafetyFilterKeyword(errorText))
                {
                    throw new InvalidOperationException($"{engineLabel} 返回安全策略错误");
                }

                throw new InvalidOperationException($"{engineLabel} 接口错误: {errorText}");
            }

            if (!doc.RootElement.TryGetProperty("choices", out JsonElement choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                continue;
            }

            JsonElement firstChoice = choices[0];
            if (firstChoice.TryGetProperty("finish_reason", out JsonElement finishReason) &&
                finishReason.ValueKind == JsonValueKind.String &&
                string.Equals(finishReason.GetString(), "content_filter", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{engineLabel} 触发 content_filter 截断");
            }

            string? chunk = null;
            if (firstChoice.TryGetProperty("delta", out JsonElement delta) &&
                delta.TryGetProperty("content", out JsonElement deltaContent) &&
                deltaContent.ValueKind == JsonValueKind.String)
            {
                chunk = deltaContent.GetString();
            }
            else if (firstChoice.TryGetProperty("text", out JsonElement textElem) &&
                     textElem.ValueKind == JsonValueKind.String)
            {
                chunk = textElem.GetString();
            }
            else if (firstChoice.TryGetProperty("message", out JsonElement message) &&
                     message.TryGetProperty("content", out JsonElement msgContent) &&
                     msgContent.ValueKind == JsonValueKind.String)
            {
                chunk = msgContent.GetString();
            }

            if (string.IsNullOrEmpty(chunk))
            {
                continue;
            }

            accumulated.Append(chunk);
            if (onLineReady is not null &&
                expectedCount > 0 &&
                chunk.IndexOfAny(['\n', '\r', '>']) >= 0)
            {
                string tail = accumulated.ToString(rawCursor, accumulated.Length - rawCursor);
                TryEmitCompletedStreamLines(
                    tail,
                    expectedCount,
                    emitted,
                    onLineReady,
                    includeLastUnterminated: false);
                rawCursor += ComputeSafeStreamAdvance(tail);
            }
        }

        string rawFinal = accumulated.ToString();
        if (onLineReady is not null && expectedCount > 0)
        {
            string finalTail = rawCursor == 0 ? rawFinal : rawFinal[rawCursor..];
            TryEmitCompletedStreamLines(
                finalTail,
                expectedCount,
                emitted,
                onLineReady,
                includeLastUnterminated: true);
        }

        string sanitized = SanitizeModelOutput(rawFinal);
        if (RefusalDetector.IsRefusalResponse(sanitized, out string refusalReason))
        {
            throw new InvalidOperationException($"{engineLabel} {refusalReason}");
        }

        return sanitized;
    }

    private static int ComputeSafeStreamAdvance(string tail)
    {
        int safeLimit = tail.Length;
        int pos = 0;
        while (pos < tail.Length)
        {
            int open = tail.IndexOf("<think>", pos, StringComparison.OrdinalIgnoreCase);
            if (open < 0)
            {
                // Avoid slicing mid-tag if tail ends with a partial '<think' prefix.
                int lastLt = tail.LastIndexOf('<');
                if (lastLt >= pos && tail.IndexOf('>', lastLt) < 0 && tail.Length - lastLt <= 8)
                {
                    safeLimit = lastLt;
                }
                break;
            }

            int close = tail.IndexOf("</think>", open + 7, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                safeLimit = open;
                break;
            }

            pos = close + 8;
        }

        if (safeLimit <= pos)
        {
            return 0;
        }

        int lastNl = tail.LastIndexOf('\n', safeLimit - 1);
        return lastNl >= pos ? lastNl + 1 : 0;
    }

    private static void TryEmitCompletedStreamLines(
        string rawAccumulated,
        int expectedCount,
        bool[] emitted,
        Action<int, string> onLineReady,
        bool includeLastUnterminated)
    {
        string clean = StripThinkBlocks(rawAccumulated, trimResult: false);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return;
        }

        string scannable = clean;
        if (!includeLastUnterminated)
        {
            int lastNl = clean.LastIndexOf('\n');
            if (lastNl < 0)
            {
                return;
            }

            scannable = clean[..lastNl];
        }

        string[] lines = scannable
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string line in lines)
        {
            if (TryParseNumberedLine(line, expectedCount, out int number, out string text))
            {
                int idx = number - 1;
                if (emitted[idx])
                {
                    continue;
                }

                if (!RefusalDetector.IsRefusalResponse(text, out _))
                {
                    emitted[idx] = true;
                    onLineReady(idx, text);
                }
            }
        }
    }

    internal static string BuildOpenAiChatCompletionsUrl(string endpoint)
    {
        string trimmed = endpoint.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "/chat/completions";
        }

        return trimmed + "/v1/chat/completions";
    }

    private string? UnprotectApiKey(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        if (_protector.IsProtected(stored))
        {
            try
            {
                return _protector.Unprotect(stored);
            }
            catch
            {
                return null;
            }
        }

        return stored.Trim();
    }

    private static bool ContainsSafetyFilterKeyword(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        return body.Contains("content_filter", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("safety", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("moderation", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("policy", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProviderId(string? id)
    {
        if (string.Equals(id, TranslationProviderIds.DeepSeek, StringComparison.OrdinalIgnoreCase))
        {
            return TranslationProviderIds.DeepSeek;
        }

        if (string.Equals(id, TranslationProviderIds.LocalSakuraLlamaCpp, StringComparison.OrdinalIgnoreCase))
        {
            return TranslationProviderIds.LocalSakuraLlamaCpp;
        }

        if (string.Equals(id, TranslationProviderIds.CustomOpenAiCompatible, StringComparison.OrdinalIgnoreCase))
        {
            return TranslationProviderIds.CustomOpenAiCompatible;
        }

        return TranslationProviderIds.DirectMachineTranslation;
    }

    private static string BuildProviderCacheKeyId(string primary, DesktopSettings settings)
    {
        if (primary != TranslationProviderIds.DeepSeek)
        {
            return primary;
        }

        string model = string.IsNullOrWhiteSpace(settings.DeepSeekModel)
            ? DefaultDeepSeekModel
            : settings.DeepSeekModel.Trim();
        string effort = NormalizeReasoningEffort(settings.DeepSeekReasoningEffort);
        return $"{primary}:{model}:{effort}";
    }

    private static List<string> BuildIntraPageContext(
        IReadOnlyList<OcrTextBlock> orderedPageBlocks,
        IReadOnlyList<OcrTextBlock> currentChunk,
        IReadOnlyDictionary<string, string> resolvedBySource)
    {
        const int maxIntraContext = 16;
        var chunkIds = new HashSet<string>(currentChunk.Select(static b => b.Id), StringComparer.Ordinal);
        int firstChunkIdx = orderedPageBlocks.Count;
        for (int i = 0; i < orderedPageBlocks.Count; i++)
        {
            if (chunkIds.Contains(orderedPageBlocks[i].Id))
            {
                firstChunkIdx = i;
                break;
            }
        }

        var beforeEntries = new List<string>();
        var otherEntries = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < orderedPageBlocks.Count; i++)
        {
            OcrTextBlock block = orderedPageBlocks[i];
            if (chunkIds.Contains(block.Id))
            {
                continue;
            }

            string src = block.Text.Trim();
            if (string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            string entry = resolvedBySource.TryGetValue(src, out string? translated) &&
                           !string.IsNullOrWhiteSpace(translated)
                ? $"{src.ReplaceLineEndings(" ")} => {translated.ReplaceLineEndings(" ").Trim()}"
                : src.ReplaceLineEndings(" ");

            if (!seen.Add(entry))
            {
                continue;
            }

            if (i < firstChunkIdx)
            {
                beforeEntries.Add(entry);
            }
            else
            {
                otherEntries.Add(entry);
            }
        }

        if (beforeEntries.Count + otherEntries.Count <= maxIntraContext)
        {
            beforeEntries.AddRange(otherEntries);
            return beforeEntries;
        }

        int reservedAfter = Math.Min(4, otherEntries.Count);
        int takeBefore = Math.Min(maxIntraContext - reservedAfter, beforeEntries.Count);
        int takeAfter = Math.Min(maxIntraContext - takeBefore, otherEntries.Count);

        var result = new List<string>(takeBefore + takeAfter);
        if (takeBefore > 0)
        {
            result.AddRange(beforeEntries.GetRange(beforeEntries.Count - takeBefore, takeBefore));
        }

        if (takeAfter > 0)
        {
            result.AddRange(otherEntries.GetRange(0, takeAfter));
        }

        return result;
    }

    private static IReadOnlyList<string>? BuildSingleBubbleContext(
        IReadOnlyList<OcrTextBlock> orderedPageBlocks,
        OcrTextBlock targetBlock,
        IReadOnlyDictionary<string, string> resolvedBySource,
        IReadOnlyList<string>? previousPageContext)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (previousPageContext is { Count: > 0 })
        {
            foreach (string prev in previousPageContext.Take(4))
            {
                if (string.IsNullOrWhiteSpace(prev))
                {
                    continue;
                }

                string normalized = prev.ReplaceLineEndings(" ").Trim();
                if (seen.Add(normalized))
                {
                    lines.Add(normalized);
                }
            }
        }

        foreach (OcrTextBlock block in orderedPageBlocks)
        {
            if (string.Equals(block.Id, targetBlock.Id, StringComparison.Ordinal))
            {
                continue;
            }

            string src = block.Text.Trim();
            if (string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            string entry = resolvedBySource.TryGetValue(src, out string? translated) &&
                           !string.IsNullOrWhiteSpace(translated)
                ? $"{src.ReplaceLineEndings(" ")} => {translated.ReplaceLineEndings(" ").Trim()}"
                : src.ReplaceLineEndings(" ");

            if (seen.Add(entry))
            {
                lines.Add(entry);
                if (lines.Count >= 16)
                {
                    break;
                }
            }
        }

        return lines.Count > 0 ? lines : null;
    }

    private sealed record CachedGlossaryEntry(string RawRules, IReadOnlyList<KeyValuePair<string, string>> Rules);

    private static CachedGlossaryEntry? s_cachedGlossary;

    internal static IReadOnlyList<KeyValuePair<string, string>> GetCachedGlossaryRules(string? rawRules)
    {
        if (string.IsNullOrWhiteSpace(rawRules))
        {
            return [];
        }

        CachedGlossaryEntry? cached = Volatile.Read(ref s_cachedGlossary);
        if (cached is not null && string.Equals(cached.RawRules, rawRules, StringComparison.Ordinal))
        {
            return cached.Rules;
        }

        IReadOnlyList<KeyValuePair<string, string>> parsed = ParseGlossaryRules(rawRules).AsReadOnly();
        Volatile.Write(ref s_cachedGlossary, new CachedGlossaryEntry(rawRules, parsed));
        return parsed;
    }

    internal static List<KeyValuePair<string, string>> ParseGlossaryRules(string? rawRules)
    {
        var entries = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(rawRules))
        {
            return entries;
        }

        string[] separators = ["=>", "->", "=", "＝", "\t", "：", ":"];
        var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        string[] lines = rawRules.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string line in lines)
        {
            if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            int bestIdx = -1;
            int bestLen = 0;
            foreach (string sep in separators)
            {
                int idx = line.IndexOf(sep, StringComparison.Ordinal);
                if (idx > 0 && (bestIdx < 0 || idx < bestIdx || (idx == bestIdx && sep.Length > bestLen)))
                {
                    bestIdx = idx;
                    bestLen = sep.Length;
                }
            }

            if (bestIdx <= 0)
            {
                continue;
            }

            string source = line[..bestIdx].Trim();
            string target = line[(bestIdx + bestLen)..].Trim();
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            var kv = new KeyValuePair<string, string>(source, target);
            if (indexByKey.TryGetValue(source, out int existingIdx))
            {
                entries[existingIdx] = kv;
            }
            else
            {
                indexByKey[source] = entries.Count;
                entries.Add(kv);
            }
        }

        return entries;
    }

    internal static List<KeyValuePair<string, string>> MatchGlossaryEntries(
        IReadOnlyList<KeyValuePair<string, string>> glossary,
        IEnumerable<string> sourceTexts)
    {
        if (glossary.Count == 0)
        {
            return [];
        }

        List<string> normalizedSources = sourceTexts
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s.Trim())
            .ToList();
        if (normalizedSources.Count == 0)
        {
            return [];
        }

        var matched = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, string> kv in glossary)
        {
            bool found = false;
            foreach (string src in normalizedSources)
            {
                if (src.Contains(kv.Key, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (found)
            {
                matched.Add(kv);
            }
        }

        matched.Sort(static (a, b) => b.Key.Length.CompareTo(a.Key.Length));
        return matched.Count > 16 ? matched.Take(16).ToList() : matched;
    }

    internal static string ApplyPostGlossaryEnforcement(
        string translated,
        IReadOnlyList<KeyValuePair<string, string>>? matchedGlossary)
    {
        if (string.IsNullOrWhiteSpace(translated) || matchedGlossary is not { Count: > 0 })
        {
            return translated;
        }

        string updated = translated;
        foreach (KeyValuePair<string, string> kv in matchedGlossary)
        {
            if (updated.Contains(kv.Key, StringComparison.Ordinal))
            {
                updated = updated.Replace(kv.Key, kv.Value, StringComparison.Ordinal);
            }
        }

        return updated;
    }

    private static string ComputeGlossarySignature(IReadOnlyList<KeyValuePair<string, string>> matchedGlossary)
    {
        if (matchedGlossary.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("\u001F", matchedGlossary.Select(static kv => $"{kv.Key}={kv.Value}"));
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

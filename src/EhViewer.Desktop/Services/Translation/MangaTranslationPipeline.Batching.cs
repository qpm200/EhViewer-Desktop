using System.Text;
using System.Text.Json.Nodes;

namespace EhViewer.Desktop.Services.Translation;

public sealed partial class MangaTranslationPipeline
{
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
}

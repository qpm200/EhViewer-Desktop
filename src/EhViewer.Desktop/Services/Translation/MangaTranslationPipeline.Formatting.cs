using System.Text;
using System.Text.Json;

namespace EhViewer.Desktop.Services.Translation;

public sealed partial class MangaTranslationPipeline
{
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
}

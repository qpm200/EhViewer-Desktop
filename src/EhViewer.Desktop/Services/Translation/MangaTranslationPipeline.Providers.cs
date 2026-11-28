using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EhViewer.Desktop.Services.Translation;

public sealed partial class MangaTranslationPipeline
{
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
}

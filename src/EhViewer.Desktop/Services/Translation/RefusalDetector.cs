namespace EhViewer.Desktop.Services.Translation;

/// <summary>
/// Detects LLM safety/content-policy refusals so explicit manga lines are never presented
/// as fake "I cannot translate this" output and instead fall back to uncensored/MT engines.
/// </summary>
public static class RefusalDetector
{
    private static readonly string[] EnglishRefusalSignals =
    [
        "i cannot translate",
        "i can't translate",
        "cannot fulfill this request",
        "can't fulfill this request",
        "unable to translate",
        "unable to assist with",
        "i cannot assist",
        "i can't assist",
        "cannot assist with",
        "i'm sorry, i cannot",
        "i'm sorry, i can't",
        "i apologize, but i cannot",
        "i must decline",
        "as an ai",
        "as a language model",
        "against my safety",
        "violates safety guidelines",
        "violates my content policy",
        "explicit sexual content",
        "sexually explicit",
        "nsfw content"
    ];

    private static readonly string[] ChineseRefusalSignals =
    [
        "无法为您翻译",
        "无法翻译该",
        "不能翻译包含",
        "抱歉，我无法",
        "对不起，我无法",
        "我不能提供",
        "我无法协助",
        "违反了安全准则",
        "违反内容政策",
        "内容安全策略",
        "包含露骨",
        "色情低俗内容",
        "不适合展示"
    ];

    public static bool IsRefusalResponse(string? text, out string reason)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            reason = "返回为空";
            return true;
        }

        string trimmed = text.Trim();
        foreach (string phrase in EnglishRefusalSignals)
        {
            if (trimmed.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"触发英文拒答规则 ({phrase})";
                return true;
            }
        }

        foreach (string phrase in ChineseRefusalSignals)
        {
            if (trimmed.Contains(phrase, StringComparison.Ordinal))
            {
                reason = $"触发中文拒答规则 ({phrase})";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }
}

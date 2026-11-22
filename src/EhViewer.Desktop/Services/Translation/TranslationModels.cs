namespace EhViewer.Desktop.Services.Translation;

public sealed record OcrRawLine(
    string Text,
    double X,
    double Y,
    double Width,
    double Height);

public sealed record OcrTextBlock(
    string Id,
    string Text,
    double NormalizedX,
    double NormalizedY,
    double NormalizedWidth,
    double NormalizedHeight,
    bool IsVertical,
    uint FillArgb = 0,
    uint TextArgb = 0);

public sealed record OcrPageResult(
    string PageKey,
    string LanguageTag,
    int ImageWidth,
    int ImageHeight,
    IReadOnlyList<OcrTextBlock> Blocks);

public sealed record TranslationResult(
    string TranslatedText,
    string ProviderId,
    string ProviderDisplayName,
    bool UsedFallback,
    string? FallbackNote = null);

public sealed record ModelDownloadProgress(
    long BytesReceived,
    long? TotalBytes,
    string StatusText)
{
    public double Percentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100.0 / TotalBytes.Value, 0.0, 100.0)
        : 0.0;
}

public static class TranslationProviderIds
{
    public const string DirectMachineTranslation = "direct-mt";
    public const string LocalSakuraLlamaCpp = "llama-sakura";
    public const string DeepSeek = "deepseek";
    public const string CustomOpenAiCompatible = "openai-compat";
}

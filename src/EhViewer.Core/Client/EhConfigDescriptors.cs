namespace EhViewer.Core.Client;

public sealed record ExcludedLanguageGroup(
    string Key,
    string DisplayName,
    string OriginalCode,
    string TranslatedCode,
    string RewriteCode);

public sealed record ExcludedNamespaceOption(int Mask, string Key, string DisplayName);

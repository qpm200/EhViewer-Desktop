namespace EhViewer.Desktop.Services;

/// <summary>
/// Resolves every on-disk location the desktop client uses. All paths live under a single
/// per-user root so an Android migration or a "green" (portable) build can relocate them together.
/// </summary>
public sealed class AppPaths
{
    public const string ProductDirectoryName = "EhViewer.Desktop";

    private AppPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
    }

    /// <summary>Root directory containing all application state.</summary>
    public string RootDirectory { get; }

    public string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");
    public string CookieDatabasePath => Path.Combine(RootDirectory, "cookie.db");
    public string HostsDatabasePath => Path.Combine(RootDirectory, "hosts.db");
    public string EhDatabasePath => Path.Combine(RootDirectory, "eh.db");
    public string SearchDatabasePath => Path.Combine(RootDirectory, "search_database.db");
    public string TranslationCacheDatabasePath => Path.Combine(RootDirectory, "translation-cache.db");
    public string TagTranslationDirectory => Path.Combine(RootDirectory, "tag-translations");
    public string CacheDirectory => Path.Combine(RootDirectory, "cache");
    public string ThumbnailCacheDirectory => Path.Combine(CacheDirectory, "thumbnails");
    public string ImageCacheDirectory => Path.Combine(CacheDirectory, "images");
    public string LogDirectory => Path.Combine(RootDirectory, "logs");
    public string ParseErrorDirectory => Path.Combine(RootDirectory, "parse-errors");
    public string DownloadDirectory => Path.Combine(RootDirectory, "download");
    public string ModelsDirectory => Path.Combine(RootDirectory, "models");
    public string OcrModelsDirectory => Path.Combine(ModelsDirectory, "ocr");
    public string BubbleDetectorModelPath => Path.Combine(OcrModelsDirectory, "bubble-detector.onnx");
    public string MangaOcrEncoderPath => Path.Combine(OcrModelsDirectory, "manga-ocr-cpt-v2-encoder.onnx");
    public string MangaOcrDecoderPath => Path.Combine(OcrModelsDirectory, "manga-ocr-cpt-v2-decoder-merged.onnx");
    public string MangaOcrVocabPath => Path.Combine(OcrModelsDirectory, "manga-ocr-cpt-v2-vocab.txt");
    public string DefaultLlamaCppDirectory => Path.Combine(ModelsDirectory, "llama-cpp");
    public string DefaultSakuraModelPath => Path.Combine(ModelsDirectory, "sakura-7b-qwen2.5-v1.0-iq4xs.gguf");

    /// <summary>
    /// Resolves the effective gallery download root, honoring <see cref="DesktopSettings.CustomDownloadDirectory"/>
    /// when configured and falling back to <see cref="DownloadDirectory"/> otherwise.
    /// </summary>
    public string GetEffectiveDownloadDirectory(DesktopSettings? settings)
    {
        string? custom = settings?.CustomDownloadDirectory;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                return Path.GetFullPath(custom.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return DownloadDirectory;
    }

    /// <summary>
    /// Default layout: <c>%LOCALAPPDATA%\EhViewer.Desktop</c> (or <c>EHVIEWER_DATA_DIR</c> when set).
    /// </summary>
    public static AppPaths CreateDefault()
    {
        string? overrideDir = Environment.GetEnvironmentVariable("EHVIEWER_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return CreateAt(overrideDir.Trim());
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            localAppData = AppContext.BaseDirectory;
        }

        return new AppPaths(Path.Combine(localAppData, ProductDirectoryName));
    }

    /// <summary>
    /// Portable layout rooted at an explicit directory (used by green builds and by tests).
    /// </summary>
    public static AppPaths CreateAt(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        return new AppPaths(Path.GetFullPath(rootDirectory));
    }

    /// <summary>Creates every directory this instance references. Safe to call repeatedly.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(TagTranslationDirectory);
        Directory.CreateDirectory(ThumbnailCacheDirectory);
        Directory.CreateDirectory(ImageCacheDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(DownloadDirectory);
    }
}

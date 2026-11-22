using System.Text.Json;
using System.Text.Json.Serialization;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Network;
using EhViewer.Desktop.Services.Theming;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Persisted desktop configuration document. Mirrors <see cref="IEhClientSettings"/> plus
/// Windows-specific options (theme, cache budgets, concurrency). Written as indented JSON so a
/// user can hand-edit it, and read with tolerant defaults when a field is missing or corrupt.
/// </summary>
public sealed class DesktopSettings
{
    // --- Networking (IEhClientSettings) ---
    public bool BuiltInHosts { get; set; } = true;
    public bool BuiltInExHosts { get; set; } = true;
    public bool UseDnsOverHttps { get; set; }
    public bool DomainFronting { get; set; } = true;
    public string DohEndpoint { get; set; } = "https://77.88.8.1/dns-query";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EhProxyType ProxyType { get; set; } = EhProxyType.System;

    public string? ProxyIp { get; set; }
    public int ProxyPort { get; set; } = -1;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EhSite GallerySite { get; set; } = EhSite.E;

    // --- Gallery presentation ---
    public int ThumbResolution { get; set; }
    public bool FixThumbUrl { get; set; }
    public bool ShowJpnTitle { get; set; }
    public bool ShowGalleryPages { get; set; }
    public bool SaveParseErrorBody { get; set; } = true;
    public string ImageResolution { get; set; } = EhConfig.ImageSizeAuto;

    // --- Windows client options ---
    /// <summary>Selected UI design theme id (e.g. "classic-material").</summary>
    public string UiTheme { get; set; } = UiDesignThemeRegistry.DefaultThemeId;

    /// <summary>0 = follow system, 1 = light, 2 = dark, 3 = AMOLED black.</summary>
    public int Theme { get; set; }

    /// <summary>Whether the Mica backdrop is requested on Windows 11.</summary>
    public bool UseMica { get; set; } = true;

    /// <summary>Whether the left navigation drawer is collapsed into an icon-only rail.</summary>
    public bool CollapseNavigationDrawer { get; set; }

    /// <summary>Whether hovering over the collapsed left navigation drawer auto-expands it and leaving auto-collapses it.</summary>
    public bool AutoExpandSidebarOnHover { get; set; }

    /// <summary>Maximum number of rows kept in history before eviction.</summary>
    public int MaxHistoryCount { get; set; } = 100;

    /// <summary>Memory budget for decoded thumbnails, in megabytes.</summary>
    public int ThumbnailMemoryBudgetMb { get; set; } = 96;

    /// <summary>On-disk thumbnail cache cap, in megabytes. 0 disables eviction.</summary>
    public int ThumbnailDiskBudgetMb { get; set; } = 512;

    /// <summary>Reader decoded-page cache budget, in megabytes.</summary>
    public int ReaderCacheBudgetMb { get; set; } = 512;

    /// <summary>Concurrent gallery-detail / image downloads.</summary>
    public int DownloadConcurrency { get; set; } = 3;

    /// <summary>Whether to restore the previous window size and position on launch.</summary>
    public bool RememberWindowPlacement { get; set; } = true;

    // --- Reader defaults & display ---
    public int PreloadCount { get; set; } = 3;
    public int DefaultReaderLayout { get; set; }
    public int DefaultReaderScale { get; set; }
    public bool DualPageMode { get; set; }
    public bool DualPageFirstAlone { get; set; } = true;
    public bool ContinuousScroll { get; set; }
    public int MaxDecodeEdge { get; set; } = 4096;
    public bool ForceSrgb { get; set; }
    public bool ShowPageNumber { get; set; } = true;
    public bool KeepScreenOn { get; set; }
    public bool PageTurnByWheel { get; set; } = true;
    public bool HideReaderChromeInFullscreen { get; set; } = true;

    // --- Reader OCR & anti-refusal translation ---
    public string ReaderOcrLanguage { get; set; } = "auto";
    public string ReaderTranslationProvider { get; set; } = "direct-mt";
    public bool ReaderTranslationAutoFallback { get; set; } = true;
    public string ReaderInlineTranslationStyle { get; set; } = "mask";
    public bool ReaderAutoTranslateOnPageTurn { get; set; }
    public string TranslationGlossaryRules { get; set; } = string.Empty;
    public bool EnableLocalSakuraModel { get; set; }
    public bool UseHfMirrorForModelDownload { get; set; } = true;
    public string? LocalLlamaServerPath { get; set; }
    public string? LocalSakuraModelPath { get; set; }
    public int LocalLlamaGpuLayers { get; set; } = 99;
    public int LocalLlamaPort { get; set; } = 18089;
    public string LocalLlamaBackend { get; set; } = "vulkan";
    public string? CustomTranslationEndpoint { get; set; }
    public string? CustomTranslationModel { get; set; }
    public string? CustomTranslationApiKeyProtected { get; set; }
    public string DeepSeekEndpoint { get; set; } = "https://api.deepseek.com";
    public string DeepSeekModel { get; set; } = "deepseek-chat";
    public string DeepSeekReasoningEffort { get; set; } = "low";
    public string? DeepSeekApiKeyProtected { get; set; }

    // --- Downloads ---
    public string? CustomDownloadDirectory { get; set; }
    public int DownloadTimeoutSeconds { get; set; } = 60;
    public bool DownloadPauseOnBattery { get; set; }

    // --- Browse & list UX ---
    public int ListPageSize { get; set; } = 25;
    public bool ShowHoverPreview { get; set; } = true;

    // --- Site uconfig sync ---
    public string ExcludedLanguages { get; set; } = string.Empty;
    public int ExcludedNamespaces { get; set; }
    public string FavoritesSortOrder { get; set; } = EhConfig.OrderByFavTime;
    public string CommentsSortOrder { get; set; } = EhConfig.CommentsSortOldestFirst;
    public bool EnablePopularOnHome { get; set; } = true;

    // --- Windows shell, notifications, security & updates ---
    public bool MinimizeToTray { get; set; }
    public bool ShowNotifications { get; set; } = true;
    public bool CheckUpdateOnLaunch { get; set; }
    public string UpdateFeedUrl { get; set; } = "https://api.github.com/repos/xiaojieonly/Ehviewer_CN_SXJ/releases/latest";
    public string? SecurityPinHash { get; set; }
    public bool UseWindowsHello { get; set; }
    public bool EnableBossKey { get; set; } = true;
    public string BossKeyGesture { get; set; } = BossKeyBinding.DefaultGesture;
    public bool RequireUnlockOnBossRestore { get; set; } = true;
    public int AutoLockMinutes { get; set; }
    public bool StealthTrayTooltip { get; set; }
    public int? SavedWindowX { get; set; }
    public int? SavedWindowY { get; set; }
    public int? SavedWindowWidth { get; set; }
    public int? SavedWindowHeight { get; set; }
    public bool SavedWindowMaximized { get; set; }
    public List<PersistedTabState> OpenTabs { get; set; } = [];
    public int ActiveTabIndex { get; set; }
}

/// <summary>
/// Serializable browse/gallery tab state persisted across launches when session restore is active.
/// </summary>
public sealed class PersistedTabState
{
    public string Title { get; set; } = "画廊列表";
    public string Kind { get; set; } = "Home";
    public string? Keyword { get; set; }
    public int Mode { get; set; } = ListUrlBuilder.ModeNormal;
    public int Category { get; set; } = EhUtils.None;
    public int PageIndex { get; set; }
    public long Gid { get; set; }
    public string? Token { get; set; }
    public string? TopListCode { get; set; }
    public int FavCat { get; set; } = FavListUrlBuilder.FavCatAll;
}

/// <summary>
/// Loads and atomically saves <see cref="DesktopSettings"/>. A corrupt or unreadable file is
/// replaced by defaults instead of failing startup; a failed write leaves the previous file intact.
/// </summary>
public sealed class DesktopSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _filePath;
    private readonly object _gate = new();

    public DesktopSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    /// <summary>Set when the previous load had to fall back to defaults.</summary>
    public string? LastLoadWarning { get; private set; }

    public DesktopSettings Load()
    {
        lock (_gate)
        {
            LastLoadWarning = null;

            if (!File.Exists(_filePath))
            {
                return new DesktopSettings();
            }

            try
            {
                string json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new DesktopSettings();
                }

                DesktopSettings loaded = JsonSerializer.Deserialize<DesktopSettings>(json, SerializerOptions) ?? new DesktopSettings();
                if (string.IsNullOrWhiteSpace(loaded.UiTheme))
                {
                    loaded.UiTheme = UiDesignThemeRegistry.DefaultThemeId;
                }

                return loaded;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                LastLoadWarning = $"settings.json 读取失败，已回退默认值：{ex.Message}";
                return new DesktopSettings();
            }
        }
    }

    /// <summary>
    /// Writes the document to a sibling temp file first, then replaces the target so a crash
    /// mid-write never truncates the live settings.
    /// </summary>
    public void Save(DesktopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            string tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
    }
}

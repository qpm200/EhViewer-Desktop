using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using EhViewer.Core.Client;
using EhViewer.Core.Network;
using EhViewer.Core.Security;
using EhViewer.Core.Storage;
using EhViewer.Core.Util;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Theming;
using EhViewer.Desktop.Services.Translation;

namespace EhViewer.Desktop.ViewModels;

/// <summary>
/// Settings view model: edits a copy of <see cref="DesktopSettings"/> and commits it through
/// <see cref="AppServices.UpdateSettings"/> so changes persist and immediately affect live services,
/// while managing <c>uconfig</c>, custom Hosts, comment blacklist, security PIN/Windows Hello,
/// updates, and database maintenance.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly int[] ListPageSizeValues = [25, 50, 100, 200];
    private static readonly string[] ImageResolutionValues =
    [
        EhConfig.ImageSizeAuto,
        EhConfig.ImageSize780X,
        EhConfig.ImageSize980X,
        EhConfig.ImageSize1280X,
        EhConfig.ImageSize1600X,
        EhConfig.ImageSize2400X
    ];
    private static readonly int[] MaxDecodeEdgeValues = [2048, 3072, 4096, 6144, 8192];
    private static readonly string[] FavoritesSortValues =
    [
        EhConfig.FavoritesSortFavoritedTime,
        EhConfig.FavoritesSortGalleryUpdateTime
    ];
    private static readonly string[] CommentsSortValues =
    [
        EhConfig.CommentsSortOldestFirst,
        EhConfig.CommentsSortRecentFirst,
        EhConfig.CommentsSortHighestScoreFirst
    ];
    private static readonly string[] OcrLanguageValues = ["auto", "ja", "en", "zh-Hans", "ko"];
    private static readonly string[] TranslationProviderValues =
    [
        TranslationProviderIds.DirectMachineTranslation,
        TranslationProviderIds.LocalSakuraLlamaCpp,
        TranslationProviderIds.CustomOpenAiCompatible,
        TranslationProviderIds.DeepSeek
    ];
    private static readonly string[] DeepSeekReasoningEffortValues = ["low", "medium", "high"];
    private static readonly string[] LlamaBackendValues = ["vulkan", "cuda", "cpu"];
    private static readonly string[] InlineTranslationStyleValues = ["mask", "badge"];

    private static readonly HashSet<string> NonPersistedPropertyNames =
    [
        nameof(MaintenanceMessage),
        nameof(ExcludedLanguagesSummary),
        nameof(ExcludedNamespacesSummary),
        nameof(UConfigStatusMessage),
        nameof(EffectiveDownloadDirectory),
        nameof(SecurityPinStatus),
        nameof(HasSecurityPin),
        nameof(CanClearAppLock),
        nameof(UpdateStatusMessage),
        nameof(LatestUpdateInfo),
        nameof(StatusMessage),
        nameof(ErrorMessage),
        nameof(ConfigDirectory),
        nameof(Summary),
        nameof(UiThemeId),
        nameof(OcrLanguagePackStatus),
        nameof(IsJapaneseOcrMissing),
        nameof(MangaOcrModelStatus),
        nameof(IsMangaOcrMissing),
        nameof(LocalModelDownloadProgress),
        nameof(IsModelDownloading),
        nameof(LocalTranslationStatusMessage),
        nameof(IsLlamaServerRunning),
        nameof(IsTestingTranslation),
        nameof(CanTestTranslation),
        nameof(CustomTranslationApiKeyStatus),
        nameof(DeepSeekApiKeyStatus),
        nameof(ShowLocalSakuraSection),
        nameof(ShowCustomOpenAiSection),
        nameof(ShowDeepSeekSection),
        nameof(TranslationCacheCountText)
    ];

    private readonly AppServices _services;
    private bool _suppressAutoSave;
    private bool _summaryDirty = true;
    private string _cachedSummary = string.Empty;

    public SettingsViewModel(AppServices services, bool deferHeavyIo = false)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Load(deferHeavyIo);
    }

    /// <summary>When enabled, modifying any persisted preference auto-saves and fires <see cref="SettingsApplied"/> immediately.</summary>
    public bool AutoSaveOnChange { get; set; }

    /// <summary>Raised whenever <see cref="Save"/> commits updated settings.</summary>
    public event EventHandler? SettingsApplied;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (AutoSaveOnChange
            && !_suppressAutoSave
            && !string.IsNullOrEmpty(e.PropertyName)
            && !NonPersistedPropertyNames.Contains(e.PropertyName))
        {
            Save();
        }
    }

    // --- Maintenance actions ---
    [ObservableProperty]
    public partial string MaintenanceMessage { get; set; } = string.Empty;

    public string? ExportData() => _services.GetRequiredService<MaintenanceService>().ExportDatabase();

    public bool ImportData(string path) => _services.GetRequiredService<MaintenanceService>().ImportDatabase(path);

    public long ClearImageCaches() => _services.GetRequiredService<MaintenanceService>().ClearImageCaches();

    public int CleanRedundantRows() => _services.GetRequiredService<MaintenanceService>().CleanRedundantDownloadRows();

    public void ClearDownloadPathCache() => _services.GetRequiredService<MaintenanceService>().ClearDownloadPathCache();

    // --- Site & Browse ---
    [ObservableProperty]
    public partial bool UseExHentai { get; set; }

    [ObservableProperty]
    public partial bool ShowJpnTitle { get; set; }

    [ObservableProperty]
    public partial bool ShowGalleryPages { get; set; }

    [ObservableProperty]
    public partial bool ShowHoverPreview { get; set; } = true;

    [ObservableProperty]
    public partial int ListPageSizeIndex { get; set; }

    [ObservableProperty]
    public partial int ThumbResolutionIndex { get; set; }

    [ObservableProperty]
    public partial bool FixThumbUrl { get; set; }

    [ObservableProperty]
    public partial int ImageResolutionIndex { get; set; }

    [ObservableProperty]
    public partial bool SaveParseErrorBody { get; set; }

    // --- UConfig sync ---
    [ObservableProperty]
    public partial string ExcludedLanguages { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int ExcludedNamespaces { get; set; }

    [ObservableProperty]
    public partial string ExcludedLanguagesSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ExcludedNamespacesSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int FavoritesSortIndex { get; set; }

    [ObservableProperty]
    public partial int CommentsSortIndex { get; set; }

    [ObservableProperty]
    public partial bool EnablePopularOnHome { get; set; } = true;

    [ObservableProperty]
    public partial string UConfigStatusMessage { get; set; } = string.Empty;

    // --- Network / Proxy / Hosts / DoH ---
    [ObservableProperty]
    public partial int ProxyTypeIndex { get; set; }

    [ObservableProperty]
    public partial string ProxyIp { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int ProxyPort { get; set; } = -1;

    [ObservableProperty]
    public partial bool BuiltInHosts { get; set; }

    [ObservableProperty]
    public partial bool BuiltInExHosts { get; set; }

    [ObservableProperty]
    public partial bool DomainFronting { get; set; } = true;

    [ObservableProperty]
    public partial bool UseDnsOverHttps { get; set; }

    [ObservableProperty]
    public partial string DohEndpoint { get; set; } = string.Empty;

    // --- Downloads ---
    [ObservableProperty]
    public partial string CustomDownloadDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EffectiveDownloadDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int DownloadConcurrency { get; set; }

    [ObservableProperty]
    public partial int DownloadTimeoutSeconds { get; set; } = 60;

    [ObservableProperty]
    public partial bool DownloadPauseOnBattery { get; set; }

    // --- Reader defaults ---
    [ObservableProperty]
    public partial int PreloadCount { get; set; } = 3;

    [ObservableProperty]
    public partial int DefaultReaderLayoutIndex { get; set; }

    [ObservableProperty]
    public partial int DefaultReaderScaleIndex { get; set; }

    [ObservableProperty]
    public partial int MaxDecodeEdgeIndex { get; set; } = 2;

    [ObservableProperty]
    public partial bool DualPageMode { get; set; }

    [ObservableProperty]
    public partial bool DualPageFirstAlone { get; set; } = true;

    [ObservableProperty]
    public partial bool ContinuousScroll { get; set; }

    [ObservableProperty]
    public partial bool ForceSrgb { get; set; }

    [ObservableProperty]
    public partial bool ShowPageNumber { get; set; } = true;

    [ObservableProperty]
    public partial bool KeepScreenOn { get; set; }

    [ObservableProperty]
    public partial bool PageTurnByWheel { get; set; } = true;

    [ObservableProperty]
    public partial bool HideReaderChromeInFullscreen { get; set; } = true;

    // --- OCR & Anti-Refusal Translation ---
    [ObservableProperty]
    public partial int ReaderOcrLanguageIndex { get; set; }

    [ObservableProperty]
    public partial int ReaderTranslationProviderIndex { get; set; }

    partial void OnReaderTranslationProviderIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ShowLocalSakuraSection));
        OnPropertyChanged(nameof(ShowCustomOpenAiSection));
        OnPropertyChanged(nameof(ShowDeepSeekSection));
        if (!_suppressAutoSave)
        {
            if (UsesLocalSakuraStatus())
            {
                RefreshOcrAndLocalModelStatus();
            }
            else
            {
                RefreshTranslationProviderStatusFromMemory();
            }
        }
    }

    [ObservableProperty]
    public partial bool ReaderTranslationAutoFallback { get; set; } = true;

    [ObservableProperty]
    public partial bool EnableLocalSakuraModel { get; set; }

    partial void OnEnableLocalSakuraModelChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLocalSakuraSection));
    }

    [ObservableProperty]
    public partial bool UseHfMirrorForModelDownload { get; set; } = true;

    [ObservableProperty]
    public partial string LocalLlamaServerPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalSakuraModelPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int LocalLlamaGpuLayers { get; set; } = 33;

    [ObservableProperty]
    public partial int LocalLlamaPort { get; set; } = 8766;

    [ObservableProperty]
    public partial int LocalLlamaBackendIndex { get; set; }

    [ObservableProperty]
    public partial string CustomTranslationEndpoint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CustomTranslationModel { get; set; } = "gpt-4o-mini";

    [ObservableProperty]
    public partial string DeepSeekEndpoint { get; set; } = MangaTranslationPipeline.DefaultDeepSeekEndpoint;

    [ObservableProperty]
    public partial string DeepSeekModel { get; set; } = MangaTranslationPipeline.DefaultDeepSeekModel;

    partial void OnDeepSeekModelChanged(string value)
    {
        if (!_suppressAutoSave && ReaderTranslationProviderIndex == 3)
        {
            RefreshTranslationProviderStatusFromMemory();
        }
    }

    [ObservableProperty]
    public partial int DeepSeekReasoningEffortIndex { get; set; }

    partial void OnDeepSeekReasoningEffortIndexChanged(int value)
    {
        if (!_suppressAutoSave && ReaderTranslationProviderIndex == 3)
        {
            RefreshTranslationProviderStatusFromMemory();
        }
    }

    [ObservableProperty]
    public partial bool IsTestingTranslation { get; set; }

    public bool CanTestTranslation => !IsTestingTranslation;

    partial void OnIsTestingTranslationChanged(bool value) =>
        OnPropertyChanged(nameof(CanTestTranslation));

    [ObservableProperty]
    public partial string OcrLanguagePackStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsJapaneseOcrMissing { get; set; }

    [ObservableProperty]
    public partial string MangaOcrModelStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsMangaOcrMissing { get; set; }

    [ObservableProperty]
    public partial double LocalModelDownloadProgress { get; set; }

    [ObservableProperty]
    public partial bool IsModelDownloading { get; set; }

    [ObservableProperty]
    public partial string LocalTranslationStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLlamaServerRunning { get; set; }

    [ObservableProperty]
    public partial string CustomTranslationApiKeyStatus { get; set; } = "未设置 API Key";

    [ObservableProperty]
    public partial string DeepSeekApiKeyStatus { get; set; } = "未设置 DeepSeek API Key";

    [ObservableProperty]
    public partial int ReaderInlineTranslationStyleIndex { get; set; }

    [ObservableProperty]
    public partial bool ReaderAutoTranslateOnPageTurn { get; set; }

    [ObservableProperty]
    public partial string TranslationGlossaryRules { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TranslationCacheCountText { get; set; } = "已持久化缓存 0 条译文 (SQLite)";

    public bool ShowLocalSakuraSection => EnableLocalSakuraModel || ReaderTranslationProviderIndex == 1;

    public bool ShowCustomOpenAiSection => ReaderTranslationProviderIndex == 2;

    public bool ShowDeepSeekSection => ReaderTranslationProviderIndex == 3;

    // --- Caches / limits ---
    [ObservableProperty]
    public partial int ThumbnailMemoryBudgetMb { get; set; }

    [ObservableProperty]
    public partial int ThumbnailDiskBudgetMb { get; set; }

    [ObservableProperty]
    public partial int ReaderCacheBudgetMb { get; set; } = 512;

    [ObservableProperty]
    public partial int MaxHistoryCount { get; set; }

    // --- Appearance / Shell / Security ---
    [ObservableProperty]
    public partial int UiThemeIndex { get; set; }

    [ObservableProperty]
    public partial string UiThemeId { get; set; } = UiDesignThemeRegistry.DefaultThemeId;

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    public partial bool UseMica { get; set; }

    [ObservableProperty]
    public partial bool CollapseNavigationDrawer { get; set; }

    [ObservableProperty]
    public partial bool AutoExpandSidebarOnHover { get; set; }

    [ObservableProperty]
    public partial bool RememberWindowPlacement { get; set; } = true;

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; }

    [ObservableProperty]
    public partial bool ShowNotifications { get; set; } = true;

    [ObservableProperty]
    public partial bool UseWindowsHello { get; set; }

    [ObservableProperty]
    public partial string SecurityPinStatus { get; set; } = "未设置安全锁 PIN（应用安全锁已关闭）";

    [ObservableProperty]
    public partial bool HasSecurityPin { get; set; }

    [ObservableProperty]
    public partial bool CanClearAppLock { get; set; }

    [ObservableProperty]
    public partial bool EnableBossKey { get; set; } = true;

    [ObservableProperty]
    public partial int BossKeyGestureIndex { get; set; }

    [ObservableProperty]
    public partial bool RequireUnlockOnBossRestore { get; set; } = true;

    [ObservableProperty]
    public partial bool StealthTrayTooltip { get; set; }

    [ObservableProperty]
    public partial int AutoLockMinutesIndex { get; set; }

    // --- Updates ---
    [ObservableProperty]
    public partial bool CheckUpdateOnLaunch { get; set; }

    [ObservableProperty]
    public partial string UpdateFeedUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdateStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial AppUpdateInfo? LatestUpdateInfo { get; set; }

    // --- Status ---
    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfigDirectory { get; set; } = string.Empty;

    public string Summary
    {
        get
        {
            if (_summaryDirty)
            {
                _cachedSummary = SettingsPresenter.ToDiagnosticJson(_services.Settings);
                _summaryDirty = false;
            }

            return _cachedSummary;
        }
    }

    private void InvalidateSummary()
    {
        _summaryDirty = true;
        _cachedSummary = string.Empty;
        OnPropertyChanged(nameof(Summary));
    }

    public IReadOnlyList<string> ProxyTypeNames { get; } = ["直连", "跟随系统", "HTTP 代理", "SOCKS5 代理"];

    public IReadOnlyList<UiThemeMetadata> AvailableUiThemes { get; } =
        UiDesignThemeRegistry.All.Select(t => t.Metadata).ToArray();

    public IReadOnlyList<string> ThemeNames { get; } = ["跟随系统", "浅色", "深色", "纯黑 (AMOLED)"];

    public IReadOnlyList<string> ListPageSizeNames { get; } = ["25 条/页", "50 条/页", "100 条/页", "200 条/页"];

    public IReadOnlyList<string> ThumbResolutionNames { get; } = ["默认（原始）", "250 px", "300 px"];

    public IReadOnlyList<string> ImageResolutionNames { get; } = ["自动", "780 px", "980 px", "1280 px", "1600 px", "2400 px"];

    public IReadOnlyList<string> ReaderLayoutNames { get; } = ["左到右", "右到左", "纵向滚动"];

    public IReadOnlyList<string> ReaderScaleNames { get; } = ["适应屏幕", "适应宽度", "适应高度", "原始大小"];

    public IReadOnlyList<string> MaxDecodeEdgeNames { get; } = ["2048 px", "3072 px", "4096 px", "6144 px", "8192 px"];

    public IReadOnlyList<string> FavoritesSortNames { get; } = ["按收藏时间 (f)", "按画廊更新时间 (p)"];

    public IReadOnlyList<string> CommentsSortNames { get; } = ["最早评论优先 (a)", "最新评论优先 (d)", "高分评论优先 (s)"];

    public IReadOnlyList<string> OcrLanguageNames { get; } =
    [
        "自动检测 (按画廊标题/日语优先)",
        "日本語 (ja)",
        "English (en)",
        "简体中文 (zh-Hans)",
        "한국어 (ko)"
    ];

    public IReadOnlyList<string> TranslationProviderNames { get; } =
    [
        "传统直译 (免配置 · 零拒答)",
        "本地 Sakura-7B (llama.cpp 推理引擎)",
        "自定义 OpenAI 兼容接口",
        "DeepSeek AI (连续上下文 · 低推理)"
    ];

    public IReadOnlyList<string> DeepSeekReasoningEffortNames { get; } =
    [
        "Low (低推理 · 极速连续漫画翻译推荐)",
        "Medium (中等推理)",
        "High (深度推理)"
    ];

    public IReadOnlyList<string> LlamaBackendNames { get; } =
    [
        "Vulkan GPU (推荐 · 兼容核显/A卡/N卡)",
        "CUDA GPU (NVIDIA 显卡)",
        "纯 CPU (AVX2)"
    ];

    public IReadOnlyList<string> InlineTranslationStyleNames { get; } =
    [
        "填底嵌字 (抹白原生排版 · 推荐)",
        "紧凑角标 (保留原文对比)"
    ];

    /// <summary>Re-reads the persisted document into the editable fields.</summary>
    public void Load() => Load(deferHeavyIo: false);

    internal void Load(bool deferHeavyIo)
    {
        bool prevSuppress = _suppressAutoSave;
        _suppressAutoSave = true;
        try
        {
            LoadCore(deferHeavyIo);
        }
        finally
        {
            _suppressAutoSave = prevSuppress;
        }
    }

    private void LoadCore(bool deferHeavyIo)
    {
        DesktopSettings s = _services.Settings;

        UseExHentai = s.GallerySite == EhSite.Ex;
        ShowJpnTitle = s.ShowJpnTitle;
        ShowGalleryPages = s.ShowGalleryPages;
        ShowHoverPreview = s.ShowHoverPreview;
        ListPageSizeIndex = IndexOfInt(ListPageSizeValues, s.ListPageSize, 0);
        ThumbResolutionIndex = Math.Clamp(s.ThumbResolution, 0, 2);
        FixThumbUrl = s.FixThumbUrl;
        ImageResolutionIndex = IndexOfString(ImageResolutionValues, s.ImageResolution, 0);
        SaveParseErrorBody = s.SaveParseErrorBody;

        ExcludedLanguages = s.ExcludedLanguages ?? string.Empty;
        ExcludedNamespaces = s.ExcludedNamespaces;
        UpdateExclusionSummaries();
        FavoritesSortIndex = IndexOfString(FavoritesSortValues, s.FavoritesSortOrder, 0);
        CommentsSortIndex = IndexOfString(CommentsSortValues, s.CommentsSortOrder, 0);
        EnablePopularOnHome = s.EnablePopularOnHome;

        ProxyTypeIndex = (int)s.ProxyType;
        ProxyIp = s.ProxyIp ?? string.Empty;
        ProxyPort = s.ProxyPort;
        BuiltInHosts = s.BuiltInHosts;
        BuiltInExHosts = s.BuiltInExHosts;
        DomainFronting = s.DomainFronting;
        UseDnsOverHttps = s.UseDnsOverHttps;
        DohEndpoint = s.DohEndpoint;

        CustomDownloadDirectory = s.CustomDownloadDirectory ?? string.Empty;
        EffectiveDownloadDirectory = _services.Paths.GetEffectiveDownloadDirectory(s);
        DownloadConcurrency = s.DownloadConcurrency;
        DownloadTimeoutSeconds = s.DownloadTimeoutSeconds;
        DownloadPauseOnBattery = s.DownloadPauseOnBattery;

        PreloadCount = Math.Clamp(s.PreloadCount, 1, 10);
        DefaultReaderLayoutIndex = Math.Clamp(s.DefaultReaderLayout, 0, 2);
        DefaultReaderScaleIndex = Math.Clamp(s.DefaultReaderScale, 0, 3);
        MaxDecodeEdgeIndex = IndexOfInt(MaxDecodeEdgeValues, s.MaxDecodeEdge, 2);
        DualPageMode = s.DualPageMode;
        DualPageFirstAlone = s.DualPageFirstAlone;
        ContinuousScroll = s.ContinuousScroll;
        ForceSrgb = s.ForceSrgb;
        ShowPageNumber = s.ShowPageNumber;
        KeepScreenOn = s.KeepScreenOn;
        PageTurnByWheel = s.PageTurnByWheel;
        HideReaderChromeInFullscreen = s.HideReaderChromeInFullscreen;

        ReaderOcrLanguageIndex = IndexOfString(OcrLanguageValues, s.ReaderOcrLanguage, 0);
        ReaderTranslationProviderIndex = IndexOfString(TranslationProviderValues, s.ReaderTranslationProvider, 0);
        ReaderTranslationAutoFallback = s.ReaderTranslationAutoFallback;
        ReaderInlineTranslationStyleIndex = IndexOfString(InlineTranslationStyleValues, s.ReaderInlineTranslationStyle, 0);
        ReaderAutoTranslateOnPageTurn = s.ReaderAutoTranslateOnPageTurn;
        TranslationGlossaryRules = s.TranslationGlossaryRules ?? string.Empty;
        EnableLocalSakuraModel = s.EnableLocalSakuraModel;
        UseHfMirrorForModelDownload = s.UseHfMirrorForModelDownload;
        LocalLlamaServerPath = s.LocalLlamaServerPath ?? string.Empty;
        LocalSakuraModelPath = s.LocalSakuraModelPath ?? string.Empty;
        LocalLlamaGpuLayers = s.LocalLlamaGpuLayers;
        LocalLlamaPort = s.LocalLlamaPort is > 1024 and < 65535 ? s.LocalLlamaPort : 8766;
        LocalLlamaBackendIndex = IndexOfString(LlamaBackendValues, s.LocalLlamaBackend, 0);
        CustomTranslationEndpoint = s.CustomTranslationEndpoint ?? string.Empty;
        CustomTranslationModel = string.IsNullOrWhiteSpace(s.CustomTranslationModel)
            ? "gpt-4o-mini"
            : s.CustomTranslationModel;
        DeepSeekEndpoint = string.IsNullOrWhiteSpace(s.DeepSeekEndpoint)
            ? MangaTranslationPipeline.DefaultDeepSeekEndpoint
            : s.DeepSeekEndpoint;
        DeepSeekModel = string.IsNullOrWhiteSpace(s.DeepSeekModel)
            ? MangaTranslationPipeline.DefaultDeepSeekModel
            : s.DeepSeekModel;
        DeepSeekReasoningEffortIndex = IndexOfString(
            DeepSeekReasoningEffortValues,
            MangaTranslationPipeline.NormalizeReasoningEffort(s.DeepSeekReasoningEffort),
            0);
        RefreshCustomApiKeyStatus(s);
        RefreshDeepSeekApiKeyStatus(s);
        if (deferHeavyIo)
        {
            RefreshTranslationProviderStatusFromMemory();
        }
        else
        {
            RefreshOcrAndLocalModelStatus();
        }

        ThumbnailMemoryBudgetMb = s.ThumbnailMemoryBudgetMb;
        ThumbnailDiskBudgetMb = s.ThumbnailDiskBudgetMb;
        ReaderCacheBudgetMb = s.ReaderCacheBudgetMb;
        MaxHistoryCount = s.MaxHistoryCount;

        UiThemeIndex = UiDesignThemeRegistry.IndexOf(s.UiTheme);
        UiThemeId = UiDesignThemeRegistry.Resolve(s.UiTheme).Metadata.Id;
        ThemeIndex = s.Theme;
        UseMica = s.UseMica;
        CollapseNavigationDrawer = s.CollapseNavigationDrawer;
        AutoExpandSidebarOnHover = s.AutoExpandSidebarOnHover;
        RememberWindowPlacement = s.RememberWindowPlacement;
        MinimizeToTray = s.MinimizeToTray;
        ShowNotifications = s.ShowNotifications;
        UseWindowsHello = s.UseWindowsHello;
        EnableBossKey = s.EnableBossKey;
        BossKeyGestureIndex = BossKeyBinding.IndexOfPreset(s.BossKeyGesture);
        RequireUnlockOnBossRestore = s.RequireUnlockOnBossRestore;
        StealthTrayTooltip = s.StealthTrayTooltip;
        AutoLockMinutesIndex = IndexOfInt([.. BossKeyBinding.AutoLockMinuteOptions], s.AutoLockMinutes, fallback: 0);
        RefreshPinStatus(s);

        CheckUpdateOnLaunch = s.CheckUpdateOnLaunch;
        UpdateFeedUrl = s.UpdateFeedUrl ?? string.Empty;

        ConfigDirectory = _services.Paths.RootDirectory;
        InvalidateSummary();
        StatusMessage = string.Empty;
        ErrorMessage = string.Empty;
    }

    public HashSet<string> GetExcludedLanguageSelection()
    {
        var cfg = new EhConfig { ExcludedLanguages = ExcludedLanguages };
        return cfg.GetExcludedLanguageSet();
    }

    public void UpdateExcludedLanguages(IEnumerable<string> codes)
    {
        var cfg = new EhConfig();
        cfg.SetExcludedLanguageSet(codes ?? []);
        ExcludedLanguages = cfg.ExcludedLanguages;
        UpdateExclusionSummaries();
    }

    public void UpdateExcludedNamespaces(int mask)
    {
        ExcludedNamespaces = mask & 0xFF;
        UpdateExclusionSummaries();
    }

    public void ApplyUConfigToCookie()
    {
        DesktopSettings draft = BuildSnapshotFromFields();
        _services.GetRequiredService<UConfigSyncService>().ApplySettings(draft);
        UConfigStatusMessage = "已将当前偏好写入本地 uconfig Cookie。";
    }

    public bool PullUConfigFromCookie()
    {
        DesktopSettings draft = BuildSnapshotFromFields();
        bool found = _services.GetRequiredService<UConfigSyncService>().PullFromCookieStore(draft);
        if (!found)
        {
            UConfigStatusMessage = "本地 Cookie 库尚未包含 uconfig。";
            return false;
        }

        ExcludedLanguages = draft.ExcludedLanguages;
        ExcludedNamespaces = draft.ExcludedNamespaces;
        FavoritesSortIndex = IndexOfString(FavoritesSortValues, draft.FavoritesSortOrder, 0);
        CommentsSortIndex = IndexOfString(CommentsSortValues, draft.CommentsSortOrder, 0);
        EnablePopularOnHome = draft.EnablePopularOnHome;
        UpdateExclusionSummaries();
        UConfigStatusMessage = "已从本地 Cookie 回填排除语言/命名空间与排序设置（请按保存使其持久化）。";
        return true;
    }

    public async Task SyncUConfigOnlineAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = string.Empty;
        UConfigStatusMessage = "正在与站点同步 uconfig.php …";

        try
        {
            DesktopSettings draft = BuildSnapshotFromFields();
            await _services.GetRequiredService<UConfigSyncService>()
                .SyncOnlineAsync(_services.GetRequiredService<EhEngine>(), draft, cancellationToken)
                .ConfigureAwait(true);

            ExcludedLanguages = draft.ExcludedLanguages;
            ExcludedNamespaces = draft.ExcludedNamespaces;
            FavoritesSortIndex = IndexOfString(FavoritesSortValues, draft.FavoritesSortOrder, 0);
            CommentsSortIndex = IndexOfString(CommentsSortValues, draft.CommentsSortOrder, 0);
            EnablePopularOnHome = draft.EnablePopularOnHome;
            UpdateExclusionSummaries();
            UConfigStatusMessage = "已完成站点 uconfig.php 同步。";
        }
        catch (Exception ex)
        {
            UConfigStatusMessage = $"站点同步失败：{GalleryBrowseService.DescribeFailure(ex)}";
        }
    }

    public void ClearCustomDownloadDirectory()
    {
        CustomDownloadDirectory = string.Empty;
        EffectiveDownloadDirectory = _services.Paths.DownloadDirectory;
    }

    // --- Custom Hosts database operations ---
    public IReadOnlyList<HostEntry> GetHostEntries() =>
        _services.GetRequiredService<HostsDatabase>().GetAll();

    public bool AddOrUpdateHost(string host, string ip, out string message)
    {
        var db = _services.GetRequiredService<HostsDatabase>();
        string trimmedHost = (host ?? string.Empty).Trim();
        string trimmedIp = (ip ?? string.Empty).Trim();
        if (!HostsDatabase.IsValidHost(trimmedHost))
        {
            message = "主机名格式无效。";
            return false;
        }

        if (!HostsDatabase.IsValidIp(trimmedIp))
        {
            message = "IP 地址格式无效（支持 IPv4 / IPv6）。";
            return false;
        }

        if (!db.Put(trimmedHost, trimmedIp))
        {
            message = "保存自定义 Host 失败。";
            return false;
        }

        message = $"已保存：{trimmedHost} -> {trimmedIp}";
        return true;
    }

    public void DeleteHost(string host) =>
        _services.GetRequiredService<HostsDatabase>().Delete(host);

    // --- Comment Blacklist operations ---
    public IReadOnlyList<BlackListEntry> GetBlackListEntries() =>
        _services.GetRequiredService<EhDatabase>().GetAllBlackList();

    public bool AddBlackListEntry(string badGuyName, string reason, out string message)
    {
        string name = (badGuyName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
        {
            message = "请填写要屏蔽的用户名。";
            return false;
        }

        var db = _services.GetRequiredService<EhDatabase>();
        if (db.InBlackList(name))
        {
            message = $"用户「{name}」已在评论黑名单中。";
            return false;
        }

        db.InsertBlackList(new BlackListEntry
        {
            BadGayName = name,
            Reason = string.IsNullOrWhiteSpace(reason) ? "手动屏蔽" : reason.Trim(),
            AngryWith = string.Empty,
            AddTime = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            Mode = 1
        });
        message = $"已将「{name}」加入评论黑名单。";
        return true;
    }

    public void DeleteBlackListEntry(BlackListEntry entry) =>
        _services.GetRequiredService<EhDatabase>().DeleteBlackList(entry);

    // --- Security PIN / Windows Hello ---
    public bool SetSecurityPin(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin) || pin.Trim().Length < 4)
        {
            ErrorMessage = "安全锁 PIN 至少需要 4 位字符。";
            return false;
        }

        string protectedHash = WindowsAppLockService.CreateProtectedPinHash(pin.Trim());
        _services.UpdateSettings(s => s.SecurityPinHash = protectedHash);
        UpdatePinLockProperties(hasPin: true, helloEnabled: UseWindowsHello || _services.Settings.UseWindowsHello);
        InvalidateSummary();
        ErrorMessage = string.Empty;
        StatusMessage = "安全锁 PIN 已通过 DPAPI 加密保存。";
        SettingsApplied?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ClearSecurityPin(bool disableWindowsHello = true)
    {
        _services.UpdateSettings(s =>
        {
            s.SecurityPinHash = null;
            if (disableWindowsHello)
            {
                s.UseWindowsHello = false;
            }
        });

        if (disableWindowsHello && UseWindowsHello)
        {
            bool prevSuppress = _suppressAutoSave;
            _suppressAutoSave = true;
            try
            {
                UseWindowsHello = false;
            }
            finally
            {
                _suppressAutoSave = prevSuppress;
            }
        }

        DesktopSettings current = _services.Settings;
        bool helloActive = UseWindowsHello || current.UseWindowsHello;
        UpdatePinLockProperties(hasPin: false, helloEnabled: helloActive);
        InvalidateSummary();
        ErrorMessage = string.Empty;
        StatusMessage = helloActive
            ? "已清除本地安全锁 PIN（当前仍保留 Windows Hello 验证）。"
            : "已清除应用安全锁 PIN 并停用安全锁验证。";
        SettingsApplied?.Invoke(this, EventArgs.Empty);
    }

    // --- OCR & Anti-Refusal Translation Helpers ---
    public void SaveCustomTranslationApiKey(string? rawApiKey)
    {
        if (string.IsNullOrWhiteSpace(rawApiKey))
        {
            _services.UpdateSettings(s => s.CustomTranslationApiKeyProtected = null);
        }
        else
        {
            string protectedValue = DpapiCredentialProtector.Instance.Protect(rawApiKey.Trim());
            _services.UpdateSettings(s => s.CustomTranslationApiKeyProtected = protectedValue);
        }

        _services.GetRequiredService<MangaTranslationPipeline>().ClearCache();
        RefreshCustomApiKeyStatus(_services.Settings);
        LocalTranslationStatusMessage = string.IsNullOrWhiteSpace(rawApiKey)
            ? "已清除自定义接口 API Key。"
            : "自定义接口 API Key 已通过 Windows DPAPI 加密保存。";
    }

    public void SaveDeepSeekApiKey(string? plainKey)
    {
        if (string.IsNullOrWhiteSpace(plainKey))
        {
            _services.UpdateSettings(s => s.DeepSeekApiKeyProtected = null);
        }
        else
        {
            string protectedValue = DpapiCredentialProtector.Instance.Protect(plainKey.Trim());
            _services.UpdateSettings(s => s.DeepSeekApiKeyProtected = protectedValue);
        }

        _services.GetRequiredService<MangaTranslationPipeline>().ClearCache();
        RefreshDeepSeekApiKeyStatus(_services.Settings);
        LocalTranslationStatusMessage = string.IsNullOrWhiteSpace(plainKey)
            ? "已清除 DeepSeek API Key。"
            : "DeepSeek API Key 已通过 Windows DPAPI 加密保存。";
    }

    public void ClearDeepSeekApiKey() => SaveDeepSeekApiKey(null);

    private bool UsesLocalSakuraStatus() =>
        !(ReaderTranslationProviderIndex == 3 ||
          ReaderTranslationProviderIndex == 2 ||
          (ReaderTranslationProviderIndex == 0 && !EnableLocalSakuraModel));

    private static string? TryBuildNonLocalProviderStatusMessage(
        int providerIndex,
        bool enableLocalSakura,
        string deepSeekEndpoint,
        string deepSeekModel,
        int deepSeekReasoningEffortIndex,
        bool hasDeepSeekKey,
        string customEndpoint,
        string customModel,
        string customApiKeyStatus)
    {
        if (providerIndex == 3)
        {
            string endpoint = string.IsNullOrWhiteSpace(deepSeekEndpoint)
                ? MangaTranslationPipeline.DefaultDeepSeekEndpoint
                : deepSeekEndpoint.Trim();
            string model = string.IsNullOrWhiteSpace(deepSeekModel)
                ? MangaTranslationPipeline.DefaultDeepSeekModel
                : deepSeekModel.Trim();
            string effort = DeepSeekReasoningEffortValues[
                Math.Clamp(deepSeekReasoningEffortIndex, 0, DeepSeekReasoningEffortValues.Length - 1)];
            bool requiresKey = Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? dsUri) &&
                               dsUri.Host.EndsWith("deepseek.com", StringComparison.OrdinalIgnoreCase);

            return !hasDeepSeekKey && requiresKey
                ? $"DeepSeek AI 尚未配置 API Key（当前模型：{model} · 推理强度：{effort} · 请填写密钥后即可单页连续翻译）"
                : $"DeepSeek AI 已就绪（模型：{model} · 推理强度：{effort} · 单页上下文连续翻译）";
        }

        if (providerIndex == 2)
        {
            return string.IsNullOrWhiteSpace(customEndpoint)
                ? "自定义 OpenAI 接口尚未填写 Base URL 接口地址"
                : $"自定义 OpenAI 接口已配置（模型：{customModel} · {customApiKeyStatus}）";
        }

        if (providerIndex == 0 && !enableLocalSakura)
        {
            return "传统直译已就绪（免配置 · 多源防限流 · 零拒答）";
        }

        return null;
    }

    public void RefreshTranslationProviderStatusFromMemory()
    {
        string? nonLocal = TryBuildNonLocalProviderStatusMessage(
            ReaderTranslationProviderIndex,
            EnableLocalSakuraModel,
            DeepSeekEndpoint,
            DeepSeekModel,
            DeepSeekReasoningEffortIndex,
            !string.IsNullOrWhiteSpace(_services.Settings.DeepSeekApiKeyProtected),
            CustomTranslationEndpoint,
            CustomTranslationModel,
            CustomTranslationApiKeyStatus);

        LocalTranslationStatusMessage = nonLocal ?? "正在检测本地 Sakura-7B 模型与 llama-server 状态…";
    }

    public void RefreshOcrAndLocalModelStatus() =>
        RefreshOcrAndLocalModelStatus(preserveStatusMessage: false);

    public void RefreshOcrAndLocalModelStatus(bool preserveStatusMessage)
    {
        DesktopSettings draft = BuildSnapshotFromFields();
        OcrStatusProbeResult probe = ProbeOcrAndLocalModelStatus(draft, CancellationToken.None);
        ApplyOcrAndLocalModelStatus(probe, preserveStatusMessage);
    }

    public async Task RefreshOcrAndLocalModelStatusAsync(
        Action<Action>? dispatchToUi = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DesktopSettings draft = BuildSnapshotFromFields();

        if (dispatchToUi is null)
        {
            OcrStatusProbeResult probe = await Task.Run(
                () => ProbeOcrAndLocalModelStatus(draft, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyOcrAndLocalModelStatus(probe, preserveStatusMessage: false);
            return;
        }

        OcrStatusProbeResult probed = await Task.Run(
            () => ProbeOcrAndLocalModelStatus(draft, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        dispatchToUi(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ApplyOcrAndLocalModelStatus(probed, preserveStatusMessage: false);
            }
        });
    }

    private sealed record OcrStatusProbeResult(
        bool HasMangaOnnx,
        bool HasBubbleDetector,
        bool HasLegacyV1,
        IReadOnlyList<string> InstalledLanguageTags,
        bool HasJapanesePack,
        string ModelPath,
        bool ModelExists,
        long ModelSizeMb,
        bool ServerExists,
        bool IsServerRunning,
        int ActivePort,
        int PersistentCacheCount);

    private OcrStatusProbeResult ProbeOcrAndLocalModelStatus(
        DesktopSettings draft,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ComicBubbleOcrPipeline comicOcr = _services.GetRequiredService<ComicBubbleOcrPipeline>();
        bool hasMangaOnnx = comicOcr.AreAllModelsReady;
        bool hasBubbleDetector = comicOcr.AreDetectorModelsReady;
        bool hasLegacyV1 = comicOcr.HasLegacyV1Models;

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> installed = MangaOcrService.GetAvailableLanguageTags();
        bool hasJa = MangaOcrService.IsJapanesePackInstalled();

        cancellationToken.ThrowIfCancellationRequested();
        LlamaCppServerManager manager = _services.GetRequiredService<LlamaCppServerManager>();
        string modelPath = manager.ResolveModelPath(draft);
        string? serverPath = manager.ResolveServerExecutablePath(draft);
        bool isServerRunning = manager.IsServerRunning;
        int activePort = manager.ActivePort;
        bool modelExists = File.Exists(modelPath);
        long modelSizeMb = modelExists ? new FileInfo(modelPath).Length / (1024 * 1024) : 0;
        bool serverExists = !string.IsNullOrWhiteSpace(serverPath) && File.Exists(serverPath);

        cancellationToken.ThrowIfCancellationRequested();
        int cacheCount = _services.GetRequiredService<MangaTranslationPipeline>().PersistentCacheCount;

        return new OcrStatusProbeResult(
            hasMangaOnnx,
            hasBubbleDetector,
            hasLegacyV1,
            installed,
            hasJa,
            modelPath,
            modelExists,
            modelSizeMb,
            serverExists,
            isServerRunning,
            activePort,
            cacheCount);
    }

    private void ApplyOcrAndLocalModelStatus(OcrStatusProbeResult probe, bool preserveStatusMessage)
    {
        IsMangaOcrMissing = !probe.HasMangaOnnx;
        MangaOcrModelStatus = probe.HasMangaOnnx
            ? "漫画专用离线 OCR 已就绪（RT-DETR 气泡检测 + Manga-OCR CPT-v2 完整版，支持竖排/振假名/艺术字手写，零显存占用）"
            : probe.HasLegacyV1
                ? "检测到旧版 Manga-OCR 模型，请点击下方按钮升级至高精度 CPT-v2 引擎（艺术字/手写准确率大幅提升，仅需下载约 118 MB）"
                : probe.HasBubbleDetector
                    ? "气泡检测器 (RT-DETR) 已就绪，仍需下载 Manga-OCR CPT-v2 竖排日语识别模型（约 118 MB）"
                    : "未下载漫画专用离线 OCR 模型（约 161 MB，免系统日语包，精准识别竖排日文与特殊字体气泡块）";

        IsJapaneseOcrMissing = !probe.HasJapanesePack && !probe.HasMangaOnnx;
        OcrLanguagePackStatus = probe.InstalledLanguageTags.Count == 0
            ? (probe.HasMangaOnnx
                ? "已启用 Manga-OCR CPT-v2 ONNX 离线日语识字；如需英文/中文请安装对应系统 OCR 语言包。"
                : "未检测到任何 Windows 离线 OCR 语言包，推荐下载下方漫画专用 OCR 模型或安装系统语言包。")
            : probe.HasJapanesePack
                ? $"系统 OCR 语言包已就绪（{string.Join(", ", probe.InstalledLanguageTags)}）"
                : probe.HasMangaOnnx
                    ? $"已启用 Manga-OCR CPT-v2 ONNX 日语引擎（无需系统日语包；系统语言包：{string.Join(", ", probe.InstalledLanguageTags)}）"
                    : $"未检测到 Windows 日语 OCR 包，推荐下载下方「漫画专用 OCR 模型」或安装系统日语包（当前：{string.Join(", ", probe.InstalledLanguageTags)}）";

        IsLlamaServerRunning = probe.IsServerRunning;

        if (!preserveStatusMessage)
        {
            string? nonLocal = TryBuildNonLocalProviderStatusMessage(
                ReaderTranslationProviderIndex,
                EnableLocalSakuraModel,
                DeepSeekEndpoint,
                DeepSeekModel,
                DeepSeekReasoningEffortIndex,
                !string.IsNullOrWhiteSpace(_services.Settings.DeepSeekApiKeyProtected),
                CustomTranslationEndpoint,
                CustomTranslationModel,
                CustomTranslationApiKeyStatus);

            if (nonLocal is not null)
            {
                LocalTranslationStatusMessage = nonLocal;
            }
            else
            {
                string modelState = probe.ModelExists
                    ? $"Sakura-7B 模型就绪 ({probe.ModelSizeMb} MB)"
                    : $"Sakura-7B 模型未下载（默认路径：{probe.ModelPath}）";
                string serverState = probe.ServerExists
                    ? "llama-server 就绪"
                    : "llama-server.exe 未安装";
                string runState = probe.IsServerRunning ? $"运行中 (:{probe.ActivePort})" : "未启动";
                LocalTranslationStatusMessage = $"{modelState} · {serverState} · 服务状态：{runState}";
            }
        }

        TranslationCacheCountText = $"已持久化缓存 {probe.PersistentCacheCount} 条译文 (SQLite)";
    }

    public void ClearPersistentTranslationCache()
    {
        _services.GetRequiredService<MangaTranslationPipeline>().ClearCache();
        RefreshOcrAndLocalModelStatus();
        StatusMessage = "已清空本地离线译文缓存。";
    }

    public async Task DownloadMangaOcrModelsAsync(CancellationToken cancellationToken = default)
    {
        if (IsModelDownloading)
        {
            return;
        }

        ComicBubbleOcrPipeline comicOcr = _services.GetRequiredService<ComicBubbleOcrPipeline>();
        IsModelDownloading = true;
        LocalModelDownloadProgress = 0;
        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            LocalModelDownloadProgress = p.Percentage;
            string sizeInfo = p.TotalBytes is > 0
                ? $"{p.BytesReceived / (1024 * 1024)} / {p.TotalBytes.Value / (1024 * 1024)} MB ({p.Percentage:F1}%)"
                : $"{p.BytesReceived / (1024 * 1024)} MB";
            MangaOcrModelStatus = $"{p.StatusText} {sizeInfo}";
        });

        try
        {
            await comicOcr
                .DownloadModelsAsync(UseHfMirrorForModelDownload, progress, cancellationToken)
                .ConfigureAwait(true);
            _services.GetRequiredService<MangaOcrService>().ClearCache();
            RefreshOcrAndLocalModelStatus();
            StatusMessage = "漫画专用离线 OCR 模型（RT-DETR + Manga-OCR CPT-v2）已准备就绪。";
        }
        catch (OperationCanceledException)
        {
            RefreshOcrAndLocalModelStatus();
            StatusMessage = "漫画 OCR 模型下载已取消（支持断点续传）。";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"漫画 OCR 模型下载失败：{ex.Message}";
            MangaOcrModelStatus = $"漫画 OCR 模型下载失败：{ex.Message}";
        }
        finally
        {
            IsModelDownloading = false;
        }
    }

    public async Task DownloadSakuraModelAsync(CancellationToken cancellationToken = default)
    {
        if (IsModelDownloading)
        {
            return;
        }

        LlamaCppServerManager manager = _services.GetRequiredService<LlamaCppServerManager>();
        DesktopSettings draft = BuildSnapshotFromFields();
        string targetPath = manager.ResolveModelPath(draft);

        IsModelDownloading = true;
        LocalModelDownloadProgress = 0;
        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            LocalModelDownloadProgress = p.Percentage;
            string sizeInfo = p.TotalBytes is > 0
                ? $"{p.BytesReceived / (1024 * 1024)} / {p.TotalBytes.Value / (1024 * 1024)} MB ({p.Percentage:F1}%)"
                : $"{p.BytesReceived / (1024 * 1024)} MB";
            LocalTranslationStatusMessage = $"{p.StatusText} {sizeInfo}";
        });

        try
        {
            string saved = await manager
                .DownloadSakuraModelAsync(UseHfMirrorForModelDownload, targetPath, progress, cancellationToken)
                .ConfigureAwait(true);
            LocalSakuraModelPath = saved;
            RefreshOcrAndLocalModelStatus();
            StatusMessage = $"Sakura-7B 模型已下载到：{saved}";
        }
        catch (OperationCanceledException)
        {
            LocalTranslationStatusMessage = "Sakura-7B 模型下载已取消（支持断点续传）。";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Sakura-7B 模型下载失败：{ex.Message}";
            LocalTranslationStatusMessage = $"模型下载失败：{ex.Message}";
        }
        finally
        {
            IsModelDownloading = false;
        }
    }

    public async Task DownloadLlamaCppRuntimeAsync(CancellationToken cancellationToken = default)
    {
        if (IsModelDownloading)
        {
            return;
        }

        LlamaCppServerManager manager = _services.GetRequiredService<LlamaCppServerManager>();
        string backend = LlamaBackendValues[Math.Clamp(LocalLlamaBackendIndex, 0, LlamaBackendValues.Length - 1)];

        IsModelDownloading = true;
        LocalModelDownloadProgress = 0;
        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            LocalModelDownloadProgress = p.Percentage;
            string sizeInfo = p.TotalBytes is > 0
                ? $"{p.BytesReceived / (1024 * 1024)} / {p.TotalBytes.Value / (1024 * 1024)} MB ({p.Percentage:F1}%)"
                : $"{p.BytesReceived / (1024 * 1024)} MB";
            LocalTranslationStatusMessage = $"{p.StatusText} {sizeInfo}";
        });

        try
        {
            string exePath = await manager
                .DownloadLlamaCppRuntimeAsync(backend, progress, cancellationToken)
                .ConfigureAwait(true);
            LocalLlamaServerPath = exePath;
            RefreshOcrAndLocalModelStatus();
            StatusMessage = $"llama-server.exe 已准备就绪：{exePath}";
        }
        catch (OperationCanceledException)
        {
            LocalTranslationStatusMessage = "llama.cpp 运行时下载已取消。";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"llama.cpp 下载失败：{ex.Message}";
            LocalTranslationStatusMessage = $"llama.cpp 下载失败：{ex.Message}";
        }
        finally
        {
            IsModelDownloading = false;
        }
    }

    public async Task ToggleLocalLlamaServerAsync(CancellationToken cancellationToken = default)
    {
        LlamaCppServerManager manager = _services.GetRequiredService<LlamaCppServerManager>();
        if (manager.IsServerRunning)
        {
            manager.StopServer();
            RefreshOcrAndLocalModelStatus();
            return;
        }

        Save();
        LocalTranslationStatusMessage = "正在启动本地 llama-server (Sakura-7B)...";
        try
        {
            Uri uri = await manager.EnsureRunningAsync(_services.Settings, cancellationToken).ConfigureAwait(true);
            RefreshOcrAndLocalModelStatus();
            StatusMessage = $"本地 Sakura-7B 推理服务已在 {uri} 启动就绪。";
        }
        catch (Exception ex)
        {
            RefreshOcrAndLocalModelStatus();
            ErrorMessage = $"启动本地推理服务失败：{ex.Message}";
            LocalTranslationStatusMessage = $"启动失败：{ex.Message}";
        }
    }

    public async Task TestTranslationPipelineAsync(CancellationToken cancellationToken = default)
    {
        if (IsTestingTranslation)
        {
            return;
        }

        IsTestingTranslation = true;
        Save();
        MangaTranslationPipeline pipeline = _services.GetRequiredService<MangaTranslationPipeline>();
        LocalTranslationStatusMessage = ReaderTranslationProviderIndex == 3
            ? "正在执行 DeepSeek 单页连续试译（2 气泡阅读流 · 测试低推理与防拒答回退链）..."
            : "正在执行试译（测试当前主引擎与防拒答回退链）...";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (ReaderTranslationProviderIndex == 3)
            {
                var sampleBlocks = new OcrTextBlock[]
                {
                    new("test-1", "ちょっと待って！", 0.55, 0.15, 0.25, 0.25, IsVertical: true),
                    new("test-2", "そんなのダメだってば！", 0.15, 0.20, 0.25, 0.25, IsVertical: true)
                };
                IReadOnlyDictionary<string, TranslationResult> batch = await pipeline
                    .TranslatePageBlocksAsync(sampleBlocks, bypassCache: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(true);
                sw.Stop();
                TranslationResult r1 = batch["test-1"];
                TranslationResult r2 = batch["test-2"];
                string note = r1.UsedFallback && !string.IsNullOrWhiteSpace(r1.FallbackNote)
                    ? $"（{r1.FallbackNote}）"
                    : string.Empty;
                LocalTranslationStatusMessage =
                    $"单页连续试译成功 [{r1.ProviderDisplayName} · {sw.ElapsedMilliseconds}ms]{note}：[1] {r1.TranslatedText} / [2] {r2.TranslatedText}";
            }
            else
            {
                TranslationResult result = await pipeline
                    .TranslateAsync("ちょっと待って！そんなのダメだってば！", providerOverride: null, bypassCache: true, cancellationToken)
                    .ConfigureAwait(true);
                sw.Stop();
                string note = result.UsedFallback && !string.IsNullOrWhiteSpace(result.FallbackNote)
                    ? $"（{result.FallbackNote}）"
                    : string.Empty;
                LocalTranslationStatusMessage =
                    $"试译成功 [{result.ProviderDisplayName} · {sw.ElapsedMilliseconds}ms]{note}：{result.TranslatedText}";
            }

            RefreshOcrAndLocalModelStatus(preserveStatusMessage: true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"试译失败：{ex.Message}";
            LocalTranslationStatusMessage = $"试译失败：{ex.Message}";
        }
        finally
        {
            IsTestingTranslation = false;
        }
    }

    private void RefreshCustomApiKeyStatus(DesktopSettings s) =>
        CustomTranslationApiKeyStatus = BuildApiKeyStatus(s.CustomTranslationApiKeyProtected, "未设置自定义 API Key");

    private void RefreshDeepSeekApiKeyStatus(DesktopSettings s) =>
        DeepSeekApiKeyStatus = BuildApiKeyStatus(s.DeepSeekApiKeyProtected, "未设置 DeepSeek API Key");

    private static string BuildApiKeyStatus(string? protectedValue, string emptyLabel) =>
        string.IsNullOrWhiteSpace(protectedValue)
            ? emptyLabel
            : "已通过 DPAPI 加密保存 API Key";

    // --- Updates ---
    public static string GetCurrentAppVersion()
    {
        Version? v = typeof(SettingsViewModel).Assembly.GetName().Version;
        return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
    }

    public static int GetCurrentVersionCode()
    {
        Version? v = typeof(SettingsViewModel).Assembly.GetName().Version;
        if (v is null)
        {
            return 10000;
        }

        return Math.Max(1, v.Major * 10000 + Math.Max(0, v.Minor) * 100 + Math.Max(0, v.Build));
    }

    public async Task<AppUpdateCheckResult?> CheckForUpdatesAsync(
        Func<string, CancellationToken, Task<string>>? fetchJsonAsync = null,
        CancellationToken cancellationToken = default)
    {
        string feedUrl = string.IsNullOrWhiteSpace(UpdateFeedUrl)
            ? new DesktopSettings().UpdateFeedUrl
            : UpdateFeedUrl.Trim();

        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            UpdateStatusMessage = "更新源 URL 无效。";
            return null;
        }

        UpdateStatusMessage = "正在检查更新…";
        try
        {
            string json;
            if (fetchJsonAsync is not null)
            {
                json = await fetchJsonAsync(feedUrl, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                HttpClient client = _services.GetRequiredService<EhHttpClientFactory>().StandardClient;
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.UserAgent.Add(new ProductInfoHeaderValue("EhViewer.Desktop", GetCurrentAppVersion()));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using HttpResponseMessage resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(true);
                resp.EnsureSuccessStatusCode();
                json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
            }

            AppUpdateCheckResult result = AppUpdateHelper.ParseAndCheck(
                json,
                GetCurrentAppVersion(),
                GetCurrentVersionCode());
            LatestUpdateInfo = result.Latest;

            if (result.HasUpdate && result.Latest is not null)
            {
                UpdateStatusMessage = $"发现新版本 {result.Latest.Version}：{result.Latest.Title ?? "可用更新"}";
            }
            else
            {
                string current = GetCurrentAppVersion();
                UpdateStatusMessage = result.Latest is null
                    ? $"未能解析更新源数据（当前版本 {current}）。"
                    : $"已是最新版本 {current}（远端版本 {result.Latest.Version}）。";
            }

            return result;
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = $"检查更新失败：{ex.Message}";
            return null;
        }
    }

    /// <summary>Validates and persists the edited values.</summary>
    public bool Save()
    {
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;

        var proxyType = (EhProxyType)Math.Clamp(ProxyTypeIndex, 0, 3);

        if (proxyType is EhProxyType.Http or EhProxyType.Socks)
        {
            if (!InetValidator.IsValidInetAddress(ProxyIp) && !InetValidator.IsValidIpv4(ProxyIp))
            {
                ErrorMessage = "代理地址无效：请填写 IP 或主机名。";
                return false;
            }

            if (!InetValidator.IsValidInetPort(ProxyPort))
            {
                ErrorMessage = "代理端口无效：应为 0–65535。";
                return false;
            }
        }

        if (UseDnsOverHttps)
        {
            if (!Uri.TryCreate(DohEndpoint, UriKind.Absolute, out Uri? dohUri))
            {
                ErrorMessage = "DoH 端点必须是合法的绝对地址。";
                return false;
            }

            if (dohUri.Scheme != Uri.UriSchemeHttps)
            {
                ErrorMessage = "DoH 端点必须使用 https。";
                return false;
            }
        }

        if (ThumbnailMemoryBudgetMb < 0 || ThumbnailDiskBudgetMb < 0 || ReaderCacheBudgetMb < 0)
        {
            ErrorMessage = "缓存预算不能为负数。";
            return false;
        }

        if (DownloadConcurrency is < 1 or > 16)
        {
            ErrorMessage = "下载并发数应在 1–16 之间。";
            return false;
        }

        if (DownloadTimeoutSeconds is < 5 or > 600)
        {
            ErrorMessage = "下载单文件超时时间应在 5–600 秒之间。";
            return false;
        }

        if (PreloadCount is < 1 or > 10)
        {
            ErrorMessage = "阅读器预加载页数应在 1–10 之间。";
            return false;
        }

        if (MaxHistoryCount is < 0 or > 100000)
        {
            ErrorMessage = "历史条数上限应在 0–100000 之间。";
            return false;
        }

        try
        {
            _suppressAutoSave = true;
            _services.UpdateSettings(ApplyToSettings);
            _services.GetRequiredService<UConfigSyncService>().ApplySettings(_services.Settings);
            EffectiveDownloadDirectory = _services.Paths.GetEffectiveDownloadDirectory(_services.Settings);
            InvalidateSummary();
            StatusMessage = "设置已实时保存并同步到 uconfig Cookie。";
            SettingsApplied?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"保存失败：{ex.Message}";
            return false;
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    /// <summary>Syncs the navigation drawer collapsed toggle when toggled directly from the main shell rail.</summary>
    public void SyncNavigationDrawerCollapsed(bool collapsed)
    {
        bool prevSuppress = _suppressAutoSave;
        _suppressAutoSave = true;
        try
        {
            CollapseNavigationDrawer = collapsed;
        }
        finally
        {
            _suppressAutoSave = prevSuppress;
        }
    }

    partial void OnUiThemeIndexChanged(int value)
    {
        if (AvailableUiThemes.Count == 0)
        {
            return;
        }

        int clamped = Math.Clamp(value, 0, AvailableUiThemes.Count - 1);
        string resolvedId = AvailableUiThemes[clamped].Id;
        if (!string.Equals(UiThemeId, resolvedId, StringComparison.OrdinalIgnoreCase))
        {
            UiThemeId = resolvedId;
        }
    }

    partial void OnUiThemeIdChanged(string value)
    {
        int idx = UiDesignThemeRegistry.IndexOf(value);
        if (UiThemeIndex != idx)
        {
            UiThemeIndex = idx;
        }
    }

    /// <summary>Syncs the theme selector index when changed directly from the main shell theme slider.</summary>
    public void SyncThemeIndex(int theme)
    {
        bool prevSuppress = _suppressAutoSave;
        _suppressAutoSave = true;
        try
        {
            ThemeIndex = theme;
        }
        finally
        {
            _suppressAutoSave = prevSuppress;
        }
    }

    private DesktopSettings BuildSnapshotFromFields()
    {
        var snapshot = new DesktopSettings();
        ApplyToSettings(snapshot);
        return snapshot;
    }

    private void ApplyToSettings(DesktopSettings s)
    {
        var proxyType = (EhProxyType)Math.Clamp(ProxyTypeIndex, 0, 3);
        s.GallerySite = UseExHentai ? EhSite.Ex : EhSite.E;
        s.ShowJpnTitle = ShowJpnTitle;
        s.ShowGalleryPages = ShowGalleryPages;
        s.ShowHoverPreview = ShowHoverPreview;
        s.ListPageSize = ListPageSizeValues[Math.Clamp(ListPageSizeIndex, 0, ListPageSizeValues.Length - 1)];
        s.ThumbResolution = Math.Clamp(ThumbResolutionIndex, 0, 2);
        s.FixThumbUrl = FixThumbUrl;
        s.ImageResolution = ImageResolutionValues[Math.Clamp(ImageResolutionIndex, 0, ImageResolutionValues.Length - 1)];
        s.SaveParseErrorBody = SaveParseErrorBody;

        s.ExcludedLanguages = ExcludedLanguages ?? string.Empty;
        s.ExcludedNamespaces = ExcludedNamespaces & 0xFF;
        s.FavoritesSortOrder = FavoritesSortValues[Math.Clamp(FavoritesSortIndex, 0, FavoritesSortValues.Length - 1)];
        s.CommentsSortOrder = CommentsSortValues[Math.Clamp(CommentsSortIndex, 0, CommentsSortValues.Length - 1)];
        s.EnablePopularOnHome = EnablePopularOnHome;

        s.ProxyType = proxyType;
        s.ProxyIp = string.IsNullOrWhiteSpace(ProxyIp) ? null : ProxyIp.Trim();
        s.ProxyPort = ProxyPort;
        s.BuiltInHosts = BuiltInHosts;
        s.BuiltInExHosts = BuiltInExHosts;
        s.DomainFronting = DomainFronting;
        s.UseDnsOverHttps = UseDnsOverHttps;
        s.DohEndpoint = DohEndpoint.Trim();

        s.CustomDownloadDirectory = string.IsNullOrWhiteSpace(CustomDownloadDirectory) ? null : CustomDownloadDirectory.Trim();
        s.DownloadConcurrency = DownloadConcurrency;
        s.DownloadTimeoutSeconds = DownloadTimeoutSeconds;
        s.DownloadPauseOnBattery = DownloadPauseOnBattery;

        s.PreloadCount = PreloadCount;
        s.DefaultReaderLayout = Math.Clamp(DefaultReaderLayoutIndex, 0, 2);
        s.DefaultReaderScale = Math.Clamp(DefaultReaderScaleIndex, 0, 3);
        s.MaxDecodeEdge = MaxDecodeEdgeValues[Math.Clamp(MaxDecodeEdgeIndex, 0, MaxDecodeEdgeValues.Length - 1)];
        s.DualPageMode = DualPageMode;
        s.DualPageFirstAlone = DualPageFirstAlone;
        s.ContinuousScroll = ContinuousScroll;
        s.ForceSrgb = ForceSrgb;
        s.ShowPageNumber = ShowPageNumber;
        s.KeepScreenOn = KeepScreenOn;
        s.PageTurnByWheel = PageTurnByWheel;
        s.HideReaderChromeInFullscreen = HideReaderChromeInFullscreen;

        s.ReaderOcrLanguage = OcrLanguageValues[Math.Clamp(ReaderOcrLanguageIndex, 0, OcrLanguageValues.Length - 1)];
        s.ReaderTranslationProvider = TranslationProviderValues[Math.Clamp(ReaderTranslationProviderIndex, 0, TranslationProviderValues.Length - 1)];
        s.ReaderTranslationAutoFallback = ReaderTranslationAutoFallback;
        s.ReaderInlineTranslationStyle = InlineTranslationStyleValues[Math.Clamp(ReaderInlineTranslationStyleIndex, 0, InlineTranslationStyleValues.Length - 1)];
        s.ReaderAutoTranslateOnPageTurn = ReaderAutoTranslateOnPageTurn;
        s.TranslationGlossaryRules = string.IsNullOrWhiteSpace(TranslationGlossaryRules) ? string.Empty : TranslationGlossaryRules.ReplaceLineEndings("\n").Trim();
        s.EnableLocalSakuraModel = EnableLocalSakuraModel;
        s.UseHfMirrorForModelDownload = UseHfMirrorForModelDownload;
        s.LocalLlamaServerPath = string.IsNullOrWhiteSpace(LocalLlamaServerPath) ? null : LocalLlamaServerPath.Trim();
        s.LocalSakuraModelPath = string.IsNullOrWhiteSpace(LocalSakuraModelPath) ? null : LocalSakuraModelPath.Trim();
        s.LocalLlamaGpuLayers = Math.Clamp(LocalLlamaGpuLayers, 0, 999);
        s.LocalLlamaPort = Math.Clamp(LocalLlamaPort is > 1024 and < 65535 ? LocalLlamaPort : 8766, 1025, 65534);
        s.LocalLlamaBackend = LlamaBackendValues[Math.Clamp(LocalLlamaBackendIndex, 0, LlamaBackendValues.Length - 1)];
        s.CustomTranslationEndpoint = string.IsNullOrWhiteSpace(CustomTranslationEndpoint) ? null : CustomTranslationEndpoint.Trim();
        s.CustomTranslationModel = string.IsNullOrWhiteSpace(CustomTranslationModel) ? "gpt-4o-mini" : CustomTranslationModel.Trim();
        s.DeepSeekEndpoint = string.IsNullOrWhiteSpace(DeepSeekEndpoint)
            ? MangaTranslationPipeline.DefaultDeepSeekEndpoint
            : DeepSeekEndpoint.Trim();
        s.DeepSeekModel = string.IsNullOrWhiteSpace(DeepSeekModel)
            ? MangaTranslationPipeline.DefaultDeepSeekModel
            : DeepSeekModel.Trim();
        s.DeepSeekReasoningEffort = DeepSeekReasoningEffortValues[
            Math.Clamp(DeepSeekReasoningEffortIndex, 0, DeepSeekReasoningEffortValues.Length - 1)];

        s.ThumbnailMemoryBudgetMb = ThumbnailMemoryBudgetMb;
        s.ThumbnailDiskBudgetMb = ThumbnailDiskBudgetMb;
        s.ReaderCacheBudgetMb = ReaderCacheBudgetMb;
        s.MaxHistoryCount = MaxHistoryCount;

        s.UiTheme = UiDesignThemeRegistry.Resolve(UiThemeId).Metadata.Id;
        s.Theme = ThemeIndex;
        s.UseMica = UseMica;
        s.CollapseNavigationDrawer = CollapseNavigationDrawer;
        s.AutoExpandSidebarOnHover = AutoExpandSidebarOnHover;
        s.RememberWindowPlacement = RememberWindowPlacement;
        s.MinimizeToTray = MinimizeToTray;
        s.ShowNotifications = ShowNotifications;
        s.UseWindowsHello = UseWindowsHello;
        s.EnableBossKey = EnableBossKey;
        s.BossKeyGesture = BossKeyBinding.GestureAt(BossKeyGestureIndex);
        s.RequireUnlockOnBossRestore = RequireUnlockOnBossRestore;
        s.StealthTrayTooltip = StealthTrayTooltip;
        IReadOnlyList<int> lockOptions = BossKeyBinding.AutoLockMinuteOptions;
        s.AutoLockMinutes = AutoLockMinutesIndex >= 0 && AutoLockMinutesIndex < lockOptions.Count
            ? lockOptions[AutoLockMinutesIndex]
            : 0;
        s.CheckUpdateOnLaunch = CheckUpdateOnLaunch;
        s.UpdateFeedUrl = string.IsNullOrWhiteSpace(UpdateFeedUrl)
            ? new DesktopSettings().UpdateFeedUrl
            : UpdateFeedUrl.Trim();
    }

    private void UpdateExclusionSummaries()
    {
        HashSet<string> langs = GetExcludedLanguageSelection();
        ExcludedLanguagesSummary = langs.Count == 0
            ? "未排除任何语言"
            : $"已排除 {langs.Count} 项语言版本 ({ExcludedLanguages})";

        var names = new List<string>();
        foreach (ExcludedNamespaceOption opt in EhConfig.ExcludedNamespaceOptions)
        {
            if ((ExcludedNamespaces & opt.Mask) != 0)
            {
                names.Add(opt.Key);
            }
        }

        ExcludedNamespacesSummary = names.Count == 0
            ? "未排除任何命名空间"
            : $"已排除 {names.Count} 项：{string.Join(", ", names)}";
    }

    partial void OnUseWindowsHelloChanged(bool value) =>
        UpdatePinLockProperties(
            hasPin: !string.IsNullOrWhiteSpace(_services.Settings.SecurityPinHash),
            helloEnabled: value);

    private void RefreshPinStatus(DesktopSettings s) =>
        UpdatePinLockProperties(
            hasPin: !string.IsNullOrWhiteSpace(s.SecurityPinHash),
            helloEnabled: s.UseWindowsHello);

    private void UpdatePinLockProperties(bool hasPin, bool helloEnabled)
    {
        HasSecurityPin = hasPin;
        CanClearAppLock = hasPin || helloEnabled;
        SecurityPinStatus = (hasPin, helloEnabled) switch
        {
            (true, true) => "已启用 DPAPI 加密安全锁 PIN（并优先使用 Windows Hello）",
            (true, false) => "已启用 DPAPI 加密安全锁 PIN",
            (false, true) => "未设置本地 PIN（当前仍开启 Windows Hello 锁定验证）",
            (false, false) => "未设置安全锁 PIN（应用安全锁已关闭）"
        };
    }

    private static int IndexOfInt(int[] options, int target, int fallback)
    {
        int idx = Array.IndexOf(options, target);
        return idx >= 0 ? idx : fallback;
    }

    private static int IndexOfString(string[] options, string? target, int fallback)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return fallback;
        }

        for (int i = 0; i < options.Length; i++)
        {
            if (string.Equals(options[i], target, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return fallback;
    }
}

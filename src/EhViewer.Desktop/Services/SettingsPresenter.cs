using System.Text.Json;
using EhViewer.Core.Client;
using EhViewer.Core.Network;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Result of a settings round trip, used by the settings page and by tests that need to confirm
/// a value survives a save/load cycle without touching the real user profile.
/// </summary>
public sealed record SettingsSummary(
    string RootDirectory,
    bool UseMica,
    int Theme,
    string GallerySite,
    string ProxyDescription,
    bool BuiltInHosts,
    bool BuiltInExHosts,
    bool UseDnsOverHttps,
    int MaxHistoryCount,
    int ThumbnailMemoryBudgetMb,
    int ThumbnailDiskBudgetMb,
    int DownloadConcurrency);

/// <summary>
/// Read-only projections and maintenance operations over <see cref="DesktopSettings"/> so views
/// never have to know the on-disk layout.
/// </summary>
public static class SettingsPresenter
{
    public static SettingsSummary Summarize(DesktopSettings settings, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);

        return new SettingsSummary(
            paths.RootDirectory,
            settings.UseMica,
            settings.Theme,
            settings.GallerySite == EhSite.Ex ? "里站 (exhentai.org)" : "表站 (e-hentai.org)",
            DescribeProxy(settings),
            settings.BuiltInHosts,
            settings.BuiltInExHosts,
            settings.UseDnsOverHttps,
            settings.MaxHistoryCount,
            settings.ThumbnailMemoryBudgetMb,
            settings.ThumbnailDiskBudgetMb,
            settings.DownloadConcurrency);
    }

    public static string DescribeProxy(DesktopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.ProxyType switch
        {
            EhProxyType.Direct => "不使用代理",
            EhProxyType.System => "跟随系统代理",
            EhProxyType.Http when !string.IsNullOrWhiteSpace(settings.ProxyIp) =>
                $"HTTP 代理 {settings.ProxyIp}:{settings.ProxyPort}",
            EhProxyType.Socks when !string.IsNullOrWhiteSpace(settings.ProxyIp) =>
                $"SOCKS5 代理 {settings.ProxyIp}:{settings.ProxyPort}",
            EhProxyType.Http or EhProxyType.Socks => "代理已启用但地址未配置（将回退直连）",
            _ => "未知"
        };
    }

    /// <summary>Serializes the settings document for diagnostics or support tickets.</summary>
    public static string ToDiagnosticJson(DesktopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }
}

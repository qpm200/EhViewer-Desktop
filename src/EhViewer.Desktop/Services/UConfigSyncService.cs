using EhViewer.Core.Client;
using EhViewer.Core.Network;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Keeps <see cref="DesktopSettings"/> uconfig preferences aligned with the <c>uconfig</c>
/// HTTP cookie consumed by E-Hentai and ExHentai requests.
/// </summary>
public sealed class UConfigSyncService
{
    private readonly ICookieStore _cookieStore;

    public UConfigSyncService(ICookieStore cookieStore)
    {
        _cookieStore = cookieStore ?? throw new ArgumentNullException(nameof(cookieStore));
    }

    public static EhConfig BuildEhConfig(DesktopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var cfg = new EhConfig
        {
            ExcludedLanguages = settings.ExcludedLanguages ?? string.Empty,
            ExcludedNamespaces = settings.ExcludedNamespaces,
            FavoritesSort = string.IsNullOrWhiteSpace(settings.FavoritesSortOrder)
                ? EhConfig.FavoritesSortFavoritedTime
                : settings.FavoritesSortOrder,
            CommentSort = string.IsNullOrWhiteSpace(settings.CommentsSortOrder)
                ? EhConfig.CommentsSortOldestFirst
                : settings.CommentsSortOrder,
            Popular = settings.EnablePopularOnHome ? EhConfig.PopularYes : EhConfig.PopularNo,
            GalleryTitle = settings.ShowJpnTitle ? EhConfig.GalleryTitleJapanese : EhConfig.GalleryTitleDefault,
            ResultCount = MapResultCount(settings.ListPageSize),
            ImageSize = string.IsNullOrWhiteSpace(settings.ImageResolution)
                ? EhConfig.ImageSizeAuto
                : settings.ImageResolution
        };
        return cfg;
    }

    public void ApplySettings(DesktopSettings settings)
    {
        EhConfig cfg = BuildEhConfig(settings);
        EhConfigCookieSync.ApplyToCookieStore(_cookieStore, cfg);
    }

    public bool PullFromCookieStore(DesktopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!EhConfigCookieSync.TryReadFromCookieStore(_cookieStore, out string? raw))
        {
            return false;
        }

        var cfg = new EhConfig();
        cfg.ApplyUConfig(raw);
        settings.ExcludedLanguages = cfg.ExcludedLanguages;
        settings.ExcludedNamespaces = cfg.ExcludedNamespaces;
        settings.FavoritesSortOrder = cfg.FavoritesSort;
        settings.CommentsSortOrder = cfg.CommentSort;
        settings.EnablePopularOnHome = string.Equals(cfg.Popular, EhConfig.PopularYes, StringComparison.Ordinal);
        return true;
    }

    /// <summary>
    /// Applies current local <see cref="DesktopSettings"/> to the <c>uconfig</c> cookie jar,
    /// performs an authenticated request to <c>uconfig.php</c> on the active site, and updates
    /// any server-returned configuration flags back into <paramref name="settings"/>.
    /// </summary>
    public async Task<bool> SyncOnlineAsync(
        EhEngine engine,
        DesktopSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(settings);

        ApplySettings(settings);
        await engine.GetUConfigAsync(settings.GallerySite, cancellationToken).ConfigureAwait(false);
        return PullFromCookieStore(settings);
    }

    private static string MapResultCount(int pageSize) => pageSize switch
    {
        >= 200 => EhConfig.ResultCount200,
        >= 100 => EhConfig.ResultCount100,
        >= 50 => EhConfig.ResultCount50,
        _ => EhConfig.ResultCount25
    };
}

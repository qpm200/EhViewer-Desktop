using System.Net.Http;
using EhViewer.Core.Client;
using EhViewer.Core.Network;
using EhViewer.Core.Security;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Services.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Composes the application object graph. Every Core service is registered as a singleton because
/// the SQLite connections they own are single-instance by design (see <c>EhDatabase</c>,
/// <c>SqliteCookieStore</c>, and <c>HostsDatabase</c>).
/// </summary>
public sealed class AppServices : IDisposable, IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private AppServices(ServiceProvider provider, AppPaths paths, DesktopSettingsStore settingsStore)
    {
        _provider = provider;
        Paths = paths;
        SettingsStore = settingsStore;
    }

    public AppPaths Paths { get; }
    public DesktopSettingsStore SettingsStore { get; }
    public IServiceProvider Services => _provider;

    public DesktopSettings Settings => _provider.GetRequiredService<DesktopSettingsHolder>().Current;

    /// <summary>Called after a settings mutation so dependent subsystems can react.</summary>
    public void UpdateSettings(Action<DesktopSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        DesktopSettingsHolder holder = _provider.GetRequiredService<DesktopSettingsHolder>();
        holder.Mutate(mutate);
        SettingsStore.Save(holder.Current);
    }

    public static AppServices Create(AppPaths paths, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (loggerFactory is null)
        {
            loggerFactory = LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.Information));
        }

        paths.EnsureCreated();

        var settingsStore = new DesktopSettingsStore(paths.SettingsFilePath);
        DesktopSettings initialSettings = settingsStore.Load();

        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddLogging();

        services.AddSingleton(paths);
        services.AddSingleton(settingsStore);
        services.AddSingleton(new DesktopSettingsHolder(initialSettings));

        services.AddSingleton<DesktopClientSettings>(static sp =>
        {
            DesktopSettingsHolder holder = sp.GetRequiredService<DesktopSettingsHolder>();
            return new DesktopClientSettings(() => holder.Current, sp.GetRequiredService<AppPaths>());
        });
        services.AddSingleton<IEhClientSettings>(static sp => sp.GetRequiredService<DesktopClientSettings>());

        services.AddSingleton<ICredentialProtector>(_ => DpapiCredentialProtector.Instance);

        services.AddSingleton(static sp => new HostsDatabase(sp.GetRequiredService<AppPaths>().HostsDatabasePath));
        services.AddSingleton(static sp => new SqliteCookieStore(
            sp.GetRequiredService<AppPaths>().CookieDatabasePath,
            sp.GetRequiredService<ICredentialProtector>()));
        services.AddSingleton<ICookieStore>(static sp => sp.GetRequiredService<SqliteCookieStore>());

        services.AddSingleton(static sp => new EhDatabase(
            sp.GetRequiredService<AppPaths>().EhDatabasePath,
            sp.GetRequiredService<DesktopSettingsHolder>().Current.MaxHistoryCount));

        // Upstream SearchDatabase: search-suggestion history in its own SQLite file.
        services.AddSingleton(static sp => new SearchHistoryDatabase(
            sp.GetRequiredService<AppPaths>().SearchDatabasePath));

        services.AddSingleton(static sp => new EhFilter(sp.GetRequiredService<EhDatabase>()));
        services.AddSingleton(static sp => new EhHosts(
            sp.GetRequiredService<HostsDatabase>(),
            sp.GetRequiredService<IEhClientSettings>()));

        services.AddSingleton(static sp => new EhHttpClientFactory(
            sp.GetRequiredService<ICookieStore>(),
            sp.GetRequiredService<EhHosts>(),
            sp.GetRequiredService<IEhClientSettings>()));

        services.AddSingleton(static sp => new EhEngine(
            sp.GetRequiredService<EhHttpClientFactory>(),
            sp.GetRequiredService<ICookieStore>(),
            sp.GetRequiredService<EhDatabase>(),
            sp.GetRequiredService<EhFilter>(),
            sp.GetRequiredService<IEhClientSettings>()));

        services.AddSingleton(static sp => new ThumbnailDiskCache(
            sp.GetRequiredService<AppPaths>().ThumbnailCacheDirectory,
            sp.GetRequiredService<DesktopSettingsHolder>().Current.ThumbnailDiskBudgetMb));

        services.AddSingleton<ThumbnailService>();
        services.AddSingleton<GalleryBrowseService>();
        services.AddSingleton<UConfigSyncService>();
        services.AddSingleton<TagTranslationSource>();
        services.AddSingleton<EhViewer.Desktop.ViewModels.ShellViewModel>();

        // Reader: one cache/engine/fetcher per app; the reader view model rebinds per gallery.
        services.AddSingleton(sp => new EhViewer.Desktop.Reader.ReaderPageCache(
            sp.GetRequiredService<DesktopSettingsHolder>().Current.ReaderCacheBudgetMb * 1024L * 1024L));
        services.AddSingleton(sp => new EhViewer.Desktop.Reader.ReaderImageFetcher(
            sp.GetRequiredService<EhEngine>(),
            sp.GetRequiredService<EhHttpClientFactory>(),
            sp.GetRequiredService<ILogger<EhViewer.Desktop.Reader.ReaderImageFetcher>>()));
        services.AddSingleton(sp => new EhViewer.Desktop.Reader.ReaderEngine(
            sp.GetRequiredService<EhViewer.Desktop.Reader.ReaderImageFetcher>().FetchAsync,
            sp.GetRequiredService<EhViewer.Desktop.Reader.ReaderPageCache>(),
            sp.GetRequiredService<ILogger<EhViewer.Desktop.Reader.ReaderEngine>>()));

        // Downloads.
        services.AddSingleton(sp => new EhViewer.Desktop.Downloads.DownloadManager(
            sp.GetRequiredService<EhEngine>(),
            sp.GetRequiredService<EhHttpClientFactory>(),
            sp.GetRequiredService<EhDatabase>(),
            sp.GetRequiredService<AppPaths>(),
            sp.GetRequiredService<ILogger<EhViewer.Desktop.Downloads.DownloadManager>>(),
            sp.GetRequiredService<DesktopSettingsHolder>().Current.DownloadConcurrency,
            sp.GetRequiredService<IEhClientSettings>(),
            () => sp.GetRequiredService<AppPaths>().GetEffectiveDownloadDirectory(sp.GetRequiredService<DesktopSettingsHolder>().Current),
            () => sp.GetRequiredService<DesktopSettingsHolder>().Current));
        services.AddSingleton<EhViewer.Desktop.Downloads.LocalLibraryScanner>();
        services.AddSingleton(sp => new EhViewer.Desktop.Downloads.ArchiverDownloadManager(
            () => sp.GetRequiredService<EhHttpClientFactory>().StandardClient,
            () => sp.GetRequiredService<AppPaths>().GetEffectiveDownloadDirectory(sp.GetRequiredService<DesktopSettingsHolder>().Current),
            sp.GetRequiredService<EhViewer.Desktop.Downloads.LocalLibraryScanner>(),
            sp.GetRequiredService<ILogger<EhViewer.Desktop.Downloads.ArchiverDownloadManager>>()));
        services.AddSingleton(sp => new EhViewer.Desktop.Downloads.AndroidMigrationService(
            sp.GetRequiredService<EhDatabase>(),
            sp.GetRequiredService<SqliteCookieStore>(),
            sp.GetRequiredService<AppPaths>(),
            sp.GetRequiredService<ILogger<EhViewer.Desktop.Downloads.AndroidMigrationService>>(),
            () => sp.GetRequiredService<AppPaths>().GetEffectiveDownloadDirectory(sp.GetRequiredService<DesktopSettingsHolder>().Current)));
        services.AddSingleton<MaintenanceService>();
        services.AddSingleton<WindowsTaskbarProgress>();
        services.AddSingleton<WindowsTrayIcon>();
        services.AddSingleton(sp => new WindowsNotifications(
            sp.GetRequiredService<WindowsTrayIcon>(),
            () => sp.GetRequiredService<DesktopSettingsHolder>().Current,
            sp.GetRequiredService<ILogger<WindowsNotifications>>()));
        services.AddSingleton(sp => new ComicBubbleOcrPipeline(
            sp.GetRequiredService<AppPaths>(),
            logger: sp.GetRequiredService<ILogger<ComicBubbleOcrPipeline>>()));
        services.AddSingleton(sp => new MangaOcrService(
            sp.GetRequiredService<ComicBubbleOcrPipeline>(),
            sp.GetRequiredService<ILogger<MangaOcrService>>()));
        services.AddSingleton(sp => new LlamaCppServerManager(
            sp.GetRequiredService<AppPaths>(),
            logger: sp.GetRequiredService<ILogger<LlamaCppServerManager>>()));
        services.AddSingleton(sp => new TranslationCacheDatabase(
            sp.GetRequiredService<AppPaths>().TranslationCacheDatabasePath));
        services.AddSingleton(sp => new MangaTranslationPipeline(
            sp.GetRequiredService<LlamaCppServerManager>(),
            () => sp.GetRequiredService<DesktopSettingsHolder>().Current,
            sp.GetRequiredService<ICredentialProtector>(),
            persistentCache: sp.GetRequiredService<TranslationCacheDatabase>(),
            logger: sp.GetRequiredService<ILogger<MangaTranslationPipeline>>()));
        services.AddTransient(CreateReaderViewModel);
        services.AddSingleton(sp => new WindowManager(
            sp.GetRequiredService<EhViewer.Desktop.ViewModels.ReaderViewModel>,
            sp.GetRequiredService<DesktopSettingsHolder>(),
            sp.GetRequiredService<ILogger<WindowManager>>()));

        ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        return new AppServices(provider, paths, settingsStore);
    }

    public T GetRequiredService<T>() where T : notnull => _provider.GetRequiredService<T>();

    /// <summary>
    /// Creates an isolated <see cref="ViewModels.ReaderViewModel"/> paired with its own scoped
    /// engine/fetcher while sharing the app-wide <see cref="Reader.ReaderPageCache"/> memory budget.
    /// </summary>
    public ViewModels.ReaderViewModel CreateReaderViewModel() => CreateReaderViewModel(_provider);

    private static ViewModels.ReaderViewModel CreateReaderViewModel(IServiceProvider sp)
    {
        var fetcher = new EhViewer.Desktop.Reader.ReaderImageFetcher(
            sp.GetRequiredService<EhEngine>(),
            sp.GetRequiredService<EhHttpClientFactory>(),
            sp.GetRequiredService<ILogger<EhViewer.Desktop.Reader.ReaderImageFetcher>>());

        var engine = new EhViewer.Desktop.Reader.ReaderEngine(
            fetcher.FetchAsync,
            sp.GetRequiredService<EhViewer.Desktop.Reader.ReaderPageCache>(),
            sp.GetRequiredService<ILogger<EhViewer.Desktop.Reader.ReaderEngine>>());

        return new ViewModels.ReaderViewModel(
            engine,
            fetcher,
            sp.GetRequiredService<GalleryBrowseService>(),
            sp.GetRequiredService<IEhClientSettings>(),
            sp.GetRequiredService<AppPaths>(),
            sp.GetRequiredService<ILogger<ViewModels.ReaderViewModel>>(),
            ownsEngine: true,
            resolveDesktopSettings: () => sp.GetRequiredService<DesktopSettingsHolder>().Current,
            database: sp.GetRequiredService<EhDatabase>(),
            thumbnails: sp.GetRequiredService<ThumbnailService>(),
            ocrService: sp.GetRequiredService<MangaOcrService>(),
            translationPipeline: sp.GetRequiredService<MangaTranslationPipeline>());
    }

    public void Dispose()
    {
        try
        {
            _provider.Dispose();
        }
        catch (InvalidOperationException)
        {
            _provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}

/// <summary>
/// Holds the mutable settings document so long-lived services observe updates without
/// re-resolving them from the container.
/// </summary>
public sealed class DesktopSettingsHolder
{
    private readonly object _gate = new();
    private DesktopSettings _current;

    public DesktopSettingsHolder(DesktopSettings initial)
    {
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    public DesktopSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Mutate(Action<DesktopSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            mutate(_current);
        }
    }
}

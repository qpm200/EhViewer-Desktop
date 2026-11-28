using EhViewer.Desktop.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class AppServicesStartupTests : DesktopTestBase
{
    [Fact]
    public async Task CreateAsync_CreatesDirectoriesLoadsSettingsAndDoesNotDeadlockCustomSyncContext()
    {
        string tempRoot = CreateTempDirectory("eh-app-startup");
        AppPaths paths = AppPaths.CreateAt(tempRoot);
        Directory.CreateDirectory(paths.RootDirectory);

        var preStore = new DesktopSettingsStore(paths.SettingsFilePath);
        preStore.Save(new DesktopSettings
        {
            ShowJpnTitle = true,
            DownloadConcurrency = 5
        });

        SynchronizationContext? previousContext = SynchronizationContext.Current;
        var customContext = new NonBlockingSynchronizationContext();
        Task<AppServices> createTask;
        SynchronizationContext.SetSynchronizationContext(customContext);
        try
        {
            createTask = AppServices.CreateAsync(paths);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        using AppServices services = await createTask;

        Assert.True(Directory.Exists(paths.LogDirectory));
        Assert.True(Directory.Exists(paths.DownloadDirectory));
        Assert.True(Directory.Exists(paths.ThumbnailCacheDirectory));
        Assert.True(services.Settings.ShowJpnTitle);
        Assert.Equal(5, services.Settings.DownloadConcurrency);
        Assert.Same(paths, services.GetRequiredService<AppPaths>());
        Assert.NotNull(services.GetRequiredService<ILogger<AppServicesStartupTests>>());
        Assert.Equal(0, customContext.SendCalls);
    }

    [Fact]
    public async Task CreateAsync_WhenCancelled_ThrowsWithoutCreatingDirectories()
    {
        string tempRoot = Path.Combine(CreateTempDirectory("eh-startup-cancel"), "uncreated");
        AppPaths paths = AppPaths.CreateAt(tempRoot);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppServices.CreateAsync(paths, cancellationToken: cts.Token));

        Assert.False(Directory.Exists(paths.RootDirectory));
    }

    [Fact]
    public async Task LoggerFactoryOwnership_ExternalFactoryIsNotDisposed_ContainerOwnedFactoryIsDisposed()
    {
        string externalRoot = CreateTempDirectory("eh-app-startup");
        string ownedRoot = CreateTempDirectory("eh-app-startup");
        using var externalFactory = new TrackingLoggerFactory();
        using (AppServices withExternal = await AppServices.CreateAsync(AppPaths.CreateAt(externalRoot), externalFactory))
        {
            Assert.Same(externalFactory, withExternal.GetRequiredService<ILoggerFactory>());
        }

        Assert.Equal(0, externalFactory.DisposeCount);

        var spyProvider = new TrackingLoggerProvider();
        using (AppServices withOwned = await AppServices.CreateAsync(AppPaths.CreateAt(ownedRoot)))
        {
            ILoggerFactory ownedFactory = withOwned.GetRequiredService<ILoggerFactory>();
            ownedFactory.AddProvider(spyProvider);
            Assert.NotNull(ownedFactory.CreateLogger<AppServicesStartupTests>());
        }

        Assert.Equal(1, spyProvider.DisposeCount);
    }

    [Fact]
    public async Task DisposeAndDisposeAsync_AreIdempotentAcrossRepeatedAndMixedCalls()
    {
        string syncRoot = CreateTempDirectory("eh-app-startup");
        string asyncRoot = CreateTempDirectory("eh-app-startup");
        var syncSpy = new TrackingLoggerProvider();
        AppServices syncServices = AppServices.Create(AppPaths.CreateAt(syncRoot));
        syncServices.GetRequiredService<ILoggerFactory>().AddProvider(syncSpy);

        syncServices.Dispose();
        syncServices.Dispose();
        await syncServices.DisposeAsync();
        Assert.Equal(1, syncSpy.DisposeCount);

        var asyncSpy = new TrackingLoggerProvider();
        AppServices asyncServices = await AppServices.CreateAsync(AppPaths.CreateAt(asyncRoot));
        asyncServices.GetRequiredService<ILoggerFactory>().AddProvider(asyncSpy);

        await asyncServices.DisposeAsync();
        asyncServices.Dispose();
        await asyncServices.DisposeAsync();
        Assert.Equal(1, asyncSpy.DisposeCount);
    }

    [Fact]
    public void AppXamlCs_UsesAsyncColdStartAndPreservesP0CrashAndDisposalInvariants()
    {
        string appCsPath = ResolveDesktopSourceFile("App.xaml.cs");

        Assert.True(File.Exists(appCsPath), $"Missing {appCsPath}");
        string source = File.ReadAllText(appCsPath);

        Assert.Contains("protected override async void OnLaunched", source, StringComparison.Ordinal);
        Assert.Contains("AppServices.CreateAsync", source, StringComparison.Ordinal);
        Assert.Contains("ConfigureAwait(true)", source, StringComparison.Ordinal);
        Assert.Contains("AppDomain.CurrentDomain.UnhandledException +=", source, StringComparison.Ordinal);
        Assert.Contains("TaskScheduler.UnobservedTaskException +=", source, StringComparison.Ordinal);
        Assert.Contains("AppendCrashLog", source, StringComparison.Ordinal);
        Assert.Contains("DisposeServicesSafely()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllText", source, StringComparison.Ordinal);
    }

    private sealed class NonBlockingSynchronizationContext : SynchronizationContext
    {
        private int _sendCalls;

        public int SendCalls => Volatile.Read(ref _sendCalls);

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _sendCalls);
            throw new InvalidOperationException("CreateAsync must not synchronously marshal onto caller SynchronizationContext.");
        }

        public override void Post(SendOrPostCallback d, object? state)
            => ThreadPool.QueueUserWorkItem(static s =>
            {
                var (callback, arg) = ((SendOrPostCallback, object?))s!;
                callback(arg);
            }, (d, state));
    }

    private sealed class TrackingLoggerFactory : ILoggerFactory
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class TrackingLoggerProvider : ILoggerProvider
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}

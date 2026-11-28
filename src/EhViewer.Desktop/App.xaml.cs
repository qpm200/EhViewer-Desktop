using System.Text;
using EhViewer.Desktop.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace EhViewer.Desktop;

public partial class App : Application
{
    private const long MaxCrashLogBytes = 5L * 1024 * 1024;
    private static readonly object CrashLogGate = new();

    private Window? _window;
    private AppServices? _services;
    private int _servicesDisposed;

    public App()
    {
        InitializeComponent();

        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>Composed service graph, available once <see cref="OnLaunched"/> has run.</summary>
    public AppServices? Services => _services;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            AppPaths paths = AppPaths.CreateDefault();
            _services = await AppServices.CreateAsync(paths).ConfigureAwait(true);

            if (_services.SettingsStore.LastLoadWarning is { } warning)
            {
                _services.GetRequiredService<ILogger<App>>().LogWarning("{Warning}", warning);
            }

            _window = new MainWindow(_services);
            _window.Closed += OnMainWindowClosed;
            _window.Activate();
        }
        catch (Exception ex)
        {
            // Startup failures must not leave an invisible process. Write the failure to disk
            // (the process may have no console), release any partially initialized services,
            // and rethrow so WER captures a dump.
            WriteStartupFailure(ex);
            DisposeServicesSafely();
            throw;
        }
    }

    /// <summary>
    /// Thread-safely appends a crash or startup failure record to <paramref name="logFilePath"/>,
    /// creating missing parent directories and rotating the file when it exceeds 5 MB.
    /// </summary>
    internal static void AppendCrashLog(string logFilePath, string category, Exception? exception, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);

        lock (CrashLogGate)
        {
            string? directory = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(logFilePath))
            {
                var info = new FileInfo(logFilePath);
                if (info.Length > MaxCrashLogBytes)
                {
                    File.Move(logFilePath, logFilePath + ".old", overwrite: true);
                }
            }

            var sb = new StringBuilder(512);
            sb.Append(DateTime.UtcNow.ToString("O"));
            sb.Append(" [");
            sb.Append(string.IsNullOrWhiteSpace(category) ? "Unhandled" : category);
            sb.Append(']');
            if (!string.IsNullOrWhiteSpace(detail))
            {
                sb.Append(' ');
                sb.Append(detail);
            }

            sb.AppendLine();
            if (exception is not null)
            {
                sb.AppendLine(exception.ToString());
            }

            sb.AppendLine("----------------------------------------");
            File.AppendAllText(logFilePath, sb.ToString(), Encoding.UTF8);
        }
    }

    /// <summary>
    /// Records a startup failure to <c>logs\startup-error.log</c> under <see cref="AppPaths.CreateDefault"/>.
    /// </summary>
    private static void WriteStartupFailure(Exception ex)
    {
        try
        {
            string logPath = Path.Combine(AppPaths.CreateDefault().LogDirectory, "startup-error.log");
            AppendCrashLog(logPath, "StartupFailure", ex);
        }
        catch
        {
            // Diagnostics must never mask the original failure.
        }
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        DisposeServicesSafely();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        TryLogError(e.Exception, "未处理的 UI 异常");
        try
        {
            AppendCrashLog(GetLogPath("ui-error.log"), "UI.UnhandledException", e.Exception, e.Message);
        }
        catch
        {
        }
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        Exception? exception = e.ExceptionObject as Exception;
        string detail = exception is null
            ? $"IsTerminating={e.IsTerminating}; NonException={e.ExceptionObject}"
            : $"IsTerminating={e.IsTerminating}";

        TryLogError(exception, "AppDomain 未处理异常");
        try
        {
            AppendCrashLog(GetLogPath("crash.log"), "AppDomain.UnhandledException", exception, detail);
        }
        catch
        {
        }

        if (e.IsTerminating)
        {
            DisposeServicesSafely();
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        TryLogError(e.Exception, "未观察到的后台任务异常");
        try
        {
            AppendCrashLog(GetLogPath("crash.log"), "TaskScheduler.UnobservedTaskException", e.Exception);
        }
        catch
        {
        }
        finally
        {
            e.SetObserved();
        }
    }

    private string GetLogPath(string fileName)
    {
        string root = (_services?.Paths ?? AppPaths.CreateDefault()).LogDirectory;
        return Path.Combine(root, fileName);
    }

    private void TryLogError(Exception? exception, string message)
    {
        try
        {
            _services?.GetRequiredService<ILogger<App>>().LogError(exception, "{Message}", message);
        }
        catch
        {
        }
    }

    private void DisposeServicesSafely()
    {
        if (Interlocked.Exchange(ref _servicesDisposed, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        AppServices? services = Interlocked.Exchange(ref _services, null);
        if (services is null)
        {
            return;
        }

        try
        {
            services.Dispose();
        }
        catch (Exception ex)
        {
            try
            {
                AppendCrashLog(Path.Combine(services.Paths.LogDirectory, "crash.log"), "App.DisposeServicesSafely", ex);
            }
            catch
            {
            }
        }
    }
}

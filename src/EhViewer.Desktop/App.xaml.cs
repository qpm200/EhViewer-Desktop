using EhViewer.Desktop.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace EhViewer.Desktop;

public partial class App : Application
{
    private Window? _window;
    private AppServices? _services;

    public App()
    {
        InitializeComponent();

        UnhandledException += OnUnhandledException;
    }

    /// <summary>Composed service graph, available once <see cref="OnLaunched"/> has run.</summary>
    public AppServices? Services => _services;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            AppPaths paths = AppPaths.CreateDefault();
            _services = AppServices.Create(paths);

            if (_services.SettingsStore.LastLoadWarning is { } warning)
            {
                _services.GetRequiredService<ILogger<App>>().LogWarning("{Warning}", warning);
            }

            _window = new MainWindow(_services);
            _window.Activate();
        }
        catch (Exception ex)
        {
            // Startup failures must not leave an invisible process. Write the failure to disk
            // (the process may have no console) and rethrow so WER captures a dump.
            WriteStartupFailure(ex);
            throw;
        }
    }

    /// <summary>
    /// Records a startup failure to <c>logs\startup-error.log</c> under <see cref="AppPaths.CreateDefault"/>.
    /// </summary>
    private static void WriteStartupFailure(Exception ex)
    {
        try
        {
            string root = AppPaths.CreateDefault().LogDirectory;
            Directory.CreateDirectory(root);

            File.WriteAllText(
                Path.Combine(root, "startup-error.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never mask the original failure.
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _services?.GetRequiredService<ILogger<App>>().LogError(e.Exception, "未处理的 UI 异常");
        try
        {
            string root = (_services?.Paths ?? AppPaths.CreateDefault()).LogDirectory;
            Directory.CreateDirectory(root);
            File.AppendAllText(
                Path.Combine(root, "ui-error.log"),
                $"{DateTimeOffset.Now:O} {e.Message}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

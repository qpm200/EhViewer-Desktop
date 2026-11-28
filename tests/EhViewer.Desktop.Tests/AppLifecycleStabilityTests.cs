using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class AppLifecycleStabilityTests : DesktopTestBase
{
    [Fact]
    public void AppendCrashLog_CreatesMissingDirectoryAndAppendsWithoutOverwriting()
    {
        string tempRoot = CreateTempDirectory("eh-app-lifecycle");
        string logPath = Path.Combine(tempRoot, "nested", "logs", "startup-error.log");

        App.AppendCrashLog(logPath, "StartupFailure", new InvalidOperationException("first-boom"), "phase-1");
        App.AppendCrashLog(logPath, "AppDomain.UnhandledException", new ArgumentException("second-boom"), "IsTerminating=true");

        Assert.True(File.Exists(logPath));
        string content = File.ReadAllText(logPath);

        int firstIdx = content.IndexOf("first-boom", StringComparison.Ordinal);
        int secondIdx = content.IndexOf("second-boom", StringComparison.Ordinal);

        Assert.True(firstIdx >= 0, "First crash entry was overwritten or missing.");
        Assert.True(secondIdx > firstIdx, "Second crash entry was not appended after first entry.");
        Assert.Contains("[StartupFailure]", content, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", content, StringComparison.Ordinal);
        Assert.Contains("phase-1", content, StringComparison.Ordinal);
        Assert.Contains("[AppDomain.UnhandledException]", content, StringComparison.Ordinal);
        Assert.Contains("ArgumentException", content, StringComparison.Ordinal);
        Assert.Contains("IsTerminating=true", content, StringComparison.Ordinal);
        Assert.Contains("Z [StartupFailure]", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppendCrashLog_SupportsConcurrentWritesWithoutLosingEntries()
    {
        string tempRoot = CreateTempDirectory("eh-app-lifecycle");
        string logPath = Path.Combine(tempRoot, "crash.log");

        const int writers = 8;
        Task[] tasks = Enumerable.Range(0, writers)
            .Select(i => Task.Run(() =>
                App.AppendCrashLog(logPath, "ConcurrentTest", new Exception($"entry-{i}"), $"detail-{i}")))
            .ToArray();

        await Task.WhenAll(tasks);

        string content = File.ReadAllText(logPath);
        for (int i = 0; i < writers; i++)
        {
            Assert.Contains($"entry-{i}", content, StringComparison.Ordinal);
            Assert.Contains($"detail-{i}", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AppXamlCs_StaticInvariants_EnforceGlobalExceptionHandlersAppendLogAndServiceDisposal()
    {
        string appCsPath = ResolveDesktopSourceFile("App.xaml.cs");

        Assert.True(File.Exists(appCsPath), $"Missing {appCsPath}");
        string source = File.ReadAllText(appCsPath);

        Assert.Contains("AppDomain.CurrentDomain.UnhandledException +=", source, StringComparison.Ordinal);
        Assert.Contains("TaskScheduler.UnobservedTaskException +=", source, StringComparison.Ordinal);
        Assert.Contains("e.SetObserved()", source, StringComparison.Ordinal);
        Assert.Contains("DisposeServicesSafely()", source, StringComparison.Ordinal);
        Assert.Contains("AppendCrashLog", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllText", source, StringComparison.Ordinal);
    }
}

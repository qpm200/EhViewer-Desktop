using System.Text.Json;
using System.Xml.Linq;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Translation;
using EhViewer.Desktop.ViewModels;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class Wave4SettingsPerfTests : IDisposable
{
    private readonly string _root;

    public Wave4SettingsPerfTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "EhViewerWave4SettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void Summary_IsLazyCachedAndRefreshesAfterAutoSaveChanges()
    {
        string appDir = Path.Combine(_root, "lazy-summary");
        using var services = AppServices.Create(AppPaths.CreateAt(appDir));
        var vm = new SettingsViewModel(services, deferHeavyIo: true);

        int summaryChangedCount = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.Summary))
            {
                summaryChangedCount++;
            }
        };

        vm.AutoSaveOnChange = true;
        vm.ShowJpnTitle = true;
        vm.MaxHistoryCount = 321;
        vm.ThemeIndex = 2;

        Assert.Equal(3, summaryChangedCount);

        string firstRead = vm.Summary;
        string secondRead = vm.Summary;
        Assert.Same(firstRead, secondRead);

        using (JsonDocument doc1 = JsonDocument.Parse(firstRead))
        {
            Assert.True(doc1.RootElement.GetProperty("showJpnTitle").GetBoolean());
            Assert.Equal(321, doc1.RootElement.GetProperty("maxHistoryCount").GetInt32());
            Assert.Equal(2, doc1.RootElement.GetProperty("theme").GetInt32());
        }

        vm.MaxHistoryCount = 777;
        Assert.Equal(4, summaryChangedCount);

        string thirdRead = vm.Summary;
        Assert.NotSame(firstRead, thirdRead);

        using (JsonDocument doc2 = JsonDocument.Parse(thirdRead))
        {
            Assert.Equal(777, doc2.RootElement.GetProperty("maxHistoryCount").GetInt32());
        }

        Assert.True(vm.SetSecurityPin("1234"));
        Assert.Equal(5, summaryChangedCount);
        using (JsonDocument doc3 = JsonDocument.Parse(vm.Summary))
        {
            Assert.False(string.IsNullOrWhiteSpace(doc3.RootElement.GetProperty("securityPinHash").GetString()));
        }

        vm.ClearSecurityPin();
        Assert.Equal(6, summaryChangedCount);
        using (JsonDocument doc4 = JsonDocument.Parse(vm.Summary))
        {
            Assert.True(
                doc4.RootElement.GetProperty("securityPinHash").ValueKind == JsonValueKind.Null ||
                string.IsNullOrEmpty(doc4.RootElement.GetProperty("securityPinHash").GetString()));
        }
    }

    [Fact]
    public async Task DeferHeavyIo_ProvidesMemoryStatusImmediatelyAndSupportsAsyncRefreshAndCancellation()
    {
        string appDir = Path.Combine(_root, "defer-heavy-io");
        AppPaths paths = AppPaths.CreateAt(appDir);
        using (AppServices seedServices = AppServices.Create(paths))
        {
            seedServices.UpdateSettings(s => s.ReaderTranslationProvider = TranslationProviderIds.DeepSeek);
        }

        using var services = AppServices.Create(paths);
        var vm = new SettingsViewModel(services, deferHeavyIo: true);

        Assert.Equal(3, vm.ReaderTranslationProviderIndex);
        Assert.Contains("DeepSeek AI 尚未配置 API Key", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);
        Assert.Equal(string.Empty, vm.MangaOcrModelStatus);
        Assert.Equal(string.Empty, vm.OcrLanguagePackStatus);

        using var cancelledCts = new CancellationTokenSource();
        cancelledCts.Cancel();
        bool cancelledDispatched = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            vm.RefreshOcrAndLocalModelStatusAsync(
                action =>
                {
                    cancelledDispatched = true;
                    action();
                },
                cancelledCts.Token));
        Assert.False(cancelledDispatched);
        Assert.Equal(string.Empty, vm.MangaOcrModelStatus);

        bool dispatched = false;
        await vm.RefreshOcrAndLocalModelStatusAsync(action =>
        {
            dispatched = true;
            action();
        });

        Assert.True(dispatched);
        Assert.False(string.IsNullOrWhiteSpace(vm.MangaOcrModelStatus));
        Assert.False(string.IsNullOrWhiteSpace(vm.OcrLanguagePackStatus));
        Assert.Contains("SQLite", vm.TranslationCacheCountText, StringComparison.Ordinal);
        Assert.Contains("DeepSeek AI 尚未配置 API Key", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);

        await vm.RefreshOcrAndLocalModelStatusAsync();
        Assert.False(string.IsNullOrWhiteSpace(vm.MangaOcrModelStatus));
    }

    [Fact]
    public void DeepSeekAndProviderChanges_UpdateStatusSynchronouslyWithoutThrowing()
    {
        string appDir = Path.Combine(_root, "provider-status-sync");
        using var services = AppServices.Create(AppPaths.CreateAt(appDir));
        var vm = new SettingsViewModel(services, deferHeavyIo: true);

        Assert.Contains("传统直译已就绪", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);

        vm.ReaderTranslationProviderIndex = 3; // DeepSeek
        Assert.Contains("DeepSeek AI 尚未配置 API Key", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);
        Assert.Contains("deepseek-chat", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);
        Assert.Contains("推理强度：low", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);

        vm.DeepSeekModel = "deepseek-reasoner";
        Assert.Contains("deepseek-reasoner", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);

        vm.DeepSeekReasoningEffortIndex = 2; // high
        Assert.Contains("推理强度：high", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);

        vm.ReaderTranslationProviderIndex = 2; // Custom OpenAI
        Assert.Contains("自定义 OpenAI 接口尚未填写", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);

        vm.ReaderTranslationProviderIndex = 1; // Local Sakura
        Assert.Contains("Sakura-7B", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);

        vm.RefreshTranslationProviderStatusFromMemory();
        Assert.False(string.IsNullOrWhiteSpace(vm.LocalTranslationStatusMessage));

        vm.ReaderTranslationProviderIndex = 0; // DirectMT
        Assert.Contains("传统直译已就绪", vm.LocalTranslationStatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPageXaml_DefersGroups1To5WithXLoadFalseAndLoadsGroup0Eagerly()
    {
        string repoRoot = LocateRepositoryRoot();
        string xamlPath = Path.Combine(repoRoot, "src", "EhViewer.Desktop", "Views", "SettingsPage.xaml");
        Assert.True(File.Exists(xamlPath), $"SettingsPage.xaml not found at {xamlPath}");

        XDocument doc = XDocument.Load(xamlPath);
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement groupEh = FindElementByXName(doc, xNs, "GroupEhPanel");
        Assert.Null(groupEh.Attribute(xNs + "Load"));

        string[] deferredGroupNames =
        [
            "GroupReadPanel",
            "GroupDownloadPanel",
            "GroupPrivacyPanel",
            "GroupAdvancedPanel",
            "GroupAboutPanel"
        ];

        foreach (string groupName in deferredGroupNames)
        {
            XElement panel = FindElementByXName(doc, xNs, groupName);
            XAttribute? loadAttr = panel.Attribute(xNs + "Load");
            Assert.NotNull(loadAttr);
            Assert.Equal("False", loadAttr!.Value);
        }
    }

    private static XElement FindElementByXName(XDocument doc, XNamespace xNs, string name)
    {
        XElement? element = doc
            .Descendants()
            .FirstOrDefault(e => string.Equals(e.Attribute(xNs + "Name")?.Value, name, StringComparison.Ordinal));
        Assert.NotNull(element);
        return element!;
    }

    private static string LocateRepositoryRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "EhViewer.Desktop.slnx")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Unable to locate EhViewer.Desktop.slnx from AppContext.BaseDirectory.");
    }
}

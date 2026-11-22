using System.Net;
using System.Text;
using EhViewer.Core.Client;
using EhViewer.Core.Network;
using EhViewer.Core.Security;
using EhViewer.Core.Storage;
using EhViewer.Core.Util;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class Batch6ConfigAndShellTests : IDisposable
{
    private readonly string _root;

    public Batch6ConfigAndShellTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "EhViewerBatch6Tests", Guid.NewGuid().ToString("N"));
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
    public void SettingsViewModel_FullRoundtrip_PersistsAllDesktopAndUConfigFields()
    {
        string appDir = Path.Combine(_root, "settings-roundtrip");
        string customDlDir = Path.Combine(_root, "custom-dl");

        using (var services = AppServices.Create(AppPaths.CreateAt(appDir)))
        {
            var vm = new SettingsViewModel(services)
            {
                UseExHentai = true,
                ShowJpnTitle = true,
                ShowGalleryPages = true,
                ShowHoverPreview = false,
                ListPageSizeIndex = 2, // 100
                ThumbResolutionIndex = 2, // 300px
                FixThumbUrl = true,
                ImageResolutionIndex = 3, // 1280px
                SaveParseErrorBody = false,

                FavoritesSortIndex = 1, // published time 'p'
                CommentsSortIndex = 2, // score 's'
                EnablePopularOnHome = false,

                BuiltInHosts = false,
                BuiltInExHosts = false,
                DomainFronting = false,
                UseDnsOverHttps = true,
                DohEndpoint = "https://dns.google/dns-query",

                CustomDownloadDirectory = customDlDir,
                DownloadConcurrency = 5,
                DownloadTimeoutSeconds = 95,
                DownloadPauseOnBattery = true,

                PreloadCount = 6,
                DefaultReaderLayoutIndex = 1, // RTL
                DefaultReaderScaleIndex = 2, // FitHeight
                MaxDecodeEdgeIndex = 3, // 6144
                DualPageMode = true,
                DualPageFirstAlone = false,
                ContinuousScroll = true,
                ForceSrgb = true,
                ShowPageNumber = false,
                KeepScreenOn = true,
                PageTurnByWheel = false,
                HideReaderChromeInFullscreen = false,
                ReaderInlineTranslationStyleIndex = 1,
                ReaderAutoTranslateOnPageTurn = true,
                TranslationGlossaryRules = "先輩=学长\nお兄ちゃん=>欧尼酱",

                ThumbnailMemoryBudgetMb = 128,
                ThumbnailDiskBudgetMb = 1024,
                ReaderCacheBudgetMb = 768,
                MaxHistoryCount = 250,

                ThemeIndex = 2,
                UseMica = false,
                RememberWindowPlacement = false,
                MinimizeToTray = true,
                ShowNotifications = false,
                UseWindowsHello = true,
                CheckUpdateOnLaunch = true,
                UpdateFeedUrl = "https://example.com/releases.json"
            };

            vm.UpdateExcludedLanguages([EhConfig.ChineseOriginal, EhConfig.JapaneseTranslated]);
            vm.UpdateExcludedNamespaces(EhConfig.NamespacesReclass | EhConfig.NamespacesFemale);

            Assert.True(vm.Save(), vm.ErrorMessage);
        }

        // Re-open fresh AppServices + SettingsViewModel from same config root
        using var reloaded = AppServices.Create(AppPaths.CreateAt(appDir));
        var vm2 = new SettingsViewModel(reloaded);

        Assert.True(vm2.UseExHentai);
        Assert.True(vm2.ShowJpnTitle);
        Assert.True(vm2.ShowGalleryPages);
        Assert.False(vm2.ShowHoverPreview);
        Assert.Equal(2, vm2.ListPageSizeIndex);
        Assert.Equal(2, vm2.ThumbResolutionIndex);
        Assert.True(vm2.FixThumbUrl);
        Assert.Equal(3, vm2.ImageResolutionIndex);
        Assert.False(vm2.SaveParseErrorBody);

        Assert.Equal(1, vm2.FavoritesSortIndex);
        Assert.Equal(2, vm2.CommentsSortIndex);
        Assert.False(vm2.EnablePopularOnHome);
        Assert.Contains(EhConfig.ChineseOriginal, vm2.GetExcludedLanguageSelection());
        Assert.Contains(EhConfig.JapaneseTranslated, vm2.GetExcludedLanguageSelection());
        Assert.Equal(EhConfig.NamespacesReclass | EhConfig.NamespacesFemale, vm2.ExcludedNamespaces);

        Assert.False(vm2.BuiltInHosts);
        Assert.False(vm2.BuiltInExHosts);
        Assert.False(vm2.DomainFronting);
        Assert.True(vm2.UseDnsOverHttps);
        Assert.Equal("https://dns.google/dns-query", vm2.DohEndpoint);

        Assert.Equal(customDlDir, vm2.CustomDownloadDirectory);
        Assert.Equal(5, vm2.DownloadConcurrency);
        Assert.Equal(95, vm2.DownloadTimeoutSeconds);
        Assert.True(vm2.DownloadPauseOnBattery);

        Assert.Equal(6, vm2.PreloadCount);
        Assert.Equal(1, vm2.DefaultReaderLayoutIndex);
        Assert.Equal(2, vm2.DefaultReaderScaleIndex);
        Assert.Equal(3, vm2.MaxDecodeEdgeIndex);
        Assert.True(vm2.DualPageMode);
        Assert.False(vm2.DualPageFirstAlone);
        Assert.True(vm2.ContinuousScroll);
        Assert.True(vm2.ForceSrgb);
        Assert.False(vm2.ShowPageNumber);
        Assert.True(vm2.KeepScreenOn);
        Assert.False(vm2.PageTurnByWheel);
        Assert.False(vm2.HideReaderChromeInFullscreen);
        Assert.Equal(1, vm2.ReaderInlineTranslationStyleIndex);
        Assert.True(vm2.ReaderAutoTranslateOnPageTurn);
        Assert.Equal("先輩=学长\nお兄ちゃん=>欧尼酱", vm2.TranslationGlossaryRules);

        Assert.Equal(128, vm2.ThumbnailMemoryBudgetMb);
        Assert.Equal(1024, vm2.ThumbnailDiskBudgetMb);
        Assert.Equal(768, vm2.ReaderCacheBudgetMb);
        Assert.Equal(250, vm2.MaxHistoryCount);

        Assert.Equal(2, vm2.ThemeIndex);
        Assert.False(vm2.UseMica);
        Assert.False(vm2.RememberWindowPlacement);
        Assert.True(vm2.MinimizeToTray);
        Assert.False(vm2.ShowNotifications);
        Assert.True(vm2.UseWindowsHello);
        Assert.True(vm2.CheckUpdateOnLaunch);
        Assert.Equal("https://example.com/releases.json", vm2.UpdateFeedUrl);

        // Save also synced uconfig cookie to cookie store.
        Assert.True(EhConfigCookieSync.TryReadFromCookieStore(reloaded.GetRequiredService<ICookieStore>(), out string? uconfig));
        Assert.False(string.IsNullOrWhiteSpace(uconfig));
        Assert.Equal(17, EhConfig.ExcludedLanguageCatalog.Count);
        Assert.Equal(8, EhConfig.ExcludedNamespaceOptions.Count);
    }

    [Fact]
    public void SettingsViewModel_AutoSaveOnChange_PersistsAndRaisesSettingsAppliedImmediately()
    {
        string appDir = Path.Combine(_root, "settings-autosave");
        using var services = AppServices.Create(AppPaths.CreateAt(appDir));
        var vm = new SettingsViewModel(services);
        int appliedCount = 0;
        vm.SettingsApplied += (_, _) => appliedCount++;
        vm.AutoSaveOnChange = true;

        vm.ShowJpnTitle = true;
        vm.ThemeIndex = 1;

        Assert.Equal(2, appliedCount);
        Assert.True(services.Settings.ShowJpnTitle);
        Assert.Equal(1, services.Settings.Theme);
    }

    [Fact]
    public async Task UConfigSyncService_OnlineSync_PushesLocalAndPullsServerCookie()
    {
        string appDir = Path.Combine(_root, "uconfig-sync");
        AppPaths paths = AppPaths.CreateAt(appDir);
        using var db = new EhDatabase(paths.EhDatabasePath);
        var filter = new EhFilter(db);
        var clientSettings = new EhClientSettings();
        using var cookies = new SqliteCookieStore(paths.CookieDatabasePath);

        var serverCfg = new EhConfig
        {
            ExcludedLanguages = "0x1025",
            ExcludedNamespaces = EhConfig.NamespacesMale,
            FavoritesSort = EhConfig.FavoritesSortGalleryUpdateTime,
            CommentSort = EhConfig.CommentsSortRecentFirst,
            Popular = EhConfig.PopularNo
        };
        string serverCookie = serverCfg.BuildUConfig();

        using var httpClient = new HttpClient(new DelegateHandler((req, _) =>
        {
            Assert.Equal(EhUrl.GetUConfigUrl(EhSite.E), req.RequestUri!.AbsoluteUri);
            Assert.Contains(cookies.GetCookies(req.RequestUri!), c => c.Name == EhConfig.KeyUconfig && !string.IsNullOrWhiteSpace(c.Value));

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>Settings saved</body></html>", Encoding.UTF8, "text/html")
            };
            string setCookie = $"uconfig={serverCookie}; path=/; domain=.e-hentai.org";
            resp.Headers.TryAddWithoutValidation("Set-Cookie", setCookie);
            // Mirror EhHttpClientFactory's cookie pipeline so Set-Cookie updates the ICookieStore.
            cookies.SaveFromResponse(req.RequestUri!, [setCookie]);
            return Task.FromResult(resp);
        }));

        var engine = new EhEngine(httpClient, cookies, db, filter, clientSettings);
        var sync = new UConfigSyncService(cookies);
        var settings = new DesktopSettings { GallerySite = EhSite.E };

        bool pulled = await sync.SyncOnlineAsync(engine, settings);
        Assert.True(pulled);
        Assert.Equal("0x1025", settings.ExcludedLanguages);
        Assert.Equal(EhConfig.NamespacesMale, settings.ExcludedNamespaces);
        Assert.Equal(EhConfig.FavoritesSortGalleryUpdateTime, settings.FavoritesSortOrder);
        Assert.Equal(EhConfig.CommentsSortRecentFirst, settings.CommentsSortOrder);
        Assert.False(settings.EnablePopularOnHome);
    }

    [Fact]
    public void SettingsViewModel_HostsAndBlackListCrud_PersistAndValidate()
    {
        string appDir = Path.Combine(_root, "hosts-bl");
        using var services = AppServices.Create(AppPaths.CreateAt(appDir));
        var vm = new SettingsViewModel(services);

        // Hosts CRUD + validation
        Assert.False(vm.AddOrUpdateHost("bad host name!", "1.2.3.4", out _));
        Assert.False(vm.AddOrUpdateHost("e-hentai.org", "not-an-ip", out _));
        Assert.True(vm.AddOrUpdateHost("e-hentai.org", "104.20.18.168", out _));
        Assert.Single(vm.GetHostEntries(), h => h.Host == "e-hentai.org" && h.Ip == "104.20.18.168");

        vm.DeleteHost("e-hentai.org");
        Assert.Empty(vm.GetHostEntries());

        // Comment blacklist CRUD + duplicate guard
        Assert.False(vm.AddBlackListEntry("   ", "spam", out _));
        Assert.True(vm.AddBlackListEntry("TrollUser", "广告刷屏", out _));
        Assert.False(vm.AddBlackListEntry("TrollUser", "重复添加", out _));

        BlackListEntry added = Assert.Single(vm.GetBlackListEntries());
        Assert.Equal("TrollUser", added.BadGayName);
        Assert.Equal("广告刷屏", added.Reason);

        vm.DeleteBlackListEntry(added);
        Assert.Empty(vm.GetBlackListEntries());
    }

    [Fact]
    public void SecurityPin_ProtectedWithDpapi_AndVerifiesInConstantTime()
    {
        string appDir = Path.Combine(_root, "pin-lock");
        using var services = AppServices.Create(AppPaths.CreateAt(appDir));
        var vm = new SettingsViewModel(services);

        Assert.False(WindowsAppLockService.IsLockEnabled(services.Settings));
        Assert.False(vm.SetSecurityPin("123")); // too short
        Assert.True(vm.SetSecurityPin("2468"));

        string? stored = services.Settings.SecurityPinHash;
        Assert.NotNull(stored);
        Assert.StartsWith(DpapiCredentialProtector.EnvelopePrefix, stored!, StringComparison.Ordinal);
        Assert.DoesNotContain("2468", stored!, StringComparison.Ordinal);
        Assert.True(WindowsAppLockService.IsLockEnabled(services.Settings));
        Assert.True(WindowsAppLockService.VerifyPin("2468", stored));
        Assert.False(WindowsAppLockService.VerifyPin("9999", stored));

        vm.ClearSecurityPin();
        Assert.False(WindowsAppLockService.IsLockEnabled(services.Settings));
        Assert.Null(services.Settings.SecurityPinHash);
    }

    [Fact]
    public void ClearSecurityPin_CanKeepOrDisableWindowsHello_AndPersistsDisabledLock()
    {
        string appDir = Path.Combine(_root, "pin-hello-clear");
        AppPaths paths = AppPaths.CreateAt(appDir);
        using (var services = AppServices.Create(paths))
        {
            var vm = new SettingsViewModel(services);
            Assert.False(vm.HasSecurityPin);
            Assert.False(vm.CanClearAppLock);

            Assert.True(vm.SetSecurityPin("2468"));
            vm.UseWindowsHello = true;
            Assert.True(vm.Save(), vm.ErrorMessage);

            Assert.True(vm.HasSecurityPin);
            Assert.True(vm.CanClearAppLock);
            Assert.True(WindowsAppLockService.IsLockEnabled(services.Settings));

            // Keeping Windows Hello clears only the local PIN while leaving lock active.
            vm.ClearSecurityPin(disableWindowsHello: false);
            Assert.Null(services.Settings.SecurityPinHash);
            Assert.True(services.Settings.UseWindowsHello);
            Assert.False(vm.HasSecurityPin);
            Assert.True(vm.CanClearAppLock);
            Assert.True(WindowsAppLockService.IsLockEnabled(services.Settings));

            // Clearing with disableWindowsHello=true disables all lock mechanisms and persists cleanly.
            vm.ClearSecurityPin();
            Assert.Null(services.Settings.SecurityPinHash);
            Assert.False(services.Settings.UseWindowsHello);
            Assert.False(vm.UseWindowsHello);
            Assert.False(vm.HasSecurityPin);
            Assert.False(vm.CanClearAppLock);
            Assert.False(WindowsAppLockService.IsLockEnabled(services.Settings));
        }

        using var reloaded = AppServices.Create(paths);
        Assert.Null(reloaded.Settings.SecurityPinHash);
        Assert.False(reloaded.Settings.UseWindowsHello);
        Assert.False(WindowsAppLockService.IsLockEnabled(reloaded.Settings));
    }

    [Fact]
    public async Task SettingsViewModel_CheckForUpdatesAsync_DetectsNewerGitHubRelease()
    {
        string appDir = Path.Combine(_root, "updates");
        using var services = AppServices.Create(AppPaths.CreateAt(appDir));
        var vm = new SettingsViewModel(services);

        const string newerReleaseJson = """
            {
              "tag_name": "v99.1.0",
              "name": "Major Desktop Release",
              "html_url": "https://github.com/example/repo/releases/tag/v99.1.0",
              "body": "All features complete"
            }
            """;

        AppUpdateCheckResult? newer = await vm.CheckForUpdatesAsync(
            fetchJsonAsync: (_, _) => Task.FromResult(newerReleaseJson));
        Assert.NotNull(newer);
        Assert.True(newer!.HasUpdate);
        Assert.Equal("99.1.0", newer.Latest!.Version);
        Assert.Contains("99.1.0", vm.UpdateStatusMessage, StringComparison.Ordinal);

        const string sameReleaseJson = """
            {
              "version": "1.0.0",
              "versionCode": 1,
              "mustUpdate": false,
              "title": "Current"
            }
            """;

        AppUpdateCheckResult? current = await vm.CheckForUpdatesAsync(
            fetchJsonAsync: (_, _) => Task.FromResult(sameReleaseJson));
        Assert.NotNull(current);
        Assert.False(current!.HasUpdate);
    }

    [Fact]
    public async Task LoginViewModel_SiteDashboardActionsAndWebViewCookiesWorkEndToEnd()
    {
        string appDir = Path.Combine(_root, "login-dashboard");
        AppPaths paths = AppPaths.CreateAt(appDir);
        using var db = new EhDatabase(paths.EhDatabasePath);
        var filter = new EhFilter(db);
        var clientSettings = new EhClientSettings();
        using var cookies = new SqliteCookieStore(paths.CookieDatabasePath);

        const string homeHtml = """
            <html><body>
              <div class="homebox">
                <p>You are currently at <strong>125</strong> towards a limit of <strong>5000</strong>.</p>
                <p>Reset Cost: <strong>50</strong> GP</p>
              </div>
              <div class="homebox"></div>
              <div class="homebox">
                <table><tbody>
                  <tr><td>100</td><td>visits</td></tr>
                  <tr><td>200</td><td>torrents</td></tr>
                  <tr><td>300</td><td>archive</td></tr>
                  <tr><td>400</td><td>hath</td></tr>
                </tbody></table>
              </div>
              <div class="homebox"></div>
              <div class="homebox"><div><div><div><div><div>100</div><div>25</div></div></div></div></div></div>
            </body></html>
            """;

        const string newsHtml = """
            <html><body>
              <div id="eventpane"><div>Announcement</div><div><p>Happy Anniversary!</p></div><div>More</div></div>
              <div class="stuffbox"><p>Latest E-Hentai server update.</p></div>
            </body></html>
            """;

        const string myTagsHtml = """
            <html><body>
              <div id="usertags_outer">
                <div>add-row</div>
                <div id="usertag_42">
                  <div id="tagpreview_42" title="language:chinese"></div>
                  <input id="tagwatch_42" checked="checked" />
                  <input id="taghide_42" />
                  <input id="tagcolor_42" placeholder="#ff5500" />
                  <input id="tagweight_42" value="15" />
                </div>
              </div>
            </body></html>
            """;

        const string forumsHtml = """
            <html><body>
              <div id="userlinks"><p><b><a href="https://forums.e-hentai.org/index.php?showuser=77">Tester</a></b></p></div>
            </body></html>
            """;

        const string profileHtml = """
            <html><body>
              <div>
                <div id="profilename"><div>DesktopTester</div></div>
                <div></div>
                <div><img src="uploads/av-77.png" /></div>
              </div>
            </body></html>
            """;

        using var httpClient = new HttpClient(new DelegateHandler(async (req, _) =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("home.php", StringComparison.Ordinal))
            {
                if (req.Method == HttpMethod.Post)
                {
                    string form = await req.Content!.ReadAsStringAsync();
                    Assert.Contains("reset_imagelimit", form, StringComparison.Ordinal);
                }

                return HtmlOk(homeHtml);
            }

            if (url.Contains("news.php", StringComparison.Ordinal))
            {
                return HtmlOk(newsHtml);
            }

            if (url.Contains("mytags", StringComparison.Ordinal))
            {
                return HtmlOk(myTagsHtml);
            }

            if (url.Contains("showuser=77", StringComparison.Ordinal))
            {
                return HtmlOk(profileHtml);
            }

            if (url.Contains("forums.e-hentai.org", StringComparison.Ordinal))
            {
                return HtmlOk(forumsHtml);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var engine = new EhEngine(httpClient, cookies, db, filter, clientSettings);
        var vm = new LoginViewModel(engine, cookies);

        int imported = vm.ImportWebViewCookies(
        [
            new EhCookie(SqliteCookieStore.KeyIpbMemberId, "123456", EhUrl.DomainE, "/", EhCookie.MaxExpiresAt, true, true, true, false),
            new EhCookie(SqliteCookieStore.KeyIpbPassHash, "hash999", EhUrl.DomainE, "/", EhCookie.MaxExpiresAt, true, true, true, false)
        ]);
        Assert.Equal(2, imported);
        Assert.True(vm.IsSignedIn);

        await vm.LoadProfileAsync();
        Assert.Equal("DesktopTester", vm.DisplayName);
        Assert.Equal("https://forums.e-hentai.org/uploads/av-77.png", vm.AvatarUrl);

        await vm.LoadHomeQuotaAsync();
        Assert.Contains("125/5000", vm.HomeQuotaText, StringComparison.Ordinal);
        Assert.Contains("50", vm.HomeQuotaText, StringComparison.Ordinal);

        await vm.ResetImageLimitAsync();
        Assert.Contains("50", vm.HomeQuotaText, StringComparison.Ordinal);

        await vm.LoadEhNewsAsync();
        Assert.Contains("Happy Anniversary", vm.EventPaneText, StringComparison.Ordinal);

        vm.NewTagName = "language:chinese";
        await vm.AddWatchedTagAsync();
        WatchedTagRow tagRow = Assert.Single(vm.WatchedTags);
        Assert.Equal("language:chinese", tagRow.Title);

        await vm.DeleteWatchedTagAsync(tagRow);
        Assert.Single(vm.WatchedTags);
    }

    [Fact]
    public void WindowsShellHelpers_ComputeTaskbarStatesAndDisposeCleanlyWithoutHwnd()
    {
        // 1. Taskbar aggregation math
        var empty = WindowsTaskbarProgress.ComputeAggregateProgress([], []);
        Assert.Equal(TaskbarProgressState.NoProgress, empty.State);

        var running = WindowsTaskbarProgress.ComputeAggregateProgress(
            [
                new DownloadProgress(1, "A", DownloadState.Download, Done: 5, Total: 20, Bytes: 1000, Error: null),
                new DownloadProgress(2, "B", DownloadState.Wait, Done: 0, Total: 10, Bytes: 0, Error: null)
            ],
            [
                new ArchiverTaskSnapshot("t1", 3, "C", "https://hath/dl", "org", "a.zip", ArchiverTaskState.Running, BytesReceived: 25, TotalBytes: 70, ErrorMessage: null, AutoImportToLibrary: true, ImportedDirectory: null)
            ]);
        Assert.Equal(TaskbarProgressState.Normal, running.State);
        Assert.Equal(30UL, running.Completed);
        Assert.Equal(100UL, running.Total);

        var pausedOnly = WindowsTaskbarProgress.ComputeAggregateProgress(
            [new DownloadProgress(1, "A", DownloadState.None, Done: 4, Total: 10, Bytes: 100, Error: null, IsPaused: true)],
            []);
        Assert.Equal(TaskbarProgressState.Paused, pausedOnly.State);
        Assert.Equal(4UL, pausedOnly.Completed);
        Assert.Equal(10UL, pausedOnly.Total);

        var runningWithError = WindowsTaskbarProgress.ComputeAggregateProgress(
            [
                new DownloadProgress(1, "A", DownloadState.Download, Done: 2, Total: 10, Bytes: 100, Error: null),
                new DownloadProgress(2, "B", DownloadState.Failed, Done: 0, Total: 10, Bytes: 0, Error: "509")
            ],
            []);
        Assert.Equal(TaskbarProgressState.Error, runningWithError.State);

        // 2. Safe zero-HWND lifecycle for tray + taskbar + notifications
        using var taskbar = new WindowsTaskbarProgress();
        taskbar.Initialize(0);
        Assert.False(taskbar.IsInitialized);
        taskbar.Update(running);
        taskbar.Clear();

        using var tray = new WindowsTrayIcon();
        Assert.False(tray.Attach(0));
        Assert.False(tray.IsAttached);

        var settings = new DesktopSettings { ShowNotifications = true };
        var notifications = new WindowsNotifications(tray, () => settings, NullLogger<WindowsNotifications>.Instance);
        string? raisedTitle = null;
        notifications.BannerNotificationRequested += (t, _, _) => raisedTitle = t;

        notifications.Notify("下载完成", "Test Gallery");
        Assert.Equal("下载完成", raisedTitle);

        raisedTitle = null;
        settings.ShowNotifications = false;
        notifications.Notify("Suppressed", "Should not fire");
        Assert.Null(raisedTitle);
    }

    [Fact]
    public void LoginViewModel_ThreeFieldCookieSignInValidatesNormalizesAndUpdatesStore()
    {
        string appDir = Path.Combine(_root, "login-three-field-cookies");
        AppPaths paths = AppPaths.CreateAt(appDir);
        using var db = new EhDatabase(paths.EhDatabasePath);
        var filter = new EhFilter(db);
        using var cookies = new SqliteCookieStore(paths.CookieDatabasePath);
        using var httpClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var engine = new EhEngine(httpClient, cookies, db, filter, new EhClientSettings());
        var vm = new LoginViewModel(engine, cookies);

        // Missing required fields fails cleanly without signing in
        vm.IpbMemberId = "";
        vm.IpbPassHash = "abcdef0123456789abcdef0123456789";
        vm.SignInWithCookie();
        Assert.False(vm.IsSignedIn);
        Assert.Contains("ipb_member_id", vm.ErrorMessage, StringComparison.Ordinal);

        vm.IpbMemberId = "123456";
        vm.IpbPassHash = "   ";
        vm.SignInWithCookie();
        Assert.False(vm.IsSignedIn);
        Assert.Contains("ipb_pass_hash", vm.ErrorMessage, StringComparison.Ordinal);

        // Sign in with three fields including accidental key=value; formatting
        vm.IpbMemberId = "ipb_member_id=123456;";
        vm.IpbPassHash = "ipb_pass_hash=abcdef0123456789abcdef0123456789;";
        vm.Igneous = "igneous: ig9988";
        vm.SignInWithCookie();

        Assert.True(vm.IsSignedIn);
        Assert.Equal(string.Empty, vm.ErrorMessage);
        Assert.Equal(string.Empty, vm.IpbMemberId);
        Assert.Equal(string.Empty, vm.IpbPassHash);
        Assert.Equal(string.Empty, vm.Igneous);

        Uri eUri = new(EhUrl.HostE);
        Uri exUri = new(EhUrl.HostEx);
        Assert.Equal("123456", cookies.GetCookies(eUri).Single(c => c.Name == SqliteCookieStore.KeyIpbMemberId).Value);
        Assert.Equal("abcdef0123456789abcdef0123456789", cookies.GetCookies(exUri).Single(c => c.Name == SqliteCookieStore.KeyIpbPassHash).Value);
        Assert.Equal("ig9988", cookies.GetCookies(exUri).Single(c => c.Name == SqliteCookieStore.KeyIgneous).Value);

        // Updating without igneous expires old igneous for same user
        vm.IpbMemberId = "123456";
        vm.IpbPassHash = "00000000000000000000000000000000";
        vm.Igneous = "";
        vm.SignInWithCookie();
        Assert.True(vm.IsSignedIn);
        Assert.DoesNotContain(cookies.GetCookies(exUri), c => c.Name == SqliteCookieStore.KeyIgneous);

        // Switching to a different account clears stale session cookies first
        vm.IpbMemberId = "654321";
        vm.IpbPassHash = "11111111111111111111111111111111";
        vm.Igneous = "new_ig";
        vm.SignInWithCookie();
        Assert.True(vm.IsSignedIn);
        Assert.Equal("654321", cookies.GetCookies(eUri).Single(c => c.Name == SqliteCookieStore.KeyIpbMemberId).Value);
        Assert.Equal("new_ig", cookies.GetCookies(exUri).Single(c => c.Name == SqliteCookieStore.KeyIgneous).Value);
    }

    private static HttpResponseMessage HtmlOk(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}

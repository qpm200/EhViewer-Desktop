using EhViewer.Core.Client;
using EhViewer.Core.Network;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace EhViewer.Desktop.Views;

/// <summary>
/// Android Material styled Account, Quota, Site News, and MyTags center.
/// Preserves DPAPI session persistence, password/cookie/WebView2 sign-in, and uconfig synchronization.
/// </summary>
public sealed partial class LoginPage : Page, ISceneAware
{
    private IShellCoordinator? _shell;

    public LoginPage(LoginViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public LoginViewModel ViewModel { get; }

    public event EventHandler? SignedIn;

    public void AttachCoordinator(IShellCoordinator coordinator) => _shell = coordinator;

    public static string FormatDisplayName(string? name)
        => string.IsNullOrWhiteSpace(name) ? "未登录访客模式" : name!;

    private void OnSelectPasswordAuthClick(object sender, RoutedEventArgs e) => SelectAuthTab(0);
    private void OnSelectCookieAuthClick(object sender, RoutedEventArgs e) => SelectAuthTab(1);
    private void OnSelectWebViewAuthClick(object sender, RoutedEventArgs e) => SelectAuthTab(2);

    private void SelectAuthTab(int index)
    {
        AuthPasswordPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        AuthCookiePanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        AuthWebViewPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        Style? primary = Application.Current?.Resources["EhTealPrimaryButtonStyle"] as Style;
        AuthTabPasswordBtn.Style = index == 0 ? primary : null;
        AuthTabCookieBtn.Style = index == 1 ? primary : null;
        AuthTabWebViewBtn.Style = index == 2 ? primary : null;
    }

    private async void OnPasswordSignInClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SignInWithPasswordAsync().ConfigureAwait(true);
        if (ViewModel.IsSignedIn)
        {
            SignedIn?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnCookieSignInClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SignInWithCookie();
        if (ViewModel.IsSignedIn)
        {
            SignedIn?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void OnWebViewLoginClick(object sender, RoutedEventArgs e)
    {
        await OpenWebViewSessionDialogAsync(
            title: "WebView2 登录 E-Hentai 论坛",
            initialUrl: EhUrl.UrlSignIn,
            seedSiteCookies: false).ConfigureAwait(true);
    }

    private async void OnWebViewUConfigClick(object sender, RoutedEventArgs e)
    {
        string url = ViewModel.ActiveSite == EhSite.Ex ? EhUrl.UrlUConfigEx : EhUrl.UrlUConfigE;
        await OpenWebViewSessionDialogAsync(
            title: "WebView2 编辑站点配置（uconfig.php）",
            initialUrl: url,
            seedSiteCookies: true).ConfigureAwait(true);
    }

    private async void OnSyncUConfigOnlineClick(object sender, RoutedEventArgs e)
        => await ViewModel.SyncUConfigOnlineAsync().ConfigureAwait(true);

    private void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SignOut();
        _shell?.RefreshAccountAndQuota(fetchQuotaIfSignedIn: false);
    }

    private async void OnRefreshProfileClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadProfileAsync().ConfigureAwait(true);
        _shell?.RefreshAccountAndQuota(fetchQuotaIfSignedIn: true);
    }

    private async void OnLoadQuotaClick(object sender, RoutedEventArgs e)
        => await ViewModel.LoadHomeQuotaAsync().ConfigureAwait(true);

    private async void OnResetLimitClick(object sender, RoutedEventArgs e)
        => await ViewModel.ResetImageLimitAsync().ConfigureAwait(true);

    private async void OnLoadNewsClick(object sender, RoutedEventArgs e)
        => await ViewModel.LoadEhNewsAsync().ConfigureAwait(true);

    private async void OnLoadWatchedTagsClick(object sender, RoutedEventArgs e)
        => await ViewModel.LoadWatchedTagsAsync().ConfigureAwait(true);

    private async void OnAddWatchedTagClick(object sender, RoutedEventArgs e)
        => await ViewModel.AddWatchedTagAsync().ConfigureAwait(true);

    private async void OnDeleteWatchedTagClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WatchedTagRow row })
        {
            await ViewModel.DeleteWatchedTagAsync(row).ConfigureAwait(true);
        }
    }

    private async Task OpenWebViewSessionDialogAsync(string title, string initialUrl, bool seedSiteCookies)
    {
        if (XamlRoot is null)
        {
            return;
        }

        WebView2? webView = null;
        try
        {
            webView = new WebView2 { Width = 820, Height = 540 };
            var statusText = new TextBlock
            {
                Text = $"正在加载 {initialUrl} …",
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var hostGrid = new Grid { RowSpacing = 8 };
            hostGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            hostGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(statusText, 0);
            Grid.SetRow(webView, 1);
            hostGrid.Children.Add(statusText);
            hostGrid.Children.Add(webView);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                PrimaryButtonText = "导入当前 Cookie 并关闭",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = hostGrid
            };

            bool initFailed = false;
            dialog.Opened += async (_, _) =>
            {
                try
                {
                    await webView.EnsureCoreWebView2Async();
                    if (seedSiteCookies)
                    {
                        CoreWebView2CookieManager cm = webView.CoreWebView2.CookieManager;
                        foreach (EhCookie cookie in ViewModel.GetCookiesForSite(ViewModel.ActiveSite))
                        {
                            try
                            {
                                CoreWebView2Cookie wvCookie = cm.CreateCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
                                cm.AddOrUpdateCookie(wvCookie);
                            }
                            catch
                            {
                            }
                        }
                    }

                    webView.CoreWebView2.NavigationCompleted += (_, args) =>
                    {
                        statusText.Text = args.IsSuccess
                            ? $"已加载：{webView.Source}"
                            : $"导航状态：{args.WebErrorStatus}";
                    };
                    webView.Source = new Uri(initialUrl);
                }
                catch (Exception ex)
                {
                    initFailed = true;
                    statusText.Text = $"WebView2 初始化失败：{ex.Message}";
                    ViewModel.ErrorMessage = $"WebView2 启动或初始化失败：{ex.Message}（可改用「Cookie 登录」完成登录）";
                }
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (!initFailed && result == ContentDialogResult.Primary && webView.CoreWebView2 is not null)
            {
                var captured = new List<EhCookie>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string target in new[] { "https://e-hentai.org", "https://exhentai.org", "https://forums.e-hentai.org" })
                {
                    IReadOnlyList<CoreWebView2Cookie> list = await webView.CoreWebView2.CookieManager.GetCookiesAsync(target);
                    foreach (CoreWebView2Cookie c in list)
                    {
                        string domain = target.Contains("exhentai", StringComparison.OrdinalIgnoreCase)
                            ? EhUrl.DomainEx
                            : EhUrl.DomainE;
                        string key = $"{c.Name}\n{domain}";
                        if (!seen.Add(key) || string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.Value))
                        {
                            continue;
                        }

                        captured.Add(new EhCookie(
                            name: c.Name,
                            value: c.Value,
                            domain: domain,
                            path: string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path,
                            expiresAt: EhCookie.MaxExpiresAt,
                            secure: c.IsSecure,
                            httpOnly: c.IsHttpOnly,
                            persistent: true,
                            hostOnly: false));
                    }
                }

                ViewModel.ImportWebViewCookies(captured);
                if (ViewModel.IsSignedIn)
                {
                    SignedIn?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"WebView2 启动或初始化失败：{ex.Message}（可改用「Cookie 登录」完成登录）";
        }
        finally
        {
            try
            {
                webView?.Close();
            }
            catch
            {
            }
        }
    }

    public bool IsNotBusy(bool busy) => !busy;

    public bool HasError(string? message) => !string.IsNullOrEmpty(message);

    public Visibility TextToVisibility(string? message) =>
        string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}

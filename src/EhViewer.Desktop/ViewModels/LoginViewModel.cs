using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Data.UserTag;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Network;
using EhViewer.Desktop.Services;

namespace EhViewer.Desktop.ViewModels;

public sealed record WatchedTagRow(UserTag Source, string Title, string DetailText);

/// <summary>
/// Sign-in and account dashboard view model supporting credential login, three-field cookie login
/// (<c>ipb_member_id</c>, <c>ipb_pass_hash</c>, <c>igneous</c>),
/// WebView2 browser sign-in/uconfig sync, quota inspection/reset, E-Hentai news, profile pull,
/// and MyTags management. Cookies always go through <see cref="ICookieStore"/> (DPAPI-protected).
/// </summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private readonly EhEngine _engine;
    private readonly ICookieStore _cookieStore;
    private readonly GalleryBrowseService _browse;
    private readonly UConfigSyncService _uconfigSync;
    private readonly AppServices? _services;

    public LoginViewModel(
        EhEngine engine,
        ICookieStore cookieStore,
        GalleryBrowseService? browse = null,
        UConfigSyncService? uconfigSync = null,
        AppServices? services = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _cookieStore = cookieStore ?? throw new ArgumentNullException(nameof(cookieStore));
        _browse = browse ?? new GalleryBrowseService(_engine, services?.GetRequiredService<IEhClientSettings>() ?? new EhClientSettings());
        _uconfigSync = uconfigSync ?? new UConfigSyncService(_cookieStore);
        _services = services;
        RefreshSignedInState();
    }

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IpbMemberId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IpbPassHash { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Igneous { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsSignedIn { get; set; }

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AvatarUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HomeQuotaText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EhNewsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EventPaneText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewTagName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool NewTagWatched { get; set; } = true;

    [ObservableProperty]
    public partial bool NewTagHidden { get; set; }

    [ObservableProperty]
    public partial string NewTagColor { get; set; } = string.Empty;

    public ObservableCollection<WatchedTagRow> WatchedTags { get; } = [];

    public EhSite ActiveSite => _services?.Settings.GallerySite ?? EhSite.E;

    public string ActiveUConfigUrl => EhUrl.GetUConfigUrl(ActiveSite);

    public IReadOnlyList<EhCookie> GetCookiesForSite(EhSite site)
    {
        string url = site == EhSite.Ex ? EhUrl.HostEx : EhUrl.HostE;
        return _cookieStore.GetCookies(new Uri(url));
    }

    public int ImportWebViewCookies(IEnumerable<EhCookie> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);

        ErrorMessage = string.Empty;
        int imported = 0;
        foreach (EhCookie cookie in cookies)
        {
            if (string.IsNullOrWhiteSpace(cookie.Name) || string.IsNullOrWhiteSpace(cookie.Value))
            {
                continue;
            }

            _cookieStore.AddCookie(cookie);
            imported++;

            if (cookie.Name is SqliteCookieStore.KeyIpbMemberId or SqliteCookieStore.KeyIpbPassHash
                or SqliteCookieStore.KeyIgneous or EhConfig.KeyUconfig)
            {
                string mirroredDomain = string.Equals(cookie.Domain, EhUrl.DomainEx, StringComparison.OrdinalIgnoreCase)
                    ? EhUrl.DomainE
                    : EhUrl.DomainEx;
                _cookieStore.AddCookie(new EhCookie(
                    name: cookie.Name,
                    value: cookie.Value,
                    domain: mirroredDomain,
                    path: "/",
                    expiresAt: EhCookie.MaxExpiresAt,
                    secure: cookie.Secure,
                    httpOnly: cookie.HttpOnly,
                    persistent: true,
                    hostOnly: false));
            }
        }

        if (_services is not null)
        {
            _services.UpdateSettings(s => _uconfigSync.PullFromCookieStore(s));
        }

        RefreshSignedInState();
        StatusMessage = imported > 0
            ? (IsSignedIn ? $"已从 WebView2 导入 {imported} 条 Cookie（已认证）。" : $"已导入 {imported} 条 Cookie，但尚未检测到完整登录凭证。")
            : "未从 WebView2 捕获到有效 Cookie。";
        return imported;
    }

    public async Task SyncUConfigOnlineAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;

        try
        {
            DesktopSettings snapshot = _services?.Settings ?? new DesktopSettings();
            bool updated = await _uconfigSync.SyncOnlineAsync(_engine, snapshot, cancellationToken).ConfigureAwait(true);
            if (_services is not null && updated)
            {
                _services.UpdateSettings(s =>
                {
                    s.ExcludedLanguages = snapshot.ExcludedLanguages;
                    s.ExcludedNamespaces = snapshot.ExcludedNamespaces;
                    s.FavoritesSortOrder = snapshot.FavoritesSortOrder;
                    s.CommentsSortOrder = snapshot.CommentsSortOrder;
                    s.EnablePopularOnHome = snapshot.EnablePopularOnHome;
                });
            }

            StatusMessage = $"已完成站点 uconfig 同步（{EhUrl.GetUConfigUrl(snapshot.GallerySite)}）。";
        }
        catch (Exception ex)
        {
            ErrorMessage = GalleryBrowseService.DescribeFailure(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadProfileAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            ProfileParser.Result profile = await _browse.GetProfileAsync(cancellationToken).ConfigureAwait(true);
            DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName) ? DisplayName : profile.DisplayName!;
            AvatarUrl = profile.Avatar ?? string.Empty;
            StatusMessage = string.IsNullOrWhiteSpace(profile.DisplayName)
                ? "未能获取用户资料（请确认已登录）。"
                : $"已刷新用户资料：{profile.DisplayName}";
        }
        catch (Exception ex)
        {
            ErrorMessage = GalleryBrowseService.DescribeFailure(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadHomeQuotaAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            HomeDetail detail = await _browse.GetHomeDetailAsync(cancellationToken).ConfigureAwait(true);
            HomeQuotaText = FormatHomeDetail(detail);
            StatusMessage = "已刷新 home.php 图片配额与 GP 统计。";
        }
        catch (Exception ex)
        {
            ErrorMessage = GalleryBrowseService.DescribeFailure(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ResetImageLimitAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            HomeDetail detail = await _browse.ResetImageLimitAsync(cancellationToken).ConfigureAwait(true);
            HomeQuotaText = FormatHomeDetail(detail);
            StatusMessage = "已提交重置图片配额请求并刷新状态。";
        }
        catch (Exception ex)
        {
            ErrorMessage = GalleryBrowseService.DescribeFailure(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadEhNewsAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            EhNewsDetail news = await _browse.GetEhNewsAsync(cancellationToken).ConfigureAwait(true);
            EventPaneText = EhHtmlHelper.HtmlToPlainText(news.GetEventPane() ?? news.EventPane);
            EhNewsText = EhHtmlHelper.HtmlToPlainText(news.WebData);
            if (string.IsNullOrWhiteSpace(EhNewsText))
            {
                EhNewsText = "当前无最新 E-Hentai 站点新闻文本。";
            }

            StatusMessage = "已获取 E-Hentai 站点新闻。";
        }
        catch (Exception ex)
        {
            ErrorMessage = GalleryBrowseService.DescribeFailure(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadWatchedTagsAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            UserTagList? list = await _browse.GetWatchedListAsync(cancellationToken).ConfigureAwait(true);
            ApplyWatchedTags(list);
            StatusMessage = $"已加载 {WatchedTags.Count} 个个人标签 (MyTags)。";
        }
        catch (Exception ex)
        {
            ErrorMessage = GalleryBrowseService.DescribeFailure(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AddWatchedTagAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(NewTagName))
        {
            ErrorMessage = "请填写要添加的标签名称（例如 language:chinese 或 artist:name）。";
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            var param = new TagPushParam
            {
                UserTagAction = "add",
                TagNameNew = NewTagName.Trim(),
                TagWatchNew = NewTagWatched ? "on" : null,
                TagHiddenNew = NewTagHidden ? "on" : null,
                TagColorNew = NewTagColor.Trim()
            };
            UserTagList? list = await _browse.AddWatchedTagAsync(param, cancellationToken).ConfigureAwait(true);
            ApplyWatchedTags(list);
            NewTagName = string.Empty;
            StatusMessage = "已添加个人标签 (MyTags)。";
        }
        catch (Exception ex)
        {
            ErrorMessage = GalleryBrowseService.DescribeFailure(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteWatchedTagAsync(WatchedTagRow row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        IsBusy = true;
        ErrorMessage = string.Empty;

        try
        {
            UserTagList? list = await _browse.DeleteWatchedTagAsync(row.Source, cancellationToken).ConfigureAwait(true);
            ApplyWatchedTags(list);
            StatusMessage = $"已删除标签：{row.Title}";
        }
        catch (Exception ex)
        {
            ErrorMessage = GalleryBrowseService.DescribeFailure(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyWatchedTags(UserTagList? list)
    {
        WatchedTags.Clear();
        if (list is null)
        {
            return;
        }

        foreach (UserTag tag in list.UserTags)
        {
            string mode = tag.Watched && tag.Hidden
                ? "关注 + 隐藏"
                : tag.Watched
                    ? "关注"
                    : tag.Hidden
                        ? "隐藏"
                        : "默认";
            string color = string.IsNullOrWhiteSpace(tag.Color) ? "默认色" : tag.Color!;
            WatchedTags.Add(new WatchedTagRow(
                tag,
                tag.TagName ?? "(未命名)",
                $"{mode} · 权重 {tag.TagWeight} · {color}"));
        }
    }

    private static string FormatHomeDetail(HomeDetail d) =>
        $"""
        {d.GetImageLimits("当前图片查看配额")}
        重置消耗：{d.ResetCost}
        画廊访问所得 GP：{d.FromGalleryVisits}
        种子完成所得 GP：{d.FromTorrentCompletions}
        归档下载所得 GP：{d.FromArchiveDownloads}
        H@H 节点所得 GP：{d.FromHentaiAtHome}
        当前管理权重：{d.CurrentModerationPower}
        """;

    /// <summary>Refreshes <see cref="IsSignedIn"/> from the persisted cookie jar.</summary>
    public void RefreshSignedInState()
    {
        IsSignedIn = _cookieStore.HasSignedIn;
    }

    /// <summary>Signs in with the credential form and persists the resulting session cookies.</summary>
    public async Task SignInWithPasswordAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "请输入用户名与密码。";
            return;
        }

        IsBusy = true;
        try
        {
            string nickname = await _engine.SignInAsync(Username.Trim(), Password, cancellationToken)
                .ConfigureAwait(true);

            DisplayName = nickname;
            IsSignedIn = _cookieStore.HasSignedIn;
            StatusMessage = string.IsNullOrWhiteSpace(nickname)
                ? "登录成功。"
                : $"登录成功，欢迎 {nickname}。";

            if (!IsSignedIn)
            {
                // The forum accepted the credentials but did not hand back a usable session.
                ErrorMessage = "登录响应成功，但未收到有效的会话 Cookie，请改用 Cookie 登录方式。";
            }

            Password = string.Empty;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "登录已取消。";
        }
        catch (Exception ex)
        {
            ErrorMessage = GalleryBrowseService.DescribeFailure(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Saves <c>ipb_member_id</c>, <c>ipb_pass_hash</c>, and optional <c>igneous</c>
    /// into the DPAPI-encrypted cookie store for E-Hentai and ExHentai.
    /// </summary>
    public void SignInWithCookie()
    {
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;

        string memberId = NormalizeCookieFieldValue(IpbMemberId, SqliteCookieStore.KeyIpbMemberId);
        string passHash = NormalizeCookieFieldValue(IpbPassHash, SqliteCookieStore.KeyIpbPassHash);
        string igneous = NormalizeCookieFieldValue(Igneous, SqliteCookieStore.KeyIgneous);

        if (string.IsNullOrWhiteSpace(memberId))
        {
            ErrorMessage = "请输入 ipb_member_id。";
            return;
        }

        if (string.IsNullOrWhiteSpace(passHash))
        {
            ErrorMessage = "请输入 ipb_pass_hash。";
            return;
        }

        int imported = ImportDirectCookies(memberId, passHash, igneous, _cookieStore);
        IsSignedIn = _cookieStore.HasSignedIn;
        if (!IsSignedIn)
        {
            ErrorMessage = "未能写入有效的登录 Cookie。";
            return;
        }

        StatusMessage = string.IsNullOrEmpty(igneous)
            ? $"已保存 {imported} 项 Cookie 凭证（未填写 igneous），登录状态有效。"
            : $"已保存 {imported} 项 Cookie 凭证（含 igneous），登录状态有效。";

        IpbMemberId = string.Empty;
        IpbPassHash = string.Empty;
        Igneous = string.Empty;
    }

    /// <summary>Clears every persisted cookie and the signed-in state.</summary>
    public void SignOut()
    {
        _cookieStore.SignOut();
        IsSignedIn = false;
        DisplayName = string.Empty;
        IpbMemberId = string.Empty;
        IpbPassHash = string.Empty;
        Igneous = string.Empty;
        StatusMessage = "已退出登录。";
        ErrorMessage = string.Empty;
    }

    internal static int ImportDirectCookies(
        string ipbMemberId,
        string ipbPassHash,
        string? igneous,
        ICookieStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(ipbMemberId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ipbPassHash);

        string trimmedId = ipbMemberId.Trim();
        string trimmedHash = ipbPassHash.Trim();
        string trimmedIgneous = igneous?.Trim() ?? string.Empty;

        // If switching to another account, wipe previous session cookies so stale tokens do not linger.
        string? existingMemberId = store.GetCookies(new Uri(EhUrl.HostE))
            .FirstOrDefault(c => string.Equals(c.Name, SqliteCookieStore.KeyIpbMemberId, StringComparison.Ordinal))
            ?.Value;
        if (!string.IsNullOrEmpty(existingMemberId)
            && !string.Equals(existingMemberId, trimmedId, StringComparison.Ordinal))
        {
            store.SignOut();
        }

        int stored = 0;
        StoreSitePair(store, SqliteCookieStore.KeyIpbMemberId, trimmedId);
        stored++;
        StoreSitePair(store, SqliteCookieStore.KeyIpbPassHash, trimmedHash);
        stored++;

        if (!string.IsNullOrEmpty(trimmedIgneous))
        {
            StoreSitePair(store, SqliteCookieStore.KeyIgneous, trimmedIgneous);
            stored++;
        }
        else
        {
            ExpireSitePair(store, SqliteCookieStore.KeyIgneous);
        }

        return stored;
    }

    private static void StoreSitePair(ICookieStore store, string name, string value)
    {
        var cookie = new EhCookie(
            name: name,
            value: value,
            domain: EhUrl.DomainE,
            path: "/",
            expiresAt: EhCookie.MaxExpiresAt,
            secure: true,
            httpOnly: true,
            persistent: true,
            hostOnly: false);
        store.AddCookie(cookie);
        store.AddCookie(cookie.WithDomain(EhUrl.DomainEx, forcePersistent: true));
    }

    private static void ExpireSitePair(ICookieStore store, string name)
    {
        foreach (string domain in new[] { EhUrl.DomainE, EhUrl.DomainEx })
        {
            store.AddCookie(new EhCookie(
                name: name,
                value: string.Empty,
                domain: domain,
                path: "/",
                expiresAt: 0,
                secure: true,
                httpOnly: true,
                persistent: true,
                hostOnly: false));
        }
    }

    internal static string NormalizeCookieFieldValue(string? raw, string expectedKey)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string trimmed = raw.Trim().TrimEnd(';', ',').Trim();
        if (trimmed.StartsWith(expectedKey, StringComparison.OrdinalIgnoreCase))
        {
            string rest = trimmed[expectedKey.Length..].TrimStart();
            if (rest.StartsWith('=') || rest.StartsWith(':'))
            {
                trimmed = rest[1..].Trim().Trim('"', '\'').TrimEnd(';', ',').Trim();
            }
        }

        return trimmed;
    }
}

using EhViewer.Core.Network;

namespace EhViewer.Core.Client;

/// <summary>
/// Synchronizes <see cref="EhConfig"/> values with the persisted <c>uconfig</c> cookie for
/// both E-Hentai and ExHentai domains.
/// </summary>
public static class EhConfigCookieSync
{
    public static void ApplyToCookieStore(ICookieStore cookieStore, EhConfig config)
    {
        ArgumentNullException.ThrowIfNull(cookieStore);
        ArgumentNullException.ThrowIfNull(config);

        string value = config.BuildUConfig();
        AddForDomain(cookieStore, EhUrl.DomainE, value);
        AddForDomain(cookieStore, EhUrl.DomainEx, value);
    }

    public static bool TryReadFromCookieStore(ICookieStore cookieStore, out string? uconfig)
    {
        ArgumentNullException.ThrowIfNull(cookieStore);

        foreach (string baseUrl in new[] { EhUrl.HostE, EhUrl.HostEx })
        {
            IReadOnlyList<EhCookie> cookies = cookieStore.GetCookies(new Uri(baseUrl));
            foreach (EhCookie cookie in cookies)
            {
                if (string.Equals(cookie.Name, EhConfig.KeyUconfig, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(cookie.Value))
                {
                    uconfig = cookie.Value;
                    return true;
                }
            }
        }

        uconfig = null;
        return false;
    }

    private static void AddForDomain(ICookieStore cookieStore, string domain, string value)
    {
        cookieStore.AddCookie(new EhCookie(
            name: EhConfig.KeyUconfig,
            value: value,
            domain: domain,
            path: "/",
            expiresAt: EhCookie.MaxExpiresAt,
            secure: false,
            httpOnly: false,
            persistent: true,
            hostOnly: false));
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace EhViewer.Core.Network;

/// <summary>
/// Immutable RFC 6265 HTTP cookie value object matching OkHttp cookie semantics.
/// </summary>
public sealed partial class EhCookie : IEquatable<EhCookie>
{
    /// <summary>
    /// Persistent session-long expiration timestamp matching upstream <c>Long.MAX_VALUE</c>.
    /// </summary>
    public const long MaxExpiresAt = long.MaxValue;

    // Matches IPv4 dotted decimal or IPv6 colon-hex strings (matching CookieRepository.VERIFY_AS_IP_ADDRESS).
    private static readonly Regex IpAddressQuickPattern = CreateIpAddressQuickPattern();

    public EhCookie(
        string name,
        string value,
        string domain,
        string path = "/",
        long expiresAt = MaxExpiresAt,
        bool secure = false,
        bool httpOnly = false,
        bool persistent = false,
        bool hostOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Name = name;
        Value = value;
        Domain = domain.TrimStart('.').ToLowerInvariant();
        Path = path.StartsWith('/') ? path : "/";
        ExpiresAt = expiresAt;
        Secure = secure;
        HttpOnly = httpOnly;
        Persistent = persistent;
        HostOnly = hostOnly;
    }

    public string Name { get; }
    public string Value { get; }
    public long ExpiresAt { get; }
    public string Domain { get; }
    public string Path { get; }
    public bool Secure { get; }
    public bool HttpOnly { get; }
    public bool Persistent { get; }
    public bool HostOnly { get; }

    public bool Matches(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (Secure && !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string host = url.Host.ToLowerInvariant();
        bool domainMatch = HostOnly
            ? string.Equals(host, Domain, StringComparison.Ordinal)
            : DomainMatch(host, Domain);
        if (!domainMatch)
        {
            return false;
        }

        string urlPath = string.IsNullOrEmpty(url.AbsolutePath) ? "/" : url.AbsolutePath;
        return PathMatch(urlPath, Path);
    }

    public EhCookie WithDomain(string newDomain, bool forcePersistent = false, bool forceLongLive = false, bool forceNotHostOnly = false)
    {
        long expiresAt = forceLongLive || (forcePersistent && !Persistent)
            ? MaxExpiresAt
            : ExpiresAt;
        bool persistent = forceLongLive || Persistent || forcePersistent;
        bool hostOnly = HostOnly && !forceNotHostOnly;

        return new EhCookie(
            Name,
            Value,
            newDomain,
            Path,
            expiresAt,
            Secure,
            HttpOnly,
            persistent,
            hostOnly);
    }

    public static bool DomainMatch(Uri url, string domain)
    {
        ArgumentNullException.ThrowIfNull(url);
        return DomainMatch(url.Host.ToLowerInvariant(), domain.ToLowerInvariant());
    }

    public static bool DomainMatch(string urlHost, string domain)
    {
        if (string.Equals(urlHost, domain, StringComparison.Ordinal))
        {
            return true;
        }

        if (urlHost.EndsWith(domain, StringComparison.Ordinal)
            && urlHost.Length > domain.Length
            && urlHost[urlHost.Length - domain.Length - 1] == '.'
            && !VerifyAsIpAddress(urlHost))
        {
            return true;
        }

        return false;
    }

    private static bool PathMatch(string urlPath, string cookiePath)
    {
        if (string.Equals(urlPath, cookiePath, StringComparison.Ordinal))
        {
            return true;
        }

        if (urlPath.StartsWith(cookiePath, StringComparison.Ordinal))
        {
            if (cookiePath.EndsWith('/'))
            {
                return true;
            }

            if (urlPath[cookiePath.Length] == '/')
            {
                return true;
            }
        }

        return false;
    }

    private static bool VerifyAsIpAddress(string host) => IpAddressQuickPattern.IsMatch(host);

    /// <summary>
    /// Parses a <c>Set-Cookie</c> response header against <paramref name="requestUrl"/> according to RFC 6265.
    /// </summary>
    public static bool TryParse(Uri requestUrl, string setCookieHeader, long currentTimeMillis, out EhCookie? cookie)
    {
        ArgumentNullException.ThrowIfNull(requestUrl);
        cookie = null;

        if (string.IsNullOrWhiteSpace(setCookieHeader))
        {
            return false;
        }

        int firstSemi = setCookieHeader.IndexOf(';');
        string nameValuePart = (firstSemi >= 0 ? setCookieHeader[..firstSemi] : setCookieHeader).Trim();
        int eq = nameValuePart.IndexOf('=');
        if (eq <= 0)
        {
            return false;
        }

        string name = nameValuePart[..eq].Trim();
        string value = nameValuePart[(eq + 1)..].Trim();
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        long expiresAt = MaxExpiresAt;
        long deltaSeconds = -1;
        string? domain = null;
        string? path = null;
        bool secureOnly = false;
        bool httpOnly = false;
        bool hostOnly = true;
        bool persistent = false;

        if (firstSemi >= 0)
        {
            string rest = setCookieHeader[(firstSemi + 1)..];
            foreach (string rawAttr in rest.Split(';'))
            {
                string attr = rawAttr.Trim();
                if (attr.Length == 0)
                {
                    continue;
                }

                int attrEq = attr.IndexOf('=');
                string attrName = (attrEq >= 0 ? attr[..attrEq] : attr).Trim();
                string attrVal = (attrEq >= 0 ? attr[(attrEq + 1)..] : string.Empty).Trim();

                if (string.Equals(attrName, "expires", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseExpires(attrVal, out long parsedExpires))
                    {
                        expiresAt = parsedExpires;
                        persistent = true;
                    }
                }
                else if (string.Equals(attrName, "max-age", StringComparison.OrdinalIgnoreCase))
                {
                    deltaSeconds = ParseMaxAge(attrVal);
                    persistent = true;
                }
                else if (string.Equals(attrName, "domain", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseDomain(attrVal, out string? parsedDomain))
                    {
                        domain = parsedDomain;
                        hostOnly = false;
                    }
                }
                else if (string.Equals(attrName, "path", StringComparison.OrdinalIgnoreCase))
                {
                    path = attrVal;
                }
                else if (string.Equals(attrName, "secure", StringComparison.OrdinalIgnoreCase))
                {
                    secureOnly = true;
                }
                else if (string.Equals(attrName, "httponly", StringComparison.OrdinalIgnoreCase))
                {
                    httpOnly = true;
                }
            }
        }

        if (deltaSeconds == long.MinValue)
        {
            expiresAt = long.MinValue;
        }
        else if (deltaSeconds != -1)
        {
            long deltaMillis = deltaSeconds <= (long.MaxValue / 1000)
                ? deltaSeconds * 1000L
                : long.MaxValue;
            expiresAt = currentTimeMillis <= long.MaxValue - deltaMillis
                ? currentTimeMillis + deltaMillis
                : long.MaxValue;
        }

        string urlHost = requestUrl.Host.ToLowerInvariant();
        if (domain is null)
        {
            domain = urlHost;
        }
        else if (!DomainMatch(urlHost, domain))
        {
            return false;
        }

        if (urlHost.Length != domain.Length && PublicSuffixCheckFails(domain))
        {
            return false;
        }

        if (string.IsNullOrEmpty(path) || !path.StartsWith('/'))
        {
            string encodedPath = requestUrl.AbsolutePath;
            int lastSlash = encodedPath.LastIndexOf('/');
            path = lastSlash > 0 ? encodedPath[..lastSlash] : "/";
        }

        cookie = new EhCookie(
            name,
            value,
            domain,
            path,
            expiresAt,
            secureOnly,
            httpOnly,
            persistent,
            hostOnly);
        return true;
    }

    private static bool PublicSuffixCheckFails(string domain)
    {
        // Reject bare TLDs / single-label domains when domain is broader than host (IPs already excluded by DomainMatch).
        return !domain.Contains('.');
    }

    private static bool TryParseDomain(string value, out string? domain)
    {
        domain = null;
        if (string.IsNullOrEmpty(value) || value.EndsWith('.'))
        {
            return false;
        }

        string trimmed = value.StartsWith('.') ? value[1..] : value;
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        domain = trimmed.ToLowerInvariant();
        return true;
    }

    private static long ParseMaxAge(string s)
    {
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed <= 0 ? long.MinValue : parsed;
        }

        // RFC 6265 overflow handling when digits exceed long.MaxValue
        if (s.All(char.IsDigit) && s.Length > 0)
        {
            return long.MaxValue;
        }

        return -1;
    }

    private static bool TryParseExpires(string s, out long expiresAtMillis)
    {
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset dto))
        {
            expiresAtMillis = dto.ToUnixTimeMilliseconds();
            return true;
        }

        expiresAtMillis = 0;
        return false;
    }

    public bool Equals(EhCookie? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return Name == other.Name
            && Value == other.Value
            && ExpiresAt == other.ExpiresAt
            && Domain == other.Domain
            && Path == other.Path
            && Secure == other.Secure
            && HttpOnly == other.HttpOnly
            && Persistent == other.Persistent
            && HostOnly == other.HostOnly;
    }

    public override bool Equals(object? obj) => Equals(obj as EhCookie);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(Value);
        hash.Add(ExpiresAt);
        hash.Add(Domain);
        hash.Add(Path);
        hash.Add(Secure);
        hash.Add(HttpOnly);
        hash.Add(Persistent);
        hash.Add(HostOnly);
        return hash.ToHashCode();
    }

    [GeneratedRegex(@"^(([0-9a-fA-F]*:[0-9a-fA-F:.]*)|([\d.]+))$", RegexOptions.CultureInvariant)]
    private static partial Regex CreateIpAddressQuickPattern();
}

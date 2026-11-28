using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace EhViewer.Core.Security;

/// <summary>
/// Windows DPAPI (<see cref="DataProtectionScope.CurrentUser"/>) credential protector
/// supporting legacy <c>dpapi:v1:</c> envelopes and domain-isolated <c>dpapi:v2:&lt;domain&gt;:</c> envelopes.
/// </summary>
public sealed class DpapiCredentialProtector : ICredentialProtector
{
    public const string EnvelopePrefix = "dpapi:v1:";
    public const string DomainEnvelopePrefix = "dpapi:v2:";

    public const string DomainCookies = "cookies";
    public const string DomainAppLock = "app-lock";
    public const string DomainTranslationApi = "translation-api";

    private static readonly byte[] LegacyV1Entropy = Encoding.UTF8.GetBytes("EhViewer.Desktop.CookieProtector.v1");
    private static readonly ConcurrentDictionary<string, DpapiCredentialProtector> DomainInstances = new(StringComparer.Ordinal);

    private readonly string? _domain;
    private readonly byte[] _entropy;
    private readonly string _v2Prefix;

    public DpapiCredentialProtector()
        : this(domain: null, entropy: LegacyV1Entropy)
    {
    }

    private DpapiCredentialProtector(string? domain, byte[] entropy)
    {
        _domain = domain;
        _entropy = entropy;
        _v2Prefix = domain is null ? string.Empty : $"{DomainEnvelopePrefix}{domain}:";
    }

    public static DpapiCredentialProtector Instance { get; } = new(domain: null, entropy: LegacyV1Entropy);

    public string? Domain => _domain;

    public static DpapiCredentialProtector ForDomain(string domain)
    {
        if (string.IsNullOrEmpty(domain))
        {
            throw new ArgumentException("Domain must not be null or empty.", nameof(domain));
        }

        for (int i = 0; i < domain.Length; i++)
        {
            char c = domain[i];
            bool valid = (c is >= 'a' and <= 'z')
                || (c is >= 'A' and <= 'Z')
                || (c is >= '0' and <= '9')
                || c is '-' or '_' or '.';
            if (!valid)
            {
                throw new ArgumentException(
                    "Domain must contain only ASCII letters, digits, '-', '_', or '.'.",
                    nameof(domain));
            }
        }

        string normalized = domain.ToLowerInvariant();
        return DomainInstances.GetOrAdd(
            normalized,
            static d => new DpapiCredentialProtector(d, Encoding.UTF8.GetBytes("EhViewer.Desktop.Dpapi.v2:" + d)));
    }

    public bool IsProtected(string? value)
    {
        return value is not null
            && (value.StartsWith(EnvelopePrefix, StringComparison.Ordinal)
                || value.StartsWith(DomainEnvelopePrefix, StringComparison.Ordinal));
    }

    public bool NeedsReprotection(string? value)
    {
        return _domain is not null
            && value is not null
            && value.StartsWith(EnvelopePrefix, StringComparison.Ordinal);
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI credential protection requires Windows.");
        }

        return ProtectWindows(plaintext);
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI credential protection requires Windows.");
        }

        if (!IsProtected(protectedValue))
        {
            throw new CryptographicException("Stored credential does not contain a valid DPAPI envelope.");
        }

        return UnprotectWindows(protectedValue);
    }

    [SupportedOSPlatform("windows")]
    private string ProtectWindows(string plaintext)
    {
        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            byte[] encrypted = ProtectedData.Protect(plainBytes, _entropy, DataProtectionScope.CurrentUser);
            string prefix = _domain is null ? EnvelopePrefix : _v2Prefix;
            return prefix + Convert.ToBase64String(encrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    [SupportedOSPlatform("windows")]
    private string UnprotectWindows(string protectedValue)
    {
        string payload;
        byte[] entropy;

        if (protectedValue.StartsWith(DomainEnvelopePrefix, StringComparison.Ordinal))
        {
            if (_domain is null)
            {
                throw new CryptographicException("Domain-isolated DPAPI v2 envelope cannot be unprotected by the default v1 protector.");
            }

            string rest = protectedValue[DomainEnvelopePrefix.Length..];
            int separator = rest.IndexOf(':');
            if (separator <= 0 || separator >= rest.Length - 1)
            {
                throw new CryptographicException("Stored credential does not contain a valid DPAPI v2 domain envelope.");
            }

            string envelopeDomain = rest[..separator];
            if (!string.Equals(envelopeDomain, _domain, StringComparison.Ordinal))
            {
                throw new CryptographicException(
                    $"Stored credential domain '{envelopeDomain}' does not match protector domain '{_domain}'.");
            }

            payload = rest[(separator + 1)..];
            entropy = _entropy;
        }
        else if (protectedValue.StartsWith(EnvelopePrefix, StringComparison.Ordinal))
        {
            payload = protectedValue[EnvelopePrefix.Length..];
            entropy = LegacyV1Entropy;
        }
        else
        {
            throw new CryptographicException("Stored credential does not contain a valid DPAPI envelope.");
        }

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(payload);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Stored credential envelope contains invalid Base64.", ex);
        }

        byte[] plainBytes = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }
}

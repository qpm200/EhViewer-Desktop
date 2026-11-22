using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace EhViewer.Core.Security;

/// <summary>
/// Windows DPAPI (<see cref="DataProtectionScope.CurrentUser"/>) credential protector.
/// </summary>
public sealed class DpapiCredentialProtector : ICredentialProtector
{
    public const string EnvelopePrefix = "dpapi:v1:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EhViewer.Desktop.CookieProtector.v1");

    public static DpapiCredentialProtector Instance { get; } = new();

    public bool IsProtected(string? value)
    {
        return value is not null && value.StartsWith(EnvelopePrefix, StringComparison.Ordinal);
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
            throw new CryptographicException("Stored credential does not contain a valid DPAPI v1 envelope.");
        }

        return UnprotectWindows(protectedValue);
    }

    [SupportedOSPlatform("windows")]
    private static string ProtectWindows(string plaintext)
    {
        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] encrypted = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        return EnvelopePrefix + Convert.ToBase64String(encrypted);
    }

    [SupportedOSPlatform("windows")]
    private static string UnprotectWindows(string protectedValue)
    {
        string payload = protectedValue[EnvelopePrefix.Length..];
        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(payload);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Stored credential envelope contains invalid Base64.", ex);
        }

        byte[] plainBytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plainBytes);
    }
}

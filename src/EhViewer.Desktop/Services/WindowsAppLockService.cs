using System.Security.Cryptography;
using System.Text;
using EhViewer.Core.Security;
using Windows.Security.Credentials.UI;

namespace EhViewer.Desktop.Services;

public readonly record struct WindowsHelloResult(
    bool Success,
    string? ErrorMessage,
    UserConsentVerifierAvailability? Availability = null);

/// <summary>
/// DPAPI-sealed local PIN verifier and optional Windows Hello user consent helper.
/// </summary>
public static class WindowsAppLockService
{
    private const string Prefix = "pin:v1:";

    public static bool IsLockEnabled(DesktopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.UseWindowsHello || !string.IsNullOrWhiteSpace(settings.SecurityPinHash);
    }

    public static string CreateProtectedPinHash(string pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = ComputeSaltedHash(salt, pin);
        string payload = $"{Prefix}{Convert.ToHexString(salt)}:{Convert.ToHexString(hash)}";
        return DpapiCredentialProtector.Instance.Protect(payload);
    }

    public static bool VerifyPin(string? pin, string? storedProtectedHash)
    {
        if (string.IsNullOrWhiteSpace(pin) || string.IsNullOrWhiteSpace(storedProtectedHash))
        {
            return false;
        }

        try
        {
            string plain = DpapiCredentialProtector.Instance.Unprotect(storedProtectedHash);
            if (!plain.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return false;
            }

            string rest = plain[Prefix.Length..];
            int colon = rest.IndexOf(':');
            if (colon <= 0 || colon >= rest.Length - 1)
            {
                return false;
            }

            byte[] salt = Convert.FromHexString(rest[..colon]);
            byte[] expectedHash = Convert.FromHexString(rest[(colon + 1)..]);
            byte[] actualHash = ComputeSaltedHash(salt, pin);
            return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<WindowsHelloResult> TryVerifyWindowsHelloAsync(string prompt)
    {
        try
        {
            UserConsentVerifierAvailability availability = await UserConsentVerifier.CheckAvailabilityAsync();
            if (availability != UserConsentVerifierAvailability.Available)
            {
                return new WindowsHelloResult(false, $"Windows Hello 当前不可用：{availability}", availability);
            }

            UserConsentVerificationResult result = await UserConsentVerifier.RequestVerificationAsync(prompt);
            return result == UserConsentVerificationResult.Verified
                ? new WindowsHelloResult(true, null, availability)
                : new WindowsHelloResult(false, $"Windows Hello 验证未通过：{result}", availability);
        }
        catch (Exception ex)
        {
            return new WindowsHelloResult(false, $"Windows Hello 调用失败：{ex.Message}");
        }
    }

    private static byte[] ComputeSaltedHash(byte[] salt, string pin)
    {
        byte[] pinBytes = Encoding.UTF8.GetBytes(pin);
        byte[] combined = new byte[salt.Length + pinBytes.Length];
        Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
        Buffer.BlockCopy(pinBytes, 0, combined, salt.Length, pinBytes.Length);
        return SHA256.HashData(combined);
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EhViewer.Core.Security;
using Windows.Security.Credentials.UI;

namespace EhViewer.Desktop.Services;

public enum PinVerifyStatus
{
    Valid,
    ValidNeedsMigration,
    InvalidPin,
    LockedOut,
    InvalidFormat,
    ProtectionError
}

public readonly record struct PinVerificationResult(
    PinVerifyStatus Status,
    TimeSpan RetryAfter = default,
    string? MigratedProtectedHash = null)
{
    public bool IsSuccess => Status is PinVerifyStatus.Valid or PinVerifyStatus.ValidNeedsMigration;
}

public readonly record struct WindowsHelloResult(
    bool Success,
    string? ErrorMessage,
    UserConsentVerifierAvailability? Availability = null);

/// <summary>
/// DPAPI-sealed local PIN verifier (PBKDF2-HMAC-SHA256 with legacy v1 migration and lockout backoff)
/// and optional Windows Hello user consent helper.
/// </summary>
public static class WindowsAppLockService
{
    private const string LegacyPrefix = "pin:v1:";
    private const string Prefix = "pin:v2:";
    private const int Pbkdf2Iterations = 120_000;
    private const int MinPbkdf2Iterations = 10_000;
    private const int MaxPbkdf2Iterations = 2_000_000;
    private const int SaltLength = 16;
    private const int HashLength = 32;
    private const int LockoutThreshold = 5;
    private const string DefaultLockScopeKey = "default";

    private static readonly DpapiCredentialProtector Protector =
        DpapiCredentialProtector.ForDomain(DpapiCredentialProtector.DomainAppLock);

    private static readonly ConcurrentDictionary<string, FailureState> FailureStates = new(StringComparer.Ordinal);

    private sealed class FailureState
    {
        public int ConsecutiveFailures;
        public DateTimeOffset LockedUntilUtc;
    }

    public static bool IsLockEnabled(DesktopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.UseWindowsHello || !string.IsNullOrWhiteSpace(settings.SecurityPinHash);
    }

    public static string CreateProtectedPinHash(string pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] pinBytes = Encoding.UTF8.GetBytes(pin);
        byte[] hash = Array.Empty<byte>();
        try
        {
            hash = Rfc2898DeriveBytes.Pbkdf2(pinBytes, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashLength);
            string payload = $"{Prefix}{Pbkdf2Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
            return Protector.Protect(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinBytes);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public static bool VerifyPin(string? pin, string? storedProtectedHash)
    {
        return VerifyPinDetailed(pin, storedProtectedHash).IsSuccess;
    }

    public static PinVerificationResult VerifyPinDetailed(
        string? pin,
        string? storedProtectedHash,
        string? lockScopeKey = null,
        DateTimeOffset? utcNow = null)
    {
        string scope = NormalizeScopeKey(lockScopeKey);
        DateTimeOffset now = utcNow ?? DateTimeOffset.UtcNow;

        if (TryGetActiveLockout(scope, now, out TimeSpan activeRetryAfter))
        {
            return new PinVerificationResult(PinVerifyStatus.LockedOut, RetryAfter: activeRetryAfter);
        }

        if (string.IsNullOrWhiteSpace(storedProtectedHash))
        {
            return new PinVerificationResult(PinVerifyStatus.InvalidFormat);
        }

        if (string.IsNullOrWhiteSpace(pin))
        {
            return new PinVerificationResult(PinVerifyStatus.InvalidPin);
        }

        string plain;
        try
        {
            plain = Protector.Unprotect(storedProtectedHash);
        }
        catch (CryptographicException)
        {
            return new PinVerificationResult(PinVerifyStatus.ProtectionError);
        }
        catch (PlatformNotSupportedException)
        {
            return new PinVerificationResult(PinVerifyStatus.ProtectionError);
        }

        if (plain.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return VerifyV2Pin(pin, storedProtectedHash, plain, scope, now);
        }

        if (plain.StartsWith(LegacyPrefix, StringComparison.Ordinal))
        {
            return VerifyLegacyV1Pin(pin, plain, scope, now);
        }

        return new PinVerificationResult(PinVerifyStatus.InvalidFormat);
    }

    internal static void ResetFailureState(string? lockScopeKey = null)
    {
        if (lockScopeKey is null)
        {
            FailureStates.Clear();
            return;
        }

        FailureStates.TryRemove(NormalizeScopeKey(lockScopeKey), out _);
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

    private static PinVerificationResult VerifyV2Pin(
        string pin,
        string storedProtectedHash,
        string plain,
        string scope,
        DateTimeOffset now)
    {
        string rest = plain[Prefix.Length..];
        string[] parts = rest.Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations)
            || iterations < MinPbkdf2Iterations
            || iterations > MaxPbkdf2Iterations
            || string.IsNullOrEmpty(parts[1])
            || string.IsNullOrEmpty(parts[2]))
        {
            return new PinVerificationResult(PinVerifyStatus.InvalidFormat);
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expectedHash = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return new PinVerificationResult(PinVerifyStatus.InvalidFormat);
        }

        if (salt.Length == 0 || expectedHash.Length == 0)
        {
            return new PinVerificationResult(PinVerifyStatus.InvalidFormat);
        }

        byte[] pinBytes = Encoding.UTF8.GetBytes(pin);
        byte[] actualHash = Array.Empty<byte>();
        bool matched;
        try
        {
            actualHash = Rfc2898DeriveBytes.Pbkdf2(pinBytes, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
            matched = CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinBytes);
            CryptographicOperations.ZeroMemory(actualHash);
            CryptographicOperations.ZeroMemory(expectedHash);
        }

        if (matched)
        {
            ClearFailureState(scope);
            if (Protector.NeedsReprotection(storedProtectedHash))
            {
                return new PinVerificationResult(
                    PinVerifyStatus.ValidNeedsMigration,
                    MigratedProtectedHash: CreateProtectedPinHash(pin));
            }

            return new PinVerificationResult(PinVerifyStatus.Valid);
        }

        TimeSpan retryAfter = RecordFailure(scope, now);
        return new PinVerificationResult(PinVerifyStatus.InvalidPin, RetryAfter: retryAfter);
    }

    private static PinVerificationResult VerifyLegacyV1Pin(
        string pin,
        string plain,
        string scope,
        DateTimeOffset now)
    {
        string rest = plain[LegacyPrefix.Length..];
        int colon = rest.IndexOf(':');
        if (colon <= 0 || colon >= rest.Length - 1 || rest.IndexOf(':', colon + 1) >= 0)
        {
            return new PinVerificationResult(PinVerifyStatus.InvalidFormat);
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromHexString(rest[..colon]);
            expectedHash = Convert.FromHexString(rest[(colon + 1)..]);
        }
        catch (FormatException)
        {
            return new PinVerificationResult(PinVerifyStatus.InvalidFormat);
        }

        if (salt.Length == 0 || expectedHash.Length == 0)
        {
            return new PinVerificationResult(PinVerifyStatus.InvalidFormat);
        }

        byte[] actualHash = Array.Empty<byte>();
        bool matched;
        try
        {
            actualHash = ComputeLegacySaltedHash(salt, pin);
            matched = CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualHash);
            CryptographicOperations.ZeroMemory(expectedHash);
        }

        if (matched)
        {
            ClearFailureState(scope);
            return new PinVerificationResult(
                PinVerifyStatus.ValidNeedsMigration,
                MigratedProtectedHash: CreateProtectedPinHash(pin));
        }

        TimeSpan retryAfter = RecordFailure(scope, now);
        return new PinVerificationResult(PinVerifyStatus.InvalidPin, RetryAfter: retryAfter);
    }

    private static byte[] ComputeLegacySaltedHash(byte[] salt, string pin)
    {
        byte[] pinBytes = Encoding.UTF8.GetBytes(pin);
        byte[] combined = new byte[salt.Length + pinBytes.Length];
        try
        {
            Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
            Buffer.BlockCopy(pinBytes, 0, combined, salt.Length, pinBytes.Length);
            return SHA256.HashData(combined);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinBytes);
            CryptographicOperations.ZeroMemory(combined);
        }
    }

    private static string NormalizeScopeKey(string? lockScopeKey)
    {
        return string.IsNullOrWhiteSpace(lockScopeKey) ? DefaultLockScopeKey : lockScopeKey;
    }

    private static bool TryGetActiveLockout(string scope, DateTimeOffset now, out TimeSpan retryAfter)
    {
        if (FailureStates.TryGetValue(scope, out FailureState? state))
        {
            lock (state)
            {
                if (state.LockedUntilUtc > now)
                {
                    retryAfter = state.LockedUntilUtc - now;
                    return true;
                }
            }
        }

        retryAfter = TimeSpan.Zero;
        return false;
    }

    private static TimeSpan RecordFailure(string scope, DateTimeOffset now)
    {
        FailureState state = FailureStates.GetOrAdd(scope, static _ => new FailureState());
        lock (state)
        {
            state.ConsecutiveFailures++;
            TimeSpan cooldown = GetCooldownDuration(state.ConsecutiveFailures);
            if (cooldown > TimeSpan.Zero)
            {
                state.LockedUntilUtc = now + cooldown;
            }

            return cooldown;
        }
    }

    private static void ClearFailureState(string scope)
    {
        if (FailureStates.TryGetValue(scope, out FailureState? state))
        {
            lock (state)
            {
                state.ConsecutiveFailures = 0;
                state.LockedUntilUtc = default;
            }
        }
    }

    private static TimeSpan GetCooldownDuration(int consecutiveFailures)
    {
        if (consecutiveFailures < LockoutThreshold)
        {
            return TimeSpan.Zero;
        }

        return consecutiveFailures switch
        {
            5 => TimeSpan.FromSeconds(15),
            6 => TimeSpan.FromSeconds(30),
            7 => TimeSpan.FromSeconds(60),
            _ => TimeSpan.FromSeconds(300)
        };
    }
}

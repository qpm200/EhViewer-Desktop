using System.Security.Cryptography;
using System.Text;
using EhViewer.Core.Security;
using EhViewer.Desktop.Services;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class AppLockSecurityTests
{
    [Fact]
    public void CreateProtectedPinHash_UsesDomainIsolatedV2EnvelopeAndPbkdf2WithRandomSalt()
    {
        string scope = "test-v2-" + Guid.NewGuid().ToString("N");
        try
        {
            string first = WindowsAppLockService.CreateProtectedPinHash("2468");
            string second = WindowsAppLockService.CreateProtectedPinHash("2468");

            Assert.StartsWith("dpapi:v2:app-lock:", first, StringComparison.Ordinal);
            Assert.StartsWith("dpapi:v2:app-lock:", second, StringComparison.Ordinal);
            Assert.NotEqual(first, second);
            Assert.DoesNotContain("2468", first, StringComparison.Ordinal);

            string inner = DpapiCredentialProtector
                .ForDomain(DpapiCredentialProtector.DomainAppLock)
                .Unprotect(first);
            Assert.StartsWith("pin:v2:120000:", inner, StringComparison.Ordinal);

            PinVerificationResult result = WindowsAppLockService.VerifyPinDetailed("2468", first, scope);
            Assert.Equal(PinVerifyStatus.Valid, result.Status);
            Assert.True(result.IsSuccess);
            Assert.Null(result.MigratedProtectedHash);
        }
        finally
        {
            WindowsAppLockService.ResetFailureState(scope);
        }
    }

    [Fact]
    public void VerifyPinDetailed_LegacyV1Pin_MigratesToV2AndVerifiesCleanly()
    {
        string scope = "test-v1-migrate-" + Guid.NewGuid().ToString("N");
        try
        {
            const string pin = "13579";
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] pinBytes = Encoding.UTF8.GetBytes(pin);
            byte[] combined = new byte[salt.Length + pinBytes.Length];
            Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
            Buffer.BlockCopy(pinBytes, 0, combined, salt.Length, pinBytes.Length);
            byte[] legacyHash = SHA256.HashData(combined);

            string legacyPayload = $"pin:v1:{Convert.ToHexString(salt)}:{Convert.ToHexString(legacyHash)}";
            string legacyProtected = DpapiCredentialProtector.Instance.Protect(legacyPayload);
            Assert.StartsWith(DpapiCredentialProtector.EnvelopePrefix, legacyProtected, StringComparison.Ordinal);

            PinVerificationResult migrated = WindowsAppLockService.VerifyPinDetailed(pin, legacyProtected, scope);
            Assert.Equal(PinVerifyStatus.ValidNeedsMigration, migrated.Status);
            Assert.True(migrated.IsSuccess);
            Assert.NotNull(migrated.MigratedProtectedHash);
            Assert.StartsWith("dpapi:v2:app-lock:", migrated.MigratedProtectedHash!, StringComparison.Ordinal);

            PinVerificationResult verifiedV2 = WindowsAppLockService.VerifyPinDetailed(
                pin,
                migrated.MigratedProtectedHash,
                scope);
            Assert.Equal(PinVerifyStatus.Valid, verifiedV2.Status);
            Assert.True(verifiedV2.IsSuccess);
            Assert.Null(verifiedV2.MigratedProtectedHash);
        }
        finally
        {
            WindowsAppLockService.ResetFailureState(scope);
        }
    }

    [Fact]
    public void VerifyPinDetailed_EnforcesExponentialLockoutBackoff_AndResetsOnSuccess()
    {
        string scope = "test-lockout-" + Guid.NewGuid().ToString("N");
        try
        {
            string stored = WindowsAppLockService.CreateProtectedPinHash("2468");
            DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

            for (int i = 0; i < 4; i++)
            {
                PinVerificationResult wrong = WindowsAppLockService.VerifyPinDetailed("0000", stored, scope, t0);
                Assert.Equal(PinVerifyStatus.InvalidPin, wrong.Status);
                Assert.Equal(TimeSpan.Zero, wrong.RetryAfter);
            }

            // 5th consecutive failure triggers a 15-second cooldown.
            PinVerificationResult fifth = WindowsAppLockService.VerifyPinDetailed("0000", stored, scope, t0);
            Assert.Equal(PinVerifyStatus.InvalidPin, fifth.Status);
            Assert.Equal(TimeSpan.FromSeconds(15), fifth.RetryAfter);

            // During cooldown, even the correct PIN is rejected with LockedOut.
            PinVerificationResult blocked = WindowsAppLockService.VerifyPinDetailed(
                "2468",
                stored,
                scope,
                t0.AddSeconds(5));
            Assert.Equal(PinVerifyStatus.LockedOut, blocked.Status);
            Assert.False(blocked.IsSuccess);
            Assert.Equal(TimeSpan.FromSeconds(10), blocked.RetryAfter);

            // After cooldown expires, a 6th consecutive failure triggers 30 seconds.
            PinVerificationResult sixth = WindowsAppLockService.VerifyPinDetailed(
                "0000",
                stored,
                scope,
                t0.AddSeconds(16));
            Assert.Equal(PinVerifyStatus.InvalidPin, sixth.Status);
            Assert.Equal(TimeSpan.FromSeconds(30), sixth.RetryAfter);

            // After 30s cooldown expires, correct PIN succeeds and resets failure count.
            PinVerificationResult recovered = WindowsAppLockService.VerifyPinDetailed(
                "2468",
                stored,
                scope,
                t0.AddSeconds(47));
            Assert.Equal(PinVerifyStatus.Valid, recovered.Status);
            Assert.True(recovered.IsSuccess);

            // A single subsequent wrong PIN must not trigger immediate lockout.
            PinVerificationResult postResetWrong = WindowsAppLockService.VerifyPinDetailed(
                "0000",
                stored,
                scope,
                t0.AddSeconds(48));
            Assert.Equal(PinVerifyStatus.InvalidPin, postResetWrong.Status);
            Assert.Equal(TimeSpan.Zero, postResetWrong.RetryAfter);
        }
        finally
        {
            WindowsAppLockService.ResetFailureState(scope);
        }
    }

    [Fact]
    public void VerifyPinDetailed_ClassifiesProtectionAndFormatErrors_WithoutIncrementingLockout()
    {
        string scope = "test-errors-" + Guid.NewGuid().ToString("N");
        try
        {
            string validStored = WindowsAppLockService.CreateProtectedPinHash("2468");
            DpapiCredentialProtector appLock = DpapiCredentialProtector.ForDomain(DpapiCredentialProtector.DomainAppLock);
            string malformedInner = appLock.Protect("pin:v2:500:AAAA:BBBB"); // iterations < 10_000
            string unknownPrefixInner = appLock.Protect("unknown-format");
            string tamperedCipher = "dpapi:v2:app-lock:" + Convert.ToBase64String("not-a-valid-dpapi-blob"u8);

            for (int i = 0; i < 6; i++)
            {
                PinVerificationResult protectionErr = WindowsAppLockService.VerifyPinDetailed("2468", tamperedCipher, scope);
                Assert.Equal(PinVerifyStatus.ProtectionError, protectionErr.Status);
                Assert.False(protectionErr.IsSuccess);

                PinVerificationResult badIter = WindowsAppLockService.VerifyPinDetailed("2468", malformedInner, scope);
                Assert.Equal(PinVerifyStatus.InvalidFormat, badIter.Status);

                PinVerificationResult badPrefix = WindowsAppLockService.VerifyPinDetailed("2468", unknownPrefixInner, scope);
                Assert.Equal(PinVerifyStatus.InvalidFormat, badPrefix.Status);
            }

            // None of the protection/format errors should have incremented PIN lockout count.
            PinVerificationResult stillAllowed = WindowsAppLockService.VerifyPinDetailed("2468", validStored, scope);
            Assert.Equal(PinVerifyStatus.Valid, stillAllowed.Status);
            Assert.True(stillAllowed.IsSuccess);
        }
        finally
        {
            WindowsAppLockService.ResetFailureState(scope);
        }
    }
}

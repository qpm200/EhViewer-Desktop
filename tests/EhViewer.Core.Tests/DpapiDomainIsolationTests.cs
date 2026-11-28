using System.Security.Cryptography;
using EhViewer.Core.Security;
using Xunit;

namespace EhViewer.Core.Tests.SecurityTests;

public sealed class DpapiDomainIsolationTests
{
    [Fact]
    public void ForDomain_ProducesDomainPrefixedEnvelopes_AndRoundTrips()
    {
        DpapiCredentialProtector cookies = DpapiCredentialProtector.ForDomain(DpapiCredentialProtector.DomainCookies);
        DpapiCredentialProtector appLock = DpapiCredentialProtector.ForDomain(DpapiCredentialProtector.DomainAppLock);

        Assert.Same(cookies, DpapiCredentialProtector.ForDomain("COOKIES"));
        Assert.NotSame(cookies, appLock);

        string cookieCipher = cookies.Protect("ipb_member_id=12345");
        string lockCipher = appLock.Protect("pin:v2:secret");

        Assert.StartsWith("dpapi:v2:cookies:", cookieCipher, StringComparison.Ordinal);
        Assert.StartsWith("dpapi:v2:app-lock:", lockCipher, StringComparison.Ordinal);
        Assert.True(cookies.IsProtected(cookieCipher));
        Assert.True(appLock.IsProtected(lockCipher));
        Assert.True(DpapiCredentialProtector.Instance.IsProtected(lockCipher));

        Assert.Equal("ipb_member_id=12345", cookies.Unprotect(cookieCipher));
        Assert.Equal("pin:v2:secret", appLock.Unprotect(lockCipher));
    }

    [Fact]
    public void ForDomain_RejectsCrossDomainAndDefaultInstanceDowngrade()
    {
        DpapiCredentialProtector cookies = DpapiCredentialProtector.ForDomain(DpapiCredentialProtector.DomainCookies);
        DpapiCredentialProtector appLock = DpapiCredentialProtector.ForDomain(DpapiCredentialProtector.DomainAppLock);

        string lockCipher = appLock.Protect("pin-secret-payload");

        Assert.Throws<CryptographicException>(() => cookies.Unprotect(lockCipher));
        Assert.Throws<CryptographicException>(() => DpapiCredentialProtector.Instance.Unprotect(lockCipher));
    }

    [Fact]
    public void ForDomain_UnprotectsLegacyV1Envelope_AndReportsNeedsReprotection()
    {
        DpapiCredentialProtector cookies = DpapiCredentialProtector.ForDomain(DpapiCredentialProtector.DomainCookies);

        string legacyV1 = DpapiCredentialProtector.Instance.Protect("legacy-cookie-value");
        string modernV2 = cookies.Protect("modern-cookie-value");

        Assert.StartsWith(DpapiCredentialProtector.EnvelopePrefix, legacyV1, StringComparison.Ordinal);
        Assert.Equal("legacy-cookie-value", cookies.Unprotect(legacyV1));
        Assert.True(cookies.NeedsReprotection(legacyV1));
        Assert.False(cookies.NeedsReprotection(modernV2));
        Assert.False(cookies.NeedsReprotection(null));
        Assert.False(DpapiCredentialProtector.Instance.NeedsReprotection(legacyV1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad:domain")]
    [InlineData("bad/domain")]
    [InlineData("bad domain")]
    [InlineData("中文域")]
    public void ForDomain_RejectsInvalidDomainNames(string invalidDomain)
    {
        Assert.Throws<ArgumentException>(() => DpapiCredentialProtector.ForDomain(invalidDomain));
    }

    [Fact]
    public void ForDomain_InvalidV2Base64AndMalformedHeader_ThrowsCryptographicException()
    {
        DpapiCredentialProtector cookies = DpapiCredentialProtector.ForDomain(DpapiCredentialProtector.DomainCookies);

        CryptographicException invalidBase64 = Assert.Throws<CryptographicException>(
            () => cookies.Unprotect("dpapi:v2:cookies:!!!not-base64!!!"));
        Assert.IsType<FormatException>(invalidBase64.InnerException);

        Assert.Throws<CryptographicException>(() => cookies.Unprotect("dpapi:v2:missing-colon"));
        Assert.Throws<CryptographicException>(() => cookies.Unprotect("dpapi:v2:cookies:"));
    }
}

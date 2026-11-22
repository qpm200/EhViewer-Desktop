using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EhViewer.Core.Client;
using EhViewer.Core.Network;
using EhViewer.Core.Security;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EhViewer.Core.Tests.NetworkTests;

/// <summary>
/// Network and security layer tests.
/// Covers: architecture boundary guard, DPAPI credential protection, <see cref="HostsDatabase"/>,
/// <see cref="EhHosts"/> resolution priority + RFC 8484 DoH, <see cref="SqliteCookieStore"/> persistence
/// and at-rest encryption, TLS certificate rejection against a real loopback self-signed server,
/// and multi-hop 3xx cookie capture through <see cref="EhHttpClientFactory"/>.
/// </summary>
public sealed class NetworkAndSecurityTests
{
    // ------------------------------------------------------------------
    // 1. Architecture boundary guard (EhViewer.Core must stay UI/Skia-free)
    // ------------------------------------------------------------------

    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Microsoft.UI",
        "Microsoft.Windows.",
        "Microsoft.Graphics.",
        "System.Windows",
        "System.Drawing",
        "WinRT",
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "SkiaSharp"
    ];

    [Fact]
    public void Architecture_CoreAssembly_ReferencesNoUiOrSkiaSharpAssemblies()
    {
        var coreAssembly = typeof(EhCookie).Assembly;
        var referenced = coreAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.NotEmpty(referenced);

        foreach (string prefix in ForbiddenAssemblyPrefixes)
        {
            Assert.DoesNotContain(
                referenced,
                name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Architecture_CoreAssembly_LoadsNoSkiaSharpTypes()
    {
        var coreAssembly = typeof(EhCookie).Assembly;
        Type[] types = coreAssembly.GetTypes();

        Assert.NotEmpty(types);
        Assert.DoesNotContain(
            types,
            t => (t.FullName ?? t.Name).StartsWith("SkiaSharp.", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // 2. DpapiCredentialProtector
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("hunter2")]
    [InlineData("")]
    [InlineData("中文密码-测试值")]
    [InlineData("ig=0123456789abcdef; ipb_member_id=42; ipb_pass_hash=deadbeef")]
    [InlineData("\U0001F600\U0001F5FF emoji payload")]
    public void Protector_Protect_Unprotect_RoundTrips(string plaintext)
    {
        var protector = DpapiCredentialProtector.Instance;

        string envelope = protector.Protect(plaintext);

        Assert.StartsWith(DpapiCredentialProtector.EnvelopePrefix, envelope, StringComparison.Ordinal);
        Assert.NotEqual(plaintext, envelope);
        Assert.Equal(plaintext, protector.Unprotect(envelope));
    }

    [Fact]
    public void Protector_LongValue_RoundTrips()
    {
        var protector = DpapiCredentialProtector.Instance;
        string plaintext = string.Concat(Enumerable.Repeat("长字符串-长字符串-abcd0123", 500));

        string envelope = protector.Protect(plaintext);

        Assert.True(envelope.Length > 1000);
        Assert.Equal(plaintext, protector.Unprotect(envelope));
    }

    [Fact]
    public void Protector_Protect_IsNonDeterministic()
    {
        var protector = DpapiCredentialProtector.Instance;

        string first = protector.Protect("same value");
        string second = protector.Protect("same value");

        Assert.NotEqual(first, second);
        Assert.Equal("same value", protector.Unprotect(first));
        Assert.Equal("same value", protector.Unprotect(second));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("plaintext", false)]
    [InlineData("DPAPI:V1:AAAA", false)]
    [InlineData("dpapi:v1:", true)]
    [InlineData("dpapi:v1:AQIDBA==", true)]
    public void Protector_IsProtected_OnlyMatchesExactEnvelopePrefix(string? value, bool expected)
    {
        Assert.Equal(expected, DpapiCredentialProtector.Instance.IsProtected(value));
    }

    [Fact]
    public void Protector_Unprotect_PlaintextWithoutEnvelope_Throws()
    {
        var protector = DpapiCredentialProtector.Instance;

        Assert.Throws<CryptographicException>(() => protector.Unprotect("plain-cookie-value"));
    }

    [Fact]
    public void Protector_Unprotect_InvalidBase64_Throws()
    {
        var protector = DpapiCredentialProtector.Instance;

        var ex = Assert.Throws<CryptographicException>(
            () => protector.Unprotect(DpapiCredentialProtector.EnvelopePrefix + "!!!not-base64!!!"));
        Assert.IsType<FormatException>(ex.InnerException);
    }

    [Fact]
    public void Protector_Unprotect_ValidBase64ButNotDpapiPayload_Throws()
    {
        var protector = DpapiCredentialProtector.Instance;
        string notDpapi = DpapiCredentialProtector.EnvelopePrefix + Convert.ToBase64String("this is not a DPAPI blob"u8);

        // Fail-closed: a well-formed envelope that cannot be decrypted must never degrade to the raw value.
        Assert.Throws<CryptographicException>(() => protector.Unprotect(notDpapi));
    }

    [Fact]
    public void Protector_Unprotect_EmptyEnvelopePayload_Throws()
    {
        var protector = DpapiCredentialProtector.Instance;

        Assert.Throws<CryptographicException>(
            () => protector.Unprotect(DpapiCredentialProtector.EnvelopePrefix));
    }

    [Fact]
    public void Protector_Unprotect_TamperedPayload_Throws()
    {
        var protector = DpapiCredentialProtector.Instance;
        string envelope = protector.Protect("tamper me");
        byte[] raw = Convert.FromBase64String(envelope[DpapiCredentialProtector.EnvelopePrefix.Length..]);
        raw[^1] ^= 0xFF;
        string tampered = DpapiCredentialProtector.EnvelopePrefix + Convert.ToBase64String(raw);

        Assert.Throws<CryptographicException>(() => protector.Unprotect(tampered));
    }

    [Fact]
    public void Protector_NullArguments_Throw()
    {
        var protector = DpapiCredentialProtector.Instance;

        Assert.Throws<ArgumentNullException>(() => protector.Protect(null!));
        Assert.Throws<ArgumentNullException>(() => protector.Unprotect(null!));
    }

    [Fact]
    public void Protector_ImplementsInterface_AndInstanceIsSingleton()
    {
        Assert.IsAssignableFrom<ICredentialProtector>(DpapiCredentialProtector.Instance);
        Assert.Same(DpapiCredentialProtector.Instance, DpapiCredentialProtector.Instance);
    }

    // ------------------------------------------------------------------
    // 3. HostsDatabase (mirrors upstream HostsTest.java)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("ni.hao", true)]
    [InlineData("a", true)]
    [InlineData("abc-123.example.com", true)]
    [InlineData("-leading-hyphen", true)] // upstream also accepts it (no leading-hyphen rule)
    [InlineData("ni.ha~o", false)]
    [InlineData(".ni.hao", false)]
    [InlineData("ni.hao.", false)]
    [InlineData("ni..hao.", false)]
    [InlineData(".", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("UPPER.CASE", false)]
    [InlineData("under_score.example.com", false)]
    [InlineData("space example.com", false)]
    public void HostsDatabase_IsValidHost_MatchesUpstream(string? host, bool expected)
    {
        Assert.Equal(expected, HostsDatabase.IsValidHost(host));
    }

    [Fact]
    public void HostsDatabase_IsValidHost_EnforcesLabelAndTotalLengthLimits()
    {
        Assert.True(HostsDatabase.IsValidHost(new string('a', 63)));
        Assert.False(HostsDatabase.IsValidHost(new string('a', 64)));

        string host253 = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));
        string host254 = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 62));

        Assert.Equal(253, host253.Length);
        Assert.Equal(254, host254.Length);
        Assert.True(HostsDatabase.IsValidHost(host253));
        Assert.False(HostsDatabase.IsValidHost(host254));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("324.0.0.1", false)]
    [InlineData("127.0.0.", false)]
    [InlineData("127.0.0", false)]
    [InlineData("-1.0.0", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("01.2.3.4", false)]
    [InlineData("256.1.1.1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HostsDatabase_IsValidIp_MatchesUpstream(string? ip, bool expected)
    {
        Assert.Equal(expected, HostsDatabase.IsValidIp(ip));
    }

    [Theory]
    [InlineData("::1", true)]
    [InlineData("::", true)]
    [InlineData("2a00:7c80:0:123::3a85", true)]
    [InlineData("2001:db8:85a3:0:0:8a2e:370:7334", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData(":1", false)]
    [InlineData("1::2::3", false)]
    [InlineData(":::", false)]
    [InlineData("::1::", false)]
    [InlineData("12345::1", false)]
    [InlineData("gggg::1", false)]
    public void HostsDatabase_IsValidIp_Ipv6Cases(string ip, bool expected)
    {
        Assert.Equal(expected, HostsDatabase.IsValidIp(ip));
    }

    [Fact]
    public void HostsDatabase_ToHostAddress_Ipv6_Returns16ByteAddress()
    {
        IPAddress? loopback = HostsDatabase.ToHostAddress("ehgt.org", "::1");
        Assert.NotNull(loopback);
        Assert.Equal(16, loopback.GetAddressBytes().Length);
        Assert.Equal(1, loopback.GetAddressBytes()[15]);

        const string cdnIp = "2a00:7c80:0:123::3a85";
        IPAddress? cdn = HostsDatabase.ToHostAddress("ehgt.org", cdnIp);
        Assert.NotNull(cdn);
        Assert.Equal(16, cdn.GetAddressBytes().Length);
        Assert.Equal(cdnIp, cdn.ToString());
    }

    [Fact]
    public void HostsDatabase_ToHostAddress_Ipv4_Returns4ByteAddress()
    {
        IPAddress? address = HostsDatabase.ToHostAddress("ni.hao", "127.0.0.1");

        Assert.NotNull(address);
        Assert.Equal(4, address.GetAddressBytes().Length);
        Assert.Equal("127.0.0.1", address.ToString());
    }

    [Fact]
    public void HostsDatabase_ToHostAddress_InvalidInput_ReturnsNull()
    {
        Assert.Null(HostsDatabase.ToHostAddress(null, "127.0.0.1"));
        Assert.Null(HostsDatabase.ToHostAddress("ni.hao", null));
        Assert.Null(HostsDatabase.ToHostAddress("ni.hao", "324.0.0.1"));
        Assert.Null(HostsDatabase.ToHostAddress(".ni.hao", "127.0.0.1"));
    }

    [Fact]
    public void HostsDatabase_Get_ReturnsStoredAddress_AndNullWhenMissing()
    {
        using var temp = new TempDirectory();
        using var hosts = new HostsDatabase(temp.File("hosts.db"));

        Assert.Null(hosts.Get("ni.hao"));

        Assert.True(hosts.Put("ni.hao", "127.0.0.1"));
        IPAddress? address = hosts.Get("ni.hao");
        Assert.NotNull(address);
        Assert.Equal("127.0.0.1", address.ToString());

        Assert.Null(hosts.Get(null));
        Assert.Null(hosts.Get(".ni.hao"));
    }

    [Fact]
    public void HostsDatabase_Put_InsertsThenUpdates()
    {
        using var temp = new TempDirectory();
        using var hosts = new HostsDatabase(temp.File("hosts.db"));

        Assert.Null(hosts.Get("ni.hao"));
        Assert.True(hosts.Put("ni.hao", "127.0.0.1"));
        Assert.Equal("127.0.0.1", hosts.Get("ni.hao")!.ToString());

        Assert.True(hosts.Put("ni.hao", "127.0.0.2"));
        Assert.Equal("127.0.0.2", hosts.Get("ni.hao")!.ToString());
        Assert.Single(hosts.GetAll());

        Assert.False(hosts.Put(".wo.hao", "127.0.0.1"));
        Assert.Null(hosts.Get(".wo.hao"));

        Assert.False(hosts.Put("wo.hao", ".127.0.0.1"));
        Assert.Null(hosts.Get("wo.hao"));

        Assert.False(hosts.Put(null, "127.0.0.1"));
        Assert.False(hosts.Put("wo.hao", null));
        Assert.False(hosts.Put(null, null));
        Assert.Null(hosts.Get("wo.hao"));
    }

    [Fact]
    public void HostsDatabase_Delete_RemovesEntry()
    {
        using var temp = new TempDirectory();
        using var hosts = new HostsDatabase(temp.File("hosts.db"));

        Assert.True(hosts.Put("ni.hao", "127.0.0.1"));
        Assert.Equal("127.0.0.1", hosts.Get("ni.hao")!.ToString());

        Assert.True(hosts.Put("ni.hao", "127.0.0.2"));
        hosts.Delete("ni.hao");

        Assert.Null(hosts.Get("ni.hao"));
        Assert.Empty(hosts.GetAll());

        // Deleting a missing / null host is a no-op.
        hosts.Delete("ni.hao");
        hosts.Delete(null);
        Assert.Null(hosts.Get("ni.hao"));
    }

    [Fact]
    public void HostsDatabase_GetAll_ReturnsEntriesInInsertionOrder()
    {
        using var temp = new TempDirectory();
        using var hosts = new HostsDatabase(temp.File("hosts.db"));

        Assert.Empty(hosts.GetAll());

        Assert.True(hosts.Put("ni.hao", "127.0.0.1"));
        Assert.True(hosts.Put("wo.hao", "127.0.0.2"));

        IReadOnlyList<HostEntry> all = hosts.GetAll();

        Assert.Equal(2, all.Count);
        Assert.Equal("ni.hao", all[0].Host);
        Assert.Equal("127.0.0.1", all[0].Ip);
        Assert.Equal("wo.hao", all[1].Host);
        Assert.Equal("127.0.0.2", all[1].Ip);
    }

    [Fact]
    public void HostsDatabase_GetList_ReturnsEveryRowForHost()
    {
        using var temp = new TempDirectory();
        string dbPath = temp.File("hosts.db");
        using (var hosts = new HostsDatabase(dbPath))
        {
            Assert.True(hosts.Put("ni.hao", "127.0.0.1"));

            // Put() upserts a single row per host; extra rows can only appear in a real hosts.db.
            ExecuteSql(dbPath, "INSERT INTO HOSTS (HOST, IP) VALUES ('ni.hao', '::1');");

            IReadOnlyList<IPAddress>? list = hosts.GetList("ni.hao");
            Assert.NotNull(list);
            Assert.Equal(2, list.Count);
            Assert.Contains(list, ip => ip.ToString() == "127.0.0.1");
            Assert.Contains(list, ip => ip.ToString() == "::1");

            Assert.Null(hosts.GetList(null));
            Assert.Null(hosts.GetList("bad host"));
            Assert.NotNull(hosts.GetList("missing.host"));
            Assert.Empty(hosts.GetList("missing.host")!);
        }
    }

    [Fact]
    public void HostsDatabase_Clear_RemovesAllRows()
    {
        using var temp = new TempDirectory();
        using var hosts = new HostsDatabase(temp.File("hosts.db"));

        Assert.True(hosts.Put("ni.hao", "127.0.0.1"));
        Assert.True(hosts.Put("wo.hao", "127.0.0.2"));

        hosts.Clear();

        Assert.Empty(hosts.GetAll());
        Assert.Null(hosts.Get("ni.hao"));
    }

    [Fact]
    public void HostsDatabase_PersistsAcrossReopen()
    {
        using var temp = new TempDirectory();
        string dbPath = temp.File("hosts.db");

        using (var hosts = new HostsDatabase(dbPath))
        {
            Assert.True(hosts.Put("ni.hao", "127.0.0.1"));
        }

        using var reopened = new HostsDatabase(dbPath);
        Assert.Equal("127.0.0.1", reopened.Get("ni.hao")!.ToString());
    }

    // ------------------------------------------------------------------
    // 4. EhHosts resolution priority + DNS over HTTPS
    // ------------------------------------------------------------------

    [Fact]
    public async Task EhHosts_LookupAsync_IpLiteral_ShortCircuitsWithoutDns()
    {
        using var temp = new TempDirectory();
        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        int resolverCalls = 0;
        using var hosts = new EhHosts(
            hostsDb,
            new EhClientSettings { ProxyType = EhProxyType.Direct },
            systemDnsResolver: (_, _) =>
            {
                Interlocked.Increment(ref resolverCalls);
                return Task.FromResult(new[] { IPAddress.Parse("10.0.0.1") });
            });

        IReadOnlyList<IPAddress> result = await hosts.LookupAsync("127.0.0.1");

        Assert.Single(result);
        Assert.Equal("127.0.0.1", result[0].ToString());
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public async Task EhHosts_LookupAsync_CustomHostsDatabase_TakesPriorityOverBuiltInAndDns()
    {
        using var temp = new TempDirectory();
        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        Assert.True(hostsDb.Put("e-hentai.org", "127.0.0.9"));

        var settings = new EhClientSettings
        {
            BuiltInHosts = true,
            BuiltInExHosts = true,
            ProxyType = EhProxyType.Direct
        };
        using var hosts = new EhHosts(hostsDb, settings, systemDnsResolver: FailingResolver());

        IReadOnlyList<IPAddress> result = await hosts.LookupAsync("E-hentai.org");

        Assert.Single(result);
        Assert.Equal("127.0.0.9", result[0].ToString());
    }

    [Fact]
    public async Task EhHosts_LookupAsync_CustomHostsDatabase_ReturnsEveryRow()
    {
        using var temp = new TempDirectory();
        string dbPath = temp.File("hosts.db");
        using var hostsDb = new HostsDatabase(dbPath);
        Assert.True(hostsDb.Put("custom.example", "10.1.2.3"));
        ExecuteSql(dbPath, "INSERT INTO HOSTS (HOST, IP) VALUES ('custom.example', '2a00:7c80:0:123::3a85');");

        using var hosts = new EhHosts(
            hostsDb,
            new EhClientSettings { BuiltInHosts = false, BuiltInExHosts = false, ProxyType = EhProxyType.Direct },
            systemDnsResolver: FailingResolver());

        IReadOnlyList<IPAddress> result = await hosts.LookupAsync("custom.example");

        Assert.Equal(2, result.Count);
        Assert.Contains(result, ip => ip.ToString() == "10.1.2.3");
        Assert.Contains(result, ip => ip.ToString() == "2a00:7c80:0:123::3a85");
    }

    [Fact]
    public async Task EhHosts_LookupAsync_BuiltInCommonHosts_ReturnsTableIps()
    {
        using var temp = new TempDirectory();
        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        var settings = new EhClientSettings
        {
            BuiltInHosts = true,
            BuiltInExHosts = false,
            UseDnsOverHttps = false,
            ProxyType = EhProxyType.Direct
        };
        using var hosts = new EhHosts(hostsDb, settings, systemDnsResolver: FailingResolver());

        IReadOnlyList<IPAddress> result = await hosts.LookupAsync(EhUrl.DomainE);

        Assert.NotEmpty(result);
        string[] expected =
        [
            "104.20.18.168", "104.20.19.168", "172.66.132.196", "172.66.140.62", "172.67.2.238"
        ];
        Assert.All(result, ip => Assert.Contains(ip.ToString(), expected));
    }

    [Fact]
    public async Task EhHosts_LookupAsync_BuiltInExHosts_ReturnsTableIps()
    {
        using var temp = new TempDirectory();
        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        var settings = new EhClientSettings
        {
            BuiltInHosts = false,
            BuiltInExHosts = true,
            UseDnsOverHttps = false,
            ProxyType = EhProxyType.Direct
        };
        using var hosts = new EhHosts(hostsDb, settings, systemDnsResolver: FailingResolver());

        IReadOnlyList<IPAddress> result = await hosts.LookupAsync(EhUrl.DomainEx);

        Assert.NotEmpty(result);
        Assert.All(result, ip => Assert.StartsWith("178.175.", ip.ToString(), StringComparison.Ordinal));
        Assert.Contains(result, ip => ip.ToString() == "178.175.128.251");
    }

    [Fact]
    public async Task EhHosts_LookupAsync_BuiltInHostsDisabled_FallsBackToSystemDns()
    {
        using var temp = new TempDirectory();
        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        var settings = new EhClientSettings
        {
            BuiltInHosts = false,
            BuiltInExHosts = false,
            UseDnsOverHttps = false,
            ProxyType = EhProxyType.Direct
        };
        var expected = IPAddress.Parse("203.0.113.7");
        string? resolvedHost = null;
        using var hosts = new EhHosts(
            hostsDb,
            settings,
            systemDnsResolver: (host, _) =>
            {
                resolvedHost = host;
                return Task.FromResult(new[] { expected });
            });

        IReadOnlyList<IPAddress> result = await hosts.LookupAsync(EhUrl.DomainE);

        Assert.Single(result);
        Assert.Equal(expected, result[0]);
        Assert.Equal(EhUrl.DomainE, resolvedHost);
    }

    [Fact]
    public async Task EhHosts_LookupAsync_SystemDnsEmpty_ThrowsSocketException()
    {
        using var temp = new TempDirectory();
        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        var settings = new EhClientSettings
        {
            BuiltInHosts = false,
            BuiltInExHosts = false,
            UseDnsOverHttps = false,
            ProxyType = EhProxyType.Direct
        };
        using var hosts = new EhHosts(
            hostsDb,
            settings,
            systemDnsResolver: (_, _) => Task.FromResult(Array.Empty<IPAddress>()));

        var ex = await Assert.ThrowsAsync<SocketException>(() => hosts.LookupAsync("no-such-host.example"));
        Assert.Equal(SocketError.HostNotFound, ex.SocketErrorCode);
    }

    [Fact]
    public async Task EhHosts_LookupAsync_DnsOverHttps_ParsesAAndAaaaRecords()
    {
        using var temp = new TempDirectory();
        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        var settings = new EhClientSettings
        {
            BuiltInHosts = false,
            BuiltInExHosts = false,
            UseDnsOverHttps = true,
            DohEndpoint = "https://doh.example/dns-query",
            ProxyType = EhProxyType.Direct
        };

        byte[] aRecord = [93, 184, 216, 34];
        byte[] aaaaRecord = IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946").GetAddressBytes();
        var handler = new DohHandler(qtype => qtype switch
        {
            1 => DnsWire.BuildResponse("doh.example", 1, aRecord),
            28 => DnsWire.BuildResponse("doh.example", 28, aaaaRecord),
            _ => null
        });

        using var hosts = new EhHosts(hostsDb, settings, handler, systemDnsResolver: FailingResolver());

        IReadOnlyList<IPAddress> result = await hosts.LookupAsync("doh.example");

        Assert.Equal(2, result.Count);
        Assert.Contains(result, ip => ip.ToString() == "93.184.216.34");
        Assert.Contains(result, ip => ip.ToString() == "2606:2800:220:1:248:1893:25c8:1946");

        var requests = handler.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, r => Assert.Equal(HttpMethod.Post.Method, r.Method));
        Assert.All(requests, r => Assert.Equal("application/dns-message", r.ContentType));
        Assert.All(requests, r => Assert.Contains("application/dns-message", r.Accept, StringComparison.Ordinal));
        Assert.Contains(requests, r => r.QType == 1);
        Assert.Contains(requests, r => r.QType == 28);
    }

    [Fact]
    public async Task EhHosts_LookupAsync_DnsOverHttps_ServerError_FallsBackToSystemDns()
    {
        using var temp = new TempDirectory();
        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        var settings = new EhClientSettings
        {
            BuiltInHosts = false,
            BuiltInExHosts = false,
            UseDnsOverHttps = true,
            DohEndpoint = "https://doh.example/dns-query",
            ProxyType = EhProxyType.Direct
        };
        var handler = new DohHandler(_ => null); // null => HTTP 500
        var fallback = IPAddress.Parse("198.51.100.23");

        using var hosts = new EhHosts(
            hostsDb,
            settings,
            handler,
            systemDnsResolver: (_, _) => Task.FromResult(new[] { fallback }));

        IReadOnlyList<IPAddress> result = await hosts.LookupAsync("tls-failure.example");

        Assert.Single(result);
        Assert.Equal(fallback, result[0]);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task EhHosts_LookupAsync_DnsOverHttps_ServfailResponse_FallsBackToSystemDns()
    {
        using var temp = new TempDirectory();
        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        var settings = new EhClientSettings
        {
            BuiltInHosts = false,
            BuiltInExHosts = false,
            UseDnsOverHttps = true,
            DohEndpoint = "https://doh.example/dns-query",
            ProxyType = EhProxyType.Direct
        };
        var handler = new DohHandler(qtype => DnsWire.BuildResponse("nx.example", qtype, [], rcode: 3));
        var fallback = IPAddress.Parse("198.51.100.24");

        using var hosts = new EhHosts(
            hostsDb,
            settings,
            handler,
            systemDnsResolver: (_, _) => Task.FromResult(new[] { fallback }));

        IReadOnlyList<IPAddress> result = await hosts.LookupAsync("nx.example");

        Assert.Single(result);
        Assert.Equal(fallback, result[0]);
    }

    [Fact]
    public async Task EhHosts_LookupAsync_BuiltInTableIncludesBothIpv4AndIpv6()
    {
        using var temp = new TempDirectory();
        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        var settings = new EhClientSettings
        {
            BuiltInHosts = true,
            BuiltInExHosts = false,
            UseDnsOverHttps = false,
            ProxyType = EhProxyType.Direct
        };
        using var hosts = new EhHosts(hostsDb, settings, systemDnsResolver: FailingResolver());

        IReadOnlyList<IPAddress> result = await hosts.LookupAsync("ehgt.org");

        Assert.Equal("e-hentai.org", EhUrl.DomainE);
        Assert.Equal("exhentai.org", EhUrl.DomainEx);
        Assert.Contains(result, ip => ip.AddressFamily == AddressFamily.InterNetwork);
        Assert.Contains(result, ip => ip.AddressFamily == AddressFamily.InterNetworkV6);
        Assert.All(result, ip => Assert.Equal(
            ip.AddressFamily == AddressFamily.InterNetworkV6 ? 16 : 4,
            ip.GetAddressBytes().Length));
    }

    // ------------------------------------------------------------------
    // 5. SqliteCookieStore
    // ------------------------------------------------------------------

    [Fact]
    public void CookieStore_PersistentCookies_SurviveReopen_SessionCookiesDoNot()
    {
        using var temp = new TempDirectory();
        string dbPath = temp.File("cookie.db");
        var urlEh = new Uri("http://www.ehviewer.com/");
        var urlNmb = new Uri("http://h.nimingban.com/");
        long future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();

        using (var store = new SqliteCookieStore(dbPath))
        {
            store.SaveFromResponse(urlEh,
            [
                "user=1234567890; Domain=ehviewer.com; Path=/",                 // session cookie
                $"level=999; Domain=www.ehviewer.com; Path=/; Expires={FormatExpires(future)}",
                $"speed=10; Domain=www.ehviewer.com; Path=/; Max-Age=3600"
            ]);
            store.SaveFromResponse(urlNmb,
            [
                $"hash=0987654321; Domain=nimingban.com; Path=/; Expires={FormatExpires(future)}"
            ]);

            IReadOnlyList<EhCookie> beforeReopen = store.GetCookies(urlEh);
            Assert.Equal(3, beforeReopen.Count);
            Assert.Equal("1234567890", beforeReopen.Single(c => c.Name == "user").Value);
        }

        using var reopened = new SqliteCookieStore(dbPath);

        IReadOnlyList<EhCookie> afterReopen = reopened.GetCookies(urlEh);
        Assert.Equal(2, afterReopen.Count);
        Assert.DoesNotContain(afterReopen, c => c.Name == "user");
        Assert.Equal("999", afterReopen.Single(c => c.Name == "level").Value);
        Assert.Equal("10", afterReopen.Single(c => c.Name == "speed").Value);

        IReadOnlyList<EhCookie> nmb = reopened.GetCookies(urlNmb);
        Assert.Single(nmb);
        Assert.Equal("0987654321", nmb[0].Value);
    }

    [Fact]
    public void CookieStore_SaveFromResponse_ReplacesExistingCookie()
    {
        using var temp = new TempDirectory();
        string dbPath = temp.File("cookie.db");
        var url = new Uri("http://www.ehviewer.com/");

        using (var store = new SqliteCookieStore(dbPath))
        {
            store.SaveFromResponse(url, ["level=999; Domain=www.ehviewer.com; Path=/; Max-Age=3600"]);
            store.SaveFromResponse(url, ["level=0; Domain=www.ehviewer.com; Path=/; Max-Age=3600"]);

            IReadOnlyList<EhCookie> cookies = store.GetCookies(url);
            Assert.Single(cookies);
            Assert.Equal("0", cookies[0].Value);
        }

        using var reopened = new SqliteCookieStore(dbPath);
        IReadOnlyList<EhCookie> persisted = reopened.GetCookies(url);
        Assert.Single(persisted);
        Assert.Equal("0", persisted[0].Value);
    }

    [Fact]
    public void CookieStore_ExpiredCookie_RemovesPersistentRow()
    {
        using var temp = new TempDirectory();
        string dbPath = temp.File("cookie.db");
        var url = new Uri("http://www.ehviewer.com/");

        using (var store = new SqliteCookieStore(dbPath))
        {
            store.SaveFromResponse(url, ["level=999; Domain=www.ehviewer.com; Path=/; Max-Age=3600"]);
            store.SaveFromResponse(url,
            [
                "level=0; Domain=www.ehviewer.com; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT"
            ]);

            Assert.Empty(store.GetCookies(url));
            Assert.Equal(0, CountCookieRows(dbPath));
        }

        using var reopened = new SqliteCookieStore(dbPath);
        Assert.Empty(reopened.GetCookies(url));
    }

    [Fact]
    public void CookieStore_NonPersistentCookie_ReplacingPersistent_RemovesRow()
    {
        using var temp = new TempDirectory();
        string dbPath = temp.File("cookie.db");
        var url = new Uri("http://www.ehviewer.com/");

        using (var store = new SqliteCookieStore(dbPath))
        {
            store.SaveFromResponse(url, ["level=999; Domain=www.ehviewer.com; Path=/; Max-Age=3600"]);
            Assert.Equal(1, CountCookieRows(dbPath));

            store.SaveFromResponse(url, ["level=0; Domain=www.ehviewer.com; Path=/"]);

            IReadOnlyList<EhCookie> cookies = store.GetCookies(url);
            Assert.Single(cookies);
            Assert.Equal("0", cookies[0].Value);
            Assert.False(cookies[0].Persistent);
            Assert.Equal(0, CountCookieRows(dbPath));
        }

        using var reopened = new SqliteCookieStore(dbPath);
        Assert.Empty(reopened.GetCookies(url));
    }

    [Fact]
    public void CookieStore_Clear_RemovesCookiesAndRows()
    {
        using var temp = new TempDirectory();
        string dbPath = temp.File("cookie.db");
        var url = new Uri("http://www.ehviewer.com/");

        using (var store = new SqliteCookieStore(dbPath))
        {
            store.SaveFromResponse(url, ["user=1234567890; Domain=ehviewer.com; Path=/; Max-Age=3600"]);
            Assert.Single(store.GetCookies(url));

            store.Clear();

            Assert.Empty(store.GetCookies(url));
            Assert.Equal(0, CountCookieRows(dbPath));

            // Clear() also drops the id map, so a later save must insert cleanly.
            store.SaveFromResponse(url, ["user=42; Domain=ehviewer.com; Path=/; Max-Age=3600"]);
            Assert.Equal("42", store.GetCookies(url).Single().Value);
        }

        using var reopened = new SqliteCookieStore(dbPath);
        Assert.Equal("42", reopened.GetCookies(url).Single().Value);
    }

    [Fact]
    public void CookieStore_GetCookies_SortsByPathLengthDescending()
    {
        using var temp = new TempDirectory();
        using var store = new SqliteCookieStore(temp.File("cookie.db"));
        var url = new Uri("http://www.ehviewer.com/long/long/long/");

        store.SaveFromResponse(url,
        [
            "user=1234567890; Domain=ehviewer.com; Path=/",
            "supersuperme=99999; Domain=ehviewer.com; Path=/long/",
            "a=b; Domain=ehviewer.com; Path=/long/long/long/",
            "speed=100; Domain=ehviewer.com; Path=/long/long/"
        ]);

        IReadOnlyList<EhCookie> cookies = store.GetCookies(url);

        Assert.Equal(4, cookies.Count);
        Assert.Equal(new[] { "a", "speed", "supersuperme", "user" }, cookies.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { 16, 11, 6, 1 }, cookies.Select(c => c.Path.Length).ToArray());
    }

    [Fact]
    public void CookieStore_ValuesAreEncryptedAtRest()
    {
        using var temp = new TempDirectory();
        string dbPath = temp.File("cookie.db");
        var url = new Uri("https://e-hentai.org/");
        const string secret = "SUPER_SECRET_COOKIE_VALUE";

        using (var store = new SqliteCookieStore(dbPath))
        {
            store.SaveFromResponse(url, [$"ipb_pass_hash={secret}; Domain=e-hentai.org; Path=/; Max-Age=3600"]);
            Assert.Equal(secret, store.GetCookies(url).Single().Value);
        }

        string[] rawValues = ReadRawCookieValues(dbPath);
        Assert.Single(rawValues);
        Assert.StartsWith(DpapiCredentialProtector.EnvelopePrefix, rawValues[0], StringComparison.Ordinal);
        Assert.DoesNotContain(secret, rawValues[0], StringComparison.Ordinal);
        Assert.Equal(secret, DpapiCredentialProtector.Instance.Unprotect(rawValues[0]));
    }

    [Fact]
    public void CookieStore_PlaintextRows_AreDeletedOnLoad_FailClosed()
    {
        using var temp = new TempDirectory();
        string dbPath = temp.File("cookie.db");
        var url = new Uri("https://e-hentai.org/");
        long future = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();

        using (var store = new SqliteCookieStore(dbPath))
        {
            store.SaveFromResponse(url, ["legit=ok; Domain=e-hentai.org; Path=/; Max-Age=3600"]);
        }

        // Simulate a tampered / legacy plaintext row.
        ExecuteSql(dbPath, $"""
            INSERT INTO OK_HTTP_3_COOKIE (NAME, VALUE, EXPIRES_AT, DOMAIN, PATH, SECURE, HTTP_ONLY, PERSISTENT, HOST_ONLY)
            VALUES ('plaintext', 'plaintext-secret', {future}, 'e-hentai.org', '/', 0, 0, 1, 0);
            """);
        Assert.Equal(2, CountCookieRows(dbPath));

        using (var reopened = new SqliteCookieStore(dbPath))
        {
            IReadOnlyList<EhCookie> cookies = reopened.GetCookies(url);
            Assert.Single(cookies);
            Assert.Equal("legit", cookies[0].Name);
            Assert.DoesNotContain(cookies, c => c.Name == "plaintext");
        }

        Assert.Equal(1, CountCookieRows(dbPath));
        Assert.Equal(0, CountCookieRows(dbPath, "plaintext"));
    }

    [Fact]
    public void CookieStore_ImportFromDatabase_MigratesPlaintextAndroidDatabase()
    {
        using var temp = new TempDirectory();
        string androidDb = temp.File("okhttp3-cookie.db");
        string targetDb = temp.File("cookie.db");
        long future = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();

        CreateCookieSchema(androidDb);
        ExecuteSql(androidDb, $"""
            INSERT INTO OK_HTTP_3_COOKIE (NAME, VALUE, EXPIRES_AT, DOMAIN, PATH, SECURE, HTTP_ONLY, PERSISTENT, HOST_ONLY) VALUES
                ('ipb_member_id', '12345', {future}, 'e-hentai.org', '/', 1, 1, 1, 0),
                ('ipb_pass_hash', 'abcdef', {future}, 'e-hentai.org', '/', 1, 1, 1, 0),
                ('expired', 'gone', 1, 'e-hentai.org', '/', 0, 0, 1, 0),
                ('session-only', 'gone', {future}, 'e-hentai.org', '/', 0, 0, 0, 0);
            """);

        using (var store = new SqliteCookieStore(targetDb))
        {
            int imported = store.ImportFromDatabase(androidDb, allowUnprotectedSource: true);

            Assert.Equal(2, imported);
            IReadOnlyList<EhCookie> cookies = store.GetCookies(new Uri("https://e-hentai.org/"));
            Assert.Equal(2, cookies.Count);
            Assert.Equal("12345", cookies.Single(c => c.Name == "ipb_member_id").Value);
            Assert.Equal("abcdef", cookies.Single(c => c.Name == "ipb_pass_hash").Value);
            Assert.True(store.HasSignedIn);
        }

        // Migrated values must be re-encrypted with the local protector.
        string[] raw = ReadRawCookieValues(targetDb);
        Assert.Equal(2, raw.Length);
        Assert.All(raw, value => Assert.StartsWith(DpapiCredentialProtector.EnvelopePrefix, value, StringComparison.Ordinal));
        string[] decrypted = raw.Select(DpapiCredentialProtector.Instance.Unprotect).ToArray();
        Assert.Contains("12345", decrypted);
        Assert.Contains("abcdef", decrypted);
    }

    [Fact]
    public void CookieStore_ImportFromDatabase_RejectsUnprotectedSourceWhenDisallowed()
    {
        using var temp = new TempDirectory();
        string androidDb = temp.File("okhttp3-cookie.db");
        long future = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();

        CreateCookieSchema(androidDb);
        ExecuteSql(androidDb, $"""
            INSERT INTO OK_HTTP_3_COOKIE (NAME, VALUE, EXPIRES_AT, DOMAIN, PATH, SECURE, HTTP_ONLY, PERSISTENT, HOST_ONLY)
            VALUES ('ipb_member_id', '12345', {future}, 'e-hentai.org', '/', 0, 0, 1, 0);
            """);

        using var store = new SqliteCookieStore(temp.File("cookie.db"));

        Assert.Equal(0, store.ImportFromDatabase(androidDb, allowUnprotectedSource: false));
        Assert.Empty(store.GetCookies(new Uri("https://e-hentai.org/")));
    }

    [Fact]
    public void CookieStore_LoadForRequest_InjectsTipsCookieForEDomain()
    {
        using var temp = new TempDirectory();
        using var store = new SqliteCookieStore(temp.File("cookie.db"));
        var eUrl = new Uri("https://e-hentai.org/g/123/abc/");

        store.SaveFromResponse(eUrl, ["ipb_member_id=1; Path=/; Max-Age=3600"]);

        IReadOnlyList<EhCookie> cookies = store.LoadForRequest(eUrl);

        EhCookie tips = cookies.Single(c => c.Name == EhConfig.KeyContentWarning);
        Assert.Equal(EhConfig.ContentWarningNotShow, tips.Value);
        Assert.Equal(EhUrl.DomainE, tips.Domain);
        Assert.True(tips.Persistent);
        Assert.False(tips.HostOnly);
        Assert.Equal(SqliteCookieStore.TipsCookie.Value, tips.Value);
    }

    [Fact]
    public void CookieStore_LoadForRequest_DoesNotInjectTipsCookieForOtherHosts()
    {
        using var temp = new TempDirectory();
        using var store = new SqliteCookieStore(temp.File("cookie.db"));
        var exUrl = new Uri("https://exhentai.org/");

        Assert.Empty(store.LoadForRequest(exUrl));

        var unrelated = new Uri("https://example.com/");
        store.SaveFromResponse(unrelated, ["a=1; Path=/; Max-Age=3600"]);
        IReadOnlyList<EhCookie> cookies = store.LoadForRequest(unrelated);
        Assert.Single(cookies);
        Assert.DoesNotContain(cookies, c => c.Name == EhConfig.KeyContentWarning);
    }

    [Fact]
    public void CookieStore_LoadForRequest_StripsUconfigAndNwOnEDomain()
    {
        using var temp = new TempDirectory();
        using var store = new SqliteCookieStore(temp.File("cookie.db"));
        var eUrl = new Uri("https://e-hentai.org/");

        store.SaveFromResponse(eUrl,
        [
            "uconfig=abc123; Path=/; Max-Age=3600",
            "nw=0; Path=/; Max-Age=3600",
            "keep=1; Path=/; Max-Age=3600"
        ]);

        IReadOnlyList<EhCookie> cookies = store.LoadForRequest(eUrl);

        Assert.DoesNotContain(cookies, c => c.Name == EhConfig.KeyUconfig);
        Assert.Contains(cookies, c => c.Name == "keep");

        EhCookie nw = Assert.Single(cookies, c => c.Name == EhConfig.KeyContentWarning);
        Assert.Equal(EhConfig.ContentWarningNotShow, nw.Value);

        // The raw GetCookies view is unfiltered, so the stored nw=0 is still readable there.
        Assert.Contains(store.GetCookies(eUrl), c => c.Name == EhConfig.KeyUconfig);
        Assert.Contains(store.GetCookies(eUrl), c => c.Name == EhConfig.KeyContentWarning && c.Value == "0");
    }

    [Fact]
    public void CookieStore_HasSignedIn_RequiresMemberIdAndPassHash()
    {
        using var temp = new TempDirectory();
        using var store = new SqliteCookieStore(temp.File("cookie.db"));
        var eUrl = new Uri(EhUrl.HostE);

        Assert.False(store.HasSignedIn);

        store.SaveFromResponse(eUrl, ["ipb_member_id=12345; Path=/; Max-Age=3600"]);
        Assert.False(store.HasSignedIn);

        store.SaveFromResponse(eUrl, ["ipb_pass_hash=abcdef; Path=/; Max-Age=3600"]);
        Assert.True(store.HasSignedIn);
        Assert.True(store.Contains(eUrl, SqliteCookieStore.KeyIpbMemberId));
        Assert.True(store.Contains(eUrl, SqliteCookieStore.KeyIpbPassHash));

        store.SignOut();
        Assert.False(store.HasSignedIn);
    }

    [Fact]
    public void CookieStore_GetCookieHeader_JoinsNameValuePairsLongestPathFirst()
    {
        using var temp = new TempDirectory();
        using var store = new SqliteCookieStore(temp.File("cookie.db"));
        var url = new Uri("https://example.com/sub/page");

        store.SaveFromResponse(url, ["a=1; Path=/; Max-Age=3600", "b=2; Path=/sub; Max-Age=3600"]);

        string header = store.GetCookieHeader(url);

        Assert.Equal("b=2; a=1", header);
        Assert.Equal("b=2; a=1", SqliteCookieStore.FormatCookieHeader(store.LoadForRequest(url)));
    }

    [Theory]
    [InlineData("a=1", true, false)]                              // session cookie
    [InlineData("a=1; Max-Age=60", true, true)]
    [InlineData("a=1; Expires=Wed, 01 Jan 2031 00:00:00 GMT", true, true)]
    [InlineData("=1", false, false)]                              // empty name
    [InlineData("novalue", false, false)]                         // no '='
    [InlineData("", false, false)]
    public void CookieStore_SaveFromResponse_ParsesUpstreamCookieShapes(string header, bool parses, bool persistent)
    {
        using var temp = new TempDirectory();
        using var store = new SqliteCookieStore(temp.File("cookie.db"));
        var url = new Uri("https://example.com/");

        store.SaveFromResponse(url, [header]);

        IReadOnlyList<EhCookie> cookies = store.GetCookies(url);
        if (!parses)
        {
            Assert.Empty(cookies);
            return;
        }

        EhCookie cookie = Assert.Single(cookies);
        Assert.Equal("a", cookie.Name);
        Assert.Equal(persistent, cookie.Persistent);
        Assert.Equal("example.com", cookie.Domain);
        Assert.True(cookie.HostOnly);
        Assert.Equal("/", cookie.Path);
    }

    [Fact]
    public void CookieStore_SaveFromResponse_MaxAgeZeroDeletesCookie()
    {
        using var temp = new TempDirectory();
        string dbPath = temp.File("cookie.db");
        var url = new Uri("https://example.com/");

        using var store = new SqliteCookieStore(dbPath);
        store.SaveFromResponse(url, ["a=1; Path=/; Max-Age=3600"]);
        Assert.Single(store.GetCookies(url));

        store.SaveFromResponse(url, ["a=1; Path=/; Max-Age=0"]);

        Assert.Empty(store.GetCookies(url));
        Assert.Equal(0, CountCookieRows(dbPath));
    }

    // ------------------------------------------------------------------
    // 6. Real loopback TLS: a self-signed certificate must be rejected
    // ------------------------------------------------------------------

    [Fact]
    public async Task HttpClientFactory_RejectsSelfSignedLoopbackCertificate()
    {
        using var temp = new TempDirectory();
        using var tlsServer = new LoopbackTlsServer();
        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        using var cookieStore = new SqliteCookieStore(temp.File("cookie.db"));

        var settings = new EhClientSettings
        {
            ProxyType = EhProxyType.Direct,
            DomainFronting = false,
            BuiltInHosts = false,
            BuiltInExHosts = false,
            UseDnsOverHttps = false
        };

        using var hosts = new EhHosts(hostsDb, settings, systemDnsResolver: FailingResolver("127.0.0.1 must not hit system DNS"));
        using var factory = new EhHttpClientFactory(cookieStore, hosts, settings);
        HttpClient client = factory.StandardClient;

        var url = new Uri($"https://127.0.0.1:{tlsServer.Port}/");

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(15)));

        // The failure must come from certificate validation, not from an unreachable endpoint.
        Assert.True(tlsServer.HandshakeAttempts > 0, "the loopback TLS server was never reached");
        Assert.Contains(Flatten(ex), e => e is AuthenticationException);
    }

    [Fact]
    public async Task HttpClientFactory_RejectsSelfSignedCertificateForLocalhostHostname()
    {
        using var temp = new TempDirectory();
        using var tlsServer = new LoopbackTlsServer();
        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        using var cookieStore = new SqliteCookieStore(temp.File("cookie.db"));

        // Force the hostname through the custom hosts table so the SAN/CN do not matter:
        // the certificate chain is still untrusted, so validation must fail.
        Assert.True(hostsDb.Put("localhost", "127.0.0.1"));

        var settings = new EhClientSettings
        {
            ProxyType = EhProxyType.Direct,
            DomainFronting = false,
            BuiltInHosts = false,
            BuiltInExHosts = false,
            UseDnsOverHttps = false
        };

        using var hosts = new EhHosts(hostsDb, settings, systemDnsResolver: FailingResolver());
        using var factory = new EhHttpClientFactory(cookieStore, hosts, settings);

        var url = new Uri($"https://localhost:{tlsServer.Port}/");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => factory.StandardClient.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(tlsServer.HandshakeAttempts > 0);
    }

    // ------------------------------------------------------------------
    // 7. Multi-hop 3xx cookie capture through EhHttpClientFactory
    // ------------------------------------------------------------------

    [Fact]
    public async Task HttpClientFactory_PersistsCookiesFromEveryRedirectHop()
    {
        using var temp = new TempDirectory();
        string dbPath = temp.File("cookie.db");

        using var server = new LoopbackHttpServer(path => path switch
        {
            "/start" => LoopbackResponse.Found("/hop2", "hop1=one; Path=/; Max-Age=3600"),
            "/hop2" => LoopbackResponse.Found("/final", "hop2=two; Path=/; Max-Age=3600"),
            "/final" => LoopbackResponse.Ok("done"),
            _ => LoopbackResponse.NotFound()
        });

        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        var settings = new EhClientSettings
        {
            ProxyType = EhProxyType.Direct,
            DomainFronting = false,
            BuiltInHosts = false,
            BuiltInExHosts = false,
            UseDnsOverHttps = false
        };

        using var hosts = new EhHosts(hostsDb, settings, systemDnsResolver: FailingResolver("127.0.0.1 must not hit system DNS"));
        using (var cookieStore = new SqliteCookieStore(dbPath))
        using (var factory = new EhHttpClientFactory(cookieStore, hosts, settings))
        {
            var url = new Uri($"http://127.0.0.1:{server.Port}/start");
            using HttpResponseMessage response =
                await factory.StandardClient.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("done", await response.Content.ReadAsStringAsync());

            var origin = new Uri($"http://127.0.0.1:{server.Port}/");
            Assert.True(cookieStore.Contains(origin, "hop1"), "cookie set on the first hop was not captured");
            Assert.True(cookieStore.Contains(origin, "hop2"), "cookie set on the second hop was not captured");
            Assert.Equal("one", cookieStore.GetCookies(origin).Single(c => c.Name == "hop1").Value);
            Assert.Equal("two", cookieStore.GetCookies(origin).Single(c => c.Name == "hop2").Value);
        }

        // The follow-up hop must have carried the cookie set by the previous hop.
        var hop2Request = Assert.Single(server.Requests, r => r.Path == "/hop2");
        Assert.True(hop2Request.Headers.TryGetValue("Cookie", out string? hop2Cookie));
        Assert.Contains("hop1=one", hop2Cookie!, StringComparison.Ordinal);

        // Both hops were persisted (encrypted) rather than kept in memory only.
        Assert.Equal(2, CountCookieRows(dbPath));
        Assert.All(ReadRawCookieValues(dbPath),
            value => Assert.StartsWith(DpapiCredentialProtector.EnvelopePrefix, value, StringComparison.Ordinal));
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public async Task HttpClientFactory_ImageClient_DoesNotFollowRedirects()
    {
        using var temp = new TempDirectory();

        using var server = new LoopbackHttpServer(path => path switch
        {
            "/start" => LoopbackResponse.Found("/hop2", "hop1=one; Path=/; Max-Age=3600"),
            "/hop2" => LoopbackResponse.Ok("followed"),
            _ => LoopbackResponse.NotFound()
        });

        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        var settings = new EhClientSettings
        {
            ProxyType = EhProxyType.Direct,
            DomainFronting = false,
            BuiltInHosts = false,
            BuiltInExHosts = false
        };

        using var hosts = new EhHosts(hostsDb, settings, systemDnsResolver: FailingResolver());
        using var cookieStore = new SqliteCookieStore(temp.File("cookie.db"));
        using var factory = new EhHttpClientFactory(cookieStore, hosts, settings);

        var url = new Uri($"http://127.0.0.1:{server.Port}/start");
        using HttpResponseMessage response =
            await factory.ImageClient.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/hop2", response.Headers.Location!.ToString());
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Single(server.Requests);

        // Cookies set on the 3xx hop are still captured even when the redirect is not followed.
        var origin = new Uri($"http://127.0.0.1:{server.Port}/");
        Assert.True(cookieStore.Contains(origin, "hop1"));
    }

    [Fact]
    public async Task HttpClientFactory_StopsAfterMaximumRedirects()
    {
        using var temp = new TempDirectory();

        using var server = new LoopbackHttpServer(path =>
            LoopbackResponse.Found("/loop", $"hop={Uri.EscapeDataString(path)}; Path=/; Max-Age=3600"));

        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        var settings = new EhClientSettings
        {
            ProxyType = EhProxyType.Direct,
            DomainFronting = false,
            BuiltInHosts = false,
            BuiltInExHosts = false
        };

        using var hosts = new EhHosts(hostsDb, settings, systemDnsResolver: FailingResolver());
        using var cookieStore = new SqliteCookieStore(temp.File("cookie.db"));
        using var factory = new EhHttpClientFactory(cookieStore, hosts, settings);

        var url = new Uri($"http://127.0.0.1:{server.Port}/loop");

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => factory.StandardClient.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Contains("redirection", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(21, server.Requests.Count);
    }

    [Fact]
    public async Task SqliteCookieStore_EnablesWal_And_HttpClientFactory_ReusesInvokersAcrossRequests()
    {
        using var temp = new TempDirectory();
        using var server = new LoopbackHttpServer(path => LoopbackResponse.Ok($"ok:{path}"));

        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        var settings = new EhClientSettings
        {
            ProxyType = EhProxyType.Direct,
            DomainFronting = false,
            BuiltInHosts = false,
            BuiltInExHosts = false
        };

        using var hosts = new EhHosts(hostsDb, settings, systemDnsResolver: FailingResolver());
        string cookieDbPath = temp.File("cookie.db");
        using (var cookieStore = new SqliteCookieStore(cookieDbPath))
        using (var factory = new EhHttpClientFactory(cookieStore, hosts, settings))
        {
            var url1 = new Uri($"http://127.0.0.1:{server.Port}/one");
            var url2 = new Uri($"http://127.0.0.1:{server.Port}/two");
            Assert.Equal("ok:/one", await factory.StandardClient.GetStringAsync(url1).WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal("ok:/two", await factory.ImageClient.GetStringAsync(url2).WaitAsync(TimeSpan.FromSeconds(15)));

            factory.InvalidateProxyConfiguration();
            Assert.Equal("ok:/one", await factory.StandardClient.GetStringAsync(url1).WaitAsync(TimeSpan.FromSeconds(15)));
        }

        using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={cookieDbPath};Mode=ReadOnly;Pooling=False");
        raw.Open();
        using Microsoft.Data.Sqlite.SqliteCommand cmd = raw.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", cmd.ExecuteScalar()?.ToString(), ignoreCase: true);
    }

    [Fact]
    public void HostsDatabase_EnablesWalAndHostIndex()
    {
        using var temp = new TempDirectory();
        string hostsDbPath = temp.File("hosts.db");
        using (var hostsDb = new HostsDatabase(hostsDbPath))
        {
            Assert.True(hostsDb.Put("example.org", "127.0.0.1"));
        }

        using var raw = new SqliteConnection($"Data Source={hostsDbPath};Mode=ReadOnly;Pooling=False");
        raw.Open();
        using (SqliteCommand journalCmd = raw.CreateCommand())
        {
            journalCmd.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", journalCmd.ExecuteScalar()?.ToString(), ignoreCase: true);
        }

        using SqliteCommand idxCmd = raw.CreateCommand();
        idxCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'HOSTS';";
        var indexes = new List<string>();
        using SqliteDataReader reader = idxCmd.ExecuteReader();
        while (reader.Read())
        {
            indexes.Add(reader.GetString(0));
        }

        Assert.Contains("IDX_HOSTS_HOST", indexes);
    }

    [Fact]
    public async Task EhHosts_LookupAsync_DohCacheAndCoalescing_DeduplicatesConcurrentAndRepeatedQueries()
    {
        using var temp = new TempDirectory();
        using var hostsDb = new HostsDatabase(temp.File("hosts.db"));
        var settings = new EhClientSettings
        {
            BuiltInHosts = false,
            BuiltInExHosts = false,
            UseDnsOverHttps = true,
            DohEndpoint = "https://doh1.example/dns-query",
            ProxyType = EhProxyType.Direct
        };

        long nowSeconds = 1_700_000_000L;
        byte[] aRecord = [93, 184, 216, 34];
        byte[] aaaaRecord = IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946").GetAddressBytes();

        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int postCount = 0;

        var handler = new GatedDohHandler(async (qtype, _) =>
        {
            Interlocked.Increment(ref postCount);
            firstEntered.TrySetResult();
            await releaseGate.Task.ConfigureAwait(false);
            return qtype switch
            {
                1 => DnsWire.BuildResponse("cache.example", 1, aRecord),
                28 => DnsWire.BuildResponse("cache.example", 28, aaaaRecord),
                _ => null
            };
        });

        using var hosts = new EhHosts(
            hostsDb,
            settings,
            handler,
            systemDnsResolver: FailingResolver(),
            utcNowSeconds: () => Volatile.Read(ref nowSeconds));

        Task<IReadOnlyList<IPAddress>> t1 = hosts.LookupAsync("cache.example");
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<IReadOnlyList<IPAddress>> t2 = hosts.LookupAsync("cache.example");
        Task<IReadOnlyList<IPAddress>> t3 = hosts.LookupAsync("cache.example");

        releaseGate.TrySetResult();
        IReadOnlyList<IPAddress>[] concurrentResults = await Task.WhenAll(t1, t2, t3).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(concurrentResults, static r => Assert.Equal(2, r.Count));
        Assert.Equal(2, Volatile.Read(ref postCount)); // 1 A + 1 AAAA shared across all 3 callers

        // Repeated sequential lookup inside TTL window (60s from DnsWire.BuildResponse) hits cache.
        IReadOnlyList<IPAddress> cached = await hosts.LookupAsync("cache.example");
        Assert.Equal(2, cached.Count);
        Assert.Equal(2, Volatile.Read(ref postCount));

        // Advancing past 60s TTL refreshes from DoH.
        Volatile.Write(ref nowSeconds, nowSeconds + 61);
        IReadOnlyList<IPAddress> refreshed = await hosts.LookupAsync("cache.example");
        Assert.Equal(2, refreshed.Count);
        Assert.Equal(4, Volatile.Read(ref postCount));

        // Changing DohEndpoint invalidates cache key without touching old entry.
        settings.DohEndpoint = "https://doh2.example/dns-query";
        IReadOnlyList<IPAddress> endpointChanged = await hosts.LookupAsync("cache.example");
        Assert.Equal(2, endpointChanged.Count);
        Assert.Equal(6, Volatile.Read(ref postCount));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Func<string, CancellationToken, Task<IPAddress[]>> FailingResolver(
        string message = "system DNS must not be used")
    {
        return (host, _) => throw new InvalidOperationException($"{message}: {host}");
    }

    private static IEnumerable<Exception> Flatten(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static string FormatExpires(long unixMillis) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMillis).UtcDateTime
            .ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", System.Globalization.CultureInfo.InvariantCulture);

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void CreateCookieSchema(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS OK_HTTP_3_COOKIE (
                _id INTEGER PRIMARY KEY AUTOINCREMENT,
                NAME TEXT,
                VALUE TEXT,
                EXPIRES_AT INTEGER,
                DOMAIN TEXT,
                PATH TEXT,
                SECURE INTEGER,
                HTTP_ONLY INTEGER,
                PERSISTENT INTEGER,
                HOST_ONLY INTEGER
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static void ExecuteSql(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string[] ReadRawCookieValues(string dbPath)
    {
        var values = new List<string>();
        using SqliteConnection connection = OpenReadOnly(dbPath);
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT VALUE FROM OK_HTTP_3_COOKIE ORDER BY _id;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private static int CountCookieRows(string dbPath, string? name = null)
    {
        using SqliteConnection connection = OpenReadOnly(dbPath);
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = name is null
            ? "SELECT COUNT(*) FROM OK_HTTP_3_COOKIE;"
            : "SELECT COUNT(*) FROM OK_HTTP_3_COOKIE WHERE NAME = $name;";
        if (name is not null)
        {
            cmd.Parameters.AddWithValue("$name", name);
        }

        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Temporary scratch directory removed on dispose (best effort).</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ehviewer-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best effort only: SQLite handles may still be closing on Windows.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>CertificateRequest-built self-signed certificate (no external tooling).</summary>
    private static X509Certificate2 CreateSelfSignedLoopbackCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            critical: false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        // Round-trip through PKCS#12 so SslStream gets a usable private key on every platform.
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx),
            password: null,
            keyStorageFlags: X509KeyStorageFlags.Exportable);
    }

    /// <summary>Minimal SslStream HTTPS endpoint that must never be trusted.</summary>
    private sealed class LoopbackTlsServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _cts = new();
        private int _handshakeAttempts;

        public LoopbackTlsServer()
        {
            _certificate = CreateSelfSignedLoopbackCertificate();
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public int HandshakeAttempts => Volatile.Read(ref _handshakeAttempts);

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    Interlocked.Increment(ref _handshakeAttempts);
                    await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                    }).ConfigureAwait(false);

                    byte[] response = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    await ssl.WriteAsync(response).ConfigureAwait(false);
                    await ssl.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                    // The client is expected to abort the handshake; nothing to do.
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
            }
            catch (SocketException)
            {
            }

            _certificate.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>Minimal HTTP/1.1 loopback server used for redirect / cookie-jar tests.</summary>
    private sealed class LoopbackHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<string, LoopbackResponse> _router;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();

        public LoopbackHttpServer(Func<string, LoopbackResponse> router)
        {
            _router = router;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    string? head = await ReadRequestHeadAsync(stream).ConfigureAwait(false);
                    if (head is null)
                    {
                        return;
                    }

                    RecordedRequest request = RecordedRequest.Parse(head);
                    _requests.Enqueue(request);

                    LoopbackResponse payload = _router(request.Path);
                    var builder = new StringBuilder();
                    builder.Append("HTTP/1.1 ").Append(payload.Status).Append(' ').Append(payload.Reason).Append("\r\n");
                    foreach ((string name, string value) in payload.Headers)
                    {
                        builder.Append(name).Append(": ").Append(value).Append("\r\n");
                    }

                    builder.Append("Content-Length: ").Append(payload.Body.Length).Append("\r\n");
                    builder.Append("Connection: close\r\n\r\n");

                    byte[] headBytes = Encoding.ASCII.GetBytes(builder.ToString());
                    await stream.WriteAsync(headBytes).ConfigureAwait(false);
                    if (payload.Body.Length > 0)
                    {
                        await stream.WriteAsync(payload.Body).ConfigureAwait(false);
                    }

                    await stream.FlushAsync().ConfigureAwait(false);
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                catch
                {
                    // Client aborted (timeouts / cancellations) — expected during negative tests.
                }
            }
        }

        private static async Task<string?> ReadRequestHeadAsync(Stream stream)
        {
            var buffer = new List<byte>(1024);
            var single = new byte[1];
            while (buffer.Count < 64 * 1024)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(single).ConfigureAwait(false);
                }
                catch
                {
                    return null;
                }

                if (read == 0)
                {
                    return buffer.Count == 0 ? null : Encoding.ASCII.GetString([.. buffer]);
                }

                buffer.Add(single[0]);
                int count = buffer.Count;
                if (count >= 4
                    && buffer[count - 4] == (byte)'\r' && buffer[count - 3] == (byte)'\n'
                    && buffer[count - 2] == (byte)'\r' && buffer[count - 1] == (byte)'\n')
                {
                    return Encoding.ASCII.GetString([.. buffer]);
                }
            }

            return null;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
            }
            catch (SocketException)
            {
            }

            _cts.Dispose();
        }
    }

    private sealed record LoopbackResponse(
        int Status,
        string Reason,
        (string Name, string Value)[] Headers,
        byte[] Body)
    {
        public static LoopbackResponse Ok(string body) =>
            new(200, "OK", [], Encoding.UTF8.GetBytes(body));

        public static LoopbackResponse Found(string location, string setCookie) =>
            new(302, "Found", [("Location", location), ("Set-Cookie", setCookie)], []);

        public static LoopbackResponse NotFound() =>
            new(404, "Not Found", [], []);
    }

    private sealed record RecordedRequest(string Method, string Path, Dictionary<string, string> Headers)
    {
        public static RecordedRequest Parse(string head)
        {
            string[] lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            string[] requestLine = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }

            string path = requestLine.Length > 1 ? requestLine[1] : string.Empty;
            int query = path.IndexOf('?');
            if (query >= 0)
            {
                path = path[..query];
            }

            return new RecordedRequest(requestLine[0], path, headers);
        }
    }

    /// <summary>Hand-written <see cref="HttpMessageHandler"/> that answers RFC 8484 DoH POSTs.</summary>
    private sealed class DohHandler(Func<ushort, byte[]?> responseFactory) : HttpMessageHandler
    {
        public ConcurrentQueue<(string Method, ushort QType, string? ContentType, string Accept)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            byte[] body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ushort qtype = body.Length >= 4
                ? BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(body.Length - 4, 2))
                : (ushort)0;

            Requests.Enqueue((
                request.Method.Method,
                qtype,
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.Accept.ToString()));

            byte[]? payload = responseFactory(qtype);
            if (payload is null)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            return response;
        }
    }

    private sealed class GatedDohHandler(Func<ushort, CancellationToken, Task<byte[]?>> responseFactory) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            byte[] body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            ushort qtype = body.Length >= 4
                ? BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(body.Length - 4, 2))
                : (ushort)0;

            byte[]? payload = await responseFactory(qtype, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            return response;
        }
    }

    /// <summary>Builds RFC 1035 wire responses for the DoH tests.</summary>
    private static class DnsWire
    {
        public static byte[] BuildResponse(string hostname, ushort qtype, byte[] rdata, int rcode = 0)
        {
            byte[] question = BuildQuestion(hostname, qtype);
            bool hasAnswer = rdata.Length > 0 && rcode == 0;
            int answerLength = hasAnswer ? 2 + 10 + rdata.Length : 0;

            var packet = new byte[12 + question.Length + answerLength];
            packet[2] = 0x81; // QR = 1, Opcode = 0, RD = 1
            packet[3] = (byte)(0x80 | (rcode & 0x0F)); // RA = 1, RCODE
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 1); // QDCOUNT
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), hasAnswer ? (ushort)1 : (ushort)0);

            int offset = 12;
            question.CopyTo(packet.AsSpan(offset));
            offset += question.Length;

            if (hasAnswer)
            {
                packet[offset++] = 0xC0; // compression pointer to the question name
                packet[offset++] = 0x0C;
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), qtype);
                offset += 2;
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), 1); // class IN
                offset += 2;
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(offset, 4), 60); // TTL
                offset += 4;
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), (ushort)rdata.Length);
                offset += 2;
                rdata.CopyTo(packet.AsSpan(offset));
            }

            return packet;
        }

        private static byte[] BuildQuestion(string hostname, ushort qtype)
        {
            string[] labels = hostname.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var buffer = new List<byte>(hostname.Length + 6);
            foreach (string label in labels)
            {
                buffer.Add((byte)label.Length);
                buffer.AddRange(Encoding.ASCII.GetBytes(label));
            }

            buffer.Add(0);
            buffer.Add((byte)(qtype >> 8));
            buffer.Add((byte)(qtype & 0xFF));
            buffer.Add(0);
            buffer.Add(1); // QCLASS = IN
            return [.. buffer];
        }
    }
}

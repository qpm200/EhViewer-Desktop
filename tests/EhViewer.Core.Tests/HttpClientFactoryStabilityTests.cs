using System.Diagnostics;
using System.Net;
using EhViewer.Core.Network;
using Xunit;

namespace EhViewer.Core.Tests.NetworkTests;

public sealed class HttpClientFactoryStabilityTests : TempDirectoryTestBase
{
    [Fact]
    public void ProxyHandlerLease_TryAcquireAndRelease_EnforcesRefCountLifecycle()
    {
        int zeroRefCallbacks = 0;
        var lease = new EhHttpClientFactory.ProxyHandlerLease(_ => Interlocked.Increment(ref zeroRefCallbacks));

        Assert.Equal(1, lease.ActiveRefCount);
        Assert.False(lease.IsDisposed);

        Assert.True(lease.TryAcquire());
        Assert.Equal(2, lease.ActiveRefCount);

        lease.Release();
        Assert.Equal(1, lease.ActiveRefCount);
        Assert.Equal(0, zeroRefCallbacks);

        lease.Release();
        Assert.Equal(0, lease.ActiveRefCount);
        Assert.Equal(1, zeroRefCallbacks);

        // Once ref count hits zero, further acquisitions must fail.
        Assert.False(lease.TryAcquire());

        lease.DisposeHandlersNow();
        Assert.True(lease.IsDisposed);
        lease.DisposeHandlersNow(); // Idempotent.
        Assert.True(lease.IsDisposed);
    }

    [Fact]
    public async Task InvalidateProxyConfiguration_DefersDisposalWhileInFlightAndDisposesAfterGracePeriod()
    {
        string tempDir = CreateTempDirectory("ehviewer-http");
        using var hostsDb = new HostsDatabase(Path.Combine(tempDir, "hosts.db"));
        using var cookieStore = new SqliteCookieStore(Path.Combine(tempDir, "cookie.db"));
        var settings = new EhClientSettings
        {
            ProxyType = EhProxyType.Http,
            ProxyIp = "127.0.0.1",
            ProxyPort = 8888,
            DomainFronting = false,
            BuiltInHosts = false,
            BuiltInExHosts = false,
            UseDnsOverHttps = false
        };

        using var hosts = new EhHosts(hostsDb, settings);
        using var factory = new EhHttpClientFactory(cookieStore, hosts, settings)
        {
            RetiredHandlerGracePeriod = TimeSpan.FromMilliseconds(40)
        };

        (_, EhHttpClientFactory.ProxyHandlerLease? lease) =
            factory.AcquireInnerInvokerForTesting(new Uri("https://e-hentai.org/"));

        Assert.NotNull(lease);
        Assert.Equal(2, lease.ActiveRefCount);
        Assert.False(lease.IsDisposed);

        // Switching/invalidating proxy while a request is in flight must not dispose the old lease.
        factory.InvalidateProxyConfiguration();
        Assert.Equal(1, lease.ActiveRefCount);
        Assert.False(lease.IsDisposed);
        Assert.Equal(1, factory.PendingRetiredLeaseCount);

        // Even after waiting longer than the grace period, an active in-flight ref keeps it alive.
        await Task.Delay(90);
        Assert.False(lease.IsDisposed);

        // Once the in-flight request completes and releases its ref, the grace timer disposes it.
        lease.Release();
        Assert.Equal(0, lease.ActiveRefCount);

        bool disposedInTime = await WaitUntilAsync(
            () => lease.IsDisposed && factory.PendingRetiredLeaseCount == 0,
            TimeSpan.FromSeconds(3));
        Assert.True(disposedInTime, "Retired proxy lease was not disposed after grace period.");
    }

    [Fact]
    public void Dispose_ImmediatelyCancelsTimersAndDisposesRetiredAndCurrentLeasesIdempotently()
    {
        string tempDir = CreateTempDirectory("ehviewer-http");
        using var hostsDb = new HostsDatabase(Path.Combine(tempDir, "hosts.db"));
        using var cookieStore = new SqliteCookieStore(Path.Combine(tempDir, "cookie.db"));
        var settings = new EhClientSettings
        {
            ProxyType = EhProxyType.Http,
            ProxyIp = "127.0.0.1",
            ProxyPort = 8888,
            DomainFronting = false,
            BuiltInHosts = false,
            BuiltInExHosts = false,
            UseDnsOverHttps = false
        };

        using var hosts = new EhHosts(hostsDb, settings);
        var factory = new EhHttpClientFactory(cookieStore, hosts, settings)
        {
            RetiredHandlerGracePeriod = TimeSpan.FromSeconds(30)
        };

        (_, EhHttpClientFactory.ProxyHandlerLease? retiredLease) =
            factory.AcquireInnerInvokerForTesting(new Uri("https://e-hentai.org/"));
        Assert.NotNull(retiredLease);

        factory.InvalidateProxyConfiguration();
        retiredLease.Release();
        Assert.False(retiredLease.IsDisposed);
        Assert.Equal(1, factory.PendingRetiredLeaseCount);

        (_, EhHttpClientFactory.ProxyHandlerLease? currentLease) =
            factory.AcquireInnerInvokerForTesting(new Uri("https://e-hentai.org/"));
        Assert.NotNull(currentLease);
        Assert.NotSame(retiredLease, currentLease);

        factory.Dispose();

        Assert.True(retiredLease.IsDisposed);
        Assert.True(currentLease.IsDisposed);
        Assert.Equal(0, factory.PendingRetiredLeaseCount);

        // Calling Dispose a second time and releasing late refs must be safe and idempotent.
        currentLease.Release();
        factory.Dispose();
    }

    [Fact]
    public async Task ConnectCandidatesWithTimeoutAsync_TimesOutSlowFirstIpAndSucceedsOnSecond()
    {
        IPAddress slowIp = IPAddress.Parse("10.255.255.1");
        IPAddress fastIp = IPAddress.Parse("127.0.0.1");
        var attempted = new List<IPAddress>();
        var sw = Stopwatch.StartNew();

        string result = await EhHttpClientFactory.ConnectCandidatesWithTimeoutAsync(
            [slowIp, fastIp],
            async (ip, attemptToken) =>
            {
                attempted.Add(ip);
                if (ip.Equals(slowIp))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, attemptToken);
                }

                return "connected-second";
            },
            perAttemptTimeout: TimeSpan.FromMilliseconds(60),
            cancellationToken: CancellationToken.None);

        sw.Stop();
        Assert.Equal("connected-second", result);
        Assert.Equal([slowIp, fastIp], attempted);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Fallback took too long: {sw.Elapsed}");
    }

    [Fact]
    public async Task ConnectCandidatesWithTimeoutAsync_PropagatesExternalCancellationWithoutTryingNextIp()
    {
        IPAddress firstIp = IPAddress.Parse("10.255.255.1");
        IPAddress secondIp = IPAddress.Parse("127.0.0.1");
        var attempted = new List<IPAddress>();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EhHttpClientFactory.ConnectCandidatesWithTimeoutAsync(
                [firstIp, secondIp],
                async (ip, attemptToken) =>
                {
                    attempted.Add(ip);
                    await Task.Delay(Timeout.InfiniteTimeSpan, attemptToken);
                    return "unreachable";
                },
                perAttemptTimeout: TimeSpan.FromSeconds(5),
                cancellationToken: cts.Token));

        Assert.Single(attempted);
        Assert.Equal(firstIp, attempted[0]);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(15);
        }

        return condition();
    }
}

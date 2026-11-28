using System.Net;
using System.Net.Sockets;

namespace EhViewer.Core.Network;

/// <summary>
/// Creates and manages the shared standard and image/non-redirecting <see cref="HttpClient"/> instances
/// with strict TLS verification, <see cref="ICookieStore"/> persistence, proxy routing, and <see cref="EhHosts"/> domain fronting.
/// </summary>
public sealed class EhHttpClientFactory : IDisposable
{
    private static readonly TimeSpan DefaultRetiredHandlerGracePeriod = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultPerAttemptConnectTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DefaultLastAttemptConnectTimeout = TimeSpan.FromSeconds(8);

    private readonly ICookieStore _cookieStore;
    private readonly EhHosts _hosts;
    private readonly IEhClientSettings _settings;
    private readonly object _gate = new();
    private readonly HashSet<ProxyHandlerLease> _retiredLeases = [];
    private readonly Dictionary<ProxyHandlerLease, Timer> _retirementTimers = [];

    private SocketsHttpHandler? _directStandardHandler;
    private SocketsHttpHandler? _directImageHandler;
    private HttpMessageInvoker? _directStandardInvoker;
    private HttpMessageInvoker? _directImageInvoker;
    private ProxyHandlerLease? _currentProxyLease;
    private EhProxyType _configuredProxyType;
    private string? _configuredProxyIp;
    private int _configuredProxyPort;
    private bool _disposed;

    private HttpClient? _standardClient;
    private HttpClient? _imageClient;

    public EhHttpClientFactory(
        ICookieStore cookieStore,
        EhHosts hosts,
        IEhClientSettings settings)
    {
        _cookieStore = cookieStore ?? throw new ArgumentNullException(nameof(cookieStore));
        _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    internal TimeSpan RetiredHandlerGracePeriod { get; set; } = DefaultRetiredHandlerGracePeriod;

    internal int PendingRetiredLeaseCount
    {
        get
        {
            lock (_gate)
            {
                return _retiredLeases.Count;
            }
        }
    }

    public HttpClient StandardClient
    {
        get
        {
            ProxyHandlerLease? retiredLease;
            HttpClient client;
            lock (_gate)
            {
                retiredLease = EnsureHandlersCurrentLocked();
                client = _standardClient ??= CreateClient(isImageClient: false, timeout: TimeSpan.FromSeconds(30));
            }

            if (retiredLease is not null)
            {
                RetireLeaseOutsideLock(retiredLease);
            }

            return client;
        }
    }

    public HttpClient ImageClient
    {
        get
        {
            ProxyHandlerLease? retiredLease;
            HttpClient client;
            lock (_gate)
            {
                retiredLease = EnsureHandlersCurrentLocked();
                client = _imageClient ??= CreateClient(isImageClient: true, timeout: TimeSpan.FromSeconds(30));
            }

            if (retiredLease is not null)
            {
                RetireLeaseOutsideLock(retiredLease);
            }

            return client;
        }
    }

    public void InvalidateProxyConfiguration()
    {
        ProxyHandlerLease? retiredLease;
        lock (_gate)
        {
            retiredLease = DetachCurrentProxyLeaseLocked();
        }

        if (retiredLease is not null)
        {
            RetireLeaseOutsideLock(retiredLease);
        }
    }

    internal HttpMessageHandler CreateTransportHandler(bool isImageClient)
    {
        return new EhCookieAndRedirectHandler(
            _cookieStore,
            selectInvoker: uri => SelectInnerInvoker(uri, isImageClient),
            followRedirects: !isImageClient);
    }

    internal (HttpMessageInvoker Invoker, ProxyHandlerLease? Lease) AcquireInnerInvokerForTesting(
        Uri requestUri,
        bool isImageClient = false)
    {
        return SelectInnerInvoker(requestUri, isImageClient);
    }

    private HttpClient CreateClient(bool isImageClient, TimeSpan timeout)
    {
        return new HttpClient(CreateTransportHandler(isImageClient), disposeHandler: true)
        {
            Timeout = timeout
        };
    }

    private (HttpMessageInvoker Invoker, ProxyHandlerLease? Lease) SelectInnerInvoker(Uri requestUri, bool isImageClient)
    {
        bool useProxy = ShouldUseProxy(requestUri);
        ProxyHandlerLease? retiredLease;
        (HttpMessageInvoker Invoker, ProxyHandlerLease? Lease) selected;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            retiredLease = EnsureHandlersCurrentLocked();

            if (!useProxy)
            {
                HttpMessageInvoker directInvoker;
                if (isImageClient)
                {
                    _directImageHandler ??= CreateDirectSocketsHandler(connectTimeout: TimeSpan.FromSeconds(20));
                    directInvoker = _directImageInvoker ??= new HttpMessageInvoker(_directImageHandler, disposeHandler: false);
                }
                else
                {
                    _directStandardHandler ??= CreateDirectSocketsHandler(connectTimeout: TimeSpan.FromSeconds(10));
                    directInvoker = _directStandardInvoker ??= new HttpMessageInvoker(_directStandardHandler, disposeHandler: false);
                }

                selected = (directInvoker, null);
            }
            else
            {
                ProxyHandlerLease lease = _currentProxyLease ??= CreateProxyLeaseLocked();
                if (!lease.TryAcquire())
                {
                    lease = CreateProxyLeaseLocked();
                    _currentProxyLease = lease;
                    lease.TryAcquire();
                }

                HttpMessageInvoker proxyInvoker = lease.GetOrCreateInvoker(isImageClient, CreateProxySocketsHandler);
                selected = (proxyInvoker, lease);
            }
        }

        if (retiredLease is not null)
        {
            RetireLeaseOutsideLock(retiredLease);
        }

        return selected;
    }

    private ProxyHandlerLease? EnsureHandlersCurrentLocked()
    {
        if (_configuredProxyType != _settings.ProxyType
            || !string.Equals(_configuredProxyIp, _settings.ProxyIp, StringComparison.Ordinal)
            || _configuredProxyPort != _settings.ProxyPort)
        {
            ProxyHandlerLease? retiredLease = DetachCurrentProxyLeaseLocked();
            _configuredProxyType = _settings.ProxyType;
            _configuredProxyIp = _settings.ProxyIp;
            _configuredProxyPort = _settings.ProxyPort;
            return retiredLease;
        }

        return null;
    }

    private ProxyHandlerLease CreateProxyLeaseLocked()
    {
        return new ProxyHandlerLease(OnLeaseReachedZeroRefs);
    }

    private ProxyHandlerLease? DetachCurrentProxyLeaseLocked()
    {
        ProxyHandlerLease? oldLease = _currentProxyLease;
        if (oldLease is null)
        {
            return null;
        }

        _currentProxyLease = null;
        _retiredLeases.Add(oldLease);
        return oldLease;
    }

    private static void RetireLeaseOutsideLock(ProxyHandlerLease oldLease)
    {
        oldLease.Release();
    }

    private void OnLeaseReachedZeroRefs(ProxyHandlerLease lease)
    {
        TimeSpan gracePeriod = RetiredHandlerGracePeriod;
        if (gracePeriod <= TimeSpan.Zero)
        {
            FinalizeRetiredLease(lease);
            return;
        }

        bool disposeImmediately = false;
        lock (_gate)
        {
            if (_disposed)
            {
                _retiredLeases.Remove(lease);
                disposeImmediately = true;
            }
            else if (!_retirementTimers.ContainsKey(lease))
            {
                _retiredLeases.Add(lease);
                var timer = new Timer(
                    static state =>
                    {
                        var (factory, targetLease) = ((EhHttpClientFactory, ProxyHandlerLease))state!;
                        factory.FinalizeRetiredLease(targetLease);
                    },
                    (this, lease),
                    dueTime: gracePeriod,
                    period: Timeout.InfiniteTimeSpan);
                _retirementTimers[lease] = timer;
            }
        }

        if (disposeImmediately)
        {
            lease.DisposeHandlersNow();
        }
    }

    private void FinalizeRetiredLease(ProxyHandlerLease lease)
    {
        Timer? timer = null;
        lock (_gate)
        {
            _retiredLeases.Remove(lease);
            if (_retirementTimers.Remove(lease, out Timer? existingTimer))
            {
                timer = existingTimer;
            }
        }

        timer?.Dispose();
        lease.DisposeHandlersNow();
    }

    private bool ShouldUseProxy(Uri requestUri)
    {
        return _settings.ProxyType switch
        {
            EhProxyType.Direct => false,
            EhProxyType.Http or EhProxyType.Socks =>
                !string.IsNullOrWhiteSpace(_settings.ProxyIp) && InetValidator.IsValidInetPort(_settings.ProxyPort),
            EhProxyType.System => HasActiveSystemProxy(requestUri),
            _ => false
        };
    }

    private static bool HasActiveSystemProxy(Uri requestUri)
    {
        IWebProxy systemProxy = HttpClient.DefaultProxy;
        if (systemProxy.IsBypassed(requestUri))
        {
            return false;
        }

        Uri? resolved = systemProxy.GetProxy(requestUri);
        return resolved is not null && resolved != requestUri;
    }

    private SocketsHttpHandler CreateDirectSocketsHandler(TimeSpan connectTimeout)
    {
        return new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = connectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            MaxConnectionsPerServer = 10,
            // Enforce strict TLS certificate validation without trusting invalid or mismatched certs.
            SslOptions =
            {
                RemoteCertificateValidationCallback = null
            },
            ConnectCallback = ConnectWithEhHostsAsync
        };
    }

    private SocketsHttpHandler CreateProxySocketsHandler(TimeSpan connectTimeout)
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = connectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            MaxConnectionsPerServer = 10,
            SslOptions =
            {
                RemoteCertificateValidationCallback = null
            }
        };

        if (_settings.ProxyType == EhProxyType.Http && !string.IsNullOrWhiteSpace(_settings.ProxyIp) && InetValidator.IsValidInetPort(_settings.ProxyPort))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy($"http://{_settings.ProxyIp}:{_settings.ProxyPort}");
        }
        else if (_settings.ProxyType == EhProxyType.Socks && !string.IsNullOrWhiteSpace(_settings.ProxyIp) && InetValidator.IsValidInetPort(_settings.ProxyPort))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy($"socks5://{_settings.ProxyIp}:{_settings.ProxyPort}");
        }
        else
        {
            handler.UseProxy = true;
            handler.Proxy = null; // Defers to HttpClient.DefaultProxy (Windows system proxy).
        }

        return handler;
    }

    internal static async Task<T> ConnectCandidatesWithTimeoutAsync<T>(
        IReadOnlyList<IPAddress> addresses,
        Func<IPAddress, CancellationToken, Task<T>> connectAttempt,
        TimeSpan perAttemptTimeout,
        CancellationToken cancellationToken,
        TimeSpan? lastAttemptTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(connectAttempt);

        Exception? lastException = null;
        int count = addresses.Count;

        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IPAddress ip = addresses[i];
            bool isLast = i == count - 1;
            TimeSpan attemptTimeout = isLast && lastAttemptTimeout.HasValue
                ? lastAttemptTimeout.Value
                : perAttemptTimeout;

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (attemptTimeout > TimeSpan.Zero && attemptTimeout != Timeout.InfiniteTimeSpan)
            {
                attemptCts.CancelAfter(attemptTimeout);
            }

            try
            {
                return await connectAttempt(ip, attemptCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
            }
        }

        throw lastException ?? new SocketException((int)SocketError.HostNotFound);
    }

    private async ValueTask<Stream> ConnectWithEhHostsAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host;
        int port = context.DnsEndPoint.Port;

        IReadOnlyList<IPAddress> addresses = await _hosts.LookupAsync(host, cancellationToken).ConfigureAwait(false);
        return await ConnectCandidatesWithTimeoutAsync(
            addresses,
            async (ip, attemptToken) =>
            {
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(ip, port), attemptToken).ConfigureAwait(false);
                    return (Stream)new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            DefaultPerAttemptConnectTimeout,
            cancellationToken,
            DefaultLastAttemptConnectTimeout).ConfigureAwait(false);
    }

    public void Dispose()
    {
        List<Timer> timersToDispose;
        List<ProxyHandlerLease> leasesToDispose;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            timersToDispose = [.. _retirementTimers.Values];
            _retirementTimers.Clear();

            leasesToDispose = [.. _retiredLeases];
            _retiredLeases.Clear();

            if (_currentProxyLease is not null)
            {
                leasesToDispose.Add(_currentProxyLease);
                _currentProxyLease = null;
            }

            _standardClient?.Dispose();
            _imageClient?.Dispose();
            _directStandardInvoker?.Dispose();
            _directImageInvoker?.Dispose();
            _directStandardHandler?.Dispose();
            _directImageHandler?.Dispose();

            _standardClient = null;
            _imageClient = null;
            _directStandardInvoker = null;
            _directImageInvoker = null;
            _directStandardHandler = null;
            _directImageHandler = null;
        }

        foreach (Timer timer in timersToDispose)
        {
            timer.Dispose();
        }

        foreach (ProxyHandlerLease lease in leasesToDispose)
        {
            lease.DisposeHandlersNow();
        }
    }

    internal sealed class ProxyHandlerLease
    {
        private readonly object _leaseGate = new();
        private readonly Action<ProxyHandlerLease>? _onZeroRefs;
        private SocketsHttpHandler? _proxyStandardHandler;
        private HttpMessageInvoker? _proxyStandardInvoker;
        private SocketsHttpHandler? _proxyImageHandler;
        private HttpMessageInvoker? _proxyImageInvoker;
        private int _refCount = 1;
        private int _disposed;

        public ProxyHandlerLease(Action<ProxyHandlerLease>? onZeroRefs = null)
        {
            _onZeroRefs = onZeroRefs;
        }

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        internal int ActiveRefCount => Volatile.Read(ref _refCount);

        internal bool TryAcquire()
        {
            while (true)
            {
                int current = Volatile.Read(ref _refCount);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        internal void Release()
        {
            int remaining = Interlocked.Decrement(ref _refCount);
            if (remaining == 0)
            {
                _onZeroRefs?.Invoke(this);
            }
            else if (remaining < 0)
            {
                Interlocked.CompareExchange(ref _refCount, 0, remaining);
            }
        }

        internal HttpMessageInvoker GetOrCreateInvoker(
            bool isImageClient,
            Func<TimeSpan, SocketsHttpHandler> createProxyHandler)
        {
            lock (_leaseGate)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);

                if (isImageClient)
                {
                    _proxyImageHandler ??= createProxyHandler(TimeSpan.FromSeconds(20));
                    return _proxyImageInvoker ??= new HttpMessageInvoker(_proxyImageHandler, disposeHandler: false);
                }

                _proxyStandardHandler ??= createProxyHandler(TimeSpan.FromSeconds(10));
                return _proxyStandardInvoker ??= new HttpMessageInvoker(_proxyStandardHandler, disposeHandler: false);
            }
        }

        internal void DisposeHandlersNow()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            HttpMessageInvoker? standardInvoker;
            HttpMessageInvoker? imageInvoker;
            SocketsHttpHandler? standardHandler;
            SocketsHttpHandler? imageHandler;

            lock (_leaseGate)
            {
                standardInvoker = _proxyStandardInvoker;
                imageInvoker = _proxyImageInvoker;
                standardHandler = _proxyStandardHandler;
                imageHandler = _proxyImageHandler;

                _proxyStandardInvoker = null;
                _proxyImageInvoker = null;
                _proxyStandardHandler = null;
                _proxyImageHandler = null;
            }

            standardInvoker?.Dispose();
            imageInvoker?.Dispose();
            standardHandler?.Dispose();
            imageHandler?.Dispose();
        }
    }

    /// <summary>
    /// Handles <see cref="ICookieStore"/> persistence across HTTP redirects so cookies set on intermediate
    /// 3xx hops (such as sign-in / ExHentai redirects) are never lost.
    /// </summary>
    internal sealed class EhCookieAndRedirectHandler(
        ICookieStore cookieStore,
        Func<Uri, (HttpMessageInvoker Invoker, ProxyHandlerLease? Lease)> selectInvoker,
        bool followRedirects) : HttpMessageHandler
    {
        private const int MaxRedirects = 20;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request.RequestUri);

            HttpRequestMessage current = request;
            int redirectCount = 0;

            while (true)
            {
                AttachCookies(current);
                (HttpMessageInvoker invoker, ProxyHandlerLease? lease) = selectInvoker(current.RequestUri!);
                HttpResponseMessage response;
                try
                {
                    response = await invoker.SendAsync(current, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    lease?.Release();
                }

                SaveResponseCookies(current.RequestUri!, response);

                if (!followRedirects || !IsRedirectStatus((int)response.StatusCode) || response.Headers.Location is null)
                {
                    if (!ReferenceEquals(current, request))
                    {
                        current.Dispose();
                    }

                    return response;
                }

                if (++redirectCount > MaxRedirects)
                {
                    if (!ReferenceEquals(current, request))
                    {
                        current.Dispose();
                    }

                    throw new HttpRequestException($"Exceeded maximum automatic redirections ({MaxRedirects}).");
                }

                Uri nextUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current.RequestUri!, response.Headers.Location);

                HttpMethod nextMethod = ShouldSwitchToGet((int)response.StatusCode, current.Method)
                    ? HttpMethod.Get
                    : current.Method;

                HttpRequestMessage nextRequest = await CloneForRedirectAsync(current, nextMethod, nextUri, cancellationToken)
                    .ConfigureAwait(false);

                if (!ReferenceEquals(current, request))
                {
                    current.Dispose();
                }

                response.Dispose();
                current = nextRequest;
            }
        }

        private void AttachCookies(HttpRequestMessage request)
        {
            request.Headers.Remove("Cookie");
            string cookieHeader = cookieStore.GetCookieHeader(request.RequestUri!);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }
        }

        private void SaveResponseCookies(Uri requestUri, HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
            {
                cookieStore.SaveFromResponse(requestUri, values);
            }
        }

        private static bool IsRedirectStatus(int statusCode)
        {
            return statusCode is 301 or 302 or 303 or 307 or 308;
        }

        private static bool ShouldSwitchToGet(int statusCode, HttpMethod currentMethod)
        {
            if (statusCode == 303)
            {
                return currentMethod != HttpMethod.Get && currentMethod != HttpMethod.Head;
            }

            if (statusCode is 301 or 302)
            {
                return currentMethod == HttpMethod.Post;
            }

            return false;
        }

        private static async Task<HttpRequestMessage> CloneForRedirectAsync(
            HttpRequestMessage previous,
            HttpMethod nextMethod,
            Uri nextUri,
            CancellationToken cancellationToken)
        {
            var next = new HttpRequestMessage(nextMethod, nextUri);
            foreach ((string key, IEnumerable<string> values) in previous.Headers)
            {
                if (string.Equals(key, "Cookie", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(previous.RequestUri?.Host, nextUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                next.Headers.TryAddWithoutValidation(key, values);
            }

            next.Headers.TryAddWithoutValidation("Host", nextUri.Authority);

            if (nextMethod == previous.Method && previous.Content is not null)
            {
                byte[] bodyBytes = await previous.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var clonedContent = new ByteArrayContent(bodyBytes);
                foreach ((string key, IEnumerable<string> values) in previous.Content.Headers)
                {
                    clonedContent.Headers.TryAddWithoutValidation(key, values);
                }

                next.Content = clonedContent;
            }

            return next;
        }
    }
}

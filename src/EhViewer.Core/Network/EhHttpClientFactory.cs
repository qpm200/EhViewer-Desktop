using System.Net;
using System.Net.Sockets;

namespace EhViewer.Core.Network;

/// <summary>
/// Creates and manages the shared standard and image/non-redirecting <see cref="HttpClient"/> instances
/// with strict TLS verification, <see cref="ICookieStore"/> persistence, proxy routing, and <see cref="EhHosts"/> domain fronting.
/// </summary>
public sealed class EhHttpClientFactory : IDisposable
{
    private readonly ICookieStore _cookieStore;
    private readonly EhHosts _hosts;
    private readonly IEhClientSettings _settings;
    private readonly object _gate = new();

    private SocketsHttpHandler? _directStandardHandler;
    private SocketsHttpHandler? _proxyStandardHandler;
    private SocketsHttpHandler? _directImageHandler;
    private SocketsHttpHandler? _proxyImageHandler;
    private HttpMessageInvoker? _directStandardInvoker;
    private HttpMessageInvoker? _proxyStandardInvoker;
    private HttpMessageInvoker? _directImageInvoker;
    private HttpMessageInvoker? _proxyImageInvoker;
    private EhProxyType _configuredProxyType;
    private string? _configuredProxyIp;
    private int _configuredProxyPort;

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

    public HttpClient StandardClient
    {
        get
        {
            lock (_gate)
            {
                EnsureHandlersCurrent();
                return _standardClient ??= CreateClient(isImageClient: false, timeout: TimeSpan.FromSeconds(30));
            }
        }
    }

    public HttpClient ImageClient
    {
        get
        {
            lock (_gate)
            {
                EnsureHandlersCurrent();
                return _imageClient ??= CreateClient(isImageClient: true, timeout: TimeSpan.FromSeconds(30));
            }
        }
    }

    public void InvalidateProxyConfiguration()
    {
        lock (_gate)
        {
            ResetProxyHandlersLocked();
        }
    }

    internal HttpMessageHandler CreateTransportHandler(bool isImageClient)
    {
        return new EhCookieAndRedirectHandler(
            _cookieStore,
            selectInvoker: uri => SelectInnerInvoker(uri, isImageClient),
            followRedirects: !isImageClient);
    }

    private HttpClient CreateClient(bool isImageClient, TimeSpan timeout)
    {
        return new HttpClient(CreateTransportHandler(isImageClient), disposeHandler: true)
        {
            Timeout = timeout
        };
    }

    private HttpMessageInvoker SelectInnerInvoker(Uri requestUri, bool isImageClient)
    {
        bool useProxy = ShouldUseProxy(requestUri);
        lock (_gate)
        {
            EnsureHandlersCurrent();

            if (isImageClient)
            {
                if (!useProxy)
                {
                    _directImageHandler ??= CreateDirectSocketsHandler(connectTimeout: TimeSpan.FromSeconds(20));
                    return _directImageInvoker ??= new HttpMessageInvoker(_directImageHandler, disposeHandler: false);
                }

                _proxyImageHandler ??= CreateProxySocketsHandler(connectTimeout: TimeSpan.FromSeconds(20));
                return _proxyImageInvoker ??= new HttpMessageInvoker(_proxyImageHandler, disposeHandler: false);
            }

            if (!useProxy)
            {
                _directStandardHandler ??= CreateDirectSocketsHandler(connectTimeout: TimeSpan.FromSeconds(10));
                return _directStandardInvoker ??= new HttpMessageInvoker(_directStandardHandler, disposeHandler: false);
            }

            _proxyStandardHandler ??= CreateProxySocketsHandler(connectTimeout: TimeSpan.FromSeconds(10));
            return _proxyStandardInvoker ??= new HttpMessageInvoker(_proxyStandardHandler, disposeHandler: false);
        }
    }

    private void EnsureHandlersCurrent()
    {
        if (_configuredProxyType != _settings.ProxyType
            || !string.Equals(_configuredProxyIp, _settings.ProxyIp, StringComparison.Ordinal)
            || _configuredProxyPort != _settings.ProxyPort)
        {
            ResetProxyHandlersLocked();
            _configuredProxyType = _settings.ProxyType;
            _configuredProxyIp = _settings.ProxyIp;
            _configuredProxyPort = _settings.ProxyPort;
        }
    }

    private void ResetProxyHandlersLocked()
    {
        _proxyStandardInvoker?.Dispose();
        _proxyImageInvoker?.Dispose();
        _proxyStandardHandler?.Dispose();
        _proxyImageHandler?.Dispose();
        _proxyStandardInvoker = null;
        _proxyImageInvoker = null;
        _proxyStandardHandler = null;
        _proxyImageHandler = null;
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

    private async ValueTask<Stream> ConnectWithEhHostsAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host;
        int port = context.DnsEndPoint.Port;

        IReadOnlyList<IPAddress> addresses = await _hosts.LookupAsync(host, cancellationToken).ConfigureAwait(false);
        Exception? lastException = null;

        foreach (IPAddress ip in addresses)
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
                socket.Dispose();
            }
        }

        throw lastException ?? new SocketException((int)SocketError.HostNotFound);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _standardClient?.Dispose();
            _imageClient?.Dispose();
            _directStandardInvoker?.Dispose();
            _proxyStandardInvoker?.Dispose();
            _directImageInvoker?.Dispose();
            _proxyImageInvoker?.Dispose();
            _directStandardHandler?.Dispose();
            _proxyStandardHandler?.Dispose();
            _directImageHandler?.Dispose();
            _proxyImageHandler?.Dispose();
        }
    }

    /// <summary>
    /// Handles <see cref="ICookieStore"/> persistence across HTTP redirects so cookies set on intermediate
    /// 3xx hops (such as sign-in / ExHentai redirects) are never lost.
    /// </summary>
    internal sealed class EhCookieAndRedirectHandler(
        ICookieStore cookieStore,
        Func<Uri, HttpMessageInvoker> selectInvoker,
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
                HttpMessageInvoker invoker = selectInvoker(current.RequestUri!);
                HttpResponseMessage response = await invoker.SendAsync(current, cancellationToken).ConfigureAwait(false);

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

using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace EhViewer.Core.Network;

/// <summary>
/// Hostname resolver supporting custom <c>hosts.db</c>, built-in EH/ExH/CDN IPs, RFC 8484 DoH queries,
/// and system DNS fallback, matching upstream <c>EhHosts</c>.
/// </summary>
public sealed class EhHosts : IDisposable
{
    private const int MaxDohCacheEntries = 256;

    private readonly record struct DohCacheEntry(IPAddress[] Addresses, long ExpiresAtUtcSeconds);

    private static readonly IReadOnlyDictionary<string, IPAddress[]> BuiltInCommonHosts = CreateBuiltInCommonHosts();
    private static readonly IReadOnlyDictionary<string, IPAddress[]> BuiltInExHosts = CreateBuiltInExHosts();

    private readonly HostsDatabase _hostsDatabase;
    private readonly IEhClientSettings _settings;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _systemDnsResolver;
    private readonly Func<long> _utcNowSeconds;
    private readonly HttpClient _dohHttpClient;
    private readonly bool _ownsDohHttpClient;
    private readonly object _dohGate = new();
    private readonly Dictionary<string, DohCacheEntry> _dohCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IPAddress[]>> _inFlightDoh = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _disposed;

    public EhHosts(
        HostsDatabase hostsDatabase,
        IEhClientSettings settings,
        HttpMessageHandler? dohHandler = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? systemDnsResolver = null)
        : this(hostsDatabase, settings, dohHandler, systemDnsResolver, utcNowSeconds: null)
    {
    }

    internal EhHosts(
        HostsDatabase hostsDatabase,
        IEhClientSettings settings,
        HttpMessageHandler? dohHandler,
        Func<string, CancellationToken, Task<IPAddress[]>>? systemDnsResolver,
        Func<long>? utcNowSeconds)
    {
        _hostsDatabase = hostsDatabase ?? throw new ArgumentNullException(nameof(hostsDatabase));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _systemDnsResolver = systemDnsResolver ?? Dns.GetHostAddressesAsync;
        _utcNowSeconds = utcNowSeconds ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        if (dohHandler is not null)
        {
            _dohHttpClient = new HttpClient(dohHandler, disposeHandler: false);
            _ownsDohHttpClient = false;
        }
        else
        {
            // Strict TLS verification (no custom RemoteCertificateValidationCallback override).
            var handler = new SocketsHttpHandler
            {
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };
            _dohHttpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            _ownsDohHttpClient = true;
        }
    }

    public async Task<IReadOnlyList<IPAddress>> LookupAsync(string hostname, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);

        if (IPAddress.TryParse(hostname, out IPAddress? literalIp))
        {
            return [literalIp];
        }

        string normalized = hostname.ToLowerInvariant();

        // 1. Custom user hosts.db override
        IReadOnlyList<IPAddress>? custom = _hostsDatabase.GetList(normalized);
        if (custom is { Count: > 0 })
        {
            return ShuffleCopy(custom);
        }

        // 2. Built-in hosts table (E-Hentai / CDNs and ExHentai)
        if (_settings.BuiltInHosts && BuiltInCommonHosts.TryGetValue(normalized, out IPAddress[]? builtIn))
        {
            return ShuffleCopy(builtIn);
        }

        if (_settings.BuiltInExHosts && BuiltInExHosts.TryGetValue(normalized, out IPAddress[]? builtInEx))
        {
            return ShuffleCopy(builtInEx);
        }

        // 3. Optional DNS over HTTPS (RFC 8484 wire format POST)
        if (_settings.UseDnsOverHttps)
        {
            IReadOnlyList<IPAddress> dohAddresses = await QueryDohAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (dohAddresses.Count > 0)
            {
                return ShuffleCopy(dohAddresses);
            }
        }

        // 4. System DNS fallback
        IPAddress[] systemAddresses = await _systemDnsResolver(hostname, cancellationToken).ConfigureAwait(false);
        if (systemAddresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        return ShuffleCopy(systemAddresses);
    }

    private async Task<IReadOnlyList<IPAddress>> QueryDohAsync(string hostname, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string cacheKey = $"{_settings.DohEndpoint}\n{hostname}";
        long now = _utcNowSeconds();

        Task<IPAddress[]> inFlightTask;
        lock (_dohGate)
        {
            if (_disposed)
            {
                return [];
            }

            if (_dohCache.TryGetValue(cacheKey, out DohCacheEntry cached) && cached.ExpiresAtUtcSeconds > now)
            {
                return cached.Addresses;
            }

            if (!_inFlightDoh.TryGetValue(cacheKey, out inFlightTask!))
            {
                inFlightTask = ExecuteDohLookupCoreAsync(hostname, cacheKey, _disposeCts.Token);
                _inFlightDoh[cacheKey] = inFlightTask;
            }
        }

        try
        {
            return await inFlightTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private async Task<IPAddress[]> ExecuteDohLookupCoreAsync(
        string hostname,
        string cacheKey,
        CancellationToken disposeToken)
    {
        try
        {
            Task<(List<IPAddress> Addresses, uint MinTtlSeconds)> queryA = SendDohQueryAsync(hostname, qtype: 1, disposeToken);
            Task<(List<IPAddress> Addresses, uint MinTtlSeconds)> queryAaaa = SendDohQueryAsync(hostname, qtype: 28, disposeToken);
            await Task.WhenAll(queryA, queryAaaa).ConfigureAwait(false);

            (List<IPAddress> aAddrs, uint aTtl) = queryA.Result;
            (List<IPAddress> aaaaAddrs, uint aaaaTtl) = queryAaaa.Result;

            if (aAddrs.Count == 0 && aaaaAddrs.Count == 0)
            {
                return [];
            }

            var results = new List<IPAddress>(aAddrs.Count + aaaaAddrs.Count);
            results.AddRange(aAddrs);
            results.AddRange(aaaaAddrs);
            IPAddress[] array = [.. results];

            uint minTtl = (aAddrs.Count, aaaaAddrs.Count) switch
            {
                (> 0, > 0) => Math.Min(aTtl, aaaaTtl),
                (> 0, 0) => aTtl,
                _ => aaaaTtl
            };
            long ttlSeconds = minTtl == 0 ? 300L : Math.Clamp((long)minTtl, 60L, 600L);
            long now = _utcNowSeconds();

            lock (_dohGate)
            {
                if (!_disposed)
                {
                    if (_dohCache.Count >= MaxDohCacheEntries && !_dohCache.ContainsKey(cacheKey))
                    {
                        PruneDohCacheLocked(now);
                    }

                    _dohCache[cacheKey] = new DohCacheEntry(array, now + ttlSeconds);
                }
            }

            return array;
        }
        catch
        {
            return [];
        }
        finally
        {
            lock (_dohGate)
            {
                _inFlightDoh.Remove(cacheKey);
            }
        }
    }

    private void PruneDohCacheLocked(long now)
    {
        List<string>? expired = null;
        foreach (KeyValuePair<string, DohCacheEntry> kv in _dohCache)
        {
            if (kv.Value.ExpiresAtUtcSeconds <= now)
            {
                expired ??= [];
                expired.Add(kv.Key);
            }
        }

        if (expired is not null)
        {
            foreach (string key in expired)
            {
                _dohCache.Remove(key);
            }
        }

        if (_dohCache.Count >= MaxDohCacheEntries)
        {
            _dohCache.Clear();
        }
    }

    private async Task<(List<IPAddress> Addresses, uint MinTtlSeconds)> SendDohQueryAsync(
        string hostname,
        ushort qtype,
        CancellationToken cancellationToken)
    {
        byte[] wireQuery = BuildDnsWireQuery(hostname, qtype);
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.DohEndpoint)
        {
            Content = new ByteArrayContent(wireQuery)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

        using HttpResponseMessage response = await _dohHttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ([], 0);
        }

        byte[] payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return ParseDnsWireResponseWithTtl(payload);
    }

    internal static byte[] BuildDnsWireQuery(string hostname, ushort qtype)
    {
        string trimmed = hostname.TrimEnd('.');
        string[] labels = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int nameLength = 1; // trailing 0 root label
        foreach (string label in labels)
        {
            nameLength += 1 + Encoding.ASCII.GetByteCount(label);
        }

        byte[] packet = new byte[12 + nameLength + 4];
        packet[2] = 0x01; // RD=1 standard query
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 1); // QDCOUNT = 1

        int offset = 12;
        foreach (string label in labels)
        {
            byte[] labelBytes = Encoding.ASCII.GetBytes(label);
            packet[offset++] = (byte)labelBytes.Length;
            Buffer.BlockCopy(labelBytes, 0, packet, offset, labelBytes.Length);
            offset += labelBytes.Length;
        }

        packet[offset++] = 0; // root terminator
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), qtype);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 2, 2), 1); // QCLASS = IN
        return packet;
    }

    internal static List<IPAddress> ParseDnsWireResponse(ReadOnlySpan<byte> payload)
        => ParseDnsWireResponseWithTtl(payload).Addresses;

    internal static (List<IPAddress> Addresses, uint MinTtlSeconds) ParseDnsWireResponseWithTtl(ReadOnlySpan<byte> payload)
    {
        var addresses = new List<IPAddress>();
        uint minTtl = 0;
        if (payload.Length < 12)
        {
            return (addresses, minTtl);
        }

        // RCODE must be 0 (NoError)
        if ((payload[3] & 0x0F) != 0)
        {
            return (addresses, minTtl);
        }

        ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2));
        ushort anCount = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(6, 2));

        int offset = 12;
        for (int i = 0; i < qdCount; i++)
        {
            if (!TrySkipDnsName(payload, ref offset) || offset + 4 > payload.Length)
            {
                return (addresses, minTtl);
            }

            offset += 4; // QTYPE + QCLASS
        }

        for (int i = 0; i < anCount; i++)
        {
            if (!TrySkipDnsName(payload, ref offset) || offset + 10 > payload.Length)
            {
                break;
            }

            ushort type = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
            ushort @class = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset + 2, 2));
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset + 4, 4));
            ushort rdLength = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset + 8, 2));
            offset += 10;

            if (offset + rdLength > payload.Length)
            {
                break;
            }

            if (@class == 1 && ((type == 1 && rdLength == 4) || (type == 28 && rdLength == 16)))
            {
                addresses.Add(new IPAddress(payload.Slice(offset, rdLength)));
                if (ttl > 0 && (minTtl == 0 || ttl < minTtl))
                {
                    minTtl = ttl;
                }
            }

            offset += rdLength;
        }

        return (addresses, minTtl);
    }

    private static bool TrySkipDnsName(ReadOnlySpan<byte> packet, ref int offset)
    {
        while (offset < packet.Length)
        {
            byte len = packet[offset];
            if (len == 0)
            {
                offset++;
                return true;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (offset + 1 >= packet.Length)
                {
                    return false;
                }

                offset += 2;
                return true;
            }

            offset += 1 + len;
        }

        return false;
    }

    private static IPAddress[] ShuffleCopy(IReadOnlyList<IPAddress> source)
    {
        IPAddress[] copy = new IPAddress[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            copy[i] = source[i];
        }

        Random.Shared.Shuffle(copy);
        return copy;
    }

    private static Dictionary<string, IPAddress[]> CreateBuiltInCommonHosts()
    {
        var map = new Dictionary<string, IPAddress[]>(StringComparer.Ordinal);
        Put(map, "e-hentai.org", "104.20.18.168", "104.20.19.168", "172.66.132.196", "172.66.140.62", "172.67.2.238");
        Put(map, "repo.e-hentai.org", "104.20.18.168", "104.20.19.168", "172.67.2.238");
        Put(map, "forums.e-hentai.org", "172.66.132.196", "172.66.140.62");
        Put(map, "upld.e-hentai.org", "89.149.221.236", "95.211.208.236");
        Put(map, "ehgt.org",
            "185.132.132.79", "62.112.8.152", "185.132.132.76",
            "2a00:7c80:0:123::3a85", "2a00:7c80:0:12d::38a1", "2a00:7c80:0:13b::37a4");
        Put(map, "raw.githubusercontent.com", "151.101.0.133", "151.101.64.133", "151.101.128.133", "151.101.192.133");
        return map;
    }

    private static Dictionary<string, IPAddress[]> CreateBuiltInExHosts()
    {
        var map = new Dictionary<string, IPAddress[]>(StringComparer.Ordinal);
        Put(map, "exhentai.org",
            "178.175.128.251", "178.175.128.252", "178.175.128.253", "178.175.128.254",
            "178.175.129.251", "178.175.129.252", "178.175.129.253", "178.175.129.254",
            "178.175.132.19", "178.175.132.20", "178.175.132.21", "178.175.132.22");
        Put(map, "upld.exhentai.org", "178.175.132.22", "178.175.129.254", "178.175.128.254");
        Put(map, "s.exhentai.org",
            "178.175.129.253", "178.175.129.254", "178.175.128.253",
            "178.175.128.254", "178.175.132.21", "178.175.132.22");
        return map;
    }

    private static void Put(Dictionary<string, IPAddress[]> map, string host, params string[] ips)
    {
        var list = new List<IPAddress>(ips.Length);
        foreach (string ip in ips)
        {
            IPAddress? parsed = HostsDatabase.ToHostAddress(host, ip);
            if (parsed is not null)
            {
                list.Add(parsed);
            }
        }

        map[host] = [.. list];
    }

    public void Dispose()
    {
        lock (_dohGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dohCache.Clear();
            _inFlightDoh.Clear();
        }

        _disposeCts.Cancel();
        _disposeCts.Dispose();
        if (_ownsDohHttpClient)
        {
            _dohHttpClient.Dispose();
        }
    }
}

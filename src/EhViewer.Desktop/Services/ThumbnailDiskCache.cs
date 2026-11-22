using System.Security.Cryptography;
using System.Text;

namespace EhViewer.Desktop.Services;

/// <summary>
/// On-disk thumbnail cache implementing the disk tier of the upstream three-level cache
/// (<c>conaco</c>): content-addressed files with LRU eviction under a byte budget.
/// </summary>
public sealed class ThumbnailDiskCache
{
    private static readonly TimeSpan DiskTouchThrottleInterval = TimeSpan.FromMinutes(10);

    private sealed class CacheFileEntry
    {
        public required string Key { get; init; }
        public required string Path { get; init; }
        public long Size { get; set; }
        public DateTime LastAccessUtc { get; set; }
        public DateTime LastDiskTouchUtc { get; set; }
    }

    private readonly string _rootDirectory;
    private readonly long _budgetBytes;
    private readonly Dictionary<string, CacheFileEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _totalBytes;
    private bool _scanned;

    public ThumbnailDiskCache(string rootDirectory, int budgetMb)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        _rootDirectory = rootDirectory;
        _budgetBytes = Math.Max(0, budgetMb) * 1024L * 1024L;
    }

    public string RootDirectory => _rootDirectory;

    public long BudgetBytes => _budgetBytes;

    /// <summary>
    /// Computes the stable cache key for a thumbnail URL at a requested width so different
    /// resolutions never overwrite each other.
    /// </summary>
    public static string ComputeKey(string url, int requestedWidth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{requestedWidth}\n{url}"));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Computes a resolution-independent disk cache key for the raw downloaded thumbnail bytes
    /// so requesting the same URL at multiple display widths shares a single disk blob.
    /// </summary>
    public static string ComputeUrlKey(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"raw\n{url}"));
        return Convert.ToHexStringLower(hash);
    }

    private string PathForKey(string key)
        => Path.Combine(_rootDirectory, key[..2], key + ".bin");

    public bool TryGetPath(string key, out string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        path = PathForKey(key);
        if (!File.Exists(path))
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        bool shouldWriteDiskTimestamp = false;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out CacheFileEntry? entry))
            {
                entry.LastAccessUtc = now;
                if (now - entry.LastDiskTouchUtc >= DiskTouchThrottleInterval)
                {
                    entry.LastDiskTouchUtc = now;
                    shouldWriteDiskTimestamp = true;
                }
            }
            else
            {
                shouldWriteDiskTimestamp = true;
                try
                {
                    long len = new FileInfo(path).Length;
                    if (_scanned)
                    {
                        _totalBytes += len;
                    }

                    _entries[key] = new CacheFileEntry
                    {
                        Key = key,
                        Path = path,
                        Size = len,
                        LastAccessUtc = now,
                        LastDiskTouchUtc = now
                    };
                }
                catch (IOException)
                {
                    return false;
                }
            }
        }

        if (shouldWriteDiskTimestamp)
        {
            Touch(path, now);
        }

        return true;
    }

    /// <summary>Stores <paramref name="data"/> and returns the resulting path.</summary>
    public string? Set(string key, ReadOnlySpan<byte> data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        string path = PathForKey(key);
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(data);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of orphaned temp file.
            }

            return null;
        }

        lock (_gate)
        {
            EnsureScannedLocked();

            DateTime now = DateTime.UtcNow;
            if (_entries.TryGetValue(key, out CacheFileEntry? existing))
            {
                _totalBytes = Math.Max(0, _totalBytes - existing.Size);
                existing.Size = data.Length;
                existing.LastAccessUtc = now;
                existing.LastDiskTouchUtc = now;
            }
            else
            {
                _entries[key] = new CacheFileEntry
                {
                    Key = key,
                    Path = path,
                    Size = data.Length,
                    LastAccessUtc = now,
                    LastDiskTouchUtc = now
                };
            }

            _totalBytes += data.Length;
            EvictIfNeededLocked(protectedKey: key);
            return path;
        }
    }

    /// <summary>Total bytes currently tracked, after a one-time directory scan.</summary>
    public long GetTotalBytes()
    {
        lock (_gate)
        {
            EnsureScannedLocked();
            return _totalBytes;
        }
    }

    private void EnsureScannedLocked()
    {
        if (_scanned)
        {
            return;
        }

        _scanned = true;
        _entries.Clear();
        _totalBytes = 0;
        if (!Directory.Exists(_rootDirectory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(_rootDirectory, "*.bin", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                long length = info.Length;
                string key = Path.GetFileNameWithoutExtension(file);
                if (_entries.TryGetValue(key, out CacheFileEntry? prev))
                {
                    _totalBytes = Math.Max(0, _totalBytes - prev.Size);
                }

                _entries[key] = new CacheFileEntry
                {
                    Key = key,
                    Path = file,
                    Size = length,
                    LastAccessUtc = info.LastAccessTimeUtc,
                    LastDiskTouchUtc = info.LastAccessTimeUtc
                };
                _totalBytes += length;
            }
            catch (IOException)
            {
                // A file removed between enumeration and stat is simply not cached.
            }
        }
    }

    private static void Touch(string path, DateTime utcNow)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, utcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Touching is an optimization; failing it must not break the cache hit.
        }
    }

    private void EvictIfNeededLocked(string? protectedKey)
    {
        if (_budgetBytes <= 0 || _totalBytes <= _budgetBytes || _entries.Count == 0)
        {
            return;
        }

        // Evict down to 85% of the budget so once full, subsequent writes do not thrash eviction on every insert.
        long targetBytes = (long)(_budgetBytes * 0.85);
        var candidates = new List<CacheFileEntry>(_entries.Count);
        foreach (CacheFileEntry entry in _entries.Values)
        {
            candidates.Add(entry);
        }

        candidates.Sort(static (a, b) => a.LastAccessUtc.CompareTo(b.LastAccessUtc));

        void DeleteEntry(CacheFileEntry entry)
        {
            try
            {
                File.Delete(entry.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            _totalBytes = Math.Max(0, _totalBytes - entry.Size);
            _entries.Remove(entry.Key);
        }

        foreach (CacheFileEntry entry in candidates)
        {
            if (_totalBytes <= targetBytes)
            {
                break;
            }

            if (string.Equals(entry.Key, protectedKey, StringComparison.Ordinal))
            {
                continue;
            }

            DeleteEntry(entry);
        }

        if (_totalBytes > _budgetBytes &&
            protectedKey is not null &&
            _entries.TryGetValue(protectedKey, out CacheFileEntry? self) &&
            self.Size > _budgetBytes)
        {
            DeleteEntry(self);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _totalBytes = 0;
            _scanned = true;
            if (!Directory.Exists(_rootDirectory))
            {
                return;
            }

            try
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked file simply survives this clear; the next one retries.
            }
        }
    }
}

using SkiaSharp;

namespace EhViewer.Desktop.Reader;

/// <summary>
/// Byte-budgeted LRU cache for decoded reader pages. Eviction defers bitmap disposal until every
/// outstanding <see cref="Lease"/> acquired by callers is disposed, eliminating use-after-dispose
/// races between UI rendering and background prefetching.
/// </summary>
public sealed class ReaderPageCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new();

    private long _totalBytes;
    private bool _disposed;

    public ReaderPageCache(long budgetBytes)
    {
        BudgetBytes = Math.Max(1, budgetBytes);
    }

    public long BudgetBytes { get; }

    public long CurrentBytes
    {
        get
        {
            lock (_gate)
            {
                return _totalBytes;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    public bool TryGet(string key, out SKBitmap? bitmap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            if (_index.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                bitmap = node.Value.Bitmap;
                return true;
            }
        }

        bitmap = null;
        return false;
    }

    public bool TryAcquire(string key, out Lease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            if (!_disposed && _index.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                node.Value.PinCount++;
                lease = new Lease(this, node.Value);
                return true;
            }
        }

        lease = null;
        return false;
    }

    public Lease AcquireOrWrap(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return new Lease(owner: null, new Entry(string.Empty, bitmap, EstimateBytes(bitmap)));
    }

    public void Put(string key, SKBitmap bitmap)
    {
        using Lease _ = PutAndAcquire(key, bitmap);
    }

    public Lease PutAndAcquire(string key, SKBitmap bitmap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(bitmap);

        long size = EstimateBytes(bitmap);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_index.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                _totalBytes -= existing.Value.Bytes;
                _order.Remove(existing);
                _index.Remove(key);
                if (!ReferenceEquals(existing.Value.Bitmap, bitmap))
                {
                    MarkEvictedLocked(existing.Value);
                }
            }

            var entry = new Entry(key, bitmap, size) { PinCount = 1 };
            LinkedListNode<Entry> node = _order.AddFirst(entry);
            _index[key] = node;
            _totalBytes += size;

            Evict();
            return new Lease(this, entry);
        }
    }

    private void Evict()
    {
        while (_totalBytes > BudgetBytes && _order.Last is { } last)
        {
            _order.RemoveLast();
            _index.Remove(last.Value.Key);
            _totalBytes -= last.Value.Bytes;
            MarkEvictedLocked(last.Value);
        }
    }

    /// <summary>Drops a single page, e.g. after a decode failure or when leaving a gallery.</summary>
    public bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            if (!_index.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                return false;
            }

            _order.Remove(node);
            _index.Remove(key);
            _totalBytes -= node.Value.Bytes;
            MarkEvictedLocked(node.Value);
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (Entry entry in _order)
            {
                MarkEvictedLocked(entry);
            }

            _order.Clear();
            _index.Clear();
            _totalBytes = 0;
        }
    }

    /// <summary>
    /// Drops only cache entries whose key starts with <paramref name="prefix"/> so closing one
    /// reader window never evicts another open reader window's decoded pages.
    /// </summary>
    public int RemoveByPrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);

        lock (_gate)
        {
            var matchingNodes = new List<LinkedListNode<Entry>>();
            for (LinkedListNode<Entry>? node = _order.First; node is not null; node = node.Next)
            {
                if (node.Value.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    matchingNodes.Add(node);
                }
            }

            foreach (LinkedListNode<Entry> node in matchingNodes)
            {
                _order.Remove(node);
                _index.Remove(node.Value.Key);
                _totalBytes -= node.Value.Bytes;
                MarkEvictedLocked(node.Value);
            }

            return matchingNodes.Count;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Clear();
        _disposed = true;
    }

    private void Release(Entry entry)
    {
        bool shouldDispose = false;
        lock (_gate)
        {
            if (entry.PinCount > 0)
            {
                entry.PinCount--;
            }

            if (entry.Evicted && entry.PinCount == 0)
            {
                shouldDispose = true;
            }
        }

        if (shouldDispose)
        {
            entry.Bitmap.Dispose();
        }
    }

    private static void MarkEvictedLocked(Entry entry)
    {
        entry.Evicted = true;
        if (entry.PinCount == 0)
        {
            entry.Bitmap.Dispose();
        }
    }

    private static long EstimateBytes(SKBitmap bitmap)
    {
        SKImageInfo info = bitmap.Info;
        return Math.Max(1L, (long)info.Width * info.Height * Math.Max(1, info.BytesPerPixel));
    }

    internal sealed class Entry
    {
        public Entry(string key, SKBitmap bitmap, long bytes)
        {
            Key = key;
            Bitmap = bitmap;
            Bytes = bytes;
        }

        public string Key { get; }
        public SKBitmap Bitmap { get; }
        public long Bytes { get; }
        public int PinCount { get; set; }
        public bool Evicted { get; set; }
    }

    /// <summary>Pinned lease preventing cache eviction from disposing the underlying bitmap while in use.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly ReaderPageCache? _owner;
        private readonly Entry _entry;
        private int _disposed;

        internal Lease(ReaderPageCache? owner, Entry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public SKBitmap Bitmap => _entry.Bitmap;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_owner is not null)
            {
                _owner.Release(_entry);
            }
            else
            {
                _entry.Bitmap.Dispose();
            }
        }
    }
}

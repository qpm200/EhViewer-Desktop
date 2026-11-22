namespace EhViewer.Desktop.Services;

/// <summary>
/// Bounded capacity Least-Recently-Used cache for UI-owned bitmaps and preview tiles.
/// </summary>
internal sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
    private readonly LinkedList<(TKey Key, TValue Value)> _lru = new();

    public BoundedLruCache(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        Capacity = capacity;
        _map = new Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>>(capacity);
    }

    public int Capacity { get; }

    public int Count => _map.Count;

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out LinkedListNode<(TKey Key, TValue Value)>? node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            value = node.Value.Value;
            return true;
        }

        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out LinkedListNode<(TKey Key, TValue Value)>? existing))
        {
            existing.Value = (key, value);
            _lru.Remove(existing);
            _lru.AddFirst(existing);
            return;
        }

        var node = new LinkedListNode<(TKey Key, TValue Value)>((key, value));
        _lru.AddFirst(node);
        _map[key] = node;

        while (_map.Count > Capacity && _lru.Last is { } oldest)
        {
            _lru.RemoveLast();
            _map.Remove(oldest.Value.Key);
        }
    }

    public void Clear()
    {
        _map.Clear();
        _lru.Clear();
    }
}

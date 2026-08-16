namespace Apex.SqlClient.Internal;

internal sealed class LruCache<TKey, TValue>
  where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _usage = new();

    public LruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _entries = new Dictionary<TKey, LinkedListNode<Entry>>(capacity, comparer);
    }

    public int Count => _entries.Count;

    public bool TryGet(TKey key, out TValue? value)
    {
        if (!_entries.TryGetValue(key, out var node))
        {
            value = default;
            return false;
        }

        _usage.Remove(node);
        _usage.AddFirst(node);
        value = node.Value.Value;
        return true;
    }

    public bool Add(TKey key, TValue value, out TValue? evicted)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            _usage.Remove(existing);
            existing.Value = new Entry(key, value);
            _usage.AddFirst(existing);
            evicted = default;
            return false;
        }

        var node = _usage.AddFirst(new Entry(key, value));
        _entries.Add(key, node);
        if (_entries.Count <= _capacity)
        {
            evicted = default;
            return false;
        }

        var eldest = _usage.Last!;
        _usage.RemoveLast();
        _entries.Remove(eldest.Value.Key);
        evicted = eldest.Value.Value;
        return true;
    }

    public bool Remove(TKey key, out TValue? value)
    {
        if (!_entries.Remove(key, out var node))
        {
            value = default;
            return false;
        }

        _usage.Remove(node);
        value = node.Value.Value;
        return true;
    }

    public TValue[] DrainValues()
    {
        TValue[] values = new TValue[_entries.Count];
        var index = 0;
        foreach (var entry in _usage)
        {
            values[index++] = entry.Value;
        }

        _entries.Clear();
        _usage.Clear();
        return values;
    }

    private readonly record struct Entry(TKey Key, TValue Value);
}

using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace Apex.SqlClient.Internal;

internal sealed class SqlColumnOrdinalMap
{
    private readonly Dictionary<string, int> _ordinals;
    private readonly string[] _names;

    internal SqlColumnOrdinalMap(IReadOnlyList<SqlColumn> columns)
    {
        _ordinals = new Dictionary<string, int>(columns.Count, StringComparer.Ordinal);
        _names = new string[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var name = columns[i].Name;
            _names[i] = name;
            _ordinals.TryAdd(name, i);
        }
    }

    internal bool Matches(IReadOnlyList<SqlColumn> columns)
    {
        if (_names.Length != columns.Count)
        {
            return false;
        }

        for (var i = 0; i < _names.Length; i++)
        {
            if (!string.Equals(_names[i], columns[i].Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal bool TryGetValue(string name, out int ordinal) =>
      _ordinals.TryGetValue(name, out ordinal);
}

internal static class SqlColumnOrdinalMapCache
{
    private const int Capacity = 256;
    private const int IndexMask = Capacity - 1;
    private static readonly SqlColumnOrdinalMap?[] s_entries = new SqlColumnOrdinalMap[Capacity];

    internal static SqlColumnOrdinalMap GetOrAdd(IReadOnlyList<SqlColumn> columns)
    {
        var hash = GetNamesHashCode(columns);
        var index = hash & IndexMask;
        var cached = Volatile.Read(ref s_entries[index]);
        if (cached is not null && cached.Matches(columns))
        {
            return cached;
        }

        SqlColumnOrdinalMap map = new(columns);
        Volatile.Write(ref s_entries[index], map);
        return map;
    }

    private static int GetNamesHashCode(IReadOnlyList<SqlColumn> columns)
    {
        var hash = unchecked((ulong)columns.Count);
        for (var i = 0; i < columns.Count; i++)
        {
            hash = XxHash3.HashToUInt64(
              MemoryMarshal.AsBytes(columns[i].Name.AsSpan()),
              unchecked((long)hash));
        }

        return unchecked((int)(hash ^ (hash >> 32)));
    }
}

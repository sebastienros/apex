using System.IO.Hashing;
using System.Text;

namespace Apex.MySqlClient.Internal;

/// <summary>
/// Caches decoded UTF-8 strings in a fixed size, two chance table so repeated column values do
/// not allocate a new string on every row.
/// </summary>
internal sealed class Utf8StringCache
{
    private static readonly Encoding s_utf8 = new UTF8Encoding(false, true);
    private readonly int _maximumByteLength;
    private Table? _table;

    internal Utf8StringCache(int capacity, int maximumByteLength)
    {
        if (capacity <= 0 || maximumByteLength <= 0)
        {
            return;
        }

        var normalizedCapacity = 1;
        while (normalizedCapacity < capacity)
        {
            normalizedCapacity <<= 1;
        }

        _table = new Table(normalizedCapacity);
        _maximumByteLength = maximumByteLength;
    }

    internal string GetString(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return string.Empty;
        }

        var table = Volatile.Read(ref _table);
        if (table is null || value.Length > _maximumByteLength)
        {
            return s_utf8.GetString(value);
        }

        var hash = XxHash3.HashToUInt64(value);
        hash = hash == 0 ? 1 : hash;
        var index = (int)hash & (table.Entries.Length - 1);
        var entry = Volatile.Read(ref table.Entries[index]);
        if (entry is not null &&
            entry.Hash == hash &&
            entry.Utf8 is not null &&
            entry.Utf8.AsSpan().SequenceEqual(value))
        {
            return entry.Value!;
        }

        var decoded = s_utf8.GetString(value);
        if (unchecked((ulong)Volatile.Read(ref table.CandidateHashes[index])) == hash)
        {
            Volatile.Write(ref table.Entries[index], new Entry(hash, value.ToArray(), decoded));
            Volatile.Write(ref table.CandidateHashes[index], 0);
        }
        else
        {
            Volatile.Write(ref table.CandidateHashes[index], unchecked((long)hash));
        }

        return decoded;
    }

    internal void Disable() => Volatile.Write(ref _table, null);

    private sealed record Entry(ulong Hash, byte[] Utf8, string Value);

    private sealed class Table(int capacity)
    {
        internal Entry?[] Entries { get; } = new Entry?[capacity];

        internal long[] CandidateHashes { get; } = new long[capacity];
    }
}

/// <summary>Reuses boxes for the small scalar values that dominate result sets.</summary>
internal static class BoxedScalarCache
{
    private const int Minimum = -128;
    private const int Maximum = 255;
    private static readonly object[] s_sByteValues = CreateSBytes();
    private static readonly object[] s_byteValues = CreateBytes();
    private static readonly object[] s_int16Values = Create(static value => (object)(short)value);
    private static readonly object[] s_uInt16Values = CreateUnsigned(static value => (object)(ushort)value);
    private static readonly object[] s_int32Values = Create(static value => value);
    private static readonly object[] s_uInt32Values = CreateUnsigned(static value => (object)(uint)value);
    private static readonly object[] s_int64Values = Create(static value => (object)(long)value);
    private static readonly object[] s_uInt64Values = CreateUnsigned(static value => (object)(ulong)value);
    private static readonly object s_true = true;
    private static readonly object s_false = false;

    internal static object Box(bool value) => value ? s_true : s_false;

    internal static object Box(sbyte value) => s_sByteValues[value - sbyte.MinValue];

    internal static object Box(byte value) => s_byteValues[value];

    internal static object Box(short value) =>
      value is >= Minimum and <= Maximum ? s_int16Values[value - Minimum] : value;

    internal static object Box(ushort value) =>
      value <= Maximum ? s_uInt16Values[value] : value;

    internal static object Box(int value) =>
      value is >= Minimum and <= Maximum ? s_int32Values[value - Minimum] : value;

    internal static object Box(uint value) =>
      value <= Maximum ? s_uInt32Values[value] : value;

    internal static object Box(long value) =>
      value is >= Minimum and <= Maximum ? s_int64Values[value - Minimum] : value;

    internal static object Box(ulong value) =>
      value <= Maximum ? s_uInt64Values[value] : value;

    private static object[] Create(Func<int, object> factory)
    {
        var values = new object[Maximum - Minimum + 1];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = factory(i + Minimum);
        }

        return values;
    }

    private static object[] CreateUnsigned(Func<int, object> factory)
    {
        var values = new object[Maximum + 1];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = factory(i);
        }

        return values;
    }

    private static object[] CreateSBytes()
    {
        var values = new object[byte.MaxValue + 1];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (sbyte)(i + sbyte.MinValue);
        }

        return values;
    }

    private static object[] CreateBytes()
    {
        var values = new object[byte.MaxValue + 1];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (byte)i;
        }

        return values;
    }
}

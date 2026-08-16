using System.IO.Hashing;

namespace Apex.MsSqlClient.Internal;

internal sealed class MsSqlStringCache
{
    private readonly int _maximumByteLength;
    private Table? _table;

    internal MsSqlStringCache(int capacity, int maximumByteLength)
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

    internal string GetString(ReadOnlySpan<byte> value, int codePage)
    {
        var encoding = TdsCollationCodec.GetEncoding(codePage);
        var table = Volatile.Read(ref _table);
        if (table is null || value.Length > _maximumByteLength)
        {
            return encoding.GetString(value);
        }

        var hash = XxHash3.HashToUInt64(value, codePage);
        hash = hash == 0 ? 1 : hash;
        var index = (int)hash & (table.Entries.Length - 1);
        var entry = Volatile.Read(ref table.Entries[index]);
        if (entry is not null &&
            entry.Hash == hash &&
            entry.CodePage == codePage &&
            entry.Bytes is not null &&
            entry.Bytes.AsSpan().SequenceEqual(value))
        {
            return entry.Value!;
        }

        var decoded = encoding.GetString(value);
        if (unchecked((ulong)Volatile.Read(ref table.CandidateHashes[index])) == hash)
        {
            Volatile.Write(
              ref table.Entries[index],
              new Entry(hash, codePage, value.ToArray(), decoded));
            Volatile.Write(ref table.CandidateHashes[index], 0);
        }
        else
        {
            Volatile.Write(ref table.CandidateHashes[index], unchecked((long)hash));
        }

        return decoded;
    }

    internal void Disable() => Volatile.Write(ref _table, null);

    private sealed record Entry(ulong Hash, int CodePage, byte[] Bytes, string Value);

    private sealed class Table(int capacity)
    {
        internal Entry?[] Entries { get; } = new Entry?[capacity];

        internal long[] CandidateHashes { get; } = new long[capacity];
    }
}

internal static class MsSqlBoxedScalarCache
{
    private const int Minimum = -128;
    private const int Maximum = 255;
    private static readonly object[] s_bytes = CreateBytes();
    private static readonly object[] s_int16Values =
      Create(static value => (object)(short)value);
    private static readonly object[] s_int32Values =
      Create(static value => value);
    private static readonly object[] s_int64Values =
      Create(static value => (object)(long)value);
    private static readonly object s_true = true;
    private static readonly object s_false = false;

    internal static object Box(bool value) => value ? s_true : s_false;

    internal static object Box(byte value) => s_bytes[value];

    internal static object Box(short value) =>
      value is >= Minimum and <= Maximum
        ? s_int16Values[value - Minimum]
        : value;

    internal static object Box(int value) =>
      value is >= Minimum and <= Maximum
        ? s_int32Values[value - Minimum]
        : value;

    internal static object Box(long value) =>
      value is >= Minimum and <= Maximum
        ? s_int64Values[value - Minimum]
        : value;

    private static object[] Create(Func<int, object> factory)
    {
        var values = new object[Maximum - Minimum + 1];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = factory(i + Minimum);
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

using System.Diagnostics.CodeAnalysis;
using Apex.SqlClient.Internal;

namespace Apex.SqlClient;

/// <summary>An immutable materialized database row.</summary>
public readonly struct SqlRow
{
    private readonly IReadOnlyList<SqlColumn> _columns;
    private readonly SqlColumnOrdinalMap _ordinals;
    private readonly ISqlRowDecoder _decoder;
    private readonly ReadOnlyMemory<byte> _memory;

    internal SqlRow(
        IReadOnlyList<SqlColumn> columns,
      SqlColumnOrdinalMap ordinals,
        SqlRowPage page,
        int offset,
        int length)
    {
        _columns = columns;
        _ordinals = ordinals;
        _decoder = page.Decoder;
        _memory = page.Data.AsMemory(offset, length);
    }

    internal SqlRow(
        IReadOnlyList<SqlColumn> columns,
        SqlColumnOrdinalMap ordinals,
        ISqlRowDecoder decoder,
        ReadOnlyMemory<byte> memory)
    {
        _columns = columns;
        _ordinals = ordinals;
        _decoder = decoder;
        _memory = memory;
    }

    public int Count => _decoder.GetFieldCount(RowMemory);

    public bool IsNull(int ordinal) =>
      _decoder.IsNull(RowMemory, ordinal);

    [SuppressMessage(
        "Usage",
        "CA2201:Do not raise reserved exception types",
        Justification = "Matches the IDataRecord.GetOrdinal contract.")]
    public int GetOrdinal(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_ordinals.TryGetValue(name, out var ordinal))
        {
            return ordinal;
        }

        throw new IndexOutOfRangeException($"Column '{name}' does not exist.");
    }

    /// <summary>
    /// Gets a common CLR or provider-specific value without routing through
    /// object decoding.
    /// </summary>
    public T Get<T>(int ordinal) =>
      SqlRowDecoder.Decode<T>(
        _decoder,
        RowMemory,
        ordinal,
        _columns[ordinal],
        copyReadOnlyMemory: false);

    public T Get<T>(string name) => Get<T>(GetOrdinal(name));

    public bool GetBoolean(int ordinal) =>
      _decoder.DecodeBoolean(RowMemory, ordinal, _columns[ordinal]);

    public bool GetBoolean(string name) => GetBoolean(GetOrdinal(name));

    public short GetInt16(int ordinal) =>
      _decoder.DecodeInt16(RowMemory, ordinal, _columns[ordinal]);

    public short GetInt16(string name) => GetInt16(GetOrdinal(name));

    public int GetInt32(int ordinal) =>
      _decoder.DecodeInt32(RowMemory, ordinal, _columns[ordinal]);

    public int GetInt32(string name) => GetInt32(GetOrdinal(name));

    public long GetInt64(int ordinal) =>
      _decoder.DecodeInt64(RowMemory, ordinal, _columns[ordinal]);

    public long GetInt64(string name) => GetInt64(GetOrdinal(name));

    public float GetFloat(int ordinal) =>
      _decoder.DecodeFloat(RowMemory, ordinal, _columns[ordinal]);

    public float GetFloat(string name) => GetFloat(GetOrdinal(name));

    public double GetDouble(int ordinal) =>
      _decoder.DecodeDouble(RowMemory, ordinal, _columns[ordinal]);

    public double GetDouble(string name) => GetDouble(GetOrdinal(name));

    public decimal GetDecimal(int ordinal) =>
      _decoder.DecodeDecimal(RowMemory, ordinal, _columns[ordinal]);

    public decimal GetDecimal(string name) => GetDecimal(GetOrdinal(name));

    public string GetString(int ordinal) =>
      _decoder.DecodeString(RowMemory, ordinal, _columns[ordinal])!;

    public string GetString(string name) => GetString(GetOrdinal(name));

    public Guid GetGuid(int ordinal) =>
      _decoder.DecodeGuid(RowMemory, ordinal, _columns[ordinal]);

    public Guid GetGuid(string name) => GetGuid(GetOrdinal(name));

    public DateOnly GetDateOnly(int ordinal) =>
      _decoder.DecodeDateOnly(RowMemory, ordinal, _columns[ordinal]);

    public DateOnly GetDateOnly(string name) => GetDateOnly(GetOrdinal(name));

    public TimeOnly GetTimeOnly(int ordinal) =>
      _decoder.DecodeTimeOnly(RowMemory, ordinal, _columns[ordinal]);

    public TimeOnly GetTimeOnly(string name) => GetTimeOnly(GetOrdinal(name));

    public DateTime GetDateTime(int ordinal) =>
      _decoder.DecodeDateTime(RowMemory, ordinal, _columns[ordinal]);

    public DateTime GetDateTime(string name) => GetDateTime(GetOrdinal(name));

    public DateTimeOffset GetDateTimeOffset(int ordinal) =>
      _decoder.DecodeDateTimeOffset(RowMemory, ordinal, _columns[ordinal]);

    public DateTimeOffset GetDateTimeOffset(string name) =>
      GetDateTimeOffset(GetOrdinal(name));

    public byte[] GetBytes(int ordinal) =>
      _decoder.DecodeBytes(RowMemory, ordinal, _columns[ordinal])!;

    public byte[] GetBytes(string name) => GetBytes(GetOrdinal(name));

    public TElement[]? GetArray<TElement>(int ordinal) =>
      _decoder.DecodeArray<TElement>(
        RowMemory,
        ordinal,
        _columns[ordinal]);

    public TElement[]? GetArray<TElement>(string name) =>
      GetArray<TElement>(GetOrdinal(name));

    public bool TryGet<T>(int ordinal, [MaybeNullWhen(false)] out T value)
    {
        if (!IsNull(ordinal))
        {
            try
            {
                value = Get<T>(ordinal);
                return true;
            }
            catch (InvalidCastException)
            {
            }
        }

        value = default;
        return false;
    }

    private ReadOnlyMemory<byte> RowMemory => _memory;
}

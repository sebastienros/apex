using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Apex.SqlClient.Internal;

namespace Apex.SqlClient.Tests;

internal sealed class TestRowDecoder : ISqlRowDecoder
{
    internal int DecodeCount { get; private set; }

    internal SqlRow CreateRow(
        IReadOnlyList<SqlColumn> columns,
        params object?[] values)
    {
        SqlRowPageBuilder builder = new(this);
        builder.Add(Encode(values));
        return builder.Build(columns)[0];
    }

    internal static byte[] Encode(params object?[] values)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(checked((byte)values.Length));
        foreach (var value in values)
        {
            if (value is null)
            {
                writer.Write(-1);
                continue;
            }

            var payload = value switch
            {
                int typed => BitConverter.GetBytes(typed),
                string typed => Encoding.UTF8.GetBytes(typed),
                byte[] typed => typed,
                _ => throw new ArgumentException(
                  $"Unsupported test value {value.GetType().FullName}.",
                  nameof(values)),
            };
            writer.Write(payload.Length);
            writer.Write(payload);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public int GetFieldCount(ReadOnlyMemory<byte> row) =>
      row.Span[0];

    public bool IsNull(ReadOnlyMemory<byte> row, int ordinal) =>
      GetField(row, ordinal).IsNull;

    public object? DecodeObject(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        DecodeCount++;
        var field = GetField(row, ordinal);
        if (field.IsNull)
        {
            return null;
        }

        return column.TypeId switch
        {
            17 => field.Value.ToArray(),
            23 => BinaryPrimitives.ReadInt32LittleEndian(
              field.Value.Span),
            25 => Encoding.UTF8.GetString(field.Value.Span),
            _ => throw CannotRead(column, typeof(object)),
        };
    }

    public int DecodeInt32(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        Validate(column, 23, typeof(int));
        DecodeCount++;
        return BinaryPrimitives.ReadInt32LittleEndian(
          GetRequiredField(row, ordinal).Value.Span);
    }

    public int? DecodeNullableInt32(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        Validate(column, 23, typeof(int?));
        DecodeCount++;
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : BinaryPrimitives.ReadInt32LittleEndian(
            field.Value.Span);
    }

    public string? DecodeString(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        Validate(column, 25, typeof(string));
        DecodeCount++;
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : Encoding.UTF8.GetString(field.Value.Span);
    }

    public byte[]? DecodeBytes(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        Validate(column, 17, typeof(byte[]));
        DecodeCount++;
        var field = GetField(row, ordinal);
        return field.IsNull ? null : field.Value.ToArray();
    }

    public ReadOnlyMemory<byte> DecodeReadOnlyMemory(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        Validate(column, 17, typeof(ReadOnlyMemory<byte>));
        DecodeCount++;
        return GetRequiredField(row, ordinal).Value;
    }

    public ReadOnlyMemory<byte>? DecodeNullableReadOnlyMemory(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        Validate(column, 17, typeof(ReadOnlyMemory<byte>?));
        DecodeCount++;
        var field = GetField(row, ordinal);
        return field.IsNull ? null : field.Value;
    }

    public bool DecodeBoolean(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<bool>(column);

    public bool? DecodeNullableBoolean(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<bool?>(column);

    public short DecodeInt16(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<short>(column);

    public short? DecodeNullableInt16(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<short?>(column);

    public long DecodeInt64(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<long>(column);

    public long? DecodeNullableInt64(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<long?>(column);

    public float DecodeFloat(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<float>(column);

    public float? DecodeNullableFloat(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<float?>(column);

    public double DecodeDouble(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<double>(column);

    public double? DecodeNullableDouble(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<double?>(column);

    public decimal DecodeDecimal(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<decimal>(column);

    public decimal? DecodeNullableDecimal(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<decimal?>(column);

    public Guid DecodeGuid(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<Guid>(column);

    public Guid? DecodeNullableGuid(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<Guid?>(column);

    public DateOnly DecodeDateOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<DateOnly>(column);

    public DateOnly? DecodeNullableDateOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<DateOnly?>(column);

    public TimeOnly DecodeTimeOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<TimeOnly>(column);

    public TimeOnly? DecodeNullableTimeOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<TimeOnly?>(column);

    public DateTime DecodeDateTime(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<DateTime>(column);

    public DateTime? DecodeNullableDateTime(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<DateTime?>(column);

    public DateTimeOffset DecodeDateTimeOffset(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<DateTimeOffset>(column);

    public DateTimeOffset? DecodeNullableDateTimeOffset(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<DateTimeOffset?>(column);

    public JsonElement DecodeJsonElement(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<JsonElement>(column);

    public JsonElement? DecodeNullableJsonElement(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<JsonElement?>(column);

    public TElement[]? DecodeArray<TElement>(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column) =>
      Throw<TElement[]?>(column);

    public T Decode<T>(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column,
        bool copyReadOnlyMemory)
    {
        if (typeof(T) == typeof(int))
        {
            var value = DecodeInt32(row, ordinal, column);
            return Unsafe.As<int, T>(ref value);
        }

        if (typeof(T) == typeof(int?))
        {
            var value = DecodeNullableInt32(row, ordinal, column);
            return Unsafe.As<int?, T>(ref value);
        }

        if (typeof(T) == typeof(string))
        {
            var value = DecodeString(row, ordinal, column);
            return Unsafe.As<string?, T>(ref value);
        }

        if (typeof(T) == typeof(byte[]))
        {
            var value = DecodeBytes(row, ordinal, column);
            return Unsafe.As<byte[]?, T>(ref value);
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            var value =
              DecodeReadOnlyMemory(row, ordinal, column);
            if (copyReadOnlyMemory)
            {
                value = value.ToArray();
            }

            return Unsafe.As<ReadOnlyMemory<byte>, T>(ref value);
        }

        if (typeof(T) == typeof(object))
        {
            return (T)DecodeObject(row, ordinal, column)!;
        }

        return Throw<T>(column);
    }

    private static Field GetRequiredField(
        ReadOnlyMemory<byte> row,
        int ordinal)
    {
        var field = GetField(row, ordinal);
        return field.IsNull
          ? throw new InvalidCastException(
            $"Column {ordinal} contains NULL.")
          : field;
    }

    private static Field GetField(
        ReadOnlyMemory<byte> row,
        int ordinal)
    {
        var span = row.Span;
        int count = span[0];
        if ((uint)ordinal >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        var position = 1;
        for (var index = 0; index < count; index++)
        {
            var length =
              BinaryPrimitives.ReadInt32LittleEndian(span[position..]);
            position += sizeof(int);
            if (length < 0)
            {
                if (index == ordinal)
                {
                    return new Field(default, IsNull: true);
                }

                continue;
            }

            if (index == ordinal)
            {
                return new Field(
                  row.Slice(position, length),
                  IsNull: false);
            }

            position += length;
        }

        throw new InvalidDataException();
    }

    private static void Validate(
        SqlColumn column,
        uint typeId,
        Type requestedType)
    {
        if (column.TypeId != typeId)
        {
            throw CannotRead(column, requestedType);
        }
    }

    private static T Throw<T>(SqlColumn column) =>
      throw CannotRead(column, typeof(T));

    private static InvalidCastException CannotRead(
        SqlColumn column,
        Type requestedType) =>
      new(
        $"Type {column.TypeId} cannot be read as " +
        $"{requestedType.FullName}.");

    private readonly record struct Field(
        ReadOnlyMemory<byte> Value,
        bool IsNull);
}

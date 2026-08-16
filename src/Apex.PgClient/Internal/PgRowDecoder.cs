using System.Buffers.Binary;
using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.PgClient.Internal;

internal sealed class PgRowDecoder : ISqlRowDecoder
{
    private readonly Utf8StringCache _strings;

    internal PgRowDecoder(
        int stringCacheCapacity,
        int stringCacheMaximumByteLength)
    {
        _strings = new Utf8StringCache(
          stringCacheCapacity,
          stringCacheMaximumByteLength);
    }

    public int GetFieldCount(ReadOnlyMemory<byte> row) =>
      GetFieldCount(row.Span);

    internal static int GetFieldCount(ReadOnlySpan<byte> row)
    {
        Ensure(row, 0, sizeof(short));
        return BinaryPrimitives.ReadUInt16BigEndian(row);
    }

    public bool IsNull(ReadOnlyMemory<byte> row, int ordinal) =>
      GetField(row, ordinal).IsNull;

    public object? DecodeObject(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureFormat(column, typeof(object));
        var field = GetField(row, ordinal);
        if (field.IsNull)
        {
            return null;
        }

        if (CanDecodeAsString(column))
        {
            return _strings.GetString(field.Value.Span);
        }

        return column.TypeId switch
        {
            16 => BoxedScalarCache.Box(
              DecodeBooleanValue(column.Format, field.Value.Span)),
            21 => BoxedScalarCache.Box(
              DecodeInt16Value(column.Format, field.Value.Span)),
            23 => BoxedScalarCache.Box(
              DecodeInt32Value(column.Format, field.Value.Span)),
            20 => BoxedScalarCache.Box(
              DecodeInt64Value(column.Format, field.Value.Span)),
            _ => column.Format == SqlDataFormat.Binary
              ? PgBinaryCodec.Decode(column.TypeId, field.Value)
              : PgTextCodec.Decode(column.TypeId, field.Value),
        };
    }

    public bool DecodeBoolean(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 16, typeof(bool));
        var field = GetRequiredField(row, ordinal);
        return DecodeBooleanValue(column.Format, field.Value.Span);
    }

    public bool? DecodeNullableBoolean(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 16, typeof(bool?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeBooleanValue(column.Format, field.Value.Span);
    }

    public short DecodeInt16(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 21, typeof(short));
        var field = GetRequiredField(row, ordinal);
        return DecodeInt16Value(column.Format, field.Value.Span);
    }

    public short? DecodeNullableInt16(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 21, typeof(short?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeInt16Value(column.Format, field.Value.Span);
    }

    public int DecodeInt32(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 23, typeof(int));
        var field = GetRequiredField(row, ordinal);
        return DecodeInt32Value(column.Format, field.Value.Span);
    }

    public int? DecodeNullableInt32(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 23, typeof(int?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeInt32Value(column.Format, field.Value.Span);
    }

    public long DecodeInt64(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 20, typeof(long));
        var field = GetRequiredField(row, ordinal);
        return DecodeInt64Value(column.Format, field.Value.Span);
    }

    public long? DecodeNullableInt64(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 20, typeof(long?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeInt64Value(column.Format, field.Value.Span);
    }

    public float DecodeFloat(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 700, typeof(float));
        var field = GetRequiredField(row, ordinal);
        return DecodeFloatValue(column.Format, field.Value.Span);
    }

    public float? DecodeNullableFloat(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 700, typeof(float?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeFloatValue(column.Format, field.Value.Span);
    }

    public double DecodeDouble(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 701, typeof(double));
        var field = GetRequiredField(row, ordinal);
        return DecodeDoubleValue(column.Format, field.Value.Span);
    }

    public double? DecodeNullableDouble(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 701, typeof(double?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeDoubleValue(column.Format, field.Value.Span);
    }

    public decimal DecodeDecimal(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 1700, typeof(decimal));
        var field = GetRequiredField(row, ordinal);
        return DecodeDecimalValue(column.Format, field.Value.Span);
    }

    public decimal? DecodeNullableDecimal(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 1700, typeof(decimal?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeDecimalValue(column.Format, field.Value.Span);
    }

    public string? DecodeString(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureStringType(column, typeof(string));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : _strings.GetString(field.Value.Span);
    }

    public byte[]? DecodeBytes(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 17, typeof(byte[]));
        var field = GetField(row, ordinal);
        if (field.IsNull)
        {
            return null;
        }

        return column.Format == SqlDataFormat.Binary
          ? PgBinaryCodec.DecodeBytes(field.Value.Span)
          : PgTextCodec.DecodeBytes(field.Value.Span);
    }

    public ReadOnlyMemory<byte> DecodeReadOnlyMemory(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 17, typeof(ReadOnlyMemory<byte>));
        var field = GetRequiredField(row, ordinal);
        return column.Format == SqlDataFormat.Binary
          ? field.Value
          : PgTextCodec.DecodeBytes(field.Value.Span);
    }

    public ReadOnlyMemory<byte>? DecodeNullableReadOnlyMemory(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 17, typeof(ReadOnlyMemory<byte>?));
        var field = GetField(row, ordinal);
        if (field.IsNull)
        {
            return null;
        }

        return column.Format == SqlDataFormat.Binary
          ? field.Value
          : PgTextCodec.DecodeBytes(field.Value.Span);
    }

    public Guid DecodeGuid(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 2950, typeof(Guid));
        var field = GetRequiredField(row, ordinal);
        return DecodeGuidValue(column.Format, field.Value.Span);
    }

    public Guid? DecodeNullableGuid(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 2950, typeof(Guid?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeGuidValue(column.Format, field.Value.Span);
    }

    public DateOnly DecodeDateOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 1082, typeof(DateOnly));
        var field = GetRequiredField(row, ordinal);
        return DecodeDateOnlyValue(column.Format, field.Value.Span);
    }

    public DateOnly? DecodeNullableDateOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 1082, typeof(DateOnly?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeDateOnlyValue(column.Format, field.Value.Span);
    }

    public TimeOnly DecodeTimeOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 1083, typeof(TimeOnly));
        var field = GetRequiredField(row, ordinal);
        return DecodeTimeOnlyValue(column.Format, field.Value.Span);
    }

    public TimeOnly? DecodeNullableTimeOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 1083, typeof(TimeOnly?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeTimeOnlyValue(column.Format, field.Value.Span);
    }

    public DateTime DecodeDateTime(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 1114, typeof(DateTime));
        var field = GetRequiredField(row, ordinal);
        return DecodeDateTimeValue(column.Format, field.Value.Span);
    }

    public DateTime? DecodeNullableDateTime(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 1114, typeof(DateTime?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeDateTimeValue(column.Format, field.Value.Span);
    }

    public DateTimeOffset DecodeDateTimeOffset(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 1184, typeof(DateTimeOffset));
        var field = GetRequiredField(row, ordinal);
        return DecodeDateTimeOffsetValue(
          column.Format,
          field.Value.Span);
    }

    public DateTimeOffset? DecodeNullableDateTimeOffset(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 1184, typeof(DateTimeOffset?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeDateTimeOffsetValue(
            column.Format,
            field.Value.Span);
    }

    public JsonElement DecodeJsonElement(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureJsonType(column, typeof(JsonElement));
        var field = GetRequiredField(row, ordinal);
        return DecodeJsonValue(column, field.Value);
    }

    public JsonElement? DecodeNullableJsonElement(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureJsonType(column, typeof(JsonElement?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeJsonValue(column, field.Value);
    }

    public TElement[]? DecodeArray<TElement>(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureArrayType(column, typeof(TElement[]));
        var field = GetField(row, ordinal);
        if (field.IsNull)
        {
            return null;
        }

        return column.Format == SqlDataFormat.Binary
                    ? PgBinaryCodec.DecodeArray<TElement>(column.TypeId, field.Value)
                    : PgTextCodec.DecodeArray<TElement>(column.TypeId, field.Value);
    }

    public T Decode<T>(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column,
        bool copyReadOnlyMemory)
    {
        if (typeof(T) == typeof(string))
        {
            var value = DecodeString(row, ordinal, column);
            return Unsafe.As<string?, T>(ref value);
        }

        if (typeof(T) == typeof(object))
        {
            return (T)DecodeObject(row, ordinal, column)!;
        }

        if (typeof(T) == typeof(byte[]))
        {
            var value = DecodeBytes(row, ordinal, column);
            return Unsafe.As<byte[]?, T>(ref value);
        }

        if (typeof(T) == typeof(int))
        {
            var value = DecodeInt32(row, ordinal, column);
            return Unsafe.As<int, T>(ref value);
        }

        if (typeof(T) == typeof(Guid))
        {
            var value = DecodeGuid(row, ordinal, column);
            return Unsafe.As<Guid, T>(ref value);
        }

        if (typeof(T) == typeof(PgPoint))
        {
            var value = DecodePgPoint(row, ordinal, column);
            return Unsafe.As<PgPoint, T>(ref value);
        }

        var kind = TypedDecoder<T>.s_kind;
        switch (kind)
        {
            case TypedDecoderKind.Int32:
                {
                    var value = DecodeInt32(row, ordinal, column);
                    return Unsafe.As<int, T>(ref value);
                }
            case TypedDecoderKind.String:
                {
                    var value = DecodeString(row, ordinal, column);
                    return Unsafe.As<string?, T>(ref value);
                }
            case TypedDecoderKind.Int64:
                {
                    var value = DecodeInt64(row, ordinal, column);
                    return Unsafe.As<long, T>(ref value);
                }
            case TypedDecoderKind.Boolean:
                {
                    var value = DecodeBoolean(row, ordinal, column);
                    return Unsafe.As<bool, T>(ref value);
                }
            case TypedDecoderKind.Int16:
                {
                    var value = DecodeInt16(row, ordinal, column);
                    return Unsafe.As<short, T>(ref value);
                }
            case TypedDecoderKind.Float:
                {
                    var value = DecodeFloat(row, ordinal, column);
                    return Unsafe.As<float, T>(ref value);
                }
            case TypedDecoderKind.Double:
                {
                    var value = DecodeDouble(row, ordinal, column);
                    return Unsafe.As<double, T>(ref value);
                }
            case TypedDecoderKind.Decimal:
                {
                    var value = DecodeDecimal(row, ordinal, column);
                    return Unsafe.As<decimal, T>(ref value);
                }
            case TypedDecoderKind.Guid:
                {
                    var value = DecodeGuid(row, ordinal, column);
                    return Unsafe.As<Guid, T>(ref value);
                }
            case TypedDecoderKind.DateOnly:
                {
                    var value = DecodeDateOnly(row, ordinal, column);
                    return Unsafe.As<DateOnly, T>(ref value);
                }
            case TypedDecoderKind.TimeOnly:
                {
                    var value = DecodeTimeOnly(row, ordinal, column);
                    return Unsafe.As<TimeOnly, T>(ref value);
                }
            case TypedDecoderKind.DateTime:
                {
                    var value = DecodeDateTime(row, ordinal, column);
                    return Unsafe.As<DateTime, T>(ref value);
                }
            case TypedDecoderKind.DateTimeOffset:
                {
                    var value =
                      DecodeDateTimeOffset(row, ordinal, column);
                    return Unsafe.As<DateTimeOffset, T>(ref value);
                }
            case TypedDecoderKind.Bytes:
                {
                    var value = DecodeBytes(row, ordinal, column);
                    return Unsafe.As<byte[]?, T>(ref value);
                }
            case TypedDecoderKind.ReadOnlyMemory:
                {
                    var value =
                      DecodeReadOnlyMemory(row, ordinal, column);
                    if (copyReadOnlyMemory)
                    {
                        value = value.ToArray();
                    }

                    return Unsafe.As<ReadOnlyMemory<byte>, T>(ref value);
                }
            case TypedDecoderKind.JsonElement:
                {
                    var value =
                      DecodeJsonElement(row, ordinal, column);
                    return Unsafe.As<JsonElement, T>(ref value);
                }
            case TypedDecoderKind.Object:
                return (T)DecodeObject(row, ordinal, column)!;
            case TypedDecoderKind.NullableInt32:
                {
                    var value = DecodeNullableInt32(row, ordinal, column);
                    return Unsafe.As<int?, T>(ref value);
                }
            case TypedDecoderKind.NullableInt64:
                {
                    var value = DecodeNullableInt64(row, ordinal, column);
                    return Unsafe.As<long?, T>(ref value);
                }
            case TypedDecoderKind.NullableBoolean:
                {
                    var value =
                      DecodeNullableBoolean(row, ordinal, column);
                    return Unsafe.As<bool?, T>(ref value);
                }
            case TypedDecoderKind.NullableInt16:
                {
                    var value = DecodeNullableInt16(row, ordinal, column);
                    return Unsafe.As<short?, T>(ref value);
                }
            case TypedDecoderKind.NullableFloat:
                {
                    var value = DecodeNullableFloat(row, ordinal, column);
                    return Unsafe.As<float?, T>(ref value);
                }
            case TypedDecoderKind.NullableDouble:
                {
                    var value =
                      DecodeNullableDouble(row, ordinal, column);
                    return Unsafe.As<double?, T>(ref value);
                }
            case TypedDecoderKind.NullableDecimal:
                {
                    var value =
                      DecodeNullableDecimal(row, ordinal, column);
                    return Unsafe.As<decimal?, T>(ref value);
                }
            case TypedDecoderKind.NullableGuid:
                {
                    var value = DecodeNullableGuid(row, ordinal, column);
                    return Unsafe.As<Guid?, T>(ref value);
                }
            case TypedDecoderKind.NullableDateOnly:
                {
                    var value =
                      DecodeNullableDateOnly(row, ordinal, column);
                    return Unsafe.As<DateOnly?, T>(ref value);
                }
            case TypedDecoderKind.NullableTimeOnly:
                {
                    var value =
                      DecodeNullableTimeOnly(row, ordinal, column);
                    return Unsafe.As<TimeOnly?, T>(ref value);
                }
            case TypedDecoderKind.NullableDateTime:
                {
                    var value =
                      DecodeNullableDateTime(row, ordinal, column);
                    return Unsafe.As<DateTime?, T>(ref value);
                }
            case TypedDecoderKind.NullableDateTimeOffset:
                {
                    var value =
                      DecodeNullableDateTimeOffset(row, ordinal, column);
                    return Unsafe.As<DateTimeOffset?, T>(ref value);
                }
            case TypedDecoderKind.NullableReadOnlyMemory:
                {
                    var value =
                      DecodeNullableReadOnlyMemory(row, ordinal, column);
                    if (copyReadOnlyMemory && value.HasValue)
                    {
                        value = value.Value.ToArray();
                    }

                    return Unsafe.As<ReadOnlyMemory<byte>?, T>(ref value);
                }
            case TypedDecoderKind.NullableJsonElement:
                {
                    var value =
                      DecodeNullableJsonElement(row, ordinal, column);
                    return Unsafe.As<JsonElement?, T>(ref value);
                }
            case TypedDecoderKind.PgNumeric:
                {
                    var value = DecodePgNumeric(row, ordinal, column);
                    return Unsafe.As<PgNumeric, T>(ref value);
                }
            case TypedDecoderKind.PgMoney:
                {
                    var value = DecodePgMoney(row, ordinal, column);
                    return Unsafe.As<PgMoney, T>(ref value);
                }
            case TypedDecoderKind.PgInterval:
                {
                    var value =
                      DecodePgInterval(row, ordinal, column);
                    return Unsafe.As<PgInterval, T>(ref value);
                }
            case TypedDecoderKind.PgTimeWithTimeZone:
                {
                    var value =
                      DecodePgTimeWithTimeZone(row, ordinal, column);
                    return Unsafe.As<PgTimeWithTimeZone, T>(ref value);
                }
            case TypedDecoderKind.PgPoint:
                {
                    var value = DecodePgPoint(row, ordinal, column);
                    return Unsafe.As<PgPoint, T>(ref value);
                }
            case TypedDecoderKind.PgLineSegment:
                {
                    var value =
                      DecodePgLineSegment(row, ordinal, column);
                    return Unsafe.As<PgLineSegment, T>(ref value);
                }
            case TypedDecoderKind.PgPath:
                {
                    var value = DecodePgPath(row, ordinal, column);
                    return Unsafe.As<PgPath?, T>(ref value);
                }
            case TypedDecoderKind.PgBox:
                {
                    var value = DecodePgBox(row, ordinal, column);
                    return Unsafe.As<PgBox, T>(ref value);
                }
            case TypedDecoderKind.PgPolygon:
                {
                    var value =
                      DecodePgPolygon(row, ordinal, column);
                    return Unsafe.As<PgPolygon?, T>(ref value);
                }
            case TypedDecoderKind.PgLine:
                {
                    var value = DecodePgLine(row, ordinal, column);
                    return Unsafe.As<PgLine, T>(ref value);
                }
            case TypedDecoderKind.PgCidr:
                {
                    var value = DecodePgCidr(row, ordinal, column);
                    return Unsafe.As<PgCidr, T>(ref value);
                }
            case TypedDecoderKind.PgCircle:
                {
                    var value = DecodePgCircle(row, ordinal, column);
                    return Unsafe.As<PgCircle, T>(ref value);
                }
            case TypedDecoderKind.PgInet:
                {
                    var value = DecodePgInet(row, ordinal, column);
                    return Unsafe.As<PgInet, T>(ref value);
                }
            case TypedDecoderKind.NullablePgNumeric:
                {
                    var value =
                      DecodeNullablePgNumeric(row, ordinal, column);
                    return Unsafe.As<PgNumeric?, T>(ref value);
                }
            case TypedDecoderKind.NullablePgMoney:
                {
                    var value =
                      DecodeNullablePgMoney(row, ordinal, column);
                    return Unsafe.As<PgMoney?, T>(ref value);
                }
            case TypedDecoderKind.NullablePgInterval:
                {
                    var value =
                      DecodeNullablePgInterval(row, ordinal, column);
                    return Unsafe.As<PgInterval?, T>(ref value);
                }
            case TypedDecoderKind.NullablePgTimeWithTimeZone:
                {
                    var value =
                      DecodeNullablePgTimeWithTimeZone(
                        row,
                        ordinal,
                        column);
                    return Unsafe.As<PgTimeWithTimeZone?, T>(ref value);
                }
            case TypedDecoderKind.NullablePgPoint:
                {
                    var value =
                      DecodeNullablePgPoint(row, ordinal, column);
                    return Unsafe.As<PgPoint?, T>(ref value);
                }
            case TypedDecoderKind.NullablePgLineSegment:
                {
                    var value =
                      DecodeNullablePgLineSegment(row, ordinal, column);
                    return Unsafe.As<PgLineSegment?, T>(ref value);
                }
            case TypedDecoderKind.NullablePgBox:
                {
                    var value =
                      DecodeNullablePgBox(row, ordinal, column);
                    return Unsafe.As<PgBox?, T>(ref value);
                }
            case TypedDecoderKind.NullablePgLine:
                {
                    var value =
                      DecodeNullablePgLine(row, ordinal, column);
                    return Unsafe.As<PgLine?, T>(ref value);
                }
            case TypedDecoderKind.NullablePgCidr:
                {
                    var value =
                      DecodeNullablePgCidr(row, ordinal, column);
                    return Unsafe.As<PgCidr?, T>(ref value);
                }
            case TypedDecoderKind.NullablePgCircle:
                {
                    var value =
                      DecodeNullablePgCircle(row, ordinal, column);
                    return Unsafe.As<PgCircle?, T>(ref value);
                }
            case TypedDecoderKind.NullablePgInet:
                {
                    var value =
                      DecodeNullablePgInet(row, ordinal, column);
                    return Unsafe.As<PgInet?, T>(ref value);
                }
            case TypedDecoderKind.Byte:
            case TypedDecoderKind.NullableByte:
            case TypedDecoderKind.SByte:
            case TypedDecoderKind.NullableSByte:
            case TypedDecoderKind.Half:
            case TypedDecoderKind.NullableHalf:
            case TypedDecoderKind.BigInteger:
            case TypedDecoderKind.NullableBigInteger:
            case TypedDecoderKind.Int128:
            case TypedDecoderKind.NullableInt128:
            case TypedDecoderKind.UInt128:
            case TypedDecoderKind.NullableUInt128:
            case TypedDecoderKind.TimeSpan:
            case TypedDecoderKind.NullableTimeSpan:
            case TypedDecoderKind.Char:
            case TypedDecoderKind.NullableChar:
            case TypedDecoderKind.Chars:
            case TypedDecoderKind.IPAddress:
            case TypedDecoderKind.PhysicalAddress:
            case TypedDecoderKind.BitArray:
                return DecodeAlternative<T>(row, ordinal, column, kind);
            default:
                throw CannotRead(column, typeof(T));
        }
    }

    private static T DecodeAlternative<T>(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column,
        TypedDecoderKind kind)
    {
        var requestedType = typeof(T);
        switch (kind)
        {
            case TypedDecoderKind.Byte:
            case TypedDecoderKind.NullableByte:
            case TypedDecoderKind.SByte:
            case TypedDecoderKind.NullableSByte:
                EnsureType(column, 21, requestedType);
                break;
            case TypedDecoderKind.Half:
            case TypedDecoderKind.NullableHalf:
                EnsureType(column, 700, requestedType);
                break;
            case TypedDecoderKind.BigInteger:
            case TypedDecoderKind.NullableBigInteger:
            case TypedDecoderKind.Int128:
            case TypedDecoderKind.NullableInt128:
            case TypedDecoderKind.UInt128:
            case TypedDecoderKind.NullableUInt128:
                EnsureType(column, 1700, requestedType);
                break;
            case TypedDecoderKind.TimeSpan:
            case TypedDecoderKind.NullableTimeSpan:
                EnsureOneOfTypes(column, requestedType, 1083, 1186);
                break;
            case TypedDecoderKind.Char:
            case TypedDecoderKind.NullableChar:
            case TypedDecoderKind.Chars:
                EnsureStringType(column, requestedType);
                break;
            case TypedDecoderKind.IPAddress:
                EnsureType(column, 869, requestedType);
                break;
            case TypedDecoderKind.PhysicalAddress:
                EnsureOneOfTypes(column, requestedType, 774, 829);
                break;
            case TypedDecoderKind.BitArray:
                EnsureOneOfTypes(column, requestedType, 1560, 1562);
                break;
            default:
                throw CannotRead(column, requestedType);
        }

        var field = GetField(row, ordinal);
        if (field.IsNull)
        {
            if (default(T) is null)
            {
                return default!;
            }

            throw new InvalidCastException($"Column {ordinal} contains NULL.");
        }

        var bytes = field.Value.Span;
                switch (kind)
        {
                        case TypedDecoderKind.Byte:
                        case TypedDecoderKind.NullableByte:
                                return CastAlternative<T, byte>(
                                    checked((byte)DecodeInt16Value(column.Format, bytes)));
                        case TypedDecoderKind.SByte:
                        case TypedDecoderKind.NullableSByte:
                                return CastAlternative<T, sbyte>(
                                    checked((sbyte)DecodeInt16Value(column.Format, bytes)));
                        case TypedDecoderKind.Half:
                        case TypedDecoderKind.NullableHalf:
                                return CastAlternative<T, Half>(
                                    checked((Half)DecodeFloatValue(column.Format, bytes)));
                        case TypedDecoderKind.BigInteger:
                        case TypedDecoderKind.NullableBigInteger:
                                return CastAlternative<T, BigInteger>(DecodeBigIntegerValue(column.Format, bytes));
                        case TypedDecoderKind.Int128:
                        case TypedDecoderKind.NullableInt128:
                                return CastAlternative<T, Int128>(
                                    checked((Int128)DecodeBigIntegerValue(column.Format, bytes)));
                        case TypedDecoderKind.UInt128:
                        case TypedDecoderKind.NullableUInt128:
                                return CastAlternative<T, UInt128>(
                                    checked((UInt128)DecodeBigIntegerValue(column.Format, bytes)));
                        case TypedDecoderKind.TimeSpan:
                        case TypedDecoderKind.NullableTimeSpan:
                                TimeSpan duration = column.TypeId == 1083
                                    ? DecodeTimeOnlyValue(column.Format, bytes).ToTimeSpan()
                                    : column.Format == SqlDataFormat.Binary
                                        ? PgBinaryCodec.DecodeTimeSpan(bytes)
                                        : PgTextCodec.DecodeTimeSpan(bytes);
                                return CastAlternative<T, TimeSpan>(duration);
                        case TypedDecoderKind.Char:
                        case TypedDecoderKind.NullableChar:
                                return CastAlternative<T, char>(PgTextCodec.DecodeChar(bytes));
                        case TypedDecoderKind.Chars:
                                return (T)(object)PgTextCodec.DecodeChars(bytes);
                        case TypedDecoderKind.IPAddress:
                                IPAddress address = column.Format == SqlDataFormat.Binary
                                    ? PgBinaryCodec.DecodeIPAddress(bytes)
                                    : PgTextCodec.DecodeIPAddress(bytes);
                                return (T)(object)address;
                        case TypedDecoderKind.PhysicalAddress:
                                PhysicalAddress physicalAddress = column.Format == SqlDataFormat.Binary
                                    ? PgBinaryCodec.DecodePhysicalAddress(bytes)
                                    : PgTextCodec.DecodePhysicalAddress(bytes);
                                return (T)(object)physicalAddress;
                        case TypedDecoderKind.BitArray:
                                BitArray bits = column.Format == SqlDataFormat.Binary
                                    ? PgBinaryCodec.DecodeBitArray(bytes)
                                    : PgTextCodec.DecodeBitArray(bytes);
                                return (T)(object)bits;
                        default:
                                throw CannotRead(column, requestedType);
                }
    }

    private static T CastAlternative<T, TValue>(TValue value)
      where TValue : struct
    {
        if (typeof(T) == typeof(TValue))
        {
            return Unsafe.As<TValue, T>(ref value);
        }

        TValue? nullable = value;
        return Unsafe.As<TValue?, T>(ref nullable);
    }

    private static BigInteger DecodeBigIntegerValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeBigInteger(value)
        : PgTextCodec.DecodeBigInteger(value);

    private static TypedDecoderKind ResolveTypedDecoder<T>()
    {
        var type = typeof(T);
        if (type == typeof(int))
        {
            return TypedDecoderKind.Int32;
        }

        if (type == typeof(string))
        {
            return TypedDecoderKind.String;
        }

        if (type == typeof(long))
        {
            return TypedDecoderKind.Int64;
        }

        if (type == typeof(bool))
        {
            return TypedDecoderKind.Boolean;
        }

        if (type == typeof(short))
        {
            return TypedDecoderKind.Int16;
        }

        if (type == typeof(float))
        {
            return TypedDecoderKind.Float;
        }

        if (type == typeof(double))
        {
            return TypedDecoderKind.Double;
        }

        if (type == typeof(decimal))
        {
            return TypedDecoderKind.Decimal;
        }

        if (type == typeof(Guid))
        {
            return TypedDecoderKind.Guid;
        }

        if (type == typeof(DateOnly))
        {
            return TypedDecoderKind.DateOnly;
        }

        if (type == typeof(TimeOnly))
        {
            return TypedDecoderKind.TimeOnly;
        }

        if (type == typeof(DateTime))
        {
            return TypedDecoderKind.DateTime;
        }

        if (type == typeof(DateTimeOffset))
        {
            return TypedDecoderKind.DateTimeOffset;
        }

        if (type == typeof(byte[]))
        {
            return TypedDecoderKind.Bytes;
        }

        if (type == typeof(ReadOnlyMemory<byte>))
        {
            return TypedDecoderKind.ReadOnlyMemory;
        }

        if (type == typeof(JsonElement))
        {
            return TypedDecoderKind.JsonElement;
        }

        if (type == typeof(object))
        {
            return TypedDecoderKind.Object;
        }

        if (type == typeof(int?))
        {
            return TypedDecoderKind.NullableInt32;
        }

        if (type == typeof(long?))
        {
            return TypedDecoderKind.NullableInt64;
        }

        if (type == typeof(bool?))
        {
            return TypedDecoderKind.NullableBoolean;
        }

        if (type == typeof(short?))
        {
            return TypedDecoderKind.NullableInt16;
        }

        if (type == typeof(float?))
        {
            return TypedDecoderKind.NullableFloat;
        }

        if (type == typeof(double?))
        {
            return TypedDecoderKind.NullableDouble;
        }

        if (type == typeof(decimal?))
        {
            return TypedDecoderKind.NullableDecimal;
        }

        if (type == typeof(Guid?))
        {
            return TypedDecoderKind.NullableGuid;
        }

        if (type == typeof(DateOnly?))
        {
            return TypedDecoderKind.NullableDateOnly;
        }

        if (type == typeof(TimeOnly?))
        {
            return TypedDecoderKind.NullableTimeOnly;
        }

        if (type == typeof(DateTime?))
        {
            return TypedDecoderKind.NullableDateTime;
        }

        if (type == typeof(DateTimeOffset?))
        {
            return TypedDecoderKind.NullableDateTimeOffset;
        }

        if (type == typeof(ReadOnlyMemory<byte>?))
        {
            return TypedDecoderKind.NullableReadOnlyMemory;
        }

        if (type == typeof(JsonElement?))
        {
            return TypedDecoderKind.NullableJsonElement;
        }

        if (type == typeof(PgNumeric))
        {
            return TypedDecoderKind.PgNumeric;
        }

        if (type == typeof(PgMoney))
        {
            return TypedDecoderKind.PgMoney;
        }

        if (type == typeof(PgInterval))
        {
            return TypedDecoderKind.PgInterval;
        }

        if (type == typeof(PgTimeWithTimeZone))
        {
            return TypedDecoderKind.PgTimeWithTimeZone;
        }

        if (type == typeof(PgPoint))
        {
            return TypedDecoderKind.PgPoint;
        }

        if (type == typeof(PgLineSegment))
        {
            return TypedDecoderKind.PgLineSegment;
        }

        if (type == typeof(PgPath))
        {
            return TypedDecoderKind.PgPath;
        }

        if (type == typeof(PgBox))
        {
            return TypedDecoderKind.PgBox;
        }

        if (type == typeof(PgPolygon))
        {
            return TypedDecoderKind.PgPolygon;
        }

        if (type == typeof(PgLine))
        {
            return TypedDecoderKind.PgLine;
        }

        if (type == typeof(PgCidr))
        {
            return TypedDecoderKind.PgCidr;
        }

        if (type == typeof(PgCircle))
        {
            return TypedDecoderKind.PgCircle;
        }

        if (type == typeof(PgInet))
        {
            return TypedDecoderKind.PgInet;
        }

        if (type == typeof(PgNumeric?))
        {
            return TypedDecoderKind.NullablePgNumeric;
        }

        if (type == typeof(PgMoney?))
        {
            return TypedDecoderKind.NullablePgMoney;
        }

        if (type == typeof(PgInterval?))
        {
            return TypedDecoderKind.NullablePgInterval;
        }

        if (type == typeof(PgTimeWithTimeZone?))
        {
            return TypedDecoderKind.NullablePgTimeWithTimeZone;
        }

        if (type == typeof(PgPoint?))
        {
            return TypedDecoderKind.NullablePgPoint;
        }

        if (type == typeof(PgLineSegment?))
        {
            return TypedDecoderKind.NullablePgLineSegment;
        }

        if (type == typeof(PgBox?))
        {
            return TypedDecoderKind.NullablePgBox;
        }

        if (type == typeof(PgLine?))
        {
            return TypedDecoderKind.NullablePgLine;
        }

        if (type == typeof(PgCidr?))
        {
            return TypedDecoderKind.NullablePgCidr;
        }

        if (type == typeof(PgCircle?))
        {
            return TypedDecoderKind.NullablePgCircle;
        }

        if (type == typeof(PgInet?))
        {
            return TypedDecoderKind.NullablePgInet;
        }

        if (type == typeof(byte))
        {
            return TypedDecoderKind.Byte;
        }

        if (type == typeof(byte?))
        {
            return TypedDecoderKind.NullableByte;
        }

        if (type == typeof(sbyte))
        {
            return TypedDecoderKind.SByte;
        }

        if (type == typeof(sbyte?))
        {
            return TypedDecoderKind.NullableSByte;
        }

        if (type == typeof(Half))
        {
            return TypedDecoderKind.Half;
        }

        if (type == typeof(Half?))
        {
            return TypedDecoderKind.NullableHalf;
        }

        if (type == typeof(BigInteger))
        {
            return TypedDecoderKind.BigInteger;
        }

        if (type == typeof(BigInteger?))
        {
            return TypedDecoderKind.NullableBigInteger;
        }

        if (type == typeof(Int128))
        {
            return TypedDecoderKind.Int128;
        }

        if (type == typeof(Int128?))
        {
            return TypedDecoderKind.NullableInt128;
        }

        if (type == typeof(UInt128))
        {
            return TypedDecoderKind.UInt128;
        }

        if (type == typeof(UInt128?))
        {
            return TypedDecoderKind.NullableUInt128;
        }

        if (type == typeof(TimeSpan))
        {
            return TypedDecoderKind.TimeSpan;
        }

        if (type == typeof(TimeSpan?))
        {
            return TypedDecoderKind.NullableTimeSpan;
        }

        if (type == typeof(char))
        {
            return TypedDecoderKind.Char;
        }

        if (type == typeof(char?))
        {
            return TypedDecoderKind.NullableChar;
        }

        if (type == typeof(char[]))
        {
            return TypedDecoderKind.Chars;
        }

        if (type == typeof(IPAddress))
        {
            return TypedDecoderKind.IPAddress;
        }

        if (type == typeof(PhysicalAddress))
        {
            return TypedDecoderKind.PhysicalAddress;
        }

        if (type == typeof(BitArray))
        {
            return TypedDecoderKind.BitArray;
        }

        return TypedDecoderKind.Unsupported;
    }

    internal void DisableCache() => _strings.Disable();

    internal static PgNumeric DecodePgNumeric(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 1700, typeof(PgNumeric));
        var field = GetRequiredField(row, ordinal);
        return DecodePgNumericValue(column.Format, field.Value.Span);
    }

    internal static PgMoney DecodePgMoney(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 790, typeof(PgMoney));
        var field = GetRequiredField(row, ordinal);
        return DecodePgMoneyValue(column.Format, field.Value.Span);
    }

    internal static PgInterval DecodePgInterval(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 1186, typeof(PgInterval));
        var field = GetRequiredField(row, ordinal);
        return DecodePgIntervalValue(column.Format, field.Value.Span);
    }

    internal static PgTimeWithTimeZone DecodePgTimeWithTimeZone(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 1266, typeof(PgTimeWithTimeZone));
        var field = GetRequiredField(row, ordinal);
        return DecodePgTimeWithTimeZoneValue(
          column.Format,
          field.Value.Span);
    }

    internal static PgPoint DecodePgPoint(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 600, typeof(PgPoint));
        var field = GetRequiredField(row, ordinal);
        return DecodePgPointValue(column.Format, field.Value.Span);
    }

    internal static PgLineSegment DecodePgLineSegment(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 601, typeof(PgLineSegment));
        var field = GetRequiredField(row, ordinal);
        return DecodePgLineSegmentValue(
          column.Format,
          field.Value.Span);
    }

    internal static PgPath? DecodePgPath(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 602, typeof(PgPath));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodePgPathValue(column.Format, field.Value.Span);
    }

    internal static PgBox DecodePgBox(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 603, typeof(PgBox));
        var field = GetRequiredField(row, ordinal);
        return DecodePgBoxValue(column.Format, field.Value.Span);
    }

    internal static PgPolygon? DecodePgPolygon(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 604, typeof(PgPolygon));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodePgPolygonValue(column.Format, field.Value.Span);
    }

    internal static PgLine DecodePgLine(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 628, typeof(PgLine));
        var field = GetRequiredField(row, ordinal);
        return DecodePgLineValue(column.Format, field.Value.Span);
    }

    internal static PgCidr DecodePgCidr(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 650, typeof(PgCidr));
        var field = GetRequiredField(row, ordinal);
        return DecodePgCidrValue(column.Format, field.Value.Span);
    }

    internal static PgCircle DecodePgCircle(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 718, typeof(PgCircle));
        var field = GetRequiredField(row, ordinal);
        return DecodePgCircleValue(column.Format, field.Value.Span);
    }

    internal static PgInet DecodePgInet(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 869, typeof(PgInet));
        var field = GetRequiredField(row, ordinal);
        return DecodePgInetValue(column.Format, field.Value.Span);
    }

    private static PgNumeric? DecodeNullablePgNumeric(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 1700, typeof(PgNumeric?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodePgNumericValue(column.Format, field.Value.Span);
    }

    private static PgMoney? DecodeNullablePgMoney(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 790, typeof(PgMoney?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodePgMoneyValue(column.Format, field.Value.Span);
    }

    private static PgInterval? DecodeNullablePgInterval(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 1186, typeof(PgInterval?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodePgIntervalValue(column.Format, field.Value.Span);
    }

    private static PgTimeWithTimeZone?
      DecodeNullablePgTimeWithTimeZone(
          ReadOnlyMemory<byte> row,
          int ordinal,
          SqlColumn column)
    {
        EnsureType(column, 1266, typeof(PgTimeWithTimeZone?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodePgTimeWithTimeZoneValue(
            column.Format,
            field.Value.Span);
    }

    private static PgPoint? DecodeNullablePgPoint(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 600, typeof(PgPoint?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodePgPointValue(column.Format, field.Value.Span);
    }

    private static PgLineSegment? DecodeNullablePgLineSegment(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 601, typeof(PgLineSegment?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodePgLineSegmentValue(
            column.Format,
            field.Value.Span);
    }

    private static PgBox? DecodeNullablePgBox(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 603, typeof(PgBox?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodePgBoxValue(column.Format, field.Value.Span);
    }

    private static PgLine? DecodeNullablePgLine(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 628, typeof(PgLine?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodePgLineValue(column.Format, field.Value.Span);
    }

    private static PgCidr? DecodeNullablePgCidr(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 650, typeof(PgCidr?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodePgCidrValue(column.Format, field.Value.Span);
    }

    private static PgCircle? DecodeNullablePgCircle(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 718, typeof(PgCircle?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodePgCircleValue(column.Format, field.Value.Span);
    }

    private static PgInet? DecodeNullablePgInet(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(column, 869, typeof(PgInet?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodePgInetValue(column.Format, field.Value.Span);
    }

    private static Field GetRequiredField(
        ReadOnlyMemory<byte> row,
        int ordinal)
    {
        var field = GetField(row, ordinal);
        if (field.IsNull)
        {
            throw new InvalidCastException(
              $"Column {ordinal} contains NULL.");
        }

        return field;
    }

    private static Field GetField(
        ReadOnlyMemory<byte> row,
        int ordinal)
    {
        var span = row.Span;
        Ensure(span, 0, sizeof(short));
        int count = BinaryPrimitives.ReadUInt16BigEndian(span);
        if ((uint)ordinal >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        var position = sizeof(short);
        for (var i = 0; i < count; i++)
        {
            Ensure(span, position, sizeof(int));
            var length =
              BinaryPrimitives.ReadInt32BigEndian(span[position..]);
            position += sizeof(int);
            if (length < 0)
            {
                if (i == ordinal)
                {
                    return new Field(default, IsNull: true);
                }

                continue;
            }

            Ensure(span, position, length);
            if (i == ordinal)
            {
                return new Field(
                  row.Slice(position, length),
                  IsNull: false);
            }

            position += length;
        }

        throw new InvalidDataException(
          "PostgreSQL row ended before the requested field.");
    }

    private static bool DecodeBooleanValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeBoolean(value)
        : PgTextCodec.DecodeBoolean(value);

    private static short DecodeInt16Value(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeInt16(value)
        : PgTextCodec.DecodeInt16(value);

    private static int DecodeInt32Value(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeInt32(value)
        : PgTextCodec.DecodeInt32(value);

    private static long DecodeInt64Value(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeInt64(value)
        : PgTextCodec.DecodeInt64(value);

    private static float DecodeFloatValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeFloat(value)
        : PgTextCodec.DecodeFloat(value);

    private static double DecodeDoubleValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeDouble(value)
        : PgTextCodec.DecodeDouble(value);

    private static decimal DecodeDecimalValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeDecimal(value)
        : PgTextCodec.DecodeDecimal(value);

    private static Guid DecodeGuidValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeGuid(value)
        : PgTextCodec.DecodeGuid(value);

    private static DateOnly DecodeDateOnlyValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeDateOnly(value)
        : PgTextCodec.DecodeDateOnly(value);

    private static TimeOnly DecodeTimeOnlyValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeTimeOnly(value)
        : PgTextCodec.DecodeTimeOnly(value);

    private static DateTime DecodeDateTimeValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeDateTime(value)
        : PgTextCodec.DecodeDateTime(value);

    private static DateTimeOffset DecodeDateTimeOffsetValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeDateTimeOffset(value)
        : PgTextCodec.DecodeDateTimeOffset(value);

    private static JsonElement DecodeJsonValue(
        SqlColumn column,
        ReadOnlyMemory<byte> value) =>
      column.Format == SqlDataFormat.Binary
        ? column.TypeId == 3802
          ? PgBinaryCodec.DecodeJsonb(value)
          : PgBinaryCodec.DecodeJson(value)
        : PgTextCodec.DecodeJson(value);

    private static PgNumeric DecodePgNumericValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeNumeric(value)
        : PgTextCodec.DecodeNumeric(value);

    private static PgMoney DecodePgMoneyValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeMoney(value)
        : PgTextCodec.DecodeMoney(value);

    private static PgInterval DecodePgIntervalValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeInterval(value)
        : PgTextCodec.DecodeInterval(value);

    private static PgTimeWithTimeZone
      DecodePgTimeWithTimeZoneValue(
          SqlDataFormat format,
          ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeTimeWithTimeZone(value)
        : PgTextCodec.DecodeTimeWithTimeZone(value);

    private static PgPoint DecodePgPointValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodePoint(value)
        : PgTextCodec.DecodePoint(value);

    private static PgLineSegment DecodePgLineSegmentValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeLineSegment(value)
        : PgTextCodec.DecodeLineSegment(value);

    private static PgPath DecodePgPathValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodePath(value)
        : PgTextCodec.DecodePath(value);

    private static PgBox DecodePgBoxValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeBox(value)
        : PgTextCodec.DecodeBox(value);

    private static PgPolygon DecodePgPolygonValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodePolygon(value)
        : PgTextCodec.DecodePolygon(value);

    private static PgLine DecodePgLineValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeLine(value)
        : PgTextCodec.DecodeLine(value);

    private static PgCidr DecodePgCidrValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeCidr(value)
        : PgTextCodec.DecodeCidr(value);

    private static PgCircle DecodePgCircleValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeCircle(value)
        : PgTextCodec.DecodeCircle(value);

    private static PgInet DecodePgInetValue(
        SqlDataFormat format,
        ReadOnlySpan<byte> value) =>
      format == SqlDataFormat.Binary
        ? PgBinaryCodec.DecodeInet(value)
        : PgTextCodec.DecodeInet(value);

    private static bool IsStringType(uint typeId) =>
      typeId is 18 or 19 or 25 or 705 or 1042 or 1043 or 3614 or 3615 ||
      typeId is not (
        16 or 17 or 20 or 21 or 23 or 26 or 114 or 142 or 600 or 601 or
        602 or 603 or 604 or 628 or 650 or 700 or 701 or 718 or 774 or
        790 or 829 or 869 or 1082 or 1083 or 1114 or 1184 or 1186 or
        1266 or 1560 or 1562 or 1700 or 2278 or 2950 or 3802 or
        1000 or 1001 or 1002 or 1003 or 1005 or 1007 or 1009 or 1015 or
        1016 or 1017 or 1018 or 1019 or 1020 or 1021 or 1022 or 1027 or
        1040 or 1041 or 1115 or 1182 or 1183 or 1185 or 1187 or 1231 or 1270 or
        199 or 629 or 651 or 719 or 775 or 791 or 1561 or 1563 or 2951 or 3807);

    private static bool IsArrayType(uint typeId) =>
      typeId is
        1000 or 1001 or 1002 or 1003 or 1005 or 1007 or 1009 or 1015 or
        1016 or 1017 or 1018 or 1019 or 1020 or 1021 or 1022 or 1027 or
        1040 or 1041 or 1115 or 1182 or 1183 or 1185 or 1187 or 1231 or 1270 or
        199 or 629 or 651 or 719 or 775 or 791 or 1561 or 1563 or 2951 or 3807;

    private static bool CanDecodeAsString(SqlColumn column) =>
      IsKnownFormat(column.Format) &&
      (column.Format == SqlDataFormat.Text &&
       IsStringType(column.TypeId) ||
       column.TypeId is 18 or 19 or 25 or 1042 or 1043);

    private static void EnsureType(
        SqlColumn column,
        uint expectedTypeId,
        Type requestedType)
    {
        if (column.TypeId != expectedTypeId ||
            !IsKnownFormat(column.Format))
        {
            throw CannotRead(column, requestedType);
        }
    }

    private static void EnsureOneOfTypes(
        SqlColumn column,
        Type requestedType,
        uint firstTypeId,
        uint secondTypeId)
    {
        if (column.TypeId != firstTypeId &&
            column.TypeId != secondTypeId ||
            !IsKnownFormat(column.Format))
        {
            throw CannotRead(column, requestedType);
        }
    }

    private static void EnsureStringType(
        SqlColumn column,
        Type requestedType)
    {
        if (!CanDecodeAsString(column))
        {
            throw CannotRead(column, requestedType);
        }
    }

    private static void EnsureJsonType(
        SqlColumn column,
        Type requestedType)
    {
        if (column.TypeId is not (114 or 3802) ||
            !IsKnownFormat(column.Format))
        {
            throw CannotRead(column, requestedType);
        }
    }

    private static void EnsureArrayType(
        SqlColumn column,
        Type requestedType)
    {
        if (!IsArrayType(column.TypeId) ||
            !IsKnownFormat(column.Format))
        {
            throw CannotRead(column, requestedType);
        }
    }

    private static void EnsureFormat(
        SqlColumn column,
        Type requestedType)
    {
        if (!IsKnownFormat(column.Format))
        {
            throw CannotRead(column, requestedType);
        }
    }

    private static bool IsKnownFormat(SqlDataFormat format) =>
      format is SqlDataFormat.Text or SqlDataFormat.Binary;

    private static InvalidCastException CannotRead(
        SqlColumn column,
        Type requestedType) =>
      new(
        $"PostgreSQL type OID {column.TypeId} in {column.Format} format " +
        $"cannot be read as {requestedType.FullName}.");

    private static void Ensure(
        ReadOnlySpan<byte> value,
        int position,
        int length)
    {
        if (length < 0 ||
            position < 0 ||
            position > value.Length - length)
        {
            throw new InvalidDataException(
              "PostgreSQL row is truncated.");
        }
    }

    private static class TypedDecoder<T>
    {
        internal static readonly TypedDecoderKind s_kind =
          ResolveTypedDecoder<T>();
    }

    private enum TypedDecoderKind : byte
    {
        Unsupported,
        Int32,
        String,
        Int64,
        Boolean,
        Int16,
        Float,
        Double,
        Decimal,
        Guid,
        DateOnly,
        TimeOnly,
        DateTime,
        DateTimeOffset,
        Bytes,
        ReadOnlyMemory,
        JsonElement,
        Object,
        NullableInt32,
        NullableInt64,
        NullableBoolean,
        NullableInt16,
        NullableFloat,
        NullableDouble,
        NullableDecimal,
        NullableGuid,
        NullableDateOnly,
        NullableTimeOnly,
        NullableDateTime,
        NullableDateTimeOffset,
        NullableReadOnlyMemory,
        NullableJsonElement,
        PgNumeric,
        PgMoney,
        PgInterval,
        PgTimeWithTimeZone,
        PgPoint,
        PgLineSegment,
        PgPath,
        PgBox,
        PgPolygon,
        PgLine,
        PgCidr,
        PgCircle,
        PgInet,
        NullablePgNumeric,
        NullablePgMoney,
        NullablePgInterval,
        NullablePgTimeWithTimeZone,
        NullablePgPoint,
        NullablePgLineSegment,
        NullablePgBox,
        NullablePgLine,
        NullablePgCidr,
        NullablePgCircle,
        NullablePgInet,
        Byte,
        NullableByte,
        SByte,
        NullableSByte,
        Half,
        NullableHalf,
        BigInteger,
        NullableBigInteger,
        Int128,
        NullableInt128,
        UInt128,
        NullableUInt128,
        TimeSpan,
        NullableTimeSpan,
        Char,
        NullableChar,
        Chars,
        IPAddress,
        PhysicalAddress,
        BitArray,
    }

    private readonly record struct Field(
        ReadOnlyMemory<byte> Value,
        bool IsNull);
}

using System.Buffers.Binary;
using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MsSqlClient.Internal;

internal sealed class MsSqlRowDecoder : ISqlRowDecoder
{
    private readonly MsSqlStringCache _strings;

    internal MsSqlRowDecoder(
        int stringCacheCapacity = 1024,
        int stringCacheMaximumByteLength = 128)
    {
        _strings = new MsSqlStringCache(
          stringCacheCapacity,
          stringCacheMaximumByteLength);
    }

    public int GetFieldCount(ReadOnlyMemory<byte> row) =>
      GetFieldCount(row.Span);

    internal static int GetFieldCount(ReadOnlySpan<byte> row)
    {
        Ensure(row, 0, sizeof(ushort));
        return BinaryPrimitives.ReadUInt16LittleEndian(row);
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

        var value = field.Value.Span;
        byte type = checked((byte)column.TypeId);
        byte scale = (byte)column.TypeModifier;
        return type switch
        {
            TdsDataType.Int1 => MsSqlBoxedScalarCache.Box(ReadByte(value)),
            TdsDataType.Bit or TdsDataType.BitN =>
              MsSqlBoxedScalarCache.Box(ReadByte(value) != 0),
            TdsDataType.Int2 =>
              MsSqlBoxedScalarCache.Box(ReadInt16(value)),
            TdsDataType.Int4 =>
              MsSqlBoxedScalarCache.Box(ReadInt32(value)),
            TdsDataType.Int8 =>
              MsSqlBoxedScalarCache.Box(ReadInt64(value)),
            TdsDataType.IntN => DecodeIntN(value),
            TdsDataType.Float4 => ReadSingle(value),
            TdsDataType.Float8 => ReadDouble(value),
            TdsDataType.FloatN => DecodeFloatN(value),
            TdsDataType.Decimal or
            TdsDataType.Numeric or
            TdsDataType.DecimalN or
            TdsDataType.NumericN => DecodeDecimalValue(value, type, scale),
            TdsDataType.Money or
            TdsDataType.MoneyN when value.Length == 8 =>
              DecodeMoney(value),
            TdsDataType.Money4 or TdsDataType.MoneyN =>
              ReadInt32(value) / 10000m,
            TdsDataType.Guid => new Guid(value),
            TdsDataType.Date => DecodeDate(value),
            TdsDataType.Time => DecodeTime(value, scale),
            TdsDataType.DateTime2 => DecodeDateTime2(value, scale),
            TdsDataType.DateTimeOffset => DecodeDateTimeOffsetValue(value, scale),
            TdsDataType.DateTime => DecodeLegacyDateTime(value),
            TdsDataType.DateTime4 => DecodeSmallDateTime(value),
            TdsDataType.DateTimeN when value.Length == 8 => DecodeLegacyDateTime(value),
            TdsDataType.DateTimeN => DecodeSmallDateTime(value),
            _ when IsStringType(type) => DecodeStringValue(value, column),
            _ when IsBinaryType(type) => value.ToArray(),
            _ => throw new NotSupportedException(
              $"Cannot decode SQL Server TDS data type 0x{type:X2}."),
        };
    }

    public bool DecodeBoolean(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(
          column,
          type is TdsDataType.Bit or TdsDataType.BitN,
          typeof(bool));
        var value = GetRequiredField(row, ordinal).Span;
        EnsureExact(value, 1);
        return value[0] != 0;
    }

    public bool? DecodeNullableBoolean(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(
          column,
          type is TdsDataType.Bit or TdsDataType.BitN,
          typeof(bool?));
        var field = GetField(row, ordinal);
        if (field.IsNull)
        {
            return null;
        }

        EnsureExact(field.Value.Span, 1);
        return field.Value.Span[0] != 0;
    }

    public short DecodeInt16(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, type == TdsDataType.Int2 ||
          type == TdsDataType.IntN && column.TypeSize == 2, typeof(short));
        return ReadInt16(GetRequiredField(row, ordinal).Span);
    }

    public short? DecodeNullableInt16(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, type == TdsDataType.Int2 ||
          type == TdsDataType.IntN && column.TypeSize == 2, typeof(short?));
        var field = GetField(row, ordinal);
        return field.IsNull ? null : ReadInt16(field.Value.Span);
    }

    public int DecodeInt32(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, type == TdsDataType.Int4 ||
          type == TdsDataType.IntN && column.TypeSize == 4, typeof(int));
        return ReadInt32(GetRequiredField(row, ordinal).Span);
    }

    public int? DecodeNullableInt32(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, type == TdsDataType.Int4 ||
          type == TdsDataType.IntN && column.TypeSize == 4, typeof(int?));
        var field = GetField(row, ordinal);
        return field.IsNull ? null : ReadInt32(field.Value.Span);
    }

    public long DecodeInt64(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, type == TdsDataType.Int8 ||
          type == TdsDataType.IntN && column.TypeSize == 8, typeof(long));
        return ReadInt64(GetRequiredField(row, ordinal).Span);
    }

    public long? DecodeNullableInt64(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, type == TdsDataType.Int8 ||
          type == TdsDataType.IntN && column.TypeSize == 8, typeof(long?));
        var field = GetField(row, ordinal);
        return field.IsNull ? null : ReadInt64(field.Value.Span);
    }

    public float DecodeFloat(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, type == TdsDataType.Float4 ||
          type == TdsDataType.FloatN && column.TypeSize == 4, typeof(float));
        return ReadSingle(GetRequiredField(row, ordinal).Span);
    }

    public float? DecodeNullableFloat(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, type == TdsDataType.Float4 ||
          type == TdsDataType.FloatN && column.TypeSize == 4, typeof(float?));
        var field = GetField(row, ordinal);
        return field.IsNull ? null : ReadSingle(field.Value.Span);
    }

    public double DecodeDouble(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, type == TdsDataType.Float8 ||
          type == TdsDataType.FloatN && column.TypeSize == 8, typeof(double));
        return ReadDouble(GetRequiredField(row, ordinal).Span);
    }

    public double? DecodeNullableDouble(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, type == TdsDataType.Float8 ||
          type == TdsDataType.FloatN && column.TypeSize == 8, typeof(double?));
        var field = GetField(row, ordinal);
        return field.IsNull ? null : ReadDouble(field.Value.Span);
    }

    public decimal DecodeDecimal(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureDecimalType(column, typeof(decimal));
        return DecodeDecimalValue(
          GetRequiredField(row, ordinal).Span,
          checked((byte)column.TypeId),
          (byte)column.TypeModifier);
    }

    public decimal? DecodeNullableDecimal(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureDecimalType(column, typeof(decimal?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeDecimalValue(
            field.Value.Span,
            checked((byte)column.TypeId),
            (byte)column.TypeModifier);
    }

    public string? DecodeString(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, IsStringType(type), typeof(string));
        var field = GetField(row, ordinal);
        return field.IsNull ? null : DecodeStringValue(field.Value.Span, column);
    }

    public byte[]? DecodeBytes(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, IsBinaryType(type), typeof(byte[]));
        var field = GetField(row, ordinal);
        return field.IsNull ? null : field.Value.ToArray();
    }

    public ReadOnlyMemory<byte> DecodeReadOnlyMemory(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, IsBinaryType(type), typeof(ReadOnlyMemory<byte>));
        return GetRequiredField(row, ordinal);
    }

    public ReadOnlyMemory<byte>? DecodeNullableReadOnlyMemory(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, IsBinaryType(type), typeof(ReadOnlyMemory<byte>?));
        var field = GetField(row, ordinal);
        return field.IsNull ? null : field.Value;
    }

    public Guid DecodeGuid(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(
          column,
          column.TypeId == TdsDataType.Guid,
          typeof(Guid));
        return new Guid(GetRequiredField(row, ordinal).Span);
    }

    public Guid? DecodeNullableGuid(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(
          column,
          column.TypeId == TdsDataType.Guid,
          typeof(Guid?));
        var field = GetField(row, ordinal);
        return field.IsNull ? null : new Guid(field.Value.Span);
    }

    public DateOnly DecodeDateOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(
          column,
          column.TypeId == TdsDataType.Date,
          typeof(DateOnly));
        return DecodeDate(GetRequiredField(row, ordinal).Span);
    }

    public DateOnly? DecodeNullableDateOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(
          column,
          column.TypeId == TdsDataType.Date,
          typeof(DateOnly?));
        var field = GetField(row, ordinal);
        return field.IsNull ? null : DecodeDate(field.Value.Span);
    }

    public TimeOnly DecodeTimeOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(
          column,
          column.TypeId == TdsDataType.Time,
          typeof(TimeOnly));
        return DecodeTime(
          GetRequiredField(row, ordinal).Span,
          (byte)column.TypeModifier);
    }

    public TimeOnly? DecodeNullableTimeOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(
          column,
          column.TypeId == TdsDataType.Time,
          typeof(TimeOnly?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeTime(field.Value.Span, (byte)column.TypeModifier);
    }

    public DateTime DecodeDateTime(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureDateTimeType(column, typeof(DateTime));
        return DecodeDateTimeValue(
          GetRequiredField(row, ordinal).Span,
          checked((byte)column.TypeId),
          (byte)column.TypeModifier);
    }

    public DateTime? DecodeNullableDateTime(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureDateTimeType(column, typeof(DateTime?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeDateTimeValue(
            field.Value.Span,
            checked((byte)column.TypeId),
            (byte)column.TypeModifier);
    }

    public DateTimeOffset DecodeDateTimeOffset(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(
          column,
          column.TypeId == TdsDataType.DateTimeOffset,
          typeof(DateTimeOffset));
        return DecodeDateTimeOffsetValue(
          GetRequiredField(row, ordinal).Span,
          (byte)column.TypeModifier);
    }

    public DateTimeOffset? DecodeNullableDateTimeOffset(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(
          column,
          column.TypeId == TdsDataType.DateTimeOffset,
          typeof(DateTimeOffset?));
        var field = GetField(row, ordinal);
        return field.IsNull
          ? null
          : DecodeDateTimeOffsetValue(
            field.Value.Span,
            (byte)column.TypeModifier);
    }

    public JsonElement DecodeJsonElement(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(
          column,
          column.TypeId == TdsDataType.Json,
          typeof(JsonElement));
        return DecodeJson(GetRequiredField(row, ordinal), column);
    }

    public JsonElement? DecodeNullableJsonElement(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureType(
          column,
          column.TypeId == TdsDataType.Json,
          typeof(JsonElement?));
        var field = GetField(row, ordinal);
        return field.IsNull ? null : DecodeJson(field.Value, column);
    }

    public TElement[]? DecodeArray<TElement>(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        EnsureFormat(column, typeof(TElement[]));
        _ = GetField(row, ordinal);
        throw CreateInvalidCast(
          checked((byte)column.TypeId),
          typeof(TElement[]));
    }

    public T Decode<T>(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column,
        bool copyReadOnlyMemory)
    {
        var kind = TypedDecoder<T>.s_kind;
        switch (kind)
        {
            case TypedDecoderKind.Boolean:
                return Cast<bool, T>(DecodeBoolean(row, ordinal, column));
            case TypedDecoderKind.NullableBoolean:
                return Cast<bool?, T>(DecodeNullableBoolean(row, ordinal, column));
            case TypedDecoderKind.Int16:
                return Cast<short, T>(DecodeInt16(row, ordinal, column));
            case TypedDecoderKind.NullableInt16:
                return Cast<short?, T>(DecodeNullableInt16(row, ordinal, column));
            case TypedDecoderKind.Int32:
                return Cast<int, T>(DecodeInt32(row, ordinal, column));
            case TypedDecoderKind.NullableInt32:
                return Cast<int?, T>(DecodeNullableInt32(row, ordinal, column));
            case TypedDecoderKind.Int64:
                return Cast<long, T>(DecodeInt64(row, ordinal, column));
            case TypedDecoderKind.NullableInt64:
                return Cast<long?, T>(DecodeNullableInt64(row, ordinal, column));
            case TypedDecoderKind.Float:
                return Cast<float, T>(DecodeFloat(row, ordinal, column));
            case TypedDecoderKind.NullableFloat:
                return Cast<float?, T>(DecodeNullableFloat(row, ordinal, column));
            case TypedDecoderKind.Double:
                return Cast<double, T>(DecodeDouble(row, ordinal, column));
            case TypedDecoderKind.NullableDouble:
                return Cast<double?, T>(DecodeNullableDouble(row, ordinal, column));
            case TypedDecoderKind.Decimal:
                return Cast<decimal, T>(DecodeDecimal(row, ordinal, column));
            case TypedDecoderKind.NullableDecimal:
                return Cast<decimal?, T>(DecodeNullableDecimal(row, ordinal, column));
            case TypedDecoderKind.String:
                return Cast<string?, T>(DecodeString(row, ordinal, column));
            case TypedDecoderKind.Bytes:
                return Cast<byte[]?, T>(DecodeBytes(row, ordinal, column));
            case TypedDecoderKind.ReadOnlyMemory:
                {
                    var value =
                      DecodeReadOnlyMemory(row, ordinal, column);
                    if (copyReadOnlyMemory)
                    {
                        value = value.ToArray();
                    }

                    return Cast<ReadOnlyMemory<byte>, T>(value);
                }
            case TypedDecoderKind.NullableReadOnlyMemory:
                {
                    var value =
                      DecodeNullableReadOnlyMemory(row, ordinal, column);
                    if (copyReadOnlyMemory && value.HasValue)
                    {
                        value = value.Value.ToArray();
                    }

                    return Cast<ReadOnlyMemory<byte>?, T>(value);
                }
            case TypedDecoderKind.Guid:
                return Cast<Guid, T>(DecodeGuid(row, ordinal, column));
            case TypedDecoderKind.NullableGuid:
                return Cast<Guid?, T>(DecodeNullableGuid(row, ordinal, column));
            case TypedDecoderKind.DateOnly:
                return Cast<DateOnly, T>(DecodeDateOnly(row, ordinal, column));
            case TypedDecoderKind.NullableDateOnly:
                return Cast<DateOnly?, T>(DecodeNullableDateOnly(row, ordinal, column));
            case TypedDecoderKind.TimeOnly:
                return Cast<TimeOnly, T>(DecodeTimeOnly(row, ordinal, column));
            case TypedDecoderKind.NullableTimeOnly:
                return Cast<TimeOnly?, T>(DecodeNullableTimeOnly(row, ordinal, column));
            case TypedDecoderKind.DateTime:
                return Cast<DateTime, T>(DecodeDateTime(row, ordinal, column));
            case TypedDecoderKind.NullableDateTime:
                return Cast<DateTime?, T>(DecodeNullableDateTime(row, ordinal, column));
            case TypedDecoderKind.DateTimeOffset:
                return Cast<DateTimeOffset, T>(
                  DecodeDateTimeOffset(row, ordinal, column));
            case TypedDecoderKind.NullableDateTimeOffset:
                return Cast<DateTimeOffset?, T>(
                  DecodeNullableDateTimeOffset(row, ordinal, column));
            case TypedDecoderKind.JsonElement:
                return Cast<JsonElement, T>(
                  DecodeJsonElement(row, ordinal, column));
            case TypedDecoderKind.NullableJsonElement:
                return Cast<JsonElement?, T>(
                  DecodeNullableJsonElement(row, ordinal, column));
            case TypedDecoderKind.Object:
                return (T)DecodeObject(row, ordinal, column)!;
            case TypedDecoderKind.Byte:
                return Cast<byte, T>(DecodeByte(row, ordinal, column));
            case TypedDecoderKind.NullableByte:
                return Cast<byte?, T>(DecodeNullableByte(row, ordinal, column));
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
                throw CreateInvalidCast(
                  checked((byte)column.TypeId),
                  typeof(T));
        }
    }

    private T DecodeAlternative<T>(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column,
        TypedDecoderKind kind)
    {
        var requestedType = typeof(T);
        byte type = checked((byte)column.TypeId);
        switch (kind)
        {
            case TypedDecoderKind.SByte:
            case TypedDecoderKind.NullableSByte:
                EnsureType(
                  column,
                  type is TdsDataType.Int1 or TdsDataType.Int2 ||
                  type == TdsDataType.IntN && column.TypeSize is 1 or 2,
                  requestedType);
                break;
            case TypedDecoderKind.Half:
            case TypedDecoderKind.NullableHalf:
                EnsureType(
                  column,
                  type == TdsDataType.Float4 ||
                  type == TdsDataType.FloatN && column.TypeSize == 4,
                  requestedType);
                break;
            case TypedDecoderKind.BigInteger:
            case TypedDecoderKind.NullableBigInteger:
            case TypedDecoderKind.Int128:
            case TypedDecoderKind.NullableInt128:
            case TypedDecoderKind.UInt128:
            case TypedDecoderKind.NullableUInt128:
                EnsureType(
                  column,
                  type is (TdsDataType.Decimal or TdsDataType.Numeric or
                    TdsDataType.DecimalN or TdsDataType.NumericN) &&
                    (byte)column.TypeModifier == 0,
                  requestedType);
                break;
            case TypedDecoderKind.TimeSpan:
            case TypedDecoderKind.NullableTimeSpan:
                EnsureType(column, type == TdsDataType.Time, requestedType);
                break;
            case TypedDecoderKind.Char:
            case TypedDecoderKind.NullableChar:
            case TypedDecoderKind.Chars:
            case TypedDecoderKind.IPAddress:
            case TypedDecoderKind.BitArray:
                EnsureType(column, IsStringType(type), requestedType);
                break;
            case TypedDecoderKind.PhysicalAddress:
                EnsureType(column, IsBinaryType(type), requestedType);
                break;
            default:
                throw CreateInvalidCast(type, requestedType);
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
            case TypedDecoderKind.SByte:
            case TypedDecoderKind.NullableSByte:
                sbyte signedByte = bytes.Length switch
                {
                    1 => checked((sbyte)bytes[0]),
                    2 => checked((sbyte)ReadInt16(bytes)),
                    _ => throw CreateInvalidCast(type, requestedType),
                };
                return CastAlternative<T, sbyte>(signedByte);
            case TypedDecoderKind.Half:
            case TypedDecoderKind.NullableHalf:
                return CastAlternative<T, Half>(checked((Half)ReadSingle(bytes)));
            case TypedDecoderKind.BigInteger:
            case TypedDecoderKind.NullableBigInteger:
                return CastAlternative<T, BigInteger>(DecodeBigInteger(bytes));
            case TypedDecoderKind.Int128:
            case TypedDecoderKind.NullableInt128:
                return CastAlternative<T, Int128>(checked((Int128)DecodeBigInteger(bytes)));
            case TypedDecoderKind.UInt128:
            case TypedDecoderKind.NullableUInt128:
                return CastAlternative<T, UInt128>(checked((UInt128)DecodeBigInteger(bytes)));
            case TypedDecoderKind.TimeSpan:
            case TypedDecoderKind.NullableTimeSpan:
                return CastAlternative<T, TimeSpan>(
                  DecodeTime(bytes, (byte)column.TypeModifier).ToTimeSpan());
            case TypedDecoderKind.Char:
            case TypedDecoderKind.NullableChar:
                string characterText = DecodeStringValue(bytes, column);
                char character = characterText.Length == 1
                  ? characterText[0]
                  : throw CreateInvalidCast(type, requestedType);
                return CastAlternative<T, char>(character);
            case TypedDecoderKind.Chars:
                return (T)(object)DecodeStringValue(bytes, column).ToCharArray();
            case TypedDecoderKind.IPAddress:
                return (T)(object)IPAddress.Parse(DecodeStringValue(bytes, column));
            case TypedDecoderKind.PhysicalAddress:
                return (T)(object)new PhysicalAddress(bytes.ToArray());
            case TypedDecoderKind.BitArray:
                string bitText = DecodeStringValue(bytes, column);
                var bits = new BitArray(bitText.Length);
                for (var i = 0; i < bitText.Length; i++)
                {
                    bits[i] = bitText[i] switch
                    {
                        '0' => false,
                        '1' => true,
                        _ => throw CreateInvalidCast(type, requestedType),
                    };
                }

                return (T)(object)bits;
            default:
                throw CreateInvalidCast(type, requestedType);
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

    private static BigInteger DecodeBigInteger(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            throw new InvalidDataException("Invalid SQL Server numeric value.");
        }

        BigInteger result = new(value[1..], isUnsigned: true, isBigEndian: false);
        return value[0] == 0 ? -result : result;
    }

    private static byte DecodeByte(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, type == TdsDataType.Int1 ||
          type == TdsDataType.IntN && column.TypeSize == 1, typeof(byte));
        var value = GetRequiredField(row, ordinal).Span;
        EnsureExact(value, 1);
        return value[0];
    }

    private static byte? DecodeNullableByte(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(column, type == TdsDataType.Int1 ||
          type == TdsDataType.IntN && column.TypeSize == 1, typeof(byte?));
        var field = GetField(row, ordinal);
        if (field.IsNull)
        {
            return null;
        }

        EnsureExact(field.Value.Span, 1);
        return field.Value.Span[0];
    }

    internal void DisableCache() => _strings.Disable();

    private static decimal DecodeDecimalValue(
        ReadOnlySpan<byte> value,
        byte type,
        byte scale) =>
      type switch
      {
          TdsDataType.Decimal or
        TdsDataType.Numeric or
        TdsDataType.DecimalN or
        TdsDataType.NumericN => DecodeDecimal(value, scale),
          TdsDataType.Money or TdsDataType.MoneyN when value.Length == 8 =>
          DecodeMoney(value),
          TdsDataType.Money4 or TdsDataType.MoneyN =>
          ReadInt32(value) / 10000m,
          _ => throw CreateInvalidCast(type, typeof(decimal)),
      };

    private static DateTime DecodeDateTimeValue(
        ReadOnlySpan<byte> value,
        byte type,
        byte scale) =>
      type switch
      {
          TdsDataType.DateTime2 => DecodeDateTime2(value, scale),
          TdsDataType.DateTime => DecodeLegacyDateTime(value),
          TdsDataType.DateTime4 => DecodeSmallDateTime(value),
          TdsDataType.DateTimeN when value.Length == 8 => DecodeLegacyDateTime(value),
          TdsDataType.DateTimeN when value.Length == 4 => DecodeSmallDateTime(value),
          _ => throw CreateInvalidCast(type, typeof(DateTime)),
      };

    private static object DecodeIntN(ReadOnlySpan<byte> value) =>
      value.Length switch
      {
          1 => MsSqlBoxedScalarCache.Box(value[0]),
          2 => MsSqlBoxedScalarCache.Box(ReadInt16(value)),
          4 => MsSqlBoxedScalarCache.Box(ReadInt32(value)),
          8 => MsSqlBoxedScalarCache.Box(ReadInt64(value)),
          _ => throw new InvalidDataException($"Invalid SQL Server INTN length {value.Length}."),
      };

    private static object DecodeFloatN(ReadOnlySpan<byte> value) =>
      value.Length switch
      {
          4 => (object)ReadSingle(value),
          8 => ReadDouble(value),
          _ => throw new InvalidDataException($"Invalid SQL Server FLTN length {value.Length}."),
      };

    private static decimal DecodeDecimal(ReadOnlySpan<byte> value, byte scale)
    {
        if (value.Length is < 1 or > 17 || scale > 28)
        {
            throw new OverflowException("SQL Server decimal value cannot be represented by System.Decimal.");
        }

        var magnitude = value[1..];
        if (magnitude.Length > 12 && !IsZero(magnitude[12..]))
        {
            throw new OverflowException("SQL Server decimal value cannot be represented by System.Decimal.");
        }

        int low = ReadDecimalPart(magnitude);
        int middle = magnitude.Length > 4
          ? ReadDecimalPart(magnitude[4..])
          : 0;
        int high = magnitude.Length > 8
          ? ReadDecimalPart(magnitude[8..])
          : 0;
        return new decimal(low, middle, high, value[0] == 0, scale);
    }

    private static int ReadDecimalPart(ReadOnlySpan<byte> value) =>
      value.Length switch
      {
          >= 4 => BinaryPrimitives.ReadInt32LittleEndian(value),
          3 => value[0] | value[1] << 8 | value[2] << 16,
          2 => value[0] | value[1] << 8,
          1 => value[0],
          _ => 0,
      };

    private static decimal DecodeMoney(ReadOnlySpan<byte> value)
    {
        EnsureExact(value, sizeof(long));
        long high = BinaryPrimitives.ReadInt32LittleEndian(value);
        long low = BinaryPrimitives.ReadUInt32LittleEndian(value[4..]);
        return ((high << 32) | low) / 10000m;
    }

    private static DateOnly DecodeDate(ReadOnlySpan<byte> value)
    {
        if (value.Length != 3)
        {
            throw new InvalidDataException($"Invalid SQL Server DATE length {value.Length}.");
        }

        int days = value[0] | value[1] << 8 | value[2] << 16;
        return DateOnly.FromDayNumber(days);
    }

    private static TimeOnly DecodeTime(ReadOnlySpan<byte> value, byte scale)
    {
        long units = ReadUnsignedLittleEndian(value);
        long ticks = checked(units * PowerOfTen(7 - scale));
        return new TimeOnly(ticks);
    }

    private static DateTime DecodeDateTime2(ReadOnlySpan<byte> value, byte scale)
    {
        int timeLength = value.Length - 3;
        if (timeLength is < 3 or > 5)
        {
            throw new InvalidDataException($"Invalid SQL Server DATETIME2 length {value.Length}.");
        }

        var time = DecodeTime(value[..timeLength], scale);
        var date = DecodeDate(value[timeLength..]);
        return date.ToDateTime(time, DateTimeKind.Unspecified);
    }

    private static DateTimeOffset DecodeDateTimeOffsetValue(
        ReadOnlySpan<byte> value,
        byte scale)
    {
        int timeLength = value.Length - 5;
        if (timeLength is < 3 or > 5)
        {
            throw new InvalidDataException(
              $"Invalid SQL Server DATETIMEOFFSET length {value.Length}.");
        }

        var time = DecodeTime(value[..timeLength], scale);
        var date = DecodeDate(value.Slice(timeLength, 3));
        TimeSpan offset = TimeSpan.FromMinutes(
          BinaryPrimitives.ReadInt16LittleEndian(value[(timeLength + 3)..]));
        DateTime utc = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Utc);
        return new DateTimeOffset(utc).ToOffset(offset);
    }

    private static DateTime DecodeLegacyDateTime(ReadOnlySpan<byte> value)
    {
        if (value.Length != 8)
        {
            throw new InvalidDataException($"Invalid SQL Server DATETIME length {value.Length}.");
        }

        int days = BinaryPrimitives.ReadInt32LittleEndian(value);
        uint threeHundredths = BinaryPrimitives.ReadUInt32LittleEndian(value[4..]);
        long milliseconds = checked((long)Math.Round(
          threeHundredths * 1000d / 300,
          MidpointRounding.AwayFromZero));
        return new DateTime(1900, 1, 1).AddDays(days).AddMilliseconds(milliseconds);
    }

    private static DateTime DecodeSmallDateTime(ReadOnlySpan<byte> value)
    {
        if (value.Length != 4)
        {
            throw new InvalidDataException(
              $"Invalid SQL Server SMALLDATETIME length {value.Length}.");
        }

        int days = BinaryPrimitives.ReadUInt16LittleEndian(value);
        int minutes = BinaryPrimitives.ReadUInt16LittleEndian(value[2..]);
        return new DateTime(1900, 1, 1).AddDays(days).AddMinutes(minutes);
    }

    private string DecodeStringValue(ReadOnlySpan<byte> value, SqlColumn column)
    {
        byte type = checked((byte)column.TypeId);
        int codePage = type == TdsDataType.Json
                    ? IsUtf16LittleEndian(value) ? 1200 : 65001
          : type is
          TdsDataType.NVarChar or
          TdsDataType.NChar or
          TdsDataType.NText or
          TdsDataType.Xml
            ? 1200
            : (int)(unchecked((uint)column.TypeModifier) >> 16);
        if (codePage == 0)
        {
            throw new InvalidDataException(
              $"SQL Server character type 0x{type:X2} did not include a resolvable collation.");
        }

        return _strings.GetString(value, codePage);
    }

    private JsonElement DecodeJson(
        ReadOnlyMemory<byte> value,
        SqlColumn column)
    {
                using var document = JsonDocument.Parse(
                    DecodeStringValue(value.Span, column));
        return document.RootElement.Clone();
    }

        private static bool IsUtf16LittleEndian(ReadOnlySpan<byte> value) =>
            value.Length >= 4 &&
            value.Length % 2 == 0 &&
            value[1] == 0 &&
            value[3] == 0;

    private static bool IsStringType(byte type) =>
      type is
        TdsDataType.NVarChar or
        TdsDataType.NChar or
        TdsDataType.NText or
        TdsDataType.Xml or
        TdsDataType.Json or
        TdsDataType.Char or
        TdsDataType.VarChar or
        TdsDataType.BigChar or
        TdsDataType.BigVarChar or
        TdsDataType.Text;

    private static bool IsBinaryType(byte type) =>
      type is
        TdsDataType.Binary or
        TdsDataType.VarBinary or
        TdsDataType.BigBinary or
        TdsDataType.BigVarBinary or
        TdsDataType.Image or
        TdsDataType.Udt;

    private static void EnsureDecimalType(SqlColumn column, Type requestedType)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(
          column,
          type is
            TdsDataType.Decimal or
            TdsDataType.Numeric or
            TdsDataType.DecimalN or
            TdsDataType.NumericN or
            TdsDataType.Money or
            TdsDataType.Money4 or
            TdsDataType.MoneyN,
          requestedType);
    }

    private static void EnsureDateTimeType(SqlColumn column, Type requestedType)
    {
        byte type = checked((byte)column.TypeId);
        EnsureType(
          column,
          type is
            TdsDataType.DateTime2 or
            TdsDataType.DateTime or
            TdsDataType.DateTime4 or
            TdsDataType.DateTimeN,
          requestedType);
    }

    private static void EnsureType(
        SqlColumn column,
        bool matches,
        Type requestedType)
    {
        if (!matches || column.Format != SqlDataFormat.Binary)
        {
            throw CreateInvalidCast(
              checked((byte)column.TypeId),
              requestedType);
        }
    }

    private static void EnsureFormat(SqlColumn column, Type requestedType)
    {
        if (column.Format != SqlDataFormat.Binary)
        {
            throw CreateInvalidCast(
              checked((byte)column.TypeId),
              requestedType);
        }
    }

    private static InvalidCastException CreateInvalidCast(byte type, Type requestedType) =>
      new(
        $"SQL Server TDS type 0x{type:X2} cannot be decoded as " +
        $"{requestedType.FullName}.");

    private static short ReadInt16(ReadOnlySpan<byte> value)
    {
        EnsureExact(value, sizeof(short));
        return BinaryPrimitives.ReadInt16LittleEndian(value);
    }

    private static byte ReadByte(ReadOnlySpan<byte> value)
    {
        EnsureExact(value, sizeof(byte));
        return value[0];
    }

    private static int ReadInt32(ReadOnlySpan<byte> value)
    {
        EnsureExact(value, sizeof(int));
        return BinaryPrimitives.ReadInt32LittleEndian(value);
    }

    private static long ReadInt64(ReadOnlySpan<byte> value)
    {
        EnsureExact(value, sizeof(long));
        return BinaryPrimitives.ReadInt64LittleEndian(value);
    }

    private static float ReadSingle(ReadOnlySpan<byte> value) =>
      BitConverter.Int32BitsToSingle(ReadInt32(value));

    private static double ReadDouble(ReadOnlySpan<byte> value) =>
      BitConverter.Int64BitsToDouble(ReadInt64(value));

    private static long ReadUnsignedLittleEndian(ReadOnlySpan<byte> value)
    {
        if (value.Length is < 1 or > 8)
        {
            throw new InvalidDataException("Invalid little-endian integer length.");
        }

        long result = 0;
        for (int i = 0; i < value.Length; i++)
        {
            result |= (long)value[i] << (8 * i);
        }

        return result;
    }

    private static long PowerOfTen(int exponent)
    {
        if ((uint)exponent > 7)
        {
            throw new InvalidDataException("Invalid SQL Server temporal scale.");
        }

        long result = 1;
        for (int i = 0; i < exponent; i++)
        {
            result *= 10;
        }

        return result;
    }

    private static ReadOnlyMemory<byte> GetRequiredField(
        ReadOnlyMemory<byte> row,
        int ordinal)
    {
        var field = GetField(row, ordinal);
        if (field.IsNull)
        {
            throw new InvalidCastException($"Column {ordinal} contains NULL.");
        }

        return field.Value;
    }

    private static Field GetField(ReadOnlyMemory<byte> row, int ordinal)
    {
        var span = row.Span;
        Ensure(span, 0, sizeof(ushort));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(span);
        if ((uint)ordinal >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        int position = sizeof(ushort);
        for (int i = 0; i < count; i++)
        {
            Ensure(span, position, sizeof(int));
            int length = BinaryPrimitives.ReadInt32LittleEndian(span[position..]);
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
                return new Field(row.Slice(position, length), IsNull: false);
            }

            position += length;
        }

        throw new InvalidDataException("SQL Server row ended before the requested field.");
    }

    private static bool IsZero(ReadOnlySpan<byte> value)
    {
        foreach (byte item in value)
        {
            if (item != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureExact(ReadOnlySpan<byte> value, int length)
    {
        if (value.Length != length)
        {
            throw new InvalidDataException(
              $"SQL Server value has length {value.Length}; expected {length}.");
        }
    }

    private static void Ensure(ReadOnlySpan<byte> value, int position, int length)
    {
        if (position < 0 || length < 0 || position > value.Length - length)
        {
            throw new InvalidDataException("SQL Server row is truncated.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TTo Cast<TFrom, TTo>(TFrom value) =>
      Unsafe.As<TFrom, TTo>(ref value);

    private static class TypedDecoder<T>
    {
        internal static readonly TypedDecoderKind s_kind = ResolveTypedDecoder<T>();
    }

    private static TypedDecoderKind ResolveTypedDecoder<T>()
    {
        var type = typeof(T);
        if (type == typeof(bool))
        {
            return TypedDecoderKind.Boolean;
        }

        if (type == typeof(bool?))
        {
            return TypedDecoderKind.NullableBoolean;
        }

        if (type == typeof(short))
        {
            return TypedDecoderKind.Int16;
        }

        if (type == typeof(short?))
        {
            return TypedDecoderKind.NullableInt16;
        }

        if (type == typeof(int))
        {
            return TypedDecoderKind.Int32;
        }

        if (type == typeof(int?))
        {
            return TypedDecoderKind.NullableInt32;
        }

        if (type == typeof(long))
        {
            return TypedDecoderKind.Int64;
        }

        if (type == typeof(long?))
        {
            return TypedDecoderKind.NullableInt64;
        }

        if (type == typeof(float))
        {
            return TypedDecoderKind.Float;
        }

        if (type == typeof(float?))
        {
            return TypedDecoderKind.NullableFloat;
        }

        if (type == typeof(double))
        {
            return TypedDecoderKind.Double;
        }

        if (type == typeof(double?))
        {
            return TypedDecoderKind.NullableDouble;
        }

        if (type == typeof(decimal))
        {
            return TypedDecoderKind.Decimal;
        }

        if (type == typeof(decimal?))
        {
            return TypedDecoderKind.NullableDecimal;
        }

        if (type == typeof(string))
        {
            return TypedDecoderKind.String;
        }

        if (type == typeof(byte[]))
        {
            return TypedDecoderKind.Bytes;
        }

        if (type == typeof(ReadOnlyMemory<byte>))
        {
            return TypedDecoderKind.ReadOnlyMemory;
        }

        if (type == typeof(ReadOnlyMemory<byte>?))
        {
            return TypedDecoderKind.NullableReadOnlyMemory;
        }

        if (type == typeof(Guid))
        {
            return TypedDecoderKind.Guid;
        }

        if (type == typeof(Guid?))
        {
            return TypedDecoderKind.NullableGuid;
        }

        if (type == typeof(DateOnly))
        {
            return TypedDecoderKind.DateOnly;
        }

        if (type == typeof(DateOnly?))
        {
            return TypedDecoderKind.NullableDateOnly;
        }

        if (type == typeof(TimeOnly))
        {
            return TypedDecoderKind.TimeOnly;
        }

        if (type == typeof(TimeOnly?))
        {
            return TypedDecoderKind.NullableTimeOnly;
        }

        if (type == typeof(DateTime))
        {
            return TypedDecoderKind.DateTime;
        }

        if (type == typeof(DateTime?))
        {
            return TypedDecoderKind.NullableDateTime;
        }

        if (type == typeof(DateTimeOffset))
        {
            return TypedDecoderKind.DateTimeOffset;
        }

        if (type == typeof(DateTimeOffset?))
        {
            return TypedDecoderKind.NullableDateTimeOffset;
        }

        if (type == typeof(JsonElement))
        {
            return TypedDecoderKind.JsonElement;
        }

        if (type == typeof(JsonElement?))
        {
            return TypedDecoderKind.NullableJsonElement;
        }

        if (type == typeof(object))
        {
            return TypedDecoderKind.Object;
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

    private enum TypedDecoderKind : byte
    {
        Unsupported,
        Boolean,
        NullableBoolean,
        Int16,
        NullableInt16,
        Int32,
        NullableInt32,
        Int64,
        NullableInt64,
        Float,
        NullableFloat,
        Double,
        NullableDouble,
        Decimal,
        NullableDecimal,
        String,
        Bytes,
        ReadOnlyMemory,
        NullableReadOnlyMemory,
        Guid,
        NullableGuid,
        DateOnly,
        NullableDateOnly,
        TimeOnly,
        NullableTimeOnly,
        DateTime,
        NullableDateTime,
        DateTimeOffset,
        NullableDateTimeOffset,
        JsonElement,
        NullableJsonElement,
        Object,
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
        bool IsNull)
    {
    }
}

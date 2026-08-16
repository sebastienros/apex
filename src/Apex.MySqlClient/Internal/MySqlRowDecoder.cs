using System.Buffers.Binary;
using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MySqlClient.Internal;

/// <summary>
/// Decodes the rows of one result set. An instance is bound to the column definitions and to
/// the protocol, text for COM_QUERY and binary for prepared statements.
/// </summary>
internal sealed class MySqlRowDecoder : ISqlRowDecoder
{
    private readonly Utf8StringCache _strings;
    private readonly MySqlZeroDateBehavior _zeroDates;
    private MySqlColumnMetadata[] _metadata = [];
    private SqlColumn[] _columns = [];
    private bool _binary;
    private int _nullBitmapLength;

    internal MySqlRowDecoder(Utf8StringCache strings, MySqlZeroDateBehavior zeroDates)
    {
        _strings = strings;
        _zeroDates = zeroDates;
    }

    internal SqlColumn[] Columns => _columns;

    internal bool IsBinary => _binary;

    internal int FieldCount => _metadata.Length;

    internal void SetColumns(MySqlColumnMetadata[] metadata, bool binary)
    {
        _metadata = metadata;
        _binary = binary;
        _nullBitmapLength = (metadata.Length + 9) / 8;
        SqlColumn[] columns = new SqlColumn[metadata.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            columns[i] = MySqlColumnCodec.ToColumn(metadata[i], binary);
        }

        _columns = columns;
    }

    internal void ValidateRow(ReadOnlySpan<byte> row)
    {
        if (_binary)
        {
            if (row.Length < 1 + _nullBitmapLength || row[0] != MySqlProtocol.OkHeader)
            {
                throw new InvalidDataException("MySQL binary row header is invalid.");
            }

            var bitmap = row.Slice(1, _nullBitmapLength);
            MySqlPayloadReader reader = new(row[(1 + _nullBitmapLength)..]);
            for (var i = 0; i < _metadata.Length; i++)
            {
                if (!IsNullInBitmap(bitmap, i))
                {
                    SkipBinaryValue(ref reader, _metadata[i].Type);
                }
            }

            if (reader.Remaining != 0)
            {
                throw new InvalidDataException("MySQL binary row has trailing data.");
            }

            return;
        }

        MySqlPayloadReader text = new(row);
        for (var i = 0; i < _metadata.Length; i++)
        {
            _ = text.ReadLengthEncodedSpan(out _);
        }

        if (text.Remaining != 0)
        {
            throw new InvalidDataException("MySQL text row has trailing data.");
        }
    }

    public int GetFieldCount(ReadOnlyMemory<byte> row) => _metadata.Length;

    internal int GetFieldCount(ReadOnlySpan<byte> _) => _metadata.Length;

    public bool IsNull(ReadOnlyMemory<byte> row, int ordinal)
    {
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull || IsNullZeroDate(value, ordinal);
    }

    public object? DecodeObject(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureColumn(ordinal, column, typeof(object));
        var value = GetField(row.Span, ordinal, out var isNull);
        if (isNull)
        {
            return null;
        }

        var unsigned = metadata.IsUnsigned;
        switch (metadata.Type)
        {
            case MySqlType.Tiny:
                return unsigned
                  ? BoxedScalarCache.Box(checked((byte)ReadUInt64(value, metadata)))
                  : BoxedScalarCache.Box(checked((sbyte)ReadInt64(value, metadata)));
            case MySqlType.Short:
                return unsigned
                  ? BoxedScalarCache.Box(checked((ushort)ReadUInt64(value, metadata)))
                  : BoxedScalarCache.Box(checked((short)ReadInt64(value, metadata)));
            case MySqlType.Year:
                return BoxedScalarCache.Box(checked((int)ReadInt64(value, metadata)));
            case MySqlType.Int24:
            case MySqlType.Long:
                return unsigned
                  ? BoxedScalarCache.Box(checked((uint)ReadUInt64(value, metadata)))
                  : BoxedScalarCache.Box(checked((int)ReadInt64(value, metadata)));
            case MySqlType.LongLong:
                return unsigned
                  ? BoxedScalarCache.Box(ReadUInt64(value, metadata))
                  : BoxedScalarCache.Box(ReadInt64(value, metadata));
            case MySqlType.Bit:
                return BoxedScalarCache.Box(MySqlValueCodec.ParseBit(value));
            case MySqlType.Float:
                return _binary
                  ? BitConverter.Int32BitsToSingle(ReadBinaryInt32(value))
                  : MySqlValueCodec.ParseSingle(value);
            case MySqlType.Double:
                return _binary
                  ? BitConverter.Int64BitsToDouble(ReadBinaryInt64(value))
                  : MySqlValueCodec.ParseDouble(value);
            case MySqlType.Decimal:
            case MySqlType.NewDecimal:
                return MySqlDecimal.Parse(_strings.GetString(value));
            case MySqlType.Date:
            case MySqlType.NewDate:
                return DecodeDateObject(value);
            case MySqlType.DateTime:
            case MySqlType.DateTime2:
            case MySqlType.Timestamp:
            case MySqlType.Timestamp2:
                return DecodeDateTimeObject(value);
            case MySqlType.Time:
            case MySqlType.Time2:
                return DecodeTime(value);
            case MySqlType.Json:
                return DecodeJson(value);
            case MySqlType.Null:
                return null;
            case MySqlType.Geometry:
            case MySqlType.Vector:
                return value.ToArray();
            default:
                return IsBinaryContent(metadata)
                  ? value.ToArray()
                  : _strings.GetString(value);
        }
    }

    internal object? DecodeObject(ReadOnlyMemory<byte> row, int ordinal) =>
      DecodeObject(row, ordinal, _columns[ordinal]);

    public bool DecodeBoolean(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureSignedType(ordinal, column, typeof(bool), MySqlType.Tiny);
        return ReadInt64(GetRequiredField(row, ordinal), metadata) != 0;
    }

    public bool? DecodeNullableBoolean(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureSignedType(ordinal, column, typeof(bool?), MySqlType.Tiny);
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : ReadInt64(value, metadata) != 0;
    }

    public short DecodeInt16(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureSignedIntegerType(ordinal, column, typeof(short));
        return checked((short)ReadInt64(GetRequiredField(row, ordinal), metadata));
    }

    public short? DecodeNullableInt16(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureSignedIntegerType(ordinal, column, typeof(short?));
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : checked((short)ReadInt64(value, metadata));
    }

    public int DecodeInt32(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureSignedIntegerType(ordinal, column, typeof(int));
        return checked((int)ReadInt64(GetRequiredField(row, ordinal), metadata));
    }

    public int? DecodeNullableInt32(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureSignedIntegerType(ordinal, column, typeof(int?));
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : checked((int)ReadInt64(value, metadata));
    }

    public long DecodeInt64(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureSignedIntegerType(ordinal, column, typeof(long));
        return ReadInt64(GetRequiredField(row, ordinal), metadata);
    }

    public long? DecodeNullableInt64(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureSignedIntegerType(ordinal, column, typeof(long?));
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : ReadInt64(value, metadata);
    }

    public float DecodeFloat(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureNumericType(ordinal, column, typeof(float));
        return (float)ReadDouble(GetRequiredField(row, ordinal), metadata);
    }

    public float? DecodeNullableFloat(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureNumericType(ordinal, column, typeof(float?));
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : (float)ReadDouble(value, metadata);
    }

    public double DecodeDouble(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureNumericType(ordinal, column, typeof(double));
        return ReadDouble(GetRequiredField(row, ordinal), metadata);
    }

    public double? DecodeNullableDouble(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureNumericType(ordinal, column, typeof(double?));
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : ReadDouble(value, metadata);
    }

    public decimal DecodeDecimal(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureNumericType(ordinal, column, typeof(decimal));
        return ReadDecimal(GetRequiredField(row, ordinal), metadata);
    }

    public decimal? DecodeNullableDecimal(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureNumericType(ordinal, column, typeof(decimal?));
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : ReadDecimal(value, metadata);
    }

    public string? DecodeString(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureColumn(ordinal, column, typeof(string));
        EnsureStringType(metadata, column, typeof(string));
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : _strings.GetString(value);
    }

    public byte[]? DecodeBytes(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureColumn(ordinal, column, typeof(byte[]));
        EnsureBytesType(metadata, column, typeof(byte[]));
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : value.ToArray();
    }

    public ReadOnlyMemory<byte> DecodeReadOnlyMemory(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureColumn(ordinal, column, typeof(ReadOnlyMemory<byte>));
        EnsureBytesType(metadata, column, typeof(ReadOnlyMemory<byte>));
        return GetRequiredFieldMemory(row, ordinal);
    }

    public ReadOnlyMemory<byte>? DecodeNullableReadOnlyMemory(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureColumn(ordinal, column, typeof(ReadOnlyMemory<byte>?));
        EnsureBytesType(metadata, column, typeof(ReadOnlyMemory<byte>?));
        var value = GetFieldMemory(row, ordinal, out var isNull);
        return isNull ? null : value;
    }

    public Guid DecodeGuid(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureColumn(ordinal, column, typeof(Guid));
        EnsureGuidType(metadata, column, typeof(Guid));
        return ReadGuid(GetRequiredField(row, ordinal), metadata);
    }

    public Guid? DecodeNullableGuid(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureColumn(ordinal, column, typeof(Guid?));
        EnsureGuidType(metadata, column, typeof(Guid?));
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : ReadGuid(value, metadata);
    }

    public DateOnly DecodeDateOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureDateType(ordinal, column, typeof(DateOnly));
        return ReadDateOnly(GetRequiredField(row, ordinal), metadata);
    }

    public DateOnly? DecodeNullableDateOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureDateType(ordinal, column, typeof(DateOnly?));
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull || IsNullZeroDate(value, ordinal) ? null : ReadDateOnly(value, metadata);
    }

    public TimeOnly DecodeTimeOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureTimeType(ordinal, column, typeof(TimeOnly));
        return ReadTimeOnly(GetRequiredField(row, ordinal), metadata);
    }

    public TimeOnly? DecodeNullableTimeOnly(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureTimeType(ordinal, column, typeof(TimeOnly?));
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : ReadTimeOnly(value, metadata);
    }

    public DateTime DecodeDateTime(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureDateType(ordinal, column, typeof(DateTime));
        return ReadDateTime(GetRequiredField(row, ordinal), metadata);
    }

    public DateTime? DecodeNullableDateTime(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureDateType(ordinal, column, typeof(DateTime?));
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull || IsNullZeroDate(value, ordinal) ? null : ReadDateTime(value, metadata);
    }

    public DateTimeOffset DecodeDateTimeOffset(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureDateType(ordinal, column, typeof(DateTimeOffset));
        return new DateTimeOffset(ReadDateTime(GetRequiredField(row, ordinal), metadata), TimeSpan.Zero);
    }

    public DateTimeOffset? DecodeNullableDateTimeOffset(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata =
          EnsureDateType(ordinal, column, typeof(DateTimeOffset?));
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull || IsNullZeroDate(value, ordinal)
          ? null
          : new DateTimeOffset(ReadDateTime(value, metadata), TimeSpan.Zero);
    }

    public JsonElement DecodeJsonElement(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        _ = EnsureType(ordinal, column, typeof(JsonElement), MySqlType.Json);
        return DecodeJson(GetRequiredField(row, ordinal));
    }

    public JsonElement? DecodeNullableJsonElement(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        _ = EnsureType(ordinal, column, typeof(JsonElement?), MySqlType.Json);
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : DecodeJson(value);
    }

    public TElement[]? DecodeArray<TElement>(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        _ = EnsureColumn(ordinal, column, typeof(TElement[]));
        throw CannotRead(column, typeof(TElement[]));
    }

    public T Decode<T>(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column,
        bool copyReadOnlyMemory)
    {
        if (IsNull(row, ordinal))
        {
            return default(T) is null
              ? default!
              : throw new InvalidCastException($"Column {ordinal} contains NULL.");
        }

        if ((MySqlType)column.TypeId == MySqlType.Json &&
            TypedDecoder<T>.s_kind is not (
              TypedDecoderKind.String or
              TypedDecoderKind.JsonElement or
              TypedDecoderKind.NullableJsonElement or
              TypedDecoderKind.Object))
        {
            _ = EnsureType(ordinal, column, typeof(T), MySqlType.Json);
            var json = GetRequiredField(row, ordinal);
            return DecodeJsonValue<T>(json, ordinal);
        }

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
                    var value = DecodeReadOnlyMemory(row, ordinal, column);
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
                return Cast<DateTimeOffset, T>(DecodeDateTimeOffset(row, ordinal, column));
            case TypedDecoderKind.NullableDateTimeOffset:
                return Cast<DateTimeOffset?, T>(DecodeNullableDateTimeOffset(row, ordinal, column));
            case TypedDecoderKind.JsonElement:
                return Cast<JsonElement, T>(DecodeJsonElement(row, ordinal, column));
            case TypedDecoderKind.NullableJsonElement:
                return Cast<JsonElement?, T>(DecodeNullableJsonElement(row, ordinal, column));
            case TypedDecoderKind.Object:
                return (T)DecodeObject(row, ordinal, column)!;
            case TypedDecoderKind.MySqlDecimal:
                return Cast<MySqlDecimal, T>(DecodeMySqlDecimal(row, ordinal, column));
            case TypedDecoderKind.NullableMySqlDecimal:
                return Cast<MySqlDecimal?, T>(DecodeNullableMySqlDecimal(row, ordinal, column));
            case TypedDecoderKind.SByte:
                return Cast<sbyte, T>(DecodeSByte(row, ordinal, column));
            case TypedDecoderKind.NullableSByte:
                return Cast<sbyte?, T>(DecodeNullableSByte(row, ordinal, column));
            case TypedDecoderKind.Byte:
                return Cast<byte, T>(DecodeByte(row, ordinal, column));
            case TypedDecoderKind.NullableByte:
                return Cast<byte?, T>(DecodeNullableByte(row, ordinal, column));
            case TypedDecoderKind.UInt16:
                return Cast<ushort, T>(DecodeUInt16(row, ordinal, column));
            case TypedDecoderKind.NullableUInt16:
                return Cast<ushort?, T>(DecodeNullableUInt16(row, ordinal, column));
            case TypedDecoderKind.UInt32:
                return Cast<uint, T>(DecodeUInt32(row, ordinal, column));
            case TypedDecoderKind.NullableUInt32:
                return Cast<uint?, T>(DecodeNullableUInt32(row, ordinal, column));
            case TypedDecoderKind.UInt64:
                return Cast<ulong, T>(DecodeUInt64(row, ordinal, column));
            case TypedDecoderKind.NullableUInt64:
                return Cast<ulong?, T>(DecodeNullableUInt64(row, ordinal, column));
            case TypedDecoderKind.TimeSpan:
                return Cast<TimeSpan, T>(DecodeTimeSpan(row, ordinal, column));
            case TypedDecoderKind.NullableTimeSpan:
                return Cast<TimeSpan?, T>(DecodeNullableTimeSpan(row, ordinal, column));
            case TypedDecoderKind.Half:
            case TypedDecoderKind.NullableHalf:
            case TypedDecoderKind.BigInteger:
            case TypedDecoderKind.NullableBigInteger:
            case TypedDecoderKind.Int128:
            case TypedDecoderKind.NullableInt128:
            case TypedDecoderKind.UInt128:
            case TypedDecoderKind.NullableUInt128:
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

    private T DecodeAlternative<T>(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column,
        TypedDecoderKind kind)
    {
        var requestedType = typeof(T);
        var metadata = EnsureColumn(ordinal, column, requestedType);
        switch (kind)
        {
            case TypedDecoderKind.Half:
            case TypedDecoderKind.NullableHalf:
                _ = EnsureType(ordinal, column, requestedType, MySqlType.Float);
                break;
            case TypedDecoderKind.BigInteger:
            case TypedDecoderKind.NullableBigInteger:
            case TypedDecoderKind.Int128:
            case TypedDecoderKind.NullableInt128:
            case TypedDecoderKind.UInt128:
            case TypedDecoderKind.NullableUInt128:
                _ = EnsureType(
                  ordinal,
                  column,
                  requestedType,
                  MySqlType.Decimal,
                  MySqlType.NewDecimal);
                break;
            case TypedDecoderKind.Char:
            case TypedDecoderKind.NullableChar:
            case TypedDecoderKind.Chars:
            case TypedDecoderKind.IPAddress:
                EnsureStringType(metadata, column, requestedType);
                break;
            case TypedDecoderKind.PhysicalAddress:
                EnsureBytesType(metadata, column, requestedType);
                break;
            case TypedDecoderKind.BitArray:
                _ = EnsureType(ordinal, column, requestedType, MySqlType.Bit);
                break;
            default:
                throw CannotRead(column, requestedType);
        }

        var bytes = GetRequiredField(row, ordinal);
        switch (kind)
        {
            case TypedDecoderKind.Half:
            case TypedDecoderKind.NullableHalf:
                return CastAlternative<T, Half>(checked((Half)ReadDouble(bytes, metadata)));
            case TypedDecoderKind.BigInteger:
            case TypedDecoderKind.NullableBigInteger:
                                return CastAlternative<T, BigInteger>(
                                    DecodeIntegralNumeric(bytes, column, requestedType));
            case TypedDecoderKind.Int128:
            case TypedDecoderKind.NullableInt128:
                                return CastAlternative<T, Int128>(checked((Int128)DecodeIntegralNumeric(
                                    bytes, column, requestedType)));
            case TypedDecoderKind.UInt128:
            case TypedDecoderKind.NullableUInt128:
                                return CastAlternative<T, UInt128>(checked((UInt128)DecodeIntegralNumeric(
                                    bytes, column, requestedType)));
            case TypedDecoderKind.Char:
            case TypedDecoderKind.NullableChar:
                string text = _strings.GetString(bytes);
                char character = text.Length == 1
                  ? text[0]
                  : throw CannotRead(column, requestedType);
                return CastAlternative<T, char>(character);
            case TypedDecoderKind.Chars:
                return (T)(object)_strings.GetString(bytes).ToCharArray();
            case TypedDecoderKind.IPAddress:
                return (T)(object)IPAddress.Parse(_strings.GetString(bytes));
            case TypedDecoderKind.PhysicalAddress:
                if (bytes.Length is not (6 or 8))
                {
                    throw CannotRead(column, requestedType);
                }

                return (T)(object)new PhysicalAddress(bytes.ToArray());
            case TypedDecoderKind.BitArray:
                int bitCount = checked((int)metadata.ColumnLength);
                if (bitCount is < 0 or > 64)
                {
                    throw CannotRead(column, requestedType);
                }

                ulong bitValue = MySqlValueCodec.ParseBit(bytes);
                var bits = new BitArray(bitCount);
                for (var i = 0; i < bitCount; i++)
                {
                    bits[i] = (bitValue & (1UL << (bitCount - 1 - i))) != 0;
                }

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

    private BigInteger DecodeIntegralNumeric(
        ReadOnlySpan<byte> value,
        SqlColumn column,
        Type requestedType)
    {
        var numeric = MySqlDecimal.Parse(_strings.GetString(value));
        return numeric.Scale == 0
          ? numeric.UnscaledValue
          : throw CannotRead(column, requestedType);
    }

    internal T Decode<T>(ReadOnlyMemory<byte> row, int ordinal)
    {
        if ((uint)ordinal >= (uint)_columns.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        return Decode<T>(row, ordinal, _columns[ordinal], copyReadOnlyMemory: false);
    }

    private MySqlDecimal DecodeMySqlDecimal(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        _ = EnsureType(ordinal, column, typeof(MySqlDecimal), MySqlType.Decimal, MySqlType.NewDecimal);
        return MySqlDecimal.Parse(_strings.GetString(GetRequiredField(row, ordinal)));
    }

    private MySqlDecimal? DecodeNullableMySqlDecimal(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        _ = EnsureType(ordinal, column, typeof(MySqlDecimal?), MySqlType.Decimal, MySqlType.NewDecimal);
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : MySqlDecimal.Parse(_strings.GetString(value));
    }

    private sbyte DecodeSByte(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureSignedType(ordinal, column, typeof(sbyte), MySqlType.Tiny);
        return checked((sbyte)ReadInt64(GetRequiredField(row, ordinal), metadata));
    }

    private sbyte? DecodeNullableSByte(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureSignedType(ordinal, column, typeof(sbyte?), MySqlType.Tiny);
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : checked((sbyte)ReadInt64(value, metadata));
    }

    private byte DecodeByte(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureUnsignedType(ordinal, column, typeof(byte), MySqlType.Tiny);
        return checked((byte)ReadUInt64(GetRequiredField(row, ordinal), metadata));
    }

    private byte? DecodeNullableByte(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureUnsignedType(ordinal, column, typeof(byte?), MySqlType.Tiny);
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : checked((byte)ReadUInt64(value, metadata));
    }

    private ushort DecodeUInt16(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureUnsignedType(ordinal, column, typeof(ushort), MySqlType.Short);
        return checked((ushort)ReadUInt64(GetRequiredField(row, ordinal), metadata));
    }

    private ushort? DecodeNullableUInt16(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureUnsignedType(ordinal, column, typeof(ushort?), MySqlType.Short);
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : checked((ushort)ReadUInt64(value, metadata));
    }

    private uint DecodeUInt32(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureUnsignedType(ordinal, column, typeof(uint), MySqlType.Int24, MySqlType.Long);
        return checked((uint)ReadUInt64(GetRequiredField(row, ordinal), metadata));
    }

    private uint? DecodeNullableUInt32(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureUnsignedType(ordinal, column, typeof(uint?), MySqlType.Int24, MySqlType.Long);
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : checked((uint)ReadUInt64(value, metadata));
    }

    private ulong DecodeUInt64(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureColumn(ordinal, column, typeof(ulong));
        if (metadata.Type != MySqlType.Bit &&
            (metadata.Type != MySqlType.LongLong || !metadata.IsUnsigned))
        {
            throw CannotRead(column, typeof(ulong));
        }

        return ReadUInt64(GetRequiredField(row, ordinal), metadata);
    }

    private ulong? DecodeNullableUInt64(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureColumn(ordinal, column, typeof(ulong?));
        if (metadata.Type != MySqlType.Bit &&
            (metadata.Type != MySqlType.LongLong || !metadata.IsUnsigned))
        {
            throw CannotRead(column, typeof(ulong?));
        }

        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : ReadUInt64(value, metadata);
    }

    private TimeSpan DecodeTimeSpan(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureType(ordinal, column, typeof(TimeSpan), MySqlType.Time, MySqlType.Time2);
        return ReadTimeSpan(GetRequiredField(row, ordinal), metadata);
    }

    private TimeSpan? DecodeNullableTimeSpan(
        ReadOnlyMemory<byte> row,
        int ordinal,
        SqlColumn column)
    {
        var metadata = EnsureType(ordinal, column, typeof(TimeSpan?), MySqlType.Time, MySqlType.Time2);
        var value = GetField(row.Span, ordinal, out var isNull);
        return isNull ? null : ReadTimeSpan(value, metadata);
    }

    private ReadOnlySpan<byte> GetRequiredField(ReadOnlyMemory<byte> row, int ordinal)
    {
        var value = GetField(row.Span, ordinal, out var isNull);
        if (isNull || IsNullZeroDate(value, ordinal))
        {
            throw new InvalidCastException($"Column {ordinal} contains NULL.");
        }

        return value;
    }

    private ReadOnlyMemory<byte> GetRequiredFieldMemory(ReadOnlyMemory<byte> row, int ordinal)
    {
        var value = GetFieldMemory(row, ordinal, out var isNull);
        if (isNull)
        {
            throw new InvalidCastException($"Column {ordinal} contains NULL.");
        }

        return value;
    }

    private ReadOnlyMemory<byte> GetFieldMemory(
        ReadOnlyMemory<byte> row,
        int ordinal,
        out bool isNull)
    {
        if ((uint)ordinal >= (uint)_metadata.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        var payload = row.Span;
        MySqlPayloadReader reader;
        if (_binary)
        {
            var baseOffset = 1 + _nullBitmapLength;
            if (payload.Length < baseOffset || payload[0] != MySqlProtocol.OkHeader)
            {
                throw new InvalidDataException("MySQL binary row header is invalid.");
            }

            var bitmap = payload.Slice(1, _nullBitmapLength);
            if (IsNullInBitmap(bitmap, ordinal))
            {
                isNull = true;
                return default;
            }

            isNull = false;
            reader = new MySqlPayloadReader(payload[baseOffset..]);
            for (var i = 0; i < ordinal; i++)
            {
                if (!IsNullInBitmap(bitmap, i))
                {
                    SkipBinaryValue(ref reader, _metadata[i].Type);
                }
            }

            var value = ReadBinaryValue(ref reader, _metadata[ordinal].Type);
            return row.Slice(baseOffset + reader.Position - value.Length, value.Length);
        }

        reader = new MySqlPayloadReader(payload);
        for (var i = 0; i < ordinal; i++)
        {
            _ = reader.ReadLengthEncodedSpan(out _);
        }

        var textValue = reader.ReadLengthEncodedSpan(out isNull);
        if (isNull)
        {
            return default;
        }

        return row.Slice(reader.Position - textValue.Length, textValue.Length);
    }

    private bool IsNullZeroDate(ReadOnlySpan<byte> value, int ordinal) =>
      _zeroDates == MySqlZeroDateBehavior.Null &&
      IsZeroTemporal(value, _metadata[ordinal].Type);

    private MySqlColumnMetadata EnsureColumn(
        int ordinal,
        SqlColumn column,
        Type requestedType)
    {
        if ((uint)ordinal >= (uint)_metadata.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        var metadata = _metadata[ordinal];
        if (!Equals(column, _columns[ordinal]))
        {
            throw CannotRead(column, requestedType);
        }

        return metadata;
    }

    private MySqlColumnMetadata EnsureType(
        int ordinal,
        SqlColumn column,
        Type requestedType,
        MySqlType type1) =>
      EnsureTypeCore(ordinal, column, requestedType, type1, type1, type1, type1);

    private MySqlColumnMetadata EnsureType(
        int ordinal,
        SqlColumn column,
        Type requestedType,
        MySqlType type1,
        MySqlType type2) =>
      EnsureTypeCore(ordinal, column, requestedType, type1, type2, type1, type1);

    private MySqlColumnMetadata EnsureType(
        int ordinal,
        SqlColumn column,
        Type requestedType,
        MySqlType type1,
        MySqlType type2,
        MySqlType type3) =>
      EnsureTypeCore(ordinal, column, requestedType, type1, type2, type3, type1);

    private MySqlColumnMetadata EnsureType(
        int ordinal,
        SqlColumn column,
        Type requestedType,
        MySqlType type1,
        MySqlType type2,
        MySqlType type3,
        MySqlType type4) =>
      EnsureTypeCore(ordinal, column, requestedType, type1, type2, type3, type4);

    private MySqlColumnMetadata EnsureTypeCore(
        int ordinal,
        SqlColumn column,
        Type requestedType,
        MySqlType type1,
        MySqlType type2,
        MySqlType type3,
        MySqlType type4)
    {
        var metadata = EnsureColumn(ordinal, column, requestedType);
        if (metadata.Type == MySqlType.Null)
        {
            return metadata;
        }

        if (metadata.Type != type1 &&
            metadata.Type != type2 &&
            metadata.Type != type3 &&
            metadata.Type != type4)
        {
            throw CannotRead(column, requestedType);
        }

        return metadata;
    }

    private MySqlColumnMetadata EnsureSignedType(
        int ordinal,
        SqlColumn column,
        Type requestedType,
        MySqlType type1) =>
      EnsureSignedType(ordinal, column, requestedType, type1, type1, type1);

    private MySqlColumnMetadata EnsureSignedType(
        int ordinal,
        SqlColumn column,
        Type requestedType,
        MySqlType type1,
        MySqlType type2) =>
      EnsureSignedType(ordinal, column, requestedType, type1, type2, type1);

    private MySqlColumnMetadata EnsureSignedType(
        int ordinal,
        SqlColumn column,
        Type requestedType,
        MySqlType type1,
        MySqlType type2,
        MySqlType type3)
    {
        var metadata =
          EnsureType(ordinal, column, requestedType, type1, type2, type3);
        if (metadata.IsUnsigned)
        {
            throw CannotRead(column, requestedType);
        }

        return metadata;
    }

    private MySqlColumnMetadata EnsureSignedIntegerType(
        int ordinal,
        SqlColumn column,
        Type requestedType)
    {
        var metadata =
          EnsureColumn(ordinal, column, requestedType);
        if (metadata.Type == MySqlType.Null)
        {
            return metadata;
        }

        if (metadata.IsUnsigned && metadata.Type != MySqlType.Year ||
            metadata.Type is not (
              MySqlType.Tiny or
              MySqlType.Short or
              MySqlType.Int24 or
              MySqlType.Long or
              MySqlType.LongLong or
              MySqlType.Year))
        {
            throw CannotRead(column, requestedType);
        }

        return metadata;
    }

    private MySqlColumnMetadata EnsureNumericType(
        int ordinal,
        SqlColumn column,
        Type requestedType)
    {
        var metadata =
          EnsureColumn(ordinal, column, requestedType);
        if (metadata.Type is not (
          MySqlType.Null or
          MySqlType.Tiny or
          MySqlType.Short or
          MySqlType.Int24 or
          MySqlType.Long or
          MySqlType.LongLong or
          MySqlType.Year or
          MySqlType.Bit or
          MySqlType.Float or
          MySqlType.Double or
          MySqlType.Decimal or
          MySqlType.NewDecimal))
        {
            throw CannotRead(column, requestedType);
        }

        return metadata;
    }

    private MySqlColumnMetadata EnsureDateType(
        int ordinal,
        SqlColumn column,
        Type requestedType)
    {
        var metadata =
          EnsureColumn(ordinal, column, requestedType);
        if (metadata.Type is not (
          MySqlType.Null or
          MySqlType.Date or
          MySqlType.NewDate or
          MySqlType.DateTime or
          MySqlType.DateTime2 or
          MySqlType.Timestamp or
          MySqlType.Timestamp2))
        {
            throw CannotRead(column, requestedType);
        }

        return metadata;
    }

    private MySqlColumnMetadata EnsureTimeType(
        int ordinal,
        SqlColumn column,
        Type requestedType)
    {
        var metadata =
          EnsureColumn(ordinal, column, requestedType);
        if (metadata.Type is not (
          MySqlType.Null or
          MySqlType.Time or
          MySqlType.Time2 or
          MySqlType.DateTime or
          MySqlType.DateTime2 or
          MySqlType.Timestamp or
          MySqlType.Timestamp2))
        {
            throw CannotRead(column, requestedType);
        }

        return metadata;
    }

    private MySqlColumnMetadata EnsureUnsignedType(
        int ordinal,
        SqlColumn column,
        Type requestedType,
        MySqlType type1) =>
      EnsureUnsignedType(ordinal, column, requestedType, type1, type1);

    private MySqlColumnMetadata EnsureUnsignedType(
        int ordinal,
        SqlColumn column,
        Type requestedType,
        MySqlType type1,
        MySqlType type2)
    {
        var metadata =
          EnsureType(ordinal, column, requestedType, type1, type2);
        if (metadata.Type == MySqlType.Null)
        {
            return metadata;
        }

        if (!metadata.IsUnsigned)
        {
            throw CannotRead(column, requestedType);
        }

        return metadata;
    }

    private static void EnsureStringType(
        MySqlColumnMetadata metadata,
        SqlColumn column,
        Type requestedType)
    {
        if (metadata.Type == MySqlType.Null)
        {
            return;
        }

        if (IsBinaryContent(metadata) || metadata.Type is not (
          MySqlType.VarChar or MySqlType.VarString or MySqlType.String or
          MySqlType.Enum or MySqlType.Set or MySqlType.Json or
          MySqlType.TinyBlob or MySqlType.Blob or MySqlType.MediumBlob or MySqlType.LongBlob))
        {
            throw CannotRead(column, requestedType);
        }
    }

    private static void EnsureBytesType(
        MySqlColumnMetadata metadata,
        SqlColumn column,
        Type requestedType)
    {
        if (metadata.Type == MySqlType.Null)
        {
            return;
        }

        if (!IsBinaryContent(metadata) &&
            metadata.Type is not (MySqlType.Bit or MySqlType.Geometry or MySqlType.Vector))
        {
            throw CannotRead(column, requestedType);
        }
    }

    private static void EnsureGuidType(
        MySqlColumnMetadata metadata,
        SqlColumn column,
        Type requestedType)
    {
        if (metadata.Type == MySqlType.Null)
        {
            return;
        }

        if (!IsBinaryContent(metadata) &&
            metadata.Type is not (MySqlType.VarChar or MySqlType.VarString or MySqlType.String))
        {
            throw CannotRead(column, requestedType);
        }
    }

    private static InvalidCastException CannotRead(SqlColumn column, Type requestedType) =>
      new(
        $"MySQL type 0x{column.TypeId:X2} ({column.Format}) cannot be read as " +
        $"{requestedType.FullName}.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TTo Cast<TFrom, TTo>(TFrom value) =>
      Unsafe.As<TFrom, TTo>(ref value);

    /// <summary>Locates one field inside a row payload of the bound protocol.</summary>
    internal ReadOnlySpan<byte> GetField(ReadOnlySpan<byte> row, int ordinal, out bool isNull)
    {
        if ((uint)ordinal >= (uint)_metadata.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        return _binary
          ? GetBinaryField(row, ordinal, out isNull)
          : GetTextField(row, ordinal, out isNull);
    }

    private static ReadOnlySpan<byte> GetTextField(ReadOnlySpan<byte> row, int ordinal, out bool isNull)
    {
        MySqlPayloadReader reader = new(row);
        for (var i = 0; i < ordinal; i++)
        {
            _ = reader.ReadLengthEncodedSpan(out _);
        }

        return reader.ReadLengthEncodedSpan(out isNull);
    }

    private ReadOnlySpan<byte> GetBinaryField(ReadOnlySpan<byte> row, int ordinal, out bool isNull)
    {
        if (row.Length < 1 + _nullBitmapLength || row[0] != MySqlProtocol.OkHeader)
        {
            throw new InvalidDataException("MySQL binary row header is invalid.");
        }

        var bitmap = row.Slice(1, _nullBitmapLength);
        if (IsNullInBitmap(bitmap, ordinal))
        {
            isNull = true;
            return default;
        }

        isNull = false;
        MySqlPayloadReader reader = new(row[(1 + _nullBitmapLength)..]);
        for (var i = 0; i < ordinal; i++)
        {
            if (IsNullInBitmap(bitmap, i))
            {
                continue;
            }

            SkipBinaryValue(ref reader, _metadata[i].Type);
        }

        return ReadBinaryValue(ref reader, _metadata[ordinal].Type);
    }

    private static bool IsNullInBitmap(ReadOnlySpan<byte> bitmap, int ordinal)
    {
        var bit = ordinal + 2;
        return (bitmap[bit >> 3] & (1 << (bit & 7))) != 0;
    }

    private static void SkipBinaryValue(scoped ref MySqlPayloadReader reader, MySqlType type) =>
      _ = ReadBinaryValue(ref reader, type);

    private static ReadOnlySpan<byte> ReadBinaryValue(
        scoped ref MySqlPayloadReader reader,
        MySqlType type)
    {
        switch (type)
        {
            case MySqlType.Tiny:
                return reader.ReadSpan(1);
            case MySqlType.Short:
            case MySqlType.Year:
                return reader.ReadSpan(2);
            case MySqlType.Int24:
            case MySqlType.Long:
            case MySqlType.Float:
                return reader.ReadSpan(4);
            case MySqlType.LongLong:
            case MySqlType.Double:
                return reader.ReadSpan(8);
            case MySqlType.Date:
            case MySqlType.NewDate:
                {
                    int length = reader.ReadByte();
                    if (length is not (0 or 4))
                    {
                        throw new InvalidDataException($"Invalid MySQL binary DATE length {length}.");
                    }

                    return reader.ReadSpan(length);
                }
            case MySqlType.DateTime:
            case MySqlType.DateTime2:
            case MySqlType.Timestamp:
            case MySqlType.Timestamp2:
                {
                    int length = reader.ReadByte();
                    if (length is not (0 or 4 or 7 or 11))
                    {
                        throw new InvalidDataException($"Invalid MySQL binary DATETIME length {length}.");
                    }

                    return reader.ReadSpan(length);
                }
            case MySqlType.Time:
            case MySqlType.Time2:
                {
                    int length = reader.ReadByte();
                    if (length is not (0 or 8 or 12))
                    {
                        throw new InvalidDataException($"Invalid MySQL binary TIME length {length}.");
                    }

                    return reader.ReadSpan(length);
                }
            case MySqlType.Null:
                return default;
            default:
                return reader.ReadLengthEncodedSpan(out _);
        }
    }

    private object? HandleZeroDate(bool dateOnly) =>
      _zeroDates switch
      {
          MySqlZeroDateBehavior.Null => null,
          MySqlZeroDateBehavior.MinValue => dateOnly ? DateOnly.MinValue : DateTime.MinValue,
          _ => throw new FormatException(
          "MySQL returned a zero date. Set MySqlConnectOptions.ZeroDateBehavior to read it."),
      };

    private object? DecodeDateObject(ReadOnlySpan<byte> value)
    {
        var date = DecodeDate(value, out _);
        return date is { } parsed ? parsed : HandleZeroDate(dateOnly: true);
    }

    private object? DecodeDateTimeObject(ReadOnlySpan<byte> value)
    {
        var timestamp = DecodeDateTime(value, out _);
        return timestamp is { } parsed ? parsed : HandleZeroDate(dateOnly: false);
    }

    private DateOnly ZeroDateOnly() =>
      HandleZeroDate(dateOnly: true) is DateOnly date
        ? date
        : throw new InvalidCastException("The column contains a zero date, which maps to NULL.");

    private DateTime ZeroDateTime() =>
      HandleZeroDate(dateOnly: false) is DateTime timestamp
        ? timestamp
        : throw new InvalidCastException("The column contains a zero date, which maps to NULL.");

    private DateOnly? DecodeDate(ReadOnlySpan<byte> value, out bool isZero)
    {
        if (_binary)
        {
            var timestamp = MySqlValueCodec.ReadBinaryDateTime(value, out isZero);
            return isZero ? null : DateOnly.FromDateTime(timestamp);
        }

        var date = MySqlValueCodec.ParseDate(value, out isZero);
        return isZero ? null : date;
    }

    private DateTime? DecodeDateTime(ReadOnlySpan<byte> value, out bool isZero)
    {
        var timestamp = _binary
          ? MySqlValueCodec.ReadBinaryDateTime(value, out isZero)
          : MySqlValueCodec.ParseDateTime(value, out isZero);
        return isZero ? null : timestamp;
    }

    private TimeSpan DecodeTime(ReadOnlySpan<byte> value) =>
      _binary ? MySqlValueCodec.ReadBinaryTime(value) : MySqlValueCodec.ParseTime(value);

    private long ReadInt64(ReadOnlySpan<byte> value, MySqlColumnMetadata metadata)
    {
        switch (metadata.Type)
        {
            case MySqlType.Tiny:
                return _binary
                  ? metadata.IsUnsigned ? value[0] : (sbyte)value[0]
                  : ParseInteger(value, metadata);
            case MySqlType.Short:
            case MySqlType.Year:
                return _binary
                  ? metadata.IsUnsigned
                    ? BinaryPrimitives.ReadUInt16LittleEndian(value)
                    : BinaryPrimitives.ReadInt16LittleEndian(value)
                  : ParseInteger(value, metadata);
            case MySqlType.Int24:
            case MySqlType.Long:
                return _binary
                  ? metadata.IsUnsigned
                    ? BinaryPrimitives.ReadUInt32LittleEndian(value)
                    : BinaryPrimitives.ReadInt32LittleEndian(value)
                  : ParseInteger(value, metadata);
            case MySqlType.LongLong:
                if (!_binary)
                {
                    return ParseInteger(value, metadata);
                }

                if (!metadata.IsUnsigned)
                {
                    return BinaryPrimitives.ReadInt64LittleEndian(value);
                }

                var unsigned = BinaryPrimitives.ReadUInt64LittleEndian(value);
                return unsigned <= long.MaxValue
                  ? (long)unsigned
                  : throw new OverflowException(
                    $"MySQL unsigned value {unsigned} does not fit in System.Int64.");
            case MySqlType.Bit:
                var bits = MySqlValueCodec.ParseBit(value);
                return bits <= long.MaxValue
                  ? (long)bits
                  : throw new OverflowException($"MySQL BIT value {bits} does not fit in System.Int64.");
            case MySqlType.Decimal:
            case MySqlType.NewDecimal:
                return checked((long)MySqlValueCodec.ParseDecimal(value));
            default:
                throw new InvalidCastException(
                  $"MySQL type 0x{(byte)metadata.Type:X2} cannot be read as an integer.");
        }
    }

    private ulong ReadUInt64(ReadOnlySpan<byte> value, MySqlColumnMetadata metadata)
    {
        if (metadata.Type is MySqlType.Bit)
        {
            return MySqlValueCodec.ParseBit(value);
        }

        if (metadata.Type is MySqlType.LongLong && metadata.IsUnsigned)
        {
            return _binary
              ? BinaryPrimitives.ReadUInt64LittleEndian(value)
              : MySqlValueCodec.ParseUInt64(value);
        }

        var signed = ReadInt64(value, metadata);
        return signed >= 0
          ? (ulong)signed
          : throw new OverflowException(
            $"MySQL value {signed} cannot be read as an unsigned integer.");
    }

    private static long ParseInteger(ReadOnlySpan<byte> value, MySqlColumnMetadata metadata)
    {
        if (!metadata.IsUnsigned)
        {
            return MySqlValueCodec.ParseInt64(value);
        }

        var unsigned = MySqlValueCodec.ParseUInt64(value);
        return unsigned <= long.MaxValue
          ? (long)unsigned
          : throw new OverflowException(
            $"MySQL unsigned value {unsigned} does not fit in System.Int64.");
    }

    private double ReadDouble(ReadOnlySpan<byte> value, MySqlColumnMetadata metadata) =>
      metadata.Type switch
      {
          MySqlType.Float => _binary
          ? BitConverter.Int32BitsToSingle(ReadBinaryInt32(value))
          : MySqlValueCodec.ParseSingle(value),
          MySqlType.Double => _binary
          ? BitConverter.Int64BitsToDouble(ReadBinaryInt64(value))
          : MySqlValueCodec.ParseDouble(value),
          MySqlType.Decimal or MySqlType.NewDecimal => (double)MySqlValueCodec.ParseDecimal(value),
          _ => metadata.IsUnsigned ? ReadUInt64(value, metadata) : ReadInt64(value, metadata),
      };

    private decimal ReadDecimal(ReadOnlySpan<byte> value, MySqlColumnMetadata metadata) =>
      metadata.Type switch
      {
          MySqlType.Decimal or MySqlType.NewDecimal => MySqlValueCodec.ParseDecimal(value),
          MySqlType.Float or MySqlType.Double => (decimal)ReadDouble(value, metadata),
          _ => metadata.IsUnsigned ? ReadUInt64(value, metadata) : ReadInt64(value, metadata),
      };

    private DateOnly ReadDateOnly(ReadOnlySpan<byte> value, MySqlColumnMetadata metadata)
    {
        switch (metadata.Type)
        {
            case MySqlType.Date:
            case MySqlType.NewDate:
                return DecodeDate(value, out _) ?? ZeroDateOnly();
            case MySqlType.DateTime:
            case MySqlType.DateTime2:
            case MySqlType.Timestamp:
            case MySqlType.Timestamp2:
                return DateOnly.FromDateTime(ReadDateTime(value, metadata));
            default:
                throw new InvalidCastException(
                  $"MySQL type 0x{(byte)metadata.Type:X2} cannot be read as a date.");
        }
    }

    private DateTime ReadDateTime(ReadOnlySpan<byte> value, MySqlColumnMetadata metadata)
    {
        switch (metadata.Type)
        {
            case MySqlType.Date:
            case MySqlType.NewDate:
                return DecodeDate(value, out _) is { } date
                  ? date.ToDateTime(default, DateTimeKind.Unspecified)
                  : ZeroDateTime();
            case MySqlType.DateTime:
            case MySqlType.DateTime2:
            case MySqlType.Timestamp:
            case MySqlType.Timestamp2:
                return DecodeDateTime(value, out _) ?? ZeroDateTime();
            default:
                throw new InvalidCastException(
                  $"MySQL type 0x{(byte)metadata.Type:X2} cannot be read as a date and time.");
        }
    }

    private TimeSpan ReadTimeSpan(ReadOnlySpan<byte> value, MySqlColumnMetadata metadata) =>
      metadata.Type is MySqlType.Time or MySqlType.Time2
        ? DecodeTime(value)
        : throw new InvalidCastException(
          $"MySQL type 0x{(byte)metadata.Type:X2} cannot be read as a duration.");

    private TimeOnly ReadTimeOnly(ReadOnlySpan<byte> value, MySqlColumnMetadata metadata)
    {
        if (metadata.Type is MySqlType.DateTime or MySqlType.DateTime2 or
            MySqlType.Timestamp or MySqlType.Timestamp2)
        {
            return TimeOnly.FromDateTime(ReadDateTime(value, metadata));
        }

        var time = ReadTimeSpan(value, metadata);
        return time >= TimeSpan.Zero && time < TimeSpan.FromDays(1)
          ? TimeOnly.FromTimeSpan(time)
          : throw new InvalidCastException(
            $"MySQL TIME value {time} is outside a single day and cannot be read as a time of day.");
    }

    private Guid ReadGuid(ReadOnlySpan<byte> value, MySqlColumnMetadata metadata)
    {
        if (IsBinaryContent(metadata) && value.Length == 16)
        {
            return new Guid(value, bigEndian: true);
        }

        Span<char> characters = stackalloc char[36];
        if (value.Length is 32 or 36 or 38)
        {
            var length = 0;
            foreach (var item in value)
            {
                if (length == characters.Length)
                {
                    break;
                }

                characters[length++] = (char)item;
            }

            if (Guid.TryParse(characters[..length], out var parsed))
            {
                return parsed;
            }
        }

        throw new InvalidCastException(
          $"MySQL type 0x{(byte)metadata.Type:X2} cannot be read as a GUID.");
    }

    private static JsonElement DecodeJson(ReadOnlySpan<byte> value)
    {
        using JsonDocument document = JsonDocument.Parse(value.ToArray());
        return document.RootElement.Clone();
    }

    private static T DecodeJsonValue<T>(ReadOnlySpan<byte> value, int ordinal)
    {
        var json = DecodeJson(value);
        if (typeof(T) == typeof(JsonElement))
        {
            return Cast<JsonElement, T>(json);
        }

        if (typeof(T) == typeof(object))
        {
            object decoded = json;
            return (T)decoded;
        }

        if (typeof(T) == typeof(bool))
        {
            var decoded = json.ValueKind is JsonValueKind.True or JsonValueKind.False
              ? json.GetBoolean()
              : throw JsonCastException<T>(ordinal);
            return Cast<bool, T>(decoded);
        }

        if (typeof(T) == typeof(bool?))
        {
            bool? decoded = json.ValueKind is JsonValueKind.True or JsonValueKind.False
              ? json.GetBoolean()
              : throw JsonCastException<T>(ordinal);
            return Cast<bool?, T>(decoded);
        }

        if (typeof(T) == typeof(int))
        {
            var decoded = json.TryGetInt32(out var parsed)
              ? parsed
              : throw JsonCastException<T>(ordinal);
            return Cast<int, T>(decoded);
        }

        if (typeof(T) == typeof(int?))
        {
            int? decoded = json.TryGetInt32(out var parsed)
              ? parsed
              : throw JsonCastException<T>(ordinal);
            return Cast<int?, T>(decoded);
        }

        if (typeof(T) == typeof(long))
        {
            var decoded = json.TryGetInt64(out var parsed)
              ? parsed
              : throw JsonCastException<T>(ordinal);
            return Cast<long, T>(decoded);
        }

        if (typeof(T) == typeof(long?))
        {
            long? decoded = json.TryGetInt64(out var parsed)
              ? parsed
              : throw JsonCastException<T>(ordinal);
            return Cast<long?, T>(decoded);
        }

        if (typeof(T) == typeof(float))
        {
            var decoded = json.TryGetSingle(out var parsed)
              ? parsed
              : throw JsonCastException<T>(ordinal);
            return Cast<float, T>(decoded);
        }

        if (typeof(T) == typeof(float?))
        {
            float? decoded = json.TryGetSingle(out var parsed)
              ? parsed
              : throw JsonCastException<T>(ordinal);
            return Cast<float?, T>(decoded);
        }

        if (typeof(T) == typeof(double))
        {
            var decoded = json.TryGetDouble(out var parsed)
              ? parsed
              : throw JsonCastException<T>(ordinal);
            return Cast<double, T>(decoded);
        }

        if (typeof(T) == typeof(double?))
        {
            double? decoded = json.TryGetDouble(out var parsed)
              ? parsed
              : throw JsonCastException<T>(ordinal);
            return Cast<double?, T>(decoded);
        }

        if (typeof(T) == typeof(decimal))
        {
            var decoded = json.TryGetDecimal(out var parsed)
              ? parsed
              : throw JsonCastException<T>(ordinal);
            return Cast<decimal, T>(decoded);
        }

        if (typeof(T) == typeof(decimal?))
        {
            decimal? decoded = json.TryGetDecimal(out var parsed)
              ? parsed
              : throw JsonCastException<T>(ordinal);
            return Cast<decimal?, T>(decoded);
        }

        if (typeof(T) == typeof(string))
        {
            var decoded = json.ValueKind == JsonValueKind.String
              ? json.GetString()!
              : json.GetRawText();
            return Cast<string, T>(decoded);
        }

        throw JsonCastException<T>(ordinal);
    }

    private static InvalidCastException JsonCastException<T>(int ordinal) =>
      new($"MySQL JSON column {ordinal} cannot be read as {typeof(T).FullName}.");

    private bool IsZeroTemporal(ReadOnlySpan<byte> value, MySqlType type)
    {
        if (type is not (
          MySqlType.Date or MySqlType.NewDate or MySqlType.DateTime or MySqlType.DateTime2 or
          MySqlType.Timestamp or MySqlType.Timestamp2))
        {
            return false;
        }

        return _binary
          ? value.IsEmpty ||
            value.Length >= 4 &&
            value[0] == 0 &&
            value[1] == 0 &&
            value[2] == 0 &&
            value[3] == 0
          : value.StartsWith("0000-00-00"u8);
    }

    private static int ReadBinaryInt32(ReadOnlySpan<byte> value) =>
      BinaryPrimitives.ReadInt32LittleEndian(value);

    private static long ReadBinaryInt64(ReadOnlySpan<byte> value) =>
      BinaryPrimitives.ReadInt64LittleEndian(value);

    private static bool IsBinaryContent(MySqlColumnMetadata metadata) =>
      metadata.Type is MySqlType.Geometry or MySqlType.Vector ||
      (metadata.IsBinary && metadata.Type is not (
        MySqlType.Decimal or MySqlType.NewDecimal or MySqlType.Json));

    private static class TypedDecoder<T>
    {
        internal static readonly TypedDecoderKind s_kind = ResolveTypedDecoder(typeof(T));
    }

    private static TypedDecoderKind ResolveTypedDecoder(Type type)
    {
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

        if (type == typeof(MySqlDecimal))
        {
            return TypedDecoderKind.MySqlDecimal;
        }

        if (type == typeof(MySqlDecimal?))
        {
            return TypedDecoderKind.NullableMySqlDecimal;
        }

        if (type == typeof(sbyte))
        {
            return TypedDecoderKind.SByte;
        }

        if (type == typeof(sbyte?))
        {
            return TypedDecoderKind.NullableSByte;
        }

        if (type == typeof(byte))
        {
            return TypedDecoderKind.Byte;
        }

        if (type == typeof(byte?))
        {
            return TypedDecoderKind.NullableByte;
        }

        if (type == typeof(ushort))
        {
            return TypedDecoderKind.UInt16;
        }

        if (type == typeof(ushort?))
        {
            return TypedDecoderKind.NullableUInt16;
        }

        if (type == typeof(uint))
        {
            return TypedDecoderKind.UInt32;
        }

        if (type == typeof(uint?))
        {
            return TypedDecoderKind.NullableUInt32;
        }

        if (type == typeof(ulong))
        {
            return TypedDecoderKind.UInt64;
        }

        if (type == typeof(ulong?))
        {
            return TypedDecoderKind.NullableUInt64;
        }

        if (type == typeof(TimeSpan))
        {
            return TypedDecoderKind.TimeSpan;
        }

        if (type == typeof(TimeSpan?))
        {
            return TypedDecoderKind.NullableTimeSpan;
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
        MySqlDecimal,
        NullableMySqlDecimal,
        SByte,
        NullableSByte,
        Byte,
        NullableByte,
        UInt16,
        NullableUInt16,
        UInt32,
        NullableUInt32,
        UInt64,
        NullableUInt64,
        TimeSpan,
        NullableTimeSpan,
        Half,
        NullableHalf,
        BigInteger,
        NullableBigInteger,
        Int128,
        NullableInt128,
        UInt128,
        NullableUInt128,
        Char,
        NullableChar,
        Chars,
        IPAddress,
        PhysicalAddress,
        BitArray,
    }
}

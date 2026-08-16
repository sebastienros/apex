using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Apex.SqlClient;

namespace Apex.PgClient.Internal;

internal static class PgParameterEncoder
{
    private static readonly DateOnly s_dateEpoch = new(2000, 1, 1);
    private static readonly DateTime s_timestampEpoch =
        new(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTimeOffset s_timestampTzEpoch =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static PgParameterFormat ResolveFormat(
        PgParameter parameter,
        PgTypeRegistry? typeRegistry = null)
    {
        if (parameter.Format != PgParameterFormat.Auto)
        {
            if (parameter.Format == PgParameterFormat.Binary &&
                !parameter.Value.IsNull &&
                !CanEncodeBinary(parameter.Type.Oid, parameter.Value, typeRegistry))
            {
                throw new NotSupportedException(
                    $"PostgreSQL type {parameter.Type.Name} cannot be encoded in binary format.");
            }

            return parameter.Format;
        }

        return parameter.Value.IsNull ||
               CanEncodeBinary(parameter.Type.Oid, parameter.Value, typeRegistry)
          ? PgParameterFormat.Binary
          : PgParameterFormat.Text;
    }

    public static byte[] Encode(
        PgParameter parameter,
        PgParameterFormat format,
        PgTypeRegistry? typeRegistry = null) =>
        format == PgParameterFormat.Binary
          ? EncodeBinary(parameter.Type.Oid, parameter.Value, typeRegistry)
          : Encoding.UTF8.GetBytes(PgTextCodec.FormatParameter(parameter.Value));

    private static bool CanEncodeBinary(
        uint oid,
        Apex.SqlClient.SqlValue value,
        PgTypeRegistry? typeRegistry) =>
        oid switch
        {
            16 => value.Kind == SqlValueKind.Boolean,
            17 => value.Kind is SqlValueKind.Bytes or SqlValueKind.ReadOnlyMemory,
            20 => value.Kind == SqlValueKind.Int64,
            21 => value.Kind == SqlValueKind.Int16,
            23 => value.Kind == SqlValueKind.Int32,
            25 or 1043 => value.Kind == SqlValueKind.String,
            700 => value.Kind == SqlValueKind.Single,
            701 => value.Kind == SqlValueKind.Double,
            1082 => value.Kind == SqlValueKind.DateOnly,
            1083 => value.Kind == SqlValueKind.TimeOnly,
            1114 => value.Kind == SqlValueKind.DateTime,
            1184 => value.Kind is SqlValueKind.DateTimeOffset or SqlValueKind.DateTime,
            2950 => value.Kind == SqlValueKind.Guid,
            114 or 3802 =>
                value.Kind is SqlValueKind.String or SqlValueKind.Bytes or
                    SqlValueKind.ReadOnlyMemory or SqlValueKind.JsonDocument or
                    SqlValueKind.JsonElement,
            _ when TryGetArrayElementOid(oid, out var elementOid) =>
                CanEncodeArray(elementOid, value, typeRegistry),
            _ => value.ToObject() is { } instance &&
                 typeRegistry is not null &&
                 typeRegistry.CanEncode(oid, instance),
        };

    private static byte[] EncodeBinary(
        uint oid,
        Apex.SqlClient.SqlValue value,
        PgTypeRegistry? typeRegistry) =>
        oid switch
        {
            16 => [value.Get<bool>() ? (byte)1 : (byte)0],
            17 => value.ToObject() switch
            {
                byte[] bytes => (byte[])bytes.Clone(),
                ReadOnlyMemory<byte> memory => memory.ToArray(),
                _ => Throw<byte[]>(oid, value),
            },
            20 => WriteInt64(value.Get<long>()),
            21 => WriteInt16(value.Get<short>()),
            23 => WriteInt32(value.Get<int>()),
            25 or 1043 => Encoding.UTF8.GetBytes(value.GetRequired<string>()),
            700 => WriteInt32(BitConverter.SingleToInt32Bits(value.Get<float>())),
            701 => WriteInt64(BitConverter.DoubleToInt64Bits(value.Get<double>())),
            1082 => WriteInt32(value.Get<DateOnly>().DayNumber - s_dateEpoch.DayNumber),
            1083 => WriteInt64(value.Get<TimeOnly>().Ticks / 10),
            1114 => WriteInt64(
                (value.Get<DateTime>().Ticks - s_timestampEpoch.Ticks) / 10),
            1184 => WriteInt64(ToTimestampTz(value)),
            2950 => EncodeGuid(value.Get<Guid>()),
            114 => Encoding.UTF8.GetBytes(GetJson(value)),
            3802 => EncodeJsonb(value),
            _ when TryGetArrayElementOid(oid, out var elementOid) =>
                EncodeArray(elementOid, value, typeRegistry),
            _ => EncodeCustom(oid, value, typeRegistry),
        };

    private static byte[] EncodeCustom(
        uint oid,
        Apex.SqlClient.SqlValue value,
        PgTypeRegistry? typeRegistry)
    {
        if (value.ToObject() is { } instance &&
            typeRegistry is not null &&
            typeRegistry.TryEncode(oid, instance, out var payload))
        {
            return payload;
        }

        throw new NotSupportedException(
            $"PostgreSQL type OID {oid} cannot be encoded in binary format.");
    }

    private static long ToTimestampTz(Apex.SqlClient.SqlValue value)
    {
        DateTimeOffset timestamp = value.Kind switch
        {
            SqlValueKind.DateTimeOffset => value.Get<DateTimeOffset>(),
            SqlValueKind.DateTime when value.Get<DateTime>().Kind == DateTimeKind.Utc =>
                new DateTimeOffset(value.Get<DateTime>()),
            SqlValueKind.DateTime =>
                new DateTimeOffset(value.Get<DateTime>().ToUniversalTime()),
            _ => Throw<DateTimeOffset>(1184, value),
        };
        return (timestamp.UtcTicks - s_timestampTzEpoch.UtcTicks) / 10;
    }

    private static byte[] EncodeGuid(Guid value)
    {
        var bytes = new byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes;
    }

    private static byte[] EncodeJsonb(Apex.SqlClient.SqlValue value)
    {
        var json = GetJsonBytes(value);
        var result = new byte[json.Length + 1];
        result[0] = 1;
        json.CopyTo(result, 1);
        return result;
    }

    private static string GetJson(Apex.SqlClient.SqlValue value) =>
        value.ToObject() switch
        {
            string json => json,
            byte[] json => Encoding.UTF8.GetString(json),
            ReadOnlyMemory<byte> json => Encoding.UTF8.GetString(json.Span),
            JsonDocument document => document.RootElement.GetRawText(),
            JsonElement element => element.GetRawText(),
            _ => Throw<string>(3802, value),
        };

    private static byte[] GetJsonBytes(Apex.SqlClient.SqlValue value) =>
        value.ToObject() switch
        {
            byte[] json => (byte[])json.Clone(),
            ReadOnlyMemory<byte> json => json.ToArray(),
            _ => Encoding.UTF8.GetBytes(GetJson(value)),
        };

    private static bool CanEncodeArray(
        uint elementOid,
        Apex.SqlClient.SqlValue value,
        PgTypeRegistry? typeRegistry)
    {
        if (value.ToObject() is not Array array || array.Rank != 1)
        {
            return false;
        }

        foreach (var element in array)
        {
            if (element is not null &&
                !CanEncodeBinary(elementOid, Apex.SqlClient.SqlValue.From(element), typeRegistry))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] EncodeArray(
        uint elementOid,
        Apex.SqlClient.SqlValue value,
        PgTypeRegistry? typeRegistry)
    {
        var array = value.ToObject() as Array ??
            throw new InvalidCastException("A PostgreSQL array parameter requires a one-dimensional CLR array.");
        if (array.Rank != 1)
        {
            throw new NotSupportedException("Multidimensional PostgreSQL arrays are not supported.");
        }

        var headerLength = array.Length == 0 ? 12 : 20;
        var elements = new byte[]?[array.Length];
        var length = headerLength;
        var hasNull = false;
        for (var i = 0; i < array.Length; i++)
        {
            var element = array.GetValue(i);
            if (element is null)
            {
                hasNull = true;
                length = checked(length + sizeof(int));
                continue;
            }

            elements[i] = EncodeBinary(
                elementOid,
                Apex.SqlClient.SqlValue.From(element),
                typeRegistry);
            length = checked(length + sizeof(int) + elements[i]!.Length);
        }

        var result = new byte[length];
        var position = 0;
        BinaryPrimitives.WriteInt32BigEndian(
            result.AsSpan(position),
            array.Length == 0 ? 0 : 1);
        position += sizeof(int);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(position), hasNull ? 1 : 0);
        position += sizeof(int);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(position), checked((int)elementOid));
        position += sizeof(int);
        if (array.Length != 0)
        {
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(position), array.Length);
            position += sizeof(int);
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(position), 1);
            position += sizeof(int);
        }

        foreach (var element in elements)
        {
            if (element is null)
            {
                BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(position), -1);
                position += sizeof(int);
                continue;
            }

            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(position), element.Length);
            position += sizeof(int);
            element.CopyTo(result, position);
            position += element.Length;
        }

        return result;
    }

    private static bool TryGetArrayElementOid(uint arrayOid, out uint elementOid)
    {
        elementOid = arrayOid switch
        {
            1000 => 16,
            1001 => 17,
            1005 => 21,
            1007 => 23,
            1009 => 25,
            1015 => 1043,
            1016 => 20,
            1021 => 700,
            1022 => 701,
            1115 => 1114,
            1182 => 1082,
            1183 => 1083,
            1185 => 1184,
            199 => 114,
            2951 => 2950,
            3807 => 3802,
            _ => 0,
        };
        return elementOid != 0;
    }

    private static byte[] WriteInt16(short value)
    {
        var bytes = new byte[sizeof(short)];
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] WriteInt32(int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] WriteInt64(long value)
    {
        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }

    private static T Throw<T>(uint oid, Apex.SqlClient.SqlValue value) =>
        throw new InvalidCastException(
            $"Value {value.ToObject()?.GetType().FullName ?? "NULL"} cannot be encoded as PostgreSQL type OID {oid}.");
}

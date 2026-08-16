using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Apex.SqlClient;

namespace Apex.MsSqlClient.Internal;

internal static class TdsRequestWriter
{
    internal static ReadOnlyMemory<byte> BuildSqlBatch(
        string sql,
        long transactionDescriptor)
    {
        ArrayBufferWriter<byte> payload = new();
        WriteAllHeaders(payload, transactionDescriptor);
        payload.WriteUtf16(sql);
        return payload.WrittenMemory;
    }

    internal static ReadOnlyMemory<byte> BuildExecuteSql(
        string sql,
        SqlParameters parameters,
        long transactionDescriptor)
    {
        ArrayBufferWriter<byte> payload = new();
        WriteAllHeaders(payload, transactionDescriptor);
        payload.WriteUInt16LittleEndian(ushort.MaxValue);
        payload.WriteUInt16LittleEndian(TdsProcedureId.ExecuteSql);
        payload.WriteUInt16LittleEndian(0);

        WriteNVarCharParameter(payload, string.Empty, sql);
        WriteNVarCharParameter(payload, string.Empty, BuildDefinitions(parameters));
        for (var i = 0; i < parameters.Count; i++)
        {
            WriteParameter(
              payload,
              "@P" + (i + 1).ToString(CultureInfo.InvariantCulture),
              parameters[i]);
        }

        return payload.WrittenMemory;
    }

    internal static ReadOnlyMemory<byte> BuildPrepareExecute(
        string sql,
        SqlParameters parameters,
        long transactionDescriptor)
    {
        ArrayBufferWriter<byte> payload = new();
        WriteRpcHeader(payload, TdsProcedureId.PrepExec, transactionDescriptor);
        WriteIntParameter(payload, string.Empty, 0, output: true);
        WriteNVarCharParameter(payload, string.Empty, BuildDefinitions(parameters));
        WriteNVarCharParameter(payload, string.Empty, sql);
        WriteParameters(payload, parameters);
        return payload.WrittenMemory;
    }

    internal static ReadOnlyMemory<byte> BuildExecute(
        int handle,
        SqlParameters parameters,
        long transactionDescriptor)
    {
        ArrayBufferWriter<byte> payload = new();
        WriteRpcHeader(payload, TdsProcedureId.Execute, transactionDescriptor);
        WriteIntParameter(payload, string.Empty, handle, output: true);
        WriteParameters(payload, parameters);
        return payload.WrittenMemory;
    }

    internal static ReadOnlyMemory<byte> BuildUnprepare(
        int handle,
        long transactionDescriptor)
    {
        ArrayBufferWriter<byte> payload = new();
        WriteRpcHeader(payload, TdsProcedureId.Unprepare, transactionDescriptor);
        WriteIntParameter(payload, string.Empty, handle, output: false);
        return payload.WrittenMemory;
    }

    internal static string BuildDefinitions(SqlParameters parameters)
    {
        StringBuilder definitions = new();
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                definitions.Append(',');
            }

            var value = parameters[i];
            definitions.Append("@P").Append(i + 1).Append(' ').Append(GetDefinition(value));
        }

        return definitions.ToString();
    }

    private static void WriteParameters(
        ArrayBufferWriter<byte> payload,
        SqlParameters parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            WriteParameter(
              payload,
              "@P" + (i + 1).ToString(CultureInfo.InvariantCulture),
              parameters[i]);
        }
    }

    private static string GetDefinition(SqlValue value) =>
      value.Kind switch
      {
          SqlValueKind.Null => "nvarchar(4000)",
          SqlValueKind.Boolean => "bit",
          SqlValueKind.Int16 => "smallint",
          SqlValueKind.Int32 => "int",
          SqlValueKind.Int64 => "bigint",
          SqlValueKind.Single => "real",
          SqlValueKind.Double => "float",
          SqlValueKind.Decimal => $"numeric(38,{GetDecimalScale(value.GetRequired<decimal>())})",
          SqlValueKind.String =>
          Encoding.Unicode.GetByteCount(value.GetRequired<string>()) > 8000
            ? "nvarchar(max)"
            : "nvarchar(4000)",
          SqlValueKind.Bytes =>
          value.GetRequired<byte[]>().Length > 8000
            ? "varbinary(max)"
            : "varbinary(8000)",
          SqlValueKind.ReadOnlyMemory =>
          value.GetRequired<ReadOnlyMemory<byte>>().Length > 8000
            ? "varbinary(max)"
            : "varbinary(8000)",
          SqlValueKind.Guid => "uniqueidentifier",
          SqlValueKind.DateOnly => "date",
          SqlValueKind.TimeOnly => "time(7)",
          SqlValueKind.DateTime => "datetime2(7)",
          SqlValueKind.DateTimeOffset => "datetimeoffset(7)",
          SqlValueKind.JsonDocument or SqlValueKind.JsonElement => "nvarchar(max)",
          SqlValueKind.Object when value.ToObject() is byte => "tinyint",
          SqlValueKind.Object when value.ToObject() is sbyte => "smallint",
          SqlValueKind.Object when value.ToObject() is Half => "real",
          SqlValueKind.Object when value.ToObject() is BigInteger => "numeric(38,0)",
          SqlValueKind.Object when value.ToObject() is Int128 or UInt128 => "numeric(38,0)",
          SqlValueKind.Object when value.ToObject() is TimeSpan => "time(7)",
          SqlValueKind.Object when value.ToObject() is char or char[] or IPAddress or BitArray =>
          "nvarchar(4000)",
          SqlValueKind.Object when value.ToObject() is PhysicalAddress => "varbinary(8000)",
          _ => throw UnsupportedParameter(value),
      };

    private static void WriteParameter(
        ArrayBufferWriter<byte> payload,
        string name,
        SqlValue value)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Null:
                WriteNVarCharParameter(payload, name, null);
                break;
            case SqlValueKind.Boolean:
                WriteHeader(payload, name, TdsDataType.BitN);
                payload.WriteByte(1);
                payload.WriteByte(1);
                payload.WriteByte(value.GetRequired<bool>() ? (byte)1 : (byte)0);
                break;
            case SqlValueKind.Int16:
                WriteHeader(payload, name, TdsDataType.IntN);
                payload.WriteByte(2);
                payload.WriteByte(2);
                payload.WriteInt16LittleEndian(value.GetRequired<short>());
                break;
            case SqlValueKind.Int32:
                WriteHeader(payload, name, TdsDataType.IntN);
                payload.WriteByte(4);
                payload.WriteByte(4);
                payload.WriteInt32LittleEndian(value.GetRequired<int>());
                break;
            case SqlValueKind.Int64:
                WriteHeader(payload, name, TdsDataType.IntN);
                payload.WriteByte(8);
                payload.WriteByte(8);
                payload.WriteInt64LittleEndian(value.GetRequired<long>());
                break;
            case SqlValueKind.Single:
                WriteFloatingPoint(
                  payload,
                  name,
                  BitConverter.SingleToInt32Bits(value.GetRequired<float>()));
                break;
            case SqlValueKind.Double:
                WriteFloatingPoint(
                  payload,
                  name,
                  BitConverter.DoubleToInt64Bits(value.GetRequired<double>()));
                break;
            case SqlValueKind.Decimal:
                WriteDecimal(payload, name, value.GetRequired<decimal>());
                break;
            case SqlValueKind.String:
                WriteNVarCharParameter(payload, name, value.GetRequired<string>());
                break;
            case SqlValueKind.Bytes:
                WriteVarBinaryParameter(payload, name, value.GetRequired<byte[]>());
                break;
            case SqlValueKind.ReadOnlyMemory:
                WriteVarBinaryParameter(
                  payload,
                  name,
                  value.GetRequired<ReadOnlyMemory<byte>>().Span);
                break;
            case SqlValueKind.Guid:
                WriteGuid(payload, name, value.GetRequired<Guid>());
                break;
            case SqlValueKind.DateOnly:
                WriteDate(payload, name, value.GetRequired<DateOnly>());
                break;
            case SqlValueKind.TimeOnly:
                WriteTime(payload, name, value.GetRequired<TimeOnly>());
                break;
            case SqlValueKind.DateTime:
                WriteDateTime(payload, name, value.GetRequired<DateTime>());
                break;
            case SqlValueKind.DateTimeOffset:
                WriteDateTimeOffset(payload, name, value.GetRequired<DateTimeOffset>());
                break;
            case SqlValueKind.JsonDocument:
                WriteNVarCharParameter(
                  payload,
                  name,
                  value.GetRequired<JsonDocument>().RootElement.GetRawText());
                break;
            case SqlValueKind.JsonElement:
                WriteNVarCharParameter(
                  payload,
                  name,
                  value.GetRequired<JsonElement>().GetRawText());
                break;
            case SqlValueKind.Object when value.ToObject() is byte byteValue:
                WriteHeader(payload, name, TdsDataType.IntN);
                payload.WriteByte(1);
                payload.WriteByte(1);
                payload.WriteByte(byteValue);
                break;
            case SqlValueKind.Object when value.ToObject() is sbyte signedByte:
                WriteHeader(payload, name, TdsDataType.IntN);
                payload.WriteByte(2);
                payload.WriteByte(2);
                payload.WriteInt16LittleEndian(signedByte);
                break;
            case SqlValueKind.Object when value.ToObject() is Half half:
                WriteFloatingPoint(payload, name, BitConverter.SingleToInt32Bits((float)half));
                break;
            case SqlValueKind.Object when value.ToObject() is BigInteger integer:
                WriteBigInteger(payload, name, integer);
                break;
            case SqlValueKind.Object when value.ToObject() is Int128 integer:
                WriteBigInteger(payload, name, BigInteger.CreateChecked(integer));
                break;
            case SqlValueKind.Object when value.ToObject() is UInt128 integer:
                WriteBigInteger(payload, name, BigInteger.CreateChecked(integer));
                break;
            case SqlValueKind.Object when value.ToObject() is TimeSpan duration:
                WriteTime(payload, name, duration);
                break;
            case SqlValueKind.Object when value.ToObject() is char character:
                WriteNVarCharParameter(payload, name, character.ToString());
                break;
            case SqlValueKind.Object when value.ToObject() is char[] characters:
                WriteNVarCharParameter(payload, name, new string(characters));
                break;
            case SqlValueKind.Object when value.ToObject() is IPAddress address:
                WriteNVarCharParameter(payload, name, address.ToString());
                break;
            case SqlValueKind.Object when value.ToObject() is PhysicalAddress address:
                WriteVarBinaryParameter(payload, name, address.GetAddressBytes());
                break;
            case SqlValueKind.Object when value.ToObject() is BitArray bits:
                WriteNVarCharParameter(payload, name, FormatBits(bits));
                break;
            default:
                throw UnsupportedParameter(value);
        }
    }

    private static void WriteAllHeaders(
        ArrayBufferWriter<byte> payload,
        long transactionDescriptor)
    {
        payload.WriteInt32LittleEndian(22);
        payload.WriteInt32LittleEndian(18);
        payload.WriteUInt16LittleEndian(2);
        payload.WriteInt64LittleEndian(transactionDescriptor);
        payload.WriteInt32LittleEndian(1);
    }

    private static void WriteRpcHeader(
        ArrayBufferWriter<byte> payload,
        ushort procedureId,
        long transactionDescriptor)
    {
        WriteAllHeaders(payload, transactionDescriptor);
        payload.WriteUInt16LittleEndian(ushort.MaxValue);
        payload.WriteUInt16LittleEndian(procedureId);
        payload.WriteUInt16LittleEndian(0);
    }

    private static void WriteHeader(
        ArrayBufferWriter<byte> payload,
        string name,
        byte type,
        bool output = false)
    {
        payload.WriteBVarChar(name);
        payload.WriteByte(output ? (byte)1 : (byte)0);
        payload.WriteByte(type);
    }

    private static void WriteNVarCharParameter(
        ArrayBufferWriter<byte> payload,
        string name,
        string? value)
    {
        WriteHeader(payload, name, TdsDataType.NVarChar);
        if (value is null)
        {
            payload.WriteUInt16LittleEndian(8000);
            WriteCollation(payload);
            payload.WriteUInt16LittleEndian(ushort.MaxValue);
            return;
        }

        var byteCount = Encoding.Unicode.GetByteCount(value);
        if (byteCount > 8000)
        {
            payload.WriteUInt16LittleEndian(ushort.MaxValue);
            WriteCollation(payload);
            payload.WriteInt64LittleEndian(byteCount);
            payload.WriteInt32LittleEndian(byteCount);
            payload.WriteUtf16(value);
            payload.WriteInt32LittleEndian(0);
        }
        else
        {
            payload.WriteUInt16LittleEndian(8000);
            WriteCollation(payload);
            payload.WriteUInt16LittleEndian(checked((ushort)byteCount));
            payload.WriteUtf16(value);
        }
    }

    private static void WriteIntParameter(
        ArrayBufferWriter<byte> payload,
        string name,
        int value,
        bool output)
    {
        WriteHeader(payload, name, TdsDataType.IntN, output);
        payload.WriteByte(sizeof(int));
        payload.WriteByte(sizeof(int));
        payload.WriteInt32LittleEndian(value);
    }

    private static void WriteVarBinaryParameter(
        ArrayBufferWriter<byte> payload,
        string name,
        ReadOnlySpan<byte> value)
    {
        WriteHeader(payload, name, TdsDataType.BigVarBinary);
        if (value.Length > 8000)
        {
            payload.WriteUInt16LittleEndian(ushort.MaxValue);
            payload.WriteInt64LittleEndian(value.Length);
            payload.WriteInt32LittleEndian(value.Length);
            payload.Write(value);
            payload.WriteInt32LittleEndian(0);
        }
        else
        {
            payload.WriteUInt16LittleEndian(8000);
            payload.WriteUInt16LittleEndian(checked((ushort)value.Length));
            payload.Write(value);
        }
    }

    private static void WriteFloatingPoint(
        ArrayBufferWriter<byte> payload,
        string name,
        int bits)
    {
        WriteHeader(payload, name, TdsDataType.FloatN);
        payload.WriteByte(4);
        payload.WriteByte(4);
        payload.WriteInt32LittleEndian(bits);
    }

    private static void WriteFloatingPoint(
        ArrayBufferWriter<byte> payload,
        string name,
        long bits)
    {
        WriteHeader(payload, name, TdsDataType.FloatN);
        payload.WriteByte(8);
        payload.WriteByte(8);
        payload.WriteInt64LittleEndian(bits);
    }

    private static void WriteDecimal(
        ArrayBufferWriter<byte> payload,
        string name,
        decimal value)
    {
        var bits = decimal.GetBits(value);
        var scale = GetDecimalScale(value);
        WriteHeader(payload, name, TdsDataType.DecimalN);
        payload.WriteByte(17);
        payload.WriteByte(38);
        payload.WriteByte(scale);
        payload.WriteByte(13);
        payload.WriteByte(value < 0 ? (byte)0 : (byte)1);
        payload.WriteInt32LittleEndian(bits[0]);
        payload.WriteInt32LittleEndian(bits[1]);
        payload.WriteInt32LittleEndian(bits[2]);
    }

    private static void WriteBigInteger(
        ArrayBufferWriter<byte> payload,
        string name,
        BigInteger value)
    {
        if (BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture).Length > 38)
        {
            throw new OverflowException("SQL Server numeric parameters support at most 38 digits.");
        }

        Span<byte> magnitude = stackalloc byte[16];
        _ = BigInteger.Abs(value).TryWriteBytes(
          magnitude,
          out _,
          isUnsigned: true,
          isBigEndian: false);
        WriteHeader(payload, name, TdsDataType.DecimalN);
        payload.WriteByte(17);
        payload.WriteByte(38);
        payload.WriteByte(0);
        payload.WriteByte(17);
        payload.WriteByte(value.Sign < 0 ? (byte)0 : (byte)1);
        payload.Write(magnitude);
    }

    private static void WriteGuid(
        ArrayBufferWriter<byte> payload,
        string name,
        Guid value)
    {
        WriteHeader(payload, name, TdsDataType.Guid);
        payload.WriteByte(16);
        payload.WriteByte(16);
        Span<byte> bytes = stackalloc byte[16];
        _ = value.TryWriteBytes(bytes);
        payload.Write(bytes);
    }

    private static void WriteDate(
        ArrayBufferWriter<byte> payload,
        string name,
        DateOnly value)
    {
        WriteHeader(payload, name, TdsDataType.Date);
        payload.WriteByte(3);
        payload.WriteUInt24LittleEndian(value.DayNumber);
    }

    private static void WriteTime(
        ArrayBufferWriter<byte> payload,
        string name,
        TimeOnly value)
    {
        WriteHeader(payload, name, TdsDataType.Time);
        payload.WriteByte(7);
        payload.WriteByte(5);
        payload.WriteUInt40LittleEndian(value.Ticks);
    }

    private static void WriteTime(
        ArrayBufferWriter<byte> payload,
        string name,
        TimeSpan value)
    {
        if (value < TimeSpan.Zero || value >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
              nameof(value),
              "SQL Server time parameters must be between 00:00:00 and 24:00:00.");
        }

        WriteHeader(payload, name, TdsDataType.Time);
        payload.WriteByte(7);
        payload.WriteByte(5);
        payload.WriteUInt40LittleEndian(value.Ticks);
    }

    private static void WriteDateTime(
        ArrayBufferWriter<byte> payload,
        string name,
        DateTime value)
    {
        WriteHeader(payload, name, TdsDataType.DateTime2);
        payload.WriteByte(7);
        payload.WriteByte(8);
        payload.WriteUInt40LittleEndian(value.TimeOfDay.Ticks);
        payload.WriteUInt24LittleEndian(DateOnly.FromDateTime(value).DayNumber);
    }

    private static void WriteDateTimeOffset(
        ArrayBufferWriter<byte> payload,
        string name,
        DateTimeOffset value)
    {
        WriteHeader(payload, name, TdsDataType.DateTimeOffset);
        payload.WriteByte(7);
        payload.WriteByte(10);
        var utc = value.UtcDateTime;
        payload.WriteUInt40LittleEndian(utc.TimeOfDay.Ticks);
        payload.WriteUInt24LittleEndian(DateOnly.FromDateTime(utc).DayNumber);
        payload.WriteInt16LittleEndian(checked((short)value.Offset.TotalMinutes));
    }

    private static byte GetDecimalScale(decimal value) =>
      (byte)((decimal.GetBits(value)[3] >> 16) & 0x7F);

    private static string FormatBits(BitArray bits)
    {
        var characters = new char[bits.Count];
        for (var i = 0; i < bits.Count; i++)
        {
            characters[i] = bits[i] ? '1' : '0';
        }

        return new string(characters);
    }

    private static void WriteCollation(ArrayBufferWriter<byte> payload)
    {
        ReadOnlySpan<byte> collation = [0x09, 0x04, 0xD0, 0x00, 0x34];
        payload.Write(collation);
    }

    private static NotSupportedException UnsupportedParameter(SqlValue value) =>
      new($"SQL Server parameter kind {value.Kind} is not supported.");
}

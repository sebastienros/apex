using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text.Json;
using Apex.SqlClient;

namespace Apex.MsSqlClient.Internal;

// Request builders write into a caller-owned buffer so the connection can reuse one buffer for
// every request. The Build* overloads that return a new payload are kept for tests.
internal static class TdsRequestWriter
{
    private const string DecimalDefinitionPrefix = "numeric(38,";
    private const int StackDefinitionCharacters = 256;

    internal static ReadOnlyMemory<byte> BuildSqlBatch(
        string sql,
        long transactionDescriptor)
    {
        ArrayBufferWriter<byte> payload = new();
        WriteSqlBatch(payload, sql, transactionDescriptor);
        return payload.WrittenMemory;
    }

    internal static void WriteSqlBatch(
        IBufferWriter<byte> payload,
        string sql,
        long transactionDescriptor)
    {
        WriteAllHeaders(payload, transactionDescriptor);
        payload.WriteUtf16(sql);
    }

    internal static ReadOnlyMemory<byte> BuildExecuteSql(
        string sql,
        SqlParameters parameters,
        long transactionDescriptor)
    {
        ArrayBufferWriter<byte> payload = new();
        WriteExecuteSql(payload, sql, parameters, transactionDescriptor);
        return payload.WrittenMemory;
    }

    internal static void WriteExecuteSql(
        IBufferWriter<byte> payload,
        string sql,
        SqlParameters parameters,
        long transactionDescriptor)
    {
        WriteRpcHeader(payload, TdsProcedureId.ExecuteSql, transactionDescriptor);
        WriteNVarCharParameter(payload, ordinal: 0, sql);
        WriteDefinitionsParameter(payload, parameters);
        WriteParameters(payload, parameters);
    }

    internal static ReadOnlyMemory<byte> BuildPrepareExecute(
        string sql,
        SqlParameters parameters,
        long transactionDescriptor) =>
        BuildPrepareExecute(sql, BuildDefinitions(parameters), parameters, transactionDescriptor);

    internal static ReadOnlyMemory<byte> BuildPrepareExecute(
        string sql,
        string definitions,
        SqlParameters parameters,
        long transactionDescriptor)
    {
        ArrayBufferWriter<byte> payload = new();
        WritePrepareExecute(payload, sql, definitions, parameters, transactionDescriptor);
        return payload.WrittenMemory;
    }

    internal static void WritePrepareExecute(
        IBufferWriter<byte> payload,
        string sql,
        string definitions,
        SqlParameters parameters,
        long transactionDescriptor)
    {
        WriteRpcHeader(payload, TdsProcedureId.PrepExec, transactionDescriptor);
        WriteIntParameter(payload, ordinal: 0, 0, output: true);
        WriteNVarCharParameter(payload, ordinal: 0, definitions);
        WriteNVarCharParameter(payload, ordinal: 0, sql);
        WriteParameters(payload, parameters);
    }

    internal static ReadOnlyMemory<byte> BuildExecute(
        int handle,
        SqlParameters parameters,
        long transactionDescriptor)
    {
        ArrayBufferWriter<byte> payload = new();
        WriteExecute(payload, handle, parameters, transactionDescriptor);
        return payload.WrittenMemory;
    }

    internal static void WriteExecute(
        IBufferWriter<byte> payload,
        int handle,
        SqlParameters parameters,
        long transactionDescriptor)
    {
        WriteRpcHeader(payload, TdsProcedureId.Execute, transactionDescriptor);
        WriteIntParameter(payload, ordinal: 0, handle, output: true);
        WriteParameters(payload, parameters);
    }

    internal static ReadOnlyMemory<byte> BuildUnprepare(
        int handle,
        long transactionDescriptor)
    {
        ArrayBufferWriter<byte> payload = new();
        WriteUnprepare(payload, handle, transactionDescriptor);
        return payload.WrittenMemory;
    }

    internal static void WriteUnprepare(
        IBufferWriter<byte> payload,
        int handle,
        long transactionDescriptor)
    {
        WriteRpcHeader(payload, TdsProcedureId.Unprepare, transactionDescriptor);
        WriteIntParameter(payload, ordinal: 0, handle, output: false);
    }

    internal static string BuildDefinitions(SqlParameters parameters)
    {
        var length = GetDefinitionsLength(parameters);
        return string.Create(
          length,
          parameters,
          static (destination, state) =>
          {
              var written = FormatDefinitions(state, destination);
              Debug.Assert(written == destination.Length);
          });
    }

    /// <summary>
    /// Reports whether <paramref name="parameters"/> would produce the same declarations as
    /// <paramref name="definitions"/> (built by <see cref="BuildDefinitions"/>), without
    /// allocating. A NULL value matches any declared type because the server converts it.
    /// </summary>
    internal static bool DefinitionsMatch(string definitions, SqlParameters parameters)
    {
        var remaining = definitions.AsSpan();
        Span<char> digits = stackalloc char[11];
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                if (remaining.IsEmpty || remaining[0] != ',')
                {
                    return false;
                }

                remaining = remaining[1..];
            }

            if (!remaining.StartsWith("@P", StringComparison.Ordinal) ||
                !(i + 1).TryFormat(digits, out var written, provider: CultureInfo.InvariantCulture) ||
                !remaining[2..].StartsWith(digits[..written], StringComparison.Ordinal) ||
                remaining.Length <= 2 + written ||
                remaining[2 + written] != ' ')
            {
                return false;
            }

            remaining = remaining[(3 + written)..];
            var value = parameters[i];
            if (value.IsNull)
            {
                remaining = remaining[DefinitionLength(remaining)..];
                continue;
            }

            if (value.Kind == SqlValueKind.Decimal)
            {
                const string prefix = "numeric(38,";
                if (!remaining.StartsWith(prefix, StringComparison.Ordinal) ||
                    !GetDecimalScale(value.GetRequired<decimal>()).TryFormat(
                      digits,
                      out written,
                      provider: CultureInfo.InvariantCulture) ||
                    !remaining[prefix.Length..].StartsWith(digits[..written], StringComparison.Ordinal) ||
                    remaining.Length <= prefix.Length + written ||
                    remaining[prefix.Length + written] != ')')
                {
                    return false;
                }

                remaining = remaining[(prefix.Length + written + 1)..];
                continue;
            }

            var definition = GetDefinition(value);
            if (!remaining.StartsWith(definition, StringComparison.Ordinal))
            {
                return false;
            }

            remaining = remaining[definition.Length..];
        }

        return remaining.IsEmpty;
    }

    // Length of the declaration at the start of value: up to the next ',' outside parentheses.
    private static int DefinitionLength(ReadOnlySpan<char> value)
    {
        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    return i;
            }
        }

        return value.Length;
    }

    private static void WriteParameters(
        IBufferWriter<byte> payload,
        SqlParameters parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            WriteParameter(payload, i + 1, parameters[i]);
        }
    }

    // Writes the sp_executesql @params declaration straight into the payload. It produces the
    // same text as BuildDefinitions without building a string.
    private static void WriteDefinitionsParameter(
        IBufferWriter<byte> payload,
        SqlParameters parameters)
    {
        var length = GetDefinitionsLength(parameters);
        char[]? rented = null;
        var definitions = length <= StackDefinitionCharacters
          ? stackalloc char[StackDefinitionCharacters]
          : rented = ArrayPool<char>.Shared.Rent(length);
        try
        {
            var written = FormatDefinitions(parameters, definitions);
            Debug.Assert(written == length);
            WriteNVarCharParameter(payload, ordinal: 0, definitions[..written]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    private static int GetDefinitionsLength(SqlParameters parameters)
    {
        var length = Math.Max(0, parameters.Count - 1);
        for (var i = 0; i < parameters.Count; i++)
        {
            var value = parameters[i];
            length += 3 + CountDigits(i + 1) + (value.Kind == SqlValueKind.Decimal
              ? DecimalDefinitionPrefix.Length + CountDigits(GetDecimalScale(value.GetRequired<decimal>())) + 1
              : GetDefinition(value).Length);
        }

        return length;
    }

    // Formats "@P1 int,@P2 numeric(38,2),..." into destination, which must be large enough.
    private static int FormatDefinitions(SqlParameters parameters, Span<char> destination)
    {
        var position = 0;
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                destination[position++] = ',';
            }

            destination[position++] = '@';
            destination[position++] = 'P';
            _ = (i + 1).TryFormat(
              destination[position..],
              out var written,
              provider: CultureInfo.InvariantCulture);
            position += written;
            destination[position++] = ' ';
            var value = parameters[i];
            if (value.Kind == SqlValueKind.Decimal)
            {
                DecimalDefinitionPrefix.CopyTo(destination[position..]);
                position += DecimalDefinitionPrefix.Length;
                _ = GetDecimalScale(value.GetRequired<decimal>()).TryFormat(
                  destination[position..],
                  out written,
                  provider: CultureInfo.InvariantCulture);
                position += written;
                destination[position++] = ')';
            }
            else
            {
                var definition = GetDefinition(value);
                definition.CopyTo(destination[position..]);
                position += definition.Length;
            }
        }

        return position;
    }

    private static int CountDigits(int value)
    {
        var digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }

        return digits;
    }

    // Decimal declarations carry the value's scale and are formatted by the callers.
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
          SqlValueKind.Decimal => throw new UnreachableException(),
          // UTF-16 strings always encode to two bytes per char (lone surrogates become U+FFFD),
          // so the length decides the 8000-byte nvarchar(4000) limit without counting bytes.
          SqlValueKind.String =>
          value.GetRequired<string>().Length > 4000
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
        IBufferWriter<byte> payload,
        int ordinal,
        SqlValue value)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Null:
                WriteNVarCharParameter(payload, ordinal, null);
                break;
            case SqlValueKind.Boolean:
                WriteHeader(payload, ordinal, TdsDataType.BitN);
                payload.WriteByte(1);
                payload.WriteByte(1);
                payload.WriteByte(value.GetRequired<bool>() ? (byte)1 : (byte)0);
                break;
            case SqlValueKind.Int16:
                WriteHeader(payload, ordinal, TdsDataType.IntN);
                payload.WriteByte(2);
                payload.WriteByte(2);
                payload.WriteInt16LittleEndian(value.GetRequired<short>());
                break;
            case SqlValueKind.Int32:
                WriteHeader(payload, ordinal, TdsDataType.IntN);
                payload.WriteByte(4);
                payload.WriteByte(4);
                payload.WriteInt32LittleEndian(value.GetRequired<int>());
                break;
            case SqlValueKind.Int64:
                WriteHeader(payload, ordinal, TdsDataType.IntN);
                payload.WriteByte(8);
                payload.WriteByte(8);
                payload.WriteInt64LittleEndian(value.GetRequired<long>());
                break;
            case SqlValueKind.Single:
                WriteFloatingPoint(
                  payload,
                  ordinal,
                  BitConverter.SingleToInt32Bits(value.GetRequired<float>()));
                break;
            case SqlValueKind.Double:
                WriteFloatingPoint(
                  payload,
                  ordinal,
                  BitConverter.DoubleToInt64Bits(value.GetRequired<double>()));
                break;
            case SqlValueKind.Decimal:
                WriteDecimal(payload, ordinal, value.GetRequired<decimal>());
                break;
            case SqlValueKind.String:
                WriteNVarCharParameter(payload, ordinal, value.GetRequired<string>());
                break;
            case SqlValueKind.Bytes:
                WriteVarBinaryParameter(payload, ordinal, value.GetRequired<byte[]>());
                break;
            case SqlValueKind.ReadOnlyMemory:
                WriteVarBinaryParameter(
                  payload,
                  ordinal,
                  value.GetRequired<ReadOnlyMemory<byte>>().Span);
                break;
            case SqlValueKind.Guid:
                WriteGuid(payload, ordinal, value.GetRequired<Guid>());
                break;
            case SqlValueKind.DateOnly:
                WriteDate(payload, ordinal, value.GetRequired<DateOnly>());
                break;
            case SqlValueKind.TimeOnly:
                WriteTime(payload, ordinal, value.GetRequired<TimeOnly>());
                break;
            case SqlValueKind.DateTime:
                WriteDateTime(payload, ordinal, value.GetRequired<DateTime>());
                break;
            case SqlValueKind.DateTimeOffset:
                WriteDateTimeOffset(payload, ordinal, value.GetRequired<DateTimeOffset>());
                break;
            case SqlValueKind.JsonDocument:
                WriteNVarCharParameter(
                  payload,
                  ordinal,
                  value.GetRequired<JsonDocument>().RootElement.GetRawText());
                break;
            case SqlValueKind.JsonElement:
                WriteNVarCharParameter(
                  payload,
                  ordinal,
                  value.GetRequired<JsonElement>().GetRawText());
                break;
            case SqlValueKind.Object when value.ToObject() is byte byteValue:
                WriteHeader(payload, ordinal, TdsDataType.IntN);
                payload.WriteByte(1);
                payload.WriteByte(1);
                payload.WriteByte(byteValue);
                break;
            case SqlValueKind.Object when value.ToObject() is sbyte signedByte:
                WriteHeader(payload, ordinal, TdsDataType.IntN);
                payload.WriteByte(2);
                payload.WriteByte(2);
                payload.WriteInt16LittleEndian(signedByte);
                break;
            case SqlValueKind.Object when value.ToObject() is Half half:
                WriteFloatingPoint(payload, ordinal, BitConverter.SingleToInt32Bits((float)half));
                break;
            case SqlValueKind.Object when value.ToObject() is BigInteger integer:
                WriteBigInteger(payload, ordinal, integer);
                break;
            case SqlValueKind.Object when value.ToObject() is Int128 integer:
                WriteBigInteger(payload, ordinal, BigInteger.CreateChecked(integer));
                break;
            case SqlValueKind.Object when value.ToObject() is UInt128 integer:
                WriteBigInteger(payload, ordinal, BigInteger.CreateChecked(integer));
                break;
            case SqlValueKind.Object when value.ToObject() is TimeSpan duration:
                WriteTime(payload, ordinal, duration);
                break;
            case SqlValueKind.Object when value.ToObject() is char character:
                WriteNVarCharParameter(payload, ordinal, character.ToString());
                break;
            case SqlValueKind.Object when value.ToObject() is char[] characters:
                WriteNVarCharParameter(payload, ordinal, new string(characters));
                break;
            case SqlValueKind.Object when value.ToObject() is IPAddress address:
                WriteNVarCharParameter(payload, ordinal, address.ToString());
                break;
            case SqlValueKind.Object when value.ToObject() is PhysicalAddress address:
                WriteVarBinaryParameter(payload, ordinal, address.GetAddressBytes());
                break;
            case SqlValueKind.Object when value.ToObject() is BitArray bits:
                WriteNVarCharParameter(payload, ordinal, FormatBits(bits));
                break;
            default:
                throw UnsupportedParameter(value);
        }
    }

    private static void WriteAllHeaders(
        IBufferWriter<byte> payload,
        long transactionDescriptor)
    {
        payload.WriteInt32LittleEndian(22);
        payload.WriteInt32LittleEndian(18);
        payload.WriteUInt16LittleEndian(2);
        payload.WriteInt64LittleEndian(transactionDescriptor);
        payload.WriteInt32LittleEndian(1);
    }

    private static void WriteRpcHeader(
        IBufferWriter<byte> payload,
        ushort procedureId,
        long transactionDescriptor)
    {
        WriteAllHeaders(payload, transactionDescriptor);
        payload.WriteUInt16LittleEndian(ushort.MaxValue);
        payload.WriteUInt16LittleEndian(procedureId);
        payload.WriteUInt16LittleEndian(0);
    }

    private static void WriteHeader(
        IBufferWriter<byte> payload,
        int ordinal,
        byte type,
        bool output = false)
    {
        WriteParameterName(payload, ordinal);
        payload.WriteByte(output ? (byte)1 : (byte)0);
        payload.WriteByte(type);
    }

    // Ordinal 0 is an unnamed parameter; ordinal n is "@Pn", written without building a string.
    private static void WriteParameterName(IBufferWriter<byte> payload, int ordinal)
    {
        if (ordinal == 0)
        {
            payload.WriteByte(0);
            return;
        }

        Span<char> name = stackalloc char[12];
        name[0] = '@';
        name[1] = 'P';
        _ = ordinal.TryFormat(name[2..], out var written, provider: CultureInfo.InvariantCulture);
        payload.WriteByte((byte)(2 + written));
        payload.WriteUtf16(name[..(2 + written)]);
    }

    private static void WriteNVarCharParameter(
        IBufferWriter<byte> payload,
        int ordinal,
        string? value)
    {
        if (value is null)
        {
            WriteHeader(payload, ordinal, TdsDataType.NVarChar);
            payload.WriteUInt16LittleEndian(8000);
            WriteCollation(payload);
            payload.WriteUInt16LittleEndian(ushort.MaxValue);
            return;
        }

        WriteNVarCharParameter(payload, ordinal, value.AsSpan());
    }

    private static void WriteNVarCharParameter(
        IBufferWriter<byte> payload,
        int ordinal,
        ReadOnlySpan<char> value)
    {
        WriteHeader(payload, ordinal, TdsDataType.NVarChar);
        var byteCount = checked(value.Length * 2);
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
            payload.WriteUInt16LittleEndian((ushort)byteCount);
            payload.WriteUtf16(value);
        }
    }

    private static void WriteIntParameter(
        IBufferWriter<byte> payload,
        int ordinal,
        int value,
        bool output)
    {
        WriteHeader(payload, ordinal, TdsDataType.IntN, output);
        payload.WriteByte(sizeof(int));
        payload.WriteByte(sizeof(int));
        payload.WriteInt32LittleEndian(value);
    }

    private static void WriteVarBinaryParameter(
        IBufferWriter<byte> payload,
        int ordinal,
        ReadOnlySpan<byte> value)
    {
        WriteHeader(payload, ordinal, TdsDataType.BigVarBinary);
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
        IBufferWriter<byte> payload,
        int ordinal,
        int bits)
    {
        WriteHeader(payload, ordinal, TdsDataType.FloatN);
        payload.WriteByte(4);
        payload.WriteByte(4);
        payload.WriteInt32LittleEndian(bits);
    }

    private static void WriteFloatingPoint(
        IBufferWriter<byte> payload,
        int ordinal,
        long bits)
    {
        WriteHeader(payload, ordinal, TdsDataType.FloatN);
        payload.WriteByte(8);
        payload.WriteByte(8);
        payload.WriteInt64LittleEndian(bits);
    }

    private static void WriteDecimal(
        IBufferWriter<byte> payload,
        int ordinal,
        decimal value)
    {
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(value, bits);
        var scale = GetDecimalScale(value);
        WriteHeader(payload, ordinal, TdsDataType.DecimalN);
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
        IBufferWriter<byte> payload,
        int ordinal,
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
        WriteHeader(payload, ordinal, TdsDataType.DecimalN);
        payload.WriteByte(17);
        payload.WriteByte(38);
        payload.WriteByte(0);
        payload.WriteByte(17);
        payload.WriteByte(value.Sign < 0 ? (byte)0 : (byte)1);
        payload.Write(magnitude);
    }

    private static void WriteGuid(
        IBufferWriter<byte> payload,
        int ordinal,
        Guid value)
    {
        WriteHeader(payload, ordinal, TdsDataType.Guid);
        payload.WriteByte(16);
        payload.WriteByte(16);
        Span<byte> bytes = stackalloc byte[16];
        _ = value.TryWriteBytes(bytes);
        payload.Write(bytes);
    }

    private static void WriteDate(
        IBufferWriter<byte> payload,
        int ordinal,
        DateOnly value)
    {
        WriteHeader(payload, ordinal, TdsDataType.Date);
        payload.WriteByte(3);
        payload.WriteUInt24LittleEndian(value.DayNumber);
    }

    private static void WriteTime(
        IBufferWriter<byte> payload,
        int ordinal,
        TimeOnly value)
    {
        WriteHeader(payload, ordinal, TdsDataType.Time);
        payload.WriteByte(7);
        payload.WriteByte(5);
        payload.WriteUInt40LittleEndian(value.Ticks);
    }

    private static void WriteTime(
        IBufferWriter<byte> payload,
        int ordinal,
        TimeSpan value)
    {
        if (value < TimeSpan.Zero || value >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
              nameof(value),
              "SQL Server time parameters must be between 00:00:00 and 24:00:00.");
        }

        WriteHeader(payload, ordinal, TdsDataType.Time);
        payload.WriteByte(7);
        payload.WriteByte(5);
        payload.WriteUInt40LittleEndian(value.Ticks);
    }

    private static void WriteDateTime(
        IBufferWriter<byte> payload,
        int ordinal,
        DateTime value)
    {
        WriteHeader(payload, ordinal, TdsDataType.DateTime2);
        payload.WriteByte(7);
        payload.WriteByte(8);
        payload.WriteUInt40LittleEndian(value.TimeOfDay.Ticks);
        payload.WriteUInt24LittleEndian(DateOnly.FromDateTime(value).DayNumber);
    }

    private static void WriteDateTimeOffset(
        IBufferWriter<byte> payload,
        int ordinal,
        DateTimeOffset value)
    {
        WriteHeader(payload, ordinal, TdsDataType.DateTimeOffset);
        payload.WriteByte(7);
        payload.WriteByte(10);
        var utc = value.UtcDateTime;
        payload.WriteUInt40LittleEndian(utc.TimeOfDay.Ticks);
        payload.WriteUInt24LittleEndian(DateOnly.FromDateTime(utc).DayNumber);
        payload.WriteInt16LittleEndian(checked((short)value.Offset.TotalMinutes));
    }

    private static byte GetDecimalScale(decimal value) => value.Scale;

    private static string FormatBits(BitArray bits)
    {
        var characters = new char[bits.Count];
        for (var i = 0; i < bits.Count; i++)
        {
            characters[i] = bits[i] ? '1' : '0';
        }

        return new string(characters);
    }

    private static void WriteCollation(IBufferWriter<byte> payload)
    {
        ReadOnlySpan<byte> collation = [0x09, 0x04, 0xD0, 0x00, 0x34];
        payload.Write(collation);
    }

    private static NotSupportedException UnsupportedParameter(SqlValue value) =>
      new($"SQL Server parameter kind {value.Kind} is not supported.");
}

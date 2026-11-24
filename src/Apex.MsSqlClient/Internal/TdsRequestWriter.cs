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
// every request. The Build* overloads that return a new payload exist for tests;
// BuildDefinitions is also used to keep a prepared handle's declarations.
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
        long transactionDescriptor,
        string[]? parameterNames = null)
    {
        ArrayBufferWriter<byte> payload = new();
        WriteExecuteSql(payload, sql, parameters, transactionDescriptor, parameterNames);
        return payload.WrittenMemory;
    }

    internal static void WriteExecuteSql(
        IBufferWriter<byte> payload,
        string sql,
        SqlParameters parameters,
        long transactionDescriptor,
        string[]? parameterNames = null)
    {
        WriteRpcHeader(payload, TdsProcedureId.ExecuteSql, transactionDescriptor);
        WriteNVarCharParameter(payload, ordinal: 0, sql);
        WriteDefinitionsParameter(payload, parameters, parameterNames);
        WriteParameters(payload, parameters, parameterNames);
    }

    internal static ReadOnlyMemory<byte> BuildPrepareExecute(
        string sql,
        SqlParameters parameters,
        long transactionDescriptor,
        string[]? parameterNames = null) =>
        BuildPrepareExecute(sql, BuildDefinitions(parameters, parameterNames), parameters, transactionDescriptor, parameterNames);

    internal static ReadOnlyMemory<byte> BuildPrepareExecute(
        string sql,
        string definitions,
        SqlParameters parameters,
        long transactionDescriptor,
        string[]? parameterNames = null)
    {
        ArrayBufferWriter<byte> payload = new();
        WritePrepareExecute(payload, sql, definitions, parameters, transactionDescriptor, parameterNames);
        return payload.WrittenMemory;
    }

    internal static void WritePrepareExecute(
        IBufferWriter<byte> payload,
        string sql,
        string definitions,
        SqlParameters parameters,
        long transactionDescriptor,
        string[]? parameterNames = null)
    {
        WriteRpcHeader(payload, TdsProcedureId.PrepExec, transactionDescriptor);
        WriteIntParameter(payload, ordinal: 0, 0, output: true);
        WriteNVarCharParameter(payload, ordinal: 0, definitions);
        WriteNVarCharParameter(payload, ordinal: 0, sql);
        WriteParameters(payload, parameters, parameterNames);
    }

    internal static ReadOnlyMemory<byte> BuildExecute(
        int handle,
        SqlParameters parameters,
        long transactionDescriptor,
        string[]? parameterNames = null,
        string? definitions = null)
    {
        ArrayBufferWriter<byte> payload = new();
        WriteExecute(payload, handle, parameters, transactionDescriptor, parameterNames, definitions);
        return payload.WrittenMemory;
    }

    internal static void WriteExecute(
        IBufferWriter<byte> payload,
        int handle,
        SqlParameters parameters,
        long transactionDescriptor,
        string[]? parameterNames = null,
        string? definitions = null)
    {
        WriteRpcHeader(payload, TdsProcedureId.Execute, transactionDescriptor);
        WriteIntParameter(payload, ordinal: 0, handle, output: true);
        WriteParameters(payload, parameters, parameterNames, definitions);
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

    internal static string BuildDefinitions(
        SqlParameters parameters,
        string[]? parameterNames = null)
    {
        var length = GetDefinitionsLength(parameters, parameterNames);
        if (parameterNames is null)
        {
            return string.Create(
              length,
              parameters,
              static (destination, state) =>
              {
                  var written = FormatDefinitions(state, parameterNames: null, destination);
                  Debug.Assert(written == destination.Length);
              });
        }

        return string.Create(
          length,
          (parameters, parameterNames),
          static (destination, state) =>
          {
              var written = FormatDefinitions(state.parameters, state.parameterNames, destination);
              Debug.Assert(written == destination.Length);
          });
    }

    /// <summary>
    /// Reports whether <paramref name="parameters"/> would produce the same declarations as
    /// <paramref name="definitions"/> (built by <see cref="BuildDefinitions"/>), without
    /// allocating. A NULL value matches any declared type because the server converts it
    /// (with varbinary NULLs encoded as varbinary by <see cref="WriteExecute"/>).
    /// </summary>
    internal static bool DefinitionsMatch(
        string definitions,
        SqlParameters parameters,
        string[]? parameterNames = null)
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

            if (parameterNames is not null)
            {
                var name = parameterNames[i];
                if (!remaining.StartsWith(name, StringComparison.Ordinal) ||
                    remaining.Length <= name.Length ||
                    remaining[name.Length] != ' ')
                {
                    return false;
                }

                remaining = remaining[(name.Length + 1)..];
            }
            else
            {
                if (!remaining.StartsWith("@P", StringComparison.Ordinal) ||
                    !(i + 1).TryFormat(digits, out var written, provider: CultureInfo.InvariantCulture) ||
                    !remaining[2..].StartsWith(digits[..written], StringComparison.Ordinal) ||
                    remaining.Length <= 2 + written ||
                    remaining[2 + written] != ' ')
                {
                    return false;
                }

                remaining = remaining[(3 + written)..];
            }
            var value = parameters[i];
            if (value.IsNull)
            {
                remaining = remaining[DefinitionLength(remaining)..];
                continue;
            }

            if (value.Kind == SqlValueKind.Decimal)
            {
                if (!remaining.StartsWith(DecimalDefinitionPrefix, StringComparison.Ordinal) ||
                    !GetDecimalScale(value.GetRequired<decimal>()).TryFormat(
                      digits,
                      out var written,
                      provider: CultureInfo.InvariantCulture) ||
                    !remaining[DecimalDefinitionPrefix.Length..].StartsWith(digits[..written], StringComparison.Ordinal) ||
                    remaining.Length <= DecimalDefinitionPrefix.Length + written ||
                    remaining[DecimalDefinitionPrefix.Length + written] != ')')
                {
                    return false;
                }

                remaining = remaining[(DecimalDefinitionPrefix.Length + written + 1)..];
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

    private static ReadOnlySpan<char> GetDeclaredType(string definitions, int index)
    {
        var remaining = definitions.AsSpan();
        for (var i = 0; i <= index; i++)
        {
            if (remaining.IsEmpty)
            {
                return ReadOnlySpan<char>.Empty;
            }

            var space = remaining.IndexOf(' ');
            if (space < 0)
            {
                return ReadOnlySpan<char>.Empty;
            }

            remaining = remaining[(space + 1)..];
            var typeLength = DefinitionLength(remaining);
            if (i == index)
            {
                return remaining[..typeLength];
            }

            remaining = typeLength < remaining.Length && remaining[typeLength] == ','
                ? remaining[(typeLength + 1)..]
                : ReadOnlySpan<char>.Empty;
        }

        return ReadOnlySpan<char>.Empty;
    }

    private static void WriteParameters(
        IBufferWriter<byte> payload,
        SqlParameters parameters,
        string[]? parameterNames = null,
        string? definitions = null)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            var name = parameterNames is not null ? parameterNames[i] : null;
            var value = parameters[i];
            if (value.IsNull && definitions is not null)
            {
                var declaredType = GetDeclaredType(definitions, i);
                if (declaredType.StartsWith("varbinary", StringComparison.Ordinal))
                {
                    WriteNullVarBinaryParameter(
                      payload,
                      i + 1,
                      isMax: declaredType.Equals("varbinary(max)", StringComparison.Ordinal),
                      name);
                    continue;
                }
            }

            WriteParameter(payload, i + 1, value, name);
        }
    }

    // Writes the sp_executesql @params declaration straight into the payload. It produces the
    // same text as BuildDefinitions without building a string.
    private static void WriteDefinitionsParameter(
        IBufferWriter<byte> payload,
        SqlParameters parameters,
        string[]? parameterNames = null)
    {
        var length = GetDefinitionsLength(parameters, parameterNames);
        char[]? rented = null;
        var definitions = length <= StackDefinitionCharacters
          ? stackalloc char[StackDefinitionCharacters]
          : rented = ArrayPool<char>.Shared.Rent(length);
        try
        {
            var written = FormatDefinitions(parameters, parameterNames, definitions);
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

    private static int GetDefinitionsLength(
        SqlParameters parameters,
        string[]? parameterNames)
    {
        var length = Math.Max(0, parameters.Count - 1);
        for (var i = 0; i < parameters.Count; i++)
        {
            var value = parameters[i];
            var nameAndSpace = parameterNames is not null
              ? parameterNames[i].Length + 1
              : 3 + CountDigits(i + 1);
            length += nameAndSpace + (value.Kind == SqlValueKind.Decimal
              ? DecimalDefinitionPrefix.Length + CountDigits(GetDecimalScale(value.GetRequired<decimal>())) + 1
              : GetDefinition(value).Length);
        }

        return length;
    }

    // Formats "@P1 int,@P2 numeric(38,2),..." into destination, which must be large enough.
    private static int FormatDefinitions(
        SqlParameters parameters,
        string[]? parameterNames,
        Span<char> destination)
    {
        var position = 0;
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                destination[position++] = ',';
            }

            if (parameterNames is not null)
            {
                var name = parameterNames[i];
                name.CopyTo(destination[position..]);
                position += name.Length;
            }
            else
            {
                destination[position++] = '@';
                destination[position++] = 'P';
                _ = (i + 1).TryFormat(
                  destination[position..],
                  out var written,
                  provider: CultureInfo.InvariantCulture);
                position += written;
            }

            destination[position++] = ' ';
            var value = parameters[i];
            if (value.Kind == SqlValueKind.Decimal)
            {
                DecimalDefinitionPrefix.CopyTo(destination[position..]);
                position += DecimalDefinitionPrefix.Length;
                _ = GetDecimalScale(value.GetRequired<decimal>()).TryFormat(
                  destination[position..],
                  out var written,
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
          SqlValueKind.Object when value.ToObject() is char or IPAddress => "nvarchar(4000)",
          SqlValueKind.Object when value.ToObject() is char[] characters =>
            characters.Length > 4000 ? "nvarchar(max)" : "nvarchar(4000)",
          SqlValueKind.Object when value.ToObject() is BitArray bits =>
            bits.Count > 4000 ? "nvarchar(max)" : "nvarchar(4000)",
          SqlValueKind.Object when value.ToObject() is PhysicalAddress address =>
            address.GetAddressBytes().Length > 8000 ? "varbinary(max)" : "varbinary(8000)",
          _ => throw UnsupportedParameter(value),
      };

    private static void WriteParameter(
        IBufferWriter<byte> payload,
        int ordinal,
        SqlValue value,
        string? name = null)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Null:
                WriteNVarCharParameter(payload, ordinal, null, name);
                break;
            case SqlValueKind.Boolean:
                WriteHeader(payload, ordinal, TdsDataType.BitN, name: name);
                payload.WriteByte(1);
                payload.WriteByte(1);
                payload.WriteByte(value.GetRequired<bool>() ? (byte)1 : (byte)0);
                break;
            case SqlValueKind.Int16:
                WriteHeader(payload, ordinal, TdsDataType.IntN, name: name);
                payload.WriteByte(2);
                payload.WriteByte(2);
                payload.WriteInt16LittleEndian(value.GetRequired<short>());
                break;
            case SqlValueKind.Int32:
                WriteHeader(payload, ordinal, TdsDataType.IntN, name: name);
                payload.WriteByte(4);
                payload.WriteByte(4);
                payload.WriteInt32LittleEndian(value.GetRequired<int>());
                break;
            case SqlValueKind.Int64:
                WriteHeader(payload, ordinal, TdsDataType.IntN, name: name);
                payload.WriteByte(8);
                payload.WriteByte(8);
                payload.WriteInt64LittleEndian(value.GetRequired<long>());
                break;
            case SqlValueKind.Single:
                WriteFloatingPoint(
                  payload,
                  ordinal,
                  BitConverter.SingleToInt32Bits(value.GetRequired<float>()),
                  name);
                break;
            case SqlValueKind.Double:
                WriteFloatingPoint(
                  payload,
                  ordinal,
                  BitConverter.DoubleToInt64Bits(value.GetRequired<double>()),
                  name);
                break;
            case SqlValueKind.Decimal:
                WriteDecimal(payload, ordinal, value.GetRequired<decimal>(), name);
                break;
            case SqlValueKind.String:
                WriteNVarCharParameter(payload, ordinal, value.GetRequired<string>(), name);
                break;
            case SqlValueKind.Bytes:
                WriteVarBinaryParameter(payload, ordinal, value.GetRequired<byte[]>(), name);
                break;
            case SqlValueKind.ReadOnlyMemory:
                WriteVarBinaryParameter(
                  payload,
                  ordinal,
                  value.GetRequired<ReadOnlyMemory<byte>>().Span,
                  name);
                break;
            case SqlValueKind.Guid:
                WriteGuid(payload, ordinal, value.GetRequired<Guid>(), name);
                break;
            case SqlValueKind.DateOnly:
                WriteDate(payload, ordinal, value.GetRequired<DateOnly>(), name);
                break;
            case SqlValueKind.TimeOnly:
                WriteTime(payload, ordinal, value.GetRequired<TimeOnly>(), name);
                break;
            case SqlValueKind.DateTime:
                WriteDateTime(payload, ordinal, value.GetRequired<DateTime>(), name);
                break;
            case SqlValueKind.DateTimeOffset:
                WriteDateTimeOffset(payload, ordinal, value.GetRequired<DateTimeOffset>(), name);
                break;
            case SqlValueKind.JsonDocument:
                WriteNVarCharParameter(
                  payload,
                  ordinal,
                  value.GetRequired<JsonDocument>().RootElement.GetRawText(),
                  name);
                break;
            case SqlValueKind.JsonElement:
                WriteNVarCharParameter(
                  payload,
                  ordinal,
                  value.GetRequired<JsonElement>().GetRawText(),
                  name);
                break;
            case SqlValueKind.Object when value.ToObject() is byte byteValue:
                WriteHeader(payload, ordinal, TdsDataType.IntN, name: name);
                payload.WriteByte(1);
                payload.WriteByte(1);
                payload.WriteByte(byteValue);
                break;
            case SqlValueKind.Object when value.ToObject() is sbyte signedByte:
                WriteHeader(payload, ordinal, TdsDataType.IntN, name: name);
                payload.WriteByte(2);
                payload.WriteByte(2);
                payload.WriteInt16LittleEndian(signedByte);
                break;
            case SqlValueKind.Object when value.ToObject() is Half half:
                WriteFloatingPoint(payload, ordinal, BitConverter.SingleToInt32Bits((float)half), name);
                break;
            case SqlValueKind.Object when value.ToObject() is BigInteger integer:
                WriteBigInteger(payload, ordinal, integer, name);
                break;
            case SqlValueKind.Object when value.ToObject() is Int128 integer:
                WriteBigInteger(payload, ordinal, BigInteger.CreateChecked(integer), name);
                break;
            case SqlValueKind.Object when value.ToObject() is UInt128 integer:
                WriteBigInteger(payload, ordinal, BigInteger.CreateChecked(integer), name);
                break;
            case SqlValueKind.Object when value.ToObject() is TimeSpan duration:
                WriteTime(payload, ordinal, duration, name);
                break;
            case SqlValueKind.Object when value.ToObject() is char character:
                WriteNVarCharParameter(payload, ordinal, character.ToString(), name);
                break;
            case SqlValueKind.Object when value.ToObject() is char[] characters:
                WriteNVarCharParameter(payload, ordinal, new string(characters), name);
                break;
            case SqlValueKind.Object when value.ToObject() is IPAddress address:
                WriteNVarCharParameter(payload, ordinal, address.ToString(), name);
                break;
            case SqlValueKind.Object when value.ToObject() is PhysicalAddress address:
                WriteVarBinaryParameter(payload, ordinal, address.GetAddressBytes(), name);
                break;
            case SqlValueKind.Object when value.ToObject() is BitArray bits:
                WriteNVarCharParameter(payload, ordinal, FormatBits(bits), name);
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
        bool output = false,
        string? name = null)
    {
        WriteParameterName(payload, ordinal, name);
        payload.WriteByte(output ? (byte)1 : (byte)0);
        payload.WriteByte(type);
    }

    // Ordinal 0 is an unnamed parameter; ordinal n is "@Pn", written without building a string.
    private static void WriteParameterName(IBufferWriter<byte> payload, int ordinal, string? name = null)
    {
        if (name is not null)
        {
            payload.WriteByte(checked((byte)name.Length));
            payload.WriteUtf16(name);
            return;
        }

        if (ordinal == 0)
        {
            payload.WriteByte(0);
            return;
        }

        Span<char> buffer = stackalloc char[12];
        buffer[0] = '@';
        buffer[1] = 'P';
        _ = ordinal.TryFormat(buffer[2..], out var written, provider: CultureInfo.InvariantCulture);
        payload.WriteByte((byte)(2 + written));
        payload.WriteUtf16(buffer[..(2 + written)]);
    }

    private static void WriteNVarCharParameter(
        IBufferWriter<byte> payload,
        int ordinal,
        string? value,
        string? name = null)
    {
        if (value is null)
        {
            WriteHeader(payload, ordinal, TdsDataType.NVarChar, name: name);
            payload.WriteUInt16LittleEndian(8000);
            WriteCollation(payload);
            payload.WriteUInt16LittleEndian(ushort.MaxValue);
            return;
        }

        WriteNVarCharParameter(payload, ordinal, value.AsSpan(), name);
    }

    private static void WriteNVarCharParameter(
        IBufferWriter<byte> payload,
        int ordinal,
        ReadOnlySpan<char> value,
        string? name = null)
    {
        WriteHeader(payload, ordinal, TdsDataType.NVarChar, name: name);
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

    private static void WriteNullVarBinaryParameter(
        IBufferWriter<byte> payload,
        int ordinal,
        bool isMax,
        string? name = null)
    {
        WriteHeader(payload, ordinal, TdsDataType.BigVarBinary, name: name);
        if (isMax)
        {
            payload.WriteUInt16LittleEndian(ushort.MaxValue);
            payload.WriteInt64LittleEndian(-1L);
        }
        else
        {
            payload.WriteUInt16LittleEndian(8000);
            payload.WriteUInt16LittleEndian(ushort.MaxValue);
        }
    }

    private static void WriteVarBinaryParameter(
        IBufferWriter<byte> payload,
        int ordinal,
        ReadOnlySpan<byte> value,
        string? name = null)
    {
        WriteHeader(payload, ordinal, TdsDataType.BigVarBinary, name: name);
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
        int bits,
        string? name = null)
    {
        WriteHeader(payload, ordinal, TdsDataType.FloatN, name: name);
        payload.WriteByte(4);
        payload.WriteByte(4);
        payload.WriteInt32LittleEndian(bits);
    }

    private static void WriteFloatingPoint(
        IBufferWriter<byte> payload,
        int ordinal,
        long bits,
        string? name = null)
    {
        WriteHeader(payload, ordinal, TdsDataType.FloatN, name: name);
        payload.WriteByte(8);
        payload.WriteByte(8);
        payload.WriteInt64LittleEndian(bits);
    }

    private static void WriteDecimal(
        IBufferWriter<byte> payload,
        int ordinal,
        decimal value,
        string? name = null)
    {
        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(value, bits);
        var scale = GetDecimalScale(value);
        WriteHeader(payload, ordinal, TdsDataType.DecimalN, name: name);
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
        BigInteger value,
        string? name = null)
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
        WriteHeader(payload, ordinal, TdsDataType.DecimalN, name: name);
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
        Guid value,
        string? name = null)
    {
        WriteHeader(payload, ordinal, TdsDataType.Guid, name: name);
        payload.WriteByte(16);
        payload.WriteByte(16);
        Span<byte> bytes = stackalloc byte[16];
        _ = value.TryWriteBytes(bytes);
        payload.Write(bytes);
    }

    private static void WriteDate(
        IBufferWriter<byte> payload,
        int ordinal,
        DateOnly value,
        string? name = null)
    {
        WriteHeader(payload, ordinal, TdsDataType.Date, name: name);
        payload.WriteByte(3);
        payload.WriteUInt24LittleEndian(value.DayNumber);
    }

    private static void WriteTime(
        IBufferWriter<byte> payload,
        int ordinal,
        TimeOnly value,
        string? name = null)
    {
        WriteHeader(payload, ordinal, TdsDataType.Time, name: name);
        payload.WriteByte(7);
        payload.WriteByte(5);
        payload.WriteUInt40LittleEndian(value.Ticks);
    }

    private static void WriteTime(
        IBufferWriter<byte> payload,
        int ordinal,
        TimeSpan value,
        string? name = null)
    {
        if (value < TimeSpan.Zero || value >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
              nameof(value),
              "SQL Server time parameters must be between 00:00:00 and 24:00:00.");
        }

        WriteHeader(payload, ordinal, TdsDataType.Time, name: name);
        payload.WriteByte(7);
        payload.WriteByte(5);
        payload.WriteUInt40LittleEndian(value.Ticks);
    }

    private static void WriteDateTime(
        IBufferWriter<byte> payload,
        int ordinal,
        DateTime value,
        string? name = null)
    {
        WriteHeader(payload, ordinal, TdsDataType.DateTime2, name: name);
        payload.WriteByte(7);
        payload.WriteByte(8);
        payload.WriteUInt40LittleEndian(value.TimeOfDay.Ticks);
        payload.WriteUInt24LittleEndian(DateOnly.FromDateTime(value).DayNumber);
    }

    private static void WriteDateTimeOffset(
        IBufferWriter<byte> payload,
        int ordinal,
        DateTimeOffset value,
        string? name = null)
    {
        WriteHeader(payload, ordinal, TdsDataType.DateTimeOffset, name: name);
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

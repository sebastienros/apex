using System.Globalization;
using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text.Json;
using Apex.SqlClient;

namespace Apex.MySqlClient.Internal;

/// <summary>Encodes <see cref="SqlValue"/> parameters for COM_STMT_EXECUTE.</summary>
internal static class MySqlParameterEncoder
{
    internal static void WriteExecute(
        MySqlPayloadWriter writer,
        uint statementId,
        MySqlCursorType cursorType,
        SqlParameters parameters,
        int expectedCount)
    {
        if (parameters.Count != expectedCount)
        {
            throw new ArgumentException(
              $"The statement expects {expectedCount} parameters but {parameters.Count} were supplied.",
              nameof(parameters));
        }

        writer.WriteByte((byte)MySqlCommand.StatementExecute);
        writer.WriteUInt32(statementId);
        writer.WriteByte((byte)cursorType);
        writer.WriteUInt32(1);
        var count = parameters.Count;
        if (count == 0)
        {
            return;
        }

        var bitmapLength = (count + 7) / 8;
        Span<byte> stack = stackalloc byte[64];
        var bitmap = bitmapLength <= stack.Length
          ? stack[..bitmapLength]
          : new byte[bitmapLength];
        bitmap.Clear();
        for (var i = 0; i < count; i++)
        {
            if (parameters[i].IsNull)
            {
                bitmap[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        writer.WriteBytes(bitmap);
        writer.WriteByte(1);
        for (var i = 0; i < count; i++)
        {
            (var type, var unsigned) = GetWireType(parameters[i]);
            writer.WriteByte((byte)type);
            writer.WriteByte(unsigned ? (byte)0x80 : (byte)0x00);
        }

        for (var i = 0; i < count; i++)
        {
            var value = parameters[i];
            if (!value.IsNull)
            {
                WriteValue(writer, value);
            }
        }
    }

    private static (MySqlType Type, bool Unsigned) GetWireType(SqlValue value) =>
      value.Kind switch
      {
          SqlValueKind.Null => (MySqlType.Null, false),
          SqlValueKind.Boolean => (MySqlType.Tiny, false),
          SqlValueKind.Int16 => (MySqlType.Short, false),
          SqlValueKind.Int32 => (MySqlType.Long, false),
          SqlValueKind.Int64 => (MySqlType.LongLong, false),
          SqlValueKind.Single => (MySqlType.Float, false),
          SqlValueKind.Double => (MySqlType.Double, false),
          SqlValueKind.Decimal => (MySqlType.NewDecimal, false),
          SqlValueKind.String => (MySqlType.VarString, false),
          SqlValueKind.Bytes or SqlValueKind.ReadOnlyMemory => (MySqlType.Blob, false),
          SqlValueKind.Guid => (MySqlType.VarString, false),
          SqlValueKind.DateOnly => (MySqlType.Date, false),
          SqlValueKind.TimeOnly => (MySqlType.Time, false),
          SqlValueKind.DateTime or SqlValueKind.DateTimeOffset => (MySqlType.DateTime, false),
          SqlValueKind.JsonDocument or SqlValueKind.JsonElement => (MySqlType.VarString, false),
          _ => GetObjectWireType(value.ToObject()),
      };

    private static (MySqlType Type, bool Unsigned) GetObjectWireType(object? value) =>
      value switch
      {
          null => (MySqlType.Null, false),
          sbyte => (MySqlType.Tiny, false),
          byte => (MySqlType.Tiny, true),
          ushort => (MySqlType.Short, true),
          uint => (MySqlType.Long, true),
          ulong => (MySqlType.LongLong, true),
          char => (MySqlType.VarString, false),
          char[] => (MySqlType.VarString, false),
          TimeSpan => (MySqlType.Time, false),
          Half => (MySqlType.Float, false),
          BigInteger => (MySqlType.NewDecimal, false),
          Int128 => (MySqlType.NewDecimal, false),
          UInt128 => (MySqlType.NewDecimal, false),
          IPAddress => (MySqlType.VarString, false),
          PhysicalAddress => (MySqlType.Blob, false),
          BitArray => (MySqlType.LongLong, true),
          MySqlDecimal => (MySqlType.NewDecimal, false),
          _ => throw new NotSupportedException(
          $"MySQL parameters of type {value.GetType().FullName} are not supported."),
      };

    private static void WriteValue(MySqlPayloadWriter writer, SqlValue value)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Boolean:
                writer.WriteByte(value.GetRequired<bool>() ? (byte)1 : (byte)0);
                return;
            case SqlValueKind.Int16:
                writer.WriteUInt16(unchecked((ushort)value.GetRequired<short>()));
                return;
            case SqlValueKind.Int32:
                writer.WriteInt32(value.GetRequired<int>());
                return;
            case SqlValueKind.Int64:
                writer.WriteInt64(value.GetRequired<long>());
                return;
            case SqlValueKind.Single:
                writer.WriteSingle(value.GetRequired<float>());
                return;
            case SqlValueKind.Double:
                writer.WriteDouble(value.GetRequired<double>());
                return;
            case SqlValueKind.Decimal:
                writer.WriteLengthEncodedString(
                  value.GetRequired<decimal>().ToString(CultureInfo.InvariantCulture));
                return;
            case SqlValueKind.String:
                writer.WriteLengthEncodedString(value.GetRequired<string>());
                return;
            case SqlValueKind.Bytes:
                writer.WriteLengthEncodedBytes(value.GetRequired<byte[]>());
                return;
            case SqlValueKind.ReadOnlyMemory:
                writer.WriteLengthEncodedBytes(value.GetRequired<ReadOnlyMemory<byte>>().Span);
                return;
            case SqlValueKind.Guid:
                writer.WriteLengthEncodedString(value.GetRequired<Guid>().ToString("D", CultureInfo.InvariantCulture));
                return;
            case SqlValueKind.DateOnly:
                WriteDate(writer, value.GetRequired<DateOnly>());
                return;
            case SqlValueKind.TimeOnly:
                WriteTime(writer, value.GetRequired<TimeOnly>().ToTimeSpan());
                return;
            case SqlValueKind.DateTime:
                WriteDateTime(writer, value.GetRequired<DateTime>());
                return;
            case SqlValueKind.DateTimeOffset:
                WriteDateTime(writer, value.GetRequired<DateTimeOffset>().UtcDateTime);
                return;
            case SqlValueKind.JsonDocument:
                writer.WriteLengthEncodedString(value.GetRequired<JsonDocument>().RootElement.GetRawText());
                return;
            case SqlValueKind.JsonElement:
                writer.WriteLengthEncodedString(value.GetRequired<JsonElement>().GetRawText());
                return;
            default:
                WriteObject(writer, value.ToObject());
                return;
        }
    }

    private static void WriteObject(MySqlPayloadWriter writer, object? value)
    {
        switch (value)
        {
            case sbyte typed:
                writer.WriteByte(unchecked((byte)typed));
                return;
            case byte typed:
                writer.WriteByte(typed);
                return;
            case ushort typed:
                writer.WriteUInt16(typed);
                return;
            case uint typed:
                writer.WriteUInt32(typed);
                return;
            case ulong typed:
                writer.WriteUInt64(typed);
                return;
            case char typed:
                writer.WriteLengthEncodedString(typed.ToString());
                return;
            case char[] typed:
                writer.WriteLengthEncodedString(new string(typed));
                return;
            case TimeSpan typed:
                WriteTime(writer, typed);
                return;
            case Half typed:
                writer.WriteSingle((float)typed);
                return;
            case BigInteger typed:
                writer.WriteLengthEncodedString(typed.ToString(CultureInfo.InvariantCulture));
                return;
            case Int128 typed:
                writer.WriteLengthEncodedString(typed.ToString(CultureInfo.InvariantCulture));
                return;
            case UInt128 typed:
                writer.WriteLengthEncodedString(typed.ToString(CultureInfo.InvariantCulture));
                return;
            case IPAddress typed:
                writer.WriteLengthEncodedString(typed.ToString());
                return;
            case PhysicalAddress typed:
                writer.WriteLengthEncodedBytes(typed.GetAddressBytes());
                return;
            case BitArray typed:
                WriteBitArray(writer, typed);
                return;
            case MySqlDecimal typed:
                writer.WriteLengthEncodedString(typed.ToString());
                return;
            default:
                throw new NotSupportedException(
                  $"MySQL parameters of type {value?.GetType().FullName ?? "null"} are not supported.");
        }
    }

    private static void WriteDate(MySqlPayloadWriter writer, DateOnly value)
    {
        writer.WriteByte(4);
        writer.WriteUInt16((ushort)value.Year);
        writer.WriteByte((byte)value.Month);
        writer.WriteByte((byte)value.Day);
    }

    private static void WriteDateTime(MySqlPayloadWriter writer, DateTime value)
    {
        var microseconds = (int)(value.Ticks % TimeSpan.TicksPerSecond / 10);
        var hasTime = value.TimeOfDay != TimeSpan.Zero;
        writer.WriteByte(microseconds != 0 ? (byte)11 : hasTime ? (byte)7 : (byte)4);
        writer.WriteUInt16((ushort)value.Year);
        writer.WriteByte((byte)value.Month);
        writer.WriteByte((byte)value.Day);
        if (microseconds == 0 && !hasTime)
        {
            return;
        }

        writer.WriteByte((byte)value.Hour);
        writer.WriteByte((byte)value.Minute);
        writer.WriteByte((byte)value.Second);
        if (microseconds != 0)
        {
            writer.WriteUInt32((uint)microseconds);
        }
    }

    private static void WriteTime(MySqlPayloadWriter writer, TimeSpan value)
    {
        if (value == TimeSpan.Zero)
        {
            writer.WriteByte(0);
            return;
        }

        var absolute = value < TimeSpan.Zero ? value.Negate() : value;
        var microseconds = absolute.Ticks % TimeSpan.TicksPerSecond / 10;
        writer.WriteByte(microseconds != 0 ? (byte)12 : (byte)8);
        writer.WriteByte(value < TimeSpan.Zero ? (byte)1 : (byte)0);
        writer.WriteUInt32((uint)absolute.Days);
        writer.WriteByte((byte)absolute.Hours);
        writer.WriteByte((byte)absolute.Minutes);
        writer.WriteByte((byte)absolute.Seconds);
        if (microseconds != 0)
        {
            writer.WriteUInt32((uint)microseconds);
        }
    }

    private static void WriteBitArray(MySqlPayloadWriter writer, BitArray value)
    {
        if (value.Count > 64)
        {
            throw new ArgumentOutOfRangeException(
              nameof(value),
              "MySQL BIT parameters support at most 64 bits.");
        }

        ulong bits = 0;
        for (var i = 0; i < value.Count; i++)
        {
            bits = (bits << 1) | (value[i] ? 1UL : 0UL);
        }

        writer.WriteUInt64(bits);
    }
}

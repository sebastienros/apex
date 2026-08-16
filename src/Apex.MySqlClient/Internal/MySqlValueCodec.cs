using System.Buffers.Binary;
using System.Buffers.Text;

namespace Apex.MySqlClient.Internal;

/// <summary>Parses the scalar encodings shared by the MySQL text and binary protocols.</summary>
internal static class MySqlValueCodec
{
    internal static long ParseInt64(ReadOnlySpan<byte> value) =>
      Utf8Parser.TryParse(value, out long parsed, out var consumed) && consumed == value.Length
        ? parsed
        : throw new FormatException($"Invalid MySQL integer value '{Describe(value)}'.");

    internal static ulong ParseUInt64(ReadOnlySpan<byte> value) =>
      Utf8Parser.TryParse(value, out ulong parsed, out var consumed) && consumed == value.Length
        ? parsed
        : throw new FormatException($"Invalid MySQL unsigned integer value '{Describe(value)}'.");

    internal static float ParseSingle(ReadOnlySpan<byte> value) =>
      Utf8Parser.TryParse(value, out float parsed, out var consumed) && consumed == value.Length
        ? parsed
        : throw new FormatException($"Invalid MySQL FLOAT value '{Describe(value)}'.");

    internal static double ParseDouble(ReadOnlySpan<byte> value) =>
      Utf8Parser.TryParse(value, out double parsed, out var consumed) && consumed == value.Length
        ? parsed
        : throw new FormatException($"Invalid MySQL DOUBLE value '{Describe(value)}'.");

    internal static decimal ParseDecimal(ReadOnlySpan<byte> value) =>
      Utf8Parser.TryParse(value, out decimal parsed, out var consumed) && consumed == value.Length
        ? parsed
        : throw new FormatException(
          $"MySQL DECIMAL value '{Describe(value)}' cannot be represented as System.Decimal.");

    /// <summary>Converts the big-endian payload of a BIT column into an unsigned integer.</summary>
    internal static ulong ParseBit(ReadOnlySpan<byte> value)
    {
        if (value.Length > sizeof(ulong))
        {
            throw new FormatException($"MySQL BIT value of {value.Length} bytes is too wide.");
        }

        ulong result = 0;
        foreach (var item in value)
        {
            result = (result << 8) | item;
        }

        return result;
    }

    /// <summary>Parses <c>YYYY-MM-DD</c>, reporting the all zero date through <paramref name="isZero"/>.</summary>
    internal static DateOnly ParseDate(ReadOnlySpan<byte> value, out bool isZero)
    {
        var position = 0;
        (var year, var month, var day) = ReadDatePart(value, ref position);
        if (position != value.Length)
        {
            throw new FormatException($"Invalid MySQL DATE value '{Describe(value)}'.");
        }

        if (year == 0 && month == 0 && day == 0)
        {
            isZero = true;
            return default;
        }

        isZero = false;
        return CreateDate(value, year, month, day);
    }

    /// <summary>Parses <c>YYYY-MM-DD HH:MM:SS[.ffffff]</c>.</summary>
    internal static DateTime ParseDateTime(ReadOnlySpan<byte> value, out bool isZero)
    {
        var position = 0;
        (var year, var month, var day) = ReadDatePart(value, ref position);
        var hour = 0;
        var minute = 0;
        var second = 0;
        var microseconds = 0;
        if (position < value.Length)
        {
            if (value[position] is not ((byte)' ' or (byte)'T'))
            {
                throw new FormatException($"Invalid MySQL DATETIME value '{Describe(value)}'.");
            }

            position++;
            hour = ReadDigits(value, ref position, 2);
            Expect(value, ref position, (byte)':');
            minute = ReadDigits(value, ref position, 2);
            Expect(value, ref position, (byte)':');
            second = ReadDigits(value, ref position, 2);
            microseconds = ReadFraction(value, ref position);
        }

        if (position != value.Length)
        {
            throw new FormatException($"Invalid MySQL DATETIME value '{Describe(value)}'.");
        }

        if (year == 0 && month == 0 && day == 0)
        {
            isZero = true;
            return default;
        }

        isZero = false;
        return CreateDateTime(value, year, month, day, hour, minute, second, microseconds);
    }

    /// <summary>Parses <c>[-]HHH:MM:SS[.ffffff]</c>, which MySQL uses for TIME durations.</summary>
    internal static TimeSpan ParseTime(ReadOnlySpan<byte> value)
    {
        var position = 0;
        var negative = position < value.Length && value[position] == (byte)'-';
        if (negative || (position < value.Length && value[position] == (byte)'+'))
        {
            position++;
        }

        var hours = ReadVariableDigits(value, ref position, maximum: 3);
        Expect(value, ref position, (byte)':');
        var minutes = ReadDigits(value, ref position, 2);
        Expect(value, ref position, (byte)':');
        var seconds = ReadDigits(value, ref position, 2);
        var microseconds = ReadFraction(value, ref position);
        if (position != value.Length)
        {
            throw new FormatException($"Invalid MySQL TIME value '{Describe(value)}'.");
        }

        TimeSpan result = new(0, hours, minutes, seconds, 0, microseconds);
        return negative ? result.Negate() : result;
    }

    /// <summary>Reads the binary protocol DATE, DATETIME and TIMESTAMP encoding.</summary>
    internal static DateTime ReadBinaryDateTime(ReadOnlySpan<byte> value, out bool isZero)
    {
        isZero = false;
        if (value.IsEmpty)
        {
            isZero = true;
            return default;
        }

        if (value.Length is not (4 or 7 or 11))
        {
            throw new FormatException($"Invalid MySQL binary date/time length {value.Length}.");
        }

        int year = BinaryPrimitives.ReadUInt16LittleEndian(value);
        var month = value.Length > 2 ? value[2] : 0;
        var day = value.Length > 3 ? value[3] : 0;
        var hour = value.Length > 4 ? value[4] : 0;
        var minute = value.Length > 5 ? value[5] : 0;
        var second = value.Length > 6 ? value[6] : 0;
        var microseconds = value.Length >= 11
          ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(value[7..]))
          : 0;
        if (year == 0 && month == 0 && day == 0)
        {
            isZero = true;
            return default;
        }

        return CreateDateTime(value, year, month, day, hour, minute, second, microseconds);
    }

    /// <summary>Reads the binary protocol TIME encoding.</summary>
    internal static TimeSpan ReadBinaryTime(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return TimeSpan.Zero;
        }

        if (value.Length is not (8 or 12))
        {
            throw new FormatException($"Invalid MySQL binary TIME length {value.Length}.");
        }

        var negative = value[0] != 0;
        var days = BinaryPrimitives.ReadUInt32LittleEndian(value[1..]);
        int hours = value[5];
        int minutes = value[6];
        int seconds = value[7];
        var microseconds = value.Length == 12
          ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(value[8..]))
          : 0;
        TimeSpan result = new(checked((int)days), hours, minutes, seconds, 0, microseconds);
        return negative ? result.Negate() : result;
    }

    internal static string Describe(ReadOnlySpan<byte> value)
    {
        const int maximum = 64;
        var trimmed = value.Length > maximum ? value[..maximum] : value;
        Span<char> characters = stackalloc char[trimmed.Length];
        for (var i = 0; i < trimmed.Length; i++)
        {
            var item = trimmed[i];
            characters[i] = item is >= 0x20 and < 0x7F ? (char)item : '?';
        }

        return new string(characters);
    }

    private static (int Year, int Month, int Day) ReadDatePart(
        ReadOnlySpan<byte> value,
        ref int position)
    {
        var year = ReadDigits(value, ref position, 4);
        Expect(value, ref position, (byte)'-');
        var month = ReadDigits(value, ref position, 2);
        Expect(value, ref position, (byte)'-');
        var day = ReadDigits(value, ref position, 2);
        return (year, month, day);
    }

    private static DateOnly CreateDate(ReadOnlySpan<byte> value, int year, int month, int day)
    {
        if (year is < 1 or > 9999 ||
            month is < 1 or > 12 ||
            day < 1 ||
            day > DateTime.DaysInMonth(year, month))
        {
            throw new FormatException($"Invalid MySQL DATE value '{Describe(value)}'.");
        }

        return new DateOnly(year, month, day);
    }

    private static DateTime CreateDateTime(
        ReadOnlySpan<byte> value,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second,
        int microseconds)
    {
        var date = CreateDate(value, year, month, day);
        if (hour > 23 || minute > 59 || second > 59 || microseconds > 999_999)
        {
            throw new FormatException($"Invalid MySQL DATETIME value '{Describe(value)}'.");
        }

        return new DateTime(date, default, DateTimeKind.Unspecified)
          .AddTicks(((((hour * 3600L) + (minute * 60L) + second) * 1_000_000L) + microseconds) * 10L);
    }

    private static int ReadDigits(ReadOnlySpan<byte> value, ref int position, int count)
    {
        if (position > value.Length - count)
        {
            throw new FormatException($"Invalid MySQL temporal value '{Describe(value)}'.");
        }

        var result = 0;
        for (var i = 0; i < count; i++)
        {
            var digit = value[position + i];
            if (digit is < (byte)'0' or > (byte)'9')
            {
                throw new FormatException($"Invalid MySQL temporal value '{Describe(value)}'.");
            }

            result = (result * 10) + (digit - '0');
        }

        position += count;
        return result;
    }

    private static int ReadVariableDigits(ReadOnlySpan<byte> value, ref int position, int maximum)
    {
        var start = position;
        var result = 0;
        while (position < value.Length &&
               value[position] is >= (byte)'0' and <= (byte)'9' &&
               position - start < maximum)
        {
            result = (result * 10) + (value[position] - '0');
            position++;
        }

        if (position == start)
        {
            throw new FormatException($"Invalid MySQL temporal value '{Describe(value)}'.");
        }

        return result;
    }

    private static int ReadFraction(ReadOnlySpan<byte> value, ref int position)
    {
        if (position >= value.Length || value[position] != (byte)'.')
        {
            return 0;
        }

        position++;
        var digits = 0;
        var result = 0;
        while (position < value.Length && value[position] is >= (byte)'0' and <= (byte)'9')
        {
            if (digits < 6)
            {
                result = (result * 10) + (value[position] - '0');
                digits++;
            }

            position++;
        }

        if (digits == 0)
        {
            throw new FormatException($"Invalid MySQL temporal value '{Describe(value)}'.");
        }

        for (var i = digits; i < 6; i++)
        {
            result *= 10;
        }

        return result;
    }

    private static void Expect(ReadOnlySpan<byte> value, ref int position, byte expected)
    {
        if (position >= value.Length || value[position] != expected)
        {
            throw new FormatException($"Invalid MySQL temporal value '{Describe(value)}'.");
        }

        position++;
    }
}

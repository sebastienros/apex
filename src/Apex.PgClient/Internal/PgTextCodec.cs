using System.Buffers.Text;
using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apex.SqlClient;

namespace Apex.PgClient.Internal;

internal static class PgTextCodec
{
    private static readonly Encoding s_utf8 = new UTF8Encoding(false, true);

    public static object Decode(uint typeId, ReadOnlyMemory<byte> value)
    {
        var text = s_utf8.GetString(value.Span);
        var elementType = GetArrayElementType(typeId);
        if (elementType != 0)
        {
            return DecodeArrayObject(typeId, value);
        }

        return DecodeText(typeId, text, value);
    }

    internal static bool DecodeBoolean(ReadOnlySpan<byte> value)
    {
        if (value.SequenceEqual("t"u8))
        {
            return true;
        }

        if (value.SequenceEqual("f"u8))
        {
            return false;
        }

        throw new FormatException("Invalid PostgreSQL BOOL value.");
    }

    internal static byte[] DecodeBytes(ReadOnlySpan<byte> value) =>
      ParseBytea(s_utf8.GetString(value));

    internal static short DecodeInt16(ReadOnlySpan<byte> value) =>
      Utf8Parser.TryParse(value, out short parsed, out var consumed) &&
      consumed == value.Length
        ? parsed
        : throw new FormatException("Invalid PostgreSQL INT2 value.");

    internal static int DecodeInt32(ReadOnlySpan<byte> value) =>
      Utf8Parser.TryParse(value, out int parsed, out var consumed) &&
      consumed == value.Length
        ? parsed
        : throw new FormatException("Invalid PostgreSQL INT4 value.");

    internal static long DecodeInt64(ReadOnlySpan<byte> value) =>
      Utf8Parser.TryParse(value, out long parsed, out var consumed) &&
      consumed == value.Length
        ? parsed
        : throw new FormatException("Invalid PostgreSQL INT8 value.");

    internal static float DecodeFloat(ReadOnlySpan<byte> value) =>
      Utf8Parser.TryParse(value, out float parsed, out var consumed) &&
      consumed == value.Length
        ? parsed
        : throw new FormatException("Invalid PostgreSQL FLOAT4 value.");

    internal static double DecodeDouble(ReadOnlySpan<byte> value) =>
      Utf8Parser.TryParse(value, out double parsed, out var consumed) &&
      consumed == value.Length
        ? parsed
        : throw new FormatException("Invalid PostgreSQL FLOAT8 value.");

    internal static decimal DecodeDecimal(ReadOnlySpan<byte> value) =>
      Utf8Parser.TryParse(value, out decimal parsed, out var consumed) &&
      consumed == value.Length
        ? parsed
        : throw new FormatException("Invalid PostgreSQL NUMERIC value.");

    internal static BigInteger DecodeBigInteger(ReadOnlySpan<byte> value) =>
      BigInteger.Parse(DecodeString(value), NumberStyles.Integer, CultureInfo.InvariantCulture);

    internal static string DecodeString(ReadOnlySpan<byte> value) =>
      s_utf8.GetString(value);

    internal static char DecodeChar(ReadOnlySpan<byte> value)
    {
        var text = DecodeString(value);
        return text.Length == 1
          ? text[0]
          : throw new InvalidCastException(
            "PostgreSQL text value must contain exactly one character.");
    }

    internal static char[] DecodeChars(ReadOnlySpan<byte> value) =>
      DecodeString(value).ToCharArray();

    internal static Guid DecodeGuid(ReadOnlySpan<byte> value) =>
      Utf8Parser.TryParse(value, out Guid parsed, out var consumed) &&
      consumed == value.Length
        ? parsed
        : throw new FormatException("Invalid PostgreSQL UUID value.");

    internal static DateOnly DecodeDateOnly(ReadOnlySpan<byte> value) =>
      ParseDate(s_utf8.GetString(value));

    internal static TimeOnly DecodeTimeOnly(ReadOnlySpan<byte> value) =>
      TimeOnly.Parse(s_utf8.GetString(value), CultureInfo.InvariantCulture);

    internal static DateTime DecodeDateTime(ReadOnlySpan<byte> value) =>
      ParseTimestamp(s_utf8.GetString(value));

    internal static DateTimeOffset DecodeDateTimeOffset(
        ReadOnlySpan<byte> value) =>
      ParseTimestampWithTimeZone(s_utf8.GetString(value));

    internal static JsonElement DecodeJson(ReadOnlyMemory<byte> value) =>
      ParseJson(value);

    internal static PgNumeric DecodeNumeric(ReadOnlySpan<byte> value) =>
      PgNumeric.Parse(s_utf8.GetString(value));

    internal static PgMoney DecodeMoney(ReadOnlySpan<byte> value) =>
      ParseMoney(s_utf8.GetString(value));

    internal static PgInterval DecodeInterval(ReadOnlySpan<byte> value) =>
      ParseInterval(s_utf8.GetString(value));

    internal static TimeSpan DecodeTimeSpan(ReadOnlySpan<byte> value) =>
      ToTimeSpan(DecodeInterval(value));

    internal static PgTimeWithTimeZone DecodeTimeWithTimeZone(
        ReadOnlySpan<byte> value) =>
      ParseTimeWithTimeZone(s_utf8.GetString(value));

    internal static PgPoint DecodePoint(ReadOnlySpan<byte> value) =>
      ParsePoint(s_utf8.GetString(value));

    internal static PgLineSegment DecodeLineSegment(
        ReadOnlySpan<byte> value) =>
      ParseLineSegment(s_utf8.GetString(value));

    internal static PgPath DecodePath(ReadOnlySpan<byte> value) =>
      ParsePath(s_utf8.GetString(value));

    internal static PgBox DecodeBox(ReadOnlySpan<byte> value) =>
      ParseBox(s_utf8.GetString(value));

    internal static PgPolygon DecodePolygon(ReadOnlySpan<byte> value) =>
      ParsePolygon(s_utf8.GetString(value));

    internal static PgLine DecodeLine(ReadOnlySpan<byte> value) =>
      ParseLine(s_utf8.GetString(value));

    internal static PgCidr DecodeCidr(ReadOnlySpan<byte> value) =>
      ParseCidr(s_utf8.GetString(value));

    internal static PgCircle DecodeCircle(ReadOnlySpan<byte> value) =>
      ParseCircle(s_utf8.GetString(value));

    internal static PgInet DecodeInet(ReadOnlySpan<byte> value) =>
      ParseInet(s_utf8.GetString(value));

    internal static IPAddress DecodeIPAddress(ReadOnlySpan<byte> value) =>
      DecodeInet(value).Address;

    internal static PhysicalAddress DecodePhysicalAddress(ReadOnlySpan<byte> value) =>
      PhysicalAddress.Parse(DecodeString(value));

    internal static BitArray DecodeBitArray(ReadOnlySpan<byte> value)
    {
        var result = new BitArray(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            result[i] = value[i] switch
            {
                (byte)'0' => false,
                (byte)'1' => true,
                _ => throw new FormatException("Invalid PostgreSQL BIT value."),
            };
        }

        return result;
    }

    internal static TElement[] DecodeArray<TElement>(
        uint typeId,
        ReadOnlyMemory<byte> value)
    {
        var elementType = GetArrayElementType(typeId);
        if (elementType == 0)
        {
            throw new PgUnsupportedTypeException(typeId);
        }

        var parsed = PgArrayParser.Parse(
          s_utf8.GetString(value.Span),
          elementType == 603 ? ';' : ',');
        var result = new TElement[parsed.Length];
        for (var i = 0; i < parsed.Length; i++)
        {
            var item = parsed[i];
            if (item is null)
            {
                if (default(TElement) is not null)
                {
                    throw new InvalidCastException(
                      $"PostgreSQL array element {i} is NULL and cannot be read as {typeof(TElement).FullName}.");
                }

                continue;
            }

            result[i] = DecodeArrayElement<TElement>(elementType, item);
        }

        return result;
    }

    private static object DecodeText(
        uint typeId,
        string text,
        ReadOnlyMemory<byte> utf8 = default) =>
      typeId switch
      {
          16 => text == "t",
          17 => ParseBytea(text),
          20 => long.Parse(
          text,
          NumberStyles.Integer,
          CultureInfo.InvariantCulture),
          21 => short.Parse(
          text,
          NumberStyles.Integer,
          CultureInfo.InvariantCulture),
          23 => int.Parse(
          text,
          NumberStyles.Integer,
          CultureInfo.InvariantCulture),
          26 or 142 or 2278 =>
          throw new PgUnsupportedTypeException(typeId),
          774 or 829 => PhysicalAddress.Parse(text),
          1560 or 1562 => DecodeBitArray(utf8.Span),
          700 => float.Parse(
          text,
          NumberStyles.Float,
          CultureInfo.InvariantCulture),
          701 => double.Parse(
          text,
          NumberStyles.Float,
          CultureInfo.InvariantCulture),
          1700 => PgNumeric.Parse(text),
          1082 => ParseDate(text),
          1083 => TimeOnly.Parse(text, CultureInfo.InvariantCulture),
          1266 => ParseTimeWithTimeZone(text),
          1114 => ParseTimestamp(text),
          1184 => ParseTimestampWithTimeZone(text),
          1186 => ParseInterval(text),
          2950 => Guid.Parse(text),
          114 or 3802 => utf8.IsEmpty
          ? ParseJson(text)
          : ParseJson(utf8),
          600 => ParsePoint(text),
          601 => ParseLineSegment(text),
          602 => ParsePath(text),
          603 => ParseBox(text),
          604 => ParsePolygon(text),
          628 => ParseLine(text),
          650 => ParseCidr(text),
          718 => ParseCircle(text),
          790 => ParseMoney(text),
          869 => ParseInet(text),
          _ => text,
      };

    internal static uint GetArrayElementType(uint typeId) =>
      typeId switch
      {
          1000 => 16,
          1001 => 17,
          1002 => 18,
          1003 => 19,
          1005 => 21,
          1007 => 23,
          1009 => 25,
          1015 => 1043,
          1016 => 20,
          1017 => 600,
          1018 => 601,
          1019 => 602,
          1020 => 603,
          1021 => 700,
          1022 => 701,
          1027 => 604,
          1040 => 829,
          1041 => 869,
          1115 => 1114,
          1182 => 1082,
          1183 => 1083,
          1185 => 1184,
          1187 => 1186,
          1231 => 1700,
          1270 => 1266,
          775 => 774,
          1561 => 1560,
          1563 => 1562,
          199 => 114,
          629 => 628,
          651 => 650,
          719 => 718,
          791 => 790,
          2951 => 2950,
          3807 => 3802,
          _ => 0,
      };

    private static object DecodeArrayObject(
        uint arrayTypeId,
        ReadOnlyMemory<byte> value) =>
      GetArrayElementType(arrayTypeId) switch
      {
          16 => DecodeArray<bool?>(arrayTypeId, value),
          17 => DecodeArray<byte[]?>(arrayTypeId, value),
          18 or 19 or 25 or 1043 => DecodeArray<string?>(arrayTypeId, value),
          20 => DecodeArray<long?>(arrayTypeId, value),
          21 => DecodeArray<short?>(arrayTypeId, value),
          23 => DecodeArray<int?>(arrayTypeId, value),
          700 => DecodeArray<float?>(arrayTypeId, value),
          701 => DecodeArray<double?>(arrayTypeId, value),
          790 => DecodeArray<PgMoney?>(arrayTypeId, value),
          1082 => DecodeArray<DateOnly?>(arrayTypeId, value),
          1083 => DecodeArray<TimeOnly?>(arrayTypeId, value),
          1114 => DecodeArray<DateTime?>(arrayTypeId, value),
          1184 => DecodeArray<DateTimeOffset?>(arrayTypeId, value),
          1186 => DecodeArray<PgInterval?>(arrayTypeId, value),
          1266 => DecodeArray<PgTimeWithTimeZone?>(arrayTypeId, value),
          1700 => DecodeArray<PgNumeric?>(arrayTypeId, value),
          2950 => DecodeArray<Guid?>(arrayTypeId, value),
          600 => DecodeArray<PgPoint?>(arrayTypeId, value),
          601 => DecodeArray<PgLineSegment?>(arrayTypeId, value),
          602 => DecodeArray<PgPath?>(arrayTypeId, value),
          603 => DecodeArray<PgBox?>(arrayTypeId, value),
          604 => DecodeArray<PgPolygon?>(arrayTypeId, value),
          628 => DecodeArray<PgLine?>(arrayTypeId, value),
          650 => DecodeArray<PgCidr?>(arrayTypeId, value),
          718 => DecodeArray<PgCircle?>(arrayTypeId, value),
          869 => DecodeArray<PgInet?>(arrayTypeId, value),
          774 or 829 => DecodeArray<PhysicalAddress?>(arrayTypeId, value),
          1560 or 1562 => DecodeArray<BitArray?>(arrayTypeId, value),
          114 or 3802 => DecodeArray<JsonElement?>(arrayTypeId, value),
          var elementType => throw new PgUnsupportedTypeException(elementType),
      };

    private static TElement DecodeArrayElement<TElement>(
        uint elementType,
        string text) =>
      elementType switch
      {
          16 => ConvertValue<TElement, bool>(text == "t", elementType),
          17 => ConvertReference<TElement, byte[]>(ParseBytea(text), elementType),
          18 or 19 or 25 or 1043 => ConvertText<TElement>(text, elementType),
          20 => ConvertValue<TElement, long>(
            long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
            elementType),
          21 => ConvertInt16<TElement>(
            short.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
            elementType),
          23 => ConvertValue<TElement, int>(
            int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
            elementType),
          700 => ConvertFloat<TElement>(
            float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
            elementType),
          701 => ConvertValue<TElement, double>(
            double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
            elementType),
          790 => ConvertValue<TElement, PgMoney>(ParseMoney(text), elementType),
          1082 => ConvertValue<TElement, DateOnly>(ParseDate(text), elementType),
          1083 => ConvertTime<TElement>(
            TimeOnly.Parse(text, CultureInfo.InvariantCulture), elementType),
          1114 => ConvertValue<TElement, DateTime>(ParseTimestamp(text), elementType),
          1184 => ConvertValue<TElement, DateTimeOffset>(
            ParseTimestampWithTimeZone(text),
            elementType),
          1186 => ConvertInterval<TElement>(ParseInterval(text), elementType),
          1266 => ConvertValue<TElement, PgTimeWithTimeZone>(
            ParseTimeWithTimeZone(text),
            elementType),
          1700 => ConvertNumeric<TElement>(PgNumeric.Parse(text), elementType),
          2950 => ConvertValue<TElement, Guid>(Guid.Parse(text), elementType),
          600 => ConvertValue<TElement, PgPoint>(ParsePoint(text), elementType),
          601 => ConvertValue<TElement, PgLineSegment>(ParseLineSegment(text), elementType),
          602 => ConvertReference<TElement, PgPath>(ParsePath(text), elementType),
          603 => ConvertValue<TElement, PgBox>(ParseBox(text), elementType),
          604 => ConvertReference<TElement, PgPolygon>(ParsePolygon(text), elementType),
          628 => ConvertValue<TElement, PgLine>(ParseLine(text), elementType),
          650 => ConvertValue<TElement, PgCidr>(ParseCidr(text), elementType),
          718 => ConvertValue<TElement, PgCircle>(ParseCircle(text), elementType),
          869 => ConvertInet<TElement>(ParseInet(text), elementType),
          774 or 829 => ConvertReference<TElement, PhysicalAddress>(
            PhysicalAddress.Parse(text), elementType),
          1560 or 1562 => ConvertReference<TElement, BitArray>(
            DecodeBitArray(s_utf8.GetBytes(text)), elementType),
          114 or 3802 => ConvertValue<TElement, JsonElement>(ParseJson(text), elementType),
          _ => throw new PgUnsupportedTypeException(elementType),
      };

    private static TElement ConvertValue<TElement, TValue>(
        TValue value,
        uint elementType)
      where TValue : struct
    {
        if (typeof(TElement) == typeof(TValue))
        {
            return Unsafe.As<TValue, TElement>(ref value);
        }

        if (typeof(TElement) == typeof(TValue?))
        {
            TValue? nullable = value;
            return Unsafe.As<TValue?, TElement>(ref nullable);
        }

        throw CannotReadArrayElement<TElement>(elementType);
    }

    private static TElement ConvertReference<TElement, TValue>(
        TValue value,
        uint elementType)
      where TValue : class
    {
        if (typeof(TElement) == typeof(TValue))
        {
            return Unsafe.As<TValue, TElement>(ref value);
        }

        throw CannotReadArrayElement<TElement>(elementType);
    }

    private static TElement ConvertInt16<TElement>(short value, uint elementType)
    {
        if (typeof(TElement) == typeof(byte) || typeof(TElement) == typeof(byte?))
        {
            return ConvertValue<TElement, byte>(checked((byte)value), elementType);
        }

        if (typeof(TElement) == typeof(sbyte) || typeof(TElement) == typeof(sbyte?))
        {
            return ConvertValue<TElement, sbyte>(checked((sbyte)value), elementType);
        }

        return ConvertValue<TElement, short>(value, elementType);
    }

    private static TElement ConvertText<TElement>(string value, uint elementType)
    {
        if (typeof(TElement) == typeof(char) || typeof(TElement) == typeof(char?))
        {
            char character = value.Length == 1
              ? value[0]
              : throw CannotReadArrayElement<TElement>(elementType);
            return ConvertValue<TElement, char>(character, elementType);
        }

        if (typeof(TElement) == typeof(char[]))
        {
            return ConvertReference<TElement, char[]>(value.ToCharArray(), elementType);
        }

        return ConvertReference<TElement, string>(value, elementType);
    }

    private static TElement ConvertNumeric<TElement>(PgNumeric value, uint elementType)
    {
      if (typeof(TElement) == typeof(BigInteger) || typeof(TElement) == typeof(BigInteger?) ||
        typeof(TElement) == typeof(Int128) || typeof(TElement) == typeof(Int128?) ||
        typeof(TElement) == typeof(UInt128) || typeof(TElement) == typeof(UInt128?))
        {
            if (!value.IsFinite || value.Scale != 0)
            {
                throw CannotReadArrayElement<TElement>(elementType);
            }

            if (typeof(TElement) == typeof(Int128) || typeof(TElement) == typeof(Int128?))
            {
              return ConvertValue<TElement, Int128>(checked((Int128)value.UnscaledValue), elementType);
            }

            if (typeof(TElement) == typeof(UInt128) || typeof(TElement) == typeof(UInt128?))
            {
              return ConvertValue<TElement, UInt128>(checked((UInt128)value.UnscaledValue), elementType);
            }

            return ConvertValue<TElement, BigInteger>(value.UnscaledValue, elementType);
        }

        return ConvertValue<TElement, PgNumeric>(value, elementType);
    }

    private static TElement ConvertFloat<TElement>(float value, uint elementType) =>
      typeof(TElement) == typeof(Half) || typeof(TElement) == typeof(Half?)
      ? ConvertValue<TElement, Half>(checked((Half)value), elementType)
      : ConvertValue<TElement, float>(value, elementType);

    private static TElement ConvertTime<TElement>(TimeOnly value, uint elementType) =>
      typeof(TElement) == typeof(TimeSpan) || typeof(TElement) == typeof(TimeSpan?)
      ? ConvertValue<TElement, TimeSpan>(value.ToTimeSpan(), elementType)
      : ConvertValue<TElement, TimeOnly>(value, elementType);

    private static TElement ConvertInterval<TElement>(PgInterval value, uint elementType) =>
      typeof(TElement) == typeof(TimeSpan) || typeof(TElement) == typeof(TimeSpan?)
      ? ConvertValue<TElement, TimeSpan>(ToTimeSpan(value), elementType)
      : ConvertValue<TElement, PgInterval>(value, elementType);

    private static TElement ConvertInet<TElement>(PgInet value, uint elementType) =>
      typeof(TElement) == typeof(IPAddress)
      ? ConvertReference<TElement, IPAddress>(value.Address, elementType)
      : ConvertValue<TElement, PgInet>(value, elementType);

    private static InvalidCastException CannotReadArrayElement<TElement>(
        uint elementType) =>
      new(
        $"PostgreSQL array element type OID {elementType} cannot be read as {typeof(TElement).FullName}.");

    private static byte[] ParseBytea(string text) =>
      text.StartsWith("\\x", StringComparison.Ordinal)
        ? Convert.FromHexString(text.AsSpan(2))
        : throw new NotSupportedException(
          "Only PostgreSQL hex bytea output is supported.");

    private static JsonElement ParseJson(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static JsonElement ParseJson(ReadOnlyMemory<byte> utf8)
    {
        using JsonDocument document = JsonDocument.Parse(utf8);
        return document.RootElement.Clone();
    }

    private static DateOnly ParseDate(string text) =>
      text switch
      {
          "infinity" => DateOnly.MaxValue,
          "-infinity" => DateOnly.MinValue,
          _ => DateOnly.ParseExact(
          text,
          "yyyy-MM-dd",
          CultureInfo.InvariantCulture),
      };

    private static DateTime ParseTimestamp(string text) =>
      text switch
      {
          "infinity" => DateTime.MaxValue,
          "-infinity" => DateTime.MinValue,
          _ => DateTime.SpecifyKind(
          DateTime.Parse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces),
          DateTimeKind.Unspecified),
      };

    private static DateTimeOffset ParseTimestampWithTimeZone(string text) =>
      text switch
      {
          "infinity" => DateTimeOffset.MaxValue,
          "-infinity" => DateTimeOffset.MinValue,
          _ => DateTimeOffset.Parse(
          text,
          CultureInfo.InvariantCulture,
          DateTimeStyles.AllowWhiteSpaces),
      };

    private static PgTimeWithTimeZone ParseTimeWithTimeZone(string text)
    {
        var separator = text.LastIndexOfAny('+', '-');
        if (separator <= 0)
        {
            throw new FormatException(
              "Invalid PostgreSQL time with time zone.");
        }

        TimeOnly time = TimeOnly.Parse(
          text[..separator],
          CultureInfo.InvariantCulture);
        var offsetText = text.AsSpan(separator);
        var sign = offsetText[0] == '-' ? -1 : 1;
        var parts = offsetText[1..].ToString().Split(':');
        var hours = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minutes = parts.Length > 1
          ? int.Parse(parts[1], CultureInfo.InvariantCulture)
          : 0;
        var seconds = parts.Length > 2
          ? int.Parse(parts[2], CultureInfo.InvariantCulture)
          : 0;
        TimeSpan offset = new(
          0,
          sign * hours,
          sign * minutes,
          sign * seconds);
        return new PgTimeWithTimeZone(time, offset);
    }

    private static PgInterval ParseInterval(string text)
    {
        var match = Regex.Match(
          text,
          @"^(?<sign>-)?P(?:(?<years>[+-]?\d+)Y)?(?:(?<months>[+-]?\d+)M)?(?:(?<days>[+-]?\d+)D)?" +
          @"(?:T(?:(?<hours>[+-]?\d+)H)?(?:(?<minutes>[+-]?\d+)M)?(?:(?<seconds>[+-]?\d+)(?:\.(?<fraction>\d{1,6}))?S)?)?$",
          RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new FormatException(
              $"Invalid PostgreSQL ISO-8601 interval '{text}'.");
        }

        var sign = match.Groups["sign"].Success ? -1 : 1;
        var fraction = ParseGroup(match, "fraction");
        if (match.Groups["fraction"].Success)
        {
            fraction *= (int)Math.Pow(
              10,
              6 - match.Groups["fraction"].Length);
        }

        var fractionSign =
          match.Groups["seconds"].Success &&
          match.Groups["seconds"].Value.StartsWith(
            "-",
            StringComparison.Ordinal)
            ? -1
            : 1;

        return new PgInterval(
          sign * ParseGroup(match, "years"),
          sign * ParseGroup(match, "months"),
          sign * ParseGroup(match, "days"),
          sign * ParseGroup(match, "hours"),
          sign * ParseGroup(match, "minutes"),
          sign * ParseGroup(match, "seconds"),
          sign * fractionSign * fraction);
    }

    private static int ParseGroup(Match match, string name) =>
      match.Groups[name].Success
        ? int.Parse(
          match.Groups[name].Value,
          CultureInfo.InvariantCulture)
        : 0;

    private static PgPoint ParsePoint(string text)
    {
        var values = ParseDoubles(text);
        return values.Length == 2
          ? new PgPoint(values[0], values[1])
          : throw new FormatException("Invalid PostgreSQL point.");
    }

    private static PgLine ParseLine(string text)
    {
        var values = ParseDoubles(text);
        return values.Length == 3
          ? new PgLine(values[0], values[1], values[2])
          : throw new FormatException("Invalid PostgreSQL line.");
    }

    private static PgLineSegment ParseLineSegment(string text)
    {
        var values = ParseDoubles(text);
        return values.Length == 4
          ? new PgLineSegment(
            new PgPoint(values[0], values[1]),
            new PgPoint(values[2], values[3]))
          : throw new FormatException(
            "Invalid PostgreSQL line segment.");
    }

    private static PgBox ParseBox(string text)
    {
        var values = ParseDoubles(text);
        return values.Length == 4
          ? new PgBox(
            new PgPoint(values[0], values[1]),
            new PgPoint(values[2], values[3]))
          : throw new FormatException("Invalid PostgreSQL box.");
    }

    private static PgPath ParsePath(string text) =>
      new(ParsePoints(text), text.Length > 0 && text[0] == '(');

    private static PgPolygon ParsePolygon(string text) =>
      new(ParsePoints(text));

    private static PgCircle ParseCircle(string text)
    {
        var values = ParseDoubles(text);
        return values.Length == 3
          ? new PgCircle(
            new PgPoint(values[0], values[1]),
            values[2])
          : throw new FormatException("Invalid PostgreSQL circle.");
    }

    private static PgPoint[] ParsePoints(string text)
    {
        var values = ParseDoubles(text);
        if (values.Length == 0 || values.Length % 2 != 0)
        {
            throw new FormatException(
              "Invalid PostgreSQL point collection.");
        }

        PgPoint[] points = new PgPoint[values.Length / 2];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = new PgPoint(
              values[i * 2],
              values[(i * 2) + 1]);
        }

        return points;
    }

    private static double[] ParseDoubles(string text) =>
      Regex.Matches(
          text,
          @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?",
          RegexOptions.CultureInvariant)
        .Select(match => double.Parse(
          match.Value,
          NumberStyles.Float,
          CultureInfo.InvariantCulture))
        .ToArray();

    private static PgInet ParseInet(string text)
    {
        (var address, var prefix) = ParseNetwork(text);
        return new PgInet(address, prefix);
    }

    private static PgCidr ParseCidr(string text)
    {
        (var address, var prefix) = ParseNetwork(text);
        var requiredPrefix = prefix ??
          (address.AddressFamily ==
           System.Net.Sockets.AddressFamily.InterNetwork
            ? 32
            : 128);
        return new PgCidr(address, requiredPrefix);
    }

    private static (
      IPAddress Address,
      int? Prefix) ParseNetwork(string text)
    {
        var separator = text.LastIndexOf('/');
        return separator < 0
          ? (IPAddress.Parse(text), null)
          : (
            IPAddress.Parse(text[..separator]),
            int.Parse(
              text[(separator + 1)..],
              CultureInfo.InvariantCulture));
    }

    private static PgMoney ParseMoney(string text)
    {
        string normalized = new(
          text.Where(character =>
              char.IsDigit(character) ||
              character is '-' or '+' or '.')
            .ToArray());
        return new PgMoney(
          decimal.Parse(
            normalized,
            CultureInfo.InvariantCulture));
    }

    private static TimeSpan ToTimeSpan(PgInterval value)
    {
        if (value.Years != 0 || value.Months != 0)
        {
            throw new InvalidCastException(
              "PostgreSQL intervals containing years or months cannot be read as TimeSpan.");
        }

        long microseconds = checked(
          (((((long)value.Days * 24) + value.Hours) * 60 + value.Minutes) * 60 + value.Seconds) *
          1_000_000 + value.Microseconds);
        return TimeSpan.FromTicks(checked(microseconds * 10));
    }

    public static string FormatParameter(SqlValue value) =>
      value.Kind switch
      {
          SqlValueKind.Null =>
          throw new InvalidOperationException(
            "NULL parameters have no text payload."),
          SqlValueKind.Boolean =>
          value.Get<bool>() ? "true" : "false",
          SqlValueKind.Bytes =>
          "\\x" + Convert.ToHexStringLower(
            value.GetRequired<byte[]>()),
          SqlValueKind.ReadOnlyMemory =>
          "\\x" + Convert.ToHexStringLower(
            value.Get<ReadOnlyMemory<byte>>().Span),
          SqlValueKind.DateOnly =>
          value.Get<DateOnly>().ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture),
          SqlValueKind.TimeOnly =>
          value.Get<TimeOnly>().ToString(
            "HH:mm:ss.fffffff",
            CultureInfo.InvariantCulture),
          SqlValueKind.DateTime =>
          value.Get<DateTime>().ToString(
            "yyyy-MM-dd HH:mm:ss.fffffff",
            CultureInfo.InvariantCulture),
          SqlValueKind.DateTimeOffset =>
          value.Get<DateTimeOffset>().ToString(
            "yyyy-MM-dd HH:mm:ss.fffffffzzz",
            CultureInfo.InvariantCulture),
          SqlValueKind.Guid => value.Get<Guid>().ToString("D"),
          SqlValueKind.JsonDocument =>
          value.GetRequired<JsonDocument>().RootElement.GetRawText(),
          SqlValueKind.JsonElement =>
          value.Get<JsonElement>().GetRawText(),
          SqlValueKind.Object when value.ToObject() is PgPoint point =>
          FormatPoint(point),
          SqlValueKind.Object when value.ToObject() is PgLine line =>
          FormatLine(line),
          SqlValueKind.Object when value.ToObject() is PgLineSegment segment =>
          FormatLineSegment(segment),
          SqlValueKind.Object when value.ToObject() is PgBox box =>
          FormatBox(box),
          SqlValueKind.Object when value.ToObject() is PgPath path =>
          FormatPath(path),
          SqlValueKind.Object when value.ToObject() is PgPolygon polygon =>
          FormatPolygon(polygon),
          SqlValueKind.Object when value.ToObject() is PgCircle circle =>
          FormatCircle(circle),
          SqlValueKind.Object when value.ToObject() is PgPoint[] points =>
          FormatArray(points, FormatPoint),
          SqlValueKind.Object when value.ToObject() is PgLine[] lines =>
          FormatArray(lines, FormatLine),
          SqlValueKind.Object when value.ToObject() is PgLineSegment[] segments =>
          FormatArray(segments, FormatLineSegment),
          SqlValueKind.Object when value.ToObject() is PgBox[] boxes =>
          FormatArray(boxes, FormatBox, ';'),
          SqlValueKind.Object when value.ToObject() is PgPath[] paths =>
          FormatArray(paths, FormatPath),
          SqlValueKind.Object when value.ToObject() is PgPolygon[] polygons =>
          FormatArray(polygons, FormatPolygon),
          SqlValueKind.Object when value.ToObject() is PgCircle[] circles =>
          FormatArray(circles, FormatCircle),
          SqlValueKind.Object when value.ToObject() is PgTimeWithTimeZone timeWithTimeZone =>
          FormatTimeWithTimeZone(timeWithTimeZone),
          SqlValueKind.Object when value.ToObject() is PgInterval interval =>
          FormatInterval(interval),
          SqlValueKind.Object when value.ToObject() is PgInet inet =>
          inet.Address + (inet.PrefixLength is { } prefix ? "/" + prefix : string.Empty),
          SqlValueKind.Object when value.ToObject() is PgCidr cidr =>
          cidr.Address + "/" + cidr.PrefixLength,
          SqlValueKind.Object when value.ToObject() is PgMoney money =>
          money.Value.ToString(CultureInfo.InvariantCulture),
          SqlValueKind.Object when value.ToObject() is BigInteger integer =>
          integer.ToString(CultureInfo.InvariantCulture),
          SqlValueKind.Object when value.ToObject() is Int128 integer =>
          integer.ToString(CultureInfo.InvariantCulture),
          SqlValueKind.Object when value.ToObject() is UInt128 integer =>
          integer.ToString(CultureInfo.InvariantCulture),
          SqlValueKind.Object when value.ToObject() is Half number =>
          number.ToString(CultureInfo.InvariantCulture),
          SqlValueKind.Object when value.ToObject() is byte unsignedByte =>
          unsignedByte.ToString(CultureInfo.InvariantCulture),
          SqlValueKind.Object when value.ToObject() is sbyte signedByte =>
          signedByte.ToString(CultureInfo.InvariantCulture),
          SqlValueKind.Object when value.ToObject() is char character =>
          character.ToString(),
          SqlValueKind.Object when value.ToObject() is char[] characters =>
          new string(characters),
          SqlValueKind.Object when value.ToObject() is TimeSpan duration =>
          FormatTimeSpan(duration),
          SqlValueKind.Object when value.ToObject() is IPAddress address =>
          address.ToString(),
          SqlValueKind.Object when value.ToObject() is PhysicalAddress address =>
          FormatPhysicalAddress(address),
          SqlValueKind.Object when value.ToObject() is BitArray bits =>
          FormatBitArray(bits),
          SqlValueKind.Object when value.ToObject() is BigInteger[] integers =>
          FormatArray(integers, integer => integer.ToString(CultureInfo.InvariantCulture)),
          SqlValueKind.Object when value.ToObject() is Int128[] integers =>
          FormatArray(integers, integer => integer.ToString(CultureInfo.InvariantCulture)),
          SqlValueKind.Object when value.ToObject() is UInt128[] integers =>
          FormatArray(integers, integer => integer.ToString(CultureInfo.InvariantCulture)),
          SqlValueKind.Object when value.ToObject() is Half[] numbers =>
          FormatArray(numbers, number => number.ToString(CultureInfo.InvariantCulture)),
          SqlValueKind.Object when value.ToObject() is sbyte[] signedBytes =>
          FormatArray(signedBytes, item => item.ToString(CultureInfo.InvariantCulture)),
          SqlValueKind.Object when value.ToObject() is TimeSpan[] durations =>
          FormatArray(durations, FormatTimeSpan),
          SqlValueKind.Object when value.ToObject() is char[][] characterArrays =>
          FormatArray(characterArrays, characters => new string(characters)),
          SqlValueKind.Object when value.ToObject() is IPAddress[] addresses =>
          FormatArray(addresses, address => address.ToString()),
          SqlValueKind.Object when value.ToObject() is PhysicalAddress[] addresses =>
          FormatArray(addresses, FormatPhysicalAddress),
          SqlValueKind.Object when value.ToObject() is BitArray[] bitArrays =>
          FormatArray(bitArrays, FormatBitArray),
          _ => value.ToObject() is IFormattable formattable
          ? formattable.ToString(
            null,
            CultureInfo.InvariantCulture)
          : value.ToObject()?.ToString() ??
            throw new InvalidOperationException(
              "Parameter has no text representation."),
      };

    private static string FormatPoint(PgPoint point) =>
      FormattableString.Invariant($"({point.X},{point.Y})");

    private static string FormatLine(PgLine line) =>
      FormattableString.Invariant($"{{{line.A},{line.B},{line.C}}}");

    private static string FormatLineSegment(PgLineSegment segment) =>
      $"[{FormatPoint(segment.Start)},{FormatPoint(segment.End)}]";

    private static string FormatBox(PgBox box) =>
      $"({FormatPoint(box.UpperRight)},{FormatPoint(box.LowerLeft)})";

    private static string FormatPath(PgPath path) =>
      FormatPoints(path.Points, path.Closed ? '(' : '[', path.Closed ? ')' : ']');

    private static string FormatPolygon(PgPolygon polygon) =>
      FormatPoints(polygon.Points, '(', ')');

    private static string FormatCircle(PgCircle circle) =>
      FormattableString.Invariant($"<{FormatPoint(circle.Center)},{circle.Radius}>");

    private static string FormatTimeWithTimeZone(PgTimeWithTimeZone value)
    {
        var offset = value.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        var formattedOffset = offset.Seconds == 0
          ? FormattableString.Invariant($"{sign}{(int)offset.TotalHours:D2}:{offset.Minutes:D2}")
          : FormattableString.Invariant(
            $"{sign}{(int)offset.TotalHours:D2}:{offset.Minutes:D2}:{offset.Seconds:D2}");
        return value.Time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture) +
          formattedOffset;
    }

    private static string FormatInterval(PgInterval value) =>
      FormattableString.Invariant(
        $"{value.Years} years {value.Months} mons {value.Days} days {value.Hours} hours {value.Minutes} mins {value.Seconds}.{Math.Abs(value.Microseconds):D6} secs");

    private static string FormatTimeSpan(TimeSpan value)
    {
        var negative = value < TimeSpan.Zero;
        decimal absoluteTicks = Math.Abs((decimal)value.Ticks);
        long days = decimal.ToInt64(decimal.Truncate(absoluteTicks / TimeSpan.TicksPerDay));
        long remainingTicks = decimal.ToInt64(absoluteTicks % TimeSpan.TicksPerDay);
        var timeValue = TimeSpan.FromTicks(remainingTicks);
        var time = timeValue.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        if (timeValue.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            time += "." +
              (timeValue.Ticks % TimeSpan.TicksPerSecond)
                .ToString("D7", CultureInfo.InvariantCulture)
                .TrimEnd('0');
        }
        var prefix = negative ? "-" : string.Empty;
        return days == 0
          ? prefix + time
          : FormattableString.Invariant($"{prefix}{days} days {time}");
    }

    private static string FormatPhysicalAddress(PhysicalAddress value) =>
      string.Join(
        ':',
        value.GetAddressBytes().Select(octet =>
          octet.ToString("x2", CultureInfo.InvariantCulture)));

    private static string FormatBitArray(BitArray value)
    {
        var characters = new char[value.Count];
        for (var i = 0; i < value.Count; i++)
        {
            characters[i] = value[i] ? '1' : '0';
        }

        return new string(characters);
    }

    private static string FormatPoints(
        IReadOnlyList<PgPoint> points,
        char opening,
        char closing) =>
      opening + string.Join(',', points.Select(FormatPoint)) + closing;

    private static string FormatArray<T>(
        IReadOnlyList<T> values,
        Func<T, string> formatter,
        char delimiter = ',') =>
      "{" + string.Join(delimiter, values.Select(value =>
        "\"" + formatter(value)
          .Replace("\\", "\\\\", StringComparison.Ordinal)
          .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"")) + "}";
}

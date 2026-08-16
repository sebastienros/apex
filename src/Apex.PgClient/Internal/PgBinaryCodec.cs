using System.Buffers.Binary;
using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Apex.PgClient.Internal;

internal static class PgBinaryCodec
{
    private static readonly DateOnly s_pgDateEpoch = new(2000, 1, 1);
    private static readonly DateTime s_pgTimestampEpoch =
      new(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTimeOffset s_pgTimestampWithTimeZoneEpoch =
      new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static object Decode(uint typeId, ReadOnlyMemory<byte> memory)
    {
        var value = memory.Span;
        return typeId switch
        {
            16 => DecodeBoolean(value),
            17 => DecodeBytes(value),
            20 => DecodeInt64(value),
            21 => DecodeInt16(value),
            23 => DecodeInt32(value),
            26 or 142 or 2278 =>
              throw new PgUnsupportedTypeException(typeId),
            774 or 829 => DecodePhysicalAddress(value),
            1560 or 1562 => DecodeBitArray(value),
            700 => DecodeFloat(value),
            701 => DecodeDouble(value),
            790 => DecodeMoney(value),
            1082 => DecodeDateOnly(value),
            1083 => DecodeTimeOnly(value),
            1114 => DecodeDateTime(value),
            1184 => DecodeDateTimeOffset(value),
            1186 => DecodeInterval(value),
            1266 => DecodeTimeWithTimeZone(value),
            1700 => DecodeNumeric(value),
            2950 => DecodeGuid(value),
            600 => DecodePoint(value),
            601 => DecodeLineSegment(value),
            602 => DecodePath(value),
            603 => DecodeBox(value),
            604 => DecodePolygon(value),
            628 => DecodeLine(value),
            650 => DecodeCidr(value),
            718 => DecodeCircle(value),
            869 => DecodeInet(value),
            114 => DecodeJson(memory),
            3802 => DecodeJsonb(memory),
            18 or 19 or 25 or 1042 or 1043 => DecodeString(value),
            1000 or 1001 or 1002 or 1003 or 1005 or 1007 or 1009 or 1015 or
            1016 or 1017 or 1018 or 1019 or 1020 or 1021 or 1022 or 1027 or
            1041 or 1115 or 1182 or 1183 or 1185 or 1187 or 1231 or 1270 or
            199 or 629 or 651 or 719 or 775 or 791 or 1040 or 1561 or 1563 or 2951 or 3807 =>
              DecodeArrayObject(typeId, memory),
            _ => throw new PgUnsupportedTypeException(typeId),
        };
    }

    internal static bool DecodeBoolean(ReadOnlySpan<byte> value) =>
      ReadByte(value) != 0;

    internal static byte[] DecodeBytes(ReadOnlySpan<byte> value) =>
      value.ToArray();

    internal static short DecodeInt16(ReadOnlySpan<byte> value) =>
      ReadInt16(value);

    internal static int DecodeInt32(ReadOnlySpan<byte> value) =>
      ReadInt32(value);

    internal static long DecodeInt64(ReadOnlySpan<byte> value) =>
      ReadInt64(value);

    internal static float DecodeFloat(ReadOnlySpan<byte> value) =>
      BitConverter.Int32BitsToSingle(ReadInt32(value));

    internal static double DecodeDouble(ReadOnlySpan<byte> value) =>
      BitConverter.Int64BitsToDouble(ReadInt64(value));

    internal static decimal DecodeDecimal(ReadOnlySpan<byte> value) =>
      DecodeNumeric(value).ToDecimal();

    internal static BigInteger DecodeBigInteger(ReadOnlySpan<byte> value)
    {
        var numeric = DecodeNumeric(value);
        if (!numeric.IsFinite || numeric.Scale != 0)
        {
            throw new InvalidCastException(
              "PostgreSQL numeric value must be a finite integer to be read as BigInteger.");
        }

        return numeric.UnscaledValue;
    }

    internal static string DecodeString(ReadOnlySpan<byte> value) =>
      Encoding.UTF8.GetString(value);

    internal static char DecodeChar(ReadOnlySpan<byte> value) =>
      PgTextCodec.DecodeChar(value);

    internal static char[] DecodeChars(ReadOnlySpan<byte> value) =>
      PgTextCodec.DecodeChars(value);

    internal static PgMoney DecodeMoney(ReadOnlySpan<byte> value) =>
      new(ReadInt64(value) / 100m);

    internal static DateOnly DecodeDateOnly(ReadOnlySpan<byte> value)
    {
        var days = ReadInt32(value);
        return days switch
        {
            int.MaxValue => DateOnly.MaxValue,
            int.MinValue => DateOnly.MinValue,
            _ => s_pgDateEpoch.AddDays(days),
        };
    }

    internal static TimeOnly DecodeTimeOnly(ReadOnlySpan<byte> value) =>
      TimeOnly.FromTimeSpan(TimeSpan.FromTicks(ReadInt64(value) * 10));

    internal static DateTime DecodeDateTime(ReadOnlySpan<byte> value)
    {
        var microseconds = ReadInt64(value);
        return microseconds switch
        {
            long.MaxValue => DateTime.MaxValue,
            long.MinValue => DateTime.MinValue,
            _ => s_pgTimestampEpoch.AddTicks(microseconds * 10),
        };
    }

    internal static DateTimeOffset DecodeDateTimeOffset(ReadOnlySpan<byte> value)
    {
        var microseconds = ReadInt64(value);
        return microseconds switch
        {
            long.MaxValue => DateTimeOffset.MaxValue,
            long.MinValue => DateTimeOffset.MinValue,
            _ => s_pgTimestampWithTimeZoneEpoch.AddTicks(microseconds * 10),
        };
    }

    internal static PgInterval DecodeInterval(ReadOnlySpan<byte> value)
    {
        Ensure(value, 0, 16);
        var microseconds = ReadInt64(value);
        var days = ReadInt32(value[8..]);
        var months = ReadInt32(value[12..]);
        var seconds = Math.DivRem(
          microseconds,
          1_000_000,
          out var remainingMicros);
        var hours = Math.DivRem(seconds, 3600, out var remainingSeconds);
        var minutes = Math.DivRem(
          remainingSeconds,
          60,
          out var finalSeconds);
        return new PgInterval(
          months / 12,
          months % 12,
          days,
          checked((int)hours),
          checked((int)minutes),
          checked((int)finalSeconds),
          checked((int)remainingMicros));
    }

    internal static TimeSpan DecodeTimeSpan(ReadOnlySpan<byte> value)
    {
        var interval = DecodeInterval(value);
        if (interval.Years != 0 || interval.Months != 0)
        {
            throw new InvalidCastException(
              "PostgreSQL intervals containing years or months cannot be read as TimeSpan.");
        }

        long microseconds = checked(
          (((((long)interval.Days * 24) + interval.Hours) * 60 + interval.Minutes) * 60 + interval.Seconds) *
          1_000_000 + interval.Microseconds);
        return TimeSpan.FromTicks(checked(microseconds * 10));
    }

    internal static PgTimeWithTimeZone DecodeTimeWithTimeZone(
        ReadOnlySpan<byte> value)
    {
        Ensure(value, 0, 12);
        TimeOnly time =
          TimeOnly.FromTimeSpan(TimeSpan.FromTicks(ReadInt64(value) * 10));
        TimeSpan offset = TimeSpan.FromSeconds(-ReadInt32(value[8..]));
        return new PgTimeWithTimeZone(time, offset);
    }

    internal static PgNumeric DecodeNumeric(ReadOnlySpan<byte> value)
    {
        var position = 0;
        int digitCount = ReadInt16(value, ref position);
        int weight = ReadInt16(value, ref position);
        var sign = unchecked((ushort)ReadInt16(value, ref position));
        int displayScale = unchecked((ushort)ReadInt16(value, ref position));
        if (sign == 0xC000)
        {
            return PgNumeric.NaN;
        }

        if (sign == 0xD000)
        {
            return PgNumeric.PositiveInfinity;
        }

        if (sign == 0xF000)
        {
            return PgNumeric.NegativeInfinity;
        }

        var coefficient = BigInteger.Zero;
        for (var i = 0; i < digitCount; i++)
        {
            int digit = unchecked((ushort)ReadInt16(value, ref position));
            if (digit > 9999)
            {
                throw new InvalidDataException(
                  "Invalid PostgreSQL numeric base-10000 digit.");
            }

            coefficient = (coefficient * 10000) + digit;
        }

        var fractionalGroups = digitCount - weight - 1;
        if (fractionalGroups < 0)
        {
            coefficient *= BigInteger.Pow(10000, -fractionalGroups);
            fractionalGroups = 0;
        }

        var scale = Math.Max(0, fractionalGroups * 4);
        if (scale > displayScale)
        {
            coefficient /= BigInteger.Pow(10, scale - displayScale);
            scale = displayScale;
        }
        else if (scale < displayScale)
        {
            coefficient *= BigInteger.Pow(10, displayScale - scale);
            scale = displayScale;
        }

        if (sign == 0x4000)
        {
            coefficient = -coefficient;
        }

        return PgNumeric.Create(coefficient, scale);
    }

    internal static Guid DecodeGuid(ReadOnlySpan<byte> value)
    {
        Ensure(value, 0, 16);
        return new Guid(value[..16], bigEndian: true);
    }

    internal static PgPoint DecodePoint(ReadOnlySpan<byte> value)
    {
        Ensure(value, 0, 16);
        return new PgPoint(ReadDouble(value), ReadDouble(value[8..]));
    }

    internal static PgLineSegment DecodeLineSegment(ReadOnlySpan<byte> value) =>
      new(DecodePoint(value), DecodePoint(value[16..]));

    internal static PgPath DecodePath(ReadOnlySpan<byte> value) =>
      new(DecodePoints(value, hasClosedFlag: true, out var closed), closed);

    internal static PgBox DecodeBox(ReadOnlySpan<byte> value) =>
      new(DecodePoint(value), DecodePoint(value[16..]));

    internal static PgPolygon DecodePolygon(ReadOnlySpan<byte> value) =>
      new(DecodePoints(value, hasClosedFlag: false));

    internal static PgLine DecodeLine(ReadOnlySpan<byte> value)
    {
        Ensure(value, 0, 24);
        return new PgLine(
          ReadDouble(value),
          ReadDouble(value[8..]),
          ReadDouble(value[16..]));
    }

    internal static PgCidr DecodeCidr(ReadOnlySpan<byte> value)
    {
        (var address, var prefix, _) = DecodeNetwork(value);
        return new PgCidr(address, prefix);
    }

    internal static PgCircle DecodeCircle(ReadOnlySpan<byte> value) =>
      new(DecodePoint(value), ReadDouble(value[16..]));

    internal static PgInet DecodeInet(ReadOnlySpan<byte> value)
    {
        (var address, var prefix, _) = DecodeNetwork(value);
        return new PgInet(address, prefix);
    }

    internal static IPAddress DecodeIPAddress(ReadOnlySpan<byte> value) =>
      DecodeInet(value).Address;

    internal static PhysicalAddress DecodePhysicalAddress(ReadOnlySpan<byte> value)
    {
        if (value.Length is not (6 or 8))
        {
            throw new InvalidDataException(
              "PostgreSQL MACADDR value must contain 6 or 8 bytes.");
        }

        return new PhysicalAddress(value.ToArray());
    }

    internal static BitArray DecodeBitArray(ReadOnlySpan<byte> value)
    {
        var bitCount = ReadInt32(value);
        if (bitCount < 0 || value.Length != sizeof(int) + ((bitCount + 7) / 8))
        {
            throw new InvalidDataException("Invalid PostgreSQL BIT value length.");
        }

        var result = new BitArray(bitCount);
        var bytes = value[sizeof(int)..];
        for (var i = 0; i < bitCount; i++)
        {
            result[i] = (bytes[i / 8] & (1 << (7 - (i % 8)))) != 0;
        }

        return result;
    }

    internal static JsonElement DecodeJson(ReadOnlyMemory<byte> value)
    {
        using JsonDocument document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    internal static JsonElement DecodeJsonb(ReadOnlyMemory<byte> value)
    {
        var span = value.Span;
        Ensure(span, 0, 1);
        if (span[0] != 1)
        {
            throw new InvalidDataException(
              $"Unsupported PostgreSQL jsonb version {span[0]}.");
        }

        return DecodeJson(value[1..]);
    }

    internal static TElement[] DecodeArray<TElement>(
      uint arrayTypeId,
      ReadOnlyMemory<byte> value)
    {
        var position = 0;
        var span = value.Span;
        var dimensions = ReadInt32(span, ref position);
        _ = ReadInt32(span, ref position);
        var elementType = unchecked((uint)ReadInt32(span, ref position));
        var expectedElementType = PgTextCodec.GetArrayElementType(arrayTypeId);
        if (elementType != expectedElementType)
        {
            throw new InvalidDataException(
              $"PostgreSQL array element OID {elementType} does not match expected OID {expectedElementType}.");
        }

        if (dimensions == 0)
        {
            return [];
        }

        if (dimensions != 1)
        {
            throw new NotSupportedException(
              "Multidimensional PostgreSQL arrays are not supported yet.");
        }

        var count = ReadInt32(span, ref position);
        _ = ReadInt32(span, ref position);
        if (count < 0 || count > (span.Length - position) / sizeof(int))
        {
            throw new InvalidDataException(
              "PostgreSQL array element count exceeds its payload.");
        }

        var result = new TElement[count];
        for (var i = 0; i < count; i++)
        {
            var length = ReadInt32(span, ref position);
            if (length < 0)
            {
                if (default(TElement) is not null)
                {
                    throw new InvalidCastException(
                      $"PostgreSQL array element {i} is NULL and cannot be read as {typeof(TElement).FullName}.");
                }

                continue;
            }

            Ensure(span, position, length);
            result[i] = DecodeArrayElement<TElement>(
              elementType,
              value.Slice(position, length));
            position += length;
        }

        return result;
    }

    private static object DecodeArrayObject(
      uint arrayTypeId,
      ReadOnlyMemory<byte> value) =>
      PgTextCodec.GetArrayElementType(arrayTypeId) switch
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
      ReadOnlyMemory<byte> memory)
    {
        var value = memory.Span;
        return elementType switch
        {
            16 => ConvertValue<TElement, bool>(DecodeBoolean(value), elementType),
            17 => ConvertReference<TElement, byte[]>(DecodeBytes(value), elementType),
            18 or 19 or 25 or 1043 =>
              ConvertText<TElement>(DecodeString(value), elementType),
            20 => ConvertValue<TElement, long>(DecodeInt64(value), elementType),
            21 => ConvertInt16<TElement>(DecodeInt16(value), elementType),
            23 => ConvertValue<TElement, int>(DecodeInt32(value), elementType),
            700 => ConvertFloat<TElement>(DecodeFloat(value), elementType),
            701 => ConvertValue<TElement, double>(DecodeDouble(value), elementType),
            790 => ConvertValue<TElement, PgMoney>(DecodeMoney(value), elementType),
            1082 => ConvertValue<TElement, DateOnly>(DecodeDateOnly(value), elementType),
            1083 => ConvertTime<TElement>(DecodeTimeOnly(value), elementType),
            1114 => ConvertValue<TElement, DateTime>(DecodeDateTime(value), elementType),
            1184 => ConvertValue<TElement, DateTimeOffset>(DecodeDateTimeOffset(value), elementType),
            1186 => ConvertInterval<TElement>(DecodeInterval(value), elementType),
            1266 => ConvertValue<TElement, PgTimeWithTimeZone>(DecodeTimeWithTimeZone(value), elementType),
            1700 => ConvertNumeric<TElement>(DecodeNumeric(value), elementType),
            2950 => ConvertValue<TElement, Guid>(DecodeGuid(value), elementType),
            600 => ConvertValue<TElement, PgPoint>(DecodePoint(value), elementType),
            601 => ConvertValue<TElement, PgLineSegment>(DecodeLineSegment(value), elementType),
            602 => ConvertReference<TElement, PgPath>(DecodePath(value), elementType),
            603 => ConvertValue<TElement, PgBox>(DecodeBox(value), elementType),
            604 => ConvertReference<TElement, PgPolygon>(DecodePolygon(value), elementType),
            628 => ConvertValue<TElement, PgLine>(DecodeLine(value), elementType),
            650 => ConvertValue<TElement, PgCidr>(DecodeCidr(value), elementType),
            718 => ConvertValue<TElement, PgCircle>(DecodeCircle(value), elementType),
            869 => ConvertInet<TElement>(DecodeInet(value), elementType),
            774 or 829 => ConvertReference<TElement, PhysicalAddress>(
              DecodePhysicalAddress(value), elementType),
            1560 or 1562 => ConvertReference<TElement, BitArray>(
              DecodeBitArray(value), elementType),
            114 => ConvertValue<TElement, JsonElement>(DecodeJson(memory), elementType),
            3802 => ConvertValue<TElement, JsonElement>(DecodeJsonb(memory), elementType),
            _ => throw new PgUnsupportedTypeException(elementType),
        };
    }

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

    private static TElement ConvertInterval<TElement>(PgInterval value, uint elementType)
    {
        if (typeof(TElement) == typeof(TimeSpan) || typeof(TElement) == typeof(TimeSpan?))
        {
            var interval = DecodeTimeSpanValue(value);
            return ConvertValue<TElement, TimeSpan>(interval, elementType);
        }

        return ConvertValue<TElement, PgInterval>(value, elementType);
    }

    private static TimeSpan DecodeTimeSpanValue(PgInterval value)
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

    private static TElement ConvertInet<TElement>(PgInet value, uint elementType) =>
      typeof(TElement) == typeof(IPAddress)
      ? ConvertReference<TElement, IPAddress>(value.Address, elementType)
      : ConvertValue<TElement, PgInet>(value, elementType);

    private static InvalidCastException CannotReadArrayElement<TElement>(
      uint elementType) =>
      new(
      $"PostgreSQL array element type OID {elementType} cannot be read as {typeof(TElement).FullName}.");

    private static PgPoint[] DecodePoints(
        ReadOnlySpan<byte> value,
        bool hasClosedFlag) =>
      DecodePoints(value, hasClosedFlag, out _);

    private static PgPoint[] DecodePoints(
        ReadOnlySpan<byte> value,
        bool hasClosedFlag,
        out bool closed)
    {
        var position = 0;
        closed = hasClosedFlag && ReadByte(value, ref position) != 0;
        var count = ReadInt32(value, ref position);
        if (count < 0 || count > (value.Length - position) / 16)
        {
            throw new InvalidDataException(
              "PostgreSQL point count exceeds its payload.");
        }

        PgPoint[] points = new PgPoint[count];
        for (var i = 0; i < count; i++)
        {
            Ensure(value, position, 16);
            points[i] = DecodePoint(value[position..]);
            position += 16;
        }

        return points;
    }

    private static (
      IPAddress Address,
      int Prefix,
      bool Cidr) DecodeNetwork(ReadOnlySpan<byte> value)
    {
        Ensure(value, 0, 4);
        int addressLength = value[3];
        Ensure(value, 4, addressLength);
        return (
          new IPAddress(value.Slice(4, addressLength)),
          value[1],
          value[2] != 0);
    }

    private static byte ReadByte(ReadOnlySpan<byte> value)
    {
        Ensure(value, 0, 1);
        return value[0];
    }

    private static byte ReadByte(
        ReadOnlySpan<byte> value,
        ref int position)
    {
        Ensure(value, position, 1);
        return value[position++];
    }

    private static short ReadInt16(ReadOnlySpan<byte> value)
    {
        Ensure(value, 0, 2);
        return BinaryPrimitives.ReadInt16BigEndian(value);
    }

    private static short ReadInt16(
        ReadOnlySpan<byte> value,
        ref int position)
    {
        Ensure(value, position, 2);
        var result = BinaryPrimitives.ReadInt16BigEndian(value[position..]);
        position += 2;
        return result;
    }

    private static int ReadInt32(ReadOnlySpan<byte> value)
    {
        Ensure(value, 0, 4);
        return BinaryPrimitives.ReadInt32BigEndian(value);
    }

    private static int ReadInt32(
        ReadOnlySpan<byte> value,
        ref int position)
    {
        Ensure(value, position, 4);
        var result = BinaryPrimitives.ReadInt32BigEndian(value[position..]);
        position += 4;
        return result;
    }

    private static long ReadInt64(ReadOnlySpan<byte> value)
    {
        Ensure(value, 0, 8);
        return BinaryPrimitives.ReadInt64BigEndian(value);
    }

    private static double ReadDouble(ReadOnlySpan<byte> value) =>
      BitConverter.Int64BitsToDouble(ReadInt64(value));

    private static void Ensure(
        ReadOnlySpan<byte> value,
        int position,
        int length)
    {
        if (length < 0 || position < 0 || position > value.Length - length)
        {
            throw new InvalidDataException(
              "PostgreSQL binary value is truncated.");
        }
    }
}

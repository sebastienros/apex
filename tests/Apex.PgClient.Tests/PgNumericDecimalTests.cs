using System.Buffers.Binary;
using System.Globalization;
using Apex.PgClient.Internal;

namespace Apex.PgClient.Tests;

// PgBinaryCodec.DecodeDecimal has a direct 96-bit fast path. These tests check it against the
// original PgNumeric -> string -> decimal.Parse path, which stays as the fallback.
[TestClass]
public sealed class PgNumericDecimalTests
{
    [TestMethod]
    [DataRow("0")]
    [DataRow("0.00")]
    [DataRow("1")]
    [DataRow("-1")]
    [DataRow("1.50")]
    [DataRow("1234.50")]
    [DataRow("12345.6789")]
    [DataRow("-1234567890123456.789012")]
    [DataRow("0.0001")]
    [DataRow("0.00000001")]
    [DataRow("10000")]
    [DataRow("100000000")]
    [DataRow("100.00")]
    [DataRow("79228162514264337593543950335")]
    [DataRow("-79228162514264337593543950335")]
    [DataRow("7.9228162514264337593543950335")]
    [DataRow("0.0000000000000000000000000001")]
    [DataRow("0.0000000000000000000000000000")]
    [DataRow("79228162514264337593543950336")]
    [DataRow("123456789012345678901234567890")]
    [DataRow("0.00000000000000000000000000001")]
    [DataRow("1.00000000000000000000000000000")]
    public void DecodeDecimalMatchesTheParsePath(string text)
    {
        var payload = Encode(text);
        AssertSameAsParsePath(payload);
    }

    [TestMethod]
    public void DecodeDecimalPreservesTheDisplayScale()
    {
        var value = PgBinaryCodec.DecodeDecimal(Encode("1.50"));
        Assert.AreEqual("1.50", value.ToString(CultureInfo.InvariantCulture));
        value = PgBinaryCodec.DecodeDecimal(Encode("100.000"));
        Assert.AreEqual("100.000", value.ToString(CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void DecodeDecimalMatchesTheParsePathForRandomRawPayloads()
    {
        // Raw payloads, including ones PostgreSQL would never send (digits past dscale,
        // dscale past the digits, leading zero groups, huge weights), must still agree.
        var random = new Random(20261122);
        for (var iteration = 0; iteration < 200_000; iteration++)
        {
            var digitCount = random.Next(0, 10);
            var digits = new short[digitCount];
            for (var i = 0; i < digitCount; i++)
            {
                digits[i] = random.Next(4) switch
                {
                    0 => 0,
                    1 => 9999,
                    _ => (short)random.Next(0, 10000),
                };
            }

            var weight = (short)random.Next(-10, 10);
            var sign = random.Next(2) == 0 ? (ushort)0 : (ushort)0x4000;
            var displayScale = (short)random.Next(0, 34);
            AssertSameAsParsePath(Raw(weight, sign, displayScale, digits));
        }
    }

    [TestMethod]
    public void DecodeDecimalMatchesTheParsePathForRandomDecimals()
    {
        var random = new Random(42);
        Span<int> bits = stackalloc int[4];
        Span<int> expected = stackalloc int[4];
        for (var iteration = 0; iteration < 100_000; iteration++)
        {
            var value = new decimal(
                random.Next(),
                random.Next(4) == 0 ? 0 : random.Next(),
                random.Next(3) == 0 ? 0 : random.Next(),
                random.Next(2) == 0,
                (byte)random.Next(0, 29));
            var payload = Encode(value.ToString(CultureInfo.InvariantCulture));
            var decoded = PgBinaryCodec.DecodeDecimal(payload);
            decimal.GetBits(decoded, bits);
            decimal.GetBits(value, expected);
            CollectionAssert.AreEqual(expected.ToArray(), bits.ToArray(), value.ToString(CultureInfo.InvariantCulture));
            AssertSameAsParsePath(payload);
        }
    }

    [TestMethod]
    public void DecodeDecimalKeepsSpecialValueErrors()
    {
        Assert.ThrowsExactly<OverflowException>(() => PgBinaryCodec.DecodeDecimal(Raw(0, 0xC000, 0)));
        Assert.ThrowsExactly<OverflowException>(() => PgBinaryCodec.DecodeDecimal(Raw(0, 0xD000, 0)));
        Assert.ThrowsExactly<OverflowException>(() => PgBinaryCodec.DecodeDecimal(Raw(0, 0xF000, 0)));
        Assert.ThrowsExactly<InvalidDataException>(() => PgBinaryCodec.DecodeDecimal(Raw(0, 0, 0, 10000)));
    }

    private static void AssertSameAsParsePath(byte[] payload)
    {
        decimal expected;
        try
        {
            expected = PgBinaryCodec.DecodeNumeric(payload).ToDecimal();
        }
        catch (Exception expectedError)
        {
            try
            {
                PgBinaryCodec.DecodeDecimal(payload);
            }
            catch (Exception actualError)
            {
                Assert.AreEqual(expectedError.GetType(), actualError.GetType(), Convert.ToHexString(payload));
                return;
            }

            Assert.Fail($"{Convert.ToHexString(payload)}: expected {expectedError.GetType().Name}.");
            return;
        }

        var actual = PgBinaryCodec.DecodeDecimal(payload);
        CollectionAssert.AreEqual(
            decimal.GetBits(expected),
            decimal.GetBits(actual),
            $"{Convert.ToHexString(payload)}: expected {expected}, got {actual}");
    }

    // Encodes a decimal literal the way PostgreSQL's numeric_send does: base-10000 groups aligned on
    // the decimal point, leading and trailing zero groups stripped, dscale = written fraction digits.
    private static byte[] Encode(string text)
    {
        var negative = text.StartsWith('-');
        var unsigned = negative ? text[1..] : text;
        var point = unsigned.IndexOf('.');
        var integer = point < 0 ? unsigned : unsigned[..point];
        var fraction = point < 0 ? string.Empty : unsigned[(point + 1)..];
        var displayScale = (short)fraction.Length;
        var integerPadded = integer.PadLeft((integer.Length + 3) / 4 * 4, '0');
        var fractionPadded = fraction.PadRight((fraction.Length + 3) / 4 * 4, '0');
        var groups = new List<short>();
        for (var i = 0; i < integerPadded.Length; i += 4)
        {
            groups.Add(short.Parse(integerPadded.AsSpan(i, 4), CultureInfo.InvariantCulture));
        }

        var weight = groups.Count - 1;
        for (var i = 0; i < fractionPadded.Length; i += 4)
        {
            groups.Add(short.Parse(fractionPadded.AsSpan(i, 4), CultureInfo.InvariantCulture));
        }

        while (groups.Count > 0 && groups[0] == 0)
        {
            groups.RemoveAt(0);
            weight--;
        }

        while (groups.Count > 0 && groups[^1] == 0)
        {
            groups.RemoveAt(groups.Count - 1);
        }

        if (groups.Count == 0)
        {
            weight = 0;
            negative = false;
        }

        return Raw((short)weight, negative ? (ushort)0x4000 : (ushort)0, displayScale, [.. groups]);
    }

    private static byte[] Raw(short weight, ushort sign, short displayScale, params short[] digits)
    {
        var payload = new byte[(4 + digits.Length) * sizeof(short)];
        BinaryPrimitives.WriteInt16BigEndian(payload, (short)digits.Length);
        BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(2), weight);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), sign);
        BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(6), displayScale);
        for (var i = 0; i < digits.Length; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(8 + (i * 2)), digits[i]);
        }

        return payload;
    }
}

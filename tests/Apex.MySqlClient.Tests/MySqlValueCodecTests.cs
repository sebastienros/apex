using System.Text;
using Apex.MySqlClient.Internal;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlValueCodecTests
{
    [TestMethod]
    public void ParsesIntegerFloatAndDecimalText()
    {
        Assert.AreEqual(-42L, MySqlValueCodec.ParseInt64(Bytes("-42")));
        Assert.AreEqual(18446744073709551615ul, MySqlValueCodec.ParseUInt64(Bytes("18446744073709551615")));
        Assert.AreEqual(1.5f, MySqlValueCodec.ParseSingle(Bytes("1.5")));
        Assert.AreEqual(2.25d, MySqlValueCodec.ParseDouble(Bytes("2.25")));
        Assert.AreEqual(12.34m, MySqlValueCodec.ParseDecimal(Bytes("12.34")));
    }

    [TestMethod]
    public void RejectsInvalidNumericText()
    {
        Assert.ThrowsExactly<FormatException>(() => MySqlValueCodec.ParseInt64(Bytes("12x")));
        Assert.ThrowsExactly<FormatException>(() => MySqlValueCodec.ParseUInt64(Bytes("-1")));
        Assert.ThrowsExactly<FormatException>(() => MySqlValueCodec.ParseSingle(Bytes("abc")));
        Assert.ThrowsExactly<FormatException>(() => MySqlValueCodec.ParseDouble(Bytes("abc")));
        Assert.ThrowsExactly<FormatException>(() => MySqlValueCodec.ParseDecimal(Bytes("abc")));
    }

    [TestMethod]
    public void ParsesBitColumnsAsBigEndianUnsignedIntegers()
    {
        Assert.AreEqual(0ul, MySqlValueCodec.ParseBit([]));
        Assert.AreEqual(1ul, MySqlValueCodec.ParseBit([1]));
        Assert.AreEqual(0x0102ul, MySqlValueCodec.ParseBit([1, 2]));
        Assert.AreEqual(ulong.MaxValue, MySqlValueCodec.ParseBit(new byte[] { 255, 255, 255, 255, 255, 255, 255, 255 }));
    }

    [TestMethod]
    public void RejectsOversizedBitValue()
    {
        Assert.ThrowsExactly<FormatException>(() => MySqlValueCodec.ParseBit(new byte[9]));
    }

    [TestMethod]
    public void ParsesTextDate()
    {
        var date = MySqlValueCodec.ParseDate(Bytes("2024-03-15"), out var isZero);

        Assert.IsFalse(isZero);
        Assert.AreEqual(new DateOnly(2024, 3, 15), date);
    }

    [TestMethod]
    public void ParsesZeroTextDate()
    {
        _ = MySqlValueCodec.ParseDate(Bytes("0000-00-00"), out var isZero);

        Assert.IsTrue(isZero);
    }

    [TestMethod]
    public void RejectsInvalidTextDate()
    {
        Assert.ThrowsExactly<FormatException>(() => MySqlValueCodec.ParseDate(Bytes("2024-13-01"), out _));
        Assert.ThrowsExactly<FormatException>(() => MySqlValueCodec.ParseDate(Bytes("2024-02-30"), out _));
        Assert.ThrowsExactly<FormatException>(() => MySqlValueCodec.ParseDate(Bytes("not-a-date"), out _));
    }

    [TestMethod]
    public void ParsesTextDateTimeWithFractionalSeconds()
    {
        var timestamp = MySqlValueCodec.ParseDateTime(
          Bytes("2024-03-15 13:45:30.123456"),
          out var isZero);

        Assert.IsFalse(isZero);
        Assert.AreEqual(new DateTime(2024, 3, 15, 13, 45, 30, DateTimeKind.Unspecified).AddTicks(1234560), timestamp);
    }

    [TestMethod]
    public void ParsesTextDateTimeWithoutTimeComponent()
    {
        var timestamp = MySqlValueCodec.ParseDateTime(Bytes("2024-03-15"), out var isZero);

        Assert.IsFalse(isZero);
        Assert.AreEqual(new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Unspecified), timestamp);
    }

    [TestMethod]
    public void ParsesZeroTextDateTime()
    {
        _ = MySqlValueCodec.ParseDateTime(Bytes("0000-00-00 00:00:00"), out var isZero);

        Assert.IsTrue(isZero);
    }

    [TestMethod]
    public void ParsesPositiveAndNegativeTextTime()
    {
        var positive = MySqlValueCodec.ParseTime(Bytes("101:20:30.500000"));
        var negative = MySqlValueCodec.ParseTime(Bytes("-10:20:30"));

        Assert.AreEqual(new TimeSpan(4, 5, 20, 30, 500), positive);
        Assert.AreEqual(-new TimeSpan(0, 10, 20, 30), negative);
    }

    [TestMethod]
    public void RejectsInvalidTextTime()
    {
        Assert.ThrowsExactly<FormatException>(() => MySqlValueCodec.ParseTime(Bytes("aa:bb:cc")));
        Assert.ThrowsExactly<FormatException>(() => MySqlValueCodec.ParseTime(Bytes("10:20")));
    }

    [TestMethod]
    public void ReadsBinaryDateTimeAtEachLengthVariant()
    {
        // The binary protocol truncates trailing zero components: 0 bytes is a zero date/time,
        // 4 bytes carries only the date, 7 adds time, 11 adds microseconds.
        Assert.IsTrue(MySqlValueCodec.ReadBinaryDateTime([], out var isZero) == default && isZero);

        byte[] dateOnly = [0xE8, 0x07, 3, 15];
        var date = MySqlValueCodec.ReadBinaryDateTime(dateOnly, out var dateIsZero);
        Assert.IsFalse(dateIsZero);
        Assert.AreEqual(new DateTime(2024, 3, 15), date);

        byte[] withTime = [0xE8, 0x07, 3, 15, 13, 45, 30];
        var withTimeValue = MySqlValueCodec.ReadBinaryDateTime(withTime, out _);
        Assert.AreEqual(new DateTime(2024, 3, 15, 13, 45, 30), withTimeValue);

        byte[] withMicros = [0xE8, 0x07, 3, 15, 13, 45, 30, 0x40, 0xE2, 0x01, 0x00];
        var withMicrosValue = MySqlValueCodec.ReadBinaryDateTime(withMicros, out _);
        Assert.AreEqual(
          new DateTime(2024, 3, 15, 13, 45, 30, DateTimeKind.Unspecified).AddTicks(1234560),
          withMicrosValue);
    }

    [TestMethod]
    public void ReadsBinaryZeroDate()
    {
        byte[] zero = [0, 0, 0, 0];

        _ = MySqlValueCodec.ReadBinaryDateTime(zero, out var isZero);

        Assert.IsTrue(isZero);
    }

    [TestMethod]
    public void ReadsBinaryTimeAtEachLengthVariant()
    {
        Assert.AreEqual(TimeSpan.Zero, MySqlValueCodec.ReadBinaryTime([]));

        byte[] withoutMicros = [0, 0, 0, 0, 0, 10, 20, 30];
        Assert.AreEqual(new TimeSpan(0, 10, 20, 30), MySqlValueCodec.ReadBinaryTime(withoutMicros));

        byte[] negativeWithMicros = [1, 1, 0, 0, 0, 10, 20, 30, 0x40, 0xE2, 0x01, 0x00];
        var negative = MySqlValueCodec.ReadBinaryTime(negativeWithMicros);
        Assert.AreEqual(-new TimeSpan(1, 10, 20, 30, 123, 456), negative);
    }

    [TestMethod]
    public void RejectsInvalidBinaryTimeLength()
    {
        Assert.ThrowsExactly<FormatException>(() => MySqlValueCodec.ReadBinaryTime(new byte[5]));
    }

    [TestMethod]
    public void DescribeReplacesNonPrintableBytesAndTruncatesLongValues()
    {
        var described = MySqlValueCodec.Describe([0x41, 0x00, 0x42]);
        Assert.AreEqual("A?B", described);

        var long65 = new byte[70];
        Array.Fill(long65, (byte)'x');
        var truncated = MySqlValueCodec.Describe(long65);
        Assert.AreEqual(64, truncated.Length);
    }

    private static byte[] Bytes(string value) => Encoding.ASCII.GetBytes(value);
}

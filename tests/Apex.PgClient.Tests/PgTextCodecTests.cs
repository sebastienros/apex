using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Apex.PgClient.Internal;
using Apex.SqlClient;

namespace Apex.PgClient.Tests;

[TestClass]
public sealed class PgTextCodecTests
{
    [TestMethod]
    public void DecodesNumericWithoutDecimalRangeLoss()
    {
        PgNumeric numeric = (PgNumeric)Decode(1700, "123456789012345678901234567890.1234");

        Assert.AreEqual("123456789012345678901234567890.1234", numeric.ToString());
        Assert.AreEqual("1200", PgNumeric.Parse("1.2e3").ToString());
        Assert.AreEqual(PgNumericSpecialValue.NaN, PgNumeric.Parse("NaN").SpecialValue);
    }

    [TestMethod]
    public void DecodesByteaJsonAndInfinity()
    {
        CollectionAssert.AreEqual(new byte[] { 0, 1, 254, 255 }, (byte[])Decode(17, "\\x0001feff"));
        JsonElement json = (JsonElement)Decode(3802, """{"ok":true}""");

        Assert.IsTrue(json.GetProperty("ok").GetBoolean());
        Assert.AreEqual(DateOnly.MaxValue, Decode(1082, "infinity"));
        Assert.AreEqual(DateTimeOffset.MinValue, Decode(1184, "-infinity"));
    }

    [TestMethod]
    public void DecodesIntervalGeometryAndNetworkTypes()
    {
        PgInterval interval = (PgInterval)Decode(1186, "P1Y2M3DT4H5M6.123456S");
        PgPoint point = (PgPoint)Decode(600, "(1.5,-2.25)");
        PgCidr cidr = (PgCidr)Decode(650, "2001:db8::/64");

        Assert.AreEqual(
          new PgInterval(1, 2, 3, 4, 5, 6, 123456),
          interval);
        Assert.AreEqual(new PgPoint(1.5, -2.25), point);
        Assert.AreEqual(IPAddress.Parse("2001:db8::"), cidr.Address);
        Assert.AreEqual(64, cidr.PrefixLength);
    }

    [TestMethod]
    public void DecodesBclScalarAlternatives()
    {
        Assert.AreEqual(
            BigInteger.Parse("123456789012345678901234567890", CultureInfo.InvariantCulture),
            PgTextCodec.DecodeBigInteger("123456789012345678901234567890"u8));
        Assert.AreEqual(TimeSpan.FromHours(26.5), PgTextCodec.DecodeTimeSpan("P1DT2H30M"u8));
        Assert.AreEqual('x', PgTextCodec.DecodeChar("x"u8));
        CollectionAssert.AreEqual("hello".ToCharArray(), PgTextCodec.DecodeChars("hello"u8));
        Assert.AreEqual(IPAddress.Parse("192.0.2.1"), PgTextCodec.DecodeIPAddress("192.0.2.1/24"u8));
        Assert.AreEqual(
            PhysicalAddress.Parse("08-00-2B-01-02-03"),
            PgTextCodec.DecodePhysicalAddress("08:00:2b:01:02:03"u8));
        CollectionAssert.AreEqual(
            new[] { true, false, true, true },
            ToBooleans(PgTextCodec.DecodeBitArray("1011"u8)));

        Assert.ThrowsExactly<InvalidCastException>(() => PgTextCodec.DecodeChar("xy"u8));
        Assert.ThrowsExactly<InvalidCastException>(() => PgTextCodec.DecodeTimeSpan("P1M"u8));
        Assert.ThrowsExactly<FormatException>(() => PgTextCodec.DecodeBitArray("102"u8));
    }

    [TestMethod]
    public void FormatsBclScalarParameters()
    {
        Assert.AreEqual(
            "123456789012345678901234567890",
            Format(BigInteger.Parse("123456789012345678901234567890", CultureInfo.InvariantCulture)));
                Assert.AreEqual("1.5", Format((Half)1.5f));
                Assert.AreEqual(Int128.MinValue.ToString(CultureInfo.InvariantCulture), Format(Int128.MinValue));
                Assert.AreEqual(UInt128.MaxValue.ToString(CultureInfo.InvariantCulture), Format(UInt128.MaxValue));
        Assert.AreEqual("255", Format((byte)255));
        Assert.AreEqual("-128", Format((sbyte)-128));
        Assert.AreEqual("x", Format('x'));
        Assert.AreEqual("hello", Format("hello".ToCharArray()));
        Assert.AreEqual("12:30:00", Format(TimeSpan.FromHours(12.5)));
        Assert.AreEqual("1 days 02:30:00", Format(TimeSpan.FromHours(26.5)));
        Assert.AreEqual("-1 days 02:30:00", Format(TimeSpan.FromHours(-26.5)));
        Assert.AreEqual("192.0.2.1", Format(IPAddress.Parse("192.0.2.1")));
        Assert.AreEqual(
            "08:00:2b:01:02:03",
            Format(PhysicalAddress.Parse("08-00-2B-01-02-03")));
        Assert.AreEqual("1011", Format(new BitArray(new[] { true, false, true, true })));
    }

    [TestMethod]
    public void DecodesAndFormatsBclAlternativeArrays()
    {
        CollectionAssert.AreEqual(
            new BigInteger?[] { BigInteger.One, null, new BigInteger(3) },
            PgTextCodec.DecodeArray<BigInteger?>(1231, "{1,NULL,3}"u8.ToArray()));
                CollectionAssert.AreEqual(
                    new Int128[] { Int128.MinValue, Int128.MaxValue },
                    PgTextCodec.DecodeArray<Int128>(
                        1231,
                        Encoding.UTF8.GetBytes(
                            $"{{{Int128.MinValue.ToString(CultureInfo.InvariantCulture)},{Int128.MaxValue.ToString(CultureInfo.InvariantCulture)}}}")));
                CollectionAssert.AreEqual(
                    new UInt128[] { UInt128.Zero, UInt128.MaxValue },
                    PgTextCodec.DecodeArray<UInt128>(
                        1231,
                        Encoding.UTF8.GetBytes(
                            $"{{0,{UInt128.MaxValue.ToString(CultureInfo.InvariantCulture)}}}")));
                CollectionAssert.AreEqual(
                    new Half[] { (Half)1.5f, (Half)(-2.25f) },
                    PgTextCodec.DecodeArray<Half>(1021, "{1.5,-2.25}"u8.ToArray()));
        CollectionAssert.AreEqual(
            new TimeSpan?[] { TimeSpan.FromHours(2), null, TimeSpan.FromDays(1) },
              PgTextCodec.DecodeArray<TimeSpan?>(1187, "{PT2H,NULL,P1D}"u8.ToArray()));
        CollectionAssert.AreEqual(
            new byte[] { 0, 255 },
            PgTextCodec.DecodeArray<byte>(1005, "{0,255}"u8.ToArray()));
        CollectionAssert.AreEqual(
            new sbyte[] { -128, 127 },
            PgTextCodec.DecodeArray<sbyte>(1005, "{-128,127}"u8.ToArray()));
        CollectionAssert.AreEqual(
            new char[] { 'a', 'b' },
            PgTextCodec.DecodeArray<char>(1009, "{a,b}"u8.ToArray()));
        CollectionAssert.AreEqual(
            new[] { IPAddress.Parse("192.0.2.1"), IPAddress.Parse("2001:db8::1") },
            PgTextCodec.DecodeArray<IPAddress>(1041, "{192.0.2.1,2001:db8::1}"u8.ToArray()));

        var macAddresses = PgTextCodec.DecodeArray<PhysicalAddress>(
            1040,
            "{08:00:2b:01:02:03,08:00:2b:04:05:06}"u8.ToArray());
        Assert.AreEqual(PhysicalAddress.Parse("08-00-2B-01-02-03"), macAddresses[0]);
        var bitArrays = PgTextCodec.DecodeArray<BitArray>(1561, "{101,010}"u8.ToArray());
        CollectionAssert.AreEqual(new[] { true, false, true }, ToBooleans(bitArrays[0]));

        Assert.AreEqual("{\"1\",\"2\"}", Format(new[] { BigInteger.One, new BigInteger(2) }));
        Assert.AreEqual("{\"-1\",\"2\"}", Format(new Int128[] { -1, 2 }));
        Assert.AreEqual("{\"1\",\"2\"}", Format(new UInt128[] { 1, 2 }));
        Assert.AreEqual("{\"1.5\",\"-2.25\"}", Format(new Half[] { (Half)1.5f, (Half)(-2.25f) }));
        SqlValue signedBytes = SqlValue.From(new sbyte[] { -128, 127 });
        Assert.AreEqual(SqlValueKind.Object, signedBytes.Kind);
        Assert.AreEqual("{\"-128\",\"127\"}", PgTextCodec.FormatParameter(signedBytes));
        Assert.AreEqual("{\"01:00:00\",\"02:00:00\"}", Format(new[]
        {
                        TimeSpan.FromHours(1),
                        TimeSpan.FromHours(2),
                }));
        Assert.AreEqual(
            "{\"192.0.2.1\",\"2001:db8::1\"}",
            Format(new[] { IPAddress.Parse("192.0.2.1"), IPAddress.Parse("2001:db8::1") }));
        Assert.AreEqual(
            "{\"101\",\"010\"}",
            Format(new[]
            {
                            new BitArray(new[] { true, false, true }),
                            new BitArray(new[] { false, true, false }),
            }));
    }

    [TestMethod]
    public void DecodesQuotedAndNullArrayElements()
    {
        var payload = Encoding.UTF8.GetBytes(
            """{"one,two",NULL,"quoted\"value"}""");
        var values = PgTextCodec.DecodeArray<string?>(1009, payload);
        var objectValue = PgTextCodec.Decode(1009, payload);

        CollectionAssert.AreEqual(
            new string?[] { "one,two", null, "quoted\"value" },
            values);
        Assert.IsInstanceOfType<string?[]>(objectValue);

        var nullableInts = PgTextCodec.DecodeArray<int?>(
            1007,
            Encoding.UTF8.GetBytes("{1,NULL,3}"));
        CollectionAssert.AreEqual(new int?[] { 1, null, 3 }, nullableInts);
        Assert.ThrowsExactly<InvalidCastException>(() =>
            PgTextCodec.DecodeArray<int>(1007, Encoding.UTF8.GetBytes("{1,NULL,3}")));
    }

    [TestMethod]
    public void RejectsMultidimensionalArrays()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => Decode(1007, "{{1,2},{3,4}}"));
    }

    [TestMethod]
    public void RejectsDocumentedUnsupportedTypes()
    {
        var exception =
          Assert.ThrowsExactly<PgUnsupportedTypeException>(() => Decode(26, "42"));

        Assert.AreEqual(26U, exception.TypeId);
    }

    private static object Decode(uint typeId, string value) =>
      PgTextCodec.Decode(typeId, Encoding.UTF8.GetBytes(value));

    private static string Format(object value) =>
        PgTextCodec.FormatParameter(SqlValue.From(value));

    private static bool[] ToBooleans(BitArray value)
    {
        var result = new bool[value.Count];
        value.CopyTo(result, 0);
        return result;
    }
}

using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text;
using Apex.PgClient.Internal;
using Apex.SqlClient;

namespace Apex.PgClient.Tests;

[TestClass]
public sealed class PgRowDecoderTests
{
    [TestMethod]
    public void EveryTypedGetterValidatesPostgreSqlType()
    {
        PgRowDecoder decoder = new(16, 64);
        var row = CreateRow([0]);
        var mismatch = Column(
          TypeId: 99999,
          SqlDataFormat.Binary);

        AssertInvalid(() => decoder.DecodeBoolean(row, 0, mismatch));
        AssertInvalid(() => decoder.DecodeInt16(row, 0, mismatch));
        AssertInvalid(() => decoder.DecodeInt32(row, 0, mismatch));
        AssertInvalid(() => decoder.DecodeInt64(row, 0, mismatch));
        AssertInvalid(() => decoder.DecodeFloat(row, 0, mismatch));
        AssertInvalid(() => decoder.DecodeDouble(row, 0, mismatch));
        AssertInvalid(() => decoder.DecodeDecimal(row, 0, mismatch));
        AssertInvalid(() => decoder.DecodeString(row, 0, mismatch));
        AssertInvalid(() => decoder.DecodeBytes(row, 0, mismatch));
        AssertInvalid(() =>
          decoder.DecodeReadOnlyMemory(row, 0, mismatch));
        AssertInvalid(() => decoder.DecodeGuid(row, 0, mismatch));
        AssertInvalid(() => decoder.DecodeDateOnly(row, 0, mismatch));
        AssertInvalid(() => decoder.DecodeTimeOnly(row, 0, mismatch));
        AssertInvalid(() => decoder.DecodeDateTime(row, 0, mismatch));
        AssertInvalid(() =>
          decoder.DecodeDateTimeOffset(row, 0, mismatch));
        AssertInvalid(() =>
          decoder.DecodeJsonElement(row, 0, mismatch));
        AssertInvalid(() => decoder.DecodeArray<int>(row, 0, mismatch));
        AssertInvalid(() => PgRowDecoder.DecodePgNumeric(row, 0, mismatch));
        AssertInvalid(() => PgRowDecoder.DecodePgMoney(row, 0, mismatch));
        AssertInvalid(() => PgRowDecoder.DecodePgInterval(row, 0, mismatch));
        AssertInvalid(() =>
          PgRowDecoder.DecodePgTimeWithTimeZone(row, 0, mismatch));
        AssertInvalid(() => PgRowDecoder.DecodePgPoint(row, 0, mismatch));
        AssertInvalid(() =>
          PgRowDecoder.DecodePgLineSegment(row, 0, mismatch));
        AssertInvalid(() => PgRowDecoder.DecodePgPath(row, 0, mismatch));
        AssertInvalid(() => PgRowDecoder.DecodePgBox(row, 0, mismatch));
        AssertInvalid(() => PgRowDecoder.DecodePgPolygon(row, 0, mismatch));
        AssertInvalid(() => PgRowDecoder.DecodePgLine(row, 0, mismatch));
        AssertInvalid(() => PgRowDecoder.DecodePgCidr(row, 0, mismatch));
        AssertInvalid(() => PgRowDecoder.DecodePgCircle(row, 0, mismatch));
        AssertInvalid(() => PgRowDecoder.DecodePgInet(row, 0, mismatch));
    }

    [TestMethod]
    public void DecodesBinaryAndTextTypedValues()
    {
        PgRowDecoder decoder = new(16, 64);
        Guid expected =
          Guid.Parse("12345678-1234-5678-9012-123456789abc");

        Assert.AreEqual(
          42,
          decoder.DecodeInt32(
            CreateRow(Int32(42)),
            0,
            Column(23, SqlDataFormat.Binary)));
        Assert.AreEqual(
          42,
          decoder.DecodeInt32(
            CreateRow("42"u8),
            0,
            Column(23, SqlDataFormat.Text)));
        Assert.AreEqual(
          expected,
          decoder.DecodeGuid(
            CreateRow(expected.ToByteArray(bigEndian: true)),
            0,
            Column(2950, SqlDataFormat.Binary)));
        Assert.AreEqual(
          expected,
          decoder.DecodeGuid(
            CreateRow(Encoding.UTF8.GetBytes(expected.ToString("D"))),
            0,
            Column(2950, SqlDataFormat.Text)));
        Assert.AreEqual(
          12.34m,
          decoder.DecodeDecimal(
            CreateRow("12.34"u8),
            0,
            Column(1700, SqlDataFormat.Text)));
    }

    [TestMethod]
    public void GenericDispatchUsesExactTypedPaths()
    {
        PgRowDecoder decoder = new(16, 64);
        Guid expected =
          Guid.Parse("12345678-1234-5678-9012-123456789abc");
        var intBinary = Column(23, SqlDataFormat.Binary);
        var intText = Column(23, SqlDataFormat.Text);
        var guidBinary = Column(2950, SqlDataFormat.Binary);
        var guidText = Column(2950, SqlDataFormat.Text);
        var pointBinary = Column(600, SqlDataFormat.Binary);

        Assert.AreEqual(
          42,
          decoder.Decode<int>(
            CreateRow(Int32(42)),
            0,
            intBinary,
            copyReadOnlyMemory: false));
        Assert.AreEqual(
          42,
          decoder.Decode<int>(
            CreateRow("42"u8),
            0,
            intText,
            copyReadOnlyMemory: false));
        Assert.AreEqual(
          expected,
          decoder.Decode<Guid>(
            CreateRow(expected.ToByteArray(bigEndian: true)),
            0,
            guidBinary,
            copyReadOnlyMemory: false));
        Assert.AreEqual(
          expected,
          decoder.Decode<Guid>(
            CreateRow(Encoding.UTF8.GetBytes(expected.ToString("D"))),
            0,
            guidText,
            copyReadOnlyMemory: false));
        Assert.AreEqual(
          new PgPoint(1.5, -2.25),
          decoder.Decode<PgPoint>(
            CreateRow(Point(1.5, -2.25)),
            0,
            pointBinary,
            copyReadOnlyMemory: false));
        Assert.AreEqual(
          42,
          decoder.Decode<object>(
            CreateRow(Int32(42)),
            0,
            intBinary,
            copyReadOnlyMemory: false));
        Assert.IsNull(
          decoder.Decode<int?>(
            CreateNullRow(),
            0,
            intBinary,
            copyReadOnlyMemory: false));
        AssertInvalid(() =>
          decoder.Decode<int>(
            CreateRow(Point(1.5, -2.25)),
            0,
            pointBinary,
            copyReadOnlyMemory: false));
        AssertInvalid(() =>
          decoder.Decode<int>(
            CreateRow(Int32(42)),
            0,
            Column(23, (SqlDataFormat)2),
            copyReadOnlyMemory: false));
    }

    [TestMethod]
    public void GenericDispatchSupportsBclAlternatives()
    {
        PgRowDecoder decoder = new(16, 64);

        Assert.AreEqual((byte)255, Decode<byte>(decoder, "255", 21));
        Assert.AreEqual((sbyte)-128, Decode<sbyte>(decoder, "-128", 21));
        Assert.AreEqual(
          BigInteger.Parse("123456789012345678901234567890", CultureInfo.InvariantCulture),
          Decode<BigInteger>(decoder, "123456789012345678901234567890", 1700));
        Assert.AreEqual((Half)1.5f, Decode<Half>(decoder, "1.5", 700));
        Assert.AreEqual(
          Int128.MaxValue,
          Decode<Int128>(decoder, Int128.MaxValue.ToString(CultureInfo.InvariantCulture), 1700));
        Assert.AreEqual(
          UInt128.MaxValue,
          Decode<UInt128>(decoder, UInt128.MaxValue.ToString(CultureInfo.InvariantCulture), 1700));
        Assert.AreEqual(
          TimeSpan.FromHours(26.5),
          Decode<TimeSpan>(decoder, "P1DT2H30M", 1186));
        Assert.AreEqual(
          TimeSpan.FromHours(12.5),
          Decode<TimeSpan>(decoder, "12:30:00", 1083));
        Assert.AreEqual('x', Decode<char>(decoder, "x", 25));
        CollectionAssert.AreEqual("hello".ToCharArray(), Decode<char[]>(decoder, "hello", 25));
        Assert.AreEqual(
          IPAddress.Parse("192.0.2.1"),
          Decode<IPAddress>(decoder, "192.0.2.1/24", 869));
        Assert.AreEqual(
          PhysicalAddress.Parse("08-00-2B-01-02-03"),
          Decode<PhysicalAddress>(decoder, "08:00:2b:01:02:03", 829));
        CollectionAssert.AreEqual(
          new[] { true, false, true, true },
          ToBooleans(Decode<BitArray>(decoder, "1011", 1560)));

        Assert.IsNull(decoder.Decode<BigInteger?>(
          CreateNullRow(),
          0,
          Column(1700, SqlDataFormat.Text),
          copyReadOnlyMemory: false));
        Assert.IsNull(decoder.Decode<IPAddress>(
          CreateNullRow(),
          0,
          Column(869, SqlDataFormat.Text),
          copyReadOnlyMemory: false));
        Assert.ThrowsExactly<OverflowException>(() => Decode<byte>(decoder, "256", 21));
        Assert.ThrowsExactly<OverflowException>(() => Decode<UInt128>(decoder, "-1", 1700));
        AssertInvalid(() => Decode<IPAddress>(decoder, "192.0.2.1", 25));
    }

    [TestMethod]
    public void GenericProviderValuesAreTypedAndNullable()
    {
        PgRowDecoder decoder = new(16, 64);
        var column = Column(600, SqlDataFormat.Binary);
        var row = CreateRow(Point(1.5, -2.25));

        Assert.AreEqual(
          new PgPoint(1.5, -2.25),
          decoder.Decode<PgPoint>(
            row,
            0,
            column,
            copyReadOnlyMemory: false));
        Assert.AreEqual(
          new PgPoint(1.5, -2.25),
          decoder.Decode<PgPoint?>(
            row,
            0,
            column,
            copyReadOnlyMemory: false));
        Assert.IsNull(
          decoder.Decode<PgPoint?>(
            CreateNullRow(),
            0,
            column,
            copyReadOnlyMemory: false));
    }

    [TestMethod]
    public void DecodesTypedArraysWithoutObjectElements()
    {
        PgRowDecoder decoder = new(16, 64);
        byte[] binaryArray =
        [
          .. Int32(1),
          .. Int32(1),
          .. Int32(23),
          .. Int32(3),
          .. Int32(1),
          .. Int32(4),
          .. Int32(1),
          .. Int32(-1),
          .. Int32(4),
          .. Int32(3),
        ];

        var binary = decoder.DecodeArray<int?>(
          CreateRow(binaryArray),
          0,
          Column(1007, SqlDataFormat.Binary));
        var text = decoder.DecodeArray<string?>(
          CreateRow("{one,NULL,three}"u8),
          0,
          Column(1009, SqlDataFormat.Text));

        CollectionAssert.AreEqual(new int?[] { 1, null, 3 }, binary);
        CollectionAssert.AreEqual(new string?[] { "one", null, "three" }, text);
        Assert.IsNull(decoder.DecodeArray<int?>(
          CreateNullRow(),
          0,
          Column(1007, SqlDataFormat.Binary)));
        Assert.ThrowsExactly<InvalidCastException>(() =>
          decoder.DecodeArray<int>(
            CreateRow(binaryArray),
            0,
            Column(1007, SqlDataFormat.Binary)));
    }

    [TestMethod]
    public void NullReferenceValuesRemainNullButStillValidateType()
    {
        PgRowDecoder decoder = new(16, 64);
        var row = CreateNullRow();

        Assert.IsNull(
          decoder.DecodeString(
            row,
            0,
            Column(25, SqlDataFormat.Text)));
        AssertInvalid(() =>
          decoder.DecodeString(
            row,
            0,
            Column(23, SqlDataFormat.Text)));
    }

    [TestMethod]
    public void GenericReadOnlyMemoryCanBorrowOrCopy()
    {
        PgRowDecoder decoder = new(16, 64);
        var row = CreateRow([1, 2, 3]);
        var column = Column(17, SqlDataFormat.Binary);
        var borrowed =
          decoder.Decode<ReadOnlyMemory<byte>>(
            row,
            0,
            column,
            copyReadOnlyMemory: false);
        var copied =
          decoder.Decode<ReadOnlyMemory<byte>>(
            row,
            0,
            column,
            copyReadOnlyMemory: true);
        var direct = decoder.DecodeReadOnlyMemory(
          row,
          0,
          column);

        row[sizeof(short) + sizeof(int)] = 9;

        CollectionAssert.AreEqual(
          new byte[] { 9, 2, 3 },
          borrowed.ToArray());
        CollectionAssert.AreEqual(
          new byte[] { 9, 2, 3 },
          direct.ToArray());
        CollectionAssert.AreEqual(
          new byte[] { 1, 2, 3 },
          copied.ToArray());
    }

    [TestMethod]
    public void ObjectDecodeUsesScalarCache()
    {
        PgRowDecoder decoder = new(16, 64);
        var row = CreateRow(Int32(42));
        var column = Column(23, SqlDataFormat.Binary);

        var first = decoder.DecodeObject(row, 0, column);
        var second = decoder.DecodeObject(row, 0, column);

        Assert.AreSame(first, second);
        Assert.AreEqual(42, first);
    }

    [TestMethod]
    public void GenericTypedDecodingDoesNotAllocateAfterWarmup()
    {
        PgRowDecoder decoder = new(16, 64);
        var intRow = CreateRow(Int32(42));
        var decimalRow = CreateRow("12.34"u8);
        var pointRow = CreateRow(Point(1.5, -2.25));
        Guid expected =
          Guid.Parse("12345678-1234-5678-9012-123456789abc");
        var guidRow =
          CreateRow(expected.ToByteArray(bigEndian: true));
        var intColumn = Column(23, SqlDataFormat.Binary);
        var decimalColumn = Column(1700, SqlDataFormat.Text);
        var pointColumn = Column(600, SqlDataFormat.Binary);
        var guidColumn = Column(2950, SqlDataFormat.Binary);
        for (var i = 0; i < 1000; i++)
        {
            _ = decoder.Decode<int>(
              intRow,
              0,
              intColumn,
              copyReadOnlyMemory: false);
            _ = decoder.Decode<decimal>(
              decimalRow,
              0,
              decimalColumn,
              copyReadOnlyMemory: false);
            _ = decoder.Decode<Guid>(
              guidRow,
              0,
              guidColumn,
              copyReadOnlyMemory: false);
            _ = decoder.Decode<PgPoint>(
              pointRow,
              0,
              pointColumn,
              copyReadOnlyMemory: false);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var intSum = 0;
        decimal decimalSum = 0;
        double pointSum = 0;
        Guid lastGuid = default;
        for (var i = 0; i < 10_000; i++)
        {
            intSum += decoder.Decode<int>(
              intRow,
              0,
              intColumn,
              copyReadOnlyMemory: false);
            decimalSum += decoder.Decode<decimal>(
              decimalRow,
              0,
              decimalColumn,
              copyReadOnlyMemory: false);
            lastGuid = decoder.Decode<Guid>(
              guidRow,
              0,
              guidColumn,
              copyReadOnlyMemory: false);
            pointSum += decoder.Decode<PgPoint>(
              pointRow,
              0,
              pointColumn,
              copyReadOnlyMemory: false).X;
        }
        var allocated =
          GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(420_000, intSum);
        Assert.AreEqual(123_400m, decimalSum);
        Assert.AreEqual(15_000d, pointSum);
        Assert.AreEqual(expected, lastGuid);
        Assert.AreEqual(0, allocated);
    }

    [TestMethod]
    public void UnknownBinaryTypeIsNotDecodedAsUtf8()
    {
        PgRowDecoder decoder = new(16, 64);
        var row = CreateRow([0xff]);
        var column = Column(99999, SqlDataFormat.Binary);

        Assert.ThrowsExactly<PgUnsupportedTypeException>(
          () => decoder.DecodeObject(row, 0, column));
    }

    private static void AssertInvalid(Action action) =>
      Assert.ThrowsExactly<InvalidCastException>(action);

    private static T Decode<T>(PgRowDecoder decoder, string value, uint typeId) =>
      decoder.Decode<T>(
        CreateRow(Encoding.UTF8.GetBytes(value)),
        0,
        Column(typeId, SqlDataFormat.Text),
        copyReadOnlyMemory: false);

    private static bool[] ToBooleans(BitArray value)
    {
        var result = new bool[value.Count];
        value.CopyTo(result, 0);
        return result;
    }

    private static SqlColumn Column(
        uint TypeId,
        SqlDataFormat format) =>
      new(
        "value",
        TypeId,
        TypeSize: -1,
        TypeModifier: -1,
        format);

    private static byte[] CreateNullRow()
    {
        var row = new byte[sizeof(short) + sizeof(int)];
        BinaryPrimitives.WriteInt16BigEndian(row, 1);
        BinaryPrimitives.WriteInt32BigEndian(
          row.AsSpan(sizeof(short)),
          -1);
        return row;
    }

    private static byte[] CreateRow(ReadOnlySpan<byte> value)
    {
        var row =
          new byte[sizeof(short) + sizeof(int) + value.Length];
        BinaryPrimitives.WriteInt16BigEndian(row, 1);
        BinaryPrimitives.WriteInt32BigEndian(
          row.AsSpan(sizeof(short)),
          value.Length);
        value.CopyTo(
          row.AsSpan(sizeof(short) + sizeof(int)));
        return row;
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Point(double x, double y)
    {
        var bytes = new byte[sizeof(double) * 2];
        BinaryPrimitives.WriteInt64BigEndian(
          bytes,
          BitConverter.DoubleToInt64Bits(x));
        BinaryPrimitives.WriteInt64BigEndian(
          bytes.AsSpan(sizeof(double)),
          BitConverter.DoubleToInt64Bits(y));
        return bytes;
    }
}

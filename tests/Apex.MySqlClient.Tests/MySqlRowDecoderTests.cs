using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Apex.MySqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlRowDecoderTests
{
    [TestMethod]
    public void TextRowDecodesSignedIntegerBoundaries()
    {
        MySqlColumnMetadata[] columns =
        [
          Column("tiny", MySqlType.Tiny),
      Column("short", MySqlType.Short),
      Column("long", MySqlType.Long),
      Column("longlong", MySqlType.LongLong),
    ];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRow(
          sbyte.MinValue.ToString(CultureInfo.InvariantCulture),
          short.MinValue.ToString(CultureInfo.InvariantCulture),
          int.MinValue.ToString(CultureInfo.InvariantCulture),
          long.MinValue.ToString(CultureInfo.InvariantCulture));

        Assert.AreEqual(sbyte.MinValue, decoder.Decode<sbyte>(row, 0));
        Assert.AreEqual(short.MinValue, decoder.Decode<short>(row, 1));
        Assert.AreEqual(int.MinValue, decoder.Decode<int>(row, 2));
        Assert.AreEqual(long.MinValue, decoder.Decode<long>(row, 3));
    }

    [TestMethod]
    public void TextRowDecodesUnsignedIntegerBoundaries()
    {
        MySqlColumnMetadata[] columns =
        [
          Column("tiny", MySqlType.Tiny, unsigned: true),
      Column("short", MySqlType.Short, unsigned: true),
      Column("long", MySqlType.Long, unsigned: true),
      Column("longlong", MySqlType.LongLong, unsigned: true),
    ];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRow(
          byte.MaxValue.ToString(CultureInfo.InvariantCulture),
          ushort.MaxValue.ToString(CultureInfo.InvariantCulture),
          uint.MaxValue.ToString(CultureInfo.InvariantCulture),
          ulong.MaxValue.ToString(CultureInfo.InvariantCulture));

        Assert.AreEqual(byte.MaxValue, decoder.Decode<byte>(row, 0));
        Assert.AreEqual(ushort.MaxValue, decoder.Decode<ushort>(row, 1));
        Assert.AreEqual(uint.MaxValue, decoder.Decode<uint>(row, 2));
        Assert.AreEqual(ulong.MaxValue, decoder.Decode<ulong>(row, 3));
    }

    [TestMethod]
    public void BinaryRowDecodesSignedIntegerBoundaries()
    {
        MySqlColumnMetadata[] columns =
        [
          Column("tiny", MySqlType.Tiny),
      Column("short", MySqlType.Short),
      Column("long", MySqlType.Long),
      Column("longlong", MySqlType.LongLong),
    ];
        var decoder = CreateDecoder(columns, binary: true);
        BinaryRowBuilder builder = new(columns.Length);
        builder.WriteInt8(0, sbyte.MinValue);
        builder.WriteInt16(1, short.MinValue);
        builder.WriteInt32(2, int.MinValue);
        builder.WriteInt64(3, long.MinValue);
        var row = builder.Build();

        Assert.AreEqual(sbyte.MinValue, decoder.Decode<sbyte>(row, 0));
        Assert.AreEqual(short.MinValue, decoder.Decode<short>(row, 1));
        Assert.AreEqual(int.MinValue, decoder.Decode<int>(row, 2));
        Assert.AreEqual(long.MinValue, decoder.Decode<long>(row, 3));
    }

    [TestMethod]
    public void BinaryRowDecodesUnsignedIntegerBoundaries()
    {
        MySqlColumnMetadata[] columns =
        [
          Column("tiny", MySqlType.Tiny, unsigned: true),
      Column("short", MySqlType.Short, unsigned: true),
      Column("long", MySqlType.Long, unsigned: true),
      Column("longlong", MySqlType.LongLong, unsigned: true),
    ];
        var decoder = CreateDecoder(columns, binary: true);
        BinaryRowBuilder builder = new(columns.Length);
        builder.WriteInt8(0, unchecked((sbyte)byte.MaxValue));
        builder.WriteInt16(1, unchecked((short)ushort.MaxValue));
        builder.WriteInt32(2, unchecked((int)uint.MaxValue));
        builder.WriteInt64(3, unchecked((long)ulong.MaxValue));
        var row = builder.Build();

        Assert.AreEqual(byte.MaxValue, decoder.Decode<byte>(row, 0));
        Assert.AreEqual(ushort.MaxValue, decoder.Decode<ushort>(row, 1));
        Assert.AreEqual(uint.MaxValue, decoder.Decode<uint>(row, 2));
        Assert.AreEqual(ulong.MaxValue, decoder.Decode<ulong>(row, 3));
    }

    [TestMethod]
    public void BinaryRowDecodesFloatAndDouble()
    {
        MySqlColumnMetadata[] columns = [Column("f", MySqlType.Float), Column("d", MySqlType.Double)];
        var decoder = CreateDecoder(columns, binary: true);
        BinaryRowBuilder builder = new(columns.Length);
        builder.WriteFloat(0, 1.5f);
        builder.WriteDouble(1, 2.25d);
        var row = builder.Build();

        Assert.AreEqual(1.5f, decoder.Decode<float>(row, 0));
        Assert.AreEqual(2.25d, decoder.Decode<double>(row, 1));
    }

    [TestMethod]
    public void TextRowDecodesFloatDoubleAndDecimal()
    {
        MySqlColumnMetadata[] columns =
        [
          Column("f", MySqlType.Float),
      Column("d", MySqlType.Double),
      Column("m", MySqlType.NewDecimal),
    ];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRow("1.5", "2.25", "12345.6789");

        Assert.AreEqual(1.5f, decoder.Decode<float>(row, 0));
        Assert.AreEqual(2.25d, decoder.Decode<double>(row, 1));
        Assert.AreEqual(12345.6789m, decoder.Decode<decimal>(row, 2));
    }

    [TestMethod]
    public void TextRowPreservesArbitraryPrecisionDecimal()
    {
        const string text =
          "12345678901234567890123456789012345.123456789012345678901234567890";
        MySqlColumnMetadata[] columns = [Column("value", MySqlType.NewDecimal)];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRow(text);

        var value = decoder.Decode<MySqlDecimal>(row, 0);

        Assert.AreEqual(text, value.ToString());
        Assert.AreEqual(value, decoder.DecodeObject(row, 0));
        Assert.ThrowsExactly<FormatException>(() => decoder.Decode<decimal>(row, 0));
    }

    [TestMethod]
    public void DecodesBitColumnAsUnsignedIntegerInBothProtocols()
    {
        MySqlColumnMetadata[] columns = [Column("bits", MySqlType.Bit)];
        var textDecoder = CreateDecoder(columns, binary: false);
        var textRow = BuildTextRowRaw([[0x01, 0x02]]);
        Assert.AreEqual(0x0102ul, textDecoder.Decode<ulong>(textRow, 0));

        var binaryDecoder = CreateDecoder(columns, binary: true);
        BinaryRowBuilder builder = new(columns.Length);
        builder.WriteLengthEncodedBytes(0, [0x01, 0x02]);
        var binaryRow = builder.Build();
        Assert.AreEqual(0x0102ul, binaryDecoder.Decode<ulong>(binaryRow, 0));
    }

    [TestMethod]
    public void DecodesTextDateAndDateTime()
    {
        MySqlColumnMetadata[] columns =
        [
          Column("d", MySqlType.Date),
      Column("dt", MySqlType.DateTime),
      Column("t", MySqlType.Time),
    ];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRow("2024-03-15", "2024-03-15 13:45:30", "13:45:30");

        Assert.AreEqual(new DateOnly(2024, 3, 15), decoder.Decode<DateOnly>(row, 0));
        Assert.AreEqual(new DateTime(2024, 3, 15, 13, 45, 30), decoder.Decode<DateTime>(row, 1));
        Assert.AreEqual(new TimeSpan(13, 45, 30), decoder.Decode<TimeSpan>(row, 2));
        Assert.AreEqual(new TimeOnly(13, 45, 30), decoder.Decode<TimeOnly>(row, 2));
    }

    [TestMethod]
    public void DecodesBinaryDateAndDateTime()
    {
        MySqlColumnMetadata[] columns = [Column("d", MySqlType.Date), Column("dt", MySqlType.DateTime)];
        var decoder = CreateDecoder(columns, binary: true);
        BinaryRowBuilder builder = new(columns.Length);
        builder.WriteDate(0, 2024, 3, 15);
        builder.WriteDateTime(1, 2024, 3, 15, 13, 45, 30, 0);
        var row = builder.Build();

        Assert.AreEqual(new DateOnly(2024, 3, 15), decoder.Decode<DateOnly>(row, 0));
        Assert.AreEqual(new DateTime(2024, 3, 15, 13, 45, 30), decoder.Decode<DateTime>(row, 1));
    }

    [TestMethod]
    public void ZeroDateBehaviorErrorThrowsFormatException()
    {
        MySqlColumnMetadata[] columns = [Column("d", MySqlType.Date)];
        var decoder = CreateDecoder(columns, binary: false, MySqlZeroDateBehavior.Error);
        var row = BuildTextRow("0000-00-00");

        Assert.ThrowsExactly<FormatException>(() => decoder.Decode<DateOnly>(row, 0));
        Assert.ThrowsExactly<FormatException>(() => decoder.DecodeObject(row, 0));
    }

    [TestMethod]
    public void ZeroDateBehaviorNullReturnsNull()
    {
        MySqlColumnMetadata[] columns = [Column("d", MySqlType.Date)];
        var decoder = CreateDecoder(columns, binary: false, MySqlZeroDateBehavior.Null);
        var row = BuildTextRow("0000-00-00");

        Assert.IsNull(decoder.DecodeObject(row, 0));
        Assert.ThrowsExactly<InvalidCastException>(() => decoder.Decode<DateOnly>(row, 0));
    }

    [TestMethod]
    public void ZeroDateBehaviorMinValueReturnsMinValue()
    {
        MySqlColumnMetadata[] columns = [Column("d", MySqlType.Date), Column("dt", MySqlType.DateTime)];
        var decoder = CreateDecoder(columns, binary: false, MySqlZeroDateBehavior.MinValue);
        var row = BuildTextRow("0000-00-00", "0000-00-00 00:00:00");

        Assert.AreEqual(DateOnly.MinValue, decoder.Decode<DateOnly>(row, 0));
        Assert.AreEqual(DateOnly.MinValue, (DateOnly)decoder.DecodeObject(row, 0)!);
        Assert.AreEqual(DateTime.MinValue, decoder.Decode<DateTime>(row, 1));
    }

    [TestMethod]
    public void DecodesNullColumnsInBothProtocols()
    {
        MySqlColumnMetadata[] columns = [Column("value", MySqlType.VarString)];

        var textDecoder = CreateDecoder(columns, binary: false);
        var textRow = BuildTextRowRaw([null]);
        Assert.IsTrue(textDecoder.IsNull(textRow, 0));
        Assert.IsNull(textDecoder.DecodeObject(textRow, 0));
        Assert.IsNull(textDecoder.Decode<string>(textRow, 0));
        Assert.ThrowsExactly<InvalidCastException>(() => textDecoder.Decode<int>(textRow, 0));

        var binaryDecoder = CreateDecoder(columns, binary: true);
        BinaryRowBuilder builder = new(columns.Length);
        builder.SetNull(0);
        var binaryRow = builder.Build();
        Assert.IsTrue(binaryDecoder.IsNull(binaryRow, 0));
        Assert.IsNull(binaryDecoder.DecodeObject(binaryRow, 0));
    }

    [TestMethod]
    public void DecodeThrowsForOutOfRangeOrdinal()
    {
        MySqlColumnMetadata[] columns = [Column("value", MySqlType.Long)];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRow("1");

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => decoder.Decode<int>(row, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => decoder.Decode<int>(row, -1));
    }

    [TestMethod]
    public void UnsignedMetadataCannotBeReadAsSignedType()
    {
        MySqlColumnMetadata[] columns = [Column("value", MySqlType.Short, unsigned: true)];
        var decoder = CreateDecoder(columns, binary: true);
        BinaryRowBuilder builder = new(columns.Length);
        builder.WriteInt16(0, unchecked((short)50_000));
        var row = builder.Build();

        Assert.ThrowsExactly<InvalidCastException>(() => decoder.Decode<short>(row, 0));
        Assert.AreEqual((ushort)50_000, decoder.Decode<ushort>(row, 0));
    }

    [TestMethod]
    public void UnsignedLongLongCannotBeReadAsSignedLong()
    {
        MySqlColumnMetadata[] columns = [Column("value", MySqlType.LongLong, unsigned: true)];
        var decoder = CreateDecoder(columns, binary: true);
        BinaryRowBuilder builder = new(columns.Length);
        builder.WriteInt64(0, unchecked((long)ulong.MaxValue));
        var row = builder.Build();

        Assert.ThrowsExactly<InvalidCastException>(() => decoder.Decode<long>(row, 0));
        Assert.AreEqual(ulong.MaxValue, decoder.Decode<ulong>(row, 0));
    }

    [TestMethod]
    public void BinaryStringColumnCannotBeReadAsString()
    {
        MySqlColumnMetadata[] columns =
        [
          new MySqlColumnMetadata(
        "value",
        "value",
        string.Empty,
        string.Empty,
        string.Empty,
        MySqlType.VarString,
        MySqlColumnFlags.Binary,
        MySqlProtocol.BinaryCollation,
        0,
        0),
    ];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRowRaw([[1, 2, 3]]);

        Assert.ThrowsExactly<InvalidCastException>(() => decoder.Decode<string>(row, 0));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, decoder.Decode<byte[]>(row, 0));
    }

    [TestMethod]
    public void DecodesGuidFromBinaryFixedLengthAndTextRepresentation()
    {
        Guid value = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        MySqlColumnMetadata[] textColumns = [Column("id", MySqlType.VarChar)];
        var textDecoder = CreateDecoder(textColumns, binary: false);
        var textRow = BuildTextRow(value.ToString());
        Assert.AreEqual(value, textDecoder.Decode<Guid>(textRow, 0));

        MySqlColumnMetadata[] binaryColumns =
        [
          new MySqlColumnMetadata(
        "id",
        "id",
        string.Empty,
        string.Empty,
        string.Empty,
        MySqlType.String,
        MySqlColumnFlags.Binary,
        MySqlProtocol.BinaryCollation,
        16,
        0),
    ];
        var binaryDecoder = CreateDecoder(binaryColumns, binary: true);
        BinaryRowBuilder builder = new(binaryColumns.Length);
        builder.WriteLengthEncodedBytes(0, value.ToByteArray(bigEndian: true));
        var binaryRow = builder.Build();
        Assert.AreEqual(value, binaryDecoder.Decode<Guid>(binaryRow, 0));
    }

    [TestMethod]
    public void DecodesJsonAndEnumColumnsAsStrings()
    {
        MySqlColumnMetadata[] columns = [Column("j", MySqlType.Json), Column("e", MySqlType.Enum)];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRow("""{"a":1}""", "small");

        Assert.AreEqual("""{"a":1}""", decoder.Decode<string>(row, 0));
        Assert.AreEqual("small", decoder.Decode<string>(row, 1));
    }

    [TestMethod]
    public void DecodesJsonAsJsonElementAndTypedScalar()
    {
        MySqlColumnMetadata[] columns =
        [
          Column("object", MySqlType.Json),
      Column("boolean", MySqlType.Json),
      Column("number", MySqlType.Json),
    ];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRow("""{"a":1}""", "true", "42");

        var json = decoder.Decode<JsonElement>(row, 0);

        Assert.AreEqual(1, json.GetProperty("a").GetInt32());
        Assert.IsInstanceOfType<JsonElement>(decoder.DecodeObject(row, 0));
        Assert.IsTrue(decoder.Decode<bool>(row, 1));
        Assert.IsTrue(decoder.Decode<bool?>(row, 1));
        Assert.AreEqual(42, decoder.Decode<int>(row, 2));
        Assert.AreEqual(42, decoder.Decode<int?>(row, 2));
    }

    [TestMethod]
    public void DecodesGeometryAndVectorAsRawBytes()
    {
        MySqlColumnMetadata[] columns = [Column("g", MySqlType.Geometry), Column("v", MySqlType.Vector)];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRowRaw([[1, 2, 3, 4], [5, 6]]);

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, decoder.Decode<byte[]>(row, 0));
        CollectionAssert.AreEqual(new byte[] { 5, 6 }, decoder.Decode<byte[]>(row, 1));
    }

    [TestMethod]
    public void GetFieldCountReturnsColumnCount()
    {
        MySqlColumnMetadata[] columns = [Column("a", MySqlType.Long), Column("b", MySqlType.Long)];
        var decoder = CreateDecoder(columns, binary: false);

        Assert.AreEqual(2, decoder.GetFieldCount(ReadOnlySpan<byte>.Empty));
    }

    [TestMethod]
    public void ObjectDecodeUsesBoxedScalarCacheForSmallValues()
    {
        MySqlColumnMetadata[] columns = [Column("a", MySqlType.Long)];
        var decoder = CreateDecoder(columns, binary: false);
        var first = BuildTextRow("5");
        var second = BuildTextRow("5");

        var firstValue = decoder.DecodeObject(first, 0);
        var secondValue = decoder.DecodeObject(second, 0);

        Assert.AreSame(firstValue, secondValue);
    }

    [TestMethod]
    public void CommonTypedDecodersRequireCompatibleMetadata()
    {
        MySqlColumnMetadata[] columns =
        [
          Column("signed", MySqlType.Long),
      Column("unsigned", MySqlType.Long, unsigned: true),
      Column("text", MySqlType.VarString),
    ];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRow("42", "42", "42");

        Assert.AreEqual(42, decoder.DecodeInt32(row, 0, decoder.Columns[0]));
        Assert.AreEqual(42L, decoder.DecodeInt64(row, 0, decoder.Columns[0]));
        Assert.ThrowsExactly<InvalidCastException>(
          () => decoder.DecodeInt32(row, 1, decoder.Columns[1]));
        Assert.ThrowsExactly<InvalidCastException>(
          () => decoder.DecodeInt32(row, 2, decoder.Columns[2]));

        var wrongFormat = decoder.Columns[0] with { Format = SqlDataFormat.Binary };
        Assert.ThrowsExactly<InvalidCastException>(
          () => decoder.DecodeInt32(row, 0, wrongFormat));
    }

    [TestMethod]
    public void TypedDecodersPreserveCompatibleNumericAndTemporalConversions()
    {
        MySqlColumnMetadata[] columns =
        [
          Column("float", MySqlType.Float),
      Column("datetime", MySqlType.DateTime),
      Column("date", MySqlType.Date),
    ];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRow("1.5", "2026-08-14 12:34:56", "2026-08-14");

        Assert.AreEqual(1.5d, decoder.DecodeDouble(row, 0, decoder.Columns[0]));
        Assert.AreEqual(
          new DateOnly(2026, 8, 14),
          decoder.DecodeDateOnly(row, 1, decoder.Columns[1]));
        Assert.AreEqual(
          new TimeOnly(12, 34, 56),
          decoder.DecodeTimeOnly(row, 1, decoder.Columns[1]));
        Assert.AreEqual(
          new DateTime(2026, 8, 14),
          decoder.DecodeDateTime(row, 2, decoder.Columns[2]));
    }

    [TestMethod]
    public void NullableAndObjectDecodersHandleNullWithoutScalarBoxing()
    {
        MySqlColumnMetadata[] columns =
        [
          Column("number", MySqlType.Long),
      Column("text", MySqlType.VarString),
    ];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRowRaw([null, null]);

        Assert.IsNull(decoder.DecodeNullableInt32(row, 0, decoder.Columns[0]));
        Assert.IsNull(decoder.DecodeString(row, 1, decoder.Columns[1]));
        Assert.IsNull(decoder.DecodeObject(row, 0, decoder.Columns[0]));
        Assert.ThrowsExactly<InvalidCastException>(
          () => decoder.DecodeInt32(row, 0, decoder.Columns[0]));
    }

    [TestMethod]
    public void GenericNullableDecodersAcceptPhysicalAndNullTypedValues()
    {
        MySqlColumnMetadata[] columns =
        [
          Column("null_type", MySqlType.Null),
      Column("json", MySqlType.Json),
    ];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRowRaw([null, null]);

        Assert.IsNull(decoder.Decode<int?>(
          row, 0, decoder.Columns[0], copyReadOnlyMemory: false));
        Assert.IsNull(decoder.Decode<string>(
          row, 0, decoder.Columns[0], copyReadOnlyMemory: false));
        Assert.IsNull(decoder.Decode<int?>(
          row, 1, decoder.Columns[1], copyReadOnlyMemory: false));
    }

    [TestMethod]
    public void ReadOnlyMemoryCanBorrowBufferedRowsOrCopyBorrowedRows()
    {
        MySqlColumnMetadata[] columns = [BinaryColumn("value", MySqlType.Blob)];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRowRaw([[1, 2, 3]]);
        var column = decoder.Columns[0];

        var borrowed =
          decoder.Decode<ReadOnlyMemory<byte>>(row, 0, column, copyReadOnlyMemory: false);
        var copied =
          decoder.Decode<ReadOnlyMemory<byte>>(row, 0, column, copyReadOnlyMemory: true);

        row[^1] = 9;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 9 }, borrowed.ToArray());
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, copied.ToArray());
    }

    [TestMethod]
    public void CommonScalarDecodeDoesNotAllocateAfterSetup()
    {
        MySqlColumnMetadata[] columns = [Column("value", MySqlType.Long)];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRow("42");
        var column = decoder.Columns[0];
        _ = decoder.Decode<int>(row, 0, column, copyReadOnlyMemory: false);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = 0;
        for (var i = 0; i < 1_000; i++)
        {
            result += decoder.Decode<int>(row, 0, column, copyReadOnlyMemory: false);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(42_000, result);
        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    public void GenericDispatchPreservesProviderSpecificTypes()
    {
        const string text =
          "12345678901234567890123456789012345.123456789012345678901234567890";
        MySqlColumnMetadata[] columns =
        [
          Column("decimal", MySqlType.NewDecimal),
      Column("duration", MySqlType.Time),
      Column("bits", MySqlType.Bit),
    ];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRowRaw(
          [
            Encoding.UTF8.GetBytes(text),
        "25:00:00"u8.ToArray(),
        [0x01, 0x02],
      ]);

        var decimalValue =
          decoder.Decode<MySqlDecimal>(row, 0, decoder.Columns[0], copyReadOnlyMemory: false);
        var duration =
          decoder.Decode<TimeSpan>(row, 1, decoder.Columns[1], copyReadOnlyMemory: false);
        var bits =
          decoder.Decode<ulong>(row, 2, decoder.Columns[2], copyReadOnlyMemory: false);

        Assert.AreEqual(text, decimalValue.ToString());
        Assert.AreEqual(TimeSpan.FromHours(25), duration);
        Assert.AreEqual(0x0102ul, bits);
    }

    [TestMethod]
    public void GenericDispatchSupportsBclAlternatives()
    {
        BigInteger integer = BigInteger.Parse(
          "123456789012345678901234567890",
          CultureInfo.InvariantCulture);
        MySqlColumnMetadata[] columns =
        [
          Column("number", MySqlType.NewDecimal),
          Column("character", MySqlType.VarString),
          Column("characters", MySqlType.VarString),
          Column("address", MySqlType.VarString),
          BinaryColumn("physical", MySqlType.Blob),
          Column("bits", MySqlType.Bit, columnLength: 4),
        ];
        var decoder = CreateDecoder(columns, binary: false);
        var row = BuildTextRowRaw(
        [
          Encoding.UTF8.GetBytes(integer.ToString(CultureInfo.InvariantCulture)),
          "x"u8.ToArray(),
          "hello"u8.ToArray(),
          "192.0.2.1"u8.ToArray(),
          [0x08, 0x00, 0x2b, 0x01, 0x02, 0x03],
          [0b0000_1011],
        ]);

        Assert.AreEqual(integer, decoder.Decode<BigInteger>(row, 0));
        Assert.AreEqual('x', decoder.Decode<char>(row, 1));
        CollectionAssert.AreEqual("hello".ToCharArray(), decoder.Decode<char[]>(row, 2));
        Assert.AreEqual(IPAddress.Parse("192.0.2.1"), decoder.Decode<IPAddress>(row, 3));
        Assert.AreEqual(
          PhysicalAddress.Parse("08-00-2B-01-02-03"),
          decoder.Decode<PhysicalAddress>(row, 4));
        BitArray bits = decoder.Decode<BitArray>(row, 5);
        CollectionAssert.AreEqual(
          new[] { true, false, true, true },
          Enumerable.Range(0, bits.Count).Select(index => bits[index]).ToArray());

        var nullDecoder = CreateDecoder([Column("number", MySqlType.NewDecimal)], binary: false);
        Assert.IsNull(nullDecoder.Decode<BigInteger?>(BuildTextRowRaw([null]), 0));
        Assert.ThrowsExactly<InvalidCastException>(() =>
          decoder.Decode<BigInteger>(BuildTextRow("1.5"), 0));

        Assert.AreEqual((Int128)integer, decoder.Decode<Int128>(row, 0));
        Assert.AreEqual((UInt128)integer, decoder.Decode<UInt128>(row, 0));
        var halfDecoder = CreateDecoder([Column("half", MySqlType.Float)], binary: false);
        Assert.AreEqual((Half)1.5f, halfDecoder.Decode<Half>(BuildTextRow("1.5"), 0));
        var unsignedDecoder = CreateDecoder([Column("number", MySqlType.NewDecimal)], binary: false);
        Assert.AreEqual(
          UInt128.MaxValue,
          unsignedDecoder.Decode<UInt128>(
            BuildTextRow(UInt128.MaxValue.ToString(CultureInfo.InvariantCulture)),
            0));
    }

    private static MySqlRowDecoder CreateDecoder(
        MySqlColumnMetadata[] columns,
        bool binary,
        MySqlZeroDateBehavior zeroDates = MySqlZeroDateBehavior.Error)
    {
        Utf8StringCache strings = new(capacity: 16, maximumByteLength: 64);
        MySqlRowDecoder decoder = new(strings, zeroDates);
        decoder.SetColumns(columns, binary);
        return decoder;
    }

    private static MySqlColumnMetadata Column(
      string name,
      MySqlType type,
      bool unsigned = false,
      uint columnLength = 0) =>
      new(
        name,
        name,
        string.Empty,
        string.Empty,
        string.Empty,
        type,
        unsigned ? MySqlColumnFlags.Unsigned : MySqlColumnFlags.None,
        MySqlProtocol.Utf8Mb4Collation,
        columnLength,
        0);

    private static MySqlColumnMetadata BinaryColumn(string name, MySqlType type) =>
      new(
        name,
        name,
        string.Empty,
        string.Empty,
        string.Empty,
        type,
        MySqlColumnFlags.Binary,
        MySqlProtocol.BinaryCollation,
        0,
        0);

    private static byte[] BuildTextRow(params string[] values) =>
      BuildTextRowRaw(values.Select(static value => (byte[]?)Encoding.UTF8.GetBytes(value)).ToArray());

    private static byte[] BuildTextRowRaw(byte[]?[] values)
    {
        MySqlPayloadWriter writer = new();
        try
        {
            foreach (var value in values)
            {
                if (value is null)
                {
                    writer.WriteByte(0xFB);
                }
                else
                {
                    writer.WriteLengthEncodedBytes(value);
                }
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Release();
        }
    }

    /// <summary>Builds a COM_STMT_EXECUTE style binary result row for decoder tests.</summary>
    private sealed class BinaryRowBuilder
    {
        private readonly int _count;
        private readonly byte[] _nullBitmap;
        private readonly MySqlPayloadWriter _values = new();

        internal BinaryRowBuilder(int count)
        {
            _count = count;
            _nullBitmap = new byte[(count + 9) / 8];
        }

        internal void SetNull(int ordinal)
        {
            var bit = ordinal + 2;
            _nullBitmap[bit >> 3] |= (byte)(1 << (bit & 7));
        }

        internal void WriteInt8(int ordinal, sbyte value) => _values.WriteByte(unchecked((byte)value));

        internal void WriteInt16(int ordinal, short value) => _values.WriteUInt16(unchecked((ushort)value));

        internal void WriteInt32(int ordinal, int value) => _values.WriteInt32(value);

        internal void WriteInt64(int ordinal, long value) => _values.WriteInt64(value);

        internal void WriteFloat(int ordinal, float value) => _values.WriteSingle(value);

        internal void WriteDouble(int ordinal, double value) => _values.WriteDouble(value);

        internal void WriteLengthEncodedBytes(int ordinal, ReadOnlySpan<byte> value) =>
          _values.WriteLengthEncodedBytes(value);

        internal void WriteDate(int ordinal, int year, int month, int day)
        {
            _values.WriteByte(4);
            _values.WriteUInt16((ushort)year);
            _values.WriteByte((byte)month);
            _values.WriteByte((byte)day);
        }

        internal void WriteDateTime(
            int ordinal,
            int year,
            int month,
            int day,
            int hour,
            int minute,
            int second,
            int microseconds)
        {
            _values.WriteByte(microseconds != 0 ? (byte)11 : (byte)7);
            _values.WriteUInt16((ushort)year);
            _values.WriteByte((byte)month);
            _values.WriteByte((byte)day);
            _values.WriteByte((byte)hour);
            _values.WriteByte((byte)minute);
            _values.WriteByte((byte)second);
            if (microseconds != 0)
            {
                _values.WriteUInt32((uint)microseconds);
            }
        }

        internal byte[] Build()
        {
            var row = new byte[1 + _nullBitmap.Length + _values.Length];
            row[0] = 0;
            _nullBitmap.CopyTo(row.AsSpan(1));
            _values.WrittenSpan.CopyTo(row.AsSpan(1 + _nullBitmap.Length));
            _values.Release();
            return row;
        }
    }
}

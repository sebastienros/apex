using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Apex.MsSqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MsSqlClient.Tests;

[TestClass]
public sealed class TdsRowCodecTests
{
    [TestMethod]
    public void DecodesSupportedScalarTypeMatrixLazily()
    {
        Guid guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        DateOnly date = new(2026, 8, 14);
        TimeOnly time = new(12, 34, 56, 789);
        DateTime dateTime = new(2025, 6, 7, 8, 9, 10, 321, DateTimeKind.Unspecified);
        DateTimeOffset offset = new(2024, 3, 2, 1, 2, 3, TimeSpan.FromHours(-7));

        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(15);
        WriteColumn(response, "boolean", TdsDataType.BitN, [1]);
        WriteColumn(response, "tiny", TdsDataType.Int1);
        WriteColumn(response, "small", TdsDataType.Int2);
        WriteColumn(response, "integer", TdsDataType.Int4);
        WriteColumn(response, "big", TdsDataType.Int8);
        WriteColumn(response, "real", TdsDataType.Float4);
        WriteColumn(response, "double", TdsDataType.Float8);
        WriteColumn(response, "number", TdsDataType.DecimalN, [17, 38, 2]);
        WriteColumn(response, "text", TdsDataType.NVarChar, CharacterInfo(8000));
        WriteColumn(response, "bytes", TdsDataType.BigVarBinary, UInt16Bytes(8000));
        WriteColumn(response, "id", TdsDataType.Guid, [16]);
        WriteColumn(response, "date", TdsDataType.Date);
        WriteColumn(response, "time", TdsDataType.Time, [7]);
        WriteColumn(response, "timestamp", TdsDataType.DateTime2, [7]);
        WriteColumn(response, "offset", TdsDataType.DateTimeOffset, [7]);

        response.WriteByte(TdsTokenType.Row);
        response.WriteByte(1);
        response.WriteByte(1);
        response.WriteByte(255);
        response.WriteInt16LittleEndian(-1234);
        response.WriteInt32LittleEndian(123456);
        response.WriteInt64LittleEndian(9_876_543_210);
        response.WriteInt32LittleEndian(BitConverter.SingleToInt32Bits(1.25f));
        response.WriteInt64LittleEndian(BitConverter.DoubleToInt64Bits(-9.5));
        response.WriteByte(13);
        response.WriteByte(1);
        response.WriteInt32LittleEndian(12345);
        response.WriteInt32LittleEndian(0);
        response.WriteInt32LittleEndian(0);
        WriteUShortValue(response, Encoding.Unicode.GetBytes("hello"));
        WriteUShortValue(response, [1, 2, 3, 4]);
        response.WriteByte(16);
        Span<byte> guidBytes = stackalloc byte[16];
        _ = guid.TryWriteBytes(guidBytes);
        response.Write(guidBytes);
        response.WriteByte(3);
        response.WriteUInt24LittleEndian(date.DayNumber);
        response.WriteByte(5);
        response.WriteUInt40LittleEndian(time.Ticks);
        response.WriteByte(8);
        response.WriteUInt40LittleEndian(dateTime.TimeOfDay.Ticks);
        response.WriteUInt24LittleEndian(DateOnly.FromDateTime(dateTime).DayNumber);
        response.WriteByte(10);
        response.WriteUInt40LittleEndian(offset.UtcDateTime.TimeOfDay.Ticks);
        response.WriteUInt24LittleEndian(DateOnly.FromDateTime(offset.UtcDateTime).DayNumber);
        response.WriteInt16LittleEndian(checked((short)offset.Offset.TotalMinutes));
        WriteDone(response);

        var parsed = new TdsQueryParser(new MsSqlRowDecoder())
          .Parse(response.WrittenMemory);
        var row = parsed.Rows[0];

        Assert.IsTrue(row.Get<bool>("boolean"));
        Assert.AreEqual((byte)255, row.Get<byte>("tiny"));
        Assert.AreEqual((short)-1234, row.Get<short>("small"));
        Assert.AreEqual(123456, row.Get<int>("integer"));
        Assert.AreEqual(9_876_543_210, row.Get<long>("big"));
        Assert.AreEqual(1.25f, row.Get<float>("real"));
        Assert.AreEqual(-9.5, row.Get<double>("double"));
        Assert.AreEqual(123.45m, row.Get<decimal>("number"));
        Assert.AreEqual("hello", row.Get<string>("text"));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, row.Get<byte[]>("bytes"));
        Assert.AreEqual(guid, row.Get<Guid>("id"));
        Assert.AreEqual(date, row.Get<DateOnly>("date"));
        Assert.AreEqual(time, row.Get<TimeOnly>("time"));
        Assert.AreEqual(dateTime, row.Get<DateTime>("timestamp"));
        Assert.AreEqual(offset, row.Get<DateTimeOffset>("offset"));
    }

    [TestMethod]
    public void DecodesNbcNullBitmapAndPlpChunks()
    {
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(2);
        WriteColumn(response, "missing", TdsDataType.IntN, [4]);
        WriteColumn(
          response,
          "large",
          TdsDataType.NVarChar,
          CharacterInfo(ushort.MaxValue));
        response.WriteByte(TdsTokenType.NbcRow);
        response.WriteByte(0x01);
        var first = Encoding.Unicode.GetBytes("chunk ");
        var second = Encoding.Unicode.GetBytes("value");
        response.WriteUInt64LittleEndian(checked((ulong)(first.Length + second.Length)));
        response.WriteUInt32LittleEndian(checked((uint)first.Length));
        response.Write(first);
        response.WriteUInt32LittleEndian(checked((uint)second.Length));
        response.Write(second);
        response.WriteUInt32LittleEndian(0);
        WriteDone(response);

        var row = new TdsQueryParser(new MsSqlRowDecoder())
          .Parse(response.WrittenMemory)
          .Rows[0];

        Assert.IsTrue(row.IsNull(0));
        Assert.AreEqual("chunk value", row.GetString(1));
    }

    [TestMethod]
    public void DecodesNullableNumericWidthsWithoutWidening()
    {
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(6);
        WriteColumn(response, "tiny", TdsDataType.IntN, [1]);
        WriteColumn(response, "small", TdsDataType.IntN, [2]);
        WriteColumn(response, "integer", TdsDataType.IntN, [4]);
        WriteColumn(response, "big", TdsDataType.IntN, [8]);
        WriteColumn(response, "real", TdsDataType.FloatN, [4]);
        WriteColumn(response, "double", TdsDataType.FloatN, [8]);
        response.WriteByte(TdsTokenType.Row);
        response.WriteByte(1);
        response.WriteByte(255);
        response.WriteByte(2);
        response.WriteInt16LittleEndian(-1234);
        response.WriteByte(4);
        response.WriteInt32LittleEndian(123456);
        response.WriteByte(8);
        response.WriteInt64LittleEndian(9_876_543_210);
        response.WriteByte(4);
        response.WriteInt32LittleEndian(BitConverter.SingleToInt32Bits(1.25f));
        response.WriteByte(8);
        response.WriteInt64LittleEndian(BitConverter.DoubleToInt64Bits(-9.5));
        WriteDone(response);

        var row = new TdsQueryParser(new MsSqlRowDecoder())
          .Parse(response.WrittenMemory)
          .Rows[0];

        Assert.AreEqual((byte)255, row.Get<byte>("tiny"));
        Assert.AreEqual((short)-1234, row.GetInt16("small"));
        Assert.AreEqual(123456, row.GetInt32("integer"));
        Assert.AreEqual(9_876_543_210, row.GetInt64("big"));
        Assert.AreEqual(1.25f, row.GetFloat("real"));
        Assert.AreEqual(-9.5, row.GetDouble("double"));
    }

    [TestMethod]
    public void DecodesCp1252ControlRangeAndUtf8Collations()
    {
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(2);
        WriteColumn(
          response,
          "ansi",
          TdsDataType.BigVarChar,
          CharacterInfo(32, collationInfo: 0x0409, sortId: 0));
        WriteColumn(
          response,
          "utf8",
          TdsDataType.BigVarChar,
          CharacterInfo(
            32,
            collationInfo: TdsCollation.Utf8Flag | 0x0409,
            sortId: 0));
        response.WriteByte(TdsTokenType.Row);
        WriteUShortValue(response, [0x80, 0x93, 0x94]);
        WriteUShortValue(response, Encoding.UTF8.GetBytes("€“”"));
        WriteDone(response);

        var row = new TdsQueryParser(new MsSqlRowDecoder())
          .Parse(response.WrittenMemory)
          .Rows[0];

        Assert.AreEqual("€“”", row.GetString("ansi"));
        Assert.AreEqual("€“”", row.GetString("utf8"));
    }

    [TestMethod]
    public void RejectsUnknownNonUnicodeCollation()
    {
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        WriteColumn(
          response,
          "value",
          TdsDataType.BigVarChar,
          CharacterInfo(32, collationInfo: 0x0001, sortId: 0));

        Assert.ThrowsExactly<InvalidDataException>(
          () => new TdsQueryParser(new MsSqlRowDecoder()).Parse(response.WrittenMemory));
    }

    [TestMethod]
    [DataRow(0x0405, 0, 1250)]
    [DataRow(0x0419, 0, 1251)]
    [DataRow(0x0409, 0, 1252)]
    [DataRow(0x0408, 0, 1253)]
    [DataRow(0x041F, 0, 1254)]
    [DataRow(0x040D, 0, 1255)]
    [DataRow(0x0401, 0, 1256)]
    [DataRow(0x0425, 0, 1257)]
    [DataRow(0x042A, 0, 1258)]
    [DataRow(0x0439, 0, 1200)]
    [DataRow(0x0411, 0, 932)]
    [DataRow(0x0804, 0, 936)]
    [DataRow(0x0412, 0, 949)]
    [DataRow(0x0404, 0, 950)]
    [DataRow(0x041E, 0, 874)]
    [DataRow(0, 30, 437)]
    [DataRow(0, 40, 850)]
    public void ResolvesInRepoCollationCodePages(int info, int sortId, int expected)
    {
        Assert.AreEqual(
          expected,
          TdsCollationCodec.ResolveCodePage(checked((uint)info), checked((byte)sortId)));
    }

    [TestMethod]
    public void DecodesNativeJsonPlpAsUtf8()
    {
        const string json = """{"name":"apex","values":[1,2]}""";
        var bytes = Encoding.UTF8.GetBytes(json);
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        WriteColumn(response, "payload", TdsDataType.Json);
        response.WriteByte(TdsTokenType.Row);
        response.WriteUInt64LittleEndian(checked((ulong)bytes.Length));
        response.WriteUInt32LittleEndian(checked((uint)bytes.Length));
        response.Write(bytes);
        response.WriteUInt32LittleEndian(0);
        WriteDone(response);

        var row = new TdsQueryParser(new MsSqlRowDecoder())
          .Parse(response.WrittenMemory)
          .Rows[0];

        Assert.AreEqual(json, row.GetString(0));
        Assert.AreEqual("apex", row.Get<JsonElement>(0).GetProperty("name").GetString());
        Assert.AreEqual(
          2,
          row.Get<JsonElement?>(0)!.Value.GetProperty("values").GetArrayLength());
    }

    [TestMethod]
    public void RoundsLegacyDateTimeToWholeMilliseconds()
    {
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        WriteColumn(response, "value", TdsDataType.DateTime);
        response.WriteByte(TdsTokenType.Row);
        response.WriteInt32LittleEndian(0);
        response.WriteUInt32LittleEndian(299);
        WriteDone(response);

        var value = new TdsQueryParser(new MsSqlRowDecoder())
          .Parse(response.WrittenMemory)
          .Rows[0]
          .GetDateTime(0);

        Assert.AreEqual(new DateTime(1900, 1, 1, 0, 0, 0, 997), value);
        Assert.AreEqual(0, value.Ticks % TimeSpan.TicksPerMillisecond);
    }

    [TestMethod]
    public void TypedScalarGetterDoesNotAllocate()
    {
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        WriteColumn(response, "value", TdsDataType.Int4);
        response.WriteByte(TdsTokenType.Row);
        response.WriteInt32LittleEndian(42);
        WriteDone(response);
        var row = new TdsQueryParser(new MsSqlRowDecoder())
          .Parse(response.WrittenMemory)
          .Rows[0];
        _ = row.GetInt32(0);
        _ = row.Get<int?>(0);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0;
        for (var i = 0; i < 1000; i++)
        {
            sum += row.GetInt32(0);
            sum += row.Get<int?>(0)!.Value;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(84_000, sum);
        Assert.AreEqual(0, allocated);
    }

    [TestMethod]
    public void EnforcesTypedCompatibilityAndNullableSemantics()
    {
        MsSqlRowDecoder decoder = new();
        SqlColumn column = new(
          "value",
          TdsDataType.Int4,
          sizeof(int),
          0,
          SqlDataFormat.Binary);
        var value = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(value, 42);
        var row = CreateDecoderRow(value);

        Assert.AreEqual(42, decoder.DecodeInt32(row, 0, column));
        Assert.AreEqual(42, decoder.DecodeNullableInt32(row, 0, column));
        Assert.ThrowsExactly<InvalidCastException>(
          () => decoder.DecodeInt64(row, 0, column));
        Assert.ThrowsExactly<InvalidCastException>(
          () => decoder.DecodeString(row, 0, column));

        var first = decoder.DecodeObject(row, 0, column)!;
        var second = decoder.DecodeObject(row, 0, column)!;
        Assert.AreEqual(42, first);
        Assert.AreSame(first, second);

        var nullRow = CreateDecoderRow(null);
        Assert.IsNull(decoder.DecodeNullableInt32(nullRow, 0, column));
        Assert.IsNull(decoder.DecodeObject(nullRow, 0, column));
        Assert.ThrowsExactly<InvalidCastException>(
          () => decoder.DecodeInt32(nullRow, 0, column));
    }

    [TestMethod]
    public void ReadOnlyMemoryBorrowsBufferedRowsAndCopiesBorrowedRows()
    {
        MsSqlRowDecoder decoder = new();
        SqlColumn column = new(
          "value",
          TdsDataType.BigVarBinary,
          3,
          0,
          SqlDataFormat.Binary);
        var row = CreateDecoderRow([1, 2, 3]);

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

        row[^1] = 9;
        CollectionAssert.AreEqual(new byte[] { 1, 2, 9 }, borrowed.ToArray());
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, copied.ToArray());
    }

    [TestMethod]
    public void DecodesZeroExtendedDecimalMagnitudeWidths()
    {
        MsSqlRowDecoder decoder = new();
        SqlColumn column = new(
          "value",
          TdsDataType.DecimalN,
          17,
          0,
          SqlDataFormat.Binary);

        Assert.AreEqual(
          3_351_057m,
          decoder.DecodeDecimal(
            CreateDecoderRow([1, 0x11, 0x22, 0x33]),
            0,
            column));
        Assert.AreEqual(
          new decimal(1, 2, 3, isNegative: false, scale: 0),
          decoder.DecodeDecimal(
            CreateDecoderRow([
              1,
          1, 0, 0, 0,
          2, 0, 0, 0,
          3, 0, 0, 0,
            ]),
            0,
            column));
    }

    [TestMethod]
    public void DecodesBclScalarAlternatives()
    {
        MsSqlRowDecoder decoder = new();
        BigInteger integer = BigInteger.Parse(
          "123456789012345678901234567890",
          CultureInfo.InvariantCulture);
        byte[] numeric = new byte[17];
        numeric[0] = 1;
        _ = integer.TryWriteBytes(
          numeric.AsSpan(1),
          out _,
          isUnsigned: true,
          isBigEndian: false);
        SqlColumn numericColumn = new(
          "number", TdsDataType.DecimalN, 17, 0, SqlDataFormat.Binary);
        Assert.AreEqual(
          integer,
          decoder.Decode<BigInteger>(CreateDecoderRow(numeric), 0, numericColumn, false));
        Assert.AreEqual(
          (Int128)integer,
          decoder.Decode<Int128>(CreateDecoderRow(numeric), 0, numericColumn, false));
        Assert.AreEqual(
          (UInt128)integer,
          decoder.Decode<UInt128>(CreateDecoderRow(numeric), 0, numericColumn, false));

        SqlColumn realColumn = new(
          "real", TdsDataType.Float4, 4, 0, SqlDataFormat.Binary);
        byte[] real = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(real, BitConverter.SingleToInt32Bits(1.5f));
        Assert.AreEqual(
          (Half)1.5f,
          decoder.Decode<Half>(CreateDecoderRow(real), 0, realColumn, false));

        TimeSpan duration = TimeSpan.FromHours(12.5);
        ArrayBufferWriter<byte> time = new();
        time.WriteUInt40LittleEndian(duration.Ticks);
        SqlColumn timeColumn = new(
          "time", TdsDataType.Time, 5, 7, SqlDataFormat.Binary);
        Assert.AreEqual(
          duration,
          decoder.Decode<TimeSpan>(CreateDecoderRow(time.WrittenSpan.ToArray()), 0, timeColumn, false));

        SqlColumn textColumn = new(
          "text", TdsDataType.NVarChar, 8000, 0, SqlDataFormat.Binary);
        Assert.AreEqual(
          'x',
          decoder.Decode<char>(CreateDecoderRow(Encoding.Unicode.GetBytes("x")), 0, textColumn, false));
        CollectionAssert.AreEqual(
          "hello".ToCharArray(),
          decoder.Decode<char[]>(CreateDecoderRow(Encoding.Unicode.GetBytes("hello")), 0, textColumn, false));
        Assert.AreEqual(
          IPAddress.Parse("192.0.2.1"),
          decoder.Decode<IPAddress>(
            CreateDecoderRow(Encoding.Unicode.GetBytes("192.0.2.1")), 0, textColumn, false));
        BitArray bits = decoder.Decode<BitArray>(
          CreateDecoderRow(Encoding.Unicode.GetBytes("1011")), 0, textColumn, false);
        Assert.IsTrue(bits[0]);
        Assert.IsFalse(bits[1]);

        SqlColumn binaryColumn = new(
          "mac", TdsDataType.BigVarBinary, 8000, 0, SqlDataFormat.Binary);
        Assert.AreEqual(
          PhysicalAddress.Parse("08-00-2B-01-02-03"),
          decoder.Decode<PhysicalAddress>(
            CreateDecoderRow([0x08, 0x00, 0x2b, 0x01, 0x02, 0x03]),
            0,
            binaryColumn,
            false));
        Assert.IsNull(decoder.Decode<BigInteger?>(
          CreateDecoderRow(null), 0, numericColumn, false));

        SqlColumn smallIntColumn = new(
          "small", TdsDataType.Int2, 2, 0, SqlDataFormat.Binary);
        byte[] signed = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(signed, -128);
        Assert.AreEqual(
          (sbyte)-128,
          decoder.Decode<sbyte>(CreateDecoderRow(signed), 0, smallIntColumn, false));
        BinaryPrimitives.WriteInt16LittleEndian(signed, 128);
        Assert.ThrowsExactly<OverflowException>(() =>
          decoder.Decode<sbyte>(CreateDecoderRow(signed), 0, smallIntColumn, false));
    }

    private static void WriteColumn(
        ArrayBufferWriter<byte> response,
        string name,
        byte type,
        ReadOnlySpan<byte> typeInfo = default)
    {
        response.WriteUInt32LittleEndian(0);
        response.WriteUInt16LittleEndian(1);
        response.WriteByte(type);
        response.Write(typeInfo);
        response.WriteBVarChar(name);
    }

    private static byte[] CharacterInfo(
        ushort maximumLength,
        uint collationInfo = 0x00D0_0409,
        byte sortId = 0x34)
    {
        var bytes = new byte[7];
        bytes[0] = (byte)maximumLength;
        bytes[1] = (byte)(maximumLength >> 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), collationInfo);
        bytes[6] = sortId;
        return bytes;
    }

    private static byte[] UInt16Bytes(ushort value) =>
      [(byte)value, (byte)(value >> 8)];

    private static void WriteUShortValue(
        ArrayBufferWriter<byte> response,
        ReadOnlySpan<byte> value)
    {
        response.WriteUInt16LittleEndian(checked((ushort)value.Length));
        response.Write(value);
    }

    private static void WriteDone(ArrayBufferWriter<byte> response)
    {
        response.WriteByte(TdsTokenType.Done);
        response.WriteUInt16LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteInt64LittleEndian(0);
    }

    private static byte[] CreateDecoderRow(byte[]? value)
    {
        var row = new byte[
          sizeof(ushort) +
          sizeof(int) +
          (value?.Length ?? 0)];
        BinaryPrimitives.WriteUInt16LittleEndian(row, 1);
        BinaryPrimitives.WriteInt32LittleEndian(
          row.AsSpan(sizeof(ushort)),
          value?.Length ?? -1);
        value?.CopyTo(row, sizeof(ushort) + sizeof(int));
        return row;
    }
}

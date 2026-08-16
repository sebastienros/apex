using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text.Json;
using Apex.MySqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlParameterEncoderTests
{
    [TestMethod]
    public void RejectsParameterCountMismatch()
    {
        MySqlPayloadWriter writer = new();
        try
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
              MySqlParameterEncoder.WriteExecute(
                writer,
                statementId: 1,
                MySqlCursorType.NoCursor,
                SqlParameters.Create(1),
                expectedCount: 2));
        }
        finally
        {
            writer.Release();
        }
    }

    [TestMethod]
    public void WritesHeaderStatementIdCursorTypeAndIterationCount()
    {
        var payload = Encode(MySqlCursorType.ReadOnly, SqlParameters.Empty);
        MySqlPayloadReader reader = new(payload);

        Assert.AreEqual((byte)MySqlCommand.StatementExecute, reader.ReadByte());
        Assert.AreEqual(7u, reader.ReadUInt32());
        Assert.AreEqual((byte)MySqlCursorType.ReadOnly, reader.ReadByte());
        Assert.AreEqual(1u, reader.ReadUInt32());
        Assert.AreEqual(0, reader.Remaining);
    }

    [TestMethod]
    public void OmitsBitmapAndTypeTableWhenThereAreNoParameters()
    {
        var payload = Encode(MySqlCursorType.NoCursor, SqlParameters.Empty);

        // header(1) + statement id(4) + cursor type(1) + iteration count(4) = 10 bytes total.
        Assert.AreEqual(10, payload.Length);
    }

    [TestMethod]
    public void SetsNullBitmapBitForEachNullParameterOnly()
    {
        SqlParameters parameters = SqlParameters.Create(
          SqlValue.Null,
          SqlValue.From(1),
          SqlValue.Null,
          SqlValue.From("x"));

        var payload = Encode(MySqlCursorType.NoCursor, parameters);
        MySqlPayloadReader reader = new(payload);
        reader.Skip(10);

        Assert.AreEqual(0b0000_0101, reader.ReadByte());
    }

    [TestMethod]
    public void NullBitmapSpansMultipleBytesForManyParameters()
    {
        SqlValue[] values = new SqlValue[10];
        values[0] = SqlValue.Null;
        values[9] = SqlValue.Null;
        for (var i = 1; i < 9; i++)
        {
            values[i] = SqlValue.From(i);
        }

        var payload = Encode(MySqlCursorType.NoCursor, SqlParameters.Create(values));
        MySqlPayloadReader reader = new(payload);
        reader.Skip(10);

        Assert.AreEqual(0b0000_0001, reader.ReadByte());
        Assert.AreEqual(0b0000_0010, reader.ReadByte());
    }

    [TestMethod]
    public void WritesTypeTableForEachSupportedSqlValueKind()
    {
        SqlParameters parameters = SqlParameters.Create(
          SqlValue.From(true),
          SqlValue.From((short)1),
          SqlValue.From(1),
          SqlValue.From(1L),
          SqlValue.From(1.5f),
          SqlValue.From(1.5d),
          SqlValue.From(1.5m),
          SqlValue.From("s"),
          SqlValue.From(new byte[] { 1 }),
          SqlValue.From(new ReadOnlyMemory<byte>(new byte[] { 1 })),
          SqlValue.From(Guid.Empty),
          SqlValue.From(new DateOnly(2024, 1, 1)),
          SqlValue.From(new TimeOnly(1, 2, 3)),
          SqlValue.From(new DateTime(2024, 1, 1)),
          SqlValue.From(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)),
          SqlValue.From(JsonDocument.Parse("{}")),
          SqlValue.From(JsonDocument.Parse("{}").RootElement));

        var payload = Encode(MySqlCursorType.NoCursor, parameters);
        MySqlPayloadReader reader = new(payload);
        SkipHeaderBitmapAndSendFlag(ref reader, parameters.Count);

        (MySqlType Type, bool Unsigned)[] expected =
        [
          (MySqlType.Tiny, false),
      (MySqlType.Short, false),
      (MySqlType.Long, false),
      (MySqlType.LongLong, false),
      (MySqlType.Float, false),
      (MySqlType.Double, false),
      (MySqlType.NewDecimal, false),
      (MySqlType.VarString, false),
      (MySqlType.Blob, false),
      (MySqlType.Blob, false),
      (MySqlType.VarString, false),
      (MySqlType.Date, false),
      (MySqlType.Time, false),
      (MySqlType.DateTime, false),
      (MySqlType.DateTime, false),
      (MySqlType.VarString, false),
      (MySqlType.VarString, false),
    ];

        AssertTypeTable(ref reader, expected);
    }

    [TestMethod]
    public void WritesTypeTableForUnsignedAndObjectTypes()
    {
        SqlParameters parameters = SqlParameters.Create(
          SqlValue.From((byte)1),
          SqlValue.From((ushort)1),
          SqlValue.From((uint)1),
          SqlValue.From((ulong)1),
          SqlValue.From((sbyte)-1),
          SqlValue.From('c'),
          SqlValue.From(TimeSpan.FromHours(1)));

        var payload = Encode(MySqlCursorType.NoCursor, parameters);
        MySqlPayloadReader reader = new(payload);
        SkipHeaderBitmapAndSendFlag(ref reader, parameters.Count);

        (MySqlType Type, bool Unsigned)[] expected =
        [
          (MySqlType.Tiny, true),
      (MySqlType.Short, true),
      (MySqlType.Long, true),
      (MySqlType.LongLong, true),
      (MySqlType.Tiny, false),
      (MySqlType.VarString, false),
      (MySqlType.Time, false),
    ];

        AssertTypeTable(ref reader, expected);
    }

        [TestMethod]
        public void EncodesBclAlternativeParameters()
        {
                BigInteger integer = BigInteger.Parse(
                    "123456789012345678901234567890",
                    CultureInfo.InvariantCulture);
                SqlParameters parameters = SqlParameters.Create(
                    SqlValue.From(integer),
                    SqlValue.From("hello".ToCharArray()),
                    SqlValue.From(IPAddress.Parse("192.0.2.1")),
                    SqlValue.From(PhysicalAddress.Parse("08-00-2B-01-02-03")),
                    SqlValue.From(new BitArray(new[] { true, false, true, true })));

                var payload = Encode(MySqlCursorType.NoCursor, parameters);
                MySqlPayloadReader reader = new(payload);
                SkipHeaderBitmapAndSendFlag(ref reader, parameters.Count);
                AssertTypeTable(ref reader,
                [
                    (MySqlType.NewDecimal, false),
                    (MySqlType.VarString, false),
                    (MySqlType.VarString, false),
                    (MySqlType.Blob, false),
                      (MySqlType.LongLong, true),
                ]);

                Assert.AreEqual(integer.ToString(CultureInfo.InvariantCulture), reader.ReadLengthEncodedString());
                Assert.AreEqual("hello", reader.ReadLengthEncodedString());
                Assert.AreEqual("192.0.2.1", reader.ReadLengthEncodedString());
                CollectionAssert.AreEqual(
                    new byte[] { 0x08, 0x00, 0x2b, 0x01, 0x02, 0x03 },
                    reader.ReadLengthEncodedSpan(out _).ToArray());
                Assert.AreEqual(0b1011UL, reader.ReadUInt64());
        }

        [TestMethod]
        public void EncodesHalfAnd128BitIntegerParameters()
        {
                SqlParameters parameters = SqlParameters.Create(
                    SqlValue.From((Half)1.5f),
                    SqlValue.From(Int128.MinValue),
                    SqlValue.From(UInt128.MaxValue));

                var payload = Encode(MySqlCursorType.NoCursor, parameters);
                MySqlPayloadReader reader = new(payload);
                SkipHeaderBitmapAndSendFlag(ref reader, parameters.Count);
                AssertTypeTable(ref reader,
                [
                    (MySqlType.Float, false),
                    (MySqlType.NewDecimal, false),
                    (MySqlType.NewDecimal, false),
                ]);
                Assert.AreEqual(1.5f, BitConverter.Int32BitsToSingle((int)reader.ReadUInt32()));
                Assert.AreEqual(Int128.MinValue.ToString(CultureInfo.InvariantCulture), reader.ReadLengthEncodedString());
                Assert.AreEqual(UInt128.MaxValue.ToString(CultureInfo.InvariantCulture), reader.ReadLengthEncodedString());
        }

    [TestMethod]
    public void RejectsUnsupportedObjectParameterType()
    {
        MySqlPayloadWriter writer = new();
        try
        {
            Assert.ThrowsExactly<NotSupportedException>(() =>
              MySqlParameterEncoder.WriteExecute(
                writer,
                statementId: 1,
                MySqlCursorType.NoCursor,
                SqlParameters.Create(SqlValue.From(new object())),
                expectedCount: 1));
        }
        finally
        {
            writer.Release();
        }
    }

    [TestMethod]
    public void EncodesScalarValueBodies()
    {
        SqlParameters parameters = SqlParameters.Create(
          SqlValue.From(true),
          SqlValue.From((short)0x0201),
          SqlValue.From(0x04030201));

        var payload = Encode(MySqlCursorType.NoCursor, parameters);
        MySqlPayloadReader reader = new(payload);
        SkipToValues(ref reader, parameters.Count);

        Assert.AreEqual((byte)1, reader.ReadByte());
        Assert.AreEqual((ushort)0x0201, reader.ReadUInt16());
        Assert.AreEqual(0x04030201u, reader.ReadUInt32());
    }

    [TestMethod]
    public void EncodesStringAndBinaryValuesAsLengthEncodedBytes()
    {
        SqlParameters parameters = SqlParameters.Create(
          SqlValue.From("abc"),
          SqlValue.From(new byte[] { 9, 8, 7 }));

        var payload = Encode(MySqlCursorType.NoCursor, parameters);
        MySqlPayloadReader reader = new(payload);
        SkipToValues(ref reader, parameters.Count);

        Assert.AreEqual("abc", reader.ReadLengthEncodedString());
        CollectionAssert.AreEqual(
          new byte[] { 9, 8, 7 },
          reader.ReadLengthEncodedSpan(out var isNull).ToArray());
        Assert.IsFalse(isNull);
    }

    [TestMethod]
    public void EncodesDecimalAndGuidAsInvariantCultureStrings()
    {
        Guid guid = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        SqlParameters parameters = SqlParameters.Create(SqlValue.From(12.5m), SqlValue.From(guid));

        var payload = Encode(MySqlCursorType.NoCursor, parameters);
        MySqlPayloadReader reader = new(payload);
        SkipToValues(ref reader, parameters.Count);

        Assert.AreEqual("12.5", reader.ReadLengthEncodedString());
        Assert.AreEqual(
          guid.ToString("D", CultureInfo.InvariantCulture),
          reader.ReadLengthEncodedString());
    }

    [TestMethod]
    public void EncodesDateOnlyAsFourByteStructure()
    {
        SqlParameters parameters = SqlParameters.Create(SqlValue.From(new DateOnly(2024, 3, 15)));

        var payload = Encode(MySqlCursorType.NoCursor, parameters);
        MySqlPayloadReader reader = new(payload);
        SkipToValues(ref reader, parameters.Count);

        Assert.AreEqual((byte)4, reader.ReadByte());
        Assert.AreEqual((ushort)2024, reader.ReadUInt16());
        Assert.AreEqual((byte)3, reader.ReadByte());
        Assert.AreEqual((byte)15, reader.ReadByte());
        Assert.AreEqual(0, reader.Remaining);
    }

    [TestMethod]
    public void EncodesDateTimeWithMicrosecondsWhenPresent()
    {
        var value = new DateTime(2024, 3, 15, 13, 45, 30, DateTimeKind.Unspecified).AddTicks(1230);
        SqlParameters parameters = SqlParameters.Create(SqlValue.From(value));

        var payload = Encode(MySqlCursorType.NoCursor, parameters);
        MySqlPayloadReader reader = new(payload);
        SkipToValues(ref reader, parameters.Count);

        Assert.AreEqual((byte)11, reader.ReadByte());
        Assert.AreEqual((ushort)2024, reader.ReadUInt16());
        Assert.AreEqual((byte)3, reader.ReadByte());
        Assert.AreEqual((byte)15, reader.ReadByte());
        Assert.AreEqual((byte)13, reader.ReadByte());
        Assert.AreEqual((byte)45, reader.ReadByte());
        Assert.AreEqual((byte)30, reader.ReadByte());
        Assert.AreEqual(123u, reader.ReadUInt32());
    }

    [TestMethod]
    public void EncodesDateTimeWithoutTimeComponentAsFourBytes()
    {
        DateTime value = new(2024, 3, 15);
        SqlParameters parameters = SqlParameters.Create(SqlValue.From(value));

        var payload = Encode(MySqlCursorType.NoCursor, parameters);
        MySqlPayloadReader reader = new(payload);
        SkipToValues(ref reader, parameters.Count);

        Assert.AreEqual((byte)4, reader.ReadByte());
        Assert.AreEqual(0, reader.Remaining - 4);
    }

    [TestMethod]
    public void EncodesDateTimeOffsetUsingUtcDateTime()
    {
        DateTimeOffset value = new(2024, 3, 15, 13, 0, 0, TimeSpan.FromHours(2));
        SqlParameters parameters = SqlParameters.Create(SqlValue.From(value));

        var payload = Encode(MySqlCursorType.NoCursor, parameters);
        MySqlPayloadReader reader = new(payload);
        SkipToValues(ref reader, parameters.Count);

        Assert.AreEqual((byte)7, reader.ReadByte());
        Assert.AreEqual((ushort)2024, reader.ReadUInt16());
        Assert.AreEqual((byte)3, reader.ReadByte());
        Assert.AreEqual((byte)15, reader.ReadByte());
        Assert.AreEqual((byte)11, reader.ReadByte());
    }

    [TestMethod]
    public void EncodesZeroTimeOnlyAsSingleZeroLengthByte()
    {
        SqlParameters parameters = SqlParameters.Create(SqlValue.From(TimeSpan.Zero));

        var payload = Encode(MySqlCursorType.NoCursor, parameters);
        MySqlPayloadReader reader = new(payload);
        SkipToValues(ref reader, parameters.Count);

        Assert.AreEqual((byte)0, reader.ReadByte());
        Assert.AreEqual(0, reader.Remaining);
    }

    [TestMethod]
    public void EncodesNegativeTimeSpanWithSignAndDays()
    {
        var value = -new TimeSpan(1, 2, 3, 4);
        SqlParameters parameters = SqlParameters.Create(SqlValue.From(value));

        var payload = Encode(MySqlCursorType.NoCursor, parameters);
        MySqlPayloadReader reader = new(payload);
        SkipToValues(ref reader, parameters.Count);

        Assert.AreEqual((byte)8, reader.ReadByte());
        Assert.AreEqual((byte)1, reader.ReadByte());
        Assert.AreEqual(1u, reader.ReadUInt32());
        Assert.AreEqual((byte)2, reader.ReadByte());
        Assert.AreEqual((byte)3, reader.ReadByte());
        Assert.AreEqual((byte)4, reader.ReadByte());
    }

    [TestMethod]
    public void NullValuesContributeNoValueBytes()
    {
        SqlParameters parameters = SqlParameters.Create(SqlValue.Null, SqlValue.From(1));

        var payload = Encode(MySqlCursorType.NoCursor, parameters);
        MySqlPayloadReader reader = new(payload);
        SkipToValues(ref reader, parameters.Count);

        // Only the non-null second parameter contributes a 4 byte MYSQL_TYPE_LONG value.
        Assert.AreEqual(4, reader.Remaining);
        Assert.AreEqual(1u, reader.ReadUInt32());
    }

    private static byte[] Encode(MySqlCursorType cursorType, SqlParameters parameters)
    {
        MySqlPayloadWriter writer = new();
        try
        {
            MySqlParameterEncoder.WriteExecute(writer, 7, cursorType, parameters, parameters.Count);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Release();
        }
    }

    /// <summary>Skips header(10) + null bitmap + the "new parameter bound" flag byte.</summary>
    private static void SkipHeaderBitmapAndSendFlag(ref MySqlPayloadReader reader, int parameterCount)
    {
        reader.Skip(10 + ((parameterCount + 7) / 8) + 1);
    }

    private static void SkipToValues(ref MySqlPayloadReader reader, int parameterCount)
    {
        SkipHeaderBitmapAndSendFlag(ref reader, parameterCount);
        reader.Skip(parameterCount * 2);
    }

    private static void AssertTypeTable(
        ref MySqlPayloadReader reader,
        (MySqlType Type, bool Unsigned)[] expected)
    {
        foreach ((var type, var unsigned) in expected)
        {
            Assert.AreEqual((byte)type, reader.ReadByte());
            Assert.AreEqual(unsigned ? (byte)0x80 : (byte)0x00, reader.ReadByte());
        }
    }
}

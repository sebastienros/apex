using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text;
using Apex.MsSqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MsSqlClient.Tests;

[TestClass]
public sealed class TdsRequestWriterTests
{
    [TestMethod]
    public void SqlBatchUsesAllHeadersAndUtf16Sql()
    {
        var payload =
          TdsRequestWriter.BuildSqlBatch("SELECT 1", 0x1020304050607080);
        TdsPayloadReader reader = new(payload.Span);

        Assert.AreEqual(22, reader.ReadInt32LittleEndian());
        Assert.AreEqual(18, reader.ReadInt32LittleEndian());
        Assert.AreEqual(2, reader.ReadUInt16LittleEndian());
        Assert.AreEqual(0x1020304050607080, reader.ReadInt64LittleEndian());
        Assert.AreEqual(1, reader.ReadInt32LittleEndian());
        Assert.AreEqual("SELECT 1", Encoding.Unicode.GetString(reader.ReadSpan(reader.Remaining)));
    }

    [TestMethod]
    public void ParametersUseSpExecuteSqlAndNamedTypedValues()
    {
        SqlParameters parameters = SqlParameters.Create(
          42,
          "not interpolated",
          Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        var payload = TdsRequestWriter.BuildExecuteSql(
          "SELECT @P1, @P2, @P3",
          parameters,
          0);
        TdsPayloadReader reader = new(payload.Span);
        reader.Skip(22);
        Assert.AreEqual(ushort.MaxValue, reader.ReadUInt16LittleEndian());
        Assert.AreEqual(10, reader.ReadUInt16LittleEndian());
        Assert.AreEqual(0, reader.ReadUInt16LittleEndian());

        Assert.AreEqual(string.Empty, reader.ReadBVarChar());
        Assert.AreEqual(0, reader.ReadByte());
        Assert.AreEqual(TdsDataType.NVarChar, reader.ReadByte());
        Assert.AreEqual(8000, reader.ReadUInt16LittleEndian());
        reader.Skip(5);
        int sqlLength = reader.ReadUInt16LittleEndian();
        Assert.AreEqual(
          "SELECT @P1, @P2, @P3",
          Encoding.Unicode.GetString(reader.ReadSpan(sqlLength)));

        Assert.IsTrue(ContainsUtf16(payload.Span, "@P1 int,@P2 nvarchar(4000),@P3 uniqueidentifier"));
        Assert.IsTrue(ContainsUtf16(payload.Span, "@P1"));
        Assert.IsTrue(ContainsUtf16(payload.Span, "not interpolated"));
    }

    [TestMethod]
    public void EncodesBclAlternativeParameters()
    {
        SqlParameters parameters = SqlParameters.Create(
          SqlValue.From(BigInteger.Parse(
            "123456789012345678901234567890",
            CultureInfo.InvariantCulture)),
          SqlValue.From(TimeSpan.FromHours(12.5)),
          SqlValue.From((sbyte)-128),
          SqlValue.From('x'),
          SqlValue.From("hello".ToCharArray()),
          SqlValue.From(IPAddress.Parse("192.0.2.1")),
          SqlValue.From(PhysicalAddress.Parse("08-00-2B-01-02-03")),
          SqlValue.From(new BitArray(new[] { true, false, true, true })));

        var payload = TdsRequestWriter.BuildExecuteSql(
          "SELECT @P1, @P2, @P3, @P4, @P5, @P6, @P7, @P8",
          parameters,
          0);

        Assert.IsTrue(ContainsUtf16(
          payload.Span,
          "@P1 numeric(38,0),@P2 time(7),@P3 smallint,@P4 nvarchar(4000)," +
          "@P5 nvarchar(4000),@P6 nvarchar(4000),@P7 varbinary(8000),@P8 nvarchar(4000)"));
        Assert.IsTrue(ContainsUtf16(payload.Span, "192.0.2.1"));
        Assert.IsTrue(ContainsUtf16(payload.Span, "1011"));

        Assert.ThrowsExactly<OverflowException>(() =>
          TdsRequestWriter.BuildExecuteSql(
            "SELECT @P1",
            SqlParameters.Create(SqlValue.From(BigInteger.Pow(10, 38))),
            0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
          TdsRequestWriter.BuildExecuteSql(
            "SELECT @P1",
            SqlParameters.Create(SqlValue.From(TimeSpan.FromDays(1))),
            0));
    }

    [TestMethod]
    public void EncodesHalfAnd128BitIntegerParameters()
    {
        SqlParameters parameters = SqlParameters.Create(
          SqlValue.From((Half)1.5f),
          SqlValue.From((Int128)1234567890123456789),
          SqlValue.From((UInt128)12345678901234567890UL));

        var payload = TdsRequestWriter.BuildExecuteSql(
          "SELECT @P1, @P2, @P3",
          parameters,
          0);

        Assert.IsTrue(ContainsUtf16(
          payload.Span,
          "@P1 real,@P2 numeric(38,0),@P3 numeric(38,0)"));
        Assert.ThrowsExactly<OverflowException>(() =>
          TdsRequestWriter.BuildExecuteSql(
            "SELECT @P1",
            SqlParameters.Create(SqlValue.From(Int128.MaxValue)),
            0));
        Assert.ThrowsExactly<OverflowException>(() =>
          TdsRequestWriter.BuildExecuteSql(
            "SELECT @P1",
            SqlParameters.Create(SqlValue.From(UInt128.MaxValue)),
            0));
    }

    [TestMethod]
    public void PrepareExecuteUsesProcedureIdAndJavaCompatibleLayout()
    {
        var payload = TdsRequestWriter.BuildPrepareExecute(
          "SELECT @P1",
          SqlParameters.Create(42),
          0x1020304050607080);
        TdsPayloadReader reader = new(payload.Span);

        AssertAllHeaders(ref reader, 0x1020304050607080);
        AssertRpcHeader(ref reader, TdsProcedureId.PrepExec);
        AssertIntParameter(ref reader, string.Empty, output: true, 0);
        AssertNVarCharParameter(ref reader, string.Empty, "@P1 int");
        AssertNVarCharParameter(ref reader, string.Empty, "SELECT @P1");
        AssertIntParameter(ref reader, "@P1", output: false, 42);
        Assert.AreEqual(0, reader.Remaining);
    }

    [TestMethod]
    public void ExecuteUsesProcedureIdHandleThenValues()
    {
        var payload = TdsRequestWriter.BuildExecute(
          0x01020304,
          SqlParameters.Create(43),
          0);
        TdsPayloadReader reader = new(payload.Span);

        AssertAllHeaders(ref reader, 0);
        AssertRpcHeader(ref reader, TdsProcedureId.Execute);
        AssertIntParameter(ref reader, string.Empty, output: true, 0x01020304);
        AssertIntParameter(ref reader, "@P1", output: false, 43);
        Assert.AreEqual(0, reader.Remaining);
    }

    [TestMethod]
    public void UnprepareUsesProcedureIdAndInputHandle()
    {
        var payload =
          TdsRequestWriter.BuildUnprepare(0x01020304, 7);
        TdsPayloadReader reader = new(payload.Span);

        AssertAllHeaders(ref reader, 7);
        AssertRpcHeader(ref reader, TdsProcedureId.Unprepare);
        AssertIntParameter(ref reader, string.Empty, output: false, 0x01020304);
        Assert.AreEqual(0, reader.Remaining);
    }

    private static void AssertAllHeaders(
        ref TdsPayloadReader reader,
        long transactionDescriptor)
    {
        Assert.AreEqual(22, reader.ReadInt32LittleEndian());
        Assert.AreEqual(18, reader.ReadInt32LittleEndian());
        Assert.AreEqual(2, reader.ReadUInt16LittleEndian());
        Assert.AreEqual(transactionDescriptor, reader.ReadInt64LittleEndian());
        Assert.AreEqual(1, reader.ReadInt32LittleEndian());
    }

    private static void AssertRpcHeader(
        ref TdsPayloadReader reader,
        ushort procedureId)
    {
        Assert.AreEqual(ushort.MaxValue, reader.ReadUInt16LittleEndian());
        Assert.AreEqual(procedureId, reader.ReadUInt16LittleEndian());
        Assert.AreEqual(0, reader.ReadUInt16LittleEndian());
    }

    private static void AssertIntParameter(
        ref TdsPayloadReader reader,
        string name,
        bool output,
        int value)
    {
        Assert.AreEqual(name, reader.ReadBVarChar());
        Assert.AreEqual(output ? 1 : 0, reader.ReadByte());
        Assert.AreEqual(TdsDataType.IntN, reader.ReadByte());
        Assert.AreEqual(sizeof(int), reader.ReadByte());
        Assert.AreEqual(sizeof(int), reader.ReadByte());
        Assert.AreEqual(value, reader.ReadInt32LittleEndian());
    }

    private static void AssertNVarCharParameter(
        ref TdsPayloadReader reader,
        string name,
        string value)
    {
        Assert.AreEqual(name, reader.ReadBVarChar());
        Assert.AreEqual(0, reader.ReadByte());
        Assert.AreEqual(TdsDataType.NVarChar, reader.ReadByte());
        Assert.AreEqual(8000, reader.ReadUInt16LittleEndian());
        reader.Skip(5);
        int byteLength = reader.ReadUInt16LittleEndian();
        Assert.AreEqual(
          value,
          Encoding.Unicode.GetString(reader.ReadSpan(byteLength)));
    }

    private static bool ContainsUtf16(ReadOnlySpan<byte> payload, string value)
    {
        var expected = Encoding.Unicode.GetBytes(value);
        return payload.IndexOf(expected) >= 0;
    }
}

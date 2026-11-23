using System.Buffers;
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
    public void DefinitionsMatchOnlyParametersWithTheSameDeclarations()
    {
        var prepared = TdsRequestWriter.BuildDefinitions(
          SqlParameters.Create(10.5m, "short", 1, new DateOnly(2026, 1, 1)));
        Assert.AreEqual("@P1 numeric(38,1),@P2 nvarchar(4000),@P3 int,@P4 date", prepared);

        Assert.IsTrue(TdsRequestWriter.DefinitionsMatch(
          prepared, SqlParameters.Create(99.9m, "other", 2, new DateOnly(2027, 2, 2))));
        // NULL converts to any declared type, so it keeps using the prepared handle.
        Assert.IsTrue(TdsRequestWriter.DefinitionsMatch(
          prepared, SqlParameters.Create(SqlValue.Null, SqlValue.Null, SqlValue.Null, SqlValue.Null)));

        Assert.IsFalse(TdsRequestWriter.DefinitionsMatch(
          prepared, SqlParameters.Create(9.99m, "other", 2, new DateOnly(2027, 2, 2))));
        Assert.IsFalse(TdsRequestWriter.DefinitionsMatch(
          prepared, SqlParameters.Create(10.5m, new string('x', 4001), 2, new DateOnly(2027, 2, 2))));
        Assert.IsFalse(TdsRequestWriter.DefinitionsMatch(
          prepared, SqlParameters.Create(10.5m, "other", 3L, new DateOnly(2027, 2, 2))));
        Assert.IsFalse(TdsRequestWriter.DefinitionsMatch(
          prepared, SqlParameters.Create(10.5m, "other", 2, new DateTime(2027, 2, 2))));
        Assert.IsFalse(TdsRequestWriter.DefinitionsMatch(
          prepared, SqlParameters.Create(10.5m, "other", 2)));
        Assert.IsFalse(TdsRequestWriter.DefinitionsMatch(
          prepared, SqlParameters.Create(10.5m, "other", 2, new DateOnly(2027, 2, 2), 5)));
        Assert.IsTrue(TdsRequestWriter.DefinitionsMatch(string.Empty, SqlParameters.Empty));
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

    [TestMethod]
    public void ExecuteSqlWritesTheSameDefinitionsAsBuildDefinitions()
    {
        SqlParameters[] cases =
        [
            SqlParameters.Empty,
            SqlParameters.Create(1),
            SqlParameters.Create(
              10.5m,
              0.0001m,
              12m,
              "short",
              new string('x', 4001),
              SqlValue.Null,
              new byte[] { 1 },
              true,
              2L,
              new DateOnly(2026, 1, 1),
              Guid.Empty,
              12.25),
            // Enough parameters for the declaration to exceed the stack buffer.
            SqlParameters.Create(Enumerable.Range(0, 40).Select(static i => (SqlValue)(i + 0.5m)).ToArray()),
        ];

        foreach (var parameters in cases)
        {
            var definitions = TdsRequestWriter.BuildDefinitions(parameters);
            var payload = TdsRequestWriter.BuildExecuteSql("SELECT 1", parameters, 0);
            TdsPayloadReader reader = new(payload.Span);
            AssertAllHeaders(ref reader, 0);
            AssertRpcHeader(ref reader, TdsProcedureId.ExecuteSql);
            AssertNVarCharParameter(ref reader, string.Empty, "SELECT 1");
            AssertNVarCharParameter(ref reader, string.Empty, definitions);
            Assert.IsTrue(TdsRequestWriter.DefinitionsMatch(definitions, parameters));
            if (parameters.Count > 0)
            {
                Assert.AreEqual("@P1", reader.ReadBVarChar());
            }
        }

        Assert.AreEqual(
          "@P1 numeric(38,1),@P2 numeric(38,4),@P3 numeric(38,0),@P4 nvarchar(4000)," +
          "@P5 nvarchar(max),@P6 nvarchar(4000),@P7 varbinary(8000),@P8 bit,@P9 bigint," +
          "@P10 date,@P11 uniqueidentifier,@P12 float",
          TdsRequestWriter.BuildDefinitions(cases[2]));
    }

    [TestMethod]
    public void LongDefinitionsUseNVarCharMax()
    {
        var parameters = SqlParameters.Create(
          Enumerable.Range(0, 300).Select(static i => (SqlValue)$"value-{i}").ToArray());
        var definitions = TdsRequestWriter.BuildDefinitions(parameters);
        Assert.IsGreaterThan(4000, definitions.Length);

        var payload = TdsRequestWriter.BuildExecuteSql("SELECT 1", parameters, 0);
        TdsPayloadReader reader = new(payload.Span);
        reader.Skip(22 + 6);
        AssertNVarCharParameter(ref reader, string.Empty, "SELECT 1");
        Assert.AreEqual(string.Empty, reader.ReadBVarChar());
        Assert.AreEqual(0, reader.ReadByte());
        Assert.AreEqual(TdsDataType.NVarChar, reader.ReadByte());
        Assert.AreEqual(ushort.MaxValue, reader.ReadUInt16LittleEndian());
        reader.Skip(5);
        Assert.AreEqual(definitions.Length * 2L, reader.ReadInt64LittleEndian());
        Assert.AreEqual(definitions.Length * 2, reader.ReadInt32LittleEndian());
        Assert.AreEqual(
          definitions,
          Encoding.Unicode.GetString(reader.ReadSpan(definitions.Length * 2)));
        Assert.AreEqual(0, reader.ReadInt32LittleEndian());
        Assert.AreEqual("@P1", reader.ReadBVarChar());
    }

    [TestMethod]
    public void StringLengthsAreTwoBytesPerCharIncludingLoneSurrogates()
    {
        var lone = "a\uD800b";
        var payload = TdsRequestWriter.BuildExecute(1, SqlParameters.Create(lone), 0);
        TdsPayloadReader reader = new(payload.Span);
        reader.Skip(22 + 6);
        AssertIntParameter(ref reader, string.Empty, output: true, 1);
        Assert.AreEqual("@P1", reader.ReadBVarChar());
        Assert.AreEqual(0, reader.ReadByte());
        Assert.AreEqual(TdsDataType.NVarChar, reader.ReadByte());
        Assert.AreEqual(8000, reader.ReadUInt16LittleEndian());
        reader.Skip(5);
        Assert.AreEqual(Encoding.Unicode.GetByteCount(lone), reader.ReadUInt16LittleEndian());
        CollectionAssert.AreEqual(
          Encoding.Unicode.GetBytes(lone),
          reader.ReadSpan(lone.Length * 2).ToArray());
        Assert.AreEqual(0, reader.Remaining);

        Assert.AreEqual(
          "@P1 nvarchar(4000),@P2 nvarchar(max)",
          TdsRequestWriter.BuildDefinitions(SqlParameters.Create(
            new string('\uD800', 4000),
            new string('\uD800', 4001))));
    }

    [TestMethod]
    public void ReusedBufferProducesTheSamePayload()
    {
        var parameters = SqlParameters.Create(42, "text", 1.25m, SqlValue.Null);
        ArrayBufferWriter<byte> buffer = new();
        TdsRequestWriter.WriteExecuteSql(
          buffer,
          new string('x', 5000),
          SqlParameters.Create(new byte[9000]),
          7);

        buffer.ResetWrittenCount();
        TdsRequestWriter.WriteExecuteSql(buffer, "SELECT @P1, @P2, @P3, @P4", parameters, 7);
        CollectionAssert.AreEqual(
          TdsRequestWriter.BuildExecuteSql("SELECT @P1, @P2, @P3, @P4", parameters, 7).ToArray(),
          buffer.WrittenSpan.ToArray());

        buffer.ResetWrittenCount();
        TdsRequestWriter.WriteExecute(buffer, 3, parameters, 7);
        CollectionAssert.AreEqual(
          TdsRequestWriter.BuildExecute(3, parameters, 7).ToArray(),
          buffer.WrittenSpan.ToArray());
    }

    [TestMethod]
    public void OperationNamesForCommonVerbsAreNotAllocated()
    {
        Assert.AreSame(MsSqlConnection.GetOperation("select 1"), MsSqlConnection.GetOperation("SELECT 2"));
        Assert.AreEqual("SELECT", MsSqlConnection.GetOperation("  select\n1"));
        Assert.AreEqual("INSERT", MsSqlConnection.GetOperation("Insert INTO t VALUES (1)"));
        Assert.AreEqual("EXEC", MsSqlConnection.GetOperation("exec sp_who"));
        Assert.AreEqual("WAITFOR", MsSqlConnection.GetOperation("waitfor delay '00:00:01'"));
        Assert.AreEqual("SELECT*FROM", MsSqlConnection.GetOperation("select*from t"));
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

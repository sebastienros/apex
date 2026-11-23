using System.Data;
using System.Data.Common;
using Apex.MsSqlClient;
using Apex.MySqlClient;
using Apex.PgClient;
using Apex.PgClient.Internal;

namespace Apex.SqlClient.AdoNet.Tests;

/// <summary>
/// Covers the adapter contract around explicit DbType, CommandTimeout, prepared commands across reopen,
/// column metadata, the native non-query path, and batch timeouts, against a scripted native connection.
/// </summary>
[TestClass]
public sealed class AdapterContractTests
{
    private enum DapperStatus { Active = 3 }

    [TestMethod]
    [DataRow("PostgreSql")]
    [DataRow("MySql")]
    [DataRow("SqlServer")]
    public async Task DapperStyleParametersWithExplicitDbTypeExecute(string provider)
    {
        // Dapper (and EF Core) set DbType on every parameter, including nulls; Dapper also sets Size.
        var server = new FakeServer();
        await using var connection = server.Connect(provider);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO t VALUES (@a, @b, @c, @d, @e, @f, @g, @h, @i, @j, @k, @l, @m, @n)";
        var id = Guid.NewGuid();
        var created = new DateTime(2026, 11, 22, 10, 30, 0, DateTimeKind.Utc);
        Add(command, "a", DbType.String, "text", size: 4000);
        Add(command, "b", DbType.Int32, 42);
        Add(command, "c", DbType.Int64, 42L);
        Add(command, "d", DbType.Guid, id);
        Add(command, "e", DbType.DateTime, created);
        Add(command, "f", DbType.Decimal, 12.5m);
        Add(command, "g", DbType.Boolean, true);
        Add(command, "h", DbType.Binary, new byte[] { 1, 2 }, size: 8000);
        Add(command, "i", DbType.Int32, DapperStatus.Active);
        Add(command, "j", DbType.StringFixedLength, 'x', size: 1);
        Add(command, "k", DbType.String, DBNull.Value);
        Add(command, "l", DbType.AnsiString, "ansi", size: 8000);
        Add(command, "m", DbType.Time, TimeSpan.FromHours(30));
        Add(command, "n", DbType.Double, 1.5);

        await command.ExecuteNonQueryAsync();

        var sent = server.Last.Parameters;
        Assert.AreEqual(14, sent.Count);
        AssertValue(sent[0], SqlValueKind.String, "text");
        AssertValue(sent[1], SqlValueKind.Int32, 42);
        AssertValue(sent[2], SqlValueKind.Int64, 42L);
        AssertValue(sent[3], SqlValueKind.Guid, id);
        AssertValue(sent[4], SqlValueKind.DateTime, created);
        AssertValue(sent[5], SqlValueKind.Decimal, 12.5m);
        AssertValue(sent[6], SqlValueKind.Boolean, true);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, sent[7].Get<byte[]>());
        AssertValue(sent[8], SqlValueKind.Int32, 3);
        AssertValue(sent[9], SqlValueKind.String, "x");
        Assert.IsTrue(sent[10].IsNull);
        AssertValue(sent[11], SqlValueKind.String, "ansi");
        Assert.AreEqual(TimeSpan.FromHours(30), sent[12].ToObject());
        AssertValue(sent[13], SqlValueKind.Double, 1.5);
    }

    [TestMethod]
    public void ExplicitDbTypeConvertsCompatibleValuesToTheDeclaredKind()
    {
        AssertValue(Convert(DbType.Int64, 7), SqlValueKind.Int64, 7L);
        AssertValue(Convert(DbType.Int16, (byte)7), SqlValueKind.Int16, (short)7);
        AssertValue(Convert(DbType.Double, 7), SqlValueKind.Double, 7d);
        AssertValue(Convert(DbType.Single, 1.5d), SqlValueKind.Single, 1.5f);
        AssertValue(Convert(DbType.Decimal, 3), SqlValueKind.Decimal, 3m);
        AssertValue(Convert(DbType.Currency, 2.25m), SqlValueKind.Decimal, 2.25m);
        AssertValue(Convert(DbType.Date, new DateTime(2026, 1, 2, 3, 4, 5)), SqlValueKind.DateOnly, new DateOnly(2026, 1, 2));
        AssertValue(Convert(DbType.DateTime2, new DateOnly(2026, 1, 2)), SqlValueKind.DateTime, new DateTime(2026, 1, 2));
        var utc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        AssertValue(Convert(DbType.DateTimeOffset, utc), SqlValueKind.DateTimeOffset, new DateTimeOffset(utc));
        AssertValue(Convert(DbType.Time, new TimeOnly(1, 2)), SqlValueKind.TimeOnly, new TimeOnly(1, 2));
        AssertValue(Convert(DbType.Xml, "<a/>"), SqlValueKind.String, "<a/>");
        AssertValue(Convert(DbType.String, new[] { 'a', 'b' }), SqlValueKind.String, "ab");
        Assert.AreEqual(SqlValueKind.ReadOnlyMemory, Convert(DbType.Binary, new Memory<byte>([1])).Kind);

        // DbTypes without a distinct native kind keep the (converted) value, as inference would.
        Assert.AreEqual((byte)7, Convert(DbType.Byte, 7).ToObject());
        Assert.AreEqual(7u, Convert(DbType.UInt32, 7).ToObject());
        AssertValue(Convert(DbType.Object, 7), SqlValueKind.Int32, 7);

        var parameter = new PgDbParameter { DbType = DbType.Int32, Value = null };
        Assert.IsTrue(parameter.ToSqlValue().IsNull);
        parameter.ResetDbType();
        Assert.AreEqual(DbType.Object, parameter.DbType);
    }

    [TestMethod]
    public void ExplicitDbTypeRejectsValuesItCannotRepresent()
    {
        Assert.ThrowsExactly<InvalidCastException>(() => Convert(DbType.Int32, "42"));
        Assert.ThrowsExactly<InvalidCastException>(() => Convert(DbType.Int32, 1.5));
        Assert.ThrowsExactly<InvalidCastException>(() => Convert(DbType.Guid, Guid.Empty.ToString()));
        Assert.ThrowsExactly<InvalidCastException>(() => Convert(DbType.Boolean, 1));
        Assert.ThrowsExactly<InvalidCastException>(() => Convert(DbType.String, 42));
        Assert.ThrowsExactly<InvalidCastException>(
            () => Convert(DbType.DateTimeOffset, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Unspecified)));
        Assert.ThrowsExactly<OverflowException>(() => Convert(DbType.Int16, 100_000));
        var error = Assert.ThrowsExactly<InvalidCastException>(
            () => new PgDbParameter { ParameterName = "p", DbType = DbType.Int32, Value = "x" }.ToSqlValue());
        StringAssert.Contains(error.Message, "'p'");
    }

    [TestMethod]
    [DataRow("PostgreSql")]
    [DataRow("MySql")]
    [DataRow("SqlServer")]
    public async Task CommandTimeoutSurfacesAsTransientTimeoutDbException(string provider)
    {
        var server = new FakeServer { HangOn = Stage.Execute };
        await using var connection = server.Connect(provider);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT slow";
        command.CommandTimeout = 1;

        AssertTimeout(await Assert.ThrowsExactlyAsync<ApexDbException>(() => command.ExecuteReaderAsync()));
        AssertTimeout(await Assert.ThrowsExactlyAsync<ApexDbException>(() => command.ExecuteScalarAsync()));
        AssertTimeout(await Assert.ThrowsExactlyAsync<ApexDbException>(() => command.ExecuteNonQueryAsync()));
    }

    [TestMethod]
    public async Task CommandTimeoutIsRearmedForReadsAndNextResultAndCoversNativeErrorsFromTheCancel()
    {
        var server = new FakeServer();
        await using var connection = server.Connect("PostgreSql");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT slow";
        command.CommandTimeout = 1;

        await using (var reader = await command.ExecuteReaderAsync())
        {
            // The user may take longer than the timeout between reads; only each read is bounded, so the
            // native read after the pause still sees an uncancelled operation.
            await Task.Delay(1200);
            Assert.IsTrue(await reader.ReadAsync());
            Assert.IsFalse(await reader.ReadAsync());
            server.HangOn = Stage.Read;
            AssertTimeout(await Assert.ThrowsExactlyAsync<ApexDbException>(() => reader.ReadAsync()));
        }

        server.HangOn = Stage.NextResult;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            AssertTimeout(await Assert.ThrowsExactlyAsync<ApexDbException>(() => reader.NextResultAsync()));
        }

        // A server error for the cancel the timeout sent (MySQL's "query interrupted") is still a timeout.
        server.HangOn = Stage.Execute;
        server.CancelError = new SqlClientException("Query execution was interrupted.");
        var timeout = await Assert.ThrowsExactlyAsync<ApexDbException>(() => command.ExecuteReaderAsync());
        AssertTimeout(timeout);
        Assert.AreSame(server.CancelError, timeout.InnerException!.InnerException);

        // The same during reader initialization, which runs under the operation's own token.
        server.HangOn = Stage.Read;
        timeout = await Assert.ThrowsExactlyAsync<ApexDbException>(() => command.ExecuteReaderAsync());
        AssertTimeout(timeout);
        Assert.AreSame(server.CancelError, timeout.InnerException!.InnerException);

        server.CancelError = null;
        server.HangOn = Stage.Prepare;
        AssertTimeout(await Assert.ThrowsExactlyAsync<ApexDbException>(() => command.PrepareAsync()));
    }

    [TestMethod]
    public async Task CallerCancellationAndCancelStayOperationCanceled()
    {
        var server = new FakeServer { HangOn = Stage.Execute };
        await using var connection = server.Connect("PostgreSql");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT slow";
        command.CommandTimeout = 1;

        using (var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => command.ExecuteReaderAsync(caller.Token));
        }

        await server.Hanging.WaitAsync(TimeSpan.FromSeconds(5));

        var execution = command.ExecuteReaderAsync();
        await server.Hanging.WaitAsync(TimeSpan.FromSeconds(5));
        command.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => execution);

        // Timeouts too long for a timer (int.MaxValue is a common "infinite") never fire.
        server.HangOn = Stage.None;
        command.CommandTimeout = int.MaxValue;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.IsTrue(await reader.ReadAsync());
        }

        server.HangOn = Stage.Execute;

        // Without a timeout nothing is translated, even if the operation runs long.
        command.CommandTimeout = 0;
        using var late = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
        await Assert.ThrowsAsync<OperationCanceledException>(() => command.ExecuteReaderAsync(late.Token));
    }

    [TestMethod]
    [DataRow("PostgreSql")]
    [DataRow("MySql")]
    [DataRow("SqlServer")]
    public async Task PreparedCommandSurvivesCloseAndReopen(string provider)
    {
        var server = new FakeServer();
        await using var connection = server.Connect(provider);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT prepared";
        await command.PrepareAsync();
        var first = server.Connections[0];
        Assert.HasCount(1, first.Statements);

        // Closing releases the statement on its own connection (a pooled lease would stay pinned otherwise).
        await connection.CloseAsync();
        Assert.IsTrue(first.Statements[0].Disposed);
        await connection.OpenAsync();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.IsTrue(await reader.ReadAsync());
        }

        // The command ran unprepared on the new connection, which stays open.
        Assert.AreEqual("reader", server.Last.Kind);
        Assert.AreSame(server.Connections[1], server.Last.Connection);
        Assert.AreEqual(ConnectionState.Open, connection.State);

        await command.PrepareAsync();
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
        Assert.AreEqual(ConnectionState.Open, connection.State);
        Assert.IsTrue(server.Last.Kind.StartsWith("prepared", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CloseReleasesPreparedStatementsEvenWhenTheReleaseFails()
    {
        var server = new FakeServer();
        await using var connection = server.Connect("PostgreSql");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT prepared";
        await command.PrepareAsync();
        server.Connections[0].Statements[0].DisposeError = new ObjectDisposedException("connection");
        await connection.CloseAsync();
        await connection.OpenAsync();

        await command.DisposeAsync();
        Assert.AreEqual(ConnectionState.Open, connection.State);

        var syncCommand = connection.CreateCommand();
        syncCommand.CommandText = "SELECT prepared";
        await syncCommand.PrepareAsync();
        server.Connections[1].Statements[0].DisposeError = new ObjectDisposedException("connection");
        await connection.CloseAsync();
        syncCommand.Dispose();
        Assert.AreEqual(ConnectionState.Closed, connection.State);
    }

    [TestMethod]
    public async Task PostgreSqlNonQueryUsesNativeExecuteForSingleStatementsThatReturnNoRows()
    {
        var server = new FakeServer();
        await using var connection = server.Connect("PostgreSql");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = "/* c */ insert into t values ($1)";
        command.Parameters.Add(new PgDbParameter { Value = 1 });
        server.CommandTag = "INSERT 0 3";
        Assert.AreEqual(3, await command.ExecuteNonQueryAsync());
        Assert.AreEqual("execute", server.Last.Kind);

        command.Parameters.Clear();
        command.CommandText = "CREATE TABLE t (id int)";
        server.CommandTag = "CREATE TABLE";
        Assert.AreEqual(-1, await command.ExecuteNonQueryAsync());
        Assert.AreEqual("execute", server.Last.Kind);

        // Row-returning and multi-statement text keep the reader, whose RecordsAffected sums statements.
        foreach (var sql in new[]
        {
            "SELECT 1",
            "WITH x AS (SELECT 1) SELECT * FROM x",
            "INSERT INTO t VALUES (1); INSERT INTO t VALUES (2)",
            "DELETE FROM t RETURNING *",
        })
        {
            command.CommandText = sql;
            server.RecordsAffected = 2;
            Assert.AreEqual(2, await command.ExecuteNonQueryAsync(), sql);
            Assert.AreEqual("reader", server.Last.Kind, sql);
        }

        command.CommandText = "UPDATE t SET id = 2";
        await command.PrepareAsync();
        server.CommandTag = "UPDATE 4";
        Assert.AreEqual(4, await command.ExecuteNonQueryAsync());
        Assert.AreEqual("prepared-execute", server.Last.Kind);
    }

    [TestMethod]
    [DataRow("MySql")]
    [DataRow("SqlServer")]
    public async Task OtherProvidersKeepTheReaderForNonQueries(string provider)
    {
        var server = new FakeServer { RecordsAffected = 5 };
        await using var connection = server.Connect(provider);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO t VALUES (1)";
        Assert.AreEqual(5, await command.ExecuteNonQueryAsync());
        Assert.AreEqual("reader", server.Last.Kind);
    }

    [TestMethod]
    [DataRow("NonQuery")]
    [DataRow("Scalar")]
    [DataRow("Reader")]
    public async Task BatchTimeoutBoundsEachCommandForEveryExecutionMethod(string method)
    {
        var server = new FakeServer { ExecuteDelay = TimeSpan.FromMilliseconds(350) };
        await using var connection = server.Connect("PostgreSql");
        await connection.OpenAsync();
        await using var batch = connection.CreateBatch();
        batch.Timeout = 1;
        for (var i = 0; i < 4; i++)
        {
            var batchCommand = batch.CreateBatchCommand();
            batchCommand.CommandText = $"SELECT {i}";
            batch.BatchCommands.Add(batchCommand);
        }

        // Four commands take ~1.4 s in total, more than Timeout, but each finishes well within it.
        await ExecuteBatchAsync(batch, method);

        server.ExecuteDelay = TimeSpan.Zero;
        server.HangOn = Stage.Execute;
        server.HangAfter = 2;
        AssertTimeout(await Assert.ThrowsExactlyAsync<ApexDbException>(() => ExecuteBatchAsync(batch, method)));
    }

    [TestMethod]
    public async Task ReadersReportProviderFieldTypesAndSchema()
    {
        var server = new FakeServer
        {
            Columns =
            [
                new("id", 23, 4, -1, SqlDataFormat.Binary),
                new("amount", 1700, -1, -1, SqlDataFormat.Binary),
                new("tags", 1009, -1, -1, SqlDataFormat.Binary),
                new("other", 3904, -1, -1, SqlDataFormat.Text),
                new("custom", 99999, -1, -1, SqlDataFormat.Binary),
            ],
        };
        await using var connection = server.Connect("PostgreSql");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT columns";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.AreEqual(typeof(int), reader.GetFieldType(0));
            Assert.AreEqual("integer", reader.GetDataTypeName(0));
            Assert.AreEqual(typeof(PgNumeric), reader.GetFieldType(1));
            Assert.AreEqual("numeric", reader.GetDataTypeName(1));
            Assert.AreEqual(typeof(string[]), reader.GetFieldType(2));
            Assert.AreEqual("text[]", reader.GetDataTypeName(2));
            Assert.AreEqual(typeof(string), reader.GetFieldType(3));
            Assert.AreEqual("int4range", reader.GetDataTypeName(3));
            Assert.AreEqual(typeof(object), reader.GetFieldType(4));
            Assert.AreEqual("99999", reader.GetDataTypeName(4));

            Assert.IsTrue(reader.CanGetColumnSchema());
            var schema = reader.GetColumnSchema();
            Assert.HasCount(5, schema);
            Assert.AreEqual("amount", schema[1].ColumnName);
            Assert.AreEqual(1, schema[1].ColumnOrdinal);
            Assert.AreEqual(typeof(PgNumeric), schema[1].DataType);
            Assert.AreEqual("numeric", schema[1].DataTypeName);

            var table = reader.GetSchemaTable()!;
            Assert.HasCount(5, table.Rows);
            Assert.AreEqual("tags", table.Rows[2][SchemaTableColumn.ColumnName]);
            Assert.AreEqual(2, table.Rows[2][SchemaTableColumn.ColumnOrdinal]);
            Assert.AreEqual(typeof(string[]), table.Rows[2][SchemaTableColumn.DataType]);
            Assert.AreEqual(DBNull.Value, table.Rows[2][SchemaTableColumn.AllowDBNull]);
        }

        await using var batch = connection.CreateBatch();
        var batchCommand = batch.CreateBatchCommand();
        batchCommand.CommandText = "SELECT columns";
        batch.BatchCommands.Add(batchCommand);
        await using (var reader = await batch.ExecuteReaderAsync())
        {
            Assert.AreEqual(typeof(PgNumeric), reader.GetFieldType(1));
            Assert.AreEqual("numeric", reader.GetColumnSchema()[1].DataTypeName);
            Assert.HasCount(5, reader.GetSchemaTable()!.Rows);
        }

        await using var standalone = new ApexDbDataReader(
            new FakeReader(new FakeConnection(server), server.Columns, 0, 0), CommandBehavior.Default, null);
        Assert.AreEqual(typeof(object), standalone.GetFieldType(0));
        Assert.AreEqual("23", standalone.GetDataTypeName(0));

        server.Columns = [];
        await using (var reader = await command.ExecuteReaderAsync())
        {
            // A result without columns has no schema table, as with other providers.
            Assert.IsEmpty(reader.GetColumnSchema());
            Assert.IsNull(reader.GetSchemaTable());
            await reader.CloseAsync();
            Assert.ThrowsExactly<InvalidOperationException>(() => reader.GetColumnSchema());
        }
    }

    [TestMethod]
    public void MySqlAndSqlServerColumnMapsFollowTheNativeObjectDecoders()
    {
        static SqlColumn MySql(MySqlType type, MySqlColumnFlags flags = MySqlColumnFlags.None, int charset = 255) =>
            new("c", (byte)type, unchecked((short)charset), (ushort)flags, SqlDataFormat.Text);
        Assert.AreEqual(typeof(sbyte), MySqlAdoColumnTypes.GetFieldType(MySql(MySqlType.Tiny)));
        Assert.AreEqual(typeof(byte), MySqlAdoColumnTypes.GetFieldType(MySql(MySqlType.Tiny, MySqlColumnFlags.Unsigned)));
        Assert.AreEqual("TINYINT UNSIGNED", MySqlAdoColumnTypes.GetDataTypeName(MySql(MySqlType.Tiny, MySqlColumnFlags.Unsigned)));
        Assert.AreEqual(typeof(ulong), MySqlAdoColumnTypes.GetFieldType(MySql(MySqlType.LongLong, MySqlColumnFlags.Unsigned)));
        Assert.AreEqual(typeof(TimeSpan), MySqlAdoColumnTypes.GetFieldType(MySql(MySqlType.Time)));
        Assert.AreEqual(typeof(MySqlDecimal), MySqlAdoColumnTypes.GetFieldType(MySql(MySqlType.NewDecimal, charset: 63)));
        Assert.AreEqual(typeof(string), MySqlAdoColumnTypes.GetFieldType(MySql(MySqlType.VarString)));
        Assert.AreEqual("VARCHAR", MySqlAdoColumnTypes.GetDataTypeName(MySql(MySqlType.VarString)));
        Assert.AreEqual(typeof(byte[]), MySqlAdoColumnTypes.GetFieldType(MySql(MySqlType.Blob, charset: 63)));
        Assert.AreEqual("BLOB", MySqlAdoColumnTypes.GetDataTypeName(MySql(MySqlType.Blob, charset: 63)));
        Assert.AreEqual("TEXT", MySqlAdoColumnTypes.GetDataTypeName(MySql(MySqlType.Blob)));

        static SqlColumn Tds(byte type, short size = 0) => new("c", type, size, 0, SqlDataFormat.Binary);
        Assert.AreEqual(typeof(short), MsSqlAdoColumnTypes.GetFieldType(Tds(0x26, 2)));
        Assert.AreEqual("smallint", MsSqlAdoColumnTypes.GetDataTypeName(Tds(0x26, 2)));
        Assert.AreEqual(typeof(long), MsSqlAdoColumnTypes.GetFieldType(Tds(0x26, 8)));
        Assert.AreEqual(typeof(float), MsSqlAdoColumnTypes.GetFieldType(Tds(0x6D, 4)));
        Assert.AreEqual(typeof(double), MsSqlAdoColumnTypes.GetFieldType(Tds(0x6D, 8)));
        Assert.AreEqual(typeof(decimal), MsSqlAdoColumnTypes.GetFieldType(Tds(0x6E, 8)));
        Assert.AreEqual(typeof(string), MsSqlAdoColumnTypes.GetFieldType(Tds(0xE7, 100)));
        Assert.AreEqual("nvarchar", MsSqlAdoColumnTypes.GetDataTypeName(Tds(0xE7, 100)));
        Assert.AreEqual(typeof(DateOnly), MsSqlAdoColumnTypes.GetFieldType(Tds(0x28)));
        Assert.AreEqual(typeof(byte[]), MsSqlAdoColumnTypes.GetFieldType(Tds(0xA5, 16)));
        Assert.AreEqual("uniqueidentifier", MsSqlAdoColumnTypes.GetDataTypeName(Tds(0x24, 16)));
    }

    [TestMethod]
    public void PostgreSqlLexerFindsTopLevelStatementBoundaries()
    {
        foreach (var single in new[]
        {
            "SELECT 1",
            "SELECT 1;",
            " SELECT 1 ;  -- trailing comment ; ",
            "SELECT ';'",
            "SELECT 'it''s; fine'",
            "SELECT E'\\'; still a string'",
            "SELECT \"semi;colon\" FROM t",
            "SELECT $$;$$",
            "SELECT $tag$ ; $$ ; $tag$",
            "SELECT $1, foo$bar; ",
            "SELECT /* outer /* nested ; */ still ; */ 1",
            "SELECT 1 -- ; comment\n",
            ";;SELECT 1;;",
        })
        {
            Assert.IsTrue(PgSqlText.IsSingleStatement(single), single);
            Assert.IsFalse(PgSqlText.HasMultipleStatements(single), single);
        }

        foreach (var multiple in new[]
        {
            "SELECT 1; SELECT 2",
            "SELECT ';'; SELECT 2",
            "SELECT $$x$$; SELECT 2",
            "SELECT 1 /* c */; SELECT 2",
            "SELECT 1 -- c\n; SELECT 2",
            "SELECT e'\\\\'; SELECT 2",
        })
        {
            Assert.IsTrue(PgSqlText.HasMultipleStatements(multiple), multiple);
            Assert.IsFalse(PgSqlText.IsSingleStatement(multiple), multiple);
        }

        Assert.IsFalse(PgSqlText.IsSingleStatement("  -- only a comment"));
        Assert.AreEqual("insert", PgSqlText.GetLeadingKeyword(" /* a */ -- b\n insert into t").ToString());
        Assert.AreEqual(string.Empty, PgSqlText.GetLeadingKeyword("(SELECT 1)").ToString());
    }

    private static async Task ExecuteBatchAsync(DbBatch batch, string method)
    {
        switch (method)
        {
            case "NonQuery":
                await batch.ExecuteNonQueryAsync();
                break;
            case "Scalar":
                await batch.ExecuteScalarAsync();
                break;
            default:
                await using (var reader = await batch.ExecuteReaderAsync())
                {
                    do
                    {
                        while (await reader.ReadAsync())
                        {
                        }
                    }
                    while (await reader.NextResultAsync());
                }

                break;
        }
    }

    private static SqlValue Convert(DbType dbType, object value) =>
        ApexDbTypeConverter.ToSqlValue(dbType, value, "p");

    private static void Add(DbCommand command, string name, DbType dbType, object value, int size = 0)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = dbType;
        parameter.Value = value;
        parameter.Size = size;
        command.Parameters.Add(parameter);
    }

    private static void AssertValue(SqlValue value, SqlValueKind kind, object expected)
    {
        Assert.AreEqual(kind, value.Kind);
        Assert.AreEqual(expected, value.ToObject());
    }

    private static void AssertTimeout(ApexDbException error)
    {
        Assert.IsInstanceOfType<TimeoutException>(error.InnerException);
        Assert.IsTrue(error.IsTransient);
        StringAssert.Contains(error.Message, "timed out");
    }

    private enum Stage { None, Execute, Prepare, Read, NextResult }

    private sealed record Execution(string Kind, FakeConnection Connection, SqlParameters Parameters);

    private sealed class FakeServer
    {
        public List<FakeConnection> Connections { get; } = [];
        public Execution Last { get; set; } = null!;
        public Stage HangOn { get; set; }
        public int HangAfter { get; set; }
        public TimeSpan ExecuteDelay { get; set; }
        public SqlClientException? CancelError { get; set; }
        public string CommandTag { get; set; } = "INSERT 0 1";
        public int RecordsAffected { get; set; } = 1;
        public SqlColumn[] Columns { get; set; } = [new("value", 23, 4, -1, SqlDataFormat.Binary)];
        public SemaphoreSlim Hanging { get; } = new(0);
        private int _executions;

        public DbConnection Connect(string provider)
        {
            ValueTask<ISqlConnection> Open(CancellationToken _)
            {
                var connection = new FakeConnection(this);
                Connections.Add(connection);
                return ValueTask.FromResult<ISqlConnection>(connection);
            }

            return provider switch
            {
                "PostgreSql" => new PgDbConnection("Host=localhost;Database=test", Open),
                "MySql" => new MySqlDbConnection("Server=localhost;Database=test", Open),
                "SqlServer" => new MsSqlDbConnection("Server=localhost;Database=test", Open),
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            };
        }

        public async Task StageAsync(Stage stage, CancellationToken cancellationToken, CancellationToken operationToken = default)
        {
            // Native readers observe the token they were executed with as well as each call's token.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationToken);
            cancellationToken = linked.Token;
            cancellationToken.ThrowIfCancellationRequested();
            if (stage == Stage.Execute)
            {
                if (ExecuteDelay > TimeSpan.Zero) await Task.Delay(ExecuteDelay, CancellationToken.None);
                if (HangOn == Stage.Execute && ++_executions <= HangAfter) return;
            }

            if (HangOn != stage)
            {
                await Task.Yield();
                return;
            }

            Hanging.Release();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException) when (CancelError is not null)
            {
                throw CancelError;
            }
        }
    }

    private sealed class FakeConnection(FakeServer server) : ISqlConnection, ISqlResultReaderConnection
    {
        public List<FakeStatement> Statements { get; } = [];
        public FakeServer Server => server;
        public bool IsSecure => false;
        public DatabaseMetadata DatabaseMetadata => new("fake", "1", 1, 0);

        public async ValueTask<ISqlPreparedStatement> PrepareAsync(string sql, CancellationToken cancellationToken = default)
        {
            await server.StageAsync(Stage.Prepare, cancellationToken);
            var statement = new FakeStatement(this);
            Statements.Add(statement);
            return statement;
        }

        public async ValueTask<ISqlRowReader> ExecuteResultReaderAsync(
            string sql, SqlParameters parameters, CancellationToken cancellationToken)
        {
            server.Last = new("reader", this, parameters);
            await server.StageAsync(Stage.Execute, cancellationToken);
            return new FakeReader(this, server.Columns, rows: 1, server.RecordsAffected, cancellationToken);
        }

        public async ValueTask<SqlCommandResult> ExecuteAsync(
            string sql, SqlParameters parameters, CancellationToken cancellationToken = default)
        {
            server.Last = new("execute", this, parameters);
            await server.StageAsync(Stage.Execute, cancellationToken);
            return new SqlCommandResult(ParseCount(server.CommandTag), server.CommandTag);
        }

        public static long ParseCount(string tag) =>
            long.TryParse(tag.AsSpan(tag.LastIndexOf(' ') + 1), out var count) ? count : 0;

        public ValueTask<SqlCommandResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default) =>
            ExecuteAsync(sql, SqlParameters.Empty, cancellationToken);
        public ValueTask<ISqlRowReader> ExecuteReaderAsync(
            string sql, SqlParameters parameters = default, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Adapters use the result reader capability.");
        public ValueTask<ISqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<SqlRowSet> QueryAsync(string sql, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<SqlRowSet> QueryAsync(
            string sql, SqlParameters parameters, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<SqlRow> StreamAsync(
            string sql, SqlParameters parameters = default, int fetchSize = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeStatement(FakeConnection connection) : ISqlPreparedStatement, ISqlResultPreparedStatement
    {
        public bool Disposed { get; private set; }
        public Exception? DisposeError { get; set; }
        public string Sql => "prepared";

        public async ValueTask<ISqlRowReader> ExecuteResultReaderAsync(
            SqlParameters parameters, CancellationToken cancellationToken)
        {
            connection.Server.Last = new("prepared-reader", connection, parameters);
            await connection.Server.StageAsync(Stage.Execute, cancellationToken);
            return new FakeReader(
                connection, connection.Server.Columns, rows: 1, connection.Server.RecordsAffected, cancellationToken);
        }

        public async ValueTask<SqlCommandResult> ExecuteAsync(
            SqlParameters parameters = default, CancellationToken cancellationToken = default)
        {
            connection.Server.Last = new("prepared-execute", connection, parameters);
            await connection.Server.StageAsync(Stage.Execute, cancellationToken);
            var tag = connection.Server.CommandTag;
            return new SqlCommandResult(FakeConnection.ParseCount(tag), tag);
        }

        public ValueTask<ISqlRowReader> ExecuteReaderAsync(
            SqlParameters parameters = default, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Adapters use the result reader capability.");
        public ValueTask<SqlRowSet> QueryAsync(
            SqlParameters parameters = default, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<IReadOnlyList<SqlCommandResult>> ExecuteBatchAsync(
            IReadOnlyList<SqlParameters> batch, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<ISqlCursor> OpenCursorAsync(
            SqlParameters parameters = default, int fetchSize = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<SqlRow> StreamAsync(
            SqlParameters parameters = default, int fetchSize = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return DisposeError is { } error ? ValueTask.FromException(error) : ValueTask.CompletedTask;
        }
    }

    private sealed class FakeReader(
        FakeConnection connection,
        IReadOnlyList<SqlColumn> columns,
        int rows,
        int recordsAffected,
        CancellationToken operationToken = default)
        : ISqlResultBoundaryReader, ISqlRecordsAffectedReader
    {
        private int _remaining = rows;
        private bool _nextResultReturned;

        public IReadOnlyList<SqlColumn> Columns => columns;
        public int FieldCount => columns.Count;
        public int RecordsAffected => recordsAffected;

        public ValueTask<bool> InitializeAsync(CancellationToken cancellationToken = default) =>
            ReadAsync(cancellationToken);

        public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
        {
            await connection.Server.StageAsync(Stage.Read, cancellationToken, operationToken);
            return _remaining-- > 0;
        }

        public async ValueTask<bool> NextResultAsync(CancellationToken cancellationToken = default)
        {
            await connection.Server.StageAsync(Stage.NextResult, cancellationToken, operationToken);
            if (_nextResultReturned) return false;
            _nextResultReturned = true;
            return false;
        }

        public T Get<T>(int ordinal) => (T)(object)ordinal;
        public int GetOrdinal(string name) => 0;
        public bool IsNull(int ordinal) => false;
        public bool GetBoolean(int ordinal) => Get<bool>(ordinal);
        public short GetInt16(int ordinal) => Get<short>(ordinal);
        public int GetInt32(int ordinal) => ordinal;
        public long GetInt64(int ordinal) => Get<long>(ordinal);
        public float GetFloat(int ordinal) => Get<float>(ordinal);
        public double GetDouble(int ordinal) => Get<double>(ordinal);
        public string GetString(int ordinal) => Get<string>(ordinal);
        public Guid GetGuid(int ordinal) => Get<Guid>(ordinal);
        public DateOnly GetDateOnly(int ordinal) => Get<DateOnly>(ordinal);
        public TimeOnly GetTimeOnly(int ordinal) => Get<TimeOnly>(ordinal);
        public DateTime GetDateTime(int ordinal) => Get<DateTime>(ordinal);
        public DateTimeOffset GetDateTimeOffset(int ordinal) => Get<DateTimeOffset>(ordinal);
        public byte[] GetBytes(int ordinal) => Get<byte[]>(ordinal);
        public TElement[]? GetArray<TElement>(int ordinal) => Get<TElement[]>(ordinal);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

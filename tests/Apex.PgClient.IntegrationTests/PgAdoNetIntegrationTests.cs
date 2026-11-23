using System.Data;
using System.Data.Common;
using Apex.SqlClient;
using Testcontainers.PostgreSql;

namespace Apex.PgClient.IntegrationTests;

/// <summary>Exercises the asynchronous ADO.NET adapter against a real PostgreSQL server.</summary>
[TestClass]
public sealed class PgAdoNetIntegrationTests
{
    private static PostgreSqlContainer s_container = null!;

    [ClassInitialize]
    public static async Task StartPostgreSqlAsync(TestContext _)
    {
        var image = Environment.GetEnvironmentVariable("POSTGRES_IMAGE") ?? "postgres:16-alpine";
        s_container = new PostgreSqlBuilder(image)
            .WithDatabase("db")
            .WithUsername("user")
            .WithPassword("pass")
            .Build();
        await s_container.StartAsync();
    }

    [ClassCleanup]
    public static async Task StopPostgreSqlAsync() => await s_container.DisposeAsync();

    private static string ConnectionString =>
        $"Host={s_container.Hostname};Port={s_container.GetMappedPublicPort(5432)};" +
        "Database=db;Username=user;Password=pass;SslMode=Disable";

    private static async Task<PgDbConnection> OpenAsync()
    {
        var connection = new PgDbConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    [TestMethod]
    public async Task MultiStatementCommandTextReturnsEveryResultAndSumsRecordsAffected()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TEMP TABLE multi (id int, name text); " +
            "INSERT INTO multi VALUES (1, 'a;b'), (2, $$c;d$$); UPDATE multi SET name = name || '!' WHERE id = 2";
        // CREATE reports no count; the INSERT and UPDATE do.
        Assert.AreEqual(3, await command.ExecuteNonQueryAsync());

        command.CommandText = "SELECT id, name FROM multi ORDER BY id; -- second result\n" +
            "SELECT 42::int4 AS answer, 12.5::numeric AS amount, true AS flag, " +
            "'2026-11-22 10:30:00+00'::timestamptz AS at, '7d2b1c1e-5d3b-4f8a-9f3b-0a1b2c3d4e5f'::uuid AS id; " +
            "DELETE FROM multi WHERE id = 1";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(1, reader.GetInt32(0));
            Assert.AreEqual("a;b", reader.GetString(1));
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual("c;d!", reader.GetString(1));
            Assert.IsFalse(await reader.ReadAsync());

            Assert.IsTrue(await reader.NextResultAsync());
            Assert.AreEqual("answer", reader.GetName(0));
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(42, reader.GetInt32(0));
            Assert.AreEqual(12.5m, reader.GetDecimal(1));
            Assert.IsTrue(reader.GetBoolean(2));
            Assert.AreEqual(
                new DateTimeOffset(2026, 11, 22, 10, 30, 0, TimeSpan.Zero),
                reader.GetFieldValue<DateTimeOffset>(3));
            Assert.AreEqual(Guid.Parse("7d2b1c1e-5d3b-4f8a-9f3b-0a1b2c3d4e5f"), reader.GetGuid(4));

            Assert.IsTrue(await reader.NextResultAsync());
            Assert.AreEqual(0, reader.FieldCount);
            Assert.IsFalse(await reader.NextResultAsync());
            Assert.AreEqual(1, reader.RecordsAffected);
        }

        // A failure part-way stops the batch and leaves the connection usable.
        command.CommandText = "SELECT 1; SELECT 1 / 0; SELECT 3";
        var error = await Assert.ThrowsExactlyAsync<ApexDbException>(async () =>
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.NextResultAsync())
            {
            }
        });
        Assert.AreEqual("22012", error.SqlState);
        command.CommandText = "SELECT 7";
        Assert.AreEqual(7, await command.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task NonQueryReportsAffectedRowsLikeTheReader()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TEMP TABLE counted (id int)";
        Assert.AreEqual(-1, await command.ExecuteNonQueryAsync());

        command.CommandText = "INSERT INTO counted SELECT generate_series(1, $1)";
        command.Parameters.Add(new PgDbParameter { Value = 5 });
        Assert.AreEqual(5, await command.ExecuteNonQueryAsync());
        await command.PrepareAsync();
        Assert.AreEqual(5, await command.ExecuteNonQueryAsync());

        command.Parameters.Clear();
        command.CommandText = "UPDATE counted SET id = id + 1 WHERE id > 3";
        Assert.AreEqual(4, await command.ExecuteNonQueryAsync());
        command.CommandText = "SELECT * FROM counted";
        Assert.AreEqual(-1, await command.ExecuteNonQueryAsync());
        command.CommandText = "DELETE FROM counted";
        Assert.AreEqual(10, await command.ExecuteNonQueryAsync());
    }

    [TestMethod]
    public async Task FieldTypesMatchTheValuesReturnedInBothProtocols()
    {
        const string select =
            "SELECT 1::int2, 2::int4, 3::int8, 1.5::float4, 2.5::float8, 3.5::numeric, 4.5::money, " +
            "true, 'text'::text, 'v'::varchar, 'c'::char(1), '\\x0102'::bytea, '2026-11-22'::date, " +
            "'10:30'::time, '2026-11-22 10:30'::timestamp, '2026-11-22 10:30+00'::timestamptz, " +
            "'1 day'::interval, '10:30+02'::timetz, gen_random_uuid(), '{\"a\":1}'::json, '{\"a\":1}'::jsonb, " +
            "'(1,2)'::point, '192.168.0.1'::inet, '10.0.0.0/8'::cidr, '08:00:2b:01:02:03'::macaddr, " +
            "B'101'::bit(3), ARRAY[1, 2]::int4[], ARRAY['a']::text[], ARRAY[1.5]::numeric[]";
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();

        // A single statement uses the extended protocol; adding a statement switches to the simple one.
        foreach (var sql in new[] { select, select + "; SELECT 1" })
        {
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            var schema = reader.GetColumnSchema();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                var name = reader.GetDataTypeName(i);
                Assert.AreEqual(value.GetType(), reader.GetFieldType(i), $"{sql == select}: column {i} ({name})");
                Assert.AreEqual(reader.GetFieldType(i), schema[i].DataType);
                Assert.IsFalse(int.TryParse(name, out _), $"column {i} has no type name: {name}");
            }

            Assert.AreEqual("integer", reader.GetDataTypeName(1));
            Assert.AreEqual("numeric", reader.GetDataTypeName(5));
            Assert.AreEqual("timestamp with time zone", reader.GetDataTypeName(15));
            Assert.AreEqual("integer[]", reader.GetDataTypeName(26));
            var table = reader.GetSchemaTable()!;
            Assert.AreEqual(reader.FieldCount, table.Rows.Count);
            Assert.AreEqual(typeof(long), table.Rows[2][SchemaTableColumn.DataType]);
        }
    }

    [TestMethod]
    public async Task DapperStyleExplicitDbTypesRoundTrip()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TEMP TABLE typed (s text, i int4, l int8, g uuid, d timestamp, " +
            "m numeric, b bool, x bytea, e int4, c text, n text, dt date, f float8)";
        await command.ExecuteNonQueryAsync();

        var id = Guid.NewGuid();
        command.CommandText = "INSERT INTO typed VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)";
        Add(command, DbType.String, "text", 4000);
        Add(command, DbType.Int32, 42);
        Add(command, DbType.Int64, 7); // widened to int8
        Add(command, DbType.Guid, id);
        Add(command, DbType.DateTime, new DateTime(2026, 11, 22, 10, 30, 0));
        Add(command, DbType.Decimal, 12.5m);
        Add(command, DbType.Boolean, true);
        Add(command, DbType.Binary, new byte[] { 1, 2 }, 8000);
        Add(command, DbType.Int32, DayOfWeek.Friday);
        Add(command, DbType.StringFixedLength, 'x', 1);
        Add(command, DbType.String, DBNull.Value);
        Add(command, DbType.Date, new DateOnly(2026, 11, 22));
        Add(command, DbType.Double, 1.25);
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync());

        command.Parameters.Clear();
        command.CommandText = "SELECT * FROM typed";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual("text", reader.GetString(0));
        Assert.AreEqual(42, reader.GetInt32(1));
        Assert.AreEqual(7L, reader.GetInt64(2));
        Assert.AreEqual(id, reader.GetGuid(3));
        Assert.AreEqual(new DateTime(2026, 11, 22, 10, 30, 0), reader.GetDateTime(4));
        Assert.AreEqual(12.5m, reader.GetDecimal(5));
        Assert.IsTrue(reader.GetBoolean(6));
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, (byte[])reader.GetValue(7));
        Assert.AreEqual((int)DayOfWeek.Friday, reader.GetInt32(8));
        Assert.AreEqual("x", reader.GetString(9));
        Assert.IsTrue(reader.IsDBNull(10));
        Assert.AreEqual(new DateOnly(2026, 11, 22), reader.GetFieldValue<DateOnly>(11));
        Assert.AreEqual(1.25, reader.GetDouble(12));
    }

    [TestMethod]
    public async Task CommandTimeoutRaisesATransientTimeoutAndKeepsTheConnectionUsable()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "SELECT pg_sleep(10)";
        var error = await Assert.ThrowsExactlyAsync<ApexDbException>(() => command.ExecuteScalarAsync());
        Assert.IsInstanceOfType<TimeoutException>(error.InnerException);
        Assert.IsTrue(error.IsTransient);

        command.CommandText = "SELECT pg_sleep(10) WHERE $1";
        command.Parameters.Add(new PgDbParameter { Value = true });
        error = await Assert.ThrowsExactlyAsync<ApexDbException>(() => command.ExecuteNonQueryAsync());
        Assert.IsInstanceOfType<TimeoutException>(error.InnerException);

        command.Parameters.Clear();
        command.CommandText = "SELECT 1";
        Assert.AreEqual(1, await command.ExecuteScalarAsync());

        command.CommandTimeout = 30;
        command.CommandText = "SELECT pg_sleep(10)";
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<OperationCanceledException>(() => command.ExecuteScalarAsync(caller.Token));
    }

    [TestMethod]
    public async Task PreparedCommandOnAPooledConnectionSurvivesCloseAndReopen()
    {
        // With one pooled connection, a statement that kept the old lease pinned would block the reopen.
        await using var source = new PgDbDataSource(
            ConnectionString,
            new SqlPoolOptions { MaximumSize = 1, AcquisitionTimeout = TimeSpan.FromSeconds(5) });
        await using var connection = source.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT $1::int4 + 1";
        var parameter = command.CreateParameter();
        parameter.Value = 1;
        command.Parameters.Add(parameter);
        await command.PrepareAsync();
        Assert.AreEqual(2, await command.ExecuteScalarAsync());

        await connection.CloseAsync();
        await connection.OpenAsync();
        parameter.Value = 2;
        Assert.AreEqual(3, await command.ExecuteScalarAsync());
        await command.PrepareAsync();
        parameter.Value = 3;
        Assert.AreEqual(4, await command.ExecuteScalarAsync());

        await connection.CloseAsync();
        await using var other = await source.OpenConnectionAsync();
        await using var check = other.CreateCommand();
        check.CommandText = "SELECT 1";
        Assert.AreEqual(1, await check.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task BatchTimeoutBoundsEachCommand()
    {
        await using var connection = await OpenAsync();
        await using var batch = connection.CreateBatch();
        batch.Timeout = 2;
        foreach (var sql in new[] { "SELECT pg_sleep(0.8)", "SELECT pg_sleep(0.8)", "SELECT pg_sleep(0.8)" })
        {
            var batchCommand = batch.CreateBatchCommand();
            batchCommand.CommandText = sql;
            batch.BatchCommands.Add(batchCommand);
        }

        // 2.4 s in total, but each command stays within the 2 s timeout.
        await batch.ExecuteNonQueryAsync();

        batch.BatchCommands[1].CommandText = "SELECT pg_sleep(10)";
        var error = await Assert.ThrowsExactlyAsync<ApexDbException>(() => batch.ExecuteNonQueryAsync());
        Assert.IsInstanceOfType<TimeoutException>(error.InnerException);
    }

    private static void Add(DbCommand command, DbType dbType, object value, int size = 0)
    {
        var parameter = command.CreateParameter();
        parameter.DbType = dbType;
        parameter.Value = value;
        parameter.Size = size;
        command.Parameters.Add(parameter);
    }
}

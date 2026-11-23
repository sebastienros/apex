using System.Data;
using System.Data.Common;
using Apex.SqlClient;
using Testcontainers.MySql;

namespace Apex.MySqlClient.IntegrationTests;

/// <summary>Exercises the asynchronous ADO.NET adapter against a real MySQL server.</summary>
[TestClass]
public sealed class MySqlAdoNetIntegrationTests
{
    private static MySqlContainer s_container = null!;

    [ClassInitialize]
    public static async Task StartMySqlAsync(TestContext _) =>
        s_container = await MySqlContainerFixture.StartAsync();

    [ClassCleanup]
    public static async Task StopMySqlAsync() => await s_container.DisposeAsync();

    private static string ConnectionString
    {
        get
        {
            var options = MySqlContainerFixture.CreateOptions(s_container);
            return $"Server={options.Host};Port={options.Port};Database={options.Database};" +
                $"User ID={options.Username};Password={options.Password};AllowPublicKeyRetrieval=true;" +
                "AllowMultiStatements=true";
        }
    }

    private static async Task<MySqlDbConnection> OpenAsync()
    {
        var connection = new MySqlDbConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    [TestMethod]
    public async Task FieldTypesMatchTheValuesReturnedByTextAndBinaryProtocols()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TEMPORARY TABLE typed (ti TINYINT, tu TINYINT UNSIGNED, si SMALLINT, su SMALLINT UNSIGNED, " +
            "mi MEDIUMINT, i INT, iu INT UNSIGNED, bi BIGINT, bu BIGINT UNSIGNED, f FLOAT, d DOUBLE, " +
            "m DECIMAL(10,2), dt DATE, ts DATETIME(3), tm TIME, y YEAR, c CHAR(2), v VARCHAR(10), t TEXT, " +
            "b BLOB, vb VARBINARY(4), j JSON, bt BIT(4), e ENUM('a','b'))";
        await command.ExecuteNonQueryAsync();
        command.CommandText =
            "INSERT INTO typed VALUES (-1, 1, -2, 2, 3, 4, 5, 6, 7, 1.5, 2.5, 3.25, '2026-11-22', " +
            "'2026-11-22 10:30:00.123', '10:30:00', 2026, 'ab', 'text', 'long', x'0102', x'03', '{\"a\":1}', b'1010', 'b')";
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync());

        command.CommandText = "SELECT * FROM typed";
        foreach (var prepared in new[] { false, true })
        {
            if (prepared) await command.PrepareAsync();
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                Assert.AreEqual(value.GetType(), reader.GetFieldType(i), $"prepared={prepared}: column {reader.GetName(i)}");
            }

            Assert.AreEqual("TINYINT UNSIGNED", reader.GetDataTypeName(1));
            Assert.AreEqual("DECIMAL", reader.GetDataTypeName(11));
            Assert.AreEqual("VARCHAR", reader.GetDataTypeName(17));
            Assert.AreEqual("BLOB", reader.GetDataTypeName(19));
            Assert.AreEqual("JSON", reader.GetDataTypeName(21));
            Assert.AreEqual(typeof(ulong), reader.GetColumnSchema()[8].DataType);
        }
    }

    [TestMethod]
    public async Task DapperStyleExplicitDbTypesRoundTrip()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TEMPORARY TABLE params (s VARCHAR(20), i INT, l BIGINT, g CHAR(36), " +
            "d DATETIME, m DECIMAL(10,2), b BOOL, x VARBINARY(4), e INT, n VARCHAR(4))";
        await command.ExecuteNonQueryAsync();

        var id = Guid.NewGuid();
        command.CommandText = "INSERT INTO params VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)";
        Add(command, DbType.String, "text", 4000);
        Add(command, DbType.Int32, 42);
        Add(command, DbType.Int64, 7);
        Add(command, DbType.String, id.ToString());
        Add(command, DbType.DateTime, new DateTime(2026, 11, 22, 10, 30, 0));
        Add(command, DbType.Decimal, 12.5m);
        Add(command, DbType.Boolean, true);
        Add(command, DbType.Binary, new byte[] { 1, 2 }, 8000);
        Add(command, DbType.Int32, DayOfWeek.Friday);
        Add(command, DbType.String, DBNull.Value);
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync());

        command.Parameters.Clear();
        command.CommandText = "SELECT * FROM params";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual("text", reader.GetString(0));
        Assert.AreEqual(42, reader.GetInt32(1));
        Assert.AreEqual(7L, reader.GetInt64(2));
        Assert.AreEqual(id.ToString(), reader.GetString(3));
        Assert.AreEqual(new DateTime(2026, 11, 22, 10, 30, 0), reader.GetDateTime(4));
        Assert.AreEqual(12.5m, reader.GetDecimal(5));
        Assert.IsTrue(reader.GetBoolean(6));
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, (byte[])reader.GetValue(7));
        Assert.AreEqual((int)DayOfWeek.Friday, reader.GetInt32(8));
        Assert.IsTrue(reader.IsDBNull(9));
    }

    [TestMethod]
    public async Task CommandTimeoutRaisesATransientTimeoutAndKeepsTheConnectionUsable()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "SELECT SLEEP(10)";
        var error = await Assert.ThrowsExactlyAsync<ApexDbException>(() => command.ExecuteScalarAsync());
        Assert.IsInstanceOfType<TimeoutException>(error.InnerException);
        Assert.IsTrue(error.IsTransient);

        command.CommandText = "SELECT 1";
        Assert.AreEqual(1L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));

        command.CommandTimeout = 30;
        command.CommandText = "SELECT SLEEP(10)";
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<OperationCanceledException>(() => command.ExecuteScalarAsync(caller.Token));
    }

    [TestMethod]
    public async Task MultiStatementTextAndPreparedReopenWork()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TEMPORARY TABLE multi (id INT); INSERT INTO multi VALUES (1), (2); " +
            "UPDATE multi SET id = id + 10";
        Assert.AreEqual(4, await command.ExecuteNonQueryAsync());

        command.CommandText = "SELECT ? + 1";
        var parameter = command.CreateParameter();
        parameter.DbType = DbType.Int32;
        parameter.Value = 1;
        command.Parameters.Add(parameter);
        await command.PrepareAsync();
        Assert.AreEqual(2L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        await connection.CloseAsync();
        await connection.OpenAsync();
        parameter.Value = 2;
        Assert.AreEqual(3L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
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

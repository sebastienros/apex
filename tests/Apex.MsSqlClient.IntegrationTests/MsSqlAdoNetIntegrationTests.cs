using System.Data;
using System.Data.Common;
using Apex.SqlClient;

namespace Apex.MsSqlClient.IntegrationTests;

/// <summary>Exercises the asynchronous ADO.NET adapter against a real SQL Server.</summary>
[TestClass]
public sealed class MsSqlAdoNetIntegrationTests
{
    private static string ConnectionString
    {
        get
        {
            var options = MsSqlTestEnvironment.Options;
            return $"Server={options.Host},{options.Port};Database={options.Database};" +
                $"User ID={options.Username};Password='{options.Password}';Encrypt=Require;TrustServerCertificate=true";
        }
    }

    private static async Task<MsSqlDbConnection> OpenAsync()
    {
        var connection = new MsSqlDbConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    [TestMethod]
    public async Task FieldTypesMatchTheReturnedValues()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CAST(1 AS tinyint), CAST(2 AS smallint), CAST(3 AS int), CAST(4 AS bigint), CAST(1 AS bit), " +
            "CAST(1.5 AS real), CAST(2.5 AS float), CAST(3.25 AS decimal(10,2)), CAST(4.5 AS money), " +
            "CAST(5.5 AS smallmoney), NEWID(), CAST('2026-11-22' AS date), CAST('10:30' AS time), " +
            "CAST('2026-11-22 10:30' AS datetime2), CAST('2026-11-22 10:30' AS datetime), " +
            "CAST('2026-11-22 10:30' AS smalldatetime), CAST('2026-11-22 10:30 +02:00' AS datetimeoffset), " +
            "N'text', CAST('ansi' AS varchar(10)), CAST('c' AS char(2)), CAST(0x0102 AS varbinary(4)), " +
            "CAST(0x03 AS binary(2)), CAST(N'max' AS nvarchar(max))";
        foreach (var prepared in new[] { false, true })
        {
            if (prepared) await command.PrepareAsync();
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                Assert.AreEqual(value.GetType(), reader.GetFieldType(i), $"prepared={prepared}: column {i} ({reader.GetDataTypeName(i)})");
            }

            Assert.AreEqual("tinyint", reader.GetDataTypeName(0));
            Assert.AreEqual("bigint", reader.GetDataTypeName(3));
            Assert.AreEqual("decimal", reader.GetDataTypeName(7));
            Assert.AreEqual("uniqueidentifier", reader.GetDataTypeName(10));
            Assert.AreEqual("datetimeoffset", reader.GetDataTypeName(16));
            Assert.AreEqual("nvarchar", reader.GetDataTypeName(17));
            Assert.AreEqual(typeof(decimal), reader.GetColumnSchema()[8].DataType);
        }
    }

    [TestMethod]
    public async Task DapperStyleExplicitDbTypesRoundTrip()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        var id = Guid.NewGuid();
        command.CommandText = "SELECT @P1, @P2, @P3, @P4, @P5, @P6, @P7, @P8, @P9, @P10, @P11";
        Add(command, DbType.String, "text", 4000);
        Add(command, DbType.Int32, 42);
        Add(command, DbType.Int64, 7);
        Add(command, DbType.Guid, id);
        Add(command, DbType.DateTime2, new DateTime(2026, 11, 22, 10, 30, 0));
        Add(command, DbType.Decimal, 12.5m);
        Add(command, DbType.Boolean, true);
        Add(command, DbType.Binary, new byte[] { 1, 2 }, 8000);
        Add(command, DbType.Int32, DayOfWeek.Friday);
        Add(command, DbType.AnsiString, DBNull.Value);
        Add(command, DbType.Byte, (byte)9);
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
        Assert.IsTrue(reader.IsDBNull(9));
        Assert.AreEqual((byte)9, reader.GetValue(10));
    }

    [TestMethod]
    public async Task CommandTimeoutRaisesATransientTimeoutAndKeepsTheConnectionUsable()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "WAITFOR DELAY '00:00:10'";
        var error = await Assert.ThrowsExactlyAsync<ApexDbException>(() => command.ExecuteNonQueryAsync());
        Assert.IsInstanceOfType<TimeoutException>(error.InnerException);
        Assert.IsTrue(error.IsTransient);

        command.CommandText = "SELECT 1";
        Assert.AreEqual(1, await command.ExecuteScalarAsync());

        command.CommandTimeout = 30;
        command.CommandText = "WAITFOR DELAY '00:00:10'";
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAsync<OperationCanceledException>(() => command.ExecuteNonQueryAsync(caller.Token));
    }

    [TestMethod]
    public async Task MultiStatementTextAndPreparedReopenWork()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE #multi (id int); INSERT INTO #multi VALUES (1), (2); " +
            "UPDATE #multi SET id = id + 10; SELECT id FROM #multi ORDER BY id";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            // The DDL and DML statements come first as results without columns.
            while (reader.FieldCount == 0)
            {
                Assert.IsTrue(await reader.NextResultAsync());
            }

            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(11, reader.GetInt32(0));
            Assert.AreEqual(4, reader.RecordsAffected);
        }

        command.CommandText = "SELECT @P1 + 1";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@P1";
        parameter.DbType = DbType.Int32;
        parameter.Value = 1;
        command.Parameters.Add(parameter);
        await command.PrepareAsync();
        Assert.AreEqual(2, await command.ExecuteScalarAsync());
        await connection.CloseAsync();
        await connection.OpenAsync();
        parameter.Value = 2;
        Assert.AreEqual(3, await command.ExecuteScalarAsync());
    }

    private static void Add(DbCommand command, DbType dbType, object value, int size = 0)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = $"@P{command.Parameters.Count + 1}";
        parameter.DbType = dbType;
        parameter.Value = value;
        parameter.Size = size;
        command.Parameters.Add(parameter);
    }
}

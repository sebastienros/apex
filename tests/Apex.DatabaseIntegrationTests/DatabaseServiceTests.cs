using Apex.MsSqlClient;
using Apex.MySqlClient;
using Apex.PgClient;
using Apex.SqlClient;

namespace Apex.DatabaseIntegrationTests;

[TestClass]
public sealed class DatabaseServiceTests
{
    [TestMethod]
    public async Task PostgreSqlServiceSupportsCoreWorkflow()
    {
        var options = PgConnectOptions.Parse(RequiredEnvironment("APEX_PG_CONNECTION_STRING"));
        await using var connection = await ConnectWithRetryAsync(
            cancellationToken => Apex.PgClient.PgClient.ConnectAsync(options, cancellationToken));

        await VerifyCoreWorkflowAsync(
            connection,
            "CREATE TEMP TABLE apex_ci_values (value int NOT NULL)",
            "INSERT INTO apex_ci_values VALUES ($1)",
            "SELECT value FROM apex_ci_values",
            "SELECT COUNT(*) FROM apex_ci_values");

        await using PgPool pool = PgPool.Create(options);
        Assert.AreEqual(42, (await pool.QueryAsync("SELECT 42"))[0].GetInt32(0));
    }

    [TestMethod]
    public async Task MySqlServiceSupportsCoreWorkflow()
    {
        var options = MySqlConnectOptions.Parse(RequiredEnvironment("APEX_MYSQL_CONNECTION_STRING"));
        await using var connection = await ConnectWithRetryAsync(
            cancellationToken => Apex.MySqlClient.MySqlClient.ConnectAsync(options, cancellationToken));

        await VerifyCoreWorkflowAsync(
            connection,
            "CREATE TEMPORARY TABLE apex_ci_values (value int NOT NULL)",
            "INSERT INTO apex_ci_values VALUES (?)",
            "SELECT value FROM apex_ci_values",
            "SELECT COUNT(*) FROM apex_ci_values");

        await using MySqlPool pool = MySqlPool.Create(options);
        Assert.AreEqual(42, (await pool.QueryAsync("SELECT 42"))[0].GetInt32(0));
    }

    [TestMethod]
    public async Task SqlServerServiceSupportsCoreWorkflow()
    {
        var options = MsSqlConnectOptions.Parse(RequiredEnvironment("APEX_MSSQL_CONNECTION_STRING"));
        await using var connection = await ConnectWithRetryAsync(
            cancellationToken => Apex.MsSqlClient.MsSqlClient.ConnectAsync(options, cancellationToken));

        await VerifyCoreWorkflowAsync(
            connection,
            "CREATE TABLE #apex_ci_values (value int NOT NULL)",
            "INSERT INTO #apex_ci_values VALUES (@P1)",
            "SELECT value FROM #apex_ci_values",
            "SELECT CAST(COUNT(*) AS bigint) FROM #apex_ci_values");

        await using MsSqlPool pool = MsSqlPool.Create(options);
        Assert.AreEqual(42, (await pool.QueryAsync("SELECT 42"))[0].GetInt32(0));
    }

    private static async Task VerifyCoreWorkflowAsync(
        ISqlConnection connection,
        string createTableSql,
        string insertSql,
        string selectSql,
        string countSql)
    {
        await connection.ExecuteAsync(createTableSql);
        await connection.ExecuteAsync(insertSql, SqlParameters.Create(42));
        Assert.AreEqual(42, (await connection.QueryAsync(selectSql))[0].GetInt32(0));

        await using (await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync(insertSql, SqlParameters.Create(84));
        }

        Assert.AreEqual(1L, (await connection.QueryAsync(countSql))[0].GetInt64(0));
    }

    private static async Task<TConnection> ConnectWithRetryAsync<TConnection>(
        Func<CancellationToken, ValueTask<TConnection>> connect)
        where TConnection : ISqlConnection
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            using CancellationTokenSource attempt = new(TimeSpan.FromSeconds(5));
            try
            {
                return await connect(attempt.Token);
            }
            catch (Exception exception)
            {
                lastError = exception;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        throw new TimeoutException("The database service did not become ready.", lastError);
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable '{name}' is required.");
}
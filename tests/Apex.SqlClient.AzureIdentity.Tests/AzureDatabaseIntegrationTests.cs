using Apex.MsSqlClient;
using Apex.MySqlClient;
using Apex.PgClient;
using Azure.Identity;

namespace Apex.SqlClient.AzureIdentity.Tests;

[TestClass]
public sealed class AzureDatabaseIntegrationTests
{
    [TestMethod]
    public async Task PostgreSqlConnectsWithAzureIdentity()
    {
        if (GetEnvironment("APEX_AZURE_PG_CONNECTION_STRING") is not { } connectionString)
        {
            return;
        }

        var options = PgConnectOptions.Parse(connectionString).UseAzureIdentity(
            new DefaultAzureCredential(),
            new AzureIdentityOptions
            {
                Username = GetEnvironment("APEX_AZURE_PG_USERNAME"),
            });
        await using var connection = await global::Apex.PgClient.PgClient.ConnectAsync(options);
        var rows = await connection.QueryAsync("SELECT 1");
        Assert.AreEqual(1, rows[0].GetInt32(0));
    }

    [TestMethod]
    public async Task MySqlConnectsWithAzureIdentity()
    {
        if (GetEnvironment("APEX_AZURE_MYSQL_CONNECTION_STRING") is not { } connectionString)
        {
            return;
        }

        var options = MySqlConnectOptions.Parse(connectionString).UseAzureIdentity(
            new DefaultAzureCredential(),
            new AzureIdentityOptions
            {
                Username = GetEnvironment("APEX_AZURE_MYSQL_USERNAME"),
            });
        await using var connection =
          await global::Apex.MySqlClient.MySqlClient.ConnectAsync(options);
        var rows = await connection.QueryAsync("SELECT 1");
        Assert.AreEqual(1, rows[0].GetInt32(0));
    }

    [TestMethod]
    public async Task SqlServerConnectsWithAzureIdentity()
    {
        if (GetEnvironment("APEX_AZURE_MSSQL_CONNECTION_STRING") is not { } connectionString)
        {
            return;
        }

        var options = MsSqlConnectOptions.Parse(connectionString).UseAzureIdentity(
            new DefaultAzureCredential());
        await using var connection =
          await global::Apex.MsSqlClient.MsSqlClient.ConnectAsync(options);
        var rows = await connection.QueryAsync("SELECT 1");
        Assert.AreEqual(1, rows[0].GetInt32(0));
    }

    private static string? GetEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : null;
}

using Apex.SqlClient;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.MySql;

namespace Apex.MySqlClient.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class ProxySqlIntegrationTests
{
    [TestMethod]
    public async Task ExecutesPreparedQueryAndBatchThroughProxySql()
    {
        var network = new NetworkBuilder().Build();
        MySqlContainer? backend = null;
        IContainer? proxy = null;
        try
        {
            await network.CreateAsync(CancellationToken.None);
            backend = await MySqlContainerFixture.StartAsync(
              network: network,
              networkAlias: "mysql-backend");
            proxy = new ContainerBuilder(
              Environment.GetEnvironmentVariable("PROXYSQL_IMAGE") ??
              "proxysql/proxysql:latest")
              .WithNetwork(network)
              .WithPortBinding(6032, true)
              .WithPortBinding(6033, true)
              .WithWaitStrategy(
                Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6032))
              .Build();
            await proxy.StartAsync();
            await ConfigureProxyAsync(proxy);
            MySqlConnectOptions options = new()
            {
                Host = proxy.Hostname,
                Port = proxy.GetMappedPublicPort(6033),
                Database = MySqlContainerFixture.Database,
                Username = MySqlContainerFixture.Username,
                Password = MySqlContainerFixture.Password,
                SslMode = MySqlSslMode.Disabled,
                PipeliningLimit = 1,
                ReconnectAttempts = 20,
                ReconnectInterval = TimeSpan.FromMilliseconds(250),
            };

            await using var connection = await MySqlClient.ConnectAsync(options);
            await using (var query = await connection.PrepareAsync(
                           "SELECT CAST(? AS SIGNED) AS value"))
            {
                Assert.AreEqual(
                  42,
                  (await query.QueryAsync(SqlParameters.Create(42)))[0].GetInt32("value"));
            }

            await connection.ExecuteAsync(
              "CREATE TABLE IF NOT EXISTS proxysql_batch (value INT PRIMARY KEY)");
            await connection.ExecuteAsync("DELETE FROM proxysql_batch");
            await using var insert = await connection.PrepareAsync(
              "INSERT INTO proxysql_batch VALUES (?)");
            var results = await insert.ExecuteBatchAsync(
              Enumerable.Range(1, 16)
                .Select(static value => SqlParameters.Create(value))
                .ToArray());

            Assert.HasCount(16, results);
            Assert.IsTrue(results.All(static result => result.AffectedRows == 1));
            Assert.AreEqual(
              16L,
              (await connection.QueryAsync(
                "SELECT COUNT(*) FROM proxysql_batch"))[0].GetInt64(0));
        }
        finally
        {
          if (proxy is not null)
          {
            await proxy.DisposeAsync();
          }

          if (backend is not null)
          {
            await backend.DisposeAsync();
          }

            await network.DisposeAsync();
        }
    }

    private static async Task ConfigureProxyAsync(IContainer proxy)
    {
        await ExecuteAdminAsync(
          proxy,
          "DELETE FROM mysql_servers; " +
          "INSERT INTO mysql_servers(hostgroup_id, hostname, port) " +
          "VALUES (0, 'mysql-backend', 3306); " +
          "LOAD MYSQL SERVERS TO RUNTIME");
        await ExecuteAdminAsync(
          proxy,
          "UPDATE global_variables SET variable_value='false' " +
          "WHERE variable_name='admin-hash_passwords'; " +
          "LOAD ADMIN VARIABLES TO RUNTIME");
        await ExecuteAdminAsync(
          proxy,
          "DELETE FROM mysql_users; " +
          $"INSERT INTO mysql_users(username, password, default_hostgroup, active) VALUES (" +
          $"'{MySqlContainerFixture.Username}', '{MySqlContainerFixture.Password}', 0, 1); " +
          "LOAD MYSQL USERS TO RUNTIME");
    }

    private static async Task ExecuteAdminAsync(IContainer proxy, string sql)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await proxy.ExecAsync(
              [
                  "mysql",
                  "-u", "admin",
                  "-padmin",
                  "-h", "127.0.0.1",
                  "-P", "6032",
                  "-e", sql,
              ]);
            if (result.ExitCode == 0)
            {
                return;
            }

            if (attempt >= 20)
            {
                throw new InvalidOperationException(
                  $"ProxySQL admin command failed: {result.Stderr}");
            }

            await Task.Delay(250);
        }
    }
}
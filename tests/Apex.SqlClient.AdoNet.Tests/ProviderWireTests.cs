using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using Apex.MsSqlClient;
using Apex.MySqlClient;
using Apex.PgClient;

namespace Apex.SqlClient.AdoNet.Tests;

[TestClass]
public sealed class ProviderWireTests
{
    [TestMethod]
    [DataRow("PostgreSql")]
    [DataRow("MySql")]
    [DataRow("SqlServer")]
    public Task CommonAdapterWrapsOrdinaryNativeReadersWithoutResultCapabilities(string provider) =>
        RunAsync(provider, async (wire, _) =>
        {
            await wire.LoginAsync(fail: false);
            await wire.ExpectQueryAsync();
            await wire.WriteResultsAsync([new("value", [1, 2])]);
            await wire.ExpectCloseAsync();
        }, async (connection, token) =>
        {
            await connection.OpenAsync(token);
            var nativeConnection = connection switch
            {
                PgDbConnection postgres => postgres.GetConnectionForCommand(),
                MySqlDbConnection mysql => mysql.GetConnectionForCommand(),
                MsSqlDbConnection sqlServer => sqlServer.GetConnectionForCommand(),
                _ => throw new ArgumentException("Unexpected provider.", nameof(connection)),
            };
            var nativeReader = await nativeConnection.ExecuteReaderAsync("SELECT value", cancellationToken: token);
            Assert.IsFalse(nativeReader is ISqlMultiResultReader);
            await using (var reader = new ApexDbDataReader(
                nativeReader, CommandBehavior.CloseConnection, connection))
            {
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual("value", reader.GetName(0));
                Assert.AreEqual(1, reader.FieldCount);
                Assert.IsTrue(reader.HasRows);
                Assert.AreEqual(1, reader.GetInt32(0));
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(2, reader.GetInt32(0));
                Assert.IsFalse(await reader.ReadAsync(token));
                Assert.IsFalse(await reader.NextResultAsync(token));
            }
            Assert.AreEqual(ConnectionState.Closed, connection.State);
        });

    [TestMethod]
    [DataRow("PostgreSql")]
    [DataRow("MySql")]
    [DataRow("SqlServer")]
    public Task ReadersPreserveMetadataDrainUnreadRowsAndRecoverAfterServerErrors(string provider) =>
        RunAsync(provider, async (wire, token) =>
        {
            await wire.LoginAsync(fail: false);
            await wire.ExpectQueryAsync();
            await wire.WriteResultsAsync(provider == "PostgreSql"
                ? [new("first", [1, 2])]
                : [new("first", [1, 2]), new("second", [3])]);
            await wire.ExpectQueryAsync();
            await wire.WriteResultsAsync([new("empty", [])]);
            await wire.ExpectQueryAsync();
            await wire.WriteErrorAsync();
            await wire.ExpectQueryAsync();
            await wire.WriteResultsAsync([new("recovered", [4])]);
            await wire.ExpectCloseAsync();
        }, async (connection, token) =>
        {
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = provider == "PostgreSql" ? "SELECT value" : "SELECT value; SELECT other";
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                Assert.IsTrue(reader.HasRows);
                Assert.AreEqual(1, reader.FieldCount);
                Assert.AreEqual("first", reader.GetName(0));
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(1, reader.GetInt32(0));

                // NextResult must discard the unread second row, not lose the next result's first row.
                if (provider != "PostgreSql")
                {
                    Assert.IsTrue(await reader.NextResultAsync(token));
                    Assert.AreEqual("second", reader.GetName(0));
                    Assert.IsTrue(reader.HasRows);
                    Assert.IsTrue(await reader.ReadAsync(token));
                    Assert.AreEqual(3, reader.GetInt32(0));
                    Assert.IsFalse(await reader.ReadAsync(token));
                }
                Assert.IsFalse(await reader.NextResultAsync(token));
            }

            command.CommandText = "SELECT empty";
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                Assert.AreEqual(1, reader.FieldCount);
                Assert.AreEqual("empty", reader.GetName(0));
                Assert.IsFalse(reader.HasRows);
                Assert.IsFalse(await reader.ReadAsync(token));
                Assert.IsFalse(await reader.NextResultAsync(token));
            }

            command.CommandText = "SELECT error";
            var error = await Assert.ThrowsExactlyAsync<ApexDbException>(
                async () =>
                {
                    await using var reader = await command.ExecuteReaderAsync(token);
                    do
                    {
                        while (await reader.ReadAsync(token)) { }
                    }
                    while (await reader.NextResultAsync(token));
                });
            AssertProviderError(provider, error);

            command.CommandText = "SELECT recovered";
            await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection, token))
            {
                Assert.IsTrue(reader.HasRows);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(4, reader.GetInt32(0));
                Assert.IsFalse(await reader.ReadAsync(token));
                Assert.IsFalse(await reader.NextResultAsync(token));
            }
            Assert.AreEqual(ConnectionState.Closed, connection.State);
        });

    [TestMethod]
    [DataRow("PostgreSql")]
    [DataRow("MySql")]
    [DataRow("SqlServer")]
    public Task OpenTranslatesWireErrorsAndRestoresClosedState(string provider) =>
        RunAsync(provider, (wire, _) => wire.LoginAsync(fail: true), async (connection, token) =>
        {
            var error = await Assert.ThrowsExactlyAsync<ApexDbException>(() => connection.OpenAsync(token));
            AssertProviderError(provider, error);
            Assert.AreEqual(ConnectionState.Closed, connection.State);
        });

    private static void AssertProviderError(string provider, DbException error)
    {
        Assert.AreEqual("wire error", error.Message);
        switch (provider)
        {
            case "PostgreSql":
                Assert.IsInstanceOfType<PgException>(error.InnerException);
                Assert.AreEqual("40001", error.SqlState);
                Assert.IsTrue(error.IsTransient);
                break;
            case "MySql":
                Assert.IsInstanceOfType<MySqlException>(error.InnerException);
                Assert.AreEqual("23000", error.SqlState);
                Assert.AreEqual(1062, error.ErrorCode);
                break;
            case "SqlServer":
                Assert.IsInstanceOfType<MsSqlException>(error.InnerException);
                Assert.AreEqual(2627, error.ErrorCode);
                Assert.IsNull(error.SqlState);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider));
        }
    }

    private static async Task RunAsync(
        string provider,
        Func<AdapterWireSession, CancellationToken, Task> serve,
        Func<DbConnection, CancellationToken, Task> exercise)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using DbConnection connection = provider switch
        {
            "PostgreSql" => new PgDbConnection(
                $"Host=127.0.0.1;Port={port};Username=user;Database=test;SslMode=Disable"),
            "MySql" => new MySqlDbConnection(
                $"Server=127.0.0.1;Port={port};User ID=user;Database=test;SslMode=Disabled;AllowMultiStatements=true"),
            "SqlServer" => new MsSqlDbConnection(
                $"Server=127.0.0.1,{port};User ID=user;Database=test;Encrypt=Disable"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

        await Task.WhenAll(ServeAsync(), exercise(connection, timeout.Token));

        async Task ServeAsync()
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            AdapterWireSession wire = provider switch
            {
                "PostgreSql" => new PgAdapterWireSession(stream, timeout.Token),
                "MySql" => new MySqlAdapterWireSession(stream, timeout.Token),
                "SqlServer" => new MsSqlAdapterWireSession(stream, timeout.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            };
            await serve(wire, timeout.Token);
        }
    }
}

internal readonly record struct WireResult(string Name, int[] Rows);

internal abstract class AdapterWireSession(Stream stream, CancellationToken cancellationToken)
{
    protected Stream Stream { get; } = stream;
    protected CancellationToken CancellationToken { get; } = cancellationToken;
    internal abstract Task LoginAsync(bool fail);
    internal abstract Task ExpectQueryAsync();
    internal abstract Task WriteResultsAsync(WireResult[] results);
    internal abstract Task WriteErrorAsync();
    internal abstract Task ExpectCloseAsync();
}

using System.Globalization;
using Apex.MySqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MySqlClient.Tests;

// Rows already in the receive buffer are read by ReadAsync itself, without waking the pump.
public sealed partial class MySqlConnectionWireTests
{
    [TestMethod]
    public async Task ReaderReadsReceivedRowsWithoutYielding()
    {
        const int rowCount = 200;
        await using var harness = await ServerHarness.StartAsync();
        Task server = Task.Run(async () =>
        {
            await using var connection = await harness.AcceptAsync();
            await connection.CompleteHandshakeAsync("8.4.2");
            await connection.ExpectTextCommandAsync(MySqlCommand.Query, "SELECT n");
            connection.BeginBatch();
            await connection.WriteColumnCountAsync(1);
            await connection.WriteColumnAsync("n", MySqlType.Long);
            for (var value = 1; value <= rowCount; value++)
            {
                await connection.WriteTextRowAsync(value.ToString(CultureInfo.InvariantCulture));
            }

            await connection.WriteFinalOkAsync();
            await connection.FlushBatchAsync();
            await connection.ExpectCommandAsync(MySqlCommand.Quit);
        });

        await using var client = await MySqlClient.ConnectAsync(harness.CreateOptions());
        await using (var reader = await client.ExecuteReaderAsync("SELECT n"))
        {
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(1, reader.GetInt32(0));
            for (var value = 2; value <= rowCount; value++)
            {
                var read = reader.ReadAsync();
                Assert.IsTrue(read.IsCompleted, $"Reading received row {value} yielded.");
                Assert.IsTrue(await read);
                Assert.AreEqual(value, reader.GetInt32(0));
            }

            // The end of the result is also received, so the pump finishes inline.
            var end = reader.ReadAsync();
            Assert.IsTrue(end.IsCompleted, "Reading the received end of the result yielded.");
            Assert.IsFalse(await end);
        }

        await client.DisposeAsync();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ResultReaderReadsReceivedRowsOfEachResultAndSurfacesALaterError()
    {
        const int rowCount = 30;
        await using var harness = await ServerHarness.StartAsync();
        Task server = Task.Run(async () =>
        {
            await using var connection = await harness.AcceptAsync();
            await connection.CompleteHandshakeAsync("8.4.2");
            await connection.ExpectTextCommandAsync(MySqlCommand.Query, "SELECT n; SELECT n; SELECT bad");
            connection.BeginBatch();
            foreach (var start in new[] { 1, 101 })
            {
                await connection.WriteColumnCountAsync(1);
                await connection.WriteColumnAsync("n", MySqlType.Long);
                for (var value = start; value < start + rowCount; value++)
                {
                    await connection.WriteTextRowAsync(value.ToString(CultureInfo.InvariantCulture));
                }

                await connection.WriteOkWithMoreResultsAsync();
            }

            await connection.WriteErrorAsync(1054, "42S22", "Unknown column 'bad'");
            await connection.FlushBatchAsync();
            await connection.ExpectCommandAsync(MySqlCommand.Quit);
        });

        await using var client = await MySqlClient.ConnectAsync(harness.CreateOptions());
        var reader = await client.ExecuteResultReaderAsync("SELECT n; SELECT n; SELECT bad", default, default);
        var results = (ISqlResultBoundaryReader)reader;
        foreach (var start in new[] { 1, 101 })
        {
            Assert.IsTrue(await results.InitializeAsync());
            Assert.AreEqual(start, reader.GetInt32(0));
            for (var value = start + 1; value < start + rowCount; value++)
            {
                var read = reader.ReadAsync();
                Assert.IsTrue(read.IsCompleted, $"Reading received row {value} yielded.");
                Assert.IsTrue(await read);
                Assert.AreEqual(value, reader.GetInt32(0));
            }

            Assert.IsFalse(await reader.ReadAsync());
            if (start == 1)
            {
                Assert.IsTrue(await results.NextResultAsync());
            }
        }

        await Assert.ThrowsAsync<MySqlException>(() => results.NextResultAsync().AsTask());
        try
        {
            await reader.DisposeAsync();
        }
        catch (MySqlException)
        {
        }

        await client.DisposeAsync();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task DisposingBetweenReceivedRowsDrainsAndReusesTheConnection()
    {
        await using var harness = await ServerHarness.StartAsync();
        Task server = Task.Run(async () =>
        {
            await using var connection = await harness.AcceptAsync();
            await connection.CompleteHandshakeAsync("8.4.2");
            await connection.ExpectTextCommandAsync(MySqlCommand.Query, "SELECT n");
            connection.BeginBatch();
            await connection.WriteColumnCountAsync(1);
            await connection.WriteColumnAsync("n", MySqlType.Long);
            for (var value = 1; value <= 50; value++)
            {
                await connection.WriteTextRowAsync(value.ToString(CultureInfo.InvariantCulture));
            }

            await connection.WriteFinalOkAsync();
            await connection.FlushBatchAsync();

            await connection.ExpectTextCommandAsync(MySqlCommand.Query, "SELECT 7");
            await connection.WriteColumnCountAsync(1);
            await connection.WriteColumnAsync("7", MySqlType.Long);
            await connection.WriteTextRowAsync("7");
            await connection.WriteFinalOkAsync();
            await connection.ExpectCommandAsync(MySqlCommand.Quit);
        });

        await using var client = await MySqlClient.ConnectAsync(harness.CreateOptions());
        await using (var reader = await client.ExecuteReaderAsync("SELECT n"))
        {
            for (var value = 1; value <= 5; value++)
            {
                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual(value, reader.GetInt32(0));
            }
        }

        var rows = await client.QueryAsync("SELECT 7");
        Assert.AreEqual(7, rows[0].GetInt32(0));
        await client.DisposeAsync();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

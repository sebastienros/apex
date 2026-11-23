using System.Diagnostics;
using Apex.SqlClient;

namespace Apex.MsSqlClient.IntegrationTests;

[TestClass]
public sealed class MsSqlPreparedHandleIntegrationTests
{
    // Large enough (about 40 MB) that the server is still blocked sending rows when the
    // client abandons the reader, so the attention lands before sp_prepexec's RETURNVALUE.
    private const string LargeResultSql =
      """
      SELECT TOP (@P1) CAST(REPLICATE(N'x', 4000) AS nvarchar(4000)) AS payload
      FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
      """;

    private const int SqlServerMissingPreparedHandle = 8179;

    [TestMethod]
    public async Task HandleProbeSeesLivePreparedHandles()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        _ = await connection.QueryAsync(
          "DECLARE @h int; EXEC sp_prepare @h OUTPUT, NULL, N'SELECT 1'; SELECT @h");

        Assert.AreEqual(1, await CountLiveHandlesAsync(connection));
        Assert.AreEqual(0, await CountLiveHandlesAsync(connection));
    }

    [TestMethod]
    public async Task AbandonedFirstReaderDoesNotLeakPreparedHandle()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        var statement = await connection.PrepareAsync(LargeResultSql);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var reader =
              await statement.ExecuteReaderAsync(SqlParameters.Create(5_000));
            Assert.IsTrue(await reader.ReadAsync());
        }

        // The statement keeps one handle across abandoned readers and reuses it.
        Assert.HasCount(
          3,
          await statement.QueryAsync(SqlParameters.Create(3)));
        await statement.DisposeAsync();

        Assert.AreEqual(0, await CountLiveHandlesAsync(connection));
    }

    [TestMethod]
    public async Task AbandonedFirstStreamDoesNotLeakPreparedHandle()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        var statement = await connection.PrepareAsync(LargeResultSql);
        await foreach (var _ in statement.StreamAsync(SqlParameters.Create(5_000), fetchSize: 4))
        {
            break;
        }

        await statement.DisposeAsync();
        Assert.AreEqual(0, await CountLiveHandlesAsync(connection));
    }

    [TestMethod]
    public async Task CanceledFirstQueryDoesNotLeakPreparedHandle()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        var statement = await connection.PrepareAsync(
          "WAITFOR DELAY '00:00:10'; SELECT CAST(@P1 AS int)");
        using (CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(250)))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(
              () => statement.QueryAsync(SqlParameters.Create(1), cancellation.Token).AsTask());
        }

        await statement.DisposeAsync();
        Assert.AreEqual(0, await CountLiveHandlesAsync(connection));
        Assert.AreEqual(
          42,
          (await connection.QueryAsync("SELECT CAST(42 AS int)"))[0].GetInt32(0));
    }

    [TestMethod]
    public async Task PrepareErrorSurfacesAndKeepsConnectionUsable()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        await using var statement =
          await connection.PrepareAsync("SELECT FROM WHERE @P1");

        await Assert.ThrowsExactlyAsync<MsSqlException>(
          () => statement.QueryAsync(SqlParameters.Create(1)).AsTask());
        Assert.AreEqual(
          42,
          (await connection.QueryAsync("SELECT CAST(42 AS int)"))[0].GetInt32(0));
        Assert.AreEqual(0, await CountLiveHandlesAsync(connection));
    }

    [TestMethod]
    public async Task PreparedExecuteSendsPositionalMixedParametersWithNulls()
    {
        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        await using var statement = await connection.PrepareAsync(
          """
          SELECT @P1 AS id, @P2 AS name, @P3 AS amount, @P4 AS missing,
                 @P5 AS payload, @P6 AS flag
          """);

        for (var i = 0; i < 3; i++)
        {
            var rows = await statement.QueryAsync(
              SqlParameters.Create(
                i,
                $"name-{i}",
                12.5m + i,
                SqlValue.Null,
                new byte[] { 1, 2, (byte)i },
                i % 2 == 0));

            Assert.AreEqual(i, rows[0].GetInt32("id"));
            Assert.AreEqual($"name-{i}", rows[0].GetString("name"));
            Assert.AreEqual(12.5m + i, rows[0].Get<decimal>("amount"));
            Assert.IsTrue(rows[0].IsNull(3));
            CollectionAssert.AreEqual(new byte[] { 1, 2, (byte)i }, rows[0].Get<byte[]>("payload"));
            Assert.AreEqual(i % 2 == 0, rows[0].Get<bool>("flag"));
        }
    }

    [TestMethod]
    public async Task UseStatementUpdatesDiagnosticsNamespace()
    {
        // Other tests run in parallel and share the global listener, so only activities in this
        // test's trace are recorded.
        using var trace = new Activity("use-namespace-probe").Start();
        var traceId = trace.TraceId;
        List<string?> namespaces = [];
        using ActivityListener listener = new()
        {
            ShouldListenTo = static source => source.Name == "Apex.SqlClient",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
              ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId != traceId)
                {
                    return;
                }

                lock (namespaces)
                {
                    namespaces.Add(activity.GetTagItem("db.namespace") as string);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await using var connection =
          await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options);
        await connection.ExecuteAsync("USE tempdb");
        _ = await connection.QueryAsync("SELECT 1");
        await connection.ExecuteAsync("USE master");
        _ = await connection.QueryAsync("SELECT 1");

        lock (namespaces)
        {
            Assert.AreEqual(
              "master,tempdb,tempdb,master",
              string.Join(',', namespaces));
        }
    }

    // Prepared handles are numbered per session, so probing low handle numbers with
    // sp_unprepare finds (and releases) any handle left on the session.
    private static async Task<int> CountLiveHandlesAsync(MsSqlConnection connection)
    {
        var live = 0;
        for (var handle = 1; handle <= 64; handle++)
        {
            try
            {
                await connection.ExecuteAsync($"EXEC sp_unprepare {handle}");
                live++;
            }
            catch (MsSqlException exception)
              when (exception.Number == SqlServerMissingPreparedHandle)
            {
            }
        }

        return live;
    }
}

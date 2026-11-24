using System.Buffers.Binary;
using System.Text;
using Apex.MySqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MySqlClient.Tests;

public sealed partial class MySqlConnectionWireTests
{
    [TestMethod]
    public void ReadGuidParsesBracedAndParenthesized38CharStrings()
    {
        var expected = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var metadata = new MySqlColumnMetadata(
            "g", "g", string.Empty, string.Empty, string.Empty,
            MySqlType.VarString, MySqlColumnFlags.None, 45, 152, 0);
        var decoder = new MySqlRowDecoder(new Utf8StringCache(0, 0), MySqlZeroDateBehavior.Error);
        decoder.SetColumns([metadata], binary: false);
        var column = decoder.Columns[0];

        foreach (var text in new[] { expected.ToString("B"), expected.ToString("P") })
        {
            var payload = EncodeLengthPrefixedTextRow(text);
            Assert.AreEqual(expected, decoder.DecodeGuid(payload, 0, column));
        }
    }

    [TestMethod]
    public void DecodeNullableTimeOnlyReturnsNullForZeroDateTimeWhenZeroDateBehaviorIsNull()
    {
        var metadata = new MySqlColumnMetadata(
            "dt", "dt", string.Empty, string.Empty, string.Empty,
            MySqlType.DateTime, MySqlColumnFlags.None, 63, 19, 0);
        var decoder = new MySqlRowDecoder(new Utf8StringCache(0, 0), MySqlZeroDateBehavior.Null);
        decoder.SetColumns([metadata], binary: false);
        var column = decoder.Columns[0];
        var payload = EncodeLengthPrefixedTextRow("0000-00-00 00:00:00");

        Assert.IsTrue(decoder.IsNull(payload, 0));
        Assert.IsNull(decoder.DecodeNullableTimeOnly(payload, 0, column));
    }

    [TestMethod]
    public void ReadDoubleParsesWideDecimalBeyondSystemDecimalRange()
    {
        var metadata = new MySqlColumnMetadata(
            "bigdec", "bigdec", string.Empty, string.Empty, string.Empty,
            MySqlType.NewDecimal, MySqlColumnFlags.None, 63, 42, 0);
        var decoder = new MySqlRowDecoder(new Utf8StringCache(0, 0), MySqlZeroDateBehavior.Error);
        decoder.SetColumns([metadata], binary: false);
        var column = decoder.Columns[0];
        var payload = EncodeLengthPrefixedTextRow("1000000000000000000000000000000");

        Assert.AreEqual(1e30, decoder.DecodeDouble(payload, 0, column));
    }

    [TestMethod]
    public void ParseUriStripsIpv6BracketsAndEnumParserRejectsNumericStrings()
    {
        var options = MySqlConnectOptions.Parse("mysql://root@[::1]:3306/app");
        Assert.AreEqual("::1", options.Host);

        Assert.ThrowsExactly<ArgumentException>(() => MySqlConnectOptions.Parse("Server=localhost;SslMode=999"));
        Assert.ThrowsExactly<ArgumentException>(() => MySqlConnectOptions.Parse("Server=localhost;ZeroDateBehavior=7"));
        Assert.ThrowsExactly<ArgumentException>(() => MySqlConnectOptions.Parse("Server=localhost;AuthenticationPlugin=42"));
    }

    [TestMethod]
    public async Task LocalInfileMissingFileKeepsConnectionUsableAndDrainsMultiStatementResults()
    {
        await using var harness = await ServerHarness.StartAsync();
        Task server = Task.Run(async () =>
        {
            await using var connection = await harness.AcceptAsync();
            await connection.CompleteHandshakeAsync("8.4.2");

            await connection.ExpectTextCommandAsync(
                MySqlCommand.Query,
                "LOAD DATA LOCAL INFILE '/nonexistent-apex-file.csv' INTO TABLE t; SELECT 1");
            await connection.WritePacketAsync([0xFB, .. Encoding.UTF8.GetBytes("/nonexistent-apex-file.csv")]);
            var empty = await connection.ReadPacketAsync();
            Assert.IsEmpty(empty);
            // OK packet with MoreResultsExist (0x00 header + affected=0 + lastId=0 + status + warnings=0)
            var status = (ushort)(MySqlServerStatus.AutoCommit | MySqlServerStatus.MoreResultsExist);
            await connection.WritePacketAsync([0x00, 0x00, 0x00, (byte)(status & 0xFF), (byte)(status >> 8), 0x00, 0x00]);
            await connection.WriteColumnCountAsync(1);
            await connection.WriteColumnAsync("v", MySqlType.Long);
            await connection.WriteTextRowAsync("1");
            await connection.WriteFinalOkAsync();

            await connection.ExpectTextCommandAsync(MySqlCommand.Query, "SELECT 42");
            await connection.WriteColumnCountAsync(1);
            await connection.WriteColumnAsync("answer", MySqlType.Long);
            await connection.WriteTextRowAsync("42");
            await connection.WriteFinalOkAsync();

            await connection.ExpectCommandAsync(MySqlCommand.Quit);
        });

        await using var client = await MySqlClient.ConnectAsync(
            harness.CreateOptions() with { AllowLoadLocalInfile = true, AllowMultiStatements = true });

        await Assert.ThrowsAsync<IOException>(() =>
            client.ExecuteAsync("LOAD DATA LOCAL INFILE '/nonexistent-apex-file.csv' INTO TABLE t; SELECT 1").AsTask());

        Assert.IsTrue(client.IsUsable);
        var rows = await client.QueryAsync("SELECT 42");
        Assert.AreEqual(42, rows[0].GetInt32(0));

        await client.DisposeAsync();
        await server;
    }

    [TestMethod]
    public async Task InvalidUtf8InColumnMetadataInvalidatesConnection()
    {
        await using var harness = await ServerHarness.StartAsync();
        Task server = Task.Run(async () =>
        {
            await using var connection = await harness.AcceptAsync();
            await connection.CompleteHandshakeAsync("8.4.2");

            await connection.ExpectTextCommandAsync(MySqlCommand.Query, "SELECT 1");
            await connection.WriteColumnCountAsync(1);
            using MemoryStream ms = new();
            ms.Write([3, (byte)'d', (byte)'e', (byte)'f']);
            ms.Write([0, 0, 0]);
            ms.Write([1, 0xFF]); // invalid UTF-8 column name
            ms.Write([0]);
            ms.Write([0x0C, 45, 0, 11, 0, 0, 0, (byte)MySqlType.Long, 0, 0, 0, 0, 0]);
            await connection.WritePacketAsync(ms.ToArray());
            await connection.WriteTextRowAsync("1");
            await connection.WriteFinalOkAsync();
        });

        await using var client = await MySqlClient.ConnectAsync(harness.CreateOptions());
        await Assert.ThrowsExactlyAsync<DecoderFallbackException>(() => client.QueryAsync("SELECT 1").AsTask());
        Assert.IsFalse(client.IsUsable);

        await client.DisposeAsync();
        await server;
    }

    [TestMethod]
    public async Task NeedReprepare1615ClosesEvictedPreparedStatementBeforeRepreparing()
    {
        var closedStatementIds = new List<uint>();
        await using var harness = await ServerHarness.StartAsync();
        Task server = Task.Run(async () =>
        {
            await using var connection = await harness.AcceptAsync();
            await connection.CompleteHandshakeAsync("8.4.2");

            await connection.ExpectTextCommandAsync(MySqlCommand.StatementPrepare, "SELECT ?");
            await connection.WritePrepareOkAsync(statementId: 10, columnCount: 1, parameterCount: 1);
            await connection.WriteColumnAsync("?", MySqlType.Long);
            await connection.WriteColumnAsync("val", MySqlType.Long);

            var exec1 = await connection.ReadPacketAsync();
            Assert.AreEqual((byte)MySqlCommand.StatementExecute, exec1[0]);
            Assert.AreEqual(10u, BinaryPrimitives.ReadUInt32LittleEndian(exec1.AsSpan(1, 4)));
            await connection.WriteErrorAsync(1615, "HY000", "Prepared statement needs to be re-prepared");

            var closePacket = await connection.ReadPacketAsync();
            Assert.AreEqual((byte)MySqlCommand.StatementClose, closePacket[0]);
            closedStatementIds.Add(BinaryPrimitives.ReadUInt32LittleEndian(closePacket.AsSpan(1, 4)));

            await connection.ExpectTextCommandAsync(MySqlCommand.StatementPrepare, "SELECT ?");
            await connection.WritePrepareOkAsync(statementId: 11, columnCount: 1, parameterCount: 1);
            await connection.WriteColumnAsync("?", MySqlType.Long);
            await connection.WriteColumnAsync("val", MySqlType.Long);

            var exec2 = await connection.ReadPacketAsync();
            Assert.AreEqual((byte)MySqlCommand.StatementExecute, exec2[0]);
            Assert.AreEqual(11u, BinaryPrimitives.ReadUInt32LittleEndian(exec2.AsSpan(1, 4)));
            await connection.WriteColumnCountAsync(1);
            await connection.WriteColumnAsync("val", MySqlType.Long);
            await connection.WriteBinaryRowAsync([MySqlType.Long], [7L]);
            await connection.WriteFinalOkAsync();

            await connection.ExpectCommandAsync(MySqlCommand.Quit);
        });

        await using var client = await MySqlClient.ConnectAsync(
            harness.CreateOptions() with { CachePreparedStatements = true });
        var rows = await client.QueryAsync("SELECT ?", SqlParameters.Create(7));
        Assert.AreEqual(7, rows[0].GetInt32(0));
        CollectionAssert.AreEqual(new uint[] { 10 }, closedStatementIds);

        await client.DisposeAsync();
        await server;
    }

    [TestMethod]
    public async Task CancellationDuringPrepareSkipsExecuteAndKeepsConnectionUsable()
    {
        var prepareReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPrepareReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executeSent = false;

        await using var harness = await ServerHarness.StartAsync();
        Task server = Task.Run(async () =>
        {
            await using var connection = await harness.AcceptAsync();
            await connection.CompleteHandshakeAsync("8.4.2");

            await connection.ExpectTextCommandAsync(MySqlCommand.StatementPrepare, "SELECT ?");
            prepareReceived.SetResult();
            await allowPrepareReply.Task;
            await connection.WritePrepareOkAsync(statementId: 5, columnCount: 1, parameterCount: 1);
            await connection.WriteColumnAsync("?", MySqlType.Long);
            await connection.WriteColumnAsync("v", MySqlType.Long);

            var next = await connection.ReadPacketAsync();
            if (next[0] == (byte)MySqlCommand.StatementExecute)
            {
                executeSent = true;
            }
            else if (next[0] == (byte)MySqlCommand.StatementClose)
            {
                await connection.ExpectCommandAsync(MySqlCommand.Quit);
            }
        });

        await using var client = await MySqlClient.ConnectAsync(
            harness.CreateOptions() with
            {
                CachePreparedStatements = false,
                QueryCancellation = MySqlQueryCancellation.CloseConnection,
            });

        using var cts = new CancellationTokenSource();
        var queryTask = client.QueryAsync("SELECT ?", SqlParameters.Create(1), cts.Token).AsTask();
        await prepareReceived.Task;
        cts.Cancel();
        allowPrepareReply.SetResult();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => queryTask);
        Assert.IsFalse(executeSent);
        Assert.IsTrue(client.IsUsable);

        await client.DisposeAsync();
        await server;
    }

    [TestMethod]
    public async Task ResultReaderNextResultCancellationReportsTransitionCancellationToken()
    {
        var secondResultReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var serverStop = new CancellationTokenSource();
        await using var harness = await ServerHarness.StartAsync();
        Task server = Task.Run(async () =>
        {
            await using var connection = await harness.AcceptAsync();
            await connection.CompleteHandshakeAsync("8.4.2", connectionId: 777);

            await connection.ExpectTextCommandAsync(MySqlCommand.Query, "SELECT 1; SELECT 2");
            await connection.WriteColumnCountAsync(1);
            await connection.WriteColumnAsync("v", MySqlType.Long);
            await connection.WriteTextRowAsync("1");
            await connection.WriteOkWithMoreResultsAsync();
            secondResultReady.SetResult();
            // Hold second result so NextResultAsync(cts.Token) cancels while waiting.
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, serverStop.Token);
            }
            catch (OperationCanceledException)
            {
            }
        });

        await using var client = await MySqlClient.ConnectAsync(
            harness.CreateOptions() with
            {
                AllowMultiStatements = true,
                QueryCancellation = MySqlQueryCancellation.CloseConnection,
            });

        await using (var reader = await client.ExecuteResultReaderAsync(
            "SELECT 1; SELECT 2",
            SqlParameters.Empty,
            CancellationToken.None))
        {
            var results = (ISqlMultiResultReader)reader;
            Assert.IsTrue(await reader.ReadAsync());
            await secondResultReady.Task;

            using var cts = new CancellationTokenSource();
            var nextTask = results.NextResultAsync(cts.Token).AsTask();
            await Task.Delay(50);
            cts.Cancel();

            var canceled = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => nextTask);
            Assert.AreEqual(cts.Token, canceled.CancellationToken);
        }

        serverStop.Cancel();
        await server;
    }

    [TestMethod]
    public async Task ConcurrentConnectionDisposeAsyncIsSafe()
    {
        await using var harness = await ServerHarness.StartAsync();
        Task server = Task.Run(async () =>
        {
            await using var connection = await harness.AcceptAsync();
            await connection.CompleteHandshakeAsync("8.4.2");
            try
            {
                _ = await connection.ReadPacketAsync();
            }
            catch
            {
            }
        });

        var client = await MySqlClient.ConnectAsync(harness.CreateOptions());
        await Task.WhenAll(
            Task.Run(() => client.DisposeAsync().AsTask()),
            Task.Run(() => client.DisposeAsync().AsTask()),
            Task.Run(() => client.DisposeAsync().AsTask()));
        await server;
    }

    private static byte[] EncodeLengthPrefixedTextRow(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var payload = new byte[1 + bytes.Length];
        payload[0] = (byte)bytes.Length;
        bytes.CopyTo(payload, 1);
        return payload;
    }
}

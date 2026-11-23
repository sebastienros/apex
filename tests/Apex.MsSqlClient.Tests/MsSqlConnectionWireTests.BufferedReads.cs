using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Apex.MsSqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MsSqlClient.Tests;

// Rows already in the receive buffer are read by ReadAsync itself, without waking the pump.
public sealed partial class MsSqlConnectionWireTests
{
    private const int DoneTokenLength = 13;

    [TestMethod]
    public async Task BorrowedReaderReadsReceivedRowsWithoutYielding()
    {
        const int rowCount = 200;
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunResponseServerAsync(listener, BuildIntResult(Sequence(1, rowCount)));
        await using var connection = await MsSqlClient.ConnectAsync(TestOptions(port));
        await using var reader = await connection.ExecuteReaderAsync("SELECT value");

        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(1, reader.GetInt32(0));
        // The server task ends once it has written the whole response.
        await server.WaitAsync(TimeSpan.FromSeconds(5));
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
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [DataRow(9)]
    [DataRow(17)]
    [DataRow(504)]
    public async Task BorrowedReaderReadsRowsSplitAcrossPacketsInOrder(int packetPayload)
    {
        // Rows that fit in the current packet are read inline; the ones that cross a packet go
        // through the pump. Both must interleave without losing or repeating a row.
        const int rowCount = 300;
        var values = new string[rowCount];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = new string((char)('a' + (i % 26)), i % 7);
        }

        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunPacketizedResponseServerAsync(listener, BuildNVarCharResult(values), packetPayload);
        await using var connection = await MsSqlClient.ConnectAsync(TestOptions(port));
        await using var reader = await connection.ExecuteReaderAsync("SELECT value");

        for (var i = 0; i < values.Length; i++)
        {
            Assert.IsTrue(await reader.ReadAsync(), $"Row {i} is missing.");
            Assert.AreEqual(values[i], reader.GetString(0));
        }

        Assert.IsFalse(await reader.ReadAsync());
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancelingOrDisposingBetweenReceivedRowsSendsAttentionAndReusesConnection(
        bool dispose)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunAttentionDuringRowsServerAsync(listener, rowCount: 50);
        await using var connection = await MsSqlClient.ConnectAsync(TestOptions(port));
        using CancellationTokenSource cancellation = new();
        var reader = await connection.ExecuteReaderAsync(
            "SELECT value",
            cancellationToken: cancellation.Token);
        for (var value = 1; value <= 5; value++)
        {
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(value, reader.GetInt32(0));
        }

        if (dispose)
        {
            await reader.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                while (await reader.ReadAsync())
                {
                }
            });
            await reader.DisposeAsync();
        }

        var rows = await connection.QueryAsync("SELECT 7 AS value");
        Assert.AreEqual(7, rows[0].GetInt32(0));
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ServerErrorAfterReceivedRowsFailsTheNextRead()
    {
        const int rowCount = 40;
        var result = BuildIntResult(Sequence(1, rowCount));
        ArrayBufferWriter<byte> response = new();
        response.Write(result.AsSpan(0, result.Length - DoneTokenLength));
        WriteError(response, "Divide by zero error encountered.");
        WriteDone(response, (ushort)TdsDoneStatus.Error);

        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunResponseServerAsync(listener, response.WrittenMemory.ToArray());
        await using var connection = await MsSqlClient.ConnectAsync(TestOptions(port));
        var reader = await connection.ExecuteReaderAsync("SELECT value");
        for (var value = 1; value <= rowCount; value++)
        {
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(value, reader.GetInt32(0));
        }

        var exception = await Assert.ThrowsAsync<SqlClientException>(() => reader.ReadAsync().AsTask());
        StringAssert.Contains(exception.Message, "Divide by zero");
        try
        {
            await reader.DisposeAsync();
        }
        catch (SqlClientException)
        {
        }

        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ResultReaderReadsReceivedRowsOfEachResult()
    {
        const int rowCount = 30;
        ArrayBufferWriter<byte> response = new();
        var first = BuildIntResult(Sequence(1, rowCount));
        response.Write(first.AsSpan(0, first.Length - DoneTokenLength));
        WriteDone(response, (ushort)(TdsDoneStatus.More | TdsDoneStatus.Count), TdsSelectCommand, rowCount);
        var second = BuildIntResult(Sequence(101, rowCount));
        response.Write(second.AsSpan(0, second.Length - DoneTokenLength));
        WriteDone(response, (ushort)TdsDoneStatus.Count, TdsSelectCommand, rowCount);

        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunResponseServerAsync(listener, response.WrittenMemory.ToArray());
        await using var connection = await MsSqlClient.ConnectAsync(TestOptions(port));
        await using var reader = await connection.ExecuteResultReaderAsync(
            "SELECT value; SELECT value",
            default,
            default);
        var results = (ISqlResultBoundaryReader)reader;
        // The server task ends once it has written the whole response.
        await server.WaitAsync(TimeSpan.FromSeconds(5));

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
            Assert.AreEqual(start == 1, await results.NextResultAsync());
        }

        Assert.AreEqual(-1, ((ISqlRecordsAffectedReader)reader).RecordsAffected);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task CommandQueuedBehindReaderRunsAfterTheReaderFinishesInline()
    {
        // The reader is a barrier, so the queued query is only sent once the reader's pump ends.
        // Here the pump ends inside the last ReadAsync, on the consumer's thread, and the scheduler
        // must still go on to send and complete the queued query.
        const int rowCount = 50;
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TaskCompletionSource firstWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = RunTwoQueryServerAsync(
            listener,
            BuildIntResult(Sequence(1, rowCount)),
            BuildIntResult(7),
            firstWritten);
        await using var connection = await MsSqlClient.ConnectAsync(TestOptions(port));
        await using var reader = await connection.ExecuteReaderAsync("SELECT value");
        var queued = connection.QueryAsync("SELECT 7 AS value").AsTask();

        Assert.IsTrue(await reader.ReadAsync());
        await firstWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var value = 2; value <= rowCount; value++)
        {
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(value, reader.GetInt32(0));
        }

        var end = reader.ReadAsync();
        Assert.IsTrue(end.IsCompleted, "Reading the received end of the result yielded.");
        Assert.IsFalse(await end);
        Assert.IsFalse(queued.IsFaulted);

        var rows = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(7, rows[0].GetInt32(0));
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task RunTwoQueryServerAsync(
        TcpListener listener,
        byte[] first,
        byte[] second,
        TaskCompletionSource firstWritten)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await LoginAsync(stream);
        TdsPacketReader reader = new(stream);
        using TdsPacketWriter writer = new(stream, 4096);
        foreach (var response in new[] { first, second })
        {
            var query = await reader.ReadMessageAsync(default);
            Assert.AreEqual(TdsMessageType.SqlBatch, query.Type);
            await writer.WriteMessageAsync(TdsMessageType.TabularResult, response, default);
            firstWritten.TrySetResult();
        }
    }

    private static int[] Sequence(int start, int count) =>
        Enumerable.Range(start, count).ToArray();

    private static byte[] BuildNVarCharResult(string[] values)
    {
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        response.WriteUInt32LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteByte(TdsDataType.NVarChar);
        response.WriteUInt16LittleEndian(40);
        response.WriteUInt32LittleEndian(0x0409);
        response.WriteByte(0);
        response.WriteBVarChar("value");
        foreach (var value in values)
        {
            response.WriteByte(TdsTokenType.Row);
            response.WriteUInt16LittleEndian(checked((ushort)(value.Length * 2)));
            response.WriteUtf16(value);
        }

        WriteDone(response, 0);
        return response.WrittenMemory.ToArray();
    }

    private static async Task RunPacketizedResponseServerAsync(
        TcpListener listener,
        byte[] response,
        int packetPayload)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await LoginAsync(stream);
        var query = await new TdsPacketReader(stream).ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, query.Type);
        byte packetId = 1;
        for (var position = 0; position < response.Length; position += packetPayload)
        {
            var count = Math.Min(packetPayload, response.Length - position);
            await WritePacketAsync(
                stream,
                response.AsMemory(position, count),
                position + count == response.Length,
                packetId++);
        }
    }

    private static async Task RunAttentionDuringRowsServerAsync(TcpListener listener, int rowCount)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await LoginAsync(stream);
        TdsPacketReader reader = new(stream);
        var query = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, query.Type);

        // The rows arrive in a packet that does not end the response, so the client must send an
        // attention and drain until the server acknowledges it.
        var rows = BuildIntResult(Sequence(1, rowCount));
        await WritePacketAsync(stream, rows.AsMemory(0, rows.Length - DoneTokenLength), false, 1);
        var attention = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.Attention, attention.Type);
        await WritePacketAsync(stream, BuildAttentionAck(), true, 2);

        var second = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, second.Type);
        using TdsPacketWriter writer = new(stream, 4096);
        await writer.WriteMessageAsync(TdsMessageType.TabularResult, BuildIntResult(7), default);
    }
}

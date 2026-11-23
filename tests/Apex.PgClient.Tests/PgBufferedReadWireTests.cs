using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Apex.SqlClient;
using static Apex.PgClient.Tests.PgConnectionWireTests;

namespace Apex.PgClient.Tests;

/// <summary>
/// Rows already in the receive buffer are read by ReadAsync itself, without waking the pump; the
/// rest of the stream still goes through the pump.
/// </summary>
[TestClass]
public sealed class PgBufferedReadWireTests
{
    [TestMethod]
    public async Task ReaderReadsReceivedRowsWithoutYielding()
    {
        const int rowCount = 200;
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        TaskCompletionSource written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = RunServerAsync(listener, [Result(1, rowCount)], chunkSize: int.MaxValue, written);
        await using var connection = await PgClient.ConnectAsync(CreateOptions(listener));
        await using (var reader = await connection.ExecuteReaderAsync("SELECT value"))
        {
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(1, reader.GetInt32(0));
            await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var value = 2; value <= rowCount; value++)
            {
                var read = reader.ReadAsync();
                Assert.IsTrue(read.IsCompleted, $"Reading received row {value} yielded.");
                Assert.IsTrue(await read);
                Assert.AreEqual(value, reader.GetInt32(0));
            }

            // CommandComplete and ReadyForQuery are also received, so the pump finishes inline.
            var end = reader.ReadAsync();
            Assert.IsTrue(end.IsCompleted, "Reading the received end of the result yielded.");
            Assert.IsFalse(await end);
        }

        await connection.DisposeAsync();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(64)]
    public async Task ReaderReadsRowsArrivingInPiecesInOrder(int chunkSize)
    {
        const int rowCount = 300;
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var server = RunServerAsync(listener, [Result(1, rowCount)], chunkSize);
        await using var connection = await PgClient.ConnectAsync(CreateOptions(listener));
        await using (var reader = await connection.ExecuteReaderAsync("SELECT value"))
        {
            for (var value = 1; value <= rowCount; value++)
            {
                Assert.IsTrue(await reader.ReadAsync(), $"Row {value} is missing.");
                Assert.AreEqual(value, reader.GetInt32(0));
            }

            Assert.IsFalse(await reader.ReadAsync());
        }

        await connection.DisposeAsync();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ServerErrorAfterReceivedRowsFailsTheNextReadAndKeepsTheConnection()
    {
        const int rowCount = 40;
        var failing = Join(
            Message((byte)'1', []),
            Message((byte)'2', []),
            Message((byte)'T', Join(Int16(1), Column("value", 23, 4))),
            Rows(1, rowCount),
            Message((byte)'E', Join(
                [(byte)'S'], CString("ERROR"),
                [(byte)'V'], CString("ERROR"),
                [(byte)'C'], CString("22012"),
                [(byte)'M'], CString("division by zero"),
                [0])),
            Message((byte)'Z', [(byte)'I']));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var server = RunServerAsync(listener, [failing, Result(7, 1)], chunkSize: int.MaxValue);
        await using var connection = await PgClient.ConnectAsync(CreateOptions(listener));
        var reader = await connection.ExecuteReaderAsync("SELECT value");
        for (var value = 1; value <= rowCount; value++)
        {
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(value, reader.GetInt32(0));
        }

        var exception = await Assert.ThrowsAsync<PgException>(() => reader.ReadAsync().AsTask());
        Assert.AreEqual("22012", exception.SqlState);
        try
        {
            await reader.DisposeAsync();
        }
        catch (PgException)
        {
        }

        await using (var next = await connection.ExecuteReaderAsync("SELECT value"))
        {
            Assert.IsTrue(await next.ReadAsync());
            Assert.AreEqual(7, next.GetInt32(0));
            Assert.IsFalse(await next.ReadAsync());
        }

        await connection.DisposeAsync();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task InvalidReceivedRowIsLeftForThePumpToReport()
    {
        // The second DataRow has two values for a one-column result. The inline read must leave it
        // unread, so the pump reads it again and fails the read, instead of skipping it.
        var invalid = Join(
            Message((byte)'1', []),
            Message((byte)'2', []),
            Message((byte)'T', Join(Int16(1), Column("value", 23, 4))),
            Message((byte)'D', DataRow("1")),
            Message((byte)'D', DataRow("2", "3")),
            Message((byte)'D', DataRow("4")),
            Message((byte)'C', CString("SELECT 3")),
            Message((byte)'Z', [(byte)'I']));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        TaskCompletionSource written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = RunServerAsync(listener, [invalid], chunkSize: int.MaxValue, written);
        var connection = await PgClient.ConnectAsync(CreateOptions(listener));
        try
        {
            var reader = await connection.ExecuteReaderAsync("SELECT value");
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(1, reader.GetInt32(0));
            await written.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync().AsTask());
            StringAssert.Contains(exception.Message, "2 values");
            await Assert.ThrowsAsync<InvalidDataException>(() => reader.DisposeAsync().AsTask());
        }
        finally
        {
            await connection.DisposeAsync();
        }

        // The connection may close without Terminate after a protocol failure; only the client's
        // behavior is under test.
        await Task.WhenAny(server, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task DisposingBetweenReceivedRowsDrainsAndReusesTheConnection()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var server = RunServerAsync(listener, [Result(1, 50), Result(7, 1)], chunkSize: int.MaxValue);
        await using var connection = await PgClient.ConnectAsync(CreateOptions(listener));
        await using (var reader = await connection.ExecuteReaderAsync("SELECT value"))
        {
            for (var value = 1; value <= 5; value++)
            {
                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual(value, reader.GetInt32(0));
            }
        }

        await using (var next = await connection.ExecuteReaderAsync("SELECT value"))
        {
            Assert.IsTrue(await next.ReadAsync());
            Assert.AreEqual(7, next.GetInt32(0));
            Assert.IsFalse(await next.ReadAsync());
        }

        await connection.DisposeAsync();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task ResultReaderReadsReceivedRowsOfEachResult()
    {
        const int rowCount = 30;
        // Two statements of one extended-protocol batch: each ends with CommandComplete, and one
        // ReadyForQuery follows the last.
        var batch = Join(
            Message((byte)'1', []),
            Message((byte)'2', []),
            Message((byte)'T', Join(Int16(1), Column("value", 23, 4))),
            Rows(1, rowCount),
            Message((byte)'C', CString($"SELECT {rowCount}")),
            Message((byte)'T', Join(Int16(1), Column("value", 23, 4))),
            Rows(101, rowCount),
            Message((byte)'C', CString($"SELECT {rowCount}")),
            Message((byte)'Z', [(byte)'I']));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        TaskCompletionSource written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = RunServerAsync(listener, [batch], chunkSize: int.MaxValue, written);
        await using var connection = await PgClient.ConnectAsync(CreateOptions(listener));
        await using (var reader = await connection.ExecuteResultReaderAsync("SELECT value", default, default))
        {
            var results = (ISqlResultBoundaryReader)reader;
            await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        }

        await connection.DisposeAsync();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [DataRow(0, int.MaxValue)]
    [DataRow(2, int.MaxValue)]
    [DataRow(2, 7)]
    public async Task NextResultSkipsTheUnreadRowsOfTheCurrentResult(int rowsReadFirst, int chunkSize)
    {
        // With small chunks, most of the first result is still arriving while it is skipped.
        const int rowCount = 30;
        var batch = Join(
            Message((byte)'1', []),
            Message((byte)'2', []),
            Message((byte)'T', Join(Int16(1), Column("value", 23, 4))),
            Rows(1, rowCount),
            Message((byte)'C', CString($"SELECT {rowCount}")),
            Message((byte)'T', Join(Int16(1), Column("value", 23, 4))),
            Rows(101, rowCount),
            Message((byte)'C', CString($"SELECT {rowCount}")),
            Message((byte)'Z', [(byte)'I']));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var server = RunServerAsync(listener, [batch], chunkSize);
        await using var connection = await PgClient.ConnectAsync(CreateOptions(listener));
        await using (var reader = await connection.ExecuteResultReaderAsync("SELECT value", default, default))
        {
            var results = (ISqlResultBoundaryReader)reader;
            Assert.IsTrue(await results.InitializeAsync());
            for (var value = 2; value <= 1 + rowsReadFirst; value++)
            {
                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual(value, reader.GetInt32(0));
            }

            // Bounded so the former hang (the pump parked on the next row of the first result)
            // fails fast.
            Assert.IsTrue(await results.NextResultAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(await results.InitializeAsync());
            for (var value = 101; value < 101 + rowCount; value++)
            {
                Assert.AreEqual(value, reader.GetInt32(0));
                Assert.AreEqual(value < 100 + rowCount, await reader.ReadAsync());
            }

            Assert.IsFalse(await results.NextResultAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }

        await connection.DisposeAsync();
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static PgConnectOptions CreateOptions(TcpListener listener) => new()
    {
        Host = "127.0.0.1",
        Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        Username = "user",
        Password = "pass",
        Database = "db",
    };

    // Serves one extended-protocol query per response, writing each response in chunks of
    // chunkSize bytes, then expects Terminate. firstWritten completes once the first response is
    // written in full.
    private static async Task RunServerAsync(
        TcpListener listener,
        byte[][] responses,
        int chunkSize,
        TaskCompletionSource? firstWritten = null)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await ReadStartupAsync(stream);
        await WriteStartupCompleteAsync(stream);
        foreach (var response in responses)
        {
            foreach (var expected in "PBDES")
            {
                var (type, _) = await ReadMessageAsync(stream);
                Assert.AreEqual((byte)expected, type);
            }

            for (var position = 0; position < response.Length; position += chunkSize)
            {
                await stream.WriteAsync(response.AsMemory(position, Math.Min(chunkSize, response.Length - position)));
                await stream.FlushAsync();
            }

            firstWritten?.TrySetResult();
        }

        var (terminate, _) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'X', terminate);
    }

    private static byte[] Result(int start, int count) =>
        Join(
            Message((byte)'1', []),
            Message((byte)'2', []),
            Message((byte)'T', Join(Int16(1), Column("value", 23, 4))),
            Rows(start, count),
            Message((byte)'C', CString($"SELECT {count}")),
            Message((byte)'Z', [(byte)'I']));

    private static byte[] Rows(int start, int count) =>
        Join(Enumerable.Range(start, count)
            .Select(static value => Message(
                (byte)'D',
                DataRow(value.ToString(CultureInfo.InvariantCulture))))
            .ToArray());

    private static byte[] Message(byte type, byte[] payload)
    {
        var frame = new byte[payload.Length + 5];
        frame[0] = type;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + 4);
        payload.CopyTo(frame, 5);
        return frame;
    }
}

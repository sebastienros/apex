using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Apex.SqlClient;
using static Apex.PgClient.Tests.PgConnectionWireTests;

namespace Apex.PgClient.Tests;

[TestClass]
public sealed class PgCancellationAndSubscriberWireTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // PostgreSQL only guarantees the backend was signalled once the postmaster closes the cancel
    // connection. Until then a cancel aimed at command A can still land on the next command B, so
    // B must not be sent before that EOF.
    [TestMethod]
    [DataRow("simple")]
    [DataRow("extended")]
    [DataRow("batch")]
    [DataRow("reader")]
    public async Task NextCommandWaitsUntilTheCancelRequestHasBeenProcessed(string path)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TaskCompletionSource commandReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = RunCancelServerAsync(listener, commandReceived);

        await using (var connection = await PgClient.ConnectAsync(CreateOptions(port)))
        {
            using CancellationTokenSource cancellation = new();
            var token = cancellation.Token;
            Task first = path switch
            {
                "simple" => connection.QueryAsync("SELECT pg_sleep(10)", token).AsTask(),
                "extended" => connection.QueryAsync(
                    "SELECT pg_sleep($1)",
                    SqlParameters.Create(10),
                    token).AsTask(),
                "batch" => ExecuteBatchAsync(connection, token),
                "reader" => ReadAsync(connection, token),
                _ => throw new ArgumentOutOfRangeException(nameof(path)),
            };

            await commandReceived.Task.WaitAsync(Timeout);
            cancellation.Cancel();
            var second = connection.QueryAsync("SELECT 'after'").AsTask();

            await Assert.ThrowsAsync<OperationCanceledException>(() => first.WaitAsync(Timeout));
            Assert.AreEqual("after", (await second.WaitAsync(Timeout))[0].Get<string>(0));
        }

        Assert.IsFalse(
            await server.WaitAsync(Timeout),
            "The next command was sent before the server closed the cancel connection.");
    }

    [TestMethod]
    public async Task CancelRequestReturnsOnlyAfterTheServerClosesTheCancelConnection()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TaskCompletionSource closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ReadStartupAsync(stream);
            await WriteStartupCompleteAsync(stream);

            using (var cancelClient = await listener.AcceptTcpClientAsync())
            {
                await ReadCancelRequestAsync(cancelClient.GetStream());
                await Task.Delay(300);
                closing.SetResult();
            }

            var (terminate, _) = await ReadMessageAsync(stream);
            Assert.AreEqual((byte)'X', terminate);
        });

        await using (var connection = await PgClient.ConnectAsync(CreateOptions(port)))
        {
            await connection.CancelRequestAsync().AsTask().WaitAsync(Timeout);
            Assert.IsTrue(closing.Task.IsCompleted, "CancelRequestAsync returned before EOF.");
        }

        await server.WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task SubscriberCompletesNotificationsWhenTheListenLoopFailsNonFatally()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ReadStartupAsync(stream);
            await WriteStartupCompleteAsync(stream);
            // An ERROR (not FATAL) while idle leaves the session usable but is not a notification.
            await WriteMessageAsync(stream, (byte)'E', ErrorFields("ERROR", "XX000", "boom"));

            var (terminate, _) = await ReadMessageAsync(stream);
            return terminate;
        });

        var subscriber = await PgSubscriber.ConnectAsync(CreateOptions(port));
        var error = await Assert.ThrowsExactlyAsync<PgException>(
            () => DrainAsync(subscriber.Notifications).WaitAsync(Timeout));
        Assert.AreEqual("XX000", error.SqlState);

        await subscriber.DisposeAsync().AsTask().WaitAsync(Timeout);
        Assert.AreEqual((byte)'X', await server.WaitAsync(Timeout));
    }

    [TestMethod]
    public async Task SubscriberCompletesNotificationsWhenTheReconnectPolicyThrows()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ReadStartupAsync(stream);
            await WriteStartupCompleteAsync(stream);
            // Dropping the session is fatal, so the subscriber consults its reconnect policy.
        });

        var subscriber = await PgSubscriber.ConnectAsync(
            CreateOptions(port),
            _ => throw new InvalidOperationException("policy failed"));
        await server.WaitAsync(Timeout);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => DrainAsync(subscriber.Notifications).WaitAsync(Timeout));
        Assert.AreEqual("policy failed", error.Message);
        await subscriber.DisposeAsync().AsTask().WaitAsync(Timeout);
    }

    private static async Task ExecuteBatchAsync(PgConnection connection, CancellationToken token)
    {
        PgBatch batch = new();
        batch.Add("SELECT pg_sleep(10)");
        _ = await connection.ExecuteBatchAsync(batch, token);
    }

    private static async Task ReadAsync(PgConnection connection, CancellationToken token)
    {
        await using var reader = await connection.ExecuteReaderAsync(
            "SELECT pg_sleep(10)",
            default,
            token);
        _ = await reader.ReadAsync(token);
    }

    private static async Task DrainAsync(IAsyncEnumerable<PgNotification> notifications)
    {
        await foreach (var _ in notifications)
        {
        }
    }

    // Returns true when the next command arrived while the cancel connection was still open.
    private static async Task<bool> RunCancelServerAsync(
        TcpListener listener,
        TaskCompletionSource commandReceived)
    {
        using CancellationTokenSource timeout = new(Timeout);
        var token = timeout.Token;
        using var client = await listener.AcceptTcpClientAsync(token);
        await using var stream = client.GetStream();
        await ReadStartupAsync(stream, token);
        await WriteStartupCompleteAsync(stream, token);

        byte type;
        do
        {
            (type, _) = await ReadMessageAsync(stream, token);
        }
        while (type is not ((byte)'Q' or (byte)'S'));

        commandReceived.SetResult();

        bool sentEarly;
        Task<(byte Type, byte[] Payload)> next;
        using (var cancelClient = await listener.AcceptTcpClientAsync(token))
        {
            await ReadCancelRequestAsync(cancelClient.GetStream(), token);
            await WriteMessageAsync(
                stream,
                (byte)'E',
                ErrorFields("ERROR", "57014", "canceling statement due to user request"),
                token);
            await WriteMessageAsync(stream, (byte)'Z', [(byte)'I'], token);

            next = ReadMessageAsync(stream, token);
            sentEarly = await Task.WhenAny(next, Task.Delay(300, token)) == next;
        }

        (type, _) = await next;
        Assert.AreEqual((byte)'Q', type);
        await WriteSingleTextResultAsync(stream, "after", token);

        (type, _) = await ReadMessageAsync(stream, token);
        Assert.AreEqual((byte)'X', type);
        return sentEarly;
    }

    private static async Task ReadCancelRequestAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var request = new byte[16];
        await stream.ReadExactlyAsync(request, cancellationToken);
        Assert.AreEqual(16, BinaryPrimitives.ReadInt32BigEndian(request));
        Assert.AreEqual(80877102, BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(4)));
        Assert.AreEqual(123, BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(8)));
        Assert.AreEqual(456, BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(12)));
    }

    private static byte[] ErrorFields(string severity, string sqlState, string message) =>
        Join(
            [(byte)'S'], CString(severity),
            [(byte)'V'], CString(severity),
            [(byte)'C'], CString(sqlState),
            [(byte)'M'], CString(message),
            [0]);

    private static PgConnectOptions CreateOptions(int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Username = "user",
        Password = "pass",
        Database = "db",
    };
}

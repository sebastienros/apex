using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Transactions;
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

        // A stopped subscriber must say so rather than accept channels it will never serve.
        var stopped = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => subscriber.SubscribeAsync("orders").AsTask().WaitAsync(Timeout));
        Assert.AreSame(error, stopped.InnerException);

        await subscriber.DisposeAsync().AsTask().WaitAsync(Timeout);
        Assert.AreEqual((byte)'X', await server.WaitAsync(Timeout));

        // Reading Notifications after dispose ends the sequence; it must not fault the getter.
        var afterDispose = await Assert.ThrowsAsync<Exception>(
            () => DrainAsync(subscriber.Notifications).WaitAsync(Timeout));
        Assert.IsNotInstanceOfType<ObjectDisposedException>(afterDispose);
    }

    // The connection drops while Prepare runs COMMIT: every resource must roll back, and the
    // scope must report an abort rather than leak the I/O error from a phase-2 notification.
    [TestMethod]
    public async Task AmbientCommitOnALostConnectionRollsBackOtherResources()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunAmbientServerAsync(listener, failStatement: false);

        await using var connection = await PgClient.ConnectAsync(CreateOptions(port));
        RecordingEnlistment other = new();
        await Assert.ThrowsExactlyAsync<TransactionAbortedException>(async () =>
        {
            using TransactionScope scope = new(TransactionScopeAsyncFlowOption.Enabled);
            await connection.EnlistTransactionAsync(Transaction.Current!);
            Transaction.Current!.EnlistVolatile(other, EnlistmentOptions.None);
            await connection.ExecuteAsync("INSERT INTO t VALUES (1)");
            scope.Complete();
        });

        Assert.AreEqual("rollback", other.Outcome);
        Assert.AreEqual("COMMIT", await server.WaitAsync(Timeout));
    }

    // The session drops inside the scope: disposing it uncompleted must not throw from the
    // Rollback notification, since PostgreSQL already rolled back on disconnect.
    [TestMethod]
    public async Task UncompletedScopeOnALostConnectionDisposesCleanly()
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
            _ = await ReadMessageAsync(stream);
            await WriteMessageAsync(stream, (byte)'C', CString("BEGIN"));
            await WriteMessageAsync(stream, (byte)'Z', [(byte)'T']);
            _ = await ReadMessageAsync(stream);
        });

        await using var connection = await PgClient.ConnectAsync(CreateOptions(port));
        using (TransactionScope scope = new(TransactionScopeAsyncFlowOption.Enabled))
        {
            await connection.EnlistTransactionAsync(Transaction.Current!);
            await Assert.ThrowsAsync<IOException>(
                () => connection.ExecuteAsync("INSERT INTO t VALUES (1)").AsTask().WaitAsync(Timeout));
        }

        await server.WaitAsync(Timeout);
    }

    // A failed transaction can only roll back, even if the session is lost while ending it.
    [TestMethod]
    public async Task FailedAmbientTransactionOnALostConnectionIsAbortedNotInDoubt()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunAmbientServerAsync(listener, failStatement: true);

        await using var connection = await PgClient.ConnectAsync(CreateOptions(port));
        var aborted = await Assert.ThrowsExactlyAsync<TransactionAbortedException>(async () =>
        {
            using TransactionScope scope = new(TransactionScopeAsyncFlowOption.Enabled);
            await connection.EnlistTransactionAsync(Transaction.Current!);
            await Assert.ThrowsExactlyAsync<PgException>(
                () => connection.ExecuteAsync("INSERT INTO t VALUES (1)").AsTask());
            scope.Complete();
        });

        Assert.IsInstanceOfType<TransactionException>(aborted.InnerException);
        Assert.AreEqual("ROLLBACK", await server.WaitAsync(Timeout));
    }

    // Answers BEGIN and one statement, then reads the closing command and drops the session.
    private static async Task<string> RunAmbientServerAsync(TcpListener listener, bool failStatement)
    {
        using CancellationTokenSource timeout = new(Timeout);
        var token = timeout.Token;
        using var client = await listener.AcceptTcpClientAsync(token);
        await using var stream = client.GetStream();
        await ReadStartupAsync(stream, token);
        await WriteStartupCompleteAsync(stream, token);

        var (type, payload) = await ReadMessageAsync(stream, token);
        Assert.AreEqual((byte)'Q', type);
        StringAssert.StartsWith(FirstCStringValue(payload), "BEGIN");
        await WriteMessageAsync(stream, (byte)'C', CString("BEGIN"), token);
        await WriteMessageAsync(stream, (byte)'Z', [(byte)'T'], token);

        (type, _) = await ReadMessageAsync(stream, token);
        Assert.AreEqual((byte)'Q', type);
        if (failStatement)
        {
            await WriteMessageAsync(stream, (byte)'E', ErrorFields("ERROR", "23505", "duplicate"), token);
            await WriteMessageAsync(stream, (byte)'Z', [(byte)'E'], token);
        }
        else
        {
            await WriteMessageAsync(stream, (byte)'C', CString("INSERT 0 1"), token);
            await WriteMessageAsync(stream, (byte)'Z', [(byte)'T'], token);
        }

        (type, payload) = await ReadMessageAsync(stream, token);
        Assert.AreEqual((byte)'Q', type);
        return FirstCStringValue(payload);
    }

    private sealed class RecordingEnlistment : IEnlistmentNotification
    {
        public string? Outcome { get; private set; }

        public void Prepare(PreparingEnlistment preparingEnlistment) =>
            preparingEnlistment.Prepared();

        public void Commit(Enlistment enlistment)
        {
            Outcome = "commit";
            enlistment.Done();
        }

        public void Rollback(Enlistment enlistment)
        {
            Outcome = "rollback";
            enlistment.Done();
        }

        public void InDoubt(Enlistment enlistment)
        {
            Outcome = "indoubt";
            enlistment.Done();
        }
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

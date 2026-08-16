using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Apex.SqlClient;

namespace Apex.PgClient.Tests;

[TestClass]
public sealed class PgConnectionWireTests
{
    [TestMethod]
    public async Task ConnectsAndExecutesSimpleQueryAgainstProtocolServer()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunServerAsync(listener);

        await using var connection = await PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "user",
            Password = "pass",
            Database = "db",
        });

        var result = await connection.QueryAsync(
            "SELECT 1 AS id, 'hello' AS message");

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(1, result[0].Get<int>("id"));
        Assert.AreEqual("hello", result[0].Get<string>("message"));
        Assert.AreEqual(16, connection.DatabaseMetadata.MajorVersion);

        await connection.DisposeAsync();
        await server;
        listener.Stop();
    }

    [TestMethod]
    public async Task PoolDoesNotRecycleConnectionWithActiveTransaction()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunPoolServerAsync(listener);
        await using PgPool pool = PgPool.Create(
            new PgConnectOptions
            {
                Host = "127.0.0.1",
                Port = port,
                Username = "user",
                Password = "pass",
                Database = "db",
            },
            new SqlPoolOptions
            {
                MaximumSize = 1,
                AcquisitionTimeout = TimeSpan.FromSeconds(2),
            });

        var first = await pool.GetConnectionAsync();
        var transaction = await first.BeginTransactionAsync();
        await first.DisposeAsync();

        var pending = pool.GetConnectionAsync().AsTask();
        await Task.Delay(50);
        Assert.IsFalse(pending.IsCompleted);

        await transaction.RollbackAsync();
        var second = await pending;
        await second.DisposeAsync();
        await pool.DisposeAsync();
        await server;
        listener.Stop();
    }

    [TestMethod]
    public async Task PreparesExecutesAndClosesStatement()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunPreparedServerAsync(listener);

        await using var connection = await PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "user",
            Password = "pass",
            Database = "db",
        });
        await using var statement =
          await connection.PrepareAsync("SELECT $1::int4 AS id");

        var rows = await statement.QueryAsync(SqlParameters.Create(7));
        Assert.AreEqual(7, rows[0].Get<int>("id"));

        try
        {
            await statement.DisposeAsync();
        }
        catch
        {
            await server;
            throw;
        }
        await connection.DisposeAsync();
        await server;
        listener.Stop();
    }

    [TestMethod]
    public async Task PoolDiscardsConnectionLeftInRawTransaction()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunRawTransactionPoolServerAsync(listener);
        await using PgPool pool = PgPool.Create(
          new PgConnectOptions
          {
              Host = "127.0.0.1",
              Port = port,
              Username = "user",
              Password = "pass",
              Database = "db",
          },
          new SqlPoolOptions { MaximumSize = 1 });

        var first = await pool.GetConnectionAsync();
        await first.ExecuteAsync("BEGIN");
        await first.DisposeAsync();

        var second = await pool.GetConnectionAsync();
        await second.DisposeAsync();
        await pool.DisposeAsync();
        await server;
        listener.Stop();
    }

    [TestMethod]
    public async Task PoolRejectsWaiterWhenWaitQueueIsDisabled()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunIdlePoolServerAsync(listener);
        await using PgPool pool = PgPool.Create(
          new PgConnectOptions
          {
              Host = "127.0.0.1",
              Port = port,
              Username = "user",
              Password = "pass",
              Database = "db",
          },
          new SqlPoolOptions
          {
              MaximumSize = 1,
              MaximumWaitQueueSize = 0,
          });

        var lease = await pool.GetConnectionAsync();
        await Assert.ThrowsExactlyAsync<SqlClientException>(
          () => pool.GetConnectionAsync().AsTask());
        await lease.DisposeAsync();
        await pool.DisposeAsync();
        await server;
        listener.Stop();
    }

    [TestMethod]
    public async Task ConnectsOverUnixDomainSocket()
    {
        var directory = Path.Combine(Path.GetTempPath(), "apex-pg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var socketPath = Path.Combine(directory, ".s.PGSQL.5432");
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen();
        var server = RunUnixServerAsync(listener);

        try
        {
            await using var connection = await PgClient.ConnectAsync(new PgConnectOptions
            {
                Host = directory,
                Port = 5432,
                Username = "user",
                Password = "pass",
                Database = "db",
            });

            Assert.AreEqual(16, connection.DatabaseMetadata.MajorVersion);
            await connection.DisposeAsync();
            await server;
        }
        finally
        {
            listener.Dispose();
            if (File.Exists(socketPath))
            {
                File.Delete(socketPath);
            }

            Directory.Delete(directory);
        }
    }

    [TestMethod]
    public async Task AbruptCloseFailsAllPipelinedCommands()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunAbruptCloseServerAsync(listener);
        await using var connection = await PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "user",
            Password = "pass",
            Database = "db",
            PipeliningLimit = 8,
        });

        var queries = Enumerable.Range(0, 32)
          .Select(index => connection.QueryAsync($"SELECT {index}::int4").AsTask())
          .ToArray();
        await Assert.ThrowsAsync<Exception>(() => Task.WhenAll(queries));

        Assert.IsTrue(queries.All(static query => query.IsFaulted || query.IsCanceled));
        await server;
        listener.Stop();
    }

    private static async Task RunServerAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();

        var startupLength = new byte[4];
        await stream.ReadExactlyAsync(startupLength);
        var startupPayloadLength = BinaryPrimitives.ReadInt32BigEndian(startupLength) - 4;
        var startup = new byte[startupPayloadLength];
        await stream.ReadExactlyAsync(startup);
        Assert.AreEqual(196608, BinaryPrimitives.ReadInt32BigEndian(startup));

        await WriteMessageAsync(stream, (byte)'R', Int32(0));
        await WriteMessageAsync(stream, (byte)'S', Join(CString("server_version"), CString("16.4")));
        await WriteMessageAsync(stream, (byte)'K', Join(Int32(123), Int32(456)));
        await WriteMessageAsync(stream, (byte)'Z', [(byte)'I']);

        (var type, var payload) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'Q', type);
        Assert.AreEqual("SELECT 1 AS id, 'hello' AS message", CStringValue(payload));

        await WriteMessageAsync(stream, (byte)'T', RowDescription());
        await WriteMessageAsync(stream, (byte)'D', DataRow("1", "hello"));
        await WriteMessageAsync(stream, (byte)'C', CString("SELECT 1"));
        await WriteMessageAsync(stream, (byte)'Z', [(byte)'I']);

        (type, payload) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'X', type);
        Assert.AreEqual(0, payload.Length);
    }

    private static async Task RunPoolServerAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await ReadStartupAsync(stream);
        await WriteStartupCompleteAsync(stream);

        (var type, var payload) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'Q', type);
        Assert.AreEqual("BEGIN", CStringValue(payload));
        await WriteMessageAsync(stream, (byte)'C', CString("BEGIN"));
        await WriteMessageAsync(stream, (byte)'Z', [(byte)'T']);

        (type, payload) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'Q', type);
        Assert.AreEqual("ROLLBACK", CStringValue(payload));
        await WriteMessageAsync(stream, (byte)'C', CString("ROLLBACK"));
        await WriteMessageAsync(stream, (byte)'Z', [(byte)'I']);

        (type, payload) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'X', type);
        Assert.AreEqual(0, payload.Length);
    }

    private static async Task RunPreparedServerAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await ReadStartupAsync(stream);
        await WriteStartupCompleteAsync(stream);

        (var type, var payload) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'P', type);
        var statementName = FirstCStringValue(payload);
        Assert.IsTrue(statementName.StartsWith('A'));
        Assert.IsTrue(System.Text.Encoding.UTF8.GetString(payload).Contains("SELECT $1::int4 AS id"));
        (type, payload) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'D', type);
        Assert.AreEqual((byte)'S', payload[0]);
        Assert.AreEqual(statementName, CStringValue(payload[1..]));
        (type, _) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'S', type);
        await WriteMessageAsync(stream, (byte)'1', []);
        await WriteMessageAsync(stream, (byte)'t', Join(Int16(1), Int32(23)));
        await WriteMessageAsync(stream, (byte)'T', Join(Int16(1), Column("id", 23, 4)));
        await WriteMessageAsync(stream, (byte)'Z', [(byte)'I']);

        (type, _) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'B', type);
        (type, _) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'E', type);
        (type, _) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'S', type);
        await WriteMessageAsync(stream, (byte)'2', []);
        await WriteMessageAsync(stream, (byte)'D', DataRowBytes(Int32(7)));
        await WriteMessageAsync(stream, (byte)'C', CString("SELECT 1"));
        await WriteMessageAsync(stream, (byte)'Z', [(byte)'I']);

        (type, payload) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'C', type);
        Assert.AreEqual((byte)'S', payload[0]);
        Assert.AreEqual(statementName, CStringValue(payload[1..]));
        (type, _) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'S', type);
        await WriteMessageAsync(stream, (byte)'3', []);
        await WriteMessageAsync(stream, (byte)'Z', [(byte)'I']);

        (type, payload) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'X', type);
        Assert.AreEqual(0, payload.Length);
    }

    private static async Task RunRawTransactionPoolServerAsync(TcpListener listener)
    {
        using (var first = await listener.AcceptTcpClientAsync())
        {
            await using var stream = first.GetStream();
            await ReadStartupAsync(stream);
            await WriteStartupCompleteAsync(stream);
            (var type, var payload) = await ReadMessageAsync(stream);
            Assert.AreEqual((byte)'Q', type);
            Assert.AreEqual("BEGIN", CStringValue(payload));
            await WriteMessageAsync(stream, (byte)'C', CString("BEGIN"));
            await WriteMessageAsync(stream, (byte)'Z', [(byte)'T']);
            (type, payload) = await ReadMessageAsync(stream);
            Assert.AreEqual((byte)'X', type);
        }

        using var second = await listener.AcceptTcpClientAsync();
        await using var secondStream = second.GetStream();
        await ReadStartupAsync(secondStream);
        await WriteStartupCompleteAsync(secondStream);
        (var secondType, _) = await ReadMessageAsync(secondStream);
        Assert.AreEqual((byte)'X', secondType);
    }

    private static async Task RunIdlePoolServerAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await ReadStartupAsync(stream);
        await WriteStartupCompleteAsync(stream);
        (var type, _) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'X', type);
    }

    private static async Task RunUnixServerAsync(Socket listener)
    {
        using var accepted = await listener.AcceptAsync();
        await using NetworkStream stream = new(accepted, ownsSocket: false);
        await ReadStartupAsync(stream);
        await WriteStartupCompleteAsync(stream);
        (var type, _) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'X', type);
    }

    private static async Task RunAbruptCloseServerAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await ReadStartupAsync(stream);
        await WriteStartupCompleteAsync(stream);
        for (var i = 0; i < 5; i++)
        {
            (var type, _) = await ReadMessageAsync(stream);
            Assert.AreEqual((byte)'Q', type);
        }
    }

    private static async Task ReadStartupAsync(Stream stream)
    {
        var startupLength = new byte[4];
        await stream.ReadExactlyAsync(startupLength);
        var startupPayloadLength = BinaryPrimitives.ReadInt32BigEndian(startupLength) - 4;
        var startup = new byte[startupPayloadLength];
        await stream.ReadExactlyAsync(startup);
        Assert.AreEqual(196608, BinaryPrimitives.ReadInt32BigEndian(startup));
    }

    private static async Task WriteStartupCompleteAsync(Stream stream)
    {
        await WriteMessageAsync(stream, (byte)'R', Int32(0));
        await WriteMessageAsync(stream, (byte)'S', Join(CString("server_version"), CString("16.4")));
        await WriteMessageAsync(stream, (byte)'K', Join(Int32(123), Int32(456)));
        await WriteMessageAsync(stream, (byte)'Z', [(byte)'I']);
    }

    private static byte[] RowDescription() =>
        Join(
            Int16(2),
            Column("id", 23, 4),
            Column("message", 25, -1));

    private static byte[] Column(string name, int typeId, short typeSize) =>
        Join(
            CString(name),
            Int32(0),
            Int16(0),
            Int32(typeId),
            Int16(typeSize),
            Int32(-1),
            Int16(0));

    private static byte[] DataRow(params string[] values)
    {
        List<byte[]> parts = [Int16(checked((short)values.Length))];
        foreach (var value in values)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            parts.Add(Int32(bytes.Length));
            parts.Add(bytes);
        }

        return Join(parts.ToArray());
    }

    private static byte[] DataRowBytes(params byte[][] values)
    {
        List<byte[]> parts = [Int16(checked((short)values.Length))];
        foreach (var value in values)
        {
            parts.Add(Int32(value.Length));
            parts.Add(value);
        }

        return Join(parts.ToArray());
    }

    private static async Task WriteMessageAsync(Stream stream, byte type, byte[] payload)
    {
        var frame = new byte[payload.Length + 5];
        frame[0] = type;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + 4);
        payload.CopyTo(frame, 5);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    private static async Task<(byte Type, byte[] Payload)> ReadMessageAsync(Stream stream)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header);
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4;
        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload);
        return (header[0], payload);
    }

    private static byte[] CString(string value) =>
        [.. System.Text.Encoding.UTF8.GetBytes(value), 0];

    private static string CStringValue(byte[] value) =>
        System.Text.Encoding.UTF8.GetString(value.AsSpan(0, value.Length - 1));

    private static string FirstCStringValue(byte[] value)
    {
        var length = Array.IndexOf(value, (byte)0);
        return System.Text.Encoding.UTF8.GetString(value.AsSpan(0, length));
    }

    private static byte[] Int16(short value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Join(params byte[][] parts)
    {
        var result = new byte[parts.Sum(static part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}

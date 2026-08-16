using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Apex.MsSqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MsSqlClient.Tests;

[TestClass]
public sealed class MsSqlConnectionWireTests
{
    [TestMethod]
    public async Task ConnectsAndExecutesAgainstInMemoryProtocolServer()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunQueryServerAsync(listener);
        try
        {
            await using var connection = await MsSqlClient.ConnectAsync(
              TestOptions(port));
            var rows = await connection.QueryAsync("SELECT 42 AS value");

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(42, rows[0].GetInt32("value"));
            Assert.AreEqual(16, connection.DatabaseMetadata.MajorVersion);
            Assert.IsFalse(connection.IsSecure);
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

      [TestMethod]
      public async Task StrictEncryptionNegotiatesTds8Alpn()
      {
        using var certificate = CreateCertificate();
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunStrictTlsQueryServerAsync(listener, certificate);
        try
        {
          await using var connection = await MsSqlClient.ConnectAsync(
            TestOptions(port) with
            {
              EncryptionMode = MsSqlEncryptionMode.Strict,
              TrustServerCertificate = true,
            });

          Assert.IsTrue(connection.IsSecure);
          Assert.AreEqual(
            7,
            (await connection.QueryAsync("SELECT 7"))[0].GetInt32(0));
          await server;
        }
        finally
        {
          listener.Stop();
        }
      }

    [TestMethod]
    public async Task AttentionIsSentOnSameConnectionAndDrainedBeforeReuse()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TaskCompletionSource queryReceived =
          new(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = RunCancellationServerAsync(listener, queryReceived);
        try
        {
            await using var connection = await MsSqlClient.ConnectAsync(
              TestOptions(port));
            using CancellationTokenSource cancellation = new();
            var pending = connection.QueryAsync(
              "WAITFOR DELAY '00:01:00'",
              cancellation.Token).AsTask();
            await queryReceived.Task;
            await cancellation.CancelAsync();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => pending);
            var rows = await connection.QueryAsync("SELECT 7 AS value");
            Assert.AreEqual(7, rows[0].GetInt32(0));
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task BorrowedReaderAndSafeStreamHonorRowLifetimes()
    {
        TcpListener readerListener = new(IPAddress.Loopback, 0);
        readerListener.Start();
        var readerPort = ((IPEndPoint)readerListener.LocalEndpoint).Port;
        var readerServer = RunRowsServerAsync(readerListener, 1, 2);
        try
        {
            await using var connection = await MsSqlClient.ConnectAsync(
              TestOptions(readerPort));
            var reader = await connection.ExecuteReaderAsync("SELECT value");
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(1, reader.GetInt32(0));
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(2, reader.GetInt32(0));
            Assert.IsFalse(await reader.ReadAsync());
            await reader.DisposeAsync();
            Assert.ThrowsExactly<ObjectDisposedException>(() => reader.GetInt32(0));
            await readerServer;
        }
        finally
        {
            readerListener.Stop();
        }

        TcpListener streamListener = new(IPAddress.Loopback, 0);
        streamListener.Start();
        var streamPort = ((IPEndPoint)streamListener.LocalEndpoint).Port;
        var streamServer = RunRowsServerAsync(streamListener, 3, 4);
        try
        {
            await using var connection = await MsSqlClient.ConnectAsync(
              TestOptions(streamPort));
            List<SqlRow> safeRows = [];
            await foreach (var row in connection.StreamAsync("SELECT value", fetchSize: 1))
            {
                safeRows.Add(row);
            }

            Assert.AreEqual(3, safeRows[0].GetInt32(0));
            Assert.AreEqual(4, safeRows[1].GetInt32(0));
            await streamServer;
        }
        finally
        {
            streamListener.Stop();
        }
    }

    [TestMethod]
    public async Task BorrowedReaderCopiesReadOnlyMemoryValues()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunBinaryRowsServerAsync(
          listener,
          [1, 2, 3],
          [4, 5, 6]);
        try
        {
            await using var connection = await MsSqlClient.ConnectAsync(
              TestOptions(port));
            await using var reader =
              await connection.ExecuteReaderAsync("SELECT value");

            Assert.IsTrue(await reader.ReadAsync());
            var first = reader.Get<ReadOnlyMemory<byte>>(0);
            Assert.IsTrue(await reader.ReadAsync());
            var second = reader.Get<ReadOnlyMemory<byte>>(0);

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, first.ToArray());
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, second.ToArray());
            Assert.IsFalse(await reader.ReadAsync());
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task ConnectTimeoutIncludesStalledLoginResponse()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TaskCompletionSource loginReceived =
          new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release =
          new(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = RunStalledLoginServerAsync(
          listener,
          loginReceived,
          release.Task);
        try
        {
            var options = TestOptions(port) with
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(150),
            };
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
              () => MsSqlClient.ConnectAsync(options).AsTask());
            Assert.IsTrue(loginReceived.Task.IsCompleted);
            Assert.IsTrue(
              System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(3));
        }
        finally
        {
            release.TrySetResult();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task ReaderDeliversSplitRowBeforeEndOfMessage()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TaskCompletionSource firstRowWritten =
          new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseRemainder =
          new(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = RunSplitRowsServerAsync(
          listener,
          firstRowWritten,
          releaseRemainder.Task);
        try
        {
            await using var connection = await MsSqlClient.ConnectAsync(
              TestOptions(port));
            await using var reader =
              await connection.ExecuteReaderAsync("SELECT value");
            var firstRead = reader.ReadAsync().AsTask();
            await firstRowWritten.Task;

            var completed = await Task.WhenAny(firstRead, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.AreSame(firstRead, completed, "The first row waited for END_OF_MESSAGE.");
            Assert.IsTrue(await firstRead);
            Assert.AreEqual(1, reader.GetInt32(0));

            releaseRemainder.SetResult();
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(2, reader.GetInt32(0));
            Assert.IsFalse(await reader.ReadAsync());
            await server;
        }
        finally
        {
            releaseRemainder.TrySetResult();
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task StreamKeepsRowsWithTheirResultSetMetadata()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunMultiResultServerAsync(listener);
        try
        {
            await using var connection = await MsSqlClient.ConnectAsync(
              TestOptions(port));
            List<SqlRow> rows = [];
            await foreach (var row in connection.StreamAsync("SELECT 1; SELECT N'x'"))
            {
                rows.Add(row);
            }

            Assert.HasCount(2, rows);
            Assert.AreEqual(0, rows[0].GetOrdinal("a"));
            Assert.AreEqual(1, rows[0].GetInt32(0));
            Assert.AreEqual(0, rows[1].GetOrdinal("b"));
            Assert.AreEqual("x", rows[1].GetString(0));
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task ReaderDecodesPlpValueSplitAcrossPackets()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunFragmentedJsonServerAsync(listener);
        try
        {
            await using var connection = await MsSqlClient.ConnectAsync(
              TestOptions(port));
            await using var reader =
              await connection.ExecuteReaderAsync("SELECT payload");

            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual("""{"split":true}""", reader.GetString(0));
            Assert.IsFalse(await reader.ReadAsync());
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task PreparedLifecycleUsesOnePrepexecThenExecuteAndUnprepare()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunPreparedLifecycleServerAsync(listener, fragmentedFirst: false);
        try
        {
            await using var connection = await MsSqlClient.ConnectAsync(
              TestOptions(port));
            var unused = await connection.PrepareAsync("SELECT @P1");
            await unused.DisposeAsync();

            var statement = await connection.PrepareAsync("SELECT @P1");
            var first =
              statement.QueryAsync(SqlParameters.Create(41)).AsTask();
            var second =
              statement.QueryAsync(SqlParameters.Create(42)).AsTask();
            var dispose = statement.DisposeAsync().AsTask();
            var results = await Task.WhenAll(first, second);

            Assert.AreEqual(41, results[0][0].GetInt32(0));
            Assert.AreEqual(42, results[1][0].GetInt32(0));
            await dispose;
            Assert.AreEqual(
              7,
              (await connection.QueryAsync("SELECT 7"))[0].GetInt32(0));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
              () => statement.QueryAsync(SqlParameters.Create(43)).AsTask());
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task FirstPreparedCollectorCapturesFragmentedReturnHandle()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunPreparedLifecycleServerAsync(listener, fragmentedFirst: true);
        try
        {
            await using var connection = await MsSqlClient.ConnectAsync(
              TestOptions(port));
            var statement = await connection.PrepareAsync("SELECT @P1");
            List<int> collected = [];
            await statement.CollectAsync(
                collected,
                static (values, row) => values.Add(row.GetInt32(0)),
                SqlParameters.Create(41));
            CollectionAssert.AreEqual(new[] { 41 }, collected);

            var second = await statement.QueryAsync(SqlParameters.Create(42));
            Assert.AreEqual(42, second[0].GetInt32(0));
            await statement.DisposeAsync();
            Assert.AreEqual(
              7,
              (await connection.QueryAsync("SELECT 7"))[0].GetInt32(0));
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task ReaderBreaksConnectionWhenIncrementalParsingFails()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunUnsupportedTypeServerAsync(listener);
        try
        {
            await using var connection = await MsSqlClient.ConnectAsync(
              TestOptions(port));
            var reader =
              await connection.ExecuteReaderAsync("SELECT CAST(1 AS sql_variant)");

            await Assert.ThrowsExactlyAsync<NotSupportedException>(
              () => reader.ReadAsync().AsTask());
            Assert.IsFalse(connection.IsUsable);
            await Assert.ThrowsExactlyAsync<NotSupportedException>(
              () => reader.DisposeAsync().AsTask());
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static MsSqlConnectOptions TestOptions(int port) =>
      new()
      {
          Host = "127.0.0.1",
          Port = port,
          Username = "sa",
          Password = "password",
          Database = "master",
          EncryptionMode = MsSqlEncryptionMode.Disable,
          ConnectTimeout = TimeSpan.FromSeconds(5),
      };

    private static async Task RunQueryServerAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await LoginAsync(stream);
        var query = await new TdsPacketReader(stream).ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, query.Type);
        using TdsPacketWriter writer = new(stream, 4096);
        await writer.WriteMessageAsync(
          TdsMessageType.TabularResult,
          BuildIntResult(42),
          default);
    }

    private static async Task RunStrictTlsQueryServerAsync(
        TcpListener listener,
        X509Certificate2 certificate)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var network = client.GetStream();
        await using SslStream tls = new(network, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(
          new SslServerAuthenticationOptions
          {
              ServerCertificate = certificate,
              EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
              ApplicationProtocols = [new SslApplicationProtocol("tds/8.0")],
          });
        TdsPacketReader reader = new(tls);
        var preLogin = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.PreLogin, preLogin.Type);
        using (TdsPacketWriter writer = new(tls, 4096))
        {
            await writer.WriteMessageAsync(
              TdsMessageType.TabularResult,
              TdsPreLogin.Encode(TdsEncryptionLevel.On),
              default);
        }

        var login = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.Login7, login.Type);
        using (TdsPacketWriter writer = new(tls, 4096))
        {
            await writer.WriteMessageAsync(
              TdsMessageType.TabularResult,
              BuildLoginAck(),
              default);
        }

        var query = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, query.Type);
        using TdsPacketWriter queryWriter = new(tls, 4096);
        await queryWriter.WriteMessageAsync(
          TdsMessageType.TabularResult,
          BuildIntResult(7),
          default);
    }

    private static async Task RunPreparedLifecycleServerAsync(
        TcpListener listener,
        bool fragmentedFirst)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await LoginAsync(stream);
        TdsPacketReader reader = new(stream);

        var first = await reader.ReadMessageAsync(default);
        AssertPreparedRequest(
          first,
          TdsProcedureId.PrepExec,
          expectedHandle: 0,
          expectedValue: 41);
        var firstResponse = BuildPreparedIntResult(
            41,
            preparedHandle: 73,
            secondValue: fragmentedFirst ? 99 : null);
        if (fragmentedFirst)
        {
            var position = 0;
            byte packetId = 1;
            while (position < firstResponse.Length)
            {
                var count = Math.Min(3, firstResponse.Length - position);
                var final = position + count == firstResponse.Length;
                await WritePacketAsync(
                  stream,
                  firstResponse.AsMemory(position, count),
                  final,
                  packetId++);
                position += count;
            }
        }
        else
        {
            using TdsPacketWriter firstWriter = new(stream, 4096);
            await firstWriter.WriteMessageAsync(
              TdsMessageType.TabularResult,
              firstResponse,
              default);
        }

        var second = await reader.ReadMessageAsync(default);
        AssertPreparedRequest(
          second,
          TdsProcedureId.Execute,
          expectedHandle: 73,
          expectedValue: 42);
        using TdsPacketWriter writer = new(stream, 4096);
        await writer.WriteMessageAsync(
          TdsMessageType.TabularResult,
          BuildPreparedIntResult(42, preparedHandle: null),
          default);

        var close = await reader.ReadMessageAsync(default);
        AssertPreparedRequest(
          close,
          TdsProcedureId.Unprepare,
          expectedHandle: 73,
          expectedValue: null);
        await writer.WriteMessageAsync(
          TdsMessageType.TabularResult,
          BuildRpcDone(),
          default);

        var reuse = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, reuse.Type);
        await writer.WriteMessageAsync(
          TdsMessageType.TabularResult,
          BuildIntResult(7),
          default);
    }

    private static async Task RunCancellationServerAsync(
        TcpListener listener,
        TaskCompletionSource queryReceived)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await LoginAsync(stream);
        TdsPacketReader reader = new(stream);
        var query = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, query.Type);
        queryReceived.SetResult();

        var attention = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.Attention, attention.Type);
        using TdsPacketWriter writer = new(stream, 4096);
        await writer.WriteMessageAsync(
          TdsMessageType.TabularResult,
          BuildAttentionAck(),
          default);

        var second = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, second.Type);
        await writer.WriteMessageAsync(
          TdsMessageType.TabularResult,
          BuildIntResult(7),
          default);
    }

    private static async Task RunRowsServerAsync(
        TcpListener listener,
        params int[] values)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await LoginAsync(stream);
        var query = await new TdsPacketReader(stream).ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, query.Type);
        using TdsPacketWriter writer = new(stream, 4096);
        await writer.WriteMessageAsync(
          TdsMessageType.TabularResult,
          BuildIntResult(values),
          default);
    }

    private static async Task RunBinaryRowsServerAsync(
        TcpListener listener,
        params byte[][] values)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await LoginAsync(stream);
        var query = await new TdsPacketReader(stream).ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, query.Type);
        using TdsPacketWriter writer = new(stream, 4096);
        await writer.WriteMessageAsync(
          TdsMessageType.TabularResult,
          BuildBinaryResult(values),
          default);
    }

    private static async Task RunStalledLoginServerAsync(
        TcpListener listener,
        TaskCompletionSource loginReceived,
        Task release)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        TdsPacketReader reader = new(stream);
        var preLogin = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.PreLogin, preLogin.Type);
        using (TdsPacketWriter writer = new(stream, 4096))
        {
            await writer.WriteMessageAsync(
              TdsMessageType.TabularResult,
              TdsPreLogin.Encode(TdsEncryptionLevel.NotSupported),
              default);
        }

        var login = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.Login7, login.Type);
        loginReceived.SetResult();
        await release;
    }

    private static async Task RunSplitRowsServerAsync(
        TcpListener listener,
        TaskCompletionSource firstRowWritten,
        Task releaseRemainder)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await LoginAsync(stream);
        var query = await new TdsPacketReader(stream).ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, query.Type);

        var response = BuildIntResult(1, 2);
        const int metadataSplit = 5;
        const int firstRowValueSplit = 24;
        const int firstRowEnd = 26;
        await WritePacketAsync(stream, response.AsMemory(0, metadataSplit), false, 1);
        await WritePacketAsync(
          stream,
          response.AsMemory(metadataSplit, firstRowValueSplit - metadataSplit),
          false,
          2);
        await WritePacketAsync(
          stream,
          response.AsMemory(firstRowValueSplit, firstRowEnd - firstRowValueSplit),
          false,
          3);
        firstRowWritten.SetResult();
        await releaseRemainder;
        await WritePacketAsync(stream, response.AsMemory(firstRowEnd), true, 4);
    }

    private static async Task RunMultiResultServerAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await LoginAsync(stream);
        var query = await new TdsPacketReader(stream).ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, query.Type);
        using TdsPacketWriter writer = new(stream, 4096);
        await writer.WriteMessageAsync(
          TdsMessageType.TabularResult,
          BuildMultiResult(),
          default);
    }

    private static async Task RunFragmentedJsonServerAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await LoginAsync(stream);
        var query = await new TdsPacketReader(stream).ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, query.Type);

        var response = BuildJsonResult("""{"split":true}""");
        var position = 0;
        byte packetId = 1;
        while (position < response.Length)
        {
            var count = Math.Min(3, response.Length - position);
            var final = position + count == response.Length;
            await WritePacketAsync(
              stream,
              response.AsMemory(position, count),
              final,
              packetId++);
            position += count;
        }
    }

    private static async Task RunUnsupportedTypeServerAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await LoginAsync(stream);
        var query = await new TdsPacketReader(stream).ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, query.Type);

        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        response.WriteUInt32LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteByte(0x62);
        response.WriteBVarChar("unsupported");
        WriteDone(response, 0);
        using TdsPacketWriter writer = new(stream, 4096);
        await writer.WriteMessageAsync(
          TdsMessageType.TabularResult,
          response.WrittenMemory,
          default);
    }

    private static async Task LoginAsync(NetworkStream stream)
    {
        TdsPacketReader reader = new(stream);
        var preLogin = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.PreLogin, preLogin.Type);
        using (TdsPacketWriter writer = new(stream, 4096))
        {
            await writer.WriteMessageAsync(
              TdsMessageType.TabularResult,
              TdsPreLogin.Encode(TdsEncryptionLevel.NotSupported),
              default);
        }

        var login = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.Login7, login.Type);
        using TdsPacketWriter loginWriter = new(stream, 4096);
        await loginWriter.WriteMessageAsync(
          TdsMessageType.TabularResult,
          BuildLoginAck(),
          default);
    }

    private static byte[] BuildLoginAck()
    {
        ArrayBufferWriter<byte> body = new();
        body.WriteByte(1);
        body.Write("\x04\x00\x00\x74"u8);
        body.WriteBVarChar("SQL Server");
        body.WriteByte(16);
        body.WriteByte(0);
        body.WriteUInt16BigEndian(1000);

        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.LoginAck);
        response.WriteUInt16LittleEndian(checked((ushort)body.WrittenCount));
        response.Write(body.WrittenSpan);
        WriteDone(response, 0);
        return response.WrittenMemory.ToArray();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
          "CN=localhost",
          rsa,
          HashAlgorithmName.SHA256,
          RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(
          new X509BasicConstraintsExtension(false, false, 0, critical: true));
        using var certificate = request.CreateSelfSigned(
          DateTimeOffset.UtcNow.AddMinutes(-5),
          DateTimeOffset.UtcNow.AddDays(1));
        return CloneCertificate(certificate);
    }

    private static X509Certificate2 CloneCertificate(X509Certificate2 certificate) =>
      X509CertificateLoader.LoadPkcs12(
        certificate.Export(X509ContentType.Pfx),
        password: null);

    private static byte[] BuildIntResult(params int[] values)
    {
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        response.WriteUInt32LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteByte(TdsDataType.Int4);
        response.WriteBVarChar("value");
        foreach (var value in values)
        {
            response.WriteByte(TdsTokenType.Row);
            response.WriteInt32LittleEndian(value);
        }
        WriteDone(response, 0);
        return response.WrittenMemory.ToArray();
    }

    private static byte[] BuildBinaryResult(params byte[][] values)
    {
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        response.WriteUInt32LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteByte(TdsDataType.BigVarBinary);
        response.WriteUInt16LittleEndian(8000);
        response.WriteBVarChar("value");
        foreach (var value in values)
        {
            response.WriteByte(TdsTokenType.Row);
            response.WriteUInt16LittleEndian(checked((ushort)value.Length));
            response.Write(value);
        }

        WriteDone(response, 0);
        return response.WrittenMemory.ToArray();
    }

    private static byte[] BuildPreparedIntResult(
        int value,
        int? preparedHandle,
        int? secondValue = null)
    {
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        response.WriteUInt32LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteByte(TdsDataType.Int4);
        response.WriteBVarChar("value");
        response.WriteByte(TdsTokenType.Row);
        response.WriteInt32LittleEndian(value);
        WriteDone(response, (ushort)TdsDoneStatus.More);
        if (secondValue is int additionalValue)
        {
            response.WriteByte(TdsTokenType.ColumnMetadata);
            response.WriteUInt16LittleEndian(1);
            response.WriteUInt32LittleEndian(0);
            response.WriteUInt16LittleEndian(0);
            response.WriteByte(TdsDataType.Int4);
            response.WriteBVarChar("additional");
            response.WriteByte(TdsTokenType.Row);
            response.WriteInt32LittleEndian(additionalValue);
            WriteDone(response, (ushort)TdsDoneStatus.More);
        }

        response.WriteByte(TdsTokenType.ReturnStatus);
        response.WriteInt32LittleEndian(0);
        if (preparedHandle is int handle)
        {
            response.WriteByte(TdsTokenType.ReturnValue);
            response.WriteUInt16LittleEndian(2);
            response.WriteBVarChar("@ignored");
            response.WriteByte(1);
            response.WriteUInt32LittleEndian(0);
            response.WriteUInt16LittleEndian(0);
            response.WriteByte(TdsDataType.IntN);
            response.WriteByte(sizeof(int));
            response.WriteByte(sizeof(int));
            response.WriteInt32LittleEndian(999);

            response.WriteByte(TdsTokenType.ReturnValue);
            response.WriteUInt16LittleEndian(1);
            response.WriteBVarChar(string.Empty);
            response.WriteByte(1);
            response.WriteUInt32LittleEndian(0);
            response.WriteUInt16LittleEndian(0);
            response.WriteByte(TdsDataType.IntN);
            response.WriteByte(sizeof(int));
            response.WriteByte(sizeof(int));
            response.WriteInt32LittleEndian(handle);
        }

        response.WriteByte(TdsTokenType.DoneProc);
        response.WriteUInt16LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteInt64LittleEndian(0);
        return response.WrittenMemory.ToArray();
    }

    private static byte[] BuildRpcDone()
    {
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ReturnStatus);
        response.WriteInt32LittleEndian(0);
        response.WriteByte(TdsTokenType.DoneProc);
        response.WriteUInt16LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteInt64LittleEndian(0);
        return response.WrittenMemory.ToArray();
    }

    private static void AssertPreparedRequest(
        TdsMessage message,
        ushort procedureId,
        int expectedHandle,
        int? expectedValue)
    {
        Assert.AreEqual(TdsMessageType.Rpc, message.Type);
        TdsPayloadReader reader = new(message.Payload.Span);
        reader.Skip(22);
        Assert.AreEqual(ushort.MaxValue, reader.ReadUInt16LittleEndian());
        Assert.AreEqual(procedureId, reader.ReadUInt16LittleEndian());
        Assert.AreEqual(0, reader.ReadUInt16LittleEndian());
        Assert.AreEqual(expectedHandle, ReadIntParameter(ref reader, output: procedureId != TdsProcedureId.Unprepare));

        if (procedureId == TdsProcedureId.PrepExec)
        {
            Assert.AreEqual("@P1 int", ReadNVarCharParameter(ref reader));
            Assert.AreEqual("SELECT @P1", ReadNVarCharParameter(ref reader));
        }

        if (expectedValue is int value)
        {
            Assert.AreEqual(value, ReadIntParameter(ref reader, output: false));
        }

        Assert.AreEqual(0, reader.Remaining);
    }

    private static int ReadIntParameter(
        ref TdsPayloadReader reader,
        bool output)
    {
        _ = reader.ReadBVarChar();
        Assert.AreEqual(output ? 1 : 0, reader.ReadByte());
        Assert.AreEqual(TdsDataType.IntN, reader.ReadByte());
        Assert.AreEqual(sizeof(int), reader.ReadByte());
        Assert.AreEqual(sizeof(int), reader.ReadByte());
        return reader.ReadInt32LittleEndian();
    }

    private static string ReadNVarCharParameter(ref TdsPayloadReader reader)
    {
        _ = reader.ReadBVarChar();
        Assert.AreEqual(0, reader.ReadByte());
        Assert.AreEqual(TdsDataType.NVarChar, reader.ReadByte());
        Assert.AreEqual(8000, reader.ReadUInt16LittleEndian());
        reader.Skip(5);
        int byteLength = reader.ReadUInt16LittleEndian();
        return System.Text.Encoding.Unicode.GetString(reader.ReadSpan(byteLength));
    }

    private static byte[] BuildAttentionAck()
    {
        ArrayBufferWriter<byte> response = new();
        WriteDone(response, (ushort)TdsDoneStatus.Attention);
        return response.WrittenMemory.ToArray();
    }

    private static byte[] BuildMultiResult()
    {
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        response.WriteUInt32LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteByte(TdsDataType.Int4);
        response.WriteBVarChar("a");
        response.WriteByte(TdsTokenType.Row);
        response.WriteInt32LittleEndian(1);
        WriteDone(response, (ushort)TdsDoneStatus.More);

        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        response.WriteUInt32LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteByte(TdsDataType.NVarChar);
        response.WriteUInt16LittleEndian(20);
        response.WriteUInt32LittleEndian(0x0409);
        response.WriteByte(0);
        response.WriteBVarChar("b");
        response.WriteByte(TdsTokenType.Row);
        response.WriteUInt16LittleEndian(2);
        response.WriteUtf16("x");
        WriteDone(response, 0);
        return response.WrittenMemory.ToArray();
    }

    private static byte[] BuildJsonResult(string json)
    {
        var value = System.Text.Encoding.UTF8.GetBytes(json);
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        response.WriteUInt32LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteByte(TdsDataType.Json);
        response.WriteBVarChar("payload");
        response.WriteByte(TdsTokenType.Row);
        response.WriteUInt64LittleEndian(checked((ulong)value.Length));
        response.WriteUInt32LittleEndian(checked((uint)value.Length));
        response.Write(value);
        response.WriteUInt32LittleEndian(0);
        WriteDone(response, 0);
        return response.WrittenMemory.ToArray();
    }

    private static async ValueTask WritePacketAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        bool endOfMessage,
        byte packetId)
    {
        var header = new byte[8];
        header[0] = TdsMessageType.TabularResult;
        header[1] = endOfMessage ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(
          header.AsSpan(2),
          checked((ushort)(payload.Length + header.Length)));
        header[6] = packetId;
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static void WriteDone(ArrayBufferWriter<byte> response, ushort status)
    {
        response.WriteByte(TdsTokenType.Done);
        response.WriteUInt16LittleEndian(status);
        response.WriteUInt16LittleEndian(0);
        response.WriteInt64LittleEndian(0);
    }
}

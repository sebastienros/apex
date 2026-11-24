using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Apex.MsSqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MsSqlClient.Tests;

[TestClass]
public sealed class MsSqlReviewBugFixTests
{
    [TestMethod]
    public void Login7UsesTds8VersionOnlyInStrictEncryptionMode()
    {
        var strict = TdsLogin7.Encode(new MsSqlConnectOptions
        {
            Host = "db",
            Username = "sa",
            EncryptionMode = MsSqlEncryptionMode.Strict,
        });
        CollectionAssert.AreEqual(
            new byte[] { 0x00, 0x00, 0x00, 0x08 },
            strict.AsSpan(4, 4).ToArray());

        var required = TdsLogin7.Encode(new MsSqlConnectOptions
        {
            Host = "db",
            Username = "sa",
            EncryptionMode = MsSqlEncryptionMode.Require,
        });
        CollectionAssert.AreEqual(
            new byte[] { 0x04, 0x00, 0x00, 0x74 },
            required.AsSpan(4, 4).ToArray());
    }

    [TestMethod]
    public void ParseOptionsUnwrapsIpv6BracketsAndRejectsKeyWithoutEqualsBeforeSemicolon()
    {
        var fromUri = MsSqlConnectOptions.Parse("sqlserver://sa:secret@[::1]:14330/app");
        Assert.AreEqual("::1", fromUri.Host);
        Assert.AreEqual(14330, fromUri.Port);
        Assert.AreEqual("app", fromUri.Database);

        var fromServerPort = MsSqlConnectOptions.Parse("Server=[::1],14331;User ID=sa");
        Assert.AreEqual("::1", fromServerPort.Host);
        Assert.AreEqual(14331, fromServerPort.Port);

        var fromServerOnly = MsSqlConnectOptions.Parse("Server=tcp:[::1];User ID=sa");
        Assert.AreEqual("::1", fromServerOnly.Host);
        Assert.AreEqual(1433, fromServerOnly.Port);

        Assert.ThrowsExactly<FormatException>(() => MsSqlConnectOptions.Parse("Server;Database=app"));
        Assert.ThrowsExactly<FormatException>(() => MsSqlConnectOptions.Parse("Server=[],1433;User ID=sa"));
    }

    [TestMethod]
    public void ValidateOptionsAllowsEmptyUsernameWhenAuthenticationProviderIsConfigured()
    {
        MsSqlConnectOptions withProvider = new()
        {
            Host = "db.example.com",
            Username = string.Empty,
            AuthenticationProvider = static _ => ValueTask.FromResult(
                new SqlAuthenticationCredential("token", SqlAuthenticationMethod.BearerToken)),
        };
        MsSqlConnection.ValidateOptions(withProvider);

        MsSqlConnectOptions withoutProvider = new()
        {
            Host = "db.example.com",
            Username = string.Empty,
        };
        Assert.ThrowsExactly<ArgumentException>(() => MsSqlConnection.ValidateOptions(withoutProvider));
    }

    [TestMethod]
    public void ParseEnvironmentChangeResetsTransactionDescriptorOnTransactionEnded()
    {
        byte[] body = [TdsEnvironmentChange.TransactionEnded, 0, 8, 1, 2, 3, 4, 5, 6, 7, 8];
        var change = TdsTokenReader.ParseEnvironmentChange(body);
        Assert.AreEqual(0L, change.TransactionDescriptor);
    }

    [TestMethod]
    public async Task TlsHandshakeStreamSkipsZeroPayloadPreLoginPackets()
    {
        var payload = "hello-tls"u8.ToArray();
        using MemoryStream wire = new();
        // Zero-length PreLogin packet (8-byte header only) followed by payload packet.
        wire.Write([TdsMessageType.PreLogin, 0x00, 0x00, 0x08, 0x00, 0x00, 0x01, 0x00]);
        var length = checked((ushort)(8 + payload.Length));
        wire.Write([TdsMessageType.PreLogin, 0x01, (byte)(length >> 8), (byte)length, 0x00, 0x00, 0x02, 0x00]);
        wire.Write(payload);

        wire.Position = 0;
        using (TdsTlsHandshakeStream asyncStream = new(wire, 512))
        {
            var read = new byte[payload.Length];
            var count = await asyncStream.ReadAsync(read);
            Assert.AreEqual(payload.Length, count);
            CollectionAssert.AreEqual(payload, read);
        }

        wire.Position = 0;
        using (TdsTlsHandshakeStream syncStream = new(wire, 512))
        {
            var read = new byte[payload.Length];
            var count = syncStream.Read(read);
            Assert.AreEqual(payload.Length, count);
            CollectionAssert.AreEqual(payload, read);
        }
    }

    [TestMethod]
    public void JsonDecoderHandlesSingleCharAndNonAsciiSecondCharUtf16Payloads()
    {
        MsSqlRowDecoder decoder = new();
        SqlColumn column = new("payload", TdsDataType.Json, -1, 0, SqlDataFormat.Binary);

        byte[] singleCharUtf16 = Encoding.Unicode.GetBytes("7");
        byte[] euroUtf16 = Encoding.Unicode.GetBytes("\"€\"");
        byte[] utf8 = "{\"k\":\"€\"}"u8.ToArray();

        Assert.AreEqual("7", decoder.DecodeString(EncodeSingleColumnRow(singleCharUtf16), 0, column));
        Assert.AreEqual(7, decoder.Decode<JsonElement>(EncodeSingleColumnRow(singleCharUtf16), 0, column, copyReadOnlyMemory: false).GetInt32());

        Assert.AreEqual("\"€\"", decoder.DecodeString(EncodeSingleColumnRow(euroUtf16), 0, column));
        Assert.AreEqual("€", decoder.Decode<JsonElement>(EncodeSingleColumnRow(euroUtf16), 0, column, copyReadOnlyMemory: false).GetString());

        Assert.AreEqual("{\"k\":\"€\"}", decoder.DecodeString(EncodeSingleColumnRow(utf8), 0, column));
        Assert.AreEqual("€", decoder.Decode<JsonElement>(EncodeSingleColumnRow(utf8), 0, column, copyReadOnlyMemory: false).GetProperty("k").GetString());
    }

    [TestMethod]
    public void RequestWriterDeclaresMaxTypesForLargeCharArrayBitArrayAndPhysicalAddress()
    {
        var largeChars = new char[4001];
        Array.Fill(largeChars, 'a');
        var largeBits = new BitArray(4001, true);
        var largeAddress = new PhysicalAddress(new byte[8001]);

        var definitions = TdsRequestWriter.BuildDefinitions(SqlParameters.Create(
            SqlValue.From(largeChars),
            SqlValue.From(largeBits),
            SqlValue.From(largeAddress)));

        Assert.AreEqual("@P1 nvarchar(max),@P2 nvarchar(max),@P3 varbinary(max)", definitions);
    }

    [TestMethod]
    public void PreparedExecuteEncodesNullAsVarBinaryForDeclaredVarBinaryParameters()
    {
        var definitions = "@P1 varbinary(8000),@P2 varbinary(max),@P3 int";
        var payload = TdsRequestWriter.BuildExecute(
            handle: 7,
            SqlParameters.Create(SqlValue.Null, SqlValue.Null, SqlValue.Null),
            transactionDescriptor: 0,
            parameterNames: null,
            definitions: definitions);

        TdsPayloadReader reader = new(payload.Span);
        reader.Skip(22); // ALL_HEADERS
        Assert.AreEqual(ushort.MaxValue, reader.ReadUInt16LittleEndian());
        Assert.AreEqual(TdsProcedureId.Execute, reader.ReadUInt16LittleEndian());
        Assert.AreEqual(0, reader.ReadUInt16LittleEndian());

        // Handle parameter
        Assert.AreEqual(string.Empty, reader.ReadBVarChar());
        Assert.AreEqual(1, reader.ReadByte()); // output
        Assert.AreEqual(TdsDataType.IntN, reader.ReadByte());
        Assert.AreEqual(4, reader.ReadByte());
        Assert.AreEqual(4, reader.ReadByte());
        Assert.AreEqual(7, reader.ReadInt32LittleEndian());

        // @P1: varbinary(8000) NULL
        Assert.AreEqual("@P1", reader.ReadBVarChar());
        Assert.AreEqual(0, reader.ReadByte());
        Assert.AreEqual(TdsDataType.BigVarBinary, reader.ReadByte());
        Assert.AreEqual(8000, reader.ReadUInt16LittleEndian());
        Assert.AreEqual(ushort.MaxValue, reader.ReadUInt16LittleEndian());

        // @P2: varbinary(max) PLP NULL
        Assert.AreEqual("@P2", reader.ReadBVarChar());
        Assert.AreEqual(0, reader.ReadByte());
        Assert.AreEqual(TdsDataType.BigVarBinary, reader.ReadByte());
        Assert.AreEqual(ushort.MaxValue, reader.ReadUInt16LittleEndian());
        Assert.AreEqual(-1L, reader.ReadInt64LittleEndian());

        // @P3: int NULL falls back to nvarchar(4000) NULL
        Assert.AreEqual("@P3", reader.ReadBVarChar());
        Assert.AreEqual(0, reader.ReadByte());
        Assert.AreEqual(TdsDataType.NVarChar, reader.ReadByte());
        Assert.AreEqual(8000, reader.ReadUInt16LittleEndian());
        reader.Skip(5); // collation
        Assert.AreEqual(ushort.MaxValue, reader.ReadUInt16LittleEndian());
        Assert.AreEqual(0, reader.Remaining);
    }

    [TestMethod]
    public async Task DisposingConnectionWhileReaderIsParkedDoesNotDeadlock()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource serverStop = new(TimeSpan.FromSeconds(10));

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(serverStop.Token);
            await using var stream = client.GetStream();
            await CompleteLoginAsync(stream, serverStop.Token);
            var reader = new TdsPacketReader(stream);
            _ = await reader.ReadMessageAsync(serverStop.Token);
            using var writer = new TdsPacketWriter(stream, 4096);
            await writer.WriteMessageAsync(
                TdsMessageType.TabularResult,
                BuildIntResult([1, 2, 3]),
                serverStop.Token);
            try
            {
                var buffer = new byte[1];
                _ = await stream.ReadAsync(buffer, serverStop.Token);
            }
            catch
            {
            }
        });

        var connection = await MsSqlClient.ConnectAsync(TestOptions(port), serverStop.Token);
        var rowReader = await connection.ExecuteReaderAsync("SELECT value", cancellationToken: serverStop.Token);
        Assert.IsTrue(await rowReader.ReadAsync(serverStop.Token));
        Assert.AreEqual(1, rowReader.GetInt32(0));

        // Dispose connection WITHOUT disposing rowReader first; previously deadlocked forever.
        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await server.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task ResultReaderSurfacesServerErrorAfterColumnMetadataAndKeepsConnectionUsable()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource serverStop = new(TimeSpan.FromSeconds(10));

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(serverStop.Token);
            await using var stream = client.GetStream();
            await CompleteLoginAsync(stream, serverStop.Token);
            var reader = new TdsPacketReader(stream);
            using var writer = new TdsPacketWriter(stream, 4096);

            // Query 1: COLMETADATA -> ERROR -> DONE(Error) (no rows)
            _ = await reader.ReadMessageAsync(serverStop.Token);
            ArrayBufferWriter<byte> errAfterMeta = new();
            WriteIntColumnMetadata(errAfterMeta, "value");
            WriteError(errAfterMeta, 8134, "Divide by zero error encountered.");
            WriteDone(errAfterMeta, TdsDoneStatus.Error);
            await writer.WriteMessageAsync(TdsMessageType.TabularResult, errAfterMeta.WrittenMemory, serverStop.Token);

            // Query 2: COLMETADATA -> ROW(1) -> ERROR -> DONE(Error | More) -> COLMETADATA -> ROW(99) -> DONE(0)
            _ = await reader.ReadMessageAsync(serverStop.Token);
            ArrayBufferWriter<byte> errMidStream = new();
            WriteIntColumnMetadata(errMidStream, "value");
            errMidStream.WriteByte(TdsTokenType.Row);
            errMidStream.WriteInt32LittleEndian(1);
            WriteError(errMidStream, 8134, "Divide by zero mid-stream.");
            WriteDone(errMidStream, TdsDoneStatus.Error | TdsDoneStatus.More);
            WriteIntColumnMetadata(errMidStream, "ignored");
            errMidStream.WriteByte(TdsTokenType.Row);
            errMidStream.WriteInt32LittleEndian(99);
            WriteDone(errMidStream, 0);
            await writer.WriteMessageAsync(TdsMessageType.TabularResult, errMidStream.WrittenMemory, serverStop.Token);

            // Query 3: connection must remain usable
            _ = await reader.ReadMessageAsync(serverStop.Token);
            await writer.WriteMessageAsync(TdsMessageType.TabularResult, BuildIntResult([42]), serverStop.Token);
        });

        await using var connection = await MsSqlClient.ConnectAsync(TestOptions(port), serverStop.Token);

        var reader1 = (MsSqlResultReader)await connection.ExecuteResultReaderAsync(
            "SELECT 1/0", SqlParameters.Empty, serverStop.Token);
        try
        {
            var ex = await Assert.ThrowsExactlyAsync<MsSqlException>(() => reader1.InitializeAsync(serverStop.Token).AsTask());
            Assert.AreEqual(8134, ex.Number);
        }
        finally
        {
            try
            {
                await reader1.DisposeAsync();
            }
            catch (MsSqlException)
            {
            }
        }

        var reader2 = (MsSqlResultReader)await connection.ExecuteResultReaderAsync(
            "SELECT 1/v FROM t; SELECT 99", SqlParameters.Empty, serverStop.Token);
        try
        {
            Assert.IsTrue(await reader2.InitializeAsync(serverStop.Token));
            Assert.AreEqual(1, reader2.GetInt32(0));
            var ex = await Assert.ThrowsExactlyAsync<MsSqlException>(() => reader2.ReadAsync(serverStop.Token).AsTask());
            Assert.AreEqual(8134, ex.Number);
        }
        finally
        {
            try
            {
                await reader2.DisposeAsync();
            }
            catch (MsSqlException)
            {
            }
        }

        var ok = await connection.QueryAsync("SELECT 42", serverStop.Token);
        Assert.AreEqual(42, ok[0].GetInt32(0));
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task CancelRequestAsyncStopsBufferedRowFastPathAndKeepsConnectionUsable()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource serverStop = new(TimeSpan.FromSeconds(10));

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(serverStop.Token);
            await using var stream = client.GetStream();
            await CompleteLoginAsync(stream, serverStop.Token);
            var reader = new TdsPacketReader(stream);
            using var writer = new TdsPacketWriter(stream, 4096);

            _ = await reader.ReadMessageAsync(serverStop.Token);
            // Send first packet with 20 rows buffered, NOT end-of-message yet.
            ArrayBufferWriter<byte> part = new();
            WriteIntColumnMetadata(part, "value");
            for (var i = 1; i <= 20; i++)
            {
                part.WriteByte(TdsTokenType.Row);
                part.WriteInt32LittleEndian(i);
            }

            await WriteRawPacketAsync(stream, part.WrittenMemory, endOfMessage: false, packetId: 1, serverStop.Token);

            // Wait for TDS Attention from CancelRequestAsync
            var attention = await reader.ReadMessageAsync(serverStop.Token);
            Assert.AreEqual(TdsMessageType.Attention, attention.Type);

            // Close original message and send DONE(Attention)
            ArrayBufferWriter<byte> ack = new();
            WriteDone(ack, TdsDoneStatus.Attention);
            await WriteRawPacketAsync(stream, ack.WrittenMemory, endOfMessage: true, packetId: 2, serverStop.Token);

            // Next query must succeed on the same connection
            _ = await reader.ReadMessageAsync(serverStop.Token);
            await writer.WriteMessageAsync(TdsMessageType.TabularResult, BuildIntResult([77]), serverStop.Token);
        });

        await using var connection = await MsSqlClient.ConnectAsync(TestOptions(port), serverStop.Token);
        await using (var reader = await connection.ExecuteReaderAsync("SELECT value", cancellationToken: serverStop.Token))
        {
            Assert.IsTrue(await reader.ReadAsync(serverStop.Token));
            Assert.AreEqual(1, reader.GetInt32(0));

            await connection.CancelRequestAsync(serverStop.Token);

            // Next ReadAsync must NOT keep serving buffered rows 2..20 from TryReadBufferedRow.
            await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(serverStop.Token).AsTask());
        }

        var next = await connection.QueryAsync("SELECT 77", serverStop.Token);
        Assert.AreEqual(77, next[0].GetInt32(0));
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static MsSqlConnectOptions TestOptions(int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Username = "sa",
        Password = "secret",
        Database = "test",
        EncryptionMode = MsSqlEncryptionMode.Disable,
    };

    private static ReadOnlyMemory<byte> EncodeSingleColumnRow(byte[] payload)
    {
        var row = new byte[sizeof(ushort) + sizeof(int) + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(row, 1);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(sizeof(ushort)), payload.Length);
        payload.CopyTo(row, sizeof(ushort) + sizeof(int));
        return row;
    }

    private static async Task CompleteLoginAsync(Stream stream, CancellationToken cancellationToken)
    {
        var reader = new TdsPacketReader(stream);
        Assert.AreEqual(TdsMessageType.PreLogin, (await reader.ReadMessageAsync(cancellationToken)).Type);
        using var writer = new TdsPacketWriter(stream, 4096);
        await writer.WriteMessageAsync(
            TdsMessageType.TabularResult,
            TdsPreLogin.Encode(TdsEncryptionLevel.NotSupported),
            cancellationToken);
        Assert.AreEqual(TdsMessageType.Login7, (await reader.ReadMessageAsync(cancellationToken)).Type);

        ArrayBufferWriter<byte> body = new();
        body.WriteByte(1);
        body.Write("\x04\x00\x00\x74"u8);
        body.WriteBVarChar("SQL Server");
        body.WriteByte(16);
        body.WriteByte(0);
        body.WriteUInt16BigEndian(1000);
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.LoginAck);
        response.WriteUInt16LittleEndian((ushort)body.WrittenCount);
        response.Write(body.WrittenSpan);
        WriteDone(response, 0);
        await writer.WriteMessageAsync(TdsMessageType.TabularResult, response.WrittenMemory, cancellationToken);
    }

    private static byte[] BuildIntResult(int[] values)
    {
        ArrayBufferWriter<byte> response = new();
        WriteIntColumnMetadata(response, "value");
        foreach (var value in values)
        {
            response.WriteByte(TdsTokenType.Row);
            response.WriteInt32LittleEndian(value);
        }

        WriteDone(response, 0);
        return response.WrittenMemory.ToArray();
    }

    private static void WriteIntColumnMetadata(ArrayBufferWriter<byte> response, string name)
    {
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        response.WriteUInt32LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteByte(TdsDataType.Int4);
        response.WriteBVarChar(name);
    }

    private static void WriteError(ArrayBufferWriter<byte> response, int number, string message)
    {
        ArrayBufferWriter<byte> body = new();
        body.WriteInt32LittleEndian(number);
        body.WriteByte(1);
        body.WriteByte(16);
        body.WriteUInt16LittleEndian(checked((ushort)message.Length));
        body.WriteUtf16(message);
        body.WriteBVarChar("server");
        body.WriteBVarChar(string.Empty);
        body.WriteInt32LittleEndian(1);
        response.WriteByte(TdsTokenType.Error);
        response.WriteUInt16LittleEndian(checked((ushort)body.WrittenCount));
        response.Write(body.WrittenSpan);
    }

    private static void WriteDone(ArrayBufferWriter<byte> response, TdsDoneStatus status)
    {
        response.WriteByte(TdsTokenType.Done);
        response.WriteUInt16LittleEndian((ushort)status);
        response.WriteUInt16LittleEndian(0);
        response.WriteInt64LittleEndian(0);
    }

    private static async ValueTask WriteRawPacketAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        bool endOfMessage,
        byte packetId,
        CancellationToken cancellationToken)
    {
        var header = new byte[8];
        header[0] = TdsMessageType.TabularResult;
        header[1] = endOfMessage ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(
            header.AsSpan(2),
            checked((ushort)(payload.Length + header.Length)));
        header[6] = packetId;
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

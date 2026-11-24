using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Transactions;
using Apex.PgClient.Internal;
using Apex.SqlClient;

namespace Apex.PgClient.Tests;

[TestClass]
public sealed class PgReviewBugFixTests
{
    [TestMethod]
    public void FormatTimeSpanAppliesNegativeSignToBothDaysAndTime()
    {
        Assert.AreEqual("-1 days -02:30:00", PgTextCodec.FormatParameter(SqlValue.From(TimeSpan.FromHours(-26.5))));
        Assert.AreEqual("-2 days", PgTextCodec.FormatParameter(SqlValue.From(TimeSpan.FromDays(-2))));
        Assert.AreEqual("2 days", PgTextCodec.FormatParameter(SqlValue.From(TimeSpan.FromDays(2))));
        Assert.AreEqual("-02:30:00", PgTextCodec.FormatParameter(SqlValue.From(TimeSpan.FromHours(-2.5))));
    }

    [TestMethod]
    public void FormatIntervalPreservesNegativeSubSecondSignAndHandlesIntMinValue()
    {
        var subSecond = new PgInterval(0, 0, 0, 0, 0, Seconds: 0, Microseconds: -250_000);
        Assert.AreEqual(
            "0 years 0 mons 0 days 0 hours 0 mins -0.250000 secs",
            PgTextCodec.FormatParameter(SqlValue.From(subSecond)));

        var minMicros = new PgInterval(0, 0, 0, 0, 0, Seconds: 0, Microseconds: int.MinValue);
        Assert.AreEqual(
            "0 years 0 mons 0 days 0 hours 0 mins -2147.483648 secs",
            PgTextCodec.FormatParameter(SqlValue.From(minMicros)));
    }

    [TestMethod]
    public void BinaryTimestampAndTimeCodecsThrowOnInt64TicksOverflow()
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(payload, 1_844_674_407_370_955_162L);

        Assert.ThrowsExactly<OverflowException>(() => PgBinaryCodec.DecodeDateTime(payload));
        Assert.ThrowsExactly<OverflowException>(() => PgBinaryCodec.DecodeDateTimeOffset(payload));
        Assert.ThrowsExactly<OverflowException>(() => PgBinaryCodec.DecodeTimeOnly(payload));

        var timetz = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(timetz, 1_844_674_407_370_955_162L);
        Assert.ThrowsExactly<OverflowException>(() => PgBinaryCodec.DecodeTimeWithTimeZone(timetz));
    }

    [TestMethod]
    public void CustomRegisteredCodecHandlesNullAndWinsInDecodeObject()
    {
        var registry = new PgTypeRegistry();
        registry.Register(
            new PgType(50000, "public.custom_box"),
            static (CustomValue value) =>
            {
                var bytes = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(bytes, value.Id);
                return bytes;
            },
            static (ReadOnlyMemory<byte> data) => new CustomValue(BinaryPrimitives.ReadInt32BigEndian(data.Span)));
        registry.Register(
            new PgType(23, "pg_catalog.int4"),
            static (CustomInt value) =>
            {
                var bytes = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(bytes, value.Value);
                return bytes;
            },
            static (ReadOnlyMemory<byte> data) => new CustomInt(BinaryPrimitives.ReadInt32BigEndian(data.Span) * 10));

        var decoder = new PgRowDecoder(0, 0, 0, 0, registry);
        var customColumn = new SqlColumn("c", 50000, -1, -1, SqlDataFormat.Binary);
        var nullRow = CreateSingleFieldRow(null);

        Assert.IsNull(decoder.Decode<CustomValue?>(nullRow, 0, customColumn, false));
        Assert.ThrowsExactly<InvalidCastException>(() => decoder.Decode<CustomValue>(nullRow, 0, customColumn, false));

        var intColumn = new SqlColumn("i", 23, 4, -1, SqlDataFormat.Binary);
        var intBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(intBytes, 7);
        var intRow = CreateSingleFieldRow(intBytes);
        var obj = decoder.DecodeObject(intRow, 0, intColumn);
        Assert.IsInstanceOfType<CustomInt>(obj);
        Assert.AreEqual(70, ((CustomInt)obj).Value);
    }

    [TestMethod]
    public void PgNumericParseTrimsWhitespaceSupportsPlusInfinityAndRejectsInteriorSpace()
    {
        Assert.AreEqual("1.5", PgNumeric.Parse(" 1.5 ").ToString());
        Assert.AreEqual(PgNumeric.PositiveInfinity, PgNumeric.Parse("+Infinity"));
        Assert.AreEqual(PgNumeric.PositiveInfinity, PgNumeric.Parse(" +Infinity "));
        Assert.ThrowsExactly<FormatException>(() => PgNumeric.Parse(". 5"));
        Assert.ThrowsExactly<FormatException>(() => PgNumeric.Parse("1. 5"));
    }

    [TestMethod]
    public void TextFloatAndDoubleAcceptSpecialValues()
    {
        Assert.IsTrue(float.IsNaN(PgTextCodec.DecodeFloat("NaN"u8)));
        Assert.IsTrue(float.IsPositiveInfinity(PgTextCodec.DecodeFloat("Infinity"u8)));
        Assert.IsTrue(float.IsNegativeInfinity(PgTextCodec.DecodeFloat("-Infinity"u8)));

        Assert.IsTrue(double.IsNaN(PgTextCodec.DecodeDouble("NaN"u8)));
        Assert.IsTrue(double.IsPositiveInfinity(PgTextCodec.DecodeDouble("Infinity"u8)));
        Assert.IsTrue(double.IsNegativeInfinity(PgTextCodec.DecodeDouble("-Infinity"u8)));
    }

    [TestMethod]
    public void NumericArrayDecodesToDecimalAndCharArrayOid1014IsSupported()
    {
        var textDecimals = PgTextCodec.DecodeArray<decimal>(1231, "{1.25,2.50}"u8.ToArray());
        CollectionAssert.AreEqual(new[] { 1.25m, 2.50m }, textDecimals);

        var textNullableDecimals = PgTextCodec.DecodeArray<decimal?>(1231, "{1.25,NULL,2.50}"u8.ToArray());
        CollectionAssert.AreEqual(new decimal?[] { 1.25m, null, 2.50m }, textNullableDecimals);

        // Build binary numeric[] with one element (1.25: ndigits=2, weight=0, sign=0, dscale=2, digits=[1, 2500])
        var numericElement = new byte[12];
        BinaryPrimitives.WriteInt16BigEndian(numericElement.AsSpan(0), 2);
        BinaryPrimitives.WriteInt16BigEndian(numericElement.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(numericElement.AsSpan(4), 0);
        BinaryPrimitives.WriteInt16BigEndian(numericElement.AsSpan(6), 2);
        BinaryPrimitives.WriteInt16BigEndian(numericElement.AsSpan(8), 1);
        BinaryPrimitives.WriteInt16BigEndian(numericElement.AsSpan(10), 2500);
        var binaryNumericArray = CreateBinaryArray(1700, [numericElement]);
        CollectionAssert.AreEqual(new[] { 1.25m }, PgBinaryCodec.DecodeArray<decimal>(1231, binaryNumericArray));

        // bpchar[] (OID 1014)
        CollectionAssert.AreEqual(new[] { "a ", "b " }, PgTextCodec.DecodeArray<string>(1014, "{\"a \",\"b \"}"u8.ToArray()));
        var decodedTextObj = PgTextCodec.Decode(1014, "{\"a \",\"b \"}"u8.ToArray());
        Assert.IsInstanceOfType<string?[]>(decodedTextObj);
        var binaryCharArray = CreateBinaryArray(1042, ["a "u8.ToArray(), "b "u8.ToArray()]);
        CollectionAssert.AreEqual(new[] { "a ", "b " }, PgBinaryCodec.DecodeArray<string>(1014, binaryCharArray));
        Assert.IsInstanceOfType<string?[]>(PgBinaryCodec.Decode(1014, binaryCharArray));
    }

    [TestMethod]
    public void FormatParameterSupportsNullableValueArraysAndPgNumericArrays()
    {
        Assert.AreEqual(
            "{\"1\",NULL,\"3\"}",
            PgTextCodec.FormatParameter(SqlValue.From(new int?[] { 1, null, 3 })));
        Assert.AreEqual(
            "{\"1.5\",\"2.25\"}",
            PgTextCodec.FormatParameter(SqlValue.From(new[] { PgNumeric.Parse("1.5"), PgNumeric.Parse("2.25") })));
    }

    [TestMethod]
    public void ParseUriStripsIpv6BracketsAndEnumParserRejectsNumericStrings()
    {
        var options = PgConnectOptions.Parse("postgres://[::1]:5432/app");
        Assert.AreEqual("::1", options.Host);

        Assert.ThrowsExactly<ArgumentException>(() => PgConnectOptions.Parse("host=db sslmode=99"));
        Assert.ThrowsExactly<ArgumentException>(() => PgConnectOptions.Parse("host=db channel_binding=99"));
        Assert.ThrowsExactly<ArgumentException>(() => PgConnectOptions.Parse("host=db sslnegotiation=99"));
    }

    [TestMethod]
    public void ArrayParserRejectsTrailingAndEmptyUnquotedDelimiters()
    {
        Assert.ThrowsExactly<FormatException>(() => PgArrayParser.Parse("{a,}"));
        Assert.ThrowsExactly<FormatException>(() => PgArrayParser.Parse("{,a}"));
        Assert.ThrowsExactly<FormatException>(() => PgArrayParser.Parse("{a,,b}"));
        // Quoted empty string remains valid.
        CollectionAssert.AreEqual(new[] { "", "a" }, PgArrayParser.Parse("{\"\",a}"));
    }

    [TestMethod]
    public async Task ScramWithoutServerFinalSignatureIsRejected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var startupHeader = new byte[8];
            await stream.ReadExactlyAsync(startupHeader);
            var startupLength = BinaryPrimitives.ReadInt32BigEndian(startupHeader);
            if (startupLength > 8)
            {
                await stream.ReadExactlyAsync(new byte[startupLength - 8]);
            }

            // Send AuthenticationSASL (10) advertising SCRAM-SHA-256
            await WritePgMessageAsync(stream, (byte)'R', [0, 0, 0, 10, .. Encoding.ASCII.GetBytes("SCRAM-SHA-256\0\0")]);
            // Read SASLInitialResponse ('p')
            var header = new byte[5];
            await stream.ReadExactlyAsync(header);
            var len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            var clientFirstPayload = new byte[len - 4];
            await stream.ReadExactlyAsync(clientFirstPayload);
            var clientFirst = Encoding.UTF8.GetString(clientFirstPayload);
            var nonceIdx = clientFirst.IndexOf("r=", StringComparison.Ordinal);
            var clientNonce = clientFirst[(nonceIdx + 2)..];

            // Send AuthenticationSASLContinue (11)
            var serverFirst = $"r={clientNonce}servernonce,s={Convert.ToBase64String(new byte[16])},i=4096";
            await WritePgMessageAsync(stream, (byte)'R', [0, 0, 0, 11, .. Encoding.UTF8.GetBytes(serverFirst)]);
            // Read SASLResponse ('p')
            await stream.ReadExactlyAsync(header);
            len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            await stream.ReadExactlyAsync(new byte[len - 4]);

            // Rogue server skips AuthenticationSASLFinal (12) and sends AuthenticationOk (0) + ReadyForQuery ('Z')
            await WritePgMessageAsync(stream, (byte)'R', [0, 0, 0, 0]);
            await WritePgMessageAsync(stream, (byte)'Z', [(byte)'I']);
        });

        await Assert.ThrowsExactlyAsync<AuthenticationException>(() =>
            PgClient.ConnectAsync(new PgConnectOptions
            {
                Host = "127.0.0.1",
                Port = port,
                Database = "app",
                Username = "app",
                Password = "secret",
                SslMode = PgSslMode.Disable,
            }).AsTask());

        await serverTask;
    }

    [TestMethod]
    public async Task EnlistTransactionHonorsAmbientIsolationLevel()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var queries = new List<string>();

        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var startupHeader = new byte[8];
            await stream.ReadExactlyAsync(startupHeader);
            var startupLength = BinaryPrimitives.ReadInt32BigEndian(startupHeader);
            if (startupLength > 8)
            {
                await stream.ReadExactlyAsync(new byte[startupLength - 8]);
            }

            await WritePgMessageAsync(stream, (byte)'R', [0, 0, 0, 0]);
            await WritePgMessageAsync(stream, (byte)'Z', [(byte)'I']);

            var header = new byte[5];
            while (true)
            {
                var read = await stream.ReadAsync(header);
                if (read == 0) break;
                var totalRead = read;
                while (totalRead < 5)
                {
                    var n = await stream.ReadAsync(header.AsMemory(totalRead));
                    if (n == 0) return;
                    totalRead += n;
                }

                var len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                var payload = len > 4 ? new byte[len - 4] : [];
                if (payload.Length > 0) await stream.ReadExactlyAsync(payload);

                if (header[0] == (byte)'X') break;
                if (header[0] == (byte)'Q')
                {
                    var sql = Encoding.UTF8.GetString(payload, 0, payload.Length - 1);
                    queries.Add(sql);
                    var tag = sql.StartsWith("BEGIN", StringComparison.Ordinal) ? "BEGIN\0" : "ROLLBACK\0";
                    var status = sql.StartsWith("BEGIN", StringComparison.Ordinal) ? (byte)'T' : (byte)'I';
                    await WritePgMessageAsync(stream, (byte)'C', Encoding.ASCII.GetBytes(tag));
                    await WritePgMessageAsync(stream, (byte)'Z', [status]);
                }
            }
        });

        await using var conn = await PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Database = "app",
            Username = "app",
            SslMode = PgSslMode.Disable,
        });

        using (var scope = new TransactionScope(
            TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = IsolationLevel.Serializable },
            TransactionScopeAsyncFlowOption.Enabled))
        {
            await conn.EnlistTransactionAsync(Transaction.Current!);
        }

        await conn.DisposeAsync();
        await serverTask;

        Assert.IsTrue(queries.Count >= 1);
        StringAssert.Contains(queries[0], "ISOLATION LEVEL SERIALIZABLE");
    }

    [TestMethod]
    public async Task PreparedEmptyQueryResponseDoesNotKillConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var startupHeader = new byte[8];
            await stream.ReadExactlyAsync(startupHeader);
            var startupLength = BinaryPrimitives.ReadInt32BigEndian(startupHeader);
            if (startupLength > 8) await stream.ReadExactlyAsync(new byte[startupLength - 8]);

            await WritePgMessageAsync(stream, (byte)'R', [0, 0, 0, 0]);
            await WritePgMessageAsync(stream, (byte)'Z', [(byte)'I']);

            var header = new byte[5];
            var syncCount = 0;
            while (true)
            {
                var read = await stream.ReadAsync(header);
                if (read == 0) break;
                var totalRead = read;
                while (totalRead < 5)
                {
                    var n = await stream.ReadAsync(header.AsMemory(totalRead));
                    if (n == 0) return;
                    totalRead += n;
                }

                var len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                var payload = len > 4 ? new byte[len - 4] : [];
                if (payload.Length > 0) await stream.ReadExactlyAsync(payload);

                if (header[0] == (byte)'X') break;
                if (header[0] == (byte)'S')
                {
                    syncCount++;
                    if (syncCount == 1)
                    {
                        // PrepareAsync response: ParseComplete, ParameterDescription(0), NoData, ReadyForQuery
                        await WritePgMessageAsync(stream, (byte)'1', []);
                        await WritePgMessageAsync(stream, (byte)'t', [0, 0]);
                        await WritePgMessageAsync(stream, (byte)'n', []);
                        await WritePgMessageAsync(stream, (byte)'Z', [(byte)'I']);
                    }
                    else if (syncCount == 2)
                    {
                        // ExecuteAsync response: BindComplete, EmptyQueryResponse ('I'), ReadyForQuery
                        await WritePgMessageAsync(stream, (byte)'2', []);
                        await WritePgMessageAsync(stream, (byte)'I', []);
                        await WritePgMessageAsync(stream, (byte)'Z', [(byte)'I']);
                    }
                    else
                    {
                        // Close prepared statement
                        await WritePgMessageAsync(stream, (byte)'3', []);
                        await WritePgMessageAsync(stream, (byte)'Z', [(byte)'I']);
                    }
                }
            }
        });

        await using var conn = await PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Database = "app",
            Username = "app",
            SslMode = PgSslMode.Disable,
        });

        await using (var stmt = await conn.PrepareAsync("-- empty"))
        {
            var result = await stmt.ExecuteAsync();
            Assert.AreEqual(0, result.AffectedRows);
        }

        Assert.IsTrue(conn.IsUsable);
        await conn.DisposeAsync();
        await serverTask;
    }

    [TestMethod]
    public async Task PgSubscriberImmediateSubscribeDoesNotDeadlock()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var listened = new List<string>();

        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var startupHeader = new byte[8];
            await stream.ReadExactlyAsync(startupHeader);
            var startupLength = BinaryPrimitives.ReadInt32BigEndian(startupHeader);
            if (startupLength > 8) await stream.ReadExactlyAsync(new byte[startupLength - 8]);

            await WritePgMessageAsync(stream, (byte)'R', [0, 0, 0, 0]);
            await WritePgMessageAsync(stream, (byte)'Z', [(byte)'I']);

            var header = new byte[5];
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(header);
                }
                catch
                {
                    break;
                }

                if (read == 0) break;
                var totalRead = read;
                while (totalRead < 5)
                {
                    var n = await stream.ReadAsync(header.AsMemory(totalRead));
                    if (n == 0) return;
                    totalRead += n;
                }

                var len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                var payload = len > 4 ? new byte[len - 4] : [];
                if (payload.Length > 0) await stream.ReadExactlyAsync(payload);

                if (header[0] == (byte)'X') break;
                if (header[0] == (byte)'Q')
                {
                    var sql = Encoding.UTF8.GetString(payload, 0, payload.Length - 1);
                    listened.Add(sql);
                    await WritePgMessageAsync(stream, (byte)'C', Encoding.ASCII.GetBytes("LISTEN\0"));
                    await WritePgMessageAsync(stream, (byte)'Z', [(byte)'I']);
                }
            }
        });

        await using var subscriber = await PgSubscriber.ConnectAsync(new PgConnectOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Database = "app",
            Username = "app",
            SslMode = PgSslMode.Disable,
        });

        for (var i = 0; i < 5; i++)
        {
            await subscriber.SubscribeAsync($"ch_{i}").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        await subscriber.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.HasCount(5, listened);
    }

    [TestMethod]
    public async Task CopyErrorAfterReadyForQueryKeepsConnectionUsable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var startupHeader = new byte[8];
            await stream.ReadExactlyAsync(startupHeader);
            var startupLength = BinaryPrimitives.ReadInt32BigEndian(startupHeader);
            if (startupLength > 8) await stream.ReadExactlyAsync(new byte[startupLength - 8]);

            await WritePgMessageAsync(stream, (byte)'R', [0, 0, 0, 0]);
            await WritePgMessageAsync(stream, (byte)'Z', [(byte)'I']);

            var header = new byte[5];
            while (true)
            {
                var read = await stream.ReadAsync(header);
                if (read == 0) break;
                var totalRead = read;
                while (totalRead < 5)
                {
                    var n = await stream.ReadAsync(header.AsMemory(totalRead));
                    if (n == 0) return;
                    totalRead += n;
                }

                var len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                var payload = len > 4 ? new byte[len - 4] : [];
                if (payload.Length > 0) await stream.ReadExactlyAsync(payload);

                if (header[0] == (byte)'X') break;
                if (header[0] == (byte)'Q')
                {
                    var sql = Encoding.UTF8.GetString(payload, 0, payload.Length - 1);
                    if (sql.StartsWith("COPY", StringComparison.Ordinal))
                    {
                        await WritePgMessageAsync(
                            stream,
                            (byte)'E',
                            Encoding.ASCII.GetBytes("SERROR\0C42P01\0Mmissing table\0\0"));
                        await WritePgMessageAsync(stream, (byte)'Z', [(byte)'I']);
                    }
                    else
                    {
                        await WritePgMessageAsync(stream, (byte)'C', Encoding.ASCII.GetBytes("SELECT 0\0"));
                        await WritePgMessageAsync(stream, (byte)'Z', [(byte)'I']);
                    }
                }
            }
        });

        await using var conn = await PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Database = "app",
            Username = "app",
            SslMode = PgSslMode.Disable,
        });

        await Assert.ThrowsExactlyAsync<PgException>(() =>
            conn.BeginBinaryImportAsync("COPY missing_table FROM STDIN (FORMAT BINARY)").AsTask());

        Assert.IsTrue(conn.IsUsable);
        _ = await conn.ExecuteAsync("SELECT 1");

        await conn.DisposeAsync();
        await serverTask;
    }

    [TestMethod]
    public async Task TransactionDisposeOnBrokenConnectionDoesNotMaskException()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var startupHeader = new byte[8];
            await stream.ReadExactlyAsync(startupHeader);
            var startupLength = BinaryPrimitives.ReadInt32BigEndian(startupHeader);
            if (startupLength > 8) await stream.ReadExactlyAsync(new byte[startupLength - 8]);

            await WritePgMessageAsync(stream, (byte)'R', [0, 0, 0, 0]);
            await WritePgMessageAsync(stream, (byte)'Z', [(byte)'I']);

            var header = new byte[5];
            await stream.ReadExactlyAsync(header);
            var len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            await stream.ReadExactlyAsync(new byte[len - 4]);
            await WritePgMessageAsync(stream, (byte)'C', Encoding.ASCII.GetBytes("BEGIN\0"));
            await WritePgMessageAsync(stream, (byte)'Z', [(byte)'T']);

            // Drop the connection on the next query.
            await stream.ReadExactlyAsync(header);
            socket.Close();
        });

        await using var conn = await PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Database = "app",
            Username = "app",
            SslMode = PgSslMode.Disable,
        });

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync("SELECT 1");
        });

        await serverTask;
    }

    private static async Task WritePgMessageAsync(Stream stream, byte type, byte[] payload)
    {
        var buffer = new byte[5 + payload.Length];
        buffer[0] = type;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), 4 + payload.Length);
        payload.CopyTo(buffer, 5);
        await stream.WriteAsync(buffer);
        await stream.FlushAsync();
    }

    private static byte[] CreateSingleFieldRow(byte[]? field)
    {
        if (field is null)
        {
            var row = new byte[6];
            BinaryPrimitives.WriteInt16BigEndian(row, 1);
            BinaryPrimitives.WriteInt32BigEndian(row.AsSpan(2), -1);
            return row;
        }

        var data = new byte[6 + field.Length];
        BinaryPrimitives.WriteInt16BigEndian(data, 1);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(2), field.Length);
        field.CopyTo(data, 6);
        return data;
    }

    private static byte[] CreateBinaryArray(uint elementOid, byte[][] elements)
    {
        var total = 20;
        foreach (var e in elements) total += 4 + e.Length;
        var buffer = new byte[total];
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0), 1); // ndim
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(4), 0); // flags
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8), elementOid);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(12), elements.Length);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(16), 1); // lower bound
        var pos = 20;
        foreach (var e in elements)
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(pos), e.Length);
            pos += 4;
            e.CopyTo(buffer, pos);
            pos += e.Length;
        }

        return buffer;
    }

    private readonly record struct CustomValue(int Id);
    private sealed record CustomInt(int Value);
}

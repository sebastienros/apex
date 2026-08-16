using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Apex.MySqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MySqlClient.Tests;

/// <summary>
/// Exercises <see cref="MySqlConnection"/> against a minimal hand written MySQL protocol server,
/// the same style used by the PostgreSQL driver's wire tests, so the handshake, simple query,
/// prepared statement, multi-result, streaming reader, cancellation and pipelining behaviors are
/// verified against real wire bytes rather than mocks.
/// </summary>
[TestClass]
public sealed class MySqlConnectionWireTests
{
    private static readonly byte[] s_nonce = Enumerable.Range(1, 20).Select(static i => (byte)i).ToArray();

    private const uint ServerCapabilities =
      (uint)(MySqlCapabilities.LongPassword | MySqlCapabilities.LongFlag | MySqlCapabilities.Protocol41 |
             MySqlCapabilities.Transactions | MySqlCapabilities.SecureConnection |
             MySqlCapabilities.PluginAuth | MySqlCapabilities.PluginAuthLengthEncodedClientData |
             MySqlCapabilities.MultiResults | MySqlCapabilities.PreparedStatementMultiResults |
             MySqlCapabilities.DeprecateEof | MySqlCapabilities.ConnectWithDatabase |
             MySqlCapabilities.FoundRows);

    [TestMethod]
    public async Task ConnectsAndExecutesSimpleTextQuery()
    {
        await using var harness = await ServerHarness.StartAsync();
        Task server = Task.Run(async () =>
        {
            await using var connection = await harness.AcceptAsync();
            await connection.CompleteHandshakeAsync("8.4.2");

            await connection.ExpectTextCommandAsync(MySqlCommand.Query, "SELECT 1 AS id, 'hello' AS message");
            await connection.WriteColumnCountAsync(2);
            await connection.WriteColumnAsync("id", MySqlType.Long);
            await connection.WriteColumnAsync("message", MySqlType.VarString);
            await connection.WriteTextRowAsync("1", "hello");
            await connection.WriteFinalOkAsync();

            await connection.ExpectCommandAsync(MySqlCommand.Quit);
        });

        await using var client = await MySqlClient.ConnectAsync(harness.CreateOptions());
        var result = await client.QueryAsync("SELECT 1 AS id, 'hello' AS message");

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(1, result[0].Get<int>("id"));
        Assert.AreEqual("hello", result[0].Get<string>("message"));
        Assert.AreEqual("MySQL", client.DatabaseMetadata.ProductName);
        Assert.AreEqual(8, client.DatabaseMetadata.MajorVersion);

        await client.DisposeAsync();
        await server;
    }

    [TestMethod]
    public async Task ParsesMariaDbCompatibilityVersionPrefixDuringHandshake()
    {
        await using var harness = await ServerHarness.StartAsync();
        Task server = Task.Run(async () =>
        {
            await using var connection = await harness.AcceptAsync();
            await connection.CompleteHandshakeAsync("5.5.5-11.8.2-MariaDB");
            await connection.ExpectCommandAsync(MySqlCommand.Quit);
        });

        await using var client = await MySqlClient.ConnectAsync(harness.CreateOptions());

        Assert.IsTrue(client.ServerVersion.IsMariaDb);
        Assert.AreEqual(11, client.ServerVersion.Major);
        Assert.AreEqual(8, client.ServerVersion.Minor);
        Assert.AreEqual(2, client.ServerVersion.Micro);
        Assert.AreEqual("MariaDB", client.DatabaseMetadata.ProductName);

        await client.DisposeAsync();
        await server;
    }

        [TestMethod]
        public async Task SendsClearPasswordOnlyOverExplicitlyEnabledTls()
        {
                using var certificate = CreateCertificate();
                await using var harness = await ServerHarness.StartAsync();
                Task server = Task.Run(async () =>
                {
                        await using var connection = await harness.AcceptAsync();
                        var response = await connection.CompleteTlsHandshakeAsync(
                            "8.4.2",
                            certificate,
                            MySqlProtocol.ClearPasswordPlugin);
                        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("pass\0"), response);
                        await connection.ExpectCommandAsync(MySqlCommand.Quit);
                });

                await using var client = await MySqlClient.ConnectAsync(
                    harness.CreateOptions() with
                    {
                            SslMode = MySqlSslMode.Required,
                            AuthenticationPlugin = MySqlAuthenticationPlugin.ClearPassword,
                            AllowCleartextPassword = true,
                    });

                Assert.IsTrue(client.IsSecure);
                await client.DisposeAsync();
                await server;
        }

        [TestMethod]
        public async Task SendsSha256PasswordInClearOnlyOverTls()
        {
                using var certificate = CreateCertificate();
                await using var harness = await ServerHarness.StartAsync();
                Task server = Task.Run(async () =>
                {
                        await using var connection = await harness.AcceptAsync();
                        var response = await connection.CompleteTlsHandshakeAsync(
                            "8.4.2",
                            certificate,
                            MySqlProtocol.Sha256PasswordPlugin);
                        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("pass\0"), response);
                        await connection.ExpectCommandAsync(MySqlCommand.Quit);
                });

                await using var client = await MySqlClient.ConnectAsync(
                    harness.CreateOptions() with
                    {
                            SslMode = MySqlSslMode.Required,
                            AuthenticationPlugin = MySqlAuthenticationPlugin.Sha256Password,
                    });

                Assert.IsTrue(client.IsSecure);
                await client.DisposeAsync();
                await server;
        }

        [TestMethod]
        public async Task RetrievesPublicKeyForUnencryptedSha256Password()
        {
                using RSA rsa = RSA.Create(2048);
                var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
                await using var harness = await ServerHarness.StartAsync();
                Task server = Task.Run(async () =>
                {
                        await using var connection = await harness.AcceptAsync();
                        var encrypted = await connection.CompleteSha256PublicKeyHandshakeAsync(
                            "8.4.2",
                            publicKey);
                        var clear = rsa.Decrypt(encrypted, RSAEncryptionPadding.OaepSHA1);
                        for (var index = 0; index < clear.Length; index++)
                        {
                                clear[index] ^= s_nonce[index % s_nonce.Length];
                        }

                        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("pass\0"), clear);
                        await connection.ExpectCommandAsync(MySqlCommand.Quit);
                });

                await using var client = await MySqlClient.ConnectAsync(
                    harness.CreateOptions() with
                    {
                            SslMode = MySqlSslMode.Disabled,
                            AuthenticationPlugin = MySqlAuthenticationPlugin.Sha256Password,
                            AllowPublicKeyRetrieval = true,
                    });

                Assert.IsFalse(client.IsSecure);
                await client.DisposeAsync();
                await server;
        }

    [TestMethod]
    public async Task PreparesExecutesAndClosesStatement()
    {
        await using var harness = await ServerHarness.StartAsync();
        Task server = Task.Run(async () =>
        {
            await using var connection = await harness.AcceptAsync();
            await connection.CompleteHandshakeAsync("8.4.2");

            await connection.ExpectTextCommandAsync(MySqlCommand.StatementPrepare, "SELECT ? AS id");
            await connection.WritePrepareOkAsync(statementId: 7, columnCount: 1, parameterCount: 1);
            await connection.WriteColumnAsync("p", MySqlType.Long);
            await connection.WriteColumnAsync("id", MySqlType.Long);

            var executePayload = await connection.ExpectCommandPayloadAsync(MySqlCommand.StatementExecute);
            Assert.AreEqual(7u, BinaryPrimitives.ReadUInt32LittleEndian(executePayload.AsSpan(1)));
            await connection.WriteColumnCountAsync(1);
            await connection.WriteColumnAsync("id", MySqlType.Long);
            await connection.WriteBinaryRowAsync([MySqlType.Long], [42]);
            await connection.WriteFinalOkAsync();

            var closePayload = await connection.ExpectCommandPayloadAsync(MySqlCommand.StatementClose);
            Assert.AreEqual(7u, BinaryPrimitives.ReadUInt32LittleEndian(closePayload.AsSpan(1)));

            await connection.ExpectCommandAsync(MySqlCommand.Quit);
        });

        await using var client = await MySqlClient.ConnectAsync(harness.CreateOptions());
        await using var statement = await client.PrepareAsync("SELECT ? AS id");

        var rows = await statement.QueryAsync(SqlParameters.Create(42));

        Assert.AreEqual(42, rows[0].Get<int>("id"));

        await statement.DisposeAsync();
        await client.DisposeAsync();
        await server;
    }

    [TestMethod]
    public async Task ChainsMultipleResultSetsUsingMoreResultsExistsFlag()
    {
        await using var harness = await ServerHarness.StartAsync();
        Task server = Task.Run(async () =>
        {
            await using var connection = await harness.AcceptAsync();
            await connection.CompleteHandshakeAsync("8.4.2");

            await connection.ExpectTextCommandAsync(MySqlCommand.Query, "CALL multi()");
            await connection.WriteColumnCountAsync(1);
            await connection.WriteColumnAsync("a", MySqlType.Long);
            await connection.WriteTextRowAsync("1");
            await connection.WriteOkWithMoreResultsAsync();

            await connection.WriteColumnCountAsync(1);
            await connection.WriteColumnAsync("b", MySqlType.Long);
            await connection.WriteTextRowAsync("2");
            await connection.WriteFinalOkAsync();

            await connection.ExpectCommandAsync(MySqlCommand.Quit);
        });

        await using var client = await MySqlClient.ConnectAsync(harness.CreateOptions());
        var first = await client.QueryAsync("CALL multi()");

        Assert.AreEqual(1, first[0].Get<int>("a"));
        Assert.IsNotNull(first.Next);
        Assert.AreEqual(2, first.Next![0].Get<int>("b"));
        Assert.IsNull(first.Next!.Next);

        await client.DisposeAsync();
        await server;
    }

    [TestMethod]
    public async Task StreamsRowsThroughTheRowReader()
    {
        await using var harness = await ServerHarness.StartAsync();
        Task server = Task.Run(async () =>
        {
            await using var connection = await harness.AcceptAsync();
            await connection.CompleteHandshakeAsync("8.4.2");

            await connection.ExpectTextCommandAsync(MySqlCommand.Query, "SELECT n FROM sequence");
            await connection.WriteColumnCountAsync(1);
            await connection.WriteColumnAsync("n", MySqlType.Long);
            await connection.WriteTextRowAsync("1");
            await connection.WriteTextRowAsync("2");
            await connection.WriteTextRowAsync("3");
            await connection.WriteFinalOkAsync();

            await connection.ExpectCommandAsync(MySqlCommand.Quit);
        });

        await using var client = await MySqlClient.ConnectAsync(harness.CreateOptions());
        List<int> values = [];
        await using (var reader = await client.ExecuteReaderAsync("SELECT n FROM sequence"))
        {
            // Column metadata only becomes available once the pump has read the first result set's
            // header, which is guaranteed after the first ReadAsync call returns.
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual(1, reader.FieldCount);
            Assert.AreEqual("n", reader.Columns[0].Name);
            values.Add(reader.GetInt32(0));
            while (await reader.ReadAsync())
            {
                values.Add(reader.GetInt32(0));
            }
        }

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, values);

        await client.DisposeAsync();
        await server;
    }

    [TestMethod]
    public async Task CancellationSendsKillQueryOnASecondConnectionAndReusesTheFirst()
    {
        await using var harness = await ServerHarness.StartAsync();
        TaskCompletionSource killReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = Task.Run(async () =>
        {
            await using var main = await harness.AcceptAsync();
            await main.CompleteHandshakeAsync("8.4.2", connectionId: 555);

            await main.ExpectTextCommandAsync(MySqlCommand.Query, "SELECT SLEEP(10)");

            await using var admin = await harness.AcceptAsync();
            await admin.CompleteHandshakeAsync("8.4.2", connectionId: 556);
            var killSql = await admin.ExpectTextCommandStartingWithAsync(MySqlCommand.Query, "KILL QUERY 555");
            Assert.AreEqual("KILL QUERY 555", killSql);
            await admin.WriteCommandOkAsync();
            await admin.ExpectCommandAsync(MySqlCommand.Quit);
            killReceived.SetResult();

            // The server now reports the original query as interrupted, as a real server would after
            // KILL QUERY lands, so the main connection can be reused for the next command.
            await main.WriteErrorAsync(1317, "45000", "Query execution was interrupted");

            await main.ExpectTextCommandAsync(MySqlCommand.Query, "SELECT 1");
            await main.WriteColumnCountAsync(1);
            await main.WriteColumnAsync("v", MySqlType.Long);
            await main.WriteTextRowAsync("1");
            await main.WriteFinalOkAsync();

            await main.ExpectCommandAsync(MySqlCommand.Quit);
        });

        await using var client = await MySqlClient.ConnectAsync(harness.CreateOptions());
        using CancellationTokenSource cancellation = new();
        var pending = client.QueryAsync("SELECT SLEEP(10)", cancellation.Token);
        await Task.Delay(100);
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => pending.AsTask());
        await killReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var rows = await client.QueryAsync("SELECT 1");
        Assert.AreEqual(1, rows[0].Get<int>("v"));

        await client.DisposeAsync();
        await server;
    }

    [TestMethod]
    public async Task PipelinedQueriesReceiveResponsesInSubmissionOrder()
    {
        await using var harness = await ServerHarness.StartAsync();
        const int count = 8;
        Task server = Task.Run(async () =>
        {
            await using var connection = await harness.AcceptAsync();
            await connection.CompleteHandshakeAsync("8.4.2");

            for (var i = 0; i < count; i++)
            {
                await connection.ExpectTextCommandAsync(MySqlCommand.Query, $"SELECT {i}");
                await connection.WriteColumnCountAsync(1);
                await connection.WriteColumnAsync("v", MySqlType.Long);
                await connection.WriteTextRowAsync(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await connection.WriteFinalOkAsync();
            }

            await connection.ExpectCommandAsync(MySqlCommand.Quit);
        });

        await using var client = await MySqlClient.ConnectAsync(
          new MySqlConnectOptions
          {
              Host = harness.Host,
              Port = harness.Port,
              Username = "user",
              Password = "pass",
              PipeliningLimit = count,
          });

        var queries = Enumerable.Range(0, count)
          .Select(i => client.QueryAsync($"SELECT {i}").AsTask())
          .ToArray();
        var results = await Task.WhenAll(queries);

        for (var i = 0; i < count; i++)
        {
            Assert.AreEqual(i, results[i][0].Get<int>("v"));
        }

        await client.DisposeAsync();
        await server;
    }

    private sealed class ServerHarness : IAsyncDisposable
    {
        private readonly TcpListener _listener;

        private ServerHarness(TcpListener listener)
        {
            _listener = listener;
        }

        internal string Host => "127.0.0.1";

        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        internal static Task<ServerHarness> StartAsync()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new ServerHarness(listener));
        }

        internal async Task<FakeConnection> AcceptAsync()
        {
            var client = await _listener.AcceptTcpClientAsync();
            return new FakeConnection(client);
        }

        internal MySqlConnectOptions CreateOptions() =>
          new()
          {
              Host = Host,
              Port = Port,
              Username = "user",
              Password = "pass",
          };

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A single accepted connection speaking the server side of the MySQL wire protocol.</summary>
    private sealed class FakeConnection : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private Stream _stream;
        private byte _sequence;

        internal FakeConnection(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        internal async Task CompleteHandshakeAsync(
            string serverVersion,
            uint connectionId = 42,
            string authPlugin = MySqlProtocol.NativePasswordPlugin)
        {
            var handshake = BuildHandshakePacket(serverVersion, connectionId, authPlugin);
            await WritePacketAsync(handshake);

            // The handshake response is not validated byte for byte here; framing and authentication
            // scrambles are covered by MySqlAuthenticationTests. We only need to advance the sequence.
            _ = await ReadPacketAsync();
            await WriteCommandOkAsync();
        }

                internal async Task<byte[]> CompleteTlsHandshakeAsync(
                        string serverVersion,
                        X509Certificate2 certificate,
                        string authPlugin)
                {
                        var handshake = BuildHandshakePacket(
                            serverVersion,
                            connectionId: 42,
                            authPlugin,
                            supportsTls: true);
                        await WritePacketAsync(handshake);
                        _ = await ReadPacketAsync();
                        SslStream tls = new(_stream, leaveInnerStreamOpen: false);
                        await tls.AuthenticateAsServerAsync(
                            certificate,
                            clientCertificateRequired: false,
                            enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
                            checkCertificateRevocation: false);
                        _stream = tls;
                        var response = ExtractAuthenticationResponse(await ReadPacketAsync());
                        await WriteCommandOkAsync();
                        return response;
                }

                internal async Task<byte[]> CompleteSha256PublicKeyHandshakeAsync(
                        string serverVersion,
                        string publicKey)
                {
                        var handshake = BuildHandshakePacket(
                            serverVersion,
                            connectionId: 42,
                            MySqlProtocol.Sha256PasswordPlugin);
                        await WritePacketAsync(handshake);
                        var request = ExtractAuthenticationResponse(await ReadPacketAsync());
                        CollectionAssert.AreEqual(
                            new byte[] { MySqlProtocol.Sha256PublicKeyRequest },
                            request);
                        await WritePacketAsync(
                            [MySqlProtocol.AuthMoreDataHeader, .. Encoding.UTF8.GetBytes(publicKey), 0]);
                        var encrypted = await ReadPacketAsync();
                        await WriteCommandOkAsync();
                        return encrypted;
                }

        internal async Task<byte[]> ExpectCommandPayloadAsync(MySqlCommand command)
        {
            var payload = await ReadPacketAsync();
            Assert.AreEqual((byte)command, payload[0]);
            return payload;
        }

        internal async Task ExpectCommandAsync(MySqlCommand command) =>
          _ = await ExpectCommandPayloadAsync(command);

        internal async Task ExpectTextCommandAsync(MySqlCommand command, string expectedSql)
        {
            var payload = await ExpectCommandPayloadAsync(command);
            var sql = Encoding.UTF8.GetString(payload.AsSpan(1));
            Assert.AreEqual(expectedSql, sql);
        }

        internal async Task<string> ExpectTextCommandStartingWithAsync(MySqlCommand command, string prefix)
        {
            var payload = await ExpectCommandPayloadAsync(command);
            var sql = Encoding.UTF8.GetString(payload.AsSpan(1));
            Assert.IsTrue(sql.StartsWith(prefix, StringComparison.Ordinal), $"'{sql}' does not start with '{prefix}'.");
            return sql;
        }

        internal Task WriteColumnCountAsync(int count)
        {
            MySqlPayloadWriter writer = new();
            try
            {
                writer.WriteLengthEncodedInteger((ulong)count);
                return WritePacketAsync(writer.WrittenSpan.ToArray());
            }
            finally
            {
                writer.Release();
            }
        }

        internal Task WritePrepareOkAsync(uint statementId, int columnCount, int parameterCount)
        {
            MySqlPayloadWriter writer = new();
            try
            {
                writer.WriteByte(0x00);
                writer.WriteUInt32(statementId);
                writer.WriteUInt16((ushort)columnCount);
                writer.WriteUInt16((ushort)parameterCount);
                writer.WriteByte(0);
                writer.WriteUInt16(0);
                return WritePacketAsync(writer.WrittenSpan.ToArray());
            }
            finally
            {
                writer.Release();
            }
        }

        internal Task WriteColumnAsync(string name, MySqlType type, bool unsigned = false)
        {
            MySqlPayloadWriter writer = new();
            try
            {
                writer.WriteLengthEncodedString("def");
                writer.WriteLengthEncodedString(string.Empty);
                writer.WriteLengthEncodedString(string.Empty);
                writer.WriteLengthEncodedString(string.Empty);
                writer.WriteLengthEncodedString(name);
                writer.WriteLengthEncodedString(name);
                writer.WriteLengthEncodedInteger(12);
                writer.WriteUInt16(MySqlProtocol.Utf8Mb4Collation);
                writer.WriteUInt32(11);
                writer.WriteByte((byte)type);
                writer.WriteUInt16(unsigned ? (ushort)MySqlColumnFlags.Unsigned : (ushort)0);
                writer.WriteByte(0);
                writer.WriteUInt16(0);
                return WritePacketAsync(writer.WrittenSpan.ToArray());
            }
            finally
            {
                writer.Release();
            }
        }

        internal Task WriteTextRowAsync(params string?[] values)
        {
            MySqlPayloadWriter writer = new();
            try
            {
                foreach (var value in values)
                {
                    if (value is null)
                    {
                        writer.WriteByte(0xFB);
                    }
                    else
                    {
                        writer.WriteLengthEncodedString(value);
                    }
                }

                return WritePacketAsync(writer.WrittenSpan.ToArray());
            }
            finally
            {
                writer.Release();
            }
        }

        internal Task WriteBinaryRowAsync(MySqlType[] types, long?[] values)
        {
            var bitmapLength = (types.Length + 9) / 8;
            var bitmap = new byte[bitmapLength];
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] is null)
                {
                    var bit = i + 2;
                    bitmap[bit >> 3] |= (byte)(1 << (bit & 7));
                }
            }

            MySqlPayloadWriter writer = new();
            try
            {
                writer.WriteByte(0x00);
                writer.WriteBytes(bitmap);
                for (var i = 0; i < values.Length; i++)
                {
                    if (values[i] is not { } value)
                    {
                        continue;
                    }

                    switch (types[i])
                    {
                        case MySqlType.Tiny:
                            writer.WriteByte((byte)value);
                            break;
                        case MySqlType.Short:
                            writer.WriteUInt16((ushort)value);
                            break;
                        case MySqlType.Long:
                            writer.WriteInt32((int)value);
                            break;
                        case MySqlType.LongLong:
                            writer.WriteInt64(value);
                            break;
                        default:
                            throw new NotSupportedException($"Test helper does not encode {types[i]}.");
                    }
                }

                return WritePacketAsync(writer.WrittenSpan.ToArray());
            }
            finally
            {
                writer.Release();
            }
        }

        internal Task WriteFinalOkAsync() => WriteOkPacketAsync(moreResults: false);

        internal Task WriteOkWithMoreResultsAsync() => WriteOkPacketAsync(moreResults: true);

        internal Task WriteErrorAsync(int errorNumber, string sqlState, string message)
        {
            MySqlPayloadWriter writer = new();
            try
            {
                writer.WriteByte(0xFF);
                writer.WriteUInt16((ushort)errorNumber);
                writer.WriteByte((byte)'#');
                writer.WriteUtf8(sqlState);
                writer.WriteUtf8(message);
                return WritePacketAsync(writer.WrittenSpan.ToArray());
            }
            finally
            {
                writer.Release();
            }
        }

        internal Task WriteCommandOkAsync()
        {
            MySqlPayloadWriter writer = new();
            try
            {
                writer.WriteByte(0x00);
                writer.WriteLengthEncodedInteger(0);
                writer.WriteLengthEncodedInteger(0);
                writer.WriteUInt16((ushort)MySqlServerStatus.AutoCommit);
                writer.WriteUInt16(0);
                return WritePacketAsync(writer.WrittenSpan.ToArray());
            }
            finally
            {
                writer.Release();
            }
        }

        private Task WriteOkPacketAsync(bool moreResults)
        {
            MySqlPayloadWriter writer = new();
            try
            {
                // With CLIENT_DEPRECATE_EOF negotiated, the terminating packet of a result set uses the
                // EOF header byte (0xFE) but the OK packet body layout.
                writer.WriteByte(MySqlProtocol.EofHeader);
                writer.WriteLengthEncodedInteger(0);
                writer.WriteLengthEncodedInteger(0);
                var status = (ushort)MySqlServerStatus.AutoCommit;
                if (moreResults)
                {
                    status |= (ushort)MySqlServerStatus.MoreResultsExist;
                }

                writer.WriteUInt16(status);
                writer.WriteUInt16(0);
                return WritePacketAsync(writer.WrittenSpan.ToArray());
            }
            finally
            {
                writer.Release();
            }
        }

        private static byte[] BuildHandshakePacket(
            string serverVersion,
            uint connectionId,
            string authPlugin,
            bool supportsTls = false)
        {
            MySqlPayloadWriter writer = new();
            try
            {
                var capabilities = supportsTls
                  ? ServerCapabilities | (uint)MySqlCapabilities.Ssl
                  : ServerCapabilities;
                writer.WriteByte(10);
                writer.WriteNullTerminatedString(serverVersion);
                writer.WriteUInt32(connectionId);
                writer.WriteBytes(s_nonce.AsSpan(0, 8));
                writer.WriteByte(0);
                writer.WriteUInt16((ushort)(capabilities & 0xFFFF));
                writer.WriteByte(MySqlProtocol.Utf8Mb4Collation);
                writer.WriteUInt16((ushort)MySqlServerStatus.AutoCommit);
                writer.WriteUInt16((ushort)((capabilities >> 16) & 0xFFFF));
                writer.WriteByte(21);
                writer.WriteZero(10);
                writer.WriteBytes(s_nonce.AsSpan(8, 12));
                writer.WriteByte(0);
                writer.WriteNullTerminatedString(authPlugin);
                return writer.WrittenSpan.ToArray();
            }
            finally
            {
                writer.Release();
            }
        }

        private static byte[] ExtractAuthenticationResponse(ReadOnlySpan<byte> response)
        {
            var position = 4 + 4 + 1 + 23;
            while (position < response.Length && response[position++] != 0)
            {
            }

            var length = response[position++];
            return response.Slice(position, length).ToArray();
        }

        private async Task WritePacketAsync(byte[] payload)
        {
            var frame = new byte[payload.Length + MySqlProtocol.PacketHeaderLength];
            MySqlPacketReader.WriteHeader(frame, payload.Length, _sequence++);
            payload.CopyTo(frame, MySqlProtocol.PacketHeaderLength);
            await _stream.WriteAsync(frame);
            await _stream.FlushAsync();
        }

        private async Task<byte[]> ReadPacketAsync()
        {
            var header = new byte[MySqlProtocol.PacketHeaderLength];
            await _stream.ReadExactlyAsync(header);
            var length = header[0] | (header[1] << 8) | (header[2] << 16);
            _sequence = (byte)(header[3] + 1);
            var payload = new byte[length];
            await _stream.ReadExactlyAsync(payload);
            return payload;
        }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            _client.Dispose();
        }
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
                return request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddMinutes(-5),
                    DateTimeOffset.UtcNow.AddDays(1));
        }
}

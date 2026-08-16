using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Apex.MsSqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MsSqlClient.Tests;

[TestClass]
public sealed class MsSqlFedAuthWireTests
{
    private const string AccessToken = "eyJhbGciOiJub25lIn0.access.token";

    [TestMethod]
    public async Task BearerTokenLoginNegotiatesFederatedAuthentication()
    {
        using var certificate = CreateCertificate();
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        FedAuthServerState state = new();
        var server = RunFedAuthServerAsync(listener, certificate, state, sendFedAuthInfo: false);
        try
        {
            await using var connection = await MsSqlClient.ConnectAsync(
              BearerOptions(port, _ => AccessToken));

            Assert.IsTrue(connection.IsSecure);
            Assert.AreEqual(7, (await connection.QueryAsync("SELECT 7"))[0].GetInt32(0));
            await server;
            Assert.IsTrue(state.PreLoginRequestedFedAuth);
            Assert.IsTrue(state.LoginEchoedFedAuthRequired);
            Assert.AreEqual(AccessToken, state.LoginToken);
            Assert.AreEqual(0, state.LoginUserNameLength);
            Assert.AreEqual(0, state.LoginPasswordLength);
            Assert.IsNull(state.MessageToken);
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task FedAuthInfoIsAnsweredWithFederatedAuthenticationTokenMessage()
    {
        using var certificate = CreateCertificate();
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        FedAuthServerState state = new();
        var server = RunFedAuthServerAsync(listener, certificate, state, sendFedAuthInfo: true);
        try
        {
            await using var connection = await MsSqlClient.ConnectAsync(
              BearerOptions(port, _ => AccessToken));

            Assert.AreEqual(7, (await connection.QueryAsync("SELECT 7"))[0].GetInt32(0));
            await server;
            Assert.AreEqual(AccessToken, state.MessageToken);
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task MissingFederatedAuthenticationAcknowledgementFailsLogin()
    {
        using var certificate = CreateCertificate();
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        FedAuthServerState state = new() { AcknowledgeFedAuth = false };
        var server = RunFedAuthServerAsync(listener, certificate, state, sendFedAuthInfo: false);
        try
        {
            var error = await Assert.ThrowsExactlyAsync<AuthenticationException>(
              async () => await MsSqlClient.ConnectAsync(BearerOptions(port, _ => AccessToken)));

            StringAssert.Contains(error.Message, "FEDAUTH");
        }
        finally
        {
            await ObserveAsync(server);
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task UnexpectedFedAuthInfoFailsPasswordLogin()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunUnexpectedFedAuthInfoServerAsync(listener);
        try
        {
            var error = await Assert.ThrowsExactlyAsync<AuthenticationException>(
              async () => await MsSqlClient.ConnectAsync(PasswordOptions(port)));

            StringAssert.Contains(error.Message, "bearer token");
        }
        finally
        {
            await ObserveAsync(server);
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task BearerTokenRequiresEncryptedTransport()
    {
        var error = await Assert.ThrowsExactlyAsync<AuthenticationException>(
          async () => await MsSqlClient.ConnectAsync(
            BearerOptions(1433, _ => AccessToken) with
            {
                EncryptionMode = MsSqlEncryptionMode.Optional,
            }));

        StringAssert.Contains(error.Message, "encryption");
    }

    [TestMethod]
    public async Task BearerTokenRejectsTrustedServerCertificate()
    {
        var error = await Assert.ThrowsExactlyAsync<AuthenticationException>(
          async () => await MsSqlClient.ConnectAsync(
            BearerOptions(1433, _ => AccessToken) with { TrustServerCertificate = true }));

        StringAssert.Contains(error.Message, "TrustServerCertificate");
    }

    [TestMethod]
    public async Task PasswordProviderKeepsClearTextLoginBehavior()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        FedAuthServerState state = new();
        var server = RunPlainLoginServerAsync(listener, state, routeToPort: null);
        try
        {
            await using var connection = await MsSqlClient.ConnectAsync(
              PasswordOptions(port) with
              {
                  AuthenticationProvider = _ => ValueTask.FromResult(
                    new SqlAuthenticationCredential(
                      "rotated",
                      SqlAuthenticationMethod.Password,
                      "rotated-user")),
              });

            Assert.AreEqual(7, (await connection.QueryAsync("SELECT 7"))[0].GetInt32(0));
            await server;
            Assert.IsFalse(state.PreLoginRequestedFedAuth);
            Assert.AreEqual("rotated-user", state.LoginUserName);
            Assert.AreEqual("rotated", state.LoginPassword);
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task ProviderIsResolvedAgainForEachRoutingRedirect()
    {
        TcpListener origin = new(IPAddress.Loopback, 0);
        TcpListener target = new(IPAddress.Loopback, 0);
        origin.Start();
        target.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        FedAuthServerState originState = new();
        FedAuthServerState targetState = new();
        var originServer = RunPlainLoginServerAsync(origin, originState, targetPort);
        var targetServer = RunPlainLoginServerAsync(target, targetState, routeToPort: null);
        var resolved = 0;
        try
        {
            await using var connection = await MsSqlClient.ConnectAsync(
              PasswordOptions(originPort) with
              {
                  AuthenticationProvider = _ => ValueTask.FromResult(
                    new SqlAuthenticationCredential(
                      $"secret-{Interlocked.Increment(ref resolved)}",
                      SqlAuthenticationMethod.Password)),
              });

            Assert.AreEqual(7, (await connection.QueryAsync("SELECT 7"))[0].GetInt32(0));
            await originServer;
            await targetServer;
            Assert.AreEqual(2, resolved);
            Assert.AreEqual("secret-1", originState.LoginPassword);
            Assert.AreEqual("secret-2", targetState.LoginPassword);
        }
        finally
        {
            origin.Stop();
            target.Stop();
        }
    }

    private static MsSqlConnectOptions BearerOptions(int port, Func<CancellationToken, string> token) =>
      new()
      {
          Host = "127.0.0.1",
          Port = port,
          Database = "master",
          EncryptionMode = MsSqlEncryptionMode.Require,
          TlsHostName = "localhost",
          CertificateValidationCallback = static (_, _, _, _) => true,
          ConnectTimeout = TimeSpan.FromSeconds(5),
          AuthenticationProvider = cancellationToken => ValueTask.FromResult(
            new SqlAuthenticationCredential(
              token(cancellationToken),
              SqlAuthenticationMethod.BearerToken,
              expiresOn: DateTimeOffset.UtcNow.AddHours(1))),
      };

    private static MsSqlConnectOptions PasswordOptions(int port) =>
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

    private static async Task RunFedAuthServerAsync(
        TcpListener listener,
        X509Certificate2 certificate,
        FedAuthServerState state,
        bool sendFedAuthInfo)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var network = client.GetStream();
        TdsPacketReader preLoginReader = new(network);
        var preLogin = await preLoginReader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.PreLogin, preLogin.Type);
        state.PreLoginRequestedFedAuth =
          TdsPreLogin.Parse(preLogin.Payload.Span).FederatedAuthenticationRequired;
        using (TdsPacketWriter writer = new(network, 4096))
        {
            await writer.WriteMessageAsync(
              TdsMessageType.TabularResult,
              TdsPreLogin.Encode(TdsEncryptionLevel.Required, requestFederatedAuthentication: true),
              default);
        }

        using TdsTlsHandshakeStream handshake = new(network, 4096);
        await using SslStream tls = new(handshake, leaveInnerStreamOpen: true);
        await tls.AuthenticateAsServerAsync(
          new SslServerAuthenticationOptions
          {
              ServerCertificate = certificate,
              EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
          });
        handshake.SwitchToRaw();

        TdsPacketReader reader = new(tls);
        var login = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.Login7, login.Type);
        ReadLogin(login.Payload.Span, state);

        if (sendFedAuthInfo)
        {
            using (TdsPacketWriter infoWriter = new(tls, 4096))
            {
                await infoWriter.WriteMessageAsync(
                  TdsMessageType.TabularResult,
                  BuildFedAuthInfoToken(),
                  default);
            }

            var token = await reader.ReadMessageAsync(default);
            Assert.AreEqual(TdsMessageType.FedAuthToken, token.Type);
            state.MessageToken = ReadFedAuthTokenMessage(token.Payload.Span);
        }

        using (TdsPacketWriter loginWriter = new(tls, 4096))
        {
            await loginWriter.WriteMessageAsync(
              TdsMessageType.TabularResult,
              BuildLoginAck(state.AcknowledgeFedAuth),
              default);
        }

        if (!state.AcknowledgeFedAuth)
        {
            return;
        }

        var query = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, query.Type);
        using TdsPacketWriter queryWriter = new(tls, 4096);
        await queryWriter.WriteMessageAsync(
          TdsMessageType.TabularResult,
          BuildIntResult(7),
          default);
    }

    private static async Task RunPlainLoginServerAsync(
        TcpListener listener,
        FedAuthServerState state,
        int? routeToPort)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        TdsPacketReader reader = new(stream);
        var preLogin = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.PreLogin, preLogin.Type);
        state.PreLoginRequestedFedAuth =
          TdsPreLogin.Parse(preLogin.Payload.Span).FederatedAuthenticationRequired;
        using (TdsPacketWriter writer = new(stream, 4096))
        {
            await writer.WriteMessageAsync(
              TdsMessageType.TabularResult,
              TdsPreLogin.Encode(TdsEncryptionLevel.NotSupported),
              default);
        }

        var login = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.Login7, login.Type);
        ReadLogin(login.Payload.Span, state);
        using (TdsPacketWriter loginWriter = new(stream, 4096))
        {
            await loginWriter.WriteMessageAsync(
              TdsMessageType.TabularResult,
              routeToPort is { } port
                ? BuildRoutingResponse(port)
                : BuildLoginAck(acknowledgeFedAuth: false),
              default);
        }

        if (routeToPort is not null)
        {
            return;
        }

        var query = await reader.ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, query.Type);
        using TdsPacketWriter queryWriter = new(stream, 4096);
        await queryWriter.WriteMessageAsync(
          TdsMessageType.TabularResult,
          BuildIntResult(7),
          default);
    }

    private static async Task RunUnexpectedFedAuthInfoServerAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        TdsPacketReader reader = new(stream);
        _ = await reader.ReadMessageAsync(default);
        using (TdsPacketWriter writer = new(stream, 4096))
        {
            await writer.WriteMessageAsync(
              TdsMessageType.TabularResult,
              TdsPreLogin.Encode(TdsEncryptionLevel.NotSupported),
              default);
        }

        _ = await reader.ReadMessageAsync(default);
        using TdsPacketWriter infoWriter = new(stream, 4096);
        await infoWriter.WriteMessageAsync(
          TdsMessageType.TabularResult,
          BuildFedAuthInfoToken(),
          default);
    }

    private static void ReadLogin(ReadOnlySpan<byte> login, FedAuthServerState state)
    {
        state.LoginUserNameLength = BinaryPrimitives.ReadUInt16LittleEndian(login[42..]);
        state.LoginPasswordLength = BinaryPrimitives.ReadUInt16LittleEndian(login[46..]);
        state.LoginUserName = ReadField(login, 40);
        state.LoginPassword = Deobfuscate(
          login.Slice(
            BinaryPrimitives.ReadUInt16LittleEndian(login[44..]),
            state.LoginPasswordLength * 2));

        int extensionPointer = BinaryPrimitives.ReadUInt16LittleEndian(login[56..]);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(login[extensionPointer..]);
        TdsPayloadReader features = new(login[offset..]);
        while (true)
        {
            var feature = features.ReadByte();
            if (feature == TdsFeatureId.Terminator)
            {
                break;
            }

            var length = (int)features.ReadUInt32LittleEndian();
            var data = features.ReadSpan(length);
            if (feature != TdsFeatureId.FedAuth)
            {
                continue;
            }

            TdsPayloadReader fedAuth = new(data);
            var options = fedAuth.ReadByte();
            Assert.AreEqual(TdsFedAuthLibrary.SecurityToken, (byte)(options >> 1));
            state.LoginEchoedFedAuthRequired = (options & 0x01) != 0;
            var tokenLength = fedAuth.ReadInt32LittleEndian();
            state.LoginToken = Encoding.Unicode.GetString(fedAuth.ReadSpan(tokenLength));
            Assert.AreEqual(0, fedAuth.Remaining);
        }
    }

    private static string ReadFedAuthTokenMessage(ReadOnlySpan<byte> payload)
    {
        TdsPayloadReader reader = new(payload);
        var dataLength = reader.ReadInt32LittleEndian();
        Assert.AreEqual(payload.Length - sizeof(int), dataLength);
        var tokenLength = reader.ReadInt32LittleEndian();
        var token = Encoding.Unicode.GetString(reader.ReadSpan(tokenLength));
        Assert.AreEqual(0, reader.Remaining);
        return token;
    }

    private static string ReadField(ReadOnlySpan<byte> login, int descriptor)
    {
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(login[descriptor..]);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(login[(descriptor + 2)..]);
        return Encoding.Unicode.GetString(login.Slice(offset, length * 2));
    }

    private static string Deobfuscate(ReadOnlySpan<byte> value)
    {
        var bytes = value.ToArray();
        for (var i = 0; i < bytes.Length; i++)
        {
            var current = (byte)(bytes[i] ^ 0xA5);
            bytes[i] = (byte)((current >> 4) | (current << 4));
        }

        return Encoding.Unicode.GetString(bytes);
    }

    private static byte[] BuildFedAuthInfoToken()
    {
        var body = TdsFedAuthTests.BuildFedAuthInfo(
          "https://login.example/tenant",
          "https://database.windows.net/");
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.FedAuthInfo);
        response.WriteUInt32LittleEndian((uint)body.Length);
        response.Write(body);
        return response.WrittenMemory.ToArray();
    }

    private static byte[] BuildLoginAck(bool acknowledgeFedAuth)
    {
        ArrayBufferWriter<byte> response = new();
        WriteLoginAck(response, acknowledgeFedAuth);
        WriteDone(response);
        return response.WrittenMemory.ToArray();
    }

    private static byte[] BuildRoutingResponse(int port)
    {
        const string host = "127.0.0.1";
        ArrayBufferWriter<byte> routing = new();
        routing.WriteByte(0);
        routing.WriteUInt16LittleEndian(checked((ushort)port));
        routing.WriteUInt16LittleEndian(checked((ushort)host.Length));
        routing.WriteUtf16(host);

        ArrayBufferWriter<byte> body = new();
        body.WriteByte(TdsEnvironmentChange.Routing);
        body.WriteUInt16LittleEndian(checked((ushort)routing.WrittenCount));
        body.Write(routing.WrittenSpan);
        body.WriteUInt16LittleEndian(0);

        ArrayBufferWriter<byte> response = new();
        WriteLoginAck(response, acknowledgeFedAuth: false);
        response.WriteByte(TdsTokenType.EnvironmentChange);
        response.WriteUInt16LittleEndian(checked((ushort)body.WrittenCount));
        response.Write(body.WrittenSpan);
        WriteDone(response);
        return response.WrittenMemory.ToArray();
    }

    private static void WriteLoginAck(ArrayBufferWriter<byte> response, bool acknowledgeFedAuth)
    {
        ArrayBufferWriter<byte> body = new();
        body.WriteByte(1);
        body.Write("\x04\x00\x00\x74"u8);
        body.WriteBVarChar("SQL Server");
        body.WriteByte(16);
        body.WriteByte(0);
        body.WriteUInt16BigEndian(1000);

        if (acknowledgeFedAuth)
        {
            response.WriteByte(TdsTokenType.FeatureExtAck);
            response.WriteByte(TdsFeatureId.FedAuth);
            response.WriteInt32LittleEndian(0);
            response.WriteByte(TdsFeatureId.Terminator);
        }

        response.WriteByte(TdsTokenType.LoginAck);
        response.WriteUInt16LittleEndian(checked((ushort)body.WrittenCount));
        response.Write(body.WrittenSpan);
    }

    private static byte[] BuildIntResult(int value)
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
        WriteDone(response);
        return response.WrittenMemory.ToArray();
    }

    private static void WriteDone(ArrayBufferWriter<byte> response)
    {
        response.WriteByte(TdsTokenType.Done);
        response.WriteUInt16LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteInt64LittleEndian(0);
    }

    private static async Task ObserveAsync(Task server)
    {
        try
        {
            await server;
        }
        catch (Exception exception) when (
          exception is IOException or SocketException or ObjectDisposedException or
            AuthenticationException)
        {
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
        using var certificate = request.CreateSelfSigned(
          DateTimeOffset.UtcNow.AddMinutes(-5),
          DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(
          certificate.Export(X509ContentType.Pfx),
          password: null);
    }

    private sealed class FedAuthServerState
    {
        internal bool AcknowledgeFedAuth { get; init; } = true;

        internal bool PreLoginRequestedFedAuth { get; set; }

        internal bool LoginEchoedFedAuthRequired { get; set; }

        internal string? LoginToken { get; set; }

        internal string? MessageToken { get; set; }

        internal string LoginUserName { get; set; } = string.Empty;

        internal string LoginPassword { get; set; } = string.Empty;

        internal int LoginUserNameLength { get; set; }

        internal int LoginPasswordLength { get; set; }
    }
}

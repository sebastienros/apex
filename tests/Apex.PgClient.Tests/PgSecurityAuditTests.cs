using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Apex.PgClient.Internal;
using Apex.SqlClient;

namespace Apex.PgClient.Tests;

[TestClass]
public sealed class PgSecurityAuditTests
{
    [TestMethod]
    public void WriteCStringRejectsEmbeddedNullByte()
    {
        ArrayBufferWriter<byte> writer = new();

        Assert.ThrowsExactly<ArgumentException>(() =>
            writer.WriteCString("mydb\0options\0-c statement_timeout=0"));
    }

    [TestMethod]
    public async Task WriteQueryAsyncRejectsEmbeddedNullByte()
    {
        using MemoryStream stream = new();
        PgWireWriter writer = new(stream, new PgTypeRegistry());

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await writer.WriteQueryAsync("SELECT 1\0; DROP TABLE users;", CancellationToken.None));
    }

    [TestMethod]
    public async Task WriteExtendedQueryAsyncRejectsEmbeddedNullByte()
    {
        using MemoryStream stream = new();
        PgWireWriter writer = new(stream, new PgTypeRegistry());

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await writer.WriteExtendedQueryAsync("SELECT 1\0", default(SqlParameters), CancellationToken.None));
    }

    [TestMethod]
    public void ScramRejectsExcessiveIterationCount()
    {
        PgScramClient client = new("user", "password");
        var clientNonce = ExtractClientNonce(client.ClientFirstMessage);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            client.HandleServerFirst($"r={clientNonce}servernonce,s=QSXCR+Q6sek8bf92,i={PgScramClient.MaximumIterations + 1}"));
    }

    [TestMethod]
    public void ScramRejectsSignedIterationCount()
    {
        PgScramClient client = new("user", "password");
        var clientNonce = ExtractClientNonce(client.ClientFirstMessage);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            client.HandleServerFirst($"r={clientNonce}servernonce,s=QSXCR+Q6sek8bf92,i=+4096"));
    }

    [TestMethod]
    public void ScramRejectsDuplicateAttributes()
    {
        PgScramClient client = new("user", "password");
        var clientNonce = ExtractClientNonce(client.ClientFirstMessage);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            client.HandleServerFirst($"r={clientNonce}s1,r={clientNonce}s2,s=QSXCR+Q6sek8bf92,i=4096"));
    }

    [TestMethod]
    public void ProxyOptionsToStringRedactsPassword()
    {
        PgProxyOptions proxy = new()
        {
            Type = PgProxyType.HttpConnect,
            Host = "proxy.internal",
            Port = 3128,
            Username = "proxyuser",
            Password = "super-secret-proxy-password",
        };
        PgConnectOptions options = new()
        {
            Host = "db.internal",
            Password = "super-secret-db-password",
            Proxy = proxy,
        };

        var proxyText = proxy.ToString();
        var optionsText = options.ToString();

        Assert.DoesNotContain("super-secret-proxy-password", proxyText);
        Assert.Contains("Password = ***", proxyText);
        Assert.DoesNotContain("super-secret-proxy-password", optionsText);
        Assert.DoesNotContain("super-secret-db-password", optionsText);
    }

    [TestMethod]
    public void HttpConnectRejectsControlAndWhitespaceCharactersInHost()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            PgProxyConnector.ValidateHttpConnectHost("db.internal\r\nX-Injected: true"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PgProxyConnector.ValidateHttpConnectHost("db.internal HTTP/1.1"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            PgProxyConnector.ValidateHttpConnectHost("db.internal\0evil"));

        // Valid hostnames and IPv6 literals are accepted.
        PgProxyConnector.ValidateHttpConnectHost("db.internal");
        PgProxyConnector.ValidateHttpConnectHost("::1");
    }

    [TestMethod]
    public async Task RejectsScramPlusOfferedOverNonSslConnection()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();

            // Read StartupMessage length and body.
            var lengthBuffer = new byte[4];
            await stream.ReadExactlyAsync(lengthBuffer);
            var startupLength = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
            var startupBody = new byte[startupLength - 4];
            await stream.ReadExactlyAsync(startupBody);

            // Send AuthenticationSASL (type 'R', auth type 10) offering SCRAM-SHA-256-PLUS + SCRAM-SHA-256.
            var mechanisms = Encoding.UTF8.GetBytes("SCRAM-SHA-256-PLUS\0SCRAM-SHA-256\0\0");
            var frame = new byte[1 + 4 + 4 + mechanisms.Length];
            frame[0] = (byte)'R';
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), 4 + 4 + mechanisms.Length);
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5, 4), 10);
            mechanisms.CopyTo(frame, 9);
            await stream.WriteAsync(frame);
            await stream.FlushAsync();
        });

        PgConnectOptions options = new()
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "user",
            Password = "password",
            Database = "db",
            SslMode = PgSslMode.Disable,
            ChannelBinding = PgChannelBinding.Prefer,
        };

        await Assert.ThrowsExactlyAsync<AuthenticationException>(async () =>
            await PgClient.ConnectAsync(options));

        await serverTask;
    }

    [TestMethod]
    public void ConnectionStringParserDoesNotLeakSecretFragmentWhenEqualsMissing()
    {
        var exception = Assert.ThrowsExactly<FormatException>(() =>
            PgConnectOptions.Parse("host=localhost password=first secret_fragment_12345"));

        Assert.DoesNotContain("secret_fragment_12345", exception.Message);
    }

    [TestMethod]
    public async Task PreAuthMessageLengthExceedingCapIsRejectedBeforeAllocation()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();

            var lengthBuffer = new byte[4];
            await stream.ReadExactlyAsync(lengthBuffer);
            var startupLength = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
            var startupBody = new byte[startupLength - 4];
            await stream.ReadExactlyAsync(startupBody);

            // Send a 5-byte header claiming a payload exceeding MaximumPreAuthMessageLength.
            var header = new byte[5];
            header[0] = (byte)'R';
            BinaryPrimitives.WriteInt32BigEndian(
                header.AsSpan(1, 4),
                PgWireReader.MaximumPreAuthMessageLength + 1);
            await stream.WriteAsync(header);
            await stream.FlushAsync();
        });

        PgConnectOptions options = new()
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "user",
            Password = "password",
            Database = "db",
            SslMode = PgSslMode.Disable,
        };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await PgClient.ConnectAsync(options));

        await serverTask;
    }

    private static string ExtractClientNonce(string clientFirstMessage)
    {
        var marker = ",r=";
        var index = clientFirstMessage.IndexOf(marker, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, index);
        return clientFirstMessage[(index + marker.Length)..];
    }
}

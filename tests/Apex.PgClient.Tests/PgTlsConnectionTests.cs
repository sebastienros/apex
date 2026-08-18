using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Apex.PgClient.Tests;

[TestClass]
public sealed class PgTlsConnectionTests
{
    [TestMethod]
    public async Task NegotiatesTraditionalPostgreSqlTls()
    {
        using var certificate = CreateCertificate();
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunTlsServerAsync(listener, certificate, direct: false);

        await using var connection = await PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "user",
            Password = "pass",
            Database = "db",
            SslMode = PgSslMode.Require,
        });

        Assert.IsTrue(connection.IsSecure);
        await connection.DisposeAsync();
        await server;
        listener.Stop();
    }

    [TestMethod]
    public async Task NegotiatesDirectTls()
    {
        using var certificate = CreateCertificate();
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunTlsServerAsync(listener, certificate, direct: true);

        await using var connection = await PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "user",
            Password = "pass",
            Database = "db",
            SslMode = PgSslMode.Require,
            SslNegotiation = PgSslNegotiation.Direct,
        });

        Assert.IsTrue(connection.IsSecure);
        await connection.DisposeAsync();
        await server;
        listener.Stop();
    }

#if NET11_0_OR_GREATER
    [TestMethod]
    public async Task NegotiatesDirectTlsWithExperimentalLowLevelTransport()
    {
        using var certificate = CreateCertificate();
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunTlsServerAsync(listener, certificate, direct: true);

        await using var connection = await PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "user",
            Password = "pass",
            Database = "db",
            SslMode = PgSslMode.Require,
            SslNegotiation = PgSslNegotiation.Direct,
            UseExperimentalLowLevelTls = true,
        });

        Assert.IsTrue(connection.IsSecure);
        await connection.DisposeAsync();
        await server;
        listener.Stop();
    }

    [TestMethod]
    public async Task NegotiatesTraditionalTlsWithExperimentalLowLevelTransport()
    {
        using var certificate = CreateCertificate();
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunTlsServerAsync(listener, certificate, direct: false);

        await using var connection = await PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "user",
            Password = "pass",
            Database = "db",
            SslMode = PgSslMode.Require,
            UseExperimentalLowLevelTls = true,
        });

        Assert.IsTrue(connection.IsSecure);
        await connection.DisposeAsync();
        await server;
        listener.Stop();
    }
#endif

    [TestMethod]
    public async Task PreferredTlsFallsBackWhenServerDeclines()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunDeclinedTlsServerAsync(listener);

        await using var connection = await PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "user",
            Password = "pass",
            Database = "db",
            SslMode = PgSslMode.Prefer,
        });

        Assert.IsFalse(connection.IsSecure);
        await connection.DisposeAsync();
        await server;
        listener.Stop();
    }

    [TestMethod]
    public async Task AllowModeRetriesWithTlsWhenServerRequiresEncryption()
    {
        using var certificate = CreateCertificate();
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunAllowFallbackServerAsync(listener, certificate);

        await using var connection = await PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "user",
            Password = "pass",
            Database = "db",
            SslMode = PgSslMode.Allow,
        });

        Assert.IsTrue(connection.IsSecure);
        await connection.DisposeAsync();
        await server;
        listener.Stop();
    }

    [TestMethod]
    public async Task VerifyCaAcceptsTrustedCertificateWithDifferentHostName()
    {
        using var authority = CreateCertificateAuthority();
        using var certificate = CreateServerCertificate(authority, "database.internal");
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunTlsServerAsync(listener, certificate, direct: false);

        await using var connection = await PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "user",
            Password = "pass",
            Database = "db",
            SslMode = PgSslMode.VerifyCa,
            CertificateValidationCallback = (_, remote, _, errors) =>
              ValidateCertificate(remote, authority, errors, verifyHostName: false),
        });

        Assert.IsTrue(connection.IsSecure);
        await connection.DisposeAsync();
        await server;
        listener.Stop();
    }

    [TestMethod]
    public async Task VerifyFullAcceptsMatchingSubjectAlternativeName()
    {
        using var authority = CreateCertificateAuthority();
        using var certificate = CreateServerCertificate(authority, "localhost");
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunTlsServerAsync(listener, certificate, direct: false);

        await using var connection = await PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = "localhost",
            Port = port,
            Username = "user",
            Password = "pass",
            Database = "db",
            SslMode = PgSslMode.VerifyFull,
            CertificateValidationCallback = (_, remote, _, errors) =>
              ValidateCertificate(remote, authority, errors, verifyHostName: true),
        });

        Assert.IsTrue(connection.IsSecure);
        await connection.DisposeAsync();
        await server;
        listener.Stop();
    }

    [TestMethod]
    public async Task VerifyFullRejectsMismatchedSubjectAlternativeName()
    {
        using var authority = CreateCertificateAuthority();
        using var certificate = CreateServerCertificate(authority, "database.internal");
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RunRejectedTlsServerAsync(listener, certificate);

        await Assert.ThrowsExactlyAsync<AuthenticationException>(
          () => PgClient.ConnectAsync(new PgConnectOptions
          {
              Host = "127.0.0.1",
              Port = port,
              Username = "user",
              Password = "pass",
              Database = "db",
              SslMode = PgSslMode.VerifyFull,
              CertificateValidationCallback = (_, remote, _, errors) =>
                ValidateCertificate(remote, authority, errors, verifyHostName: true),
          }).AsTask());

        await server;
        listener.Stop();
    }

    private static async Task RunTlsServerAsync(
        TcpListener listener,
        X509Certificate2 certificate,
        bool direct)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var network = client.GetStream();
        if (!direct)
        {
            var sslRequest = new byte[8];
            await network.ReadExactlyAsync(sslRequest);
            Assert.AreEqual(8, BinaryPrimitives.ReadInt32BigEndian(sslRequest));
            Assert.AreEqual(80877103, BinaryPrimitives.ReadInt32BigEndian(sslRequest.AsSpan(4)));
            await network.WriteAsync(new byte[] { (byte)'S' });
            await network.FlushAsync();
        }

        await using SslStream tls = new(network, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(
          new SslServerAuthenticationOptions
          {
              ServerCertificate = certificate,
              EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
              ApplicationProtocols = direct
              ? [new SslApplicationProtocol("postgresql")]
              : null,
          });
        await ReadStartupAsync(tls);
        await WriteStartupCompleteAsync(tls, direct ? "17.1" : "16.4");
        (var type, _) = await ReadMessageAsync(tls);
        Assert.AreEqual((byte)'X', type);
    }

    private static async Task RunDeclinedTlsServerAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var sslRequest = new byte[8];
        await stream.ReadExactlyAsync(sslRequest);
        await stream.WriteAsync(new byte[] { (byte)'N' });
        await stream.FlushAsync();
        await ReadStartupAsync(stream);
        await WriteStartupCompleteAsync(stream, "16.4");
        (var type, _) = await ReadMessageAsync(stream);
        Assert.AreEqual((byte)'X', type);
    }

    private static async Task RunRejectedTlsServerAsync(
        TcpListener listener,
        X509Certificate2 certificate)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var network = client.GetStream();
        var sslRequest = new byte[8];
        await network.ReadExactlyAsync(sslRequest);
        await network.WriteAsync(new byte[] { (byte)'S' });
        await network.FlushAsync();
        await using SslStream tls = new(network, leaveInnerStreamOpen: false);
        try
        {
            await tls.AuthenticateAsServerAsync(certificate);
        }
        catch (AuthenticationException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task RunAllowFallbackServerAsync(
        TcpListener listener,
        X509Certificate2 certificate)
    {
        using (var plain = await listener.AcceptTcpClientAsync())
        {
            await using var stream = plain.GetStream();
            await ReadStartupAsync(stream);
            byte[] error =
            [
              (byte)'S', .. CString("FATAL"),
        (byte)'C', .. CString("28000"),
        (byte)'M', .. CString("no pg_hba.conf entry for host, no encryption"),
        0,
      ];
            await WriteMessageAsync(stream, (byte)'E', error);
            (var type, _) = await ReadMessageAsync(stream);
            Assert.AreEqual((byte)'X', type);
        }

        using var secure = await listener.AcceptTcpClientAsync();
        await using var network = secure.GetStream();
        var sslRequest = new byte[8];
        await network.ReadExactlyAsync(sslRequest);
        await network.WriteAsync(new byte[] { (byte)'S' });
        await network.FlushAsync();
        await using SslStream tls = new(network, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(certificate);
        await ReadStartupAsync(tls);
        await WriteStartupCompleteAsync(tls, "16.4");
        (var terminate, _) = await ReadMessageAsync(tls);
        Assert.AreEqual((byte)'X', terminate);
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
        request.CertificateExtensions.Add(
          new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
              using var certificate = request.CreateSelfSigned(
          DateTimeOffset.UtcNow.AddMinutes(-5),
          DateTimeOffset.UtcNow.AddDays(1));
              return CloneCertificate(certificate);
    }

        private static X509Certificate2 CreateCertificateAuthority()
        {
                using RSA rsa = RSA.Create(2048);
                CertificateRequest request = new(
                    "CN=Apex PostgreSQL Test CA",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(true, false, 0, critical: true));
                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(
                        X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                        critical: true));
                request.CertificateExtensions.Add(
                    new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
                using var certificate = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddMinutes(-5),
                    DateTimeOffset.UtcNow.AddDays(1));
                return CloneCertificate(certificate);
        }

        private static X509Certificate2 CreateServerCertificate(
                X509Certificate2 authority,
                string dnsName)
        {
                using RSA rsa = RSA.Create(2048);
                CertificateRequest request = new(
                    "CN=" + dnsName,
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                SubjectAlternativeNameBuilder names = new();
                names.AddDnsName(dnsName);
                request.CertificateExtensions.Add(names.Build());
                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, critical: true));
                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                        critical: true));
                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        [new Oid("1.3.6.1.5.5.7.3.1")],
                        critical: true));
                var serial = RandomNumberGenerator.GetBytes(16);
                using var issued = request.Create(
                    authority,
                    new DateTimeOffset(authority.NotBefore).AddSeconds(1),
                    new DateTimeOffset(authority.NotAfter).AddSeconds(-1),
                    serial);
                using var certificate = issued.CopyWithPrivateKey(rsa);
                return CloneCertificate(certificate);
        }

        private static X509Certificate2 CloneCertificate(X509Certificate2 certificate) =>
                X509CertificateLoader.LoadPkcs12(
                    certificate.Export(X509ContentType.Pfx),
                    password: null);

        private static bool ValidateCertificate(
                X509Certificate? remote,
                X509Certificate2 authority,
                SslPolicyErrors errors,
                bool verifyHostName)
        {
                if (remote is null ||
                        verifyHostName &&
                        (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                {
                        return false;
                }

                using X509Chain chain = new();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(authority);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(remote));
        }

    private static async Task ReadStartupAsync(Stream stream)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length);
        var payload = new byte[BinaryPrimitives.ReadInt32BigEndian(length) - 4];
        await stream.ReadExactlyAsync(payload);
        Assert.AreEqual(196608, BinaryPrimitives.ReadInt32BigEndian(payload));
    }

    private static async Task WriteStartupCompleteAsync(Stream stream, string version)
    {
        await WriteMessageAsync(stream, (byte)'R', Int32(0));
        await WriteMessageAsync(
          stream,
          (byte)'S',
          [.. CString("server_version"), .. CString(version)]);
        await WriteMessageAsync(stream, (byte)'K', [.. Int32(123), .. Int32(456)]);
        await WriteMessageAsync(stream, (byte)'Z', [(byte)'I']);
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
        var payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4];
        await stream.ReadExactlyAsync(payload);
        return (header[0], payload);
    }

    private static byte[] CString(string value) =>
      [.. System.Text.Encoding.UTF8.GetBytes(value), 0];

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}

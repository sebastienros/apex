#if NET11_0_OR_GREATER
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Apex.SqlClient.Internal;

namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class LowLevelTlsStreamTests
{
    [TestMethod]
    public async Task DisposeDuringPendingReadKeepsBuffersUntilTheReadUnwinds()
    {
        using var certificate = CreateCertificate();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource serverStop = new(TimeSpan.FromSeconds(30));
        var server = RunServerAsync(listener, certificate, serverStop.Token);

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        GatedStream gated = new(client.GetStream());
        var tls = await LowLevelTlsStream.AuthenticateAsClientAsync(
            gated,
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
            CancellationToken.None);

        gated.HoldNextRead();
        var read = tls.ReadAsync(new byte[64]).AsTask();
        await gated.ReadStarted.WaitAsync(TimeSpan.FromSeconds(10));

        await tls.DisposeAsync();

        // The pending read still owns the cipher buffer, so it must not be returned yet.
        Assert.IsTrue(gated.IsDisposed);
        Assert.IsFalse(tls.ResourcesReleased);
        Assert.IsFalse(read.IsCompleted);

        IOException aborted = new("The transport was closed.");
        gated.CompleteHeldRead(aborted);

        // The read reports the transport failure, not a disposed semaphore.
        var thrown = await Assert.ThrowsExactlyAsync<IOException>(() => read);
        Assert.AreSame(aborted, thrown);
        Assert.IsTrue(tls.ResourcesReleased);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => tls.ReadAsync(new byte[64]).AsTask());

        client.Close();
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task LastOperationToUnwindAfterDisposeReleasesResources()
    {
        using var certificate = CreateCertificate();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource serverStop = new(TimeSpan.FromSeconds(30));
        var server = RunServerAsync(listener, certificate, serverStop.Token);

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        GatedStream gated = new(client.GetStream());
        var tls = await LowLevelTlsStream.AuthenticateAsClientAsync(
            gated,
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
            CancellationToken.None);

        gated.HoldNextRead();
        var read = tls.ReadAsync(new byte[64]).AsTask();
        await gated.ReadStarted.WaitAsync(TimeSpan.FromSeconds(10));
        gated.HoldNextWrite();
        var write = tls.WriteAsync("ping"u8.ToArray()).AsTask();
        await gated.WriteStarted.WaitAsync(TimeSpan.FromSeconds(10));

        // Shutdown needs the write lock, so disposal waits for the held write.
        var dispose = tls.DisposeAsync().AsTask();
        Assert.IsFalse(dispose.IsCompleted);
        gated.CompleteHeldWrite();
        await write;
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(tls.ResourcesReleased);

        gated.CompleteHeldRead(new IOException("The transport was closed."));
        await Assert.ThrowsExactlyAsync<IOException>(() => read);
        Assert.IsTrue(tls.ResourcesReleased);

        client.Close();
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // A writer blocked because the peer stopped reading holds the write lock that close_notify
    // needs. Disposal used to wait for it forever; it now aborts the transport at its deadline.
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DisposeAbortsAWriteThatNeverCompletes(bool asynchronous)
    {
        using var certificate = CreateCertificate();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource serverStop = new(TimeSpan.FromSeconds(30));
        var server = RunServerAsync(listener, certificate, serverStop.Token);

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        GatedStream gated = new(client.GetStream()) { AbortHeldWriteOnDispose = true };
        var tls = await LowLevelTlsStream.AuthenticateAsClientAsync(
            gated,
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
            CancellationToken.None);
        tls.ShutdownTimeout = TimeSpan.FromMilliseconds(200);

        gated.HoldNextWrite();
        var write = tls.WriteAsync("ping"u8.ToArray()).AsTask();
        await gated.WriteStarted.WaitAsync(TimeSpan.FromSeconds(10));

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var dispose = asynchronous
          ? tls.DisposeAsync().AsTask()
          : Task.Run(tls.Dispose);
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150), elapsed.Elapsed);
        Assert.IsTrue(gated.IsDisposed);
        await Assert.ThrowsExactlyAsync<IOException>(() => write);
        Assert.IsTrue(tls.ResourcesReleased);

        client.Close();
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task DisposeAfterTheTransportWasAbortedDoesNotThrow()
    {
        using var certificate = CreateCertificate();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource serverStop = new(TimeSpan.FromSeconds(30));
        var server = RunServerAsync(listener, certificate, serverStop.Token);

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var tls = await LowLevelTlsStream.AuthenticateAsClientAsync(
            client.GetStream(),
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
            CancellationToken.None);

        // What the command scheduler does to a connection stuck in I/O, before the driver
        // disposes its stream: close_notify can no longer be sent.
        client.Client.Dispose();
        await tls.DisposeAsync();

        Assert.IsTrue(tls.ResourcesReleased);
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task DisposeWithoutPendingOperationsReleasesResources()
    {
        using var certificate = CreateCertificate();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource serverStop = new(TimeSpan.FromSeconds(30));
        var server = RunServerAsync(listener, certificate, serverStop.Token);

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var tls = await LowLevelTlsStream.AuthenticateAsClientAsync(
            client.GetStream(),
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
            CancellationToken.None);

        await tls.WriteAsync("ping"u8.ToArray());
        tls.Dispose();

        Assert.IsTrue(tls.ResourcesReleased);
        Assert.ThrowsExactly<ObjectDisposedException>(() => tls.Write("x"u8));

        client.Close();
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // Accepts one TLS client and reads until it goes away. The client side of each test
    // closes the connection, possibly before this side has finished its handshake, so every
    // transport failure (and the safety timeout) ends the server quietly.
    private static async Task RunServerAsync(
        TcpListener listener,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        try
        {
            using var accepted = await listener.AcceptTcpClientAsync(cancellationToken);
            await using SslStream tls = new(accepted.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                },
                cancellationToken);
            var buffer = new byte[256];
            while (await tls.ReadAsync(buffer, cancellationToken) > 0)
            {
            }
        }
        catch (Exception exception) when (
            exception is IOException or OperationCanceledException or AuthenticationException)
        {
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx),
            password: null);
    }

    /// <summary>
    /// Passes I/O through to a network stream, except that one read can be held pending
    /// past disposal, like a transport whose pending reads complete asynchronously.
    /// </summary>
    private sealed class GatedStream(Stream inner) : Stream
    {
        private readonly TaskCompletionSource _readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<int>? _heldRead;
        private TaskCompletionSource? _heldWrite;
        private bool _holdNextRead;
        private bool _holdNextWrite;

        public Task ReadStarted => _readStarted.Task;

        public Task WriteStarted => _writeStarted.Task;

        public bool IsDisposed { get; private set; }

        // Like a socket: disposing fails a write that is still pending.
        public bool AbortHeldWriteOnDispose { get; init; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void HoldNextRead() => _holdNextRead = true;

        public void CompleteHeldRead(Exception exception) =>
            _heldRead!.SetException(exception);

        public void HoldNextWrite() => _holdNextWrite = true;

        public void CompleteHeldWrite() => _heldWrite!.SetResult();

        private void AbortHeldWrite()
        {
            if (AbortHeldWriteOnDispose)
            {
                _heldWrite?.TrySetException(new IOException("The transport was closed."));
            }
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_holdNextRead)
            {
                return inner.ReadAsync(buffer, cancellationToken);
            }

            _holdNextRead = false;
            _heldRead = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _readStarted.SetResult();
            return new ValueTask<int>(_heldRead.Task);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_holdNextWrite)
            {
                _holdNextWrite = false;
                _heldWrite = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _writeStarted.SetResult();
                await _heldWrite.Task;
            }

            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
                AbortHeldWrite();
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            IsDisposed = true;
            AbortHeldWrite();
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
#endif

#if NET11_0_OR_GREATER
#pragma warning disable SYSLIB5007

using System.Buffers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Apex.SqlClient.Internal;

internal sealed class LowLevelTlsStream : Stream
{
    private const int CipherBufferSize = 32 * 1024;
    private const int MaximumCipherBufferSize = 1024 * 1024;
    private const int MaximumTlsPlaintextSize = 16 * 1024;
    private readonly Stream _inner;
    private readonly SslClientAuthenticationOptions _authenticationOptions;
    private readonly TlsContext _context;
    private readonly TlsBufferSession _session;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly SemaphoreSlim _applicationWriteLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private byte[]? _cipherInput;
    private byte[]? _cipherOutput;
    private byte[]? _plainInput;
    private int _cipherStart;
    private int _cipherEnd;
    private int _plainStart;
    private int _plainEnd;
    private int _disposed;

    private LowLevelTlsStream(
        Stream inner,
        SslClientAuthenticationOptions authenticationOptions)
    {
        _inner = inner;
        _authenticationOptions = authenticationOptions;
        if (authenticationOptions.RemoteCertificateValidationCallback is { } validation)
        {
            authenticationOptions.RemoteCertificateValidationCallback =
              (_, certificate, chain, errors) => validation(this, certificate, chain, errors);
        }
        _context = TlsContext.CreateClient(authenticationOptions);
        _session = new TlsBufferSession();
        _session.SetContext(_context);
        _cipherInput = ArrayPool<byte>.Shared.Rent(CipherBufferSize);
        _cipherOutput = ArrayPool<byte>.Shared.Rent(CipherBufferSize);
        _plainInput = ArrayPool<byte>.Shared.Rent(CipherBufferSize);
    }

    internal SslApplicationProtocol NegotiatedApplicationProtocol =>
      _session.NegotiatedApplicationProtocol;

    internal X509Certificate2? RemoteCertificate => _session.GetRemoteCertificate();

    public override bool CanRead => Volatile.Read(ref _disposed) == 0 && _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => Volatile.Read(ref _disposed) == 0 && _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    internal static async ValueTask<LowLevelTlsStream> AuthenticateAsClientAsync(
        Stream inner,
        SslClientAuthenticationOptions authenticationOptions,
        CancellationToken cancellationToken)
    {
        LowLevelTlsStream stream = new(inner, authenticationOptions);
        try
        {
            await stream.AuthenticateAsync(cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override void Flush()
    {
        ThrowIfDisposed();
        _inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _inner.FlushAsync(cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        _readLock.Wait();
        try
        {
            if (TryCopyPlaintext(buffer, out var copied))
            {
                return copied;
            }

            while (true)
            {
                TlsOperationStatus status;
                int written;
                _stateLock.Wait();
                try
                {
                    var usePlainBuffer = buffer.Length < MaximumTlsPlaintextSize;
                    status = ReadSession(
                        usePlainBuffer ? GetPlainInput() : buffer,
                        out written);
                    if (usePlainBuffer && written > 0)
                    {
                        _plainStart = 0;
                        _plainEnd = written;
                    }
                    DrainPendingOutput();
                }
                finally
                {
                    _stateLock.Release();
                }

                if (written > 0)
                {
                    return TryCopyPlaintext(buffer, out copied) ? copied : written;
                }

                switch (status)
                {
                    case TlsOperationStatus.NeedMoreData:
                        ReadCiphertext();
                        break;
                    case TlsOperationStatus.DestinationTooSmall:
                        if (ReadIntoPlaintextBuffer() > 0 &&
                            TryCopyPlaintext(buffer, out copied))
                        {
                            return copied;
                        }
                        break;
                    case TlsOperationStatus.Closed:
                        return 0;
                    case TlsOperationStatus.NeedsCertificateValidation:
                        ResolveCertificateValidation();
                        break;
                    case TlsOperationStatus.CertificateRequested:
                        ResolveClientCertificate();
                        break;
                    case TlsOperationStatus.Complete:
                        // TLS 1.3 post-handshake records can complete without plaintext.
                        break;
                    default:
                        throw UnexpectedStatus(status, "read");
                }
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        await _readLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryCopyPlaintext(buffer.Span, out var copied))
            {
                return copied;
            }

            while (true)
            {
                TlsOperationStatus status;
                int written;
                await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var usePlainBuffer = buffer.Length < MaximumTlsPlaintextSize;
                    status = ReadSession(
                        usePlainBuffer ? GetPlainInput() : buffer.Span,
                        out written);
                    if (usePlainBuffer && written > 0)
                    {
                        _plainStart = 0;
                        _plainEnd = written;
                    }
                    await DrainPendingOutputAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _stateLock.Release();
                }

                if (written > 0)
                {
                    return TryCopyPlaintext(buffer.Span, out copied) ? copied : written;
                }

                switch (status)
                {
                    case TlsOperationStatus.NeedMoreData:
                        await ReadCiphertextAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    case TlsOperationStatus.DestinationTooSmall:
                        if (await ReadIntoPlaintextBufferAsync(cancellationToken).ConfigureAwait(false) > 0 &&
                            TryCopyPlaintext(buffer.Span, out copied))
                        {
                            return copied;
                        }
                        break;
                    case TlsOperationStatus.Closed:
                        return 0;
                    case TlsOperationStatus.NeedsCertificateValidation:
                        ResolveCertificateValidation();
                        break;
                    case TlsOperationStatus.CertificateRequested:
                        ResolveClientCertificate();
                        break;
                    case TlsOperationStatus.Complete:
                        // TLS 1.3 post-handshake records can complete without plaintext.
                        break;
                    default:
                        throw UnexpectedStatus(status, "read");
                }
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty)
        {
            return;
        }

        _applicationWriteLock.Wait();
        try
        {
            while (!buffer.IsEmpty)
            {
                _stateLock.Wait();
                try
                {
                    var status = _session.Write(
                        buffer,
                        GetCipherOutput(),
                        out var consumed,
                        out var written);
                    WriteCiphertext(written);
                    DrainPendingOutput();
                    buffer = buffer[consumed..];
                    HandleWriteStatus(status, consumed, written);
                }
                finally
                {
                    _stateLock.Release();
                }
            }
        }
        finally
        {
            _applicationWriteLock.Release();
        }
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (buffer.IsEmpty)
        {
            return;
        }

        await _applicationWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var position = 0;
            while (position < buffer.Length)
            {
                await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var status = _session.Write(
                        buffer.Span[position..],
                        GetCipherOutput(),
                        out var consumed,
                        out var written);
                    await WriteCiphertextAsync(written, cancellationToken)
                      .ConfigureAwait(false);
                    await DrainPendingOutputAsync(cancellationToken)
                      .ConfigureAwait(false);
                    position += consumed;
                    HandleWriteStatus(status, consumed, written);
                }
                finally
                {
                    _stateLock.Release();
                }
            }
        }
        finally
        {
            _applicationWriteLock.Release();
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            base.Dispose(disposing);
            return;
        }

        try
        {
            Shutdown();
        }
        finally
        {
            ReleaseResources();
            _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
        finally
        {
            ReleaseResources();
            await _inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    private async ValueTask AuthenticateAsync(CancellationToken cancellationToken)
    {
        while (!_session.IsHandshakeComplete)
        {
            var status = _session.Handshake(
                GetCipherInput(),
                GetCipherOutput(),
                out var consumed,
                out var written);
            ConsumeCiphertext(consumed);
            await WriteCiphertextAsync(written, cancellationToken).ConfigureAwait(false);
            await DrainPendingOutputAsync(cancellationToken).ConfigureAwait(false);

            switch (status)
            {
                case TlsOperationStatus.Complete:
                    break;
                case TlsOperationStatus.NeedMoreData:
                    await ReadCiphertextAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case TlsOperationStatus.DestinationTooSmall:
                    break;
                case TlsOperationStatus.NeedsCertificateValidation:
                    ResolveCertificateValidation();
                    break;
                case TlsOperationStatus.CertificateRequested:
                    ResolveClientCertificate();
                    break;
                case TlsOperationStatus.Closed:
                    throw new AuthenticationException("The TLS peer closed the connection during authentication.");
                default:
                    throw UnexpectedStatus(status, "handshake");
            }
        }
    }

    private TlsOperationStatus ReadSession(Span<byte> destination, out int written)
    {
        var status = _session.Read(
            GetCipherInput(),
            destination,
            out var consumed,
            out written);
        ConsumeCiphertext(consumed);
        return status;
    }

    private int ReadIntoPlaintextBuffer()
    {
        _stateLock.Wait();
        try
        {
            var status = ReadSession(GetPlainInput(), out var written);
            _plainStart = 0;
            _plainEnd = written;
            DrainPendingOutput();
            HandleBufferedReadStatus(status, written);
            return written;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async ValueTask<int> ReadIntoPlaintextBufferAsync(CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = ReadSession(GetPlainInput(), out var written);
            _plainStart = 0;
            _plainEnd = written;
            await DrainPendingOutputAsync(cancellationToken).ConfigureAwait(false);
            HandleBufferedReadStatus(status, written);
            return written;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private static void HandleBufferedReadStatus(TlsOperationStatus status, int written)
    {
        if (written > 0 ||
            status is TlsOperationStatus.NeedMoreData or TlsOperationStatus.Complete)
        {
            return;
        }

        if (status == TlsOperationStatus.Closed)
        {
            return;
        }

        throw UnexpectedStatus(status, "buffered read");
    }

    private void HandleWriteStatus(TlsOperationStatus status, int consumed, int written)
    {
        switch (status)
        {
            case TlsOperationStatus.Complete:
            case TlsOperationStatus.DestinationTooSmall:
                if (consumed == 0 && written == 0 && !_session.HasPendingOutput)
                {
                    throw new IOException("TLS write made no forward progress.");
                }
                break;
            case TlsOperationStatus.NeedsCertificateValidation:
                ResolveCertificateValidation();
                break;
            case TlsOperationStatus.CertificateRequested:
                ResolveClientCertificate();
                break;
            case TlsOperationStatus.Closed:
                throw new IOException("The TLS peer closed the connection.");
            default:
                throw UnexpectedStatus(status, "write");
        }
    }

    private void ResolveCertificateValidation()
    {
        _ = _session.AcceptWithDefaultValidation();
    }

    private void ResolveClientCertificate()
    {
        var certificates = _authenticationOptions.ClientCertificates ??
          new X509CertificateCollection();
        X509Certificate? selected;
        if (_authenticationOptions.LocalCertificateSelectionCallback is { } callback)
        {
            selected = callback(
                this,
                _authenticationOptions.TargetHost ?? string.Empty,
                certificates,
                _session.GetRemoteCertificate(),
                _session.GetAcceptableIssuers()?.ToArray() ?? []);
        }
        else
        {
            var acceptableIssuers = _session.GetAcceptableIssuers();
            selected = certificates
              .OfType<X509Certificate2>()
              .FirstOrDefault(certificate =>
                certificate.HasPrivateKey &&
                IsAcceptedClientCertificate(certificate, acceptableIssuers));
        }

        if (selected is null)
        {
            _session.SetClientCertificateContext(null);
            return;
        }

        var certificate = selected as X509Certificate2 ??
          X509CertificateLoader.LoadCertificate(selected.GetRawCertData());
        if (!certificate.HasPrivateKey)
        {
            throw new AuthenticationException(
              "The selected TLS client certificate does not have a private key.");
        }

        _session.SetClientCertificateContext(
          SslStreamCertificateContext.Create(certificate, additionalCertificates: null));
    }

    private static bool IsAcceptedClientCertificate(
        X509Certificate2 certificate,
        IReadOnlyList<string>? acceptableIssuers)
    {
        if (acceptableIssuers is not { Count: > 0 })
        {
            return true;
        }

        if (acceptableIssuers.Contains(certificate.Issuer, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        using X509Chain chain = new();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        _ = chain.Build(certificate);
        return chain.ChainElements
          .OfType<X509ChainElement>()
          .Any(element => acceptableIssuers.Contains(
            element.Certificate.Subject,
            StringComparer.OrdinalIgnoreCase));
    }

    private void ReadCiphertext()
    {
        PrepareCipherInputForRead();
        var read = _inner.Read(_cipherInput!.AsSpan(_cipherEnd));
        if (read == 0)
        {
            throw new EndOfStreamException("The TLS peer closed the connection.");
        }
        _cipherEnd += read;
    }

    private async ValueTask ReadCiphertextAsync(CancellationToken cancellationToken)
    {
        PrepareCipherInputForRead();
        var read = await _inner.ReadAsync(
            _cipherInput!.AsMemory(_cipherEnd),
            cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw new EndOfStreamException("The TLS peer closed the connection.");
        }
        _cipherEnd += read;
    }

    private void PrepareCipherInputForRead()
    {
        var buffer = _cipherInput ??
          throw new ObjectDisposedException(nameof(LowLevelTlsStream));
        if (_cipherStart > 0)
        {
            buffer.AsSpan(_cipherStart, _cipherEnd - _cipherStart).CopyTo(buffer);
            _cipherEnd -= _cipherStart;
            _cipherStart = 0;
        }

        if (_cipherEnd < buffer.Length)
        {
            return;
        }

        if (buffer.Length >= MaximumCipherBufferSize)
        {
            throw new InvalidDataException(
              $"TLS input exceeded {MaximumCipherBufferSize} bytes without making progress.");
        }

        var grown = ArrayPool<byte>.Shared.Rent(
          Math.Min(MaximumCipherBufferSize, buffer.Length * 2));
        buffer.AsSpan(0, _cipherEnd).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(buffer);
        _cipherInput = grown;
    }

    private ReadOnlySpan<byte> GetCipherInput() =>
      (_cipherInput ?? throw new ObjectDisposedException(nameof(LowLevelTlsStream)))
        .AsSpan(_cipherStart, _cipherEnd - _cipherStart);

    private Span<byte> GetCipherOutput() =>
      _cipherOutput ?? throw new ObjectDisposedException(nameof(LowLevelTlsStream));

    private Span<byte> GetPlainInput() =>
      _plainInput ?? throw new ObjectDisposedException(nameof(LowLevelTlsStream));

    private void ConsumeCiphertext(int consumed)
    {
        if ((uint)consumed > (uint)(_cipherEnd - _cipherStart))
        {
            throw new InvalidDataException("The TLS state machine consumed invalid input length.");
        }

        _cipherStart += consumed;
        if (_cipherStart == _cipherEnd)
        {
            _cipherStart = 0;
            _cipherEnd = 0;
        }
    }

    private bool TryCopyPlaintext(Span<byte> destination, out int copied)
    {
        var available = _plainEnd - _plainStart;
        if (available == 0)
        {
            copied = 0;
            return false;
        }

        copied = Math.Min(available, destination.Length);
        _plainInput!.AsSpan(_plainStart, copied).CopyTo(destination);
        _plainStart += copied;
        if (_plainStart == _plainEnd)
        {
            _plainStart = 0;
            _plainEnd = 0;
        }
        return true;
    }

    private void WriteCiphertextAlreadyLocked(int written)
    {
        if (written > 0)
        {
            _inner.Write(_cipherOutput!.AsSpan(0, written));
        }
    }

    private void WriteCiphertext(int written)
    {
        if (written == 0)
        {
            return;
        }

        _writeLock.Wait();
        try
        {
            WriteCiphertextAlreadyLocked(written);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async ValueTask WriteCiphertextAsync(
        int written,
        CancellationToken cancellationToken)
    {
        if (written == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteCiphertextAlreadyLockedAsync(written, cancellationToken)
              .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private ValueTask WriteCiphertextAlreadyLockedAsync(
        int written,
        CancellationToken cancellationToken) =>
      written == 0
        ? ValueTask.CompletedTask
        : _inner.WriteAsync(_cipherOutput!.AsMemory(0, written), cancellationToken);

    private void DrainPendingOutput()
    {
        if (!_session.HasPendingOutput)
        {
            return;
        }

        _writeLock.Wait();
        try
        {
            DrainPendingOutputAlreadyWriteLocked();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async ValueTask DrainPendingOutputAsync(CancellationToken cancellationToken)
    {
        if (!_session.HasPendingOutput)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DrainPendingOutputAlreadyWriteLockedAsync(cancellationToken)
              .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void DrainPendingOutputAlreadyWriteLocked()
    {
        while (_session.HasPendingOutput)
        {
            _ = _session.DrainPendingOutput(GetCipherOutput(), out var written);
            if (written == 0)
            {
                throw new IOException("TLS pending output could not be drained.");
            }
            _inner.Write(_cipherOutput!.AsSpan(0, written));
        }
    }

    private async ValueTask DrainPendingOutputAlreadyWriteLockedAsync(
        CancellationToken cancellationToken)
    {
        while (_session.HasPendingOutput)
        {
            _ = _session.DrainPendingOutput(GetCipherOutput(), out var written);
            if (written == 0)
            {
                throw new IOException("TLS pending output could not be drained.");
            }
            await _inner.WriteAsync(
                _cipherOutput!.AsMemory(0, written),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private void Shutdown()
    {
        _applicationWriteLock.Wait();
        try
        {
            _stateLock.Wait();
            try
            {
                if (_session.IsHandshakeComplete)
                {
                    _ = _session.Shutdown(GetCipherOutput(), out var written);
                    WriteCiphertext(written);
                    DrainPendingOutput();
                    _inner.Flush();
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }
        finally
        {
            _applicationWriteLock.Release();
        }
    }

    private async ValueTask ShutdownAsync()
    {
        await _applicationWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_session.IsHandshakeComplete)
                {
                    _ = _session.Shutdown(GetCipherOutput(), out var written);
                    await WriteCiphertextAsync(written, CancellationToken.None).ConfigureAwait(false);
                    await DrainPendingOutputAsync(CancellationToken.None)
                      .ConfigureAwait(false);
                    await _inner.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }
        finally
        {
            _applicationWriteLock.Release();
        }
    }

    private void ReleaseResources()
    {
        _session.Dispose();
        _context.Dispose();
        ReturnBuffer(ref _cipherInput);
        ReturnBuffer(ref _cipherOutput);
        ReturnBuffer(ref _plainInput);
        _readLock.Dispose();
        _applicationWriteLock.Dispose();
        _writeLock.Dispose();
        _stateLock.Dispose();
    }

    private static void ReturnBuffer(ref byte[]? buffer)
    {
        var returned = Interlocked.Exchange(ref buffer, null);
        if (returned is not null)
        {
            ArrayPool<byte>.Shared.Return(returned);
        }
    }

    private static IOException UnexpectedStatus(TlsOperationStatus status, string operation) =>
      new($"Unexpected TLS status '{status}' during {operation}.");

    private void ThrowIfDisposed() =>
      ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

#endif

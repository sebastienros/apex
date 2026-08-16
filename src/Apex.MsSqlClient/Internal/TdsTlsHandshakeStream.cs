using System.Buffers.Binary;

namespace Apex.MsSqlClient.Internal;

internal sealed class TdsTlsHandshakeStream : Stream
{
    private readonly Stream _inner;
    private readonly int _packetSize;
    private readonly byte[] _header = new byte[8];
    private readonly byte[] _writePacket;
    private byte[] _readPayload = [];
    private int _readLength;
    private int _readPosition;
    private bool _raw;

    internal TdsTlsHandshakeStream(Stream inner, int packetSize)
    {
        _inner = inner;
        _packetSize = packetSize;
        _writePacket = new byte[packetSize];
    }

    internal void SwitchToRaw() => _raw = true;

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
      _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (TryCopyBuffered(buffer, out var copied))
        {
            return copied;
        }

        if (_raw)
        {
            return _inner.Read(buffer);
        }

        ReadFramedPayload();
        _ = TryCopyBuffered(buffer, out copied);
        return copied;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (TryCopyBuffered(buffer.Span, out var copied))
        {
            return copied;
        }

        if (_raw)
        {
            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        await ReadFramedPayloadAsync(cancellationToken).ConfigureAwait(false);
        _ = TryCopyBuffered(buffer.Span, out copied);
        return copied;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_raw)
        {
            _inner.Write(buffer);
            return;
        }

        WriteFramed(buffer);
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_raw)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            return;
        }

        await TdsPacketWriter.WriteMessageCoreAsync(
          _inner,
          TdsMessageType.PreLogin,
          buffer,
          _packetSize,
          cancellationToken,
          _writePacket).ConfigureAwait(false);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _readPayload = [];
        _readLength = 0;
        base.Dispose(disposing);
    }

    private bool TryCopyBuffered(Span<byte> destination, out int copied)
    {
        var remaining = _readLength - _readPosition;
        if (remaining == 0)
        {
            copied = 0;
            return false;
        }

        copied = Math.Min(destination.Length, remaining);
        _readPayload.AsSpan(_readPosition, copied).CopyTo(destination);
        _readPosition += copied;
        if (_readPosition == _readLength)
        {
            _readPosition = 0;
            _readLength = 0;
        }

        return true;
    }

    private void ReadFramedPayload()
    {
        _inner.ReadExactly(_header);
        SetPayload(_header);
        _inner.ReadExactly(_readPayload.AsSpan(0, _readLength));
    }

    private async ValueTask ReadFramedPayloadAsync(CancellationToken cancellationToken)
    {
        await _inner.ReadExactlyAsync(_header, cancellationToken).ConfigureAwait(false);
        SetPayload(_header);
        await _inner.ReadExactlyAsync(
          _readPayload.AsMemory(0, _readLength),
          cancellationToken).ConfigureAwait(false);
    }

    private void SetPayload(ReadOnlySpan<byte> header)
    {
        if (header[0] != TdsMessageType.PreLogin)
        {
            throw new InvalidDataException(
              $"Expected encapsulated TLS PRELOGIN packet, received type 0x{header[0]:X2}.");
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        if (length < 8 || length > 32767)
        {
            throw new InvalidDataException($"Invalid encapsulated TLS TDS packet length {length}.");
        }

        _readLength = length - 8;
        if (_readPayload.Length < _readLength)
        {
            _readPayload = new byte[_readLength];
        }

        _readPosition = 0;
    }

    private void WriteFramed(ReadOnlySpan<byte> payload)
    {
        var maximumPayload = _packetSize - 8;
        var position = 0;
        byte packetId = 1;
        do
        {
            var count = Math.Min(maximumPayload, payload.Length - position);
            _writePacket[0] = TdsMessageType.PreLogin;
            _writePacket[1] = position + count == payload.Length ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt16BigEndian(
              _writePacket.AsSpan(2),
              checked((ushort)(count + 8)));
            _writePacket[6] = packetId++;
            if (count > 0)
            {
                payload.Slice(position, count).CopyTo(_writePacket.AsSpan(8));
            }

            _inner.Write(_writePacket.AsSpan(0, count + 8));
            position += count;
        }
        while (position < payload.Length);

        _inner.Flush();
    }
}

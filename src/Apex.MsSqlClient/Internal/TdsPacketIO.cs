using System.Buffers;
using System.Buffers.Binary;

namespace Apex.MsSqlClient.Internal;

internal readonly record struct TdsMessage(byte Type, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Reads TDS packets and exposes the payload of the current message.
/// </summary>
/// <remarks>
/// The default reader issues exact-length reads and never consumes bytes past the packet it is
/// reading, which keeps the pre-TLS PRELOGIN exchange from swallowing handshake records. A
/// connection created after the TLS switch uses <see cref="CreateBuffered"/>, which receives into
/// a pooled buffer and lets token parsers read primitives and whole rows synchronously from
/// <see cref="GetBufferedPayload"/>, so they fall back to async only at buffer or packet
/// boundaries.
/// </remarks>
internal sealed class TdsPacketReader
{
    internal const int DefaultReceiveBufferSize = 32 * 1024;
    private const int HeaderLength = 8;
    private const int MaximumRetainedMessageBufferLength = 1024 * 1024;
    private const int Idle = 0;
    private const int Filling = 1;
    private const int Released = 2;

    private readonly Stream _stream;
    private readonly bool _buffered;
    private readonly byte[] _skipBuffer;
    private byte[] _buffer;
    private byte[] _messageBuffer = [];
    private int _start;
    private int _end;
    private int _state;
    private int _packetPayloadRemaining;
    private byte _messageType;
    private bool _packetEndsMessage;
    private bool _messageActive;

    internal TdsPacketReader(Stream stream)
    {
        _stream = stream;
        _buffer = new byte[HeaderLength];
        _skipBuffer = new byte[512];
    }

    private TdsPacketReader(Stream stream, int receiveBufferSize)
    {
        _stream = stream;
        _buffered = true;
        _buffer = ArrayPool<byte>.Shared.Rent(receiveBufferSize);
        _skipBuffer = [];
    }

    internal bool EndOfMessage =>
      _messageActive &&
      _packetPayloadRemaining == 0 &&
      _packetEndsMessage;

    /// <summary>
    /// Creates a reader that receives into a pooled buffer and may read past the current message.
    /// Use it only once nothing else reads the stream (after PRELOGIN and the TLS switch), and call
    /// <see cref="Release"/> when the stream is closed.
    /// </summary>
    internal static TdsPacketReader CreateBuffered(
        Stream stream,
        int receiveBufferSize = DefaultReceiveBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(receiveBufferSize, 512);
        return new TdsPacketReader(stream, receiveBufferSize);
    }

    internal ValueTask<byte> BeginMessageAsync(CancellationToken cancellationToken)
    {
        if (_messageActive && !EndOfMessage)
        {
            throw new InvalidOperationException(
              "The current TDS message must be consumed before reading the next message.");
        }

        _messageActive = false;

        // The previous message's payload is no longer valid, so a large reassembly buffer can go
        // even when this connection only streams from now on.
        if (_messageBuffer.Length > MaximumRetainedMessageBufferLength)
        {
            _messageBuffer = [];
        }

        if (TryReadPacketHeader(expectedType: null))
        {
            _messageActive = true;
            return new ValueTask<byte>(_messageType);
        }

        return BeginMessageSlowAsync(cancellationToken);
    }

    /// <summary>
    /// Reads a whole message. A buffered reader returns memory it owns, which stays valid only
    /// until the next read; an exact reader returns a new array.
    /// </summary>
    internal async ValueTask<TdsMessage> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var type = await BeginMessageAsync(cancellationToken).ConfigureAwait(false);
        if (!_buffered)
        {
            return new TdsMessage(
              type,
              await ReadOwnedPayloadAsync(cancellationToken).ConfigureAwait(false));
        }

        // Most responses fit one packet: hand out the received bytes without copying them.
        if (_packetEndsMessage && _packetPayloadRemaining <= _buffer.Length)
        {
            var length = _packetPayloadRemaining;
            if (_end - _start < length)
            {
                await FillAsync(length, cancellationToken).ConfigureAwait(false);
            }

            var payload = _buffer.AsMemory(_start, length);
            AdvancePayload(length);
            return new TdsMessage(type, payload);
        }

        var written = 0;
        while (true)
        {
            await EnsurePayloadAsync(cancellationToken).ConfigureAwait(false);
            if (EndOfMessage)
            {
                break;
            }

            var length = _packetPayloadRemaining;
            EnsureMessageCapacity(checked(written + length), written);
            await ReadPayloadExactlyAsync(
              _messageBuffer.AsMemory(written, length),
              cancellationToken).ConfigureAwait(false);
            written += length;
        }

        return new TdsMessage(type, _messageBuffer.AsMemory(0, written));
    }

    /// <summary>
    /// Returns the payload bytes of the current packet that are already received. The span is
    /// valid until the next asynchronous read on this reader.
    /// </summary>
    internal ReadOnlySpan<byte> GetBufferedPayload()
    {
        while (_packetPayloadRemaining == 0 &&
               _messageActive &&
               !_packetEndsMessage &&
               TryReadPacketHeader(_messageType))
        {
        }

        return _buffer.AsSpan(_start, Math.Min(_end - _start, _packetPayloadRemaining));
    }

    /// <summary>Consumes bytes returned by <see cref="GetBufferedPayload"/>.</summary>
    internal void AdvancePayload(int count)
    {
        if ((uint)count > (uint)Math.Min(_end - _start, _packetPayloadRemaining))
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _start += count;
        _packetPayloadRemaining -= count;
    }

    internal bool TryReadPayload(int length, out ReadOnlySpan<byte> value)
    {
        var buffered = GetBufferedPayload();
        if ((uint)length > (uint)buffered.Length)
        {
            value = default;
            return false;
        }

        value = buffered[..length];
        _start += length;
        _packetPayloadRemaining -= length;
        return true;
    }

    internal ValueTask ReadPayloadExactlyAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        if (TryReadPayload(destination.Length, out var value))
        {
            value.CopyTo(destination.Span);
            return ValueTask.CompletedTask;
        }

        return ReadPayloadExactlySlowAsync(destination, cancellationToken);
    }

    internal ValueTask SkipPayloadAsync(
        int length,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (TryReadPayload(length, out _))
        {
            return ValueTask.CompletedTask;
        }

        return SkipPayloadSlowAsync(length, cancellationToken);
    }

    /// <summary>
    /// Returns the pooled receive buffer once no receive can still write into it. Call it after the
    /// stream is closed; a receive that is still pending returns the buffer when it completes.
    /// </summary>
    internal void Release()
    {
        if (Interlocked.Exchange(ref _state, Released) == Idle)
        {
            ReturnBuffer();
        }
    }

    private async ValueTask<byte> BeginMessageSlowAsync(CancellationToken cancellationToken)
    {
        await FillAsync(HeaderLength, cancellationToken).ConfigureAwait(false);
        _ = TryReadPacketHeader(expectedType: null);
        _messageActive = true;
        return _messageType;
    }

    private async ValueTask<ReadOnlyMemory<byte>> ReadOwnedPayloadAsync(
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> payload = new();
        while (true)
        {
            await EnsurePayloadAsync(cancellationToken).ConfigureAwait(false);
            if (EndOfMessage)
            {
                break;
            }

            var payloadLength = _packetPayloadRemaining;
            await ReadPayloadExactlyAsync(
              payload.GetMemory(payloadLength)[..payloadLength],
              cancellationToken).ConfigureAwait(false);
            payload.Advance(payloadLength);
        }

        return payload.WrittenMemory;
    }

    private ValueTask ReadPayloadExactlySlowAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken) =>
      ConsumePayloadSlowAsync(destination, destination.Length, cancellationToken);

    private ValueTask SkipPayloadSlowAsync(int length, CancellationToken cancellationToken) =>
      ConsumePayloadSlowAsync(Memory<byte>.Empty, length, cancellationToken);

    /// <summary>
    /// Consumes <paramref name="length"/> payload bytes across packets, copying them into
    /// <paramref name="destination"/> unless it is empty, in which case they are discarded.
    /// </summary>
    private async ValueTask ConsumePayloadSlowAsync(
        Memory<byte> destination,
        int length,
        CancellationToken cancellationToken)
    {
        var copy = !destination.IsEmpty;
        while (length > 0)
        {
            await EnsurePayloadAsync(cancellationToken).ConfigureAwait(false);
            if (EndOfMessage)
            {
                throw new InvalidDataException("TDS token stream is truncated at END_OF_MESSAGE.");
            }

            var buffered = Math.Min(_end - _start, _packetPayloadRemaining);
            if (buffered == 0)
            {
                if (!_buffered)
                {
                    // Exact mode reads straight into the destination, never past this packet.
                    var target = copy ? destination : _skipBuffer;
                    var count = Math.Min(
                      Math.Min(target.Length, length),
                      _packetPayloadRemaining);
                    await _stream.ReadExactlyAsync(target[..count], cancellationToken)
                      .ConfigureAwait(false);
                    _packetPayloadRemaining -= count;
                    length -= count;
                    if (copy)
                    {
                        destination = destination[count..];
                    }

                    continue;
                }

                await FillAsync(1, cancellationToken).ConfigureAwait(false);
                buffered = Math.Min(_end - _start, _packetPayloadRemaining);
            }

            var consumed = Math.Min(buffered, length);
            if (copy)
            {
                _buffer.AsSpan(_start, consumed).CopyTo(destination.Span);
                destination = destination[consumed..];
            }

            _start += consumed;
            _packetPayloadRemaining -= consumed;
            length -= consumed;
        }
    }

    private ValueTask EnsurePayloadAsync(CancellationToken cancellationToken)
    {
        while (_packetPayloadRemaining == 0 && !_packetEndsMessage)
        {
            if (!TryReadPacketHeader(_messageType))
            {
                return EnsurePayloadSlowAsync(cancellationToken);
            }
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask EnsurePayloadSlowAsync(CancellationToken cancellationToken)
    {
        while (_packetPayloadRemaining == 0 && !_packetEndsMessage)
        {
            if (!TryReadPacketHeader(_messageType))
            {
                await FillAsync(HeaderLength, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool TryReadPacketHeader(byte? expectedType)
    {
        if (_end - _start < HeaderLength)
        {
            return false;
        }

        var header = _buffer.AsSpan(_start, HeaderLength);
        int packetLength = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        if (packetLength < HeaderLength)
        {
            throw new InvalidDataException($"Invalid TDS packet length {packetLength}.");
        }

        var type = header[0];
        if (expectedType is not null && expectedType != type)
        {
            throw new InvalidDataException(
              "A TDS message changed packet type before END_OF_MESSAGE.");
        }

        _messageType = type;
        _packetPayloadRemaining = packetLength - HeaderLength;
        _packetEndsMessage = (header[1] & 0x01) != 0;
        _start += HeaderLength;
        return true;
    }

    /// <summary>
    /// Receives until at least <paramref name="minimumAvailable"/> unread bytes are buffered. A
    /// buffered reader reads as much as the stream returns; an exact reader never reads more than
    /// it was asked for.
    /// </summary>
    private async ValueTask FillAsync(int minimumAvailable, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
          Interlocked.CompareExchange(ref _state, Filling, Idle) != Idle,
          this);

        var released = false;
        try
        {
            var buffer = _buffer;
            var available = _end - _start;
            if (available > 0 && _start > 0)
            {
                buffer.AsSpan(_start, available).CopyTo(buffer);
            }

            _start = 0;
            _end = available;
            while (_end < minimumAvailable)
            {
                var target = _buffered
                  ? buffer.AsMemory(_end)
                  : buffer.AsMemory(_end, minimumAvailable - _end);
                var read = await _stream.ReadAsync(target, cancellationToken)
                  .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                      "SQL Server closed the connection in the middle of a TDS packet.");
                }

                _end += read;
            }
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _state, Idle, Filling) == Released)
            {
                ReturnBuffer();
                released = true;
            }
        }

        // The connection was disposed while this receive was pending: the bytes went back to the
        // pool with the buffer, so the caller must not parse them.
        ObjectDisposedException.ThrowIf(released, this);
    }

    private void EnsureMessageCapacity(int required, int written)
    {
        if (required <= _messageBuffer.Length)
        {
            return;
        }

        var grown = new byte[Math.Max(required, Math.Max(4096, _messageBuffer.Length * 2))];
        _messageBuffer.AsSpan(0, written).CopyTo(grown);
        _messageBuffer = grown;
    }

    private void ReturnBuffer()
    {
        var buffer = _buffer;
        _buffer = [];
        _start = 0;
        _end = 0;
        if (_buffered && buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

internal sealed class TdsPacketWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private byte[] _packetBuffer = [];
    private int _packetSize;
    private bool _disposed;

    internal TdsPacketWriter(Stream stream, int packetSize)
    {
        _stream = stream;
        PacketSize = packetSize;
    }

    internal int PacketSize
    {
        get => _packetSize;
        set
        {
            if (value is < 512 or > 32767)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _packetSize = value;
            if (_packetBuffer.Length != value)
            {
                _packetBuffer = new byte[value];
            }
        }
    }

    internal async ValueTask WriteMessageAsync(
        byte type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteMessageCoreAsync(
              _stream,
              type,
              payload,
              _packetSize,
              cancellationToken,
              _packetBuffer).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal ValueTask WriteAttentionAsync(CancellationToken cancellationToken) =>
      WriteMessageAsync(TdsMessageType.Attention, ReadOnlyMemory<byte>.Empty, cancellationToken);

    internal static async ValueTask WriteMessageCoreAsync(
        Stream stream,
        byte type,
        ReadOnlyMemory<byte> payload,
        int packetSize,
        CancellationToken cancellationToken,
        byte[]? reusablePacket = null)
    {
        var maximumPayload = packetSize - 8;
        var packet = reusablePacket is { Length: >= 512 } &&
          reusablePacket.Length >= packetSize
            ? reusablePacket
            : new byte[packetSize];
        var position = 0;
        byte packetId = 1;
        do
        {
            var count = Math.Min(maximumPayload, payload.Length - position);
            var final = position + count == payload.Length;
            packet[0] = type;
            packet[1] = final ? (byte)0x01 : (byte)0x00;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), checked((ushort)(count + 8)));
            packet[6] = packetId++;
            if (count > 0)
            {
                payload.Span.Slice(position, count).CopyTo(packet.AsSpan(8));
            }

            await stream.WriteAsync(packet.AsMemory(0, count + 8), cancellationToken)
              .ConfigureAwait(false);
            position += count;
        }
        while (position < payload.Length);

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();
    }
}

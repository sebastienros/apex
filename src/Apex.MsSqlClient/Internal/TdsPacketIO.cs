using System.Buffers;
using System.Buffers.Binary;

namespace Apex.MsSqlClient.Internal;

internal readonly record struct TdsMessage(byte Type, ReadOnlyMemory<byte> Payload);

internal sealed class TdsPacketReader
{
    private const int HeaderLength = 8;
    private readonly Stream _stream;
    private readonly byte[] _header = new byte[HeaderLength];
    private readonly byte[] _skipBuffer = new byte[512];
    private int _packetPayloadRemaining;
    private byte _messageType;
    private bool _packetEndsMessage;
    private bool _messageActive;

    internal TdsPacketReader(Stream stream)
    {
        _stream = stream;
    }

    internal bool EndOfMessage =>
      _messageActive &&
      _packetPayloadRemaining == 0 &&
      _packetEndsMessage;

    internal async ValueTask<byte> BeginMessageAsync(CancellationToken cancellationToken)
    {
        if (_messageActive && !EndOfMessage)
        {
            throw new InvalidOperationException(
              "The current TDS message must be consumed before reading the next message.");
        }

        _messageActive = false;
        await ReadPacketHeaderAsync(expectedType: null, cancellationToken).ConfigureAwait(false);
        _messageActive = true;
        return _messageType;
    }

    internal async ValueTask<TdsMessage> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var type = await BeginMessageAsync(cancellationToken).ConfigureAwait(false);
        ArrayBufferWriter<byte> payload = new();
        while (!EndOfMessage)
        {
            await EnsurePayloadAsync(cancellationToken).ConfigureAwait(false);
            if (EndOfMessage)
            {
                break;
            }

            var payloadLength = _packetPayloadRemaining;
            var destination = payload.GetMemory(payloadLength)[..payloadLength];
            await _stream.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
            _packetPayloadRemaining = 0;
            payload.Advance(payloadLength);
        }

        return new TdsMessage(type, payload.WrittenMemory);
    }

    internal async ValueTask ReadPayloadExactlyAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        while (!destination.IsEmpty)
        {
            await EnsurePayloadAsync(cancellationToken).ConfigureAwait(false);
            if (EndOfMessage)
            {
                throw new InvalidDataException("TDS token stream is truncated at END_OF_MESSAGE.");
            }

            var count = Math.Min(destination.Length, _packetPayloadRemaining);
            await _stream.ReadExactlyAsync(destination[..count], cancellationToken)
              .ConfigureAwait(false);
            _packetPayloadRemaining -= count;
            destination = destination[count..];
        }
    }

    internal async ValueTask SkipPayloadAsync(
        int length,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        while (length > 0)
        {
            var count = Math.Min(length, _skipBuffer.Length);
            await ReadPayloadExactlyAsync(
              _skipBuffer.AsMemory(0, count),
              cancellationToken).ConfigureAwait(false);
            length -= count;
        }
    }

    private async ValueTask EnsurePayloadAsync(CancellationToken cancellationToken)
    {
        while (_packetPayloadRemaining == 0 && !_packetEndsMessage)
        {
            await ReadPacketHeaderAsync(_messageType, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ReadPacketHeaderAsync(
        byte? expectedType,
        CancellationToken cancellationToken)
    {
        await _stream.ReadExactlyAsync(_header, cancellationToken).ConfigureAwait(false);
        int packetLength = BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(2));
        if (packetLength < HeaderLength)
        {
            throw new InvalidDataException($"Invalid TDS packet length {packetLength}.");
        }

        var type = _header[0];
        if (expectedType is not null && expectedType != type)
        {
            throw new InvalidDataException(
              "A TDS message changed packet type before END_OF_MESSAGE.");
        }

        _messageType = type;
        _packetPayloadRemaining = packetLength - HeaderLength;
        _packetEndsMessage = (_header[1] & 0x01) != 0;
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

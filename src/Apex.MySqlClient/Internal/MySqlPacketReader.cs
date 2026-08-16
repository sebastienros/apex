using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Apex.MySqlClient.Internal;

/// <summary>A reassembled MySQL packet backed by a pooled buffer.</summary>
internal readonly struct MySqlPacket : IDisposable
{
    private readonly byte[]? _owner;
    private readonly ReadOnlyMemory<byte> _memory;

    internal MySqlPacket(
        ReadOnlyMemory<byte> memory,
        byte[]? owner,
        byte sequence)
    {
        _memory = memory;
        _owner = owner;
        Length = memory.Length;
        Sequence = sequence;
    }

    internal int Length { get; }

    internal byte Sequence { get; }

    internal ReadOnlySpan<byte> Span => _memory.Span;

    internal ReadOnlyMemory<byte> Memory => _memory;

    internal byte Header => Length == 0 ? (byte)0 : _memory.Span[0];

    public void Dispose()
    {
        if (_owner is not null)
        {
            ArrayPool<byte>.Shared.Return(_owner);
        }
    }
}

/// <summary>
/// Reads MySQL wire frames from a pipe, joining the 16 MiB chunks the protocol uses to split
/// large payloads into a single logical packet.
/// </summary>
internal sealed class MySqlPacketReader
{
    private readonly Stream _stream;
    private byte[]? _buffer;
    private int _start;
    private int _end;

    internal MySqlPacketReader(Stream stream)
    {
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
    }

    internal MySqlPacketReader(PipeReader reader)
        : this(reader.AsStream(leaveOpen: true))
    {
    }

    internal async ValueTask<MySqlPacket> ReadAsync(CancellationToken cancellationToken)
    {
        var first = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        if (first.Length < MySqlProtocol.MaximumFramePayloadLength)
        {
            return first;
        }

        var accumulated = ArrayPool<byte>.Shared.Rent(
          Math.Min(MySqlProtocol.MaximumPayloadLength, first.Length * 2));
        try
        {
            first.Span.CopyTo(accumulated);
            var length = first.Length;
            var expectedSequence = (byte)(first.Sequence + 1);
            first.Dispose();
            while (true)
            {
                using var next = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                if (next.Sequence != expectedSequence)
                {
                    throw new InvalidDataException(
                      $"MySQL split packet sequence {next.Sequence} did not match " +
                      $"the expected sequence {expectedSequence}.");
                }

                expectedSequence++;
                if (next.Length > MySqlProtocol.MaximumPayloadLength - length)
                {
                    throw new InvalidDataException(
                      $"MySQL payload exceeds {MySqlProtocol.MaximumPayloadLength} bytes.");
                }

                if (length + next.Length > accumulated.Length)
                {
                    var grown = ArrayPool<byte>.Shared.Rent(
                      Math.Max(length + next.Length, Math.Min(
                        MySqlProtocol.MaximumPayloadLength,
                        accumulated.Length * 2)));
                    accumulated.AsSpan(0, length).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(accumulated);
                    accumulated = grown;
                }

                next.Span.CopyTo(accumulated.AsSpan(length));
                length += next.Length;
                if (next.Length < MySqlProtocol.MaximumFramePayloadLength)
                {
                    MySqlPacket joined = new(
                        accumulated.AsMemory(0, length),
                        accumulated,
                        next.Sequence);
                    accumulated = null;
                    return joined;
                }
            }
        }
        catch
        {
            if (accumulated is not null)
            {
                ArrayPool<byte>.Shared.Return(accumulated);
            }

            throw;
        }
    }

    internal ValueTask CompleteAsync(Exception? exception = null)
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Reads one packet directly from a stream, without buffering beyond its payload.</summary>
    internal static async ValueTask<MySqlPacket> ReadFromStreamAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[MySqlProtocol.PacketHeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = header[0] | (header[1] << 8) | (header[2] << 16);
        var sequence = header[3];
        if (length == 0)
        {
            return new MySqlPacket(ReadOnlyMemory<byte>.Empty, null, sequence);
        }

        var payload = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await stream.ReadExactlyAsync(payload.AsMemory(0, length), cancellationToken)
              .ConfigureAwait(false);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(payload);
            throw;
        }

        return new MySqlPacket(payload.AsMemory(0, length), payload, sequence);
    }

    private async ValueTask<MySqlPacket> ReadFrameAsync(CancellationToken cancellationToken)
    {
        await EnsureBufferedAsync(
            MySqlProtocol.PacketHeaderLength,
            cancellationToken).ConfigureAwait(false);
        var buffer = _buffer!;
        var header = BinaryPrimitives.ReadUInt32LittleEndian(
            buffer.AsSpan(_start, MySqlProtocol.PacketHeaderLength));
        var length = (int)(header & 0x00FF_FFFF);
        var sequence = (byte)(header >> 24);
        var totalLength = MySqlProtocol.PacketHeaderLength + length;

        if (totalLength > buffer.Length)
        {
            var payload = length == 0 ? null : ArrayPool<byte>.Shared.Rent(length);
            var bufferedPayload = Math.Min(length, _end - _start - MySqlProtocol.PacketHeaderLength);
            if (bufferedPayload > 0)
            {
                buffer.AsSpan(
                    _start + MySqlProtocol.PacketHeaderLength,
                    bufferedPayload).CopyTo(payload);
            }

            _start += MySqlProtocol.PacketHeaderLength + bufferedPayload;
            if (_start == _end)
            {
                _start = 0;
                _end = 0;
            }

            try
            {
                if (bufferedPayload < length)
                {
                    await _stream.ReadExactlyAsync(
                        payload!.AsMemory(bufferedPayload, length - bufferedPayload),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                if (payload is not null)
                {
                    ArrayPool<byte>.Shared.Return(payload);
                }

                throw;
            }

            return new MySqlPacket(
                payload is null
                    ? ReadOnlyMemory<byte>.Empty
                    : payload.AsMemory(0, length),
                payload,
                sequence);
        }

        await EnsureBufferedAsync(totalLength, cancellationToken).ConfigureAwait(false);
        buffer = _buffer!;
        var memory = buffer.AsMemory(
            _start + MySqlProtocol.PacketHeaderLength,
            length);
        _start += totalLength;
        if (_start == _end)
        {
            _start = 0;
            _end = 0;
        }

        return new MySqlPacket(memory, null, sequence);
    }

    private async ValueTask EnsureBufferedAsync(
        int required,
        CancellationToken cancellationToken)
    {
        var buffer = _buffer ??
            throw new ObjectDisposedException(nameof(MySqlPacketReader));
        var available = _end - _start;
        if (available >= required)
        {
            return;
        }

        if (_start > 0)
        {
            buffer.AsSpan(_start, available).CopyTo(buffer);
        }

        _start = 0;
        _end = available;
        while (_end < required)
        {
            var read = await _stream.ReadAsync(
                buffer.AsMemory(_end),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "MySQL closed the connection mid-packet.");
            }

            _end += read;
        }
    }

    internal static void WriteHeader(Span<byte> destination, int payloadLength, byte sequence)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(
          destination,
          (uint)payloadLength | ((uint)sequence << 24));
    }
}

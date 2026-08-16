using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Apex.MySqlClient.Internal;

/// <summary>A reassembled MySQL packet backed by a pooled buffer.</summary>
internal readonly struct MySqlPacket : IDisposable
{
    private readonly byte[]? _buffer;

    internal MySqlPacket(byte[]? buffer, int length, byte sequence)
    {
        _buffer = buffer;
        Length = length;
        Sequence = sequence;
    }

    internal int Length { get; }

    internal byte Sequence { get; }

    internal ReadOnlySpan<byte> Span =>
      _buffer is null ? ReadOnlySpan<byte>.Empty : _buffer.AsSpan(0, Length);

    internal ReadOnlyMemory<byte> Memory =>
      _buffer is null ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(0, Length);

    internal byte Header => Length == 0 ? (byte)0 : _buffer![0];

    public void Dispose()
    {
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }
}

/// <summary>
/// Reads MySQL wire frames from a pipe, joining the 16 MiB chunks the protocol uses to split
/// large payloads into a single logical packet.
/// </summary>
internal sealed class MySqlPacketReader
{
    private readonly PipeReader _reader;

    internal MySqlPacketReader(PipeReader reader)
    {
        _reader = reader;
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
                    MySqlPacket joined = new(accumulated, length, next.Sequence);
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

    internal ValueTask CompleteAsync(Exception? exception = null) =>
      _reader.CompleteAsync(exception);

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
            return new MySqlPacket(null, 0, sequence);
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

        return new MySqlPacket(payload, length, sequence);
    }

    private async ValueTask<MySqlPacket> ReadFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (TryReadFrame(buffer, out var packet, out var consumed))
            {
                _reader.AdvanceTo(consumed);
                return packet;
            }

            if (result.IsCompleted)
            {
                _reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException("MySQL closed the connection mid-packet.");
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static bool TryReadFrame(
        ReadOnlySequence<byte> buffer,
        out MySqlPacket packet,
        out SequencePosition consumed)
    {
        packet = default;
        consumed = buffer.Start;
        if (buffer.Length < MySqlProtocol.PacketHeaderLength)
        {
            return false;
        }

        SequenceReader<byte> reader = new(buffer);
        if (!reader.TryReadLittleEndian(out int header))
        {
            return false;
        }

        var length = header & 0x00FF_FFFF;
        var sequence = (byte)((uint)header >> 24);
        var total = MySqlProtocol.PacketHeaderLength + (long)length;
        if (buffer.Length < total)
        {
            return false;
        }

        var payload = length == 0 ? null : ArrayPool<byte>.Shared.Rent(length);
        if (payload is not null)
        {
            buffer.Slice(MySqlProtocol.PacketHeaderLength, length).CopyTo(payload);
        }

        consumed = buffer.GetPosition(total);
        packet = new MySqlPacket(payload, length, sequence);
        return true;
    }

    internal static void WriteHeader(Span<byte> destination, int payloadLength, byte sequence)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(
          destination,
          (uint)payloadLength | ((uint)sequence << 24));
    }
}

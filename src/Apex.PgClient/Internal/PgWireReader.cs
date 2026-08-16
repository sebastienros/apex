using System.Buffers;
using System.Buffers.Binary;

namespace Apex.PgClient.Internal;

internal readonly struct PgWireMessage : IDisposable
{
    public PgWireMessage(byte type, ReadOnlyMemory<byte> payload)
    {
        Type = type;
        Payload = payload;
    }

    public byte Type { get; }

    public int PayloadLength => Payload.Length;

    public ReadOnlyMemory<byte> Payload { get; }

    public void Dispose() { }
}

internal sealed class PgWireReader
{
    private const int MaximumMessageLength = 64 * 1024 * 1024;
    private readonly Stream _stream;
    private byte[]? _buffer;
    private int _start;
    private int _end;

    public PgWireReader(Stream stream)
    {
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
    }

    public async ValueTask<PgWireMessage> ReadAsync(CancellationToken cancellationToken)
    {
        await EnsureBufferedAsync(5, cancellationToken).ConfigureAwait(false);
        var buffer = _buffer!;
        var length = BinaryPrimitives.ReadInt32BigEndian(
            buffer.AsSpan(_start + 1, sizeof(int)));
        if (length < 4)
        {
            throw new InvalidDataException($"Invalid PostgreSQL message length {length}.");
        }

        if (length > MaximumMessageLength)
        {
            throw new InvalidDataException(
              $"PostgreSQL message length {length} exceeds {MaximumMessageLength} bytes.");
        }

        var totalLength = checked(1 + length);
        await EnsureBufferedAsync(totalLength, cancellationToken).ConfigureAwait(false);
        buffer = _buffer!;
        var type = buffer[_start];
        var payloadLength = length - sizeof(int);
        var payload = buffer.AsMemory(_start + 5, payloadLength);

        _start += totalLength;
        if (_start == _end)
        {
            _start = 0;
            _end = 0;
        }

        return new PgWireMessage(type, payload);
    }

    public ValueTask CompleteAsync(Exception? exception = null)
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask EnsureBufferedAsync(
        int required,
        CancellationToken cancellationToken)
    {
        var buffer = _buffer ??
            throw new ObjectDisposedException(nameof(PgWireReader));
        var available = _end - _start;
        if (available >= required)
        {
            return;
        }

        if (required > buffer.Length)
        {
            var grown = ArrayPool<byte>.Shared.Rent(required);
            buffer.AsSpan(_start, available).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(buffer);
            _buffer = buffer = grown;
        }
        else if (_start > 0)
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
                    "PostgreSQL closed the connection mid-message.");
            }

            _end += read;
        }
    }
}

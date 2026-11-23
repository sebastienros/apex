using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

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
    private const int InitialBufferLength = 32 * 1024;

    // A buffer grown past this for one large message goes back to the pool at the next read, once
    // the data still buffered fits in half of it, so a connection does not hold up to 64 MB forever.
    internal const int MaximumRetainedBufferLength = 1024 * 1024;
    private readonly Stream _stream;
    private byte[]? _buffer;
    private int _start;
    private int _end;

    public PgWireReader(Stream stream)
    {
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferLength);
    }

    internal int BufferLength => _buffer?.Length ?? 0;

    public async ValueTask<PgWireMessage> ReadAsync(CancellationToken cancellationToken)
    {
        // The previous message's payload is only valid until this call, so the buffer it points
        // into can be released here.
        if (_buffer is { Length: > MaximumRetainedBufferLength } grown)
        {
            ShrinkBuffer(grown);
        }

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

    /// <summary>
    /// Returns the next message without consuming it when it is already received in full, so a
    /// caller can inspect it and only then <see cref="Consume"/> it. The payload is valid until the
    /// next read. Returns false whenever <see cref="ReadAsync"/> would have more to do than slice
    /// the buffer (a partial or invalid message, or a grown buffer to shrink).
    /// </summary>
    internal bool TryPeek(out PgWireMessage message, out int totalLength)
    {
        message = default;
        totalLength = 0;
        var buffer = _buffer;
        var available = _end - _start;
        // ReadAsync would first give a grown buffer back to the pool (see ShrinkBuffer), so leave
        // that to it; when too much is buffered to shrink, it reads in place as this does.
        if (buffer is null ||
            available < 5 ||
            (buffer.Length > MaximumRetainedBufferLength &&
             available <= MaximumRetainedBufferLength / 2))
        {
            return false;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(_start + 1, sizeof(int)));
        if (length < 4 || length > available - 1)
        {
            return false;
        }

        totalLength = 1 + length;
        message = new PgWireMessage(
            buffer[_start],
            buffer.AsMemory(_start + 5, length - sizeof(int)));
        return true;
    }

    /// <summary>Consumes the message returned by <see cref="TryPeek"/>.</summary>
    internal void Consume(int totalLength)
    {
        Debug.Assert(totalLength >= 5 && totalLength <= _end - _start);
        _start += totalLength;
        if (_start == _end)
        {
            _start = 0;
            _end = 0;
        }
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

    private void ShrinkBuffer(byte[] buffer)
    {
        // Bytes already read ahead (for example a stream of small rows) move to a buffer just
        // large enough for them, so a busy stream does not pin the grown buffer either.
        var available = _end - _start;
        if (available > MaximumRetainedBufferLength / 2)
        {
            return;
        }

        var shrunk = ArrayPool<byte>.Shared.Rent(Math.Max(InitialBufferLength, available));
        buffer.AsSpan(_start, available).CopyTo(shrunk);
        if (Interlocked.CompareExchange(ref _buffer, shrunk, buffer) != buffer)
        {
            // CompleteAsync released the buffer concurrently; EnsureBufferedAsync reports it.
            ArrayPool<byte>.Shared.Return(shrunk);
            return;
        }

        _start = 0;
        _end = available;
        ArrayPool<byte>.Shared.Return(buffer);
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

using System.Buffers;
using System.IO.Pipelines;

namespace Apex.PgClient.Internal;

internal readonly struct PgWireMessage : IDisposable
{
    private readonly byte[]? _buffer;

    public PgWireMessage(byte type, byte[]? buffer, int payloadLength)
    {
        Type = type;
        _buffer = buffer;
        PayloadLength = payloadLength;
    }

    public byte Type { get; }

    public int PayloadLength { get; }

    public ReadOnlyMemory<byte> Payload =>
      _buffer is null ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(0, PayloadLength);

    public void Dispose()
    {
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }
}

internal sealed class PgWireReader
{
    private const int MaximumMessageLength = 64 * 1024 * 1024;
    private readonly PipeReader _reader;

    public PgWireReader(PipeReader reader)
    {
        _reader = reader;
    }

    public async ValueTask<PgWireMessage> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (TryReadMessage(buffer, out var message, out var consumed))
            {
                _reader.AdvanceTo(consumed);
                return message;
            }

            if (result.IsCompleted)
            {
                _reader.AdvanceTo(buffer.End);
                throw new EndOfStreamException("PostgreSQL closed the connection mid-message.");
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    public ValueTask CompleteAsync(Exception? exception = null) => _reader.CompleteAsync(exception);

    private static bool TryReadMessage(
        ReadOnlySequence<byte> buffer,
        out PgWireMessage message,
        out SequencePosition consumed)
    {
        message = default;
        consumed = buffer.Start;
        if (buffer.Length < 5)
        {
            return false;
        }

        SequenceReader<byte> reader = new(buffer);
        _ = reader.TryRead(out var type);
        _ = reader.TryReadBigEndian(out int length);
        if (length < 4)
        {
            throw new InvalidDataException($"Invalid PostgreSQL message length {length}.");
        }

        if (length > MaximumMessageLength)
        {
            throw new InvalidDataException(
              $"PostgreSQL message length {length} exceeds {MaximumMessageLength} bytes.");
        }

        var totalLength = 1L + length;
        if (buffer.Length < totalLength)
        {
            return false;
        }

        var payloadLength = length - 4;
        var payload = payloadLength == 0
          ? null
          : ArrayPool<byte>.Shared.Rent(payloadLength);
        if (payload is not null)
        {
            buffer.Slice(5, payloadLength).CopyTo(payload);
        }

        consumed = buffer.GetPosition(totalLength);
        message = new PgWireMessage(type, payload, payloadLength);
        return true;
    }
}

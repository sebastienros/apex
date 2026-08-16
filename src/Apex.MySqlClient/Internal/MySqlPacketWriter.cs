using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace Apex.MySqlClient.Internal;

/// <summary>
/// Frames MySQL payloads onto a pipe, splitting anything at or above the 16 MiB frame limit
/// into the chunk sequence the protocol requires.
/// </summary>
internal sealed class MySqlPacketWriter
{
    private static readonly Encoding s_utf8 = new UTF8Encoding(false, true);
    private readonly PipeWriter _writer;

    internal MySqlPacketWriter(PipeWriter writer)
    {
        _writer = writer;
    }

    internal void WritePacket(byte sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MySqlProtocol.MaximumPayloadLength)
        {
            throw new InvalidOperationException(
              $"MySQL payload exceeds {MySqlProtocol.MaximumPayloadLength} bytes.");
        }

        if (payload.Length < MySqlProtocol.MaximumFramePayloadLength)
        {
            WriteFrame(sequence, payload);
            return;
        }

        while (payload.Length >= MySqlProtocol.MaximumFramePayloadLength)
        {
            WriteFrame(sequence++, payload[..MySqlProtocol.MaximumFramePayloadLength]);
            payload = payload[MySqlProtocol.MaximumFramePayloadLength..];
        }

        WriteFrame(sequence, payload);
    }

    /// <summary>Writes a single byte command such as COM_QUIT or COM_PING.</summary>
    internal void WriteCommand(MySqlCommand command)
    {
        var destination = _writer.GetSpan(MySqlProtocol.PacketHeaderLength + 1);
        MySqlPacketReader.WriteHeader(destination, 1, 0);
        destination[MySqlProtocol.PacketHeaderLength] = (byte)command;
        _writer.Advance(MySqlProtocol.PacketHeaderLength + 1);
    }

    /// <summary>Writes a text command such as COM_QUERY without staging the payload twice.</summary>
    internal void WriteTextCommand(MySqlCommand command, string text)
    {
        var byteCount = s_utf8.GetByteCount(text);
        var payloadLength = checked(byteCount + 1);
        if (payloadLength > MySqlProtocol.MaximumPayloadLength)
        {
            throw new InvalidOperationException(
              $"MySQL payload exceeds {MySqlProtocol.MaximumPayloadLength} bytes.");
        }

        if (payloadLength >= MySqlProtocol.MaximumFramePayloadLength)
        {
            MySqlPayloadWriter payload = new(payloadLength);
            try
            {
                payload.WriteByte((byte)command);
                payload.WriteUtf8(text);
                WritePacket(0, payload.WrittenSpan);
            }
            finally
            {
                payload.Release();
            }

            return;
        }

        var destination = _writer.GetSpan(MySqlProtocol.PacketHeaderLength + payloadLength);
        MySqlPacketReader.WriteHeader(destination, payloadLength, 0);
        destination[MySqlProtocol.PacketHeaderLength] = (byte)command;
        s_utf8.GetBytes(text, destination[(MySqlProtocol.PacketHeaderLength + 1)..]);
        _writer.Advance(MySqlProtocol.PacketHeaderLength + payloadLength);
    }

    internal ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken) =>
      _writer.FlushAsync(cancellationToken);

    internal ValueTask CompleteAsync(Exception? exception = null) =>
      _writer.CompleteAsync(exception);

    private void WriteFrame(byte sequence, ReadOnlySpan<byte> payload)
    {
        var header = _writer.GetSpan(MySqlProtocol.PacketHeaderLength);
        MySqlPacketReader.WriteHeader(header, payload.Length, sequence);
        _writer.Advance(MySqlProtocol.PacketHeaderLength);
        if (!payload.IsEmpty)
        {
            _writer.Write(payload);
        }
    }
}

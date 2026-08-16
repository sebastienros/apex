using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using Apex.MySqlClient.Internal;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlPacketWriterTests
{
    [TestMethod]
    public async Task WritesSingleFrameBelowLimit()
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        MySqlPacketWriter writer = new(pipe.Writer);

        writer.WritePacket(sequence: 2, [1, 2, 3]);
        await writer.FlushAsync(CancellationToken.None);

        var frame = await ReadAllAsync(pipe.Reader, 7);
        Assert.AreEqual(3, ReadLength(frame));
        Assert.AreEqual(2, frame[3]);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, frame[4..]);
        await pipe.Writer.CompleteAsync();
    }

    [TestMethod]
    public async Task SplitsPayloadAtMaximumFrameLength()
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        MySqlPacketWriter writer = new(pipe.Writer);
        var payload = new byte[MySqlProtocol.MaximumFramePayloadLength + 10];

        writer.WritePacket(sequence: 0, payload);
        await writer.FlushAsync(CancellationToken.None);

        var firstHeader = await ReadAllAsync(pipe.Reader, 4);
        Assert.AreEqual(MySqlProtocol.MaximumFramePayloadLength, ReadLength(firstHeader));
        Assert.AreEqual(0, firstHeader[3]);
        _ = await ReadAllAsync(pipe.Reader, MySqlProtocol.MaximumFramePayloadLength);

        var secondHeader = await ReadAllAsync(pipe.Reader, 4);
        Assert.AreEqual(10, ReadLength(secondHeader));
        Assert.AreEqual(1, secondHeader[3]);
        _ = await ReadAllAsync(pipe.Reader, 10);

        await pipe.Writer.CompleteAsync();
    }

    [TestMethod]
    public async Task WritesZeroLengthContinuationFrameForExactBoundaryPayload()
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        MySqlPacketWriter writer = new(pipe.Writer);
        var payload = new byte[MySqlProtocol.MaximumFramePayloadLength];

        writer.WritePacket(sequence: 5, payload);
        await writer.FlushAsync(CancellationToken.None);

        var firstHeader = await ReadAllAsync(pipe.Reader, 4);
        Assert.AreEqual(MySqlProtocol.MaximumFramePayloadLength, ReadLength(firstHeader));
        Assert.AreEqual(5, firstHeader[3]);
        _ = await ReadAllAsync(pipe.Reader, MySqlProtocol.MaximumFramePayloadLength);

        var secondHeader = await ReadAllAsync(pipe.Reader, 4);
        Assert.AreEqual(0, ReadLength(secondHeader));
        Assert.AreEqual(6, secondHeader[3]);

        await pipe.Writer.CompleteAsync();
    }

    [TestMethod]
    public async Task WritesSingleByteCommand()
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        MySqlPacketWriter writer = new(pipe.Writer);

        writer.WriteCommand(MySqlCommand.Ping);
        await writer.FlushAsync(CancellationToken.None);

        var frame = await ReadAllAsync(pipe.Reader, 5);
        Assert.AreEqual(1, ReadLength(frame));
        Assert.AreEqual(0, frame[3]);
        Assert.AreEqual((byte)MySqlCommand.Ping, frame[4]);
        await pipe.Writer.CompleteAsync();
    }

    [TestMethod]
    public async Task WritesTextCommandWithSequenceZero()
    {
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        MySqlPacketWriter writer = new(pipe.Writer);

        writer.WriteTextCommand(MySqlCommand.Query, "SELECT 1");
        await writer.FlushAsync(CancellationToken.None);

        var frame = await ReadAllAsync(pipe.Reader, 4 + 1 + 8);
        Assert.AreEqual(9, ReadLength(frame));
        Assert.AreEqual((byte)MySqlCommand.Query, frame[4]);
        Assert.AreEqual("SELECT 1", System.Text.Encoding.UTF8.GetString(frame[5..]));
        await pipe.Writer.CompleteAsync();
    }

    private static int ReadLength(ReadOnlySpan<byte> header) =>
      header[0] | (header[1] << 8) | (header[2] << 16);

    private static async Task<byte[]> ReadAllAsync(PipeReader reader, int count)
    {
        var result = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await reader.ReadAsync();
            var buffer = read.Buffer;
            var toCopy = (int)Math.Min(count - offset, buffer.Length);
            buffer.Slice(0, toCopy).CopyTo(result.AsSpan(offset, toCopy));
            offset += toCopy;
            reader.AdvanceTo(buffer.GetPosition(toCopy));
        }

        return result;
    }
}

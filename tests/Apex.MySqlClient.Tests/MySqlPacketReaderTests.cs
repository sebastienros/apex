using System.Buffers.Binary;
using System.IO.Pipelines;
using Apex.MySqlClient.Internal;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlPacketReaderTests
{
    [TestMethod]
    public async Task ReadsSinglePacketSmallerThanFrameLimit()
    {
        Pipe pipe = new();
        MySqlPacketReader reader = new(pipe.Reader);
        var frame = CreateFrame(sequence: 3, [1, 2, 3, 4]);

        await pipe.Writer.WriteAsync(frame);

        using var packet = await reader.ReadAsync(CancellationToken.None);
        Assert.AreEqual(4, packet.Length);
        Assert.AreEqual(3, packet.Sequence);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, packet.Span.ToArray());
        await pipe.Writer.CompleteAsync();
        await reader.CompleteAsync();
    }

    [TestMethod]
    public async Task ReadsPacketSplitAcrossWrites()
    {
        Pipe pipe = new();
        MySqlPacketReader reader = new(pipe.Reader);
        var frame = CreateFrame(sequence: 0, [10, 20, 30, 40, 50]);

        var pending = reader.ReadAsync(CancellationToken.None).AsTask();
        await pipe.Writer.WriteAsync(frame.AsMemory(0, 3));
        Assert.IsFalse(pending.IsCompleted);
        await pipe.Writer.WriteAsync(frame.AsMemory(3));

        using var packet = await pending;
        CollectionAssert.AreEqual(new byte[] { 10, 20, 30, 40, 50 }, packet.Span.ToArray());
        await pipe.Writer.CompleteAsync();
        await reader.CompleteAsync();
    }

    [TestMethod]
    public async Task ReadsPacketAtEveryByteBoundary()
    {
        var frame = CreateFrame(
          sequence: 5,
          Enumerable.Range(0, 97).Select(static value => (byte)value).ToArray());
        for (var split = 0; split <= frame.Length; split++)
        {
            Pipe pipe = new();
            MySqlPacketReader reader = new(pipe.Reader);
            var pending = reader.ReadAsync(CancellationToken.None).AsTask();
            if (split > 0)
            {
                await pipe.Writer.WriteAsync(frame.AsMemory(0, split));
            }

            if (split < frame.Length)
            {
                await pipe.Writer.WriteAsync(frame.AsMemory(split));
            }

            using var packet = await pending;
            CollectionAssert.AreEqual(frame[4..], packet.Span.ToArray());
            await pipe.Writer.CompleteAsync();
            await reader.CompleteAsync();
        }
    }

    [TestMethod]
    public async Task ReassemblesExactMaximumFramePayloadFollowedByZeroLengthFrame()
    {
        // A payload of exactly 0xFFFFFF bytes must be followed by a zero-length continuation frame,
        // per the MySQL protocol's chunking rule (a frame is "final" only when its length is
        // strictly less than 0xFFFFFF).
        Pipe pipe = new();
        MySqlPacketReader reader = new(pipe.Reader);
        var first = CreateFrame(sequence: 0, new byte[MySqlProtocol.MaximumFramePayloadLength]);
        var second = CreateFrame(sequence: 1, []);

        Task writer = Task.Run(async () =>
        {
            await pipe.Writer.WriteAsync(first);
            await pipe.Writer.WriteAsync(second);
        });

        using var packet = await reader.ReadAsync(CancellationToken.None);
        await writer;

        Assert.AreEqual(MySqlProtocol.MaximumFramePayloadLength, packet.Length);
        Assert.AreEqual(1, packet.Sequence);
        await pipe.Writer.CompleteAsync();
        await reader.CompleteAsync();
    }

    [TestMethod]
    public async Task ReassemblesPayloadSplitAcrossTwoMaximumFrames()
    {
        Pipe pipe = new();
        MySqlPacketReader reader = new(pipe.Reader);
        var firstPayload = CreatePattern(MySqlProtocol.MaximumFramePayloadLength, seed: 1);
        var secondPayload = CreatePattern(128, seed: 2);
        var first = CreateFrame(sequence: 0, firstPayload);
        var second = CreateFrame(sequence: 1, secondPayload);

        Task writer = Task.Run(async () =>
        {
            await pipe.Writer.WriteAsync(first);
            await pipe.Writer.WriteAsync(second);
        });

        using var packet = await reader.ReadAsync(CancellationToken.None);
        await writer;

        Assert.AreEqual(firstPayload.Length + secondPayload.Length, packet.Length);
        CollectionAssert.AreEqual(firstPayload, packet.Span[..firstPayload.Length].ToArray());
        CollectionAssert.AreEqual(secondPayload, packet.Span[firstPayload.Length..].ToArray());
        await pipe.Writer.CompleteAsync();
        await reader.CompleteAsync();
    }

    [TestMethod]
    public async Task ThrowsWhenReassembledPayloadExceedsMaximumPayloadLength()
    {
        // 16 full 0xFFFFFF frames accumulate to 268,435,440 bytes, 16 bytes short of the 256 MiB
        // reassembly ceiling. Any further continuation frame must push the running total over the
        // limit and fail fast rather than growing the buffer without bound.
        Pipe pipe = new();
        MySqlPacketReader reader = new(pipe.Reader);
        const int fullFrameCount = 16;
        Task writer = Task.Run(async () =>
        {
            for (var i = 0; i < fullFrameCount; i++)
            {
                var frame = CreateFrame(
              sequence: (byte)i,
              new byte[MySqlProtocol.MaximumFramePayloadLength]);
                await pipe.Writer.WriteAsync(frame);
            }

            await pipe.Writer.WriteAsync(CreateFrame(sequence: fullFrameCount, new byte[17]));
        });

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
          () => reader.ReadAsync(CancellationToken.None).AsTask());

        await writer;
        await pipe.Writer.CompleteAsync();
        await reader.CompleteAsync();
    }

    [TestMethod]
    public async Task ThrowsEndOfStreamWhenConnectionClosesMidPacket()
    {
        Pipe pipe = new();
        MySqlPacketReader reader = new(pipe.Reader);
        await pipe.Writer.WriteAsync(new byte[] { 5, 0, 0, 0 });
        await pipe.Writer.CompleteAsync();

        await Assert.ThrowsExactlyAsync<EndOfStreamException>(
          () => reader.ReadAsync(CancellationToken.None).AsTask());
    }

    [TestMethod]
    public async Task ReadsZeroLengthPacketFromStream()
    {
        (var client, var server) = CreateConnectedStreams();
        await using (client)
        await using (server)
        {
            byte[] header = { 0, 0, 0, 9 };
            await client.WriteAsync(header);

            using var packet =
              await MySqlPacketReader.ReadFromStreamAsync(server, CancellationToken.None);

            Assert.AreEqual(0, packet.Length);
            Assert.AreEqual(9, packet.Sequence);
        }
    }

    [TestMethod]
    public async Task ReadsPacketFromStreamDirectly()
    {
        (var client, var server) = CreateConnectedStreams();
        await using (client)
        await using (server)
        {
            var frame = CreateFrame(sequence: 2, [9, 8, 7]);
            await client.WriteAsync(frame);

            using var packet =
              await MySqlPacketReader.ReadFromStreamAsync(server, CancellationToken.None);

            Assert.AreEqual(2, packet.Sequence);
            CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, packet.Span.ToArray());
        }
    }

    [TestMethod]
    public void WriteHeaderPacksLengthAndSequenceLittleEndian()
    {
        Span<byte> destination = stackalloc byte[4];

        MySqlPacketReader.WriteHeader(destination, 0x0102_03, 0x04);

        CollectionAssert.AreEqual(new byte[] { 0x03, 0x02, 0x01, 0x04 }, destination.ToArray());
    }

    private static byte[] CreatePattern(int length, byte seed)
    {
        var result = new byte[length];
        for (var i = 0; i < length; i++)
        {
            result[i] = unchecked((byte)(seed + i));
        }

        return result;
    }

    private static byte[] CreateFrame(byte sequence, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(
          frame,
          (uint)payload.Length | ((uint)sequence << 24));
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private static (Stream Client, Stream Server) CreateConnectedStreams()
    {
        System.Net.Sockets.Socket listener = new(
          System.Net.Sockets.AddressFamily.InterNetwork,
          System.Net.Sockets.SocketType.Stream,
          System.Net.Sockets.ProtocolType.Tcp);
        listener.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        listener.Listen();
        System.Net.Sockets.Socket client = new(
          System.Net.Sockets.AddressFamily.InterNetwork,
          System.Net.Sockets.SocketType.Stream,
          System.Net.Sockets.ProtocolType.Tcp);
        client.Connect((System.Net.IPEndPoint)listener.LocalEndPoint!);
        var server = listener.Accept();
        listener.Dispose();
        return (
          new System.Net.Sockets.NetworkStream(client, ownsSocket: true),
          new System.Net.Sockets.NetworkStream(server, ownsSocket: true));
    }
}

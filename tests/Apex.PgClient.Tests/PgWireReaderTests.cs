using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using Apex.PgClient.Internal;

namespace Apex.PgClient.Tests;

[TestClass]
public sealed class PgWireReaderTests
{
    [TestMethod]
    public async Task ReadsMessageSplitAcrossWrites()
    {
        Pipe pipe = new();
        PgWireReader reader = new(pipe.Reader);
        var frame = CreateFrame((byte)'C', [(byte)'O', (byte)'K', 0]);

        var pending = reader.ReadAsync(CancellationToken.None).AsTask();
        pipe.Writer.Write(frame.AsSpan(0, 3));
        await pipe.Writer.FlushAsync();
        Assert.IsFalse(pending.IsCompleted);

        pipe.Writer.Write(frame.AsSpan(3));
        await pipe.Writer.FlushAsync();

        using var message = await pending;
        Assert.AreEqual((byte)'C', message.Type);
        CollectionAssert.AreEqual(
          new byte[] { (byte)'O', (byte)'K', 0 },
          message.Payload.ToArray());
        await pipe.Writer.CompleteAsync();
        await reader.CompleteAsync();
    }

    [TestMethod]
    public async Task ReadsMessageAtEveryByteBoundary()
    {
        var frame = CreateFrame(
          (byte)'D',
          Enumerable.Range(0, 64).Select(static value => (byte)value).ToArray());
        for (var split = 0; split <= frame.Length; split++)
        {
            Pipe pipe = new();
            PgWireReader reader = new(pipe.Reader);
            var pending = reader.ReadAsync(CancellationToken.None).AsTask();
            if (split > 0)
            {
                pipe.Writer.Write(frame.AsSpan(0, split));
                await pipe.Writer.FlushAsync();
            }

            if (split < frame.Length)
            {
                pipe.Writer.Write(frame.AsSpan(split));
                await pipe.Writer.FlushAsync();
            }

            using var message = await pending;
            Assert.AreEqual((byte)'D', message.Type);
            CollectionAssert.AreEqual(frame[5..], message.Payload.ToArray());
            await pipe.Writer.CompleteAsync();
            await reader.CompleteAsync();
        }
    }

    [TestMethod]
    public async Task ReadsCoalescedMessagesIndividually()
    {
        Pipe pipe = new();
        PgWireReader reader = new(pipe.Reader);
        pipe.Writer.Write(CreateFrame((byte)'1', []));
        pipe.Writer.Write(CreateFrame((byte)'Z', [(byte)'I']));
        await pipe.Writer.FlushAsync();

        using var first = await reader.ReadAsync(CancellationToken.None);
        using var second = await reader.ReadAsync(CancellationToken.None);

        Assert.AreEqual((byte)'1', first.Type);
        Assert.AreEqual((byte)'Z', second.Type);
        CollectionAssert.AreEqual(new byte[] { (byte)'I' }, second.Payload.ToArray());
        await pipe.Writer.CompleteAsync();
        await reader.CompleteAsync();
    }

    [TestMethod]
    public async Task ReadsRandomlyFragmentedCoalescedCorpus()
    {
        Random random = new(42);
        var frames = Enumerable.Range(0, 100)
          .Select(index => CreateFrame((byte)'D', Enumerable.Repeat((byte)index, index % 31).ToArray()))
          .ToArray();
        var corpus = frames.SelectMany(static frame => frame).ToArray();
        Pipe pipe = new();
        PgWireReader reader = new(pipe.Reader);
        Task writer = Task.Run(async () =>
        {
            var position = 0;
            while (position < corpus.Length)
            {
                var length = Math.Min(random.Next(1, 23), corpus.Length - position);
                pipe.Writer.Write(corpus.AsSpan(position, length));
                await pipe.Writer.FlushAsync();
                position += length;
            }
        });

        for (var i = 0; i < frames.Length; i++)
        {
            using var message = await reader.ReadAsync(CancellationToken.None);
            Assert.AreEqual((byte)'D', message.Type);
            CollectionAssert.AreEqual(frames[i][5..], message.Payload.ToArray());
        }

        await writer;
        await pipe.Writer.CompleteAsync();
        await reader.CompleteAsync();
    }

    [TestMethod]
    public async Task RejectsInvalidMessageLength()
    {
        Pipe pipe = new();
        PgWireReader reader = new(pipe.Reader);
        pipe.Writer.Write(new byte[] { (byte)'D', 0, 0, 0, 3 });
        await pipe.Writer.FlushAsync();

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
          () => reader.ReadAsync(CancellationToken.None).AsTask());

        await pipe.Writer.CompleteAsync();
        await reader.CompleteAsync();
    }

    private static byte[] CreateFrame(byte type, ReadOnlySpan<byte> payload)
    {
        var frame = GC.AllocateUninitializedArray<byte>(payload.Length + 5);
        frame[0] = type;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + 4);
        payload.CopyTo(frame.AsSpan(5));
        return frame;
    }
}

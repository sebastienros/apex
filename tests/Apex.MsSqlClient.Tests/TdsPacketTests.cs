using Apex.MsSqlClient.Internal;

namespace Apex.MsSqlClient.Tests;

[TestClass]
public sealed class TdsPacketTests
{
    [TestMethod]
    public async Task FramesAndReassemblesMultiPacketMessage()
    {
        var payload = Enumerable.Range(0, 1000).Select(static value => (byte)value).ToArray();
        using MemoryStream stream = new();
        using (TdsPacketWriter writer = new(stream, 512))
        {
            await writer.WriteMessageAsync(TdsMessageType.SqlBatch, payload, default);
        }

        var framed = stream.ToArray();
        Assert.AreEqual(TdsMessageType.SqlBatch, framed[0]);
        Assert.AreEqual(0, framed[1] & 1);
        Assert.AreEqual(512, (framed[2] << 8) | framed[3]);

        stream.Position = 0;
        var message = await new TdsPacketReader(stream).ReadMessageAsync(default);
        Assert.AreEqual(TdsMessageType.SqlBatch, message.Type);
        CollectionAssert.AreEqual(payload, message.Payload.ToArray());
    }

    [TestMethod]
    public async Task WritesEmptyAttentionPacket()
    {
        using MemoryStream stream = new();
        using (TdsPacketWriter writer = new(stream, 512))
        {
            await writer.WriteAttentionAsync(default);
        }

        CollectionAssert.AreEqual(
          new byte[] { TdsMessageType.Attention, 1, 0, 8, 0, 0, 1, 0 },
          stream.ToArray());
    }

    [TestMethod]
    public async Task RejectsPacketTypeChangeDuringMessage()
    {
        byte[] malformed =
        [
          TdsMessageType.SqlBatch, 0, 0, 9, 0, 0, 1, 0, 1,
      TdsMessageType.Rpc, 1, 0, 9, 0, 0, 2, 0, 2,
    ];
        using MemoryStream stream = new(malformed);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
          () => new TdsPacketReader(stream).ReadMessageAsync(default).AsTask());
    }
}

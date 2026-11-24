using System.Buffers.Binary;
using Apex.MsSqlClient.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apex.MsSqlClient.Tests;

[TestClass]
public sealed class MsSqlSecurityAuditTests
{
    [TestMethod]
    public async Task ExactPacketReaderRejectsMessageExceedingMaxBytes()
    {
        var packet = CreateTdsPacket(TdsMessageType.TabularResult, endOfMessage: false, payloadLength: 64);
        var stream = new MemoryStream([.. packet, .. packet]);
        TdsPacketReader reader = new(stream);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await reader.ReadMessageAsync(CancellationToken.None, maxMessageBytes: 100));
    }

    [TestMethod]
    public async Task BufferedPacketReaderRejectsMessageExceedingMaxBytes()
    {
        var packet = CreateTdsPacket(TdsMessageType.TabularResult, endOfMessage: false, payloadLength: 64);
        var stream = new MemoryStream([.. packet, .. packet]);
        var reader = TdsPacketReader.CreateBuffered(stream);
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await reader.ReadMessageAsync(CancellationToken.None, maxMessageBytes: 100));
        }
        finally
        {
            reader.Release();
        }
    }

    [TestMethod]
    public async Task BufferedPacketReaderRejectsSinglePacketExceedingMaxBytes()
    {
        var packet = CreateTdsPacket(TdsMessageType.TabularResult, endOfMessage: true, payloadLength: 128);
        var stream = new MemoryStream(packet);
        var reader = TdsPacketReader.CreateBuffered(stream);
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await reader.ReadMessageAsync(CancellationToken.None, maxMessageBytes: 100));
        }
        finally
        {
            reader.Release();
        }
    }

    [TestMethod]
    public void OptionsToStringMasksPassword()
    {
        var options = MsSqlConnectOptions.Parse("Server=localhost;User Id=sa;Password=SuperSecretPassword123!;");
        var text = options.ToString();

        Assert.IsFalse(text.Contains("SuperSecretPassword123!", StringComparison.Ordinal));
        StringAssert.Contains(text, "Password = ***");
    }

    [TestMethod]
    public void FedAuthLoginToStringMasksAccessToken()
    {
        var login = new TdsFedAuthLogin(
            "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.SecretTokenPayload",
            EchoFederatedAuthenticationRequired: true,
            Nonce: ReadOnlyMemory<byte>.Empty);
        var text = login.ToString();

        Assert.IsFalse(text.Contains("SecretTokenPayload", StringComparison.Ordinal));
        StringAssert.Contains(text, "AccessToken = ***");
    }

    [TestMethod]
    public void ConnectionStringParserDoesNotLeakUnquotedPasswordFragment()
    {
        var ex = Assert.ThrowsExactly<FormatException>(() =>
            MsSqlConnectOptions.Parse("Server=localhost;Password=part1;SecretPart2"));

        Assert.IsFalse(ex.Message.Contains("SecretPart2", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TlsHandshakeStreamRejectsExcessiveConsecutiveEmptyPreLoginPackets()
    {
        var emptyPacket = CreateTdsPacket(TdsMessageType.PreLogin, endOfMessage: true, payloadLength: 0);
        using var inner = new MemoryStream();
        for (var i = 0; i < 16; i++)
        {
            inner.Write(emptyPacket);
        }

        inner.Position = 0;
        using var stream = new TdsTlsHandshakeStream(inner, packetSize: 4096);
        var buffer = new byte[16];

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await stream.ReadExactlyAsync(buffer));
    }

    [TestMethod]
    public void RoutingTokenRejectsEmptyHost()
    {
        // ENVCHANGE (0xE3): length=10, type=Routing(20), routingLength=5, protocol=0, port=1433, hostLen=0, oldLen=0
        byte[] payload =
        [
            0x0A, 0x00, // ENVCHANGE total length = 10
            TdsEnvironmentChange.Routing,
            0x05, 0x00, // routing value length = 5
            0x00,       // protocol = TCP/IP
            0x99, 0x05, // port = 1433
            0x00, 0x00, // host char count = 0
            0x00, 0x00, // old value length = 0
        ];
        TdsTokenReader reader = new(payload);

        Assert.ThrowsExactly<InvalidDataException>(() => reader.ReadEnvironmentChange());
    }

    [TestMethod]
    public void RoutingTokenRejectsOutOfBoundsLength()
    {
        // ENVCHANGE (0xE3): length=10, type=Routing(20), routingLength=100 (exceeds payload)
        byte[] payload =
        [
            0x0A, 0x00,
            TdsEnvironmentChange.Routing,
            0x64, 0x00, // routing value length = 100 (truncated)
            0x00,
            0x99, 0x05,
            0x01, 0x00,
            (byte)'a', 0x00,
        ];
        TdsTokenReader reader = new(payload);

        Assert.ThrowsExactly<InvalidDataException>(() => reader.ReadEnvironmentChange());
    }

    private static byte[] CreateTdsPacket(byte type, bool endOfMessage, int payloadLength)
    {
        var packet = new byte[8 + payloadLength];
        packet[0] = type;
        packet[1] = endOfMessage ? (byte)0x01 : (byte)0x00;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), checked((ushort)packet.Length));
        packet[6] = 1;
        return packet;
    }
}

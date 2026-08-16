using System.Buffers.Binary;
using System.Text;
using Apex.MsSqlClient.Internal;

namespace Apex.MsSqlClient.Tests;

[TestClass]
public sealed class TdsHandshakeTests
{
    [TestMethod]
    public void EncodesAndParsesPreLoginOptions()
    {
        var payload = TdsPreLogin.Encode(TdsEncryptionLevel.On);
        var response = TdsPreLogin.Parse(payload);

        Assert.AreEqual(TdsEncryptionLevel.On, response.EncryptionLevel);
        Assert.AreEqual(16, response.ServerVersion!.Major);
        Assert.IsFalse(response.MarsSupported);
        Assert.AreEqual(0xFF, payload[15]);
    }

    [TestMethod]
    public void RejectsInvalidPreLoginOffset()
    {
        var payload = TdsPreLogin.Encode(TdsEncryptionLevel.Off);
        payload[1] = 0x7F;
        payload[2] = 0xFF;

        Assert.ThrowsExactly<InvalidDataException>(() => TdsPreLogin.Parse(payload));
    }

    [TestMethod]
    public void Login7ContainsCorrectOffsetsAndObfuscatedPassword()
    {
        MsSqlConnectOptions options = new()
        {
            Host = "db",
            Username = "app",
            Password = "S3cret!",
            Database = "catalog",
            ApplicationName = "tests",
            ClientInterfaceName = "Apex",
            WorkstationId = "host",
            PacketSize = 8192,
        };

        var login = TdsLogin7.Encode(options);

        Assert.AreEqual(login.Length, BinaryPrimitives.ReadInt32LittleEndian(login));
        CollectionAssert.AreEqual(
          new byte[] { 0x04, 0x00, 0x00, 0x74 },
          login.AsSpan(4, 4).ToArray());
        Assert.AreEqual(8192, BinaryPrimitives.ReadInt32LittleEndian(login.AsSpan(8)));
        Assert.AreEqual(0xC0, login[24]);
        Assert.AreEqual(0x02, login[25]);
        Assert.AreEqual(0x10, login[27]);
        AssertField(login, 36, "host");
        AssertField(login, 40, "app");
        AssertField(login, 48, "tests");
        AssertField(login, 52, "db");
        AssertField(login, 60, "Apex");
        AssertField(login, 68, "catalog");
        int extensionPointerOffset = BinaryPrimitives.ReadUInt16LittleEndian(login.AsSpan(56));
        Assert.AreEqual(4, BinaryPrimitives.ReadUInt16LittleEndian(login.AsSpan(58)));
        var extensionOffset = BinaryPrimitives.ReadInt32LittleEndian(login.AsSpan(extensionPointerOffset));
        CollectionAssert.AreEqual(
          new byte[] { 0x0D, 1, 0, 0, 0, 1, 0xFF },
          login.AsSpan(extensionOffset, 7).ToArray());

        int passwordOffset = BinaryPrimitives.ReadUInt16LittleEndian(login.AsSpan(44));
        int passwordLength = BinaryPrimitives.ReadUInt16LittleEndian(login.AsSpan(46));
        Assert.AreEqual(options.Password.Length, passwordLength);
        CollectionAssert.AreEqual(
          TdsLogin7.ObfuscatePassword(options.Password),
          login.AsSpan(passwordOffset, passwordLength * 2).ToArray());
    }

    [TestMethod]
    public async Task TlsHandshakeBytesAreEncapsulatedInPreLoginPackets()
    {
        using MemoryStream output = new();
        using (TdsTlsHandshakeStream handshake = new(output, 512))
        {
            await handshake.WriteAsync("client hello"u8.ToArray());
        }

        var framed = output.ToArray();
        Assert.AreEqual(TdsMessageType.PreLogin, framed[0]);
        Assert.AreEqual(1, framed[1]);
        Assert.AreEqual("client hello"u8.Length + 8, (framed[2] << 8) | framed[3]);

        output.Position = 0;
        using TdsTlsHandshakeStream input = new(output, 512);
        var payload = new byte["client hello"u8.Length];
        await input.ReadExactlyAsync(payload);
        CollectionAssert.AreEqual("client hello"u8.ToArray(), payload);
    }

    private static void AssertField(byte[] login, int descriptorOffset, string expected)
    {
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(login.AsSpan(descriptorOffset));
        int length = BinaryPrimitives.ReadUInt16LittleEndian(login.AsSpan(descriptorOffset + 2));
        Assert.AreEqual(expected.Length, length);
        Assert.AreEqual(
          expected,
          Encoding.Unicode.GetString(login, offset, length * 2));
    }
}

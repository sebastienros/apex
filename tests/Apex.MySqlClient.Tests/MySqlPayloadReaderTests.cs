using Apex.MySqlClient.Internal;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlPayloadReaderTests
{
    [TestMethod]
    public void ReadsLittleEndianPrimitives()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09];
        MySqlPayloadReader reader = new(payload);

        Assert.AreEqual((byte)0x01, reader.ReadByte());
        Assert.AreEqual((ushort)0x0302, reader.ReadUInt16());
        Assert.AreEqual((uint)0x060504, reader.ReadUInt24());
        Assert.AreEqual((byte)0x07, reader.PeekByte());
        reader.Skip(1);
        Assert.AreEqual(2, reader.Remaining);
    }

    [TestMethod]
    public void ReadsUInt32AndUInt64LittleEndian()
    {
        byte[] payload = [0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        MySqlPayloadReader reader = new(payload);

        Assert.AreEqual(1u, reader.ReadUInt32());
        Assert.AreEqual(2ul, reader.ReadUInt64());
    }

    [TestMethod]
    public void ReadsLengthEncodedIntegerVariants()
    {
        // < 0xFB is a single byte; 0xFC introduces a 2 byte value; 0xFD a 3 byte value;
        // 0xFE an 8 byte value; 0xFB itself signals NULL.
        Assert.AreEqual(250ul, ReadLengthEncodedInteger([250]));
        Assert.AreEqual(500ul, ReadLengthEncodedInteger([0xFC, 0xF4, 0x01]));
        Assert.AreEqual(0x030201ul, ReadLengthEncodedInteger([0xFD, 0x01, 0x02, 0x03]));
        Assert.AreEqual(
          0x0807060504030201ul,
          ReadLengthEncodedInteger([0xFE, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]));

        MySqlPayloadReader nullReader = new([0xFB]);
        Assert.IsNull(nullReader.ReadLengthEncodedInteger());
    }

    [TestMethod]
    public void RejectsInvalidLengthEncodedIntegerPrefix()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
        {
            MySqlPayloadReader reader = new([0xFF]);
            _ = reader.ReadLengthEncodedInteger();
        });
    }

    [TestMethod]
    public void RequiredLengthEncodedIntegerRejectsNull()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
        {
            MySqlPayloadReader reader = new([0xFB]);
            _ = reader.ReadRequiredLengthEncodedInteger();
        });
    }

    [TestMethod]
    public void ReadsLengthEncodedStringAndNullVariant()
    {
        byte[] payload = [3, (byte)'a', (byte)'b', (byte)'c', 0xFB];
        MySqlPayloadReader reader = new(payload);

        Assert.AreEqual("abc", reader.ReadLengthEncodedString());
        Assert.AreEqual(string.Empty, reader.ReadLengthEncodedString());
    }

    [TestMethod]
    public void ReadsNullTerminatedString()
    {
        byte[] payload = [(byte)'o', (byte)'k', 0, (byte)'x'];
        MySqlPayloadReader reader = new(payload);

        Assert.AreEqual("ok", reader.ReadNullTerminatedString());
        Assert.AreEqual(1, reader.Remaining);
    }

    [TestMethod]
    public void RejectsUnterminatedNullTerminatedString()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
        {
            MySqlPayloadReader reader = new([(byte)'o', (byte)'k']);
            _ = reader.ReadNullTerminatedString();
        });
    }

    [TestMethod]
    public void ReadsRemainingStringAndSpan()
    {
        byte[] payload = [(byte)'h', (byte)'i'];
        MySqlPayloadReader reader = new(payload);

        Assert.AreEqual("hi", reader.ReadRemainingString());
        Assert.AreEqual(0, reader.Remaining);

        MySqlPayloadReader emptyReader = new([]);
        Assert.AreEqual(string.Empty, emptyReader.ReadRemainingString());
    }

    [TestMethod]
    public void RejectsTruncatedFixedWidthReads()
    {
        Assert.ThrowsExactly<InvalidDataException>(ReadTruncatedUInt32);
        Assert.ThrowsExactly<InvalidDataException>(ReadTruncatedUInt64);
        Assert.ThrowsExactly<InvalidDataException>(ReadEmptyByte);

        static void ReadTruncatedUInt32()
        {
            MySqlPayloadReader reader = new([0x01, 0x02, 0x03]);
            _ = reader.ReadUInt32();
        }

        static void ReadTruncatedUInt64()
        {
            MySqlPayloadReader reader = new(new byte[7]);
            _ = reader.ReadUInt64();
        }

        static void ReadEmptyByte()
        {
            MySqlPayloadReader reader = new([]);
            _ = reader.ReadByte();
        }
    }

    [TestMethod]
    public void ToLengthRejectsValuesAboveIntMaxValue()
    {
        Assert.ThrowsExactly<InvalidDataException>(
          () => MySqlPayloadReader.ToLength((ulong)int.MaxValue + 1));
        Assert.AreEqual(int.MaxValue, MySqlPayloadReader.ToLength(int.MaxValue));
    }

    private static ulong? ReadLengthEncodedInteger(byte[] payload)
    {
        MySqlPayloadReader reader = new(payload);
        return reader.ReadLengthEncodedInteger();
    }
}

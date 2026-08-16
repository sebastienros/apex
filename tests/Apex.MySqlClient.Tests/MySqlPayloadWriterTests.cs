using Apex.MySqlClient.Internal;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlPayloadWriterTests
{
    [TestMethod]
    public void WritesLittleEndianPrimitives()
    {
        MySqlPayloadWriter writer = new();
        try
        {
            writer.WriteByte(0x01);
            writer.WriteUInt16(0x0302);
            writer.WriteUInt32(0x07060504);
            writer.WriteUInt64(0x0F0E0D0C0B0A0908);
            writer.WriteInt32(-1);
            writer.WriteInt64(-1L);

            byte[] expected =
            [
              0x01,
        0x02, 0x03,
        0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
      ];
            CollectionAssert.AreEqual(expected, writer.WrittenSpan.ToArray());
        }
        finally
        {
            writer.Release();
        }
    }

    [TestMethod]
    public void WritesSingleAndDoubleAsRawBits()
    {
        MySqlPayloadWriter writer = new();
        try
        {
            writer.WriteSingle(1.5f);
            writer.WriteDouble(2.5d);

            MySqlPayloadReader reader = new(writer.WrittenSpan);
            Assert.AreEqual(1.5f, BitConverter.Int32BitsToSingle(unchecked((int)reader.ReadUInt32())));
            Assert.AreEqual(2.5d, BitConverter.Int64BitsToDouble(unchecked((long)reader.ReadUInt64())));
        }
        finally
        {
            writer.Release();
        }
    }

    [TestMethod]
    public void WriteZeroFillsWithZeroBytes()
    {
        MySqlPayloadWriter writer = new();
        try
        {
            writer.WriteByte(0xAB);
            writer.WriteZero(4);
            writer.WriteByte(0xCD);

            CollectionAssert.AreEqual(
              new byte[] { 0xAB, 0, 0, 0, 0, 0xCD },
              writer.WrittenSpan.ToArray());
        }
        finally
        {
            writer.Release();
        }
    }

    [TestMethod]
    public void WritesLengthEncodedIntegerVariants()
    {
        CollectionAssert.AreEqual(new byte[] { 250 }, WriteLengthEncoded(250));
        CollectionAssert.AreEqual(new byte[] { 0xFC, 0xF4, 0x01 }, WriteLengthEncoded(500));
        CollectionAssert.AreEqual(
          new byte[] { 0xFD, 0x01, 0x02, 0x03 },
          WriteLengthEncoded(0x030201));
        CollectionAssert.AreEqual(
          new byte[] { 0xFE, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 },
          WriteLengthEncoded(0x0807060504030201));
    }

    [TestMethod]
    public void LengthEncodedIntegerRoundTripsThroughReader()
    {
        ulong[] values =
        [
          0, 100, 250, 251, 0xFB, ushort.MaxValue, (ulong)ushort.MaxValue + 1, 0xFFFFFF,
      0xFFFFFFul + 1, ulong.MaxValue,
    ];
        foreach (var value in values)
        {
            MySqlPayloadWriter writer = new();
            try
            {
                writer.WriteLengthEncodedInteger(value);
                MySqlPayloadReader reader = new(writer.WrittenSpan);
                Assert.AreEqual(value, reader.ReadRequiredLengthEncodedInteger());
                Assert.AreEqual(0, reader.Remaining);
            }
            finally
            {
                writer.Release();
            }
        }
    }

    [TestMethod]
    public void WritesLengthEncodedBytesAndString()
    {
        MySqlPayloadWriter writer = new();
        try
        {
            writer.WriteLengthEncodedBytes([1, 2, 3]);
            writer.WriteLengthEncodedString("abc");

            MySqlPayloadReader reader = new(writer.WrittenSpan);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, reader.ReadLengthEncodedSpan(out var isNull).ToArray());
            Assert.IsFalse(isNull);
            Assert.AreEqual("abc", reader.ReadLengthEncodedString());
        }
        finally
        {
            writer.Release();
        }
    }

    [TestMethod]
    public void WritesNullTerminatedStringWithSingleTerminator()
    {
        MySqlPayloadWriter writer = new();
        try
        {
            writer.WriteNullTerminatedString("abc");

            byte[] expected = [(byte)'a', (byte)'b', (byte)'c', 0];
            CollectionAssert.AreEqual(expected, writer.WrittenSpan.ToArray());
        }
        finally
        {
            writer.Release();
        }
    }

    [TestMethod]
    public void GrowsBufferBeyondInitialCapacity()
    {
        MySqlPayloadWriter writer = new(capacity: 4);
        try
        {
            var payload = Enumerable.Range(0, 1000).Select(static value => (byte)value).ToArray();

            writer.WriteBytes(payload);

            CollectionAssert.AreEqual(payload, writer.WrittenSpan.ToArray());
            Assert.AreEqual(1000, writer.Length);
        }
        finally
        {
            writer.Release();
        }
    }

    [TestMethod]
    public void ResetReusesBufferWithoutReleasingIt()
    {
        MySqlPayloadWriter writer = new();
        try
        {
            writer.WriteByte(1);
            writer.WriteByte(2);
            writer.Reset();

            Assert.AreEqual(0, writer.Length);
            writer.WriteByte(9);
            Assert.AreEqual(1, writer.Length);
            Assert.AreEqual((byte)9, writer.WrittenSpan[0]);
        }
        finally
        {
            writer.Release();
        }
    }

    [TestMethod]
    public void ReleaseIsIdempotentAndResetsState()
    {
        MySqlPayloadWriter writer = new();
        writer.WriteByte(1);

        writer.Release();
        writer.Release();

        Assert.AreEqual(0, writer.Length);
    }

    private static byte[] WriteLengthEncoded(ulong value)
    {
        MySqlPayloadWriter writer = new();
        try
        {
            writer.WriteLengthEncodedInteger(value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Release();
        }
    }
}

using System.Buffers.Binary;
using System.Collections;
using System.Net.NetworkInformation;
using System.Numerics;
using Apex.PgClient.Internal;

namespace Apex.PgClient.Tests;

[TestClass]
public sealed class PgBinaryCodecTests
{
    [TestMethod]
    public void DecodesPrimitiveAndInfinityValues()
    {
        Assert.AreEqual(42, PgBinaryCodec.Decode(23, Int32(42)));
        Assert.AreEqual(DateOnly.MaxValue, PgBinaryCodec.Decode(1082, Int32(int.MaxValue)));
        Assert.AreEqual(DateTimeOffset.MinValue, PgBinaryCodec.Decode(1184, Int64(long.MinValue)));
    }

    [TestMethod]
    public void DecodesArbitraryNumeric()
    {
        byte[] numeric =
        [
          .. Int16(3),
      .. Int16(1),
      .. Int16(0),
      .. Int16(2),
      .. Int16(1),
      .. Int16(2345),
      .. Int16(6700),
    ];

        PgNumeric value = (PgNumeric)PgBinaryCodec.Decode(1700, numeric);

        Assert.AreEqual("12345.67", value.ToString());

        byte[] weighted =
        [
          .. Int16(1),
      .. Int16(1),
      .. Int16(0),
      .. Int16(0),
      .. Int16(1),
    ];
        Assert.AreEqual(
          "10000",
          ((PgNumeric)PgBinaryCodec.Decode(1700, weighted)).ToString());
    }

    [TestMethod]
    public void DecodesBclScalarAlternatives()
    {
        byte[] integerNumeric =
        [
            .. Int16(2),
                    .. Int16(1),
                    .. Int16(0),
                    .. Int16(0),
                    .. Int16(1234),
                    .. Int16(5678),
                ];
        Assert.AreEqual(new BigInteger(12_345_678), PgBinaryCodec.DecodeBigInteger(integerNumeric));

        byte[] interval =
        [
            .. Int64(2_000_003),
                    .. Int32(1),
                    .. Int32(0),
                ];
        Assert.AreEqual(
            TimeSpan.FromDays(1) + TimeSpan.FromSeconds(2) + TimeSpan.FromTicks(30),
            PgBinaryCodec.DecodeTimeSpan(interval));
        Assert.AreEqual(
            PhysicalAddress.Parse("08-00-2B-01-02-03"),
            PgBinaryCodec.DecodePhysicalAddress([0x08, 0x00, 0x2b, 0x01, 0x02, 0x03]));
        CollectionAssert.AreEqual(
            new[] { true, false, true, true },
            ToBooleans(PgBinaryCodec.DecodeBitArray([0, 0, 0, 4, 0b1011_0000])));

        byte[] fractionalNumeric =
        [
            .. Int16(1),
                    .. Int16(0),
                    .. Int16(0),
                    .. Int16(1),
                    .. Int16(15),
                ];
        Assert.ThrowsExactly<InvalidCastException>(() =>
            PgBinaryCodec.DecodeBigInteger(fractionalNumeric));
    }

    [TestMethod]
    public void DecodesOneDimensionalArrayWithNull()
    {
        byte[] array =
        [
          .. Int32(1),
      .. Int32(1),
      .. Int32(23),
      .. Int32(3),
      .. Int32(1),
      .. Int32(4),
      .. Int32(1),
      .. Int32(-1),
      .. Int32(4),
      .. Int32(3),
    ];

        var values = PgBinaryCodec.DecodeArray<int?>(1007, array);
        var objectValue = PgBinaryCodec.Decode(1007, array);

        CollectionAssert.AreEqual(new int?[] { 1, null, 3 }, values);
        Assert.IsInstanceOfType<int?[]>(objectValue);
        Assert.ThrowsExactly<InvalidCastException>(() =>
            PgBinaryCodec.DecodeArray<int>(1007, array));
    }

    [TestMethod]
    public void TypedArrayDoesNotBoxElements()
    {
        const int count = 100;
        List<byte> payload =
        [
          .. Int32(1),
                      .. Int32(0),
                      .. Int32(23),
                      .. Int32(count),
                      .. Int32(1),
                    ];
        for (var i = 0; i < count; i++)
        {
            payload.AddRange(Int32(sizeof(int)));
            payload.AddRange(Int32(i));
        }

        byte[] array = payload.ToArray();
        _ = PgBinaryCodec.DecodeArray<int>(1007, array);
        _ = new int[count];

        var before = GC.GetAllocatedBytesForCurrentThread();
        var baseline = new int[count];
        var arrayAllocation = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        var decoded = PgBinaryCodec.DecodeArray<int>(1007, array);
        var decodeAllocation = GC.GetAllocatedBytesForCurrentThread() - before;

        GC.KeepAlive(baseline);
        CollectionAssert.AreEqual(Enumerable.Range(0, count).ToArray(), decoded);
        Assert.AreEqual(arrayAllocation, decodeAllocation);
    }

    [TestMethod]
    public void DecodesInterval()
    {
        byte[] interval =
        [
          .. Int64(3_600_000_123),
      .. Int32(2),
      .. Int32(14),
    ];

        PgInterval value = (PgInterval)PgBinaryCodec.Decode(1186, interval);

        Assert.AreEqual(new PgInterval(1, 2, 2, 1, 0, 0, 123), value);
    }

    [TestMethod]
    public void RejectsTruncatedValue()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
          PgBinaryCodec.Decode(23, new byte[3]));
    }

    private static byte[] Int16(short value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Int64(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }

    private static bool[] ToBooleans(BitArray value)
    {
        var result = new bool[value.Count];
        value.CopyTo(result, 0);
        return result;
    }
}

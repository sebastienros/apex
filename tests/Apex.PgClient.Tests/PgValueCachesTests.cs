using System.Text;
using System.Runtime.InteropServices;
using Apex.PgClient.Internal;

namespace Apex.PgClient.Tests;

[TestClass]
public sealed class PgValueCachesTests
{
    [TestMethod]
    public void CachesRepeatedSmallUtf8AfterSecondObservation()
    {
        Utf8StringCache cache = new(capacity: 16, maximumByteLength: 64);
        var value = Encoding.UTF8.GetBytes("repeated");

        var first = cache.GetString(value);
        var second = cache.GetString(value);
        var third = cache.GetString(value);

        Assert.AreNotSame(first, second);
        Assert.AreSame(second, third);
    }

    [TestMethod]
    public void DoesNotCacheValuesAboveMaximumLength()
    {
        Utf8StringCache cache = new(capacity: 16, maximumByteLength: 4);
        var value = Encoding.UTF8.GetBytes("longer");

        var first = cache.GetString(value);
        var second = cache.GetString(value);
        var third = cache.GetString(value);

        Assert.AreNotSame(first, second);
        Assert.AreNotSame(second, third);
    }

    [TestMethod]
    public void CachesRepeatedSmallUtf8BytesBehindReadOnlyMemory()
    {
        Utf8BytesCache cache = new(capacity: 16, maximumByteLength: 64);
        var value = Encoding.UTF8.GetBytes("repeated");

        var first = cache.GetBytes(value);
        var second = cache.GetBytes(value);
        var third = cache.GetBytes(value);

        Assert.IsFalse(HasSameBackingArray(first, second));
        Assert.IsTrue(HasSameBackingArray(second, third));
        Assert.IsTrue(third.Span.SequenceEqual(value));
    }

    [TestMethod]
    public void DirectMappedReplacementNeverReturnsCollisionValue()
    {
        Utf8StringCache cache = new(capacity: 1, maximumByteLength: 64);
        var firstValue = Encoding.UTF8.GetBytes("first");
        var secondValue = Encoding.UTF8.GetBytes("second");
        _ = cache.GetString(firstValue);
        var cachedFirst = cache.GetString(firstValue);
        _ = cache.GetString(secondValue);
        var cachedSecond = cache.GetString(secondValue);

        Assert.AreEqual("first", cachedFirst);
        Assert.AreEqual("second", cachedSecond);
        Assert.AreEqual("first", cache.GetString(firstValue));
    }

    [TestMethod]
    public void ReusesPreboxedCommonScalars()
    {
        Assert.AreSame(BoxedScalarCache.Box(true), BoxedScalarCache.Box(true));
        Assert.AreSame(BoxedScalarCache.Box((short)42), BoxedScalarCache.Box((short)42));
        Assert.AreSame(BoxedScalarCache.Box(42), BoxedScalarCache.Box(42));
        Assert.AreSame(BoxedScalarCache.Box(42L), BoxedScalarCache.Box(42L));
        Assert.AreNotSame(BoxedScalarCache.Box(1000), BoxedScalarCache.Box(1000));
    }

    [TestMethod]
    public void DisableClearsCacheAndStopsRetainingValues()
    {
        Utf8StringCache cache = new(capacity: 16, maximumByteLength: 64);
        var value = Encoding.UTF8.GetBytes("repeated");
        _ = cache.GetString(value);
        var cached = cache.GetString(value);

        cache.Disable();
        var afterDisable = cache.GetString(value);

        Assert.AreNotSame(cached, afterDisable);
        Assert.AreNotSame(afterDisable, cache.GetString(value));
    }

    [TestMethod]
    public void ConcurrentReadsAndDisableRemainSafe()
    {
        Utf8StringCache cache = new(capacity: 16, maximumByteLength: 64);
        byte[][] values =
        [
          Encoding.UTF8.GetBytes("first"),
          Encoding.UTF8.GetBytes("second"),
          Encoding.UTF8.GetBytes("third"),
        ];

        Parallel.For(0, 10_000, index =>
        {
            if (index == 5_000)
            {
                cache.Disable();
            }

            var value = values[index % values.Length];
            Assert.AreEqual(Encoding.UTF8.GetString(value), cache.GetString(value));
        });
    }

    private static bool HasSameBackingArray(
        ReadOnlyMemory<byte> left,
        ReadOnlyMemory<byte> right)
    {
        Assert.IsTrue(MemoryMarshal.TryGetArray(left, out var leftSegment));
        Assert.IsTrue(MemoryMarshal.TryGetArray(right, out var rightSegment));
        return ReferenceEquals(leftSegment.Array, rightSegment.Array);
    }
}

using System.Text;
using Apex.MySqlClient.Internal;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlValueCachesTests
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
    public void EmptyValueAlwaysReturnsSharedEmptyString()
    {
        Utf8StringCache cache = new(capacity: 16, maximumByteLength: 64);

        Assert.AreSame(string.Empty, cache.GetString(ReadOnlySpan<byte>.Empty));
    }

    [TestMethod]
    public void ZeroCapacityCacheNeverRetainsValues()
    {
        Utf8StringCache cache = new(capacity: 0, maximumByteLength: 0);
        var value = Encoding.UTF8.GetBytes("abc");

        var first = cache.GetString(value);
        var second = cache.GetString(value);

        Assert.AreEqual("abc", first);
        Assert.AreNotSame(first, second);
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

    [TestMethod]
    public void ReusesPreboxedCommonScalars()
    {
        Assert.AreSame(BoxedScalarCache.Box(true), BoxedScalarCache.Box(true));
        Assert.AreSame(BoxedScalarCache.Box(false), BoxedScalarCache.Box(false));
        Assert.AreSame(BoxedScalarCache.Box((sbyte)-1), BoxedScalarCache.Box((sbyte)-1));
        Assert.AreSame(BoxedScalarCache.Box((byte)200), BoxedScalarCache.Box((byte)200));
        Assert.AreSame(BoxedScalarCache.Box((short)42), BoxedScalarCache.Box((short)42));
        Assert.AreSame(BoxedScalarCache.Box((ushort)42), BoxedScalarCache.Box((ushort)42));
        Assert.AreSame(BoxedScalarCache.Box(42), BoxedScalarCache.Box(42));
        Assert.AreSame(BoxedScalarCache.Box(42u), BoxedScalarCache.Box(42u));
        Assert.AreSame(BoxedScalarCache.Box(42L), BoxedScalarCache.Box(42L));
        Assert.AreSame(BoxedScalarCache.Box(42ul), BoxedScalarCache.Box(42ul));
    }

    [TestMethod]
    public void ValuesOutsideBoxRangeAreNotShared()
    {
        Assert.AreNotSame(BoxedScalarCache.Box(1000), BoxedScalarCache.Box(1000));
        Assert.AreNotSame(BoxedScalarCache.Box(1000L), BoxedScalarCache.Box(1000L));
        Assert.AreNotSame(BoxedScalarCache.Box(1000u), BoxedScalarCache.Box(1000u));
        Assert.AreNotSame(BoxedScalarCache.Box(1000ul), BoxedScalarCache.Box(1000ul));
    }

    [TestMethod]
    public void BoxedScalarsPreserveTheirValue()
    {
        Assert.AreEqual((short)-100, BoxedScalarCache.Box((short)-100));
        Assert.AreEqual(-100, BoxedScalarCache.Box(-100));
        Assert.AreEqual(-100L, BoxedScalarCache.Box(-100L));
        Assert.AreEqual((byte)255, BoxedScalarCache.Box((byte)255));
    }
}

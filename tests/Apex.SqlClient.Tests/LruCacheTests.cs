using Apex.SqlClient.Internal;

namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class LruCacheTests
{
    [TestMethod]
    public void EvictsLeastRecentlyUsedEntry()
    {
        LruCache<string, int> cache = new(2, StringComparer.Ordinal);
        Assert.IsFalse(cache.Add("one", 1, out _));
        Assert.IsFalse(cache.Add("two", 2, out _));
        Assert.IsTrue(cache.TryGet("one", out var one));
        Assert.AreEqual(1, one);

        Assert.IsTrue(cache.Add("three", 3, out var evicted));

        Assert.AreEqual(2, evicted);
        Assert.IsFalse(cache.TryGet("two", out _));
        Assert.IsTrue(cache.TryGet("one", out _));
        Assert.IsTrue(cache.TryGet("three", out _));
    }

    [TestMethod]
    public void RemovesEntry()
    {
        LruCache<string, int> cache = new(2, StringComparer.Ordinal);
        cache.Add("one", 1, out _);

        Assert.IsTrue(cache.Remove("one", out var removed));
        Assert.AreEqual(1, removed);
        Assert.IsFalse(cache.TryGet("one", out _));
    }

    [TestMethod]
    public void DrainsValuesAndClearsCache()
    {
        LruCache<string, int> cache = new(2, StringComparer.Ordinal);
        cache.Add("one", 1, out _);
        cache.Add("two", 2, out _);

        var values = cache.DrainValues();

        CollectionAssert.AreEquivalent(new[] { 1, 2 }, values);
        Assert.AreEqual(0, cache.Count);
        Assert.IsFalse(cache.TryGet("one", out _));
        Assert.IsFalse(cache.TryGet("two", out _));
    }
}

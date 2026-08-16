using Apex.SqlClient.Internal;

namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class SqlRowTests
{
    private static readonly SqlColumn[] s_columns =
    [
        new("id", 23, 4, -1, SqlDataFormat.Binary),
        new("message", 25, -1, -1, SqlDataFormat.Text),
    ];

    [TestMethod]
    public void GetsValuesByOrdinalAndName()
    {
        var row = new TestRowDecoder().CreateRow(
          s_columns,
          1,
          "hello");

        Assert.AreEqual(1, row.Get<int>(0));
        Assert.AreEqual(1, row.Get<int?>(0));
        Assert.AreEqual(1, row.Get<object>(0));
        Assert.AreEqual("hello", row.Get<string>(row.GetOrdinal("message")));
        Assert.AreEqual("hello", row.Get<string>("message"));
    }

    [TestMethod]
    public void NameLookupIsOrdinal()
    {
        var row = new TestRowDecoder().CreateRow(
          s_columns,
          1,
          "hello");

        Assert.ThrowsExactly<IndexOutOfRangeException>(() => row.GetOrdinal("MESSAGE"));
    }

    [TestMethod]
    public void NameLookupReturnsFirstDuplicate()
    {
        SqlColumn[] columns =
        [
          new("value", 23, 4, -1, SqlDataFormat.Binary),
          new("value", 23, 4, -1, SqlDataFormat.Binary),
        ];
        var row = new TestRowDecoder().CreateRow(columns, 1, 2);

        Assert.AreEqual(0, row.GetOrdinal("value"));
        Assert.AreEqual(1, row.GetInt32("value"));
    }

    [TestMethod]
    public void OrdinalMapIsSharedForMatchingColumnNames()
    {
        SqlColumn[] first =
        [
          new("id", 23, 4, -1, SqlDataFormat.Binary),
          new("message", 25, -1, -1, SqlDataFormat.Text),
        ];
        SqlColumn[] second =
        [
          new("id", 25, -1, -1, SqlDataFormat.Text),
          new("message", 23, 4, -1, SqlDataFormat.Binary),
        ];

        var firstMap = SqlColumnOrdinalMapCache.GetOrAdd(first);
        var secondMap = SqlColumnOrdinalMapCache.GetOrAdd(second);

        Assert.AreSame(firstMap, secondMap);
    }

    [TestMethod]
    public void NullValueCannotBeReadAsNonNullableValueType()
    {
        var row = new TestRowDecoder().CreateRow(
          s_columns,
          null,
          "hello");

        Assert.IsTrue(row.IsNull(0));
        Assert.IsNull(row.Get<int?>(0));
        Assert.ThrowsExactly<InvalidCastException>(() => row.Get<int>(0));
    }

    [TestMethod]
    public void TypedInt32AccessDoesNotAllocate()
    {
        TestRowDecoder decoder = new();
        var row = decoder.CreateRow(
          [new("id", 23, 4, -1, SqlDataFormat.Binary)],
          42);
        for (var i = 0; i < 1000; i++)
        {
            _ = row.GetInt32(0);
            _ = row.Get<int>(0);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0;
        for (var i = 0; i < 10_000; i++)
        {
            sum += row.GetInt32(0);
            sum += row.Get<int>(0);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(840_000, sum);
        Assert.AreEqual(0, allocated);
    }
}

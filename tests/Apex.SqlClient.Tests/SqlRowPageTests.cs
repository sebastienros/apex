using Apex.SqlClient.Internal;

namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class SqlRowPageTests
{
    [TestMethod]
    public void DecodesPageValuesLazily()
    {
        TestRowDecoder decoder = new();
        SqlColumn[] columns = [new("value", 23, 4, -1, SqlDataFormat.Binary)];
        SqlRowPageBuilder builder = new(decoder, rowCapacity: 2, byteCapacity: 8);
        builder.Add(TestRowDecoder.Encode(1));
        builder.Add(TestRowDecoder.Encode(2));

        var rows = builder.Build(columns);

        Assert.AreEqual(0, decoder.DecodeCount);
        Assert.AreEqual(1, rows[0].GetInt32(0));
        Assert.AreEqual(1, decoder.DecodeCount);
        Assert.AreEqual(2, rows[1].Get<int>(0));
        Assert.AreEqual(2, decoder.DecodeCount);
    }

    [TestMethod]
    public void BuildsLargeResultsAcrossBoundedPages()
    {
        TestRowDecoder decoder = new();
        SqlColumn[] columns = [new("value", 23, 4, -1, SqlDataFormat.Binary)];
        SqlRowPageCollectionBuilder builder = new(decoder);
        for (var i = 0; i < 300; i++)
        {
            builder.Add(TestRowDecoder.Encode(i & 0xff));
        }

        var rows = builder.Build(columns);

        Assert.HasCount(300, rows);
        Assert.AreEqual(0, rows[0].GetInt32(0));
        Assert.AreEqual(43, rows[299].GetInt32(0));
    }

    [TestMethod]
    public void BuildsEmptyAndSingleRowResults()
    {
        TestRowDecoder decoder = new();
        SqlColumn[] columns = [new("value", 23, 4, -1, SqlDataFormat.Binary)];
        SqlRowPageCollectionBuilder empty = new(decoder);
        SqlRowPageCollectionBuilder single = new(decoder);
        single.Add(TestRowDecoder.Encode(42));

        Assert.HasCount(0, empty.Build(columns));
        var rows = single.Build(columns);
        Assert.HasCount(1, rows);
        Assert.AreEqual(42, rows[0].GetInt32(0));
    }

}

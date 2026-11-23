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

    [TestMethod]
    public void BuiltRowsDoNotAliasScratchBuffersReusedByLaterResults()
    {
        TestRowDecoder decoder = new();
        SqlColumn[] columns = [new("value", 23, 4, -1, SqlDataFormat.Binary)];
        SqlRowPageCollectionBuilder first = new(decoder);
        for (var i = 0; i < 600; i++)
        {
            first.Add(TestRowDecoder.Encode(i));
        }

        var rows = first.Build(columns);
        for (var round = 0; round < 4; round++)
        {
            SqlRowPageCollectionBuilder later = new(decoder);
            for (var i = 0; i < 600; i++)
            {
                later.Add(TestRowDecoder.Encode(-1 - i));
            }

            _ = later.Build(columns);
        }

        for (var i = 0; i < rows.Length; i++)
        {
            Assert.AreEqual(i, rows[i].GetInt32(0));
        }

        Assert.AreSame(rows, first.Build(columns));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => first.Add(TestRowDecoder.Encode(1)));
    }

    [TestMethod]
    public void SplitsPagesAtTheByteLimitAndKeepsOversizedRowsWhole()
    {
        TestRowDecoder decoder = new();
        SqlColumn[] columns = [new("value", 17, -1, -1, SqlDataFormat.Binary)];
        SqlRowPageCollectionBuilder builder = new(decoder);
        byte[][] payloads =
        [
            Filled(40_000, 1),
            Filled(40_000, 2),
            Filled(100_000, 3),
            Filled(10, 4),
            [],
        ];
        foreach (var payload in payloads)
        {
            builder.Add(TestRowDecoder.Encode(payload));
        }

        var rows = builder.Build(columns);

        Assert.HasCount(payloads.Length, rows);
        for (var i = 0; i < payloads.Length; i++)
        {
            CollectionAssert.AreEqual(payloads[i], rows[i].GetBytes(0));
        }
    }

    private static byte[] Filled(int length, byte value)
    {
        var bytes = new byte[length];
        bytes.AsSpan().Fill(value);
        return bytes;
    }
}

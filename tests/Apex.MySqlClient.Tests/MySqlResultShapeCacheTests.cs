using Apex.MySqlClient.Internal;
using Apex.SqlClient.Internal;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlResultShapeCacheTests
{
    [TestMethod]
    public void ReusesDecoderForIdenticalDefinitions()
    {
        var cache = CreateCache();

        var first = Read(cache, binary: false, ("id", MySqlType.Long), ("name", MySqlType.VarString));
        var second = Read(cache, binary: false, ("id", MySqlType.Long), ("name", MySqlType.VarString));

        Assert.AreSame(first, second);
        Assert.AreSame(first.Columns, second.Columns);
        Assert.AreEqual(2, first.FieldCount);
        Assert.AreEqual("id", first.Columns[0].Name);
        Assert.AreEqual("name", first.Metadata[1].Name);
        Assert.AreEqual("schema", first.Metadata[1].Schema);
        Assert.AreEqual("table", first.Metadata[1].Table);
    }

    [TestMethod]
    public void SeparatesProtocolsAndDifferentDefinitions()
    {
        var cache = CreateCache();

        var text = Read(cache, binary: false, ("id", MySqlType.Long));
        var binary = Read(cache, binary: true, ("id", MySqlType.Long));
        var renamed = Read(cache, binary: false, ("key", MySqlType.Long));
        var retyped = Read(cache, binary: false, ("id", MySqlType.LongLong));

        Assert.AreNotSame(text, binary);
        Assert.IsFalse(text.IsBinary);
        Assert.IsTrue(binary.IsBinary);
        Assert.AreSame(text.Metadata, binary.Metadata);
        Assert.AreNotSame(text, renamed);
        Assert.AreEqual("key", renamed.Columns[0].Name);
        Assert.AreNotSame(text, retyped);
        Assert.AreEqual(MySqlType.LongLong, retyped.Metadata[0].Type);
        Assert.AreSame(text, Read(cache, binary: false, ("id", MySqlType.Long)));
    }

    [TestMethod]
    public void HandlesManyShapesAndLargeDefinitions()
    {
        var cache = CreateCache();
        for (var i = 0; i < 100; i++)
        {
            var decoder = Read(cache, binary: false, ($"c{i}", MySqlType.Long));
            Assert.AreEqual($"c{i}", decoder.Columns[0].Name);
        }

        var wide = Enumerable.Range(0, 400)
          .Select(static i => ($"a_rather_long_column_name_{i:D4}", MySqlType.VarString))
          .ToArray();
        var first = Read(cache, binary: true, wide);
        var second = Read(cache, binary: true, wide);
        Assert.AreEqual(400, second.FieldCount);
        Assert.AreEqual("a_rather_long_column_name_0399", second.Columns[399].Name);
        // Definitions above the cached key limit are parsed every time.
        Assert.AreNotSame(first, second);
    }

    private static MySqlResultShapeCache CreateCache() =>
      new(new Utf8StringCache(capacity: 16, maximumByteLength: 64), MySqlZeroDateBehavior.Error);

    private static MySqlRowDecoder Read(
        MySqlResultShapeCache cache,
        bool binary,
        params (string Name, MySqlType Type)[] columns)
    {
        cache.Begin();
        foreach (var (name, type) in columns)
        {
            cache.Append(BuildDefinition(name, type));
        }

        return cache.Complete(columns.Length, binary);
    }

    private static byte[] BuildDefinition(string name, MySqlType type)
    {
        MySqlPayloadWriter writer = new();
        try
        {
            writer.WriteLengthEncodedString("def");
            writer.WriteLengthEncodedString("schema");
            writer.WriteLengthEncodedString("table");
            writer.WriteLengthEncodedString("table");
            writer.WriteLengthEncodedString(name);
            writer.WriteLengthEncodedString(name);
            writer.WriteLengthEncodedInteger(12);
            writer.WriteUInt16(MySqlProtocol.Utf8Mb4Collation);
            writer.WriteUInt32(11);
            writer.WriteByte((byte)type);
            writer.WriteUInt16(0);
            writer.WriteByte(0);
            writer.WriteUInt16(0);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Release();
        }
    }
}

using System.Runtime.CompilerServices;

namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class SqlParametersTests
{
    [TestMethod]
    public void CreateCopiesInputArray()
    {
        SqlValue[] values = [1, "two", SqlValue.Null];

        SqlParameters parameters = SqlParameters.Create(values);
        values[0] = 42;

        Assert.AreEqual(3, parameters.Count);
        Assert.AreEqual(1, parameters[0].Get<int>());
        Assert.AreEqual("two", parameters[1].Get<string>());
        Assert.IsTrue(parameters[2].IsNull);
    }

    [TestMethod]
    public void StoresCommonScalarsWithoutObjectInput()
    {
        SqlParameters parameters = SqlParameters.Create(
          true,
          (short)2,
          3,
          4L,
          5.5f,
          6.5d,
          7.5m);

        Assert.AreEqual(SqlValueKind.Boolean, parameters[0].Kind);
        Assert.AreEqual(3, parameters[2].Get<int>());
        Assert.AreEqual(7.5m, parameters[6].Get<decimal>());
    }

    [TestMethod]
    public void StoresSixteenByteScalarsInline()
    {
        Guid guid = Guid.Parse("12345678-1234-5678-9012-123456789abc");
        DateTimeOffset timestamp = new(2026, 8, 16, 9, 30, 0, TimeSpan.FromHours(-7));

        SqlValue guidValue = guid;
        SqlValue timestampValue = timestamp;

        Assert.AreEqual(guid, guidValue.Get<Guid>());
        Assert.AreEqual(guid, guidValue.ToObject());
        Assert.AreEqual(timestamp, timestampValue.Get<DateTimeOffset>());
        Assert.AreEqual(timestamp, timestampValue.ToObject());
    }

    [TestMethod]
    public void UsesCompactValueLayout()
    {
        Assert.AreEqual(32, Unsafe.SizeOf<SqlValue>());
    }

    [TestMethod]
    public void DistinguishesByteAndSignedByteArrays()
    {
        byte[] bytes = [1, 2];
        sbyte[] signedBytes = [-1, 2];

        SqlValue binary = SqlValue.From(bytes);
        SqlValue signed = SqlValue.From(signedBytes);

        Assert.AreEqual(SqlValueKind.Bytes, binary.Kind);
        Assert.AreSame(bytes, binary.Get<byte[]>());
        Assert.AreEqual(SqlValueKind.Object, signed.Kind);
        Assert.AreSame(signedBytes, signed.Get<sbyte[]>());
    }

    [TestMethod]
    public void DefaultValueIsEmpty()
    {
        SqlParameters parameters = default;

        Assert.AreEqual(0, parameters.Count);
        Assert.AreEqual(0, parameters.Count());
    }
}

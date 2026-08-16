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

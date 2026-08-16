namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class SqlCommandResultTests
{
    [TestMethod]
    public void PreservesTwoValueConstructionAndDeconstruction()
    {
        SqlCommandResult result = new(3, "UPDATE");
        (var affectedRows, var commandTag) = result;

        Assert.AreEqual(3L, affectedRows);
        Assert.AreEqual("UPDATE", commandTag);
        Assert.IsNull(result.LastInsertId);
        Assert.AreEqual(0U, result.StatusFlags);
        Assert.AreEqual(0, result.WarningCount);
    }

    [TestMethod]
    public void CarriesDriverSpecificCommandMetadata()
    {
        SqlCommandResult result = new(1, "inserted", 42, 3, 2);

        Assert.AreEqual(42UL, result.LastInsertId);
        Assert.AreEqual(3U, result.StatusFlags);
        Assert.AreEqual(2, result.WarningCount);
    }
}

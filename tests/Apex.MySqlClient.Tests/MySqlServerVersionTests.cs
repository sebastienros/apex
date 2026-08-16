namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlServerVersionTests
{
    [TestMethod]
    public void ParsesPlainMySqlVersion()
    {
        var version = MySqlConnection.ParseServerVersion("8.4.2");

        Assert.AreEqual(8, version.Major);
        Assert.AreEqual(4, version.Minor);
        Assert.AreEqual(2, version.Micro);
        Assert.IsFalse(version.IsMariaDb);
        Assert.AreEqual("MySQL", version.ProductName);
        Assert.AreEqual("8.4.2", version.FullVersion);
    }

    [TestMethod]
    public void ParsesMySqlVersionWithSuffix()
    {
        var version = MySqlConnection.ParseServerVersion("8.0.36-log");

        Assert.AreEqual(8, version.Major);
        Assert.AreEqual(0, version.Minor);
        Assert.AreEqual(36, version.Micro);
        Assert.IsFalse(version.IsMariaDb);
    }

    [TestMethod]
    public void StripsMariaDbCompatibilityPrefix()
    {
        // MariaDB reports itself as "5.5.5-<real version>-MariaDB" for backward compatibility with
        // clients that gate features on the reported MySQL version.
        var version = MySqlConnection.ParseServerVersion("5.5.5-11.8.2-MariaDB");

        Assert.AreEqual(11, version.Major);
        Assert.AreEqual(8, version.Minor);
        Assert.AreEqual(2, version.Micro);
        Assert.IsTrue(version.IsMariaDb);
        Assert.AreEqual("MariaDB", version.ProductName);
        Assert.AreEqual("5.5.5-11.8.2-MariaDB", version.FullVersion);
    }

    [TestMethod]
    public void RecognizesMariaDbWithoutCompatibilityPrefix()
    {
        var version = MySqlConnection.ParseServerVersion("10.11.6-MariaDB");

        Assert.AreEqual(10, version.Major);
        Assert.AreEqual(11, version.Minor);
        Assert.AreEqual(6, version.Micro);
        Assert.IsTrue(version.IsMariaDb);
    }

    [TestMethod]
    public void MissingComponentsDefaultToZero()
    {
        var version = MySqlConnection.ParseServerVersion("9");

        Assert.AreEqual(9, version.Major);
        Assert.AreEqual(0, version.Minor);
        Assert.AreEqual(0, version.Micro);
    }

    [TestMethod]
    public void NonNumericComponentsDefaultToZero()
    {
        var version = MySqlConnection.ParseServerVersion("unknown-server");

        Assert.AreEqual(0, version.Major);
        Assert.AreEqual(0, version.Minor);
        Assert.AreEqual(0, version.Micro);
    }
}

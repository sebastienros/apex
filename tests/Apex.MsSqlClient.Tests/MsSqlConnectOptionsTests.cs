namespace Apex.MsSqlClient.Tests;

[TestClass]
public sealed class MsSqlConnectOptionsTests
{
    [TestMethod]
    public void UsesSqlServerDefaults()
    {
        MsSqlConnectOptions options = new();

        Assert.AreEqual("localhost", options.Host);
        Assert.AreEqual(1433, options.Port);
        Assert.AreEqual("sa", options.Username);
        Assert.AreEqual(MsSqlEncryptionMode.Require, options.EncryptionMode);
        Assert.AreEqual(4096, options.PacketSize);
        Assert.AreEqual(1024, options.StringCacheCapacity);
        Assert.AreEqual(128, options.StringCacheMaximumByteLength);
    }

    [TestMethod]
    public void ParsesUri()
    {
        MsSqlConnectOptions options = MsSqlConnectOptions.Parse(
          "sqlserver://app%20user:s%40cret@db.example:1444/app%20db" +
          "?encrypt=strict&applicationName=wire-tests&trustServerCertificate=false");

        Assert.AreEqual("db.example", options.Host);
        Assert.AreEqual(1444, options.Port);
        Assert.AreEqual("app user", options.Username);
        Assert.AreEqual("s@cret", options.Password);
        Assert.AreEqual("app db", options.Database);
        Assert.AreEqual(MsSqlEncryptionMode.Strict, options.EncryptionMode);
        Assert.AreEqual("wire-tests", options.ApplicationName);
        Assert.IsFalse(options.TrustServerCertificate);
    }

    [TestMethod]
    public void ParsesFormEncodedUriQuery()
    {
        MsSqlConnectOptions options = MsSqlConnectOptions.Parse(
          "sqlserver://localhost/app?applicationName=Apex+Driver" +
          "&workstationId=build%2Bagent");

        Assert.AreEqual("Apex Driver", options.ApplicationName);
        Assert.AreEqual("build+agent", options.WorkstationId);
    }

    [TestMethod]
    public void ParsesKeywordConnectionStringAndAliases()
    {
        MsSqlConnectOptions options = MsSqlConnectOptions.Parse(
          "Server=tcp:db.example,1555;Initial Catalog={app;db};" +
          "User ID='app user';Password=\"s;cret\";Encrypt=optional;" +
          "Trust Server Certificate=yes;Packet Size=8192;Connect Timeout=5;" +
          "String Cache Capacity=64;String Cache Maximum Byte Length=256");

        Assert.AreEqual("db.example", options.Host);
        Assert.AreEqual(1555, options.Port);
        Assert.AreEqual("app;db", options.Database);
        Assert.AreEqual("app user", options.Username);
        Assert.AreEqual("s;cret", options.Password);
        Assert.AreEqual(MsSqlEncryptionMode.Optional, options.EncryptionMode);
        Assert.IsTrue(options.TrustServerCertificate);
        Assert.AreEqual(8192, options.PacketSize);
        Assert.AreEqual(TimeSpan.FromSeconds(5), options.ConnectTimeout);
        Assert.AreEqual(64, options.StringCacheCapacity);
        Assert.AreEqual(256, options.StringCacheMaximumByteLength);
    }

    [TestMethod]
    public void RejectsMalformedOrUnsupportedOptions()
    {
        Assert.ThrowsExactly<FormatException>(
          () => MsSqlConnectOptions.Parse("Server"));
        Assert.ThrowsExactly<FormatException>(
          () => MsSqlConnectOptions.Parse("Port=70000"));
        Assert.ThrowsExactly<FormatException>(
          () => MsSqlConnectOptions.Parse("Integrated Security=true"));
        Assert.ThrowsExactly<FormatException>(
          () => MsSqlConnectOptions.Parse("Cache Prepared Statements=true"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
          () => MsSqlConnection.ValidateOptions(new MsSqlConnectOptions { PacketSize = 128 }));
        Assert.ThrowsExactly<ArgumentException>(
          () => MsSqlConnection.ValidateOptions(
            new MsSqlConnectOptions { CachePreparedStatements = true }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
          () => MsSqlConnection.ValidateOptions(
            new MsSqlConnectOptions { StringCacheCapacity = 1_048_577 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
          () => MsSqlConnection.ValidateOptions(
            new MsSqlConnectOptions { StringCacheMaximumByteLength = 4097 }));
    }

    [TestMethod]
    public void BoundsNormalizationStackUsageForLongKeys()
    {
        string longKey = new('x', 100_000);
        Assert.ThrowsExactly<FormatException>(
          () => MsSqlConnectOptions.Parse($"{longKey}=1"));
    }
}

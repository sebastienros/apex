namespace Apex.PgClient.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PgConnectOptionsTests
{
    [TestMethod]
    public void UsesPostgreSqlDefaults()
    {
        PgConnectOptions options = new();

        Assert.AreEqual("localhost", options.Host);
        Assert.AreEqual(5432, options.Port);
        Assert.AreEqual(256, options.PipeliningLimit);
        Assert.AreEqual(PgSslMode.Disable, options.SslMode);
        Assert.AreEqual(PgChannelBinding.Prefer, options.ChannelBinding);
    }

    [TestMethod]
    public void ReadsEnvironment()
    {
        var oldHost = Environment.GetEnvironmentVariable("PGHOST");
        var oldPort = Environment.GetEnvironmentVariable("PGPORT");
        try
        {
            Environment.SetEnvironmentVariable("PGHOST", "database.example");
            Environment.SetEnvironmentVariable("PGPORT", "5544");

            PgConnectOptions options = PgConnectOptions.FromEnvironment();

            Assert.AreEqual("database.example", options.Host);
            Assert.AreEqual(5544, options.Port);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PGHOST", oldHost);
            Environment.SetEnvironmentVariable("PGPORT", oldPort);
        }
    }

    [TestMethod]
    public void ParsesUri()
    {
        PgConnectOptions options = PgConnectOptions.Parse(
          "postgresql://app%20user:s%40cret@db.example:5544/app%20db" +
          "?sslmode=verify-full&channel_binding=require&application_name=tests");

        Assert.AreEqual("db.example", options.Host);
        Assert.AreEqual(5544, options.Port);
        Assert.AreEqual("app user", options.Username);
        Assert.AreEqual("s@cret", options.Password);
        Assert.AreEqual("app db", options.Database);
        Assert.AreEqual(PgSslMode.VerifyFull, options.SslMode);
        Assert.AreEqual(PgChannelBinding.Require, options.ChannelBinding);
        Assert.AreEqual("tests", options.Properties["application_name"]);
    }

    [TestMethod]
    public void ParsesKeywordConnectionString()
    {
        PgConnectOptions options = PgConnectOptions.Parse(
          "host=db.example port=5544 user='app user' password='s\\'ecret' " +
          "dbname=app sslmode=require pipelininglimit=32 useexperimentallowleveltls=true");

        Assert.AreEqual("db.example", options.Host);
        Assert.AreEqual(5544, options.Port);
        Assert.AreEqual("app user", options.Username);
        Assert.AreEqual("s'ecret", options.Password);
        Assert.AreEqual("app", options.Database);
        Assert.AreEqual(PgSslMode.Require, options.SslMode);
        Assert.AreEqual(32, options.PipeliningLimit);
        Assert.IsTrue(options.UseExperimentalLowLevelTls);
    }

    [TestMethod]
    public void ParsesAdoNetSemicolonConnectionString()
    {
        PgConnectOptions options = PgConnectOptions.Parse(
          "Host=db.example;Port=5544;Username=app;Pass" +
          "word=\"secret value;with \"\"quotes\"\"\";Database=app;sslmode=disable");

        Assert.AreEqual("db.example", options.Host);
        Assert.AreEqual(5544, options.Port);
        Assert.AreEqual("app", options.Username);
        Assert.AreEqual("secret value;with \"quotes\"", options.Password);
        Assert.AreEqual("app", options.Database);
        Assert.AreEqual(PgSslMode.Disable, options.SslMode);
    }

    [TestMethod]
    public void PreservesPostgreSqlKeywordEscapingAndAliases()
    {
        PgConnectOptions options = PgConnectOptions.Parse(
          "host=db.example username=app\\ user password='s\\\\ecret' " +
          "database='app db' custom_option=enabled");

        Assert.AreEqual("app user", options.Username);
        Assert.AreEqual("s\\ecret", options.Password);
        Assert.AreEqual("app db", options.Database);
        Assert.AreEqual("enabled", options.Properties["custom_option"]);
    }

    [TestMethod]
    public void RejectsMalformedConnectionString()
    {
        Assert.ThrowsExactly<FormatException>(() => PgConnectOptions.Parse("host"));
        Assert.ThrowsExactly<FormatException>(() => PgConnectOptions.Parse("port=invalid"));
        Assert.ThrowsExactly<FormatException>(() => PgConnectOptions.Parse("host='unterminated"));
        Assert.ThrowsExactly<FormatException>(() => PgConnectOptions.Parse("host=value\\"));
    }
}

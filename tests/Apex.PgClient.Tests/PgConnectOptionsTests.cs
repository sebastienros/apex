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
    public void KeepsLibpqSemanticsForSemicolonsAndDoubleQuotes()
    {
        // ADO.NET-style strings are translated by Apex.PgClient.AdoNet; the native parser keeps
        // libpq semantics, where ';' and '"' are ordinary value characters.
        PgConnectOptions options = PgConnectOptions.Parse(
          "host=db.example user=app password=ab;cd\"ef dbname=app");

        Assert.AreEqual("db.example", options.Host);
        Assert.AreEqual("app", options.Username);
        Assert.AreEqual("ab;cd\"ef", options.Password);
        Assert.AreEqual("app", options.Database);
    }

    [TestMethod]
    public void PreservesPostgreSqlKeywordEscapingAndAliases()
    {
        PgConnectOptions options = PgConnectOptions.Parse(
          "host=db.example username=app\\ user password='s\\\\ecret' " +
          "database='app db' search_path=app");

        Assert.AreEqual("app user", options.Username);
        Assert.AreEqual("s\\ecret", options.Password);
        Assert.AreEqual("app db", options.Database);
        Assert.AreEqual("app", options.Properties["search_path"]);
    }

    [TestMethod]
    [DataRow("host=db pasword=hunter2", "pasword")]
    [DataRow("host=db custom_option=hunter2", "custom_option")]
    [DataRow("postgresql://app@db/app?sslrootcert=hunter2", "sslrootcert")]
    [DataRow("postgresql://app@db/app?Passwd=hunter2", "Passwd")]
    public void RejectsUnknownKeysWithoutRevealingTheirValue(string connectionString, string key)
    {
        var exception = Assert.ThrowsExactly<FormatException>(
          () => PgConnectOptions.Parse(connectionString));

        StringAssert.Contains(exception.Message, $"'{key}'");
        Assert.IsFalse(exception.Message.Contains("hunter2", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("host=db pasword=x", "password")]
    [DataRow("host=db sslmod=require", "sslmode")]
    [DataRow("host=db search-path=x", "search_path")]
    [DataRow("host=db aplication_name=x", "application_name")]
    public void SuggestsTheClosestKnownKey(string connectionString, string suggestion)
    {
        var exception = Assert.ThrowsExactly<FormatException>(
          () => PgConnectOptions.Parse(connectionString));

        StringAssert.Contains(exception.Message, $"Did you mean '{suggestion}'?");
    }

    [TestMethod]
    public void SendsKnownStartupParametersUnderTheirCanonicalNames()
    {
        PgConnectOptions options = PgConnectOptions.Parse(
          "host=db APPLICATION_NAME=billing datestyle='SQL, DMY' timezone=UTC " +
          "Search_Path=app options='-c statement_timeout=5s'");

        Assert.AreEqual("billing", options.Properties["application_name"]);
        Assert.AreEqual("SQL, DMY", options.Properties["DateStyle"]);
        Assert.AreEqual("UTC", options.Properties["TimeZone"]);
        Assert.AreEqual("app", options.Properties["search_path"]);
        Assert.AreEqual("-c statement_timeout=5s", options.Properties["options"]);
        Assert.IsFalse(options.Properties.ContainsKey("datestyle"));
        Assert.IsFalse(options.Properties.ContainsKey("APPLICATION_NAME"));
    }

    [TestMethod]
    public void AcceptsOptionsInUriQuery()
    {
        PgConnectOptions options = PgConnectOptions.Parse(
          "postgresql://app@db/app?options=-c%20search_path%3Dapp");

        Assert.AreEqual("-c search_path=app", options.Properties["options"]);
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

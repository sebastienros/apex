using Apex.PgClient;

namespace Apex.SqlClient.AdoNet.Tests;

[TestClass]
public sealed class PgAdoConnectionStringTests
{
    [TestMethod]
    public void ParsesAdoNetSemicolonConnectionString()
    {
        var options = PgAdoConnectionString.Parse(
            "Host=db.example;Port=5544;Username=app;Pass" +
            "word=\"secret value;with \"\"quotes\"\" and \\ 'single'\";Database=app;sslmode=disable;");

        Assert.AreEqual("db.example", options.Host);
        Assert.AreEqual(5544, options.Port);
        Assert.AreEqual("app", options.Username);
        Assert.AreEqual("secret value;with \"quotes\" and \\ 'single'", options.Password);
        Assert.AreEqual("app", options.Database);
        Assert.AreEqual(PgSslMode.Disable, options.SslMode);
    }

    [TestMethod]
    public void ParsesSingleQuotedAndUnquotedAdoNetValues()
    {
        var options = PgAdoConnectionString.Parse(" Host = db ; Password='it''s' ; Database=app");

        Assert.AreEqual("db", options.Host);
        Assert.AreEqual("it's", options.Password);
        Assert.AreEqual("app", options.Database);
    }

    [TestMethod]
    [DataRow("host=db password=secret dbname=app")]
    [DataRow("postgresql://app:secret@db/app;ignored")]
    public void PassesLibpqKeywordStringsAndUrisToTheNativeParser(string connectionString) =>
        Assert.AreEqual(connectionString, PgAdoConnectionString.ToNative(connectionString));

    [TestMethod]
    public void EmptyConnectionStringUsesDefaults() =>
        Assert.AreEqual(new PgConnectOptions().Host, PgAdoConnectionString.Parse(string.Empty).Host);

    [TestMethod]
    [DataRow("Host=db;Password=\"unterminated")]
    [DataRow("Host=db;=value")]
    [DataRow("Host=db;Database")]
    [DataRow("Host=db;Password=\"a\"b")]
    public void RejectsMalformedAdoNetStrings(string connectionString) =>
        Assert.ThrowsExactly<FormatException>(() => PgAdoConnectionString.Parse(connectionString));

    [TestMethod]
    public void ConnectionKeepsTheOriginalConnectionString()
    {
        const string connectionString = "Host=db.example;Database=app";
        using PgDbConnection connection = new(connectionString);

        Assert.AreEqual(connectionString, connection.ConnectionString);
        Assert.AreEqual("db.example", connection.DataSource);
        Assert.AreEqual("app", connection.Database);
    }

    [TestMethod]
    public void LibpqSingleQuotedSemicolonAndSingleEntryDoubleQuotedAdoStringsParseCleanly()
    {
        using PgDbConnection libpq = new("host=localhost password='a;b' dbname=app");
        Assert.AreEqual("localhost", libpq.DataSource);
        Assert.AreEqual("app", libpq.Database);

        using PgDbConnection singleDoubleQuoted = new("Host=\"my host\"");
        Assert.AreEqual("my host", singleDoubleQuoted.DataSource);
    }
}

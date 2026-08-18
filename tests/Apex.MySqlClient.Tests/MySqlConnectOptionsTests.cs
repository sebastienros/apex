namespace Apex.MySqlClient.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MySqlConnectOptionsTests
{
    [TestMethod]
    public void UsesMySqlDefaults()
    {
        MySqlConnectOptions options = new();

        Assert.AreEqual("localhost", options.Host);
        Assert.AreEqual(3306, options.Port);
        Assert.AreEqual("root", options.Username);
        Assert.AreEqual(string.Empty, options.Password);
        Assert.AreEqual(string.Empty, options.Database);
        Assert.AreEqual(1, options.PipeliningLimit);
        Assert.AreEqual(MySqlSslMode.Preferred, options.SslMode);
        Assert.AreEqual(MySqlAuthenticationPlugin.Default, options.AuthenticationPlugin);
        Assert.AreEqual(MySqlZeroDateBehavior.Error, options.ZeroDateBehavior);
        Assert.AreEqual(MySqlQueryCancellation.KillQuery, options.QueryCancellation);
        Assert.IsFalse(options.AllowPublicKeyRetrieval);
        Assert.IsFalse(options.AllowCleartextPassword);
        Assert.IsFalse(options.UseAffectedRows);
        Assert.IsFalse(options.AllowMultiStatements);
    }

    [TestMethod]
    public void ReadsEnvironment()
    {
        var oldHost = Environment.GetEnvironmentVariable("MYSQL_HOST");
        var oldPort = Environment.GetEnvironmentVariable("MYSQL_TCP_PORT");
        var oldDatabase = Environment.GetEnvironmentVariable("MYSQL_DATABASE");
        var oldUser = Environment.GetEnvironmentVariable("MYSQL_USER");
        var oldPassword = Environment.GetEnvironmentVariable("MYSQL_PWD");
        try
        {
            Environment.SetEnvironmentVariable("MYSQL_HOST", "database.example");
            Environment.SetEnvironmentVariable("MYSQL_TCP_PORT", "3316");
            Environment.SetEnvironmentVariable("MYSQL_DATABASE", "appdb");
            Environment.SetEnvironmentVariable("MYSQL_USER", "svc");
            Environment.SetEnvironmentVariable("MYSQL_PWD", "s3cret");

            MySqlConnectOptions options = MySqlConnectOptions.FromEnvironment();

            Assert.AreEqual("database.example", options.Host);
            Assert.AreEqual(3316, options.Port);
            Assert.AreEqual("appdb", options.Database);
            Assert.AreEqual("svc", options.Username);
            Assert.AreEqual("s3cret", options.Password);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MYSQL_HOST", oldHost);
            Environment.SetEnvironmentVariable("MYSQL_TCP_PORT", oldPort);
            Environment.SetEnvironmentVariable("MYSQL_DATABASE", oldDatabase);
            Environment.SetEnvironmentVariable("MYSQL_USER", oldUser);
            Environment.SetEnvironmentVariable("MYSQL_PWD", oldPassword);
        }
    }

    [TestMethod]
    public void ParsesMySqlUri()
    {
        var scheme = "mysql://";
        var userInfo = "app%20user" + ":" + "s%40cret";
        var uri = scheme + userInfo + "@" + "db.example:3316/app%20db" +
          "?sslmode=verifyca&authenticationplugin=cachingsha2password&pipelininglimit=16";

        MySqlConnectOptions options = MySqlConnectOptions.Parse(uri);

        Assert.AreEqual("db.example", options.Host);
        Assert.AreEqual(3316, options.Port);
        Assert.AreEqual("app user", options.Username);
        Assert.AreEqual("s@cret", options.Password);
        Assert.AreEqual("app db", options.Database);
        Assert.AreEqual(MySqlSslMode.VerifyCa, options.SslMode);
        Assert.AreEqual(MySqlAuthenticationPlugin.CachingSha2Password, options.AuthenticationPlugin);
        Assert.AreEqual(16, options.PipeliningLimit);
    }

    [TestMethod]
    public void ParsesMariaDbUriScheme()
    {
        MySqlConnectOptions options = MySqlConnectOptions.Parse("mariadb://root@db.example/test");

        Assert.AreEqual("db.example", options.Host);
        Assert.AreEqual(3306, options.Port);
        Assert.AreEqual("root", options.Username);
        Assert.AreEqual("test", options.Database);
    }

    [TestMethod]
    public void UriQueryUsesSharedDecodingWhileRetainingMySqlAliases()
    {
        MySqlConnectOptions options = MySqlConnectOptions.Parse(
          "mysql://root@db/app?usefoundrows=true&application+name=Apex%20MySQL");

        Assert.IsFalse(options.UseAffectedRows);
        Assert.AreEqual("Apex MySQL", options.ConnectionAttributes["application name"]);
    }

    [TestMethod]
    public void ParsesKeywordConnectionString()
    {
        // MySqlConnectionStringParser uses SQL-style quote doubling to escape an embedded quote,
        // not backslash escaping: 'app user' and 's''ecret' are the correctly quoted forms.
        MySqlConnectOptions options = MySqlConnectOptions.Parse(
          "Server=db.example;Port=3316;User ID='app user';Password='s''ecret';" +
          "Database=app;SslMode=Required;AllowPublicKeyRetrieval=false;" +
          "UseExperimentalLowLevelTls=true");

        Assert.AreEqual("db.example", options.Host);
        Assert.AreEqual(3316, options.Port);
        Assert.AreEqual("app user", options.Username);
        Assert.AreEqual("s'ecret", options.Password);
        Assert.AreEqual("app", options.Database);
        Assert.AreEqual(MySqlSslMode.Required, options.SslMode);
        Assert.IsFalse(options.AllowPublicKeyRetrieval);
        Assert.IsTrue(options.UseExperimentalLowLevelTls);
    }

    [TestMethod]
    public void ParsesUseFoundRowsAsInverseOfUseAffectedRows()
    {
        MySqlConnectOptions options = MySqlConnectOptions.Parse("Server=db;UseFoundRows=true");

        Assert.IsFalse(options.UseAffectedRows);

        MySqlConnectOptions inverted = MySqlConnectOptions.Parse("Server=db;UseAffectedRows=true");

        Assert.IsTrue(inverted.UseAffectedRows);
    }

    [TestMethod]
    public void UnknownKeysBecomeConnectionAttributes()
    {
        MySqlConnectOptions options = MySqlConnectOptions.Parse(
          "Server=db;sql_mode=STRICT_ALL_TABLES;time_zone=+00:00");

        Assert.AreEqual("STRICT_ALL_TABLES", options.ConnectionAttributes["sql_mode"]);
        Assert.AreEqual("+00:00", options.ConnectionAttributes["time_zone"]);
    }

    [TestMethod]
    public void RejectsMalformedConnectionString()
    {
        Assert.ThrowsExactly<FormatException>(() => MySqlConnectOptions.Parse("port=invalid"));
        Assert.ThrowsExactly<FormatException>(() => MySqlConnectOptions.Parse("pipelininglimit=0"));

        // Unknown enum-backed values fall back to the shared enum parser, which reports an
        // ArgumentException rather than a FormatException.
        Assert.ThrowsExactly<ArgumentException>(
          () => MySqlConnectOptions.Parse("sslmode=not-a-mode"));
    }

    [TestMethod]
    public void RejectsInvalidUriScheme()
    {
        Assert.ThrowsExactly<FormatException>(
          () => MySqlConnectOptions.Parse("postgres://user@host/db"));
    }

    [TestMethod]
    public void RejectsClearPasswordWithoutOptIn()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
          MySqlConnection.ConnectAsync(
            new MySqlConnectOptions
            {
                AuthenticationPlugin = MySqlAuthenticationPlugin.ClearPassword,
                AllowCleartextPassword = false,
            },
            CancellationToken.None).AsTask().GetAwaiter().GetResult());

        StringAssert.Contains(exception.Message, "AllowCleartextPassword");
    }

    [TestMethod]
    public void RejectsCleartextPasswordWithoutTls()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
          MySqlConnection.ConnectAsync(
            new MySqlConnectOptions { AllowCleartextPassword = true, SslMode = MySqlSslMode.Disabled },
            CancellationToken.None).AsTask().GetAwaiter().GetResult());

        StringAssert.Contains(exception.Message, "TLS");
    }

    [TestMethod]
    public void RejectsInvalidPipeliningLimit()
    {
        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
          MySqlConnection.ConnectAsync(
            new MySqlConnectOptions { PipeliningLimit = 0 },
            CancellationToken.None).AsTask().GetAwaiter().GetResult());

        Assert.AreEqual("options.PipeliningLimit", exception.ParamName);
    }

    [TestMethod]
    public void RejectsZeroCollation()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
          MySqlConnection.ConnectAsync(
            new MySqlConnectOptions { Collation = 0 },
            CancellationToken.None).AsTask().GetAwaiter().GetResult());

        StringAssert.Contains(exception.Message, "collation");
    }

    [TestMethod]
    public void RejectsOversizedPort()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
          MySqlConnection.ConnectAsync(
            new MySqlConnectOptions { Port = 70000 },
            CancellationToken.None).AsTask().GetAwaiter().GetResult());
    }
}

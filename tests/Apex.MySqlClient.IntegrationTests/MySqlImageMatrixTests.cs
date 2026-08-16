using Apex.SqlClient;
using Testcontainers.MySql;

namespace Apex.MySqlClient.IntegrationTests;

/// <summary>
/// Proves the reusable <see cref="MySqlContainerFixture"/> works against every server in the
/// supported matrix. Each data row starts and tears down its own container (unavoidable here
/// because each row targets a different image), but the assertions are intentionally a small
/// smoke test rather than the full behavior suite, which lives in
/// <see cref="MySqlConnectionIntegrationTests"/> and runs once against the default image.
/// </summary>
[TestClass]
public sealed class MySqlImageMatrixTests
{
    [TestMethod]
    [DataRow("mysql:8.4", false)]
    [DataRow("mysql:9.6", false)]
    [DataRow("mariadb:11.8", true)]
    public async Task ConnectsQueriesAndReportsExpectedProductForEachSupportedImage(
        string image,
        bool isMariaDb)
    {
        await using var container = await MySqlContainerFixture.StartAsync(image);
        var options = MySqlContainerFixture.CreateOptions(container);

        await using var connection = await MySqlClient.ConnectAsync(options);
        var rows = await connection.QueryAsync("SELECT 1 AS id, 'hello' AS message");

        Assert.AreEqual(1, rows[0].Get<int>("id"));
        Assert.AreEqual("hello", rows[0].Get<string>("message"));
        Assert.AreEqual(isMariaDb, connection.ServerVersion.IsMariaDb);
        Assert.AreEqual(isMariaDb ? "MariaDB" : "MySQL", connection.DatabaseMetadata.ProductName);

        await using var statement = await connection.PrepareAsync("SELECT ? + 1 AS n");
        var prepared = await statement.QueryAsync(SqlParameters.Create(41));
        Assert.AreEqual(42, prepared[0].Get<int>("n"));
    }
}

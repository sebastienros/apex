using Apex.SqlClient;
using Apex.SqlClient.SpecificationTests;
using Testcontainers.MySql;

namespace Apex.MySqlClient.IntegrationTests;

[TestClass]
public sealed class MySqlSqlClientSpecificationTests : SqlClientSpecificationTests
{
    private static MySqlContainer s_container = null!;

    [ClassInitialize]
    public static async Task StartMySqlAsync(TestContext testContext) =>
      s_container = await MySqlContainerFixture.StartAsync();

    [ClassCleanup]
    public static async Task StopMySqlAsync() => await s_container.DisposeAsync();

    private static MySqlConnectOptions Options => MySqlContainerFixture.CreateOptions(s_container);

    protected override string ParameterizedScalarSql => "SELECT CAST(? AS SIGNED)";

    protected override string CreateTemporaryTableSql =>
      "CREATE TEMPORARY TABLE specification_values (value INT)";

    protected override string InsertTemporaryValueSql =>
      "INSERT INTO specification_values VALUES (?)";

    protected override string CountTemporaryValuesSql =>
      "SELECT COUNT(*) FROM specification_values";

    protected override string SequenceSql =>
      "WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 10) " +
      "SELECT n FROM seq ORDER BY n";

    protected override string LongRunningSql => "SELECT SLEEP(10)";

    protected override string DiagnosticSystemName => "mysql";

    protected override bool CoercesInvalidIntegerParameters => true;

    protected override string ServerHost => Options.Host;

    protected override int ServerPort => Options.Port;

    protected override string CountRowsSql(string tableName) =>
      $"SELECT CAST(COUNT(*) AS SIGNED) FROM {tableName}";

    protected override async ValueTask<ISqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default) =>
      await MySqlClient.ConnectAsync(Options, cancellationToken);

    protected override async ValueTask<ISqlConnection> OpenConnectionAsync(
        string host,
        int port,
        int reconnectAttempts,
        TimeSpan reconnectInterval,
        CancellationToken cancellationToken = default) =>
      await MySqlClient.ConnectAsync(
        Options with
        {
            Host = host,
            Port = port,
            ReconnectAttempts = reconnectAttempts,
            ReconnectInterval = reconnectInterval,
        },
        cancellationToken);

    protected override ISqlPool CreatePool(int maximumSize = 4) =>
      MySqlPool.Create(Options, new SqlPoolOptions { MaximumSize = maximumSize });

    protected override ISqlPool CreatePool(
        string host,
        int port,
        int reconnectAttempts,
        TimeSpan reconnectInterval,
        int maximumSize = 4) =>
      MySqlPool.Create(
        Options with
        {
            Host = host,
            Port = port,
            ReconnectAttempts = reconnectAttempts,
            ReconnectInterval = reconnectInterval,
        },
        new SqlPoolOptions { MaximumSize = maximumSize });
}

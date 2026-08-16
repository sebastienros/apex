using Apex.SqlClient;
using Apex.SqlClient.SpecificationTests;

namespace Apex.MsSqlClient.IntegrationTests;

[TestClass]
public sealed class MsSqlSqlClientSpecificationTests : SqlClientSpecificationTests
{
    protected override string ParameterizedScalarSql =>
      "SELECT CAST(@P1 AS int)";

    protected override string CreateTemporaryTableSql =>
      "CREATE TABLE #specification_values (value int NOT NULL)";

    protected override string InsertTemporaryValueSql =>
      "INSERT INTO #specification_values VALUES (@P1)";

    protected override string CountTemporaryValuesSql =>
      "SELECT CAST(COUNT(*) AS bigint) FROM #specification_values";

    protected override string SequenceSql =>
      """
    WITH sequence(value) AS
    (
      SELECT CAST(1 AS int)
      UNION ALL
      SELECT value + 1 FROM sequence WHERE value < 10
    )
    SELECT value FROM sequence OPTION (MAXRECURSION 10)
    """;

    protected override string LongRunningSql =>
      "WAITFOR DELAY '00:00:10'; SELECT CAST(1 AS int)";

    protected override string DiagnosticSystemName => "sqlserver";

    protected override string ServerHost => MsSqlTestEnvironment.Options.Host;

    protected override int ServerPort => MsSqlTestEnvironment.Options.Port;

    protected override string CountUncommittedRowsSql(string tableName) =>
      $"SELECT CAST(COUNT(*) AS BIGINT) FROM {tableName} WITH (READPAST)";

    protected override async ValueTask<ISqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default) =>
      await MsSqlClient.ConnectAsync(MsSqlTestEnvironment.Options, cancellationToken);

    protected override async ValueTask<ISqlConnection> OpenConnectionAsync(
        string host,
        int port,
        int reconnectAttempts,
        TimeSpan reconnectInterval,
        CancellationToken cancellationToken = default) =>
      await MsSqlClient.ConnectAsync(
        MsSqlTestEnvironment.Options with
        {
            Host = host,
            Port = port,
            ReconnectAttempts = reconnectAttempts,
            ReconnectInterval = reconnectInterval,
        },
        cancellationToken);

    protected override ISqlPool CreatePool(int maximumSize = 4) =>
      MsSqlPool.Create(
        MsSqlTestEnvironment.Options,
        new SqlPoolOptions { MaximumSize = maximumSize });

    protected override ISqlPool CreatePool(
        string host,
        int port,
        int reconnectAttempts,
        TimeSpan reconnectInterval,
        int maximumSize = 4) =>
      MsSqlPool.Create(
        MsSqlTestEnvironment.Options with
        {
            Host = host,
            Port = port,
            ReconnectAttempts = reconnectAttempts,
            ReconnectInterval = reconnectInterval,
        },
        new SqlPoolOptions { MaximumSize = maximumSize });
}

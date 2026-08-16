using Apex.SqlClient;
using Apex.SqlClient.SpecificationTests;
using Testcontainers.PostgreSql;

namespace Apex.PgClient.IntegrationTests;

[TestClass]
public sealed class PgSqlClientSpecificationTests : SqlClientSpecificationTests
{
    private PostgreSqlContainer? _container;

    private PgConnectOptions Options
    {
        get
        {
            var container = _container ??
              throw new InvalidOperationException("The PostgreSQL container is not running.");
            return new PgConnectOptions
            {
                Host = container.Hostname,
                Port = container.GetMappedPublicPort(5432),
                Database = "db",
                Username = "user",
                Password = "pass",
                PipeliningLimit = 8,
            };
        }
    }

    protected override string ParameterizedScalarSql => "SELECT $1::int4";

    protected override string CreateTemporaryTableSql =>
      "CREATE TEMP TABLE specification_values (value int4)";

    protected override string InsertTemporaryValueSql =>
      "INSERT INTO specification_values VALUES ($1::int4)";

    protected override string CountTemporaryValuesSql =>
      "SELECT COUNT(*)::int8 FROM specification_values";

    protected override string SequenceSql =>
      "SELECT generate_series(1, 10)::int4";

    protected override string LongRunningSql => "SELECT pg_sleep(10)";

    protected override string DiagnosticSystemName => "postgresql";

    protected override string ServerHost => Options.Host;

    protected override int ServerPort => Options.Port;

    [TestInitialize]
    public async Task StartPostgreSqlAsync()
    {
        var image = Environment.GetEnvironmentVariable("POSTGRES_IMAGE") ?? "postgres:16-alpine";
        _container = new PostgreSqlBuilder(image)
          .WithDatabase("db")
          .WithUsername("user")
          .WithPassword("pass")
          .Build();
        await _container.StartAsync();
    }

    [TestCleanup]
    public async Task StopPostgreSqlAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    protected override async ValueTask<ISqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default) =>
      await PgClient.ConnectAsync(Options, cancellationToken);

    protected override async ValueTask<ISqlConnection> OpenConnectionAsync(
        string host,
        int port,
        int reconnectAttempts,
        TimeSpan reconnectInterval,
        CancellationToken cancellationToken = default) =>
      await PgClient.ConnectAsync(
        Options with
        {
            Host = host,
            Port = port,
            ReconnectAttempts = reconnectAttempts,
            ReconnectInterval = reconnectInterval,
        },
        cancellationToken);

    protected override ISqlPool CreatePool(int maximumSize = 4) =>
      PgPool.Create(Options, new SqlPoolOptions { MaximumSize = maximumSize });

    protected override ISqlPool CreatePool(
        string host,
        int port,
        int reconnectAttempts,
        TimeSpan reconnectInterval,
        int maximumSize = 4) =>
      PgPool.Create(
        Options with
        {
            Host = host,
            Port = port,
            ReconnectAttempts = reconnectAttempts,
            ReconnectInterval = reconnectInterval,
        },
        new SqlPoolOptions { MaximumSize = maximumSize });
}

using Apex.SqlClient;
using BenchmarkDotNet.Attributes;
using MySqlConnector;
using ApexMySql = Apex.MySqlClient;

namespace Apex.DriverBenchmarks;

/// <summary>
/// Compares Apex.MySqlClient's prepared-statement pipelining against MySqlConnector's
/// <see cref="MySqlBatch"/> at representative in-flight depths.
/// </summary>
/// <remarks>
/// MySqlConnector does not permit concurrent commands on one connection, so its closest
/// supported equivalent to pipelining is one reusable <see cref="MySqlBatch"/> containing the
/// same number of prepared <c>SELECT</c> commands, matching the PostgreSQL/Npgsql comparison.
/// </remarks>
[MemoryDiagnoser]
public class MySqlPipeliningBenchmarks
{
    private ApexMySql.MySqlConnection _apex = null!;
    private ISqlPreparedStatement _apexStatement = null!;
    private Task<SqlRowSet>[] _apexPending = null!;
    private MySqlConnection _connector = null!;
    private MySqlBatch _connectorBatch = null!;

    [Params(1, 16, 64, 256)]
    public int Depth { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var connectionString =
          Environment.GetEnvironmentVariable("APEX_MYSQL_CONNECTION_STRING") ??
          throw new InvalidOperationException(
            "Set APEX_MYSQL_CONNECTION_STRING before running database benchmarks.");
        MySqlConnectionStringBuilder builder = new(connectionString);
        var username = string.IsNullOrEmpty(builder.UserID)
          ? throw new InvalidOperationException("The benchmark connection string requires UserID.")
          : builder.UserID;
        _apex = await ApexMySql.MySqlClient.ConnectAsync(new ApexMySql.MySqlConnectOptions
        {
            Host = string.IsNullOrEmpty(builder.Server)
            ? throw new InvalidOperationException("The benchmark connection string requires Server.")
            : builder.Server,
            Port = (int)builder.Port,
            Database = builder.Database,
            Username = username,
            Password = builder.Password,
            PipeliningLimit = 256,
        });
        _apexStatement = await _apex.PrepareAsync("SELECT CAST(1 AS SIGNED)");
        _apexPending = new Task<SqlRowSet>[Depth];

        _connector = new MySqlConnection(builder.ConnectionString);
        await _connector.OpenAsync();
        _connectorBatch = new MySqlBatch(_connector);
        for (var i = 0; i < Depth; i++)
        {
            _connectorBatch.BatchCommands.Add(
              new MySqlBatchCommand("SELECT CAST(1 AS SIGNED)"));
        }

        await _connectorBatch.PrepareAsync();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _apexStatement.DisposeAsync();
        await _apex.DisposeAsync();
        await _connectorBatch.DisposeAsync();
        await _connector.DisposeAsync();
    }

    [Benchmark]
    public async Task<long> ApexPipelineAsync()
    {
        for (var i = 0; i < _apexPending.Length; i++)
        {
            _apexPending[i] = _apexStatement.QueryAsync().AsTask();
        }

        var results = await Task.WhenAll(_apexPending);
        long sum = 0;
        foreach (var rows in results)
        {
            sum += rows[0].Get<long>(0);
        }

        return sum == Depth ? sum : throw new InvalidOperationException("Unexpected Apex pipeline result.");
    }

    [Benchmark]
    public async Task<long> MySqlConnectorBatchAsync()
    {
        await using var reader =
          await _connectorBatch.ExecuteReaderAsync();
        long sum = 0;
        var count = 0;
        do
        {
            if (!await reader.ReadAsync())
            {
                throw new InvalidOperationException("MySqlConnector batch result is empty.");
            }

            sum += reader.GetInt64(0);
            count++;
        }
        while (await reader.NextResultAsync());

        if (count != Depth)
        {
            throw new InvalidOperationException($"Expected {Depth} MySqlConnector results but received {count}.");
        }

        return sum == Depth ? sum : throw new InvalidOperationException("Unexpected MySqlConnector batch result.");
    }
}

using Apex.PgClient;
using Apex.SqlClient;
using BenchmarkDotNet.Attributes;
using Npgsql;

namespace Apex.DriverBenchmarks;

/// <summary>
/// Measures the shared core paths that every driver goes through: the pool's one-shot
/// query path and buffered row materialization. PostgreSQL is used as the carrier.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 15)]
public class CorePoolAndRowBenchmarks
{
    private PgPool _pool = null!;
    private PgConnection _connection = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var options = CoreBenchmarkConnection.GetOptions();
        _pool = PgPool.Create(options, new SqlPoolOptions { MaximumSize = 1 });
        _connection = await Apex.PgClient.PgClient.ConnectAsync(options);

        // Open the pooled connection before measuring.
        _ = await _pool.QueryAsync("SELECT 1");
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _connection.DisposeAsync();
        await _pool.DisposeAsync();
    }

    [Benchmark]
    public async Task<int> PoolQueryOneRow()
    {
        var rows = await _pool.QueryAsync("SELECT 1");
        return rows[0].GetInt32(0);
    }

    [Benchmark]
    public async Task<int> PoolQueryWithParameter()
    {
        var rows = await _pool.QueryAsync("SELECT $1::int4", SqlParameters.Create(42));
        return rows[0].GetInt32(0);
    }

    [Benchmark]
    public async Task<long> PoolExecute()
    {
        var result = await _pool.ExecuteAsync("SELECT 1");
        return result.AffectedRows;
    }

    [Benchmark]
    public async Task<int> QueryOneRow()
    {
        var rows = await _connection.QueryAsync("SELECT 1");
        return rows[0].GetInt32(0);
    }

    [Benchmark]
    public async Task<int> Query100Rows()
    {
        var rows = await _connection.QueryAsync("SELECT generate_series(1, 100)::int4");
        return rows.Count;
    }

    [Benchmark]
    public async Task<int> Query1000Rows()
    {
        var rows = await _connection.QueryAsync("SELECT generate_series(1, 1000)::int4");
        return rows.Count;
    }
}

/// <summary>Measures in-memory core value types without a database.</summary>
[MemoryDiagnoser]
public class CoreValueBenchmarks
{
    private readonly byte[] _bytes = new byte[32];
    private SqlRowSet _rows = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        await using var connection =
          await Apex.PgClient.PgClient.ConnectAsync(CoreBenchmarkConnection.GetOptions());
        _rows = await connection.QueryAsync("SELECT generate_series(1, 100)::int4");
    }

    [Benchmark]
    public int CreateThreeParameters() =>
      SqlParameters.Create(42, "apex", 7L).Count;

    [Benchmark]
    public int ReadOnlyMemoryValue()
    {
        SqlValue value = new ReadOnlyMemory<byte>(_bytes, 4, 16);
        return value.GetRequired<ReadOnlyMemory<byte>>().Length;
    }

    [Benchmark]
    public int EnumerateRowSet()
    {
        var count = 0;
        foreach (var row in _rows)
        {
            count += row.GetInt32(0);
        }

        return count;
    }
}

internal static class CoreBenchmarkConnection
{
    internal static PgConnectOptions GetOptions()
    {
        var connectionString =
          Environment.GetEnvironmentVariable("APEX_PG_CONNECTION_STRING") ??
          throw new InvalidOperationException(
            "Set APEX_PG_CONNECTION_STRING before running database benchmarks.");
        NpgsqlConnectionStringBuilder builder = new(connectionString);
        var username = builder.Username ??
          throw new InvalidOperationException("The benchmark connection string requires Username.");
        return new PgConnectOptions
        {
            Host = builder.Host ??
              throw new InvalidOperationException("The benchmark connection string requires Host."),
            Port = builder.Port,
            Database = builder.Database ?? username,
            Username = username,
            Password = builder.Password ?? string.Empty,
            SslMode = PgSslMode.Disable,
        };
    }
}

using System.Globalization;
using Apex.SqlClient;
using BenchmarkDotNet.Attributes;
using MySqlConnector;
using ApexMySql = Apex.MySqlClient;

namespace Apex.DriverBenchmarks;

/// <summary>
/// Compares Apex.MySqlClient against MySqlConnector for simple query, prepared query,
/// safe streaming, borrowed-reader, and repeated-string workloads.
/// </summary>
/// <remarks>
/// The 100-row workloads use a recursive common table expression because MySQL and MariaDB have
/// no built-in row-generating function equivalent to PostgreSQL's <c>generate_series</c>. This
/// requires MySQL 8.0+ or MariaDB 10.2+.
/// </remarks>
[MemoryDiagnoser]
public class MySqlBenchmarks
{
    private const string SequenceSql =
      "WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 100) " +
      "SELECT CAST(n AS SIGNED) FROM seq";
    private const string StringSequenceSql =
      "WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 100) " +
      "SELECT 'repeated-value' FROM seq";
    private const long ExpectedSum = 100L * 101L / 2L;

    private ApexMySql.MySqlConnection _apex = null!;
    private ISqlPreparedStatement _apexPrepared = null!;
    private MySqlConnection _connector = null!;
    private MySqlCommand _connectorPrepared = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("APEX_MYSQL_CONNECTION_STRING") ??
            throw new InvalidOperationException(
                "Set APEX_MYSQL_CONNECTION_STRING before running database benchmarks.");
        MySqlConnectionStringBuilder builder = new(connectionString);
        _connector = new MySqlConnection(builder.ConnectionString);
        await _connector.OpenAsync();
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
        });
        _apexPrepared = await _apex.PrepareAsync("SELECT CAST(? AS SIGNED)");
        _connectorPrepared = new MySqlCommand("SELECT CAST(@value AS SIGNED)", _connector);
        _connectorPrepared.Parameters.Add(new MySqlParameter("@value", 42));
        await _connectorPrepared.PrepareAsync();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _apexPrepared.DisposeAsync();
        await _connectorPrepared.DisposeAsync();
        await _apex.DisposeAsync();
        await _connector.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public async Task<long> MySqlConnectorSimpleQueryAsync()
    {
        await using MySqlCommand command = new("SELECT CAST(1 AS SIGNED)", _connector);
        var value = Convert.ToInt64(
          await command.ExecuteScalarAsync(),
          CultureInfo.InvariantCulture);
        return value == 1L ? value : throw new InvalidOperationException("Unexpected simple query result.");
    }

    [Benchmark]
    public async Task<long> ApexSimpleQueryAsync()
    {
        var rows = await _apex.QueryAsync("SELECT CAST(1 AS SIGNED)");
        var value = rows[0].Get<long>(0);
        return value == 1L ? value : throw new InvalidOperationException("Unexpected simple query result.");
    }

    [Benchmark]
    public async Task<long> MySqlConnectorPreparedQueryAsync()
    {
        var value = Convert.ToInt64(
          await _connectorPrepared.ExecuteScalarAsync(),
          CultureInfo.InvariantCulture);
        return value == 42L ? value : throw new InvalidOperationException("Unexpected prepared query result.");
    }

    [Benchmark]
    public async Task<long> ApexPreparedQueryAsync()
    {
        var rows = await _apexPrepared.QueryAsync(SqlParameters.Create(42));
        var value = rows[0].Get<long>(0);
        return value == 42L ? value : throw new InvalidOperationException("Unexpected prepared query result.");
    }

    [Benchmark]
    public async Task<long> MySqlConnectorStream100RowsAsync()
    {
        await using MySqlCommand command = new(SequenceSql, _connector);
        await using var reader = await command.ExecuteReaderAsync();
        long sum = 0;
        while (await reader.ReadAsync())
        {
            sum += reader.GetInt64(0);
        }

        return sum == ExpectedSum ? sum : throw new InvalidOperationException("Unexpected stream sum.");
    }

    [Benchmark]
    public async Task<long> ApexStream100RowsAsync()
    {
        long sum = 0;
        await foreach (var row in _apex.StreamAsync(SequenceSql, fetchSize: 16))
        {
            sum += row.GetInt64(0);
        }

        return sum == ExpectedSum ? sum : throw new InvalidOperationException("Unexpected stream sum.");
    }

    [Benchmark]
    public async Task<long> ApexBorrowedReader100RowsAsync()
    {
        long sum = 0;
        await using var reader = await _apex.ExecuteReaderAsync(SequenceSql);
        while (await reader.ReadAsync())
        {
            sum += reader.GetInt64(0);
        }

        return sum == ExpectedSum ? sum : throw new InvalidOperationException("Unexpected reader sum.");
    }

    [Benchmark]
    public async Task<int> MySqlConnectorString100RowsAsync()
    {
        await using MySqlCommand command = new(StringSequenceSql, _connector);
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            if (reader.GetString(0) != "repeated-value")
            {
                throw new InvalidOperationException("Unexpected string value.");
            }

            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> ApexString100RowsAsync()
    {
        var count = 0;
        await foreach (var row in _apex.StreamAsync(StringSequenceSql, fetchSize: 16))
        {
            if (row.GetString(0) != "repeated-value")
            {
                throw new InvalidOperationException("Unexpected string value.");
            }

            count++;
        }

        return count;
    }
}

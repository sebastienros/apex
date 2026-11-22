using System.Globalization;
using Apex.PgClient;
using Apex.SqlClient;
using BenchmarkDotNet.Attributes;
using Npgsql;

namespace Apex.DriverBenchmarks;

[MemoryDiagnoser]
public class PostgreSqlBenchmarks
{
    private const string RowsSql = "SELECT generate_series(1, 100)::int4";
    private PgConnection _apex = null!;
    private PgDbConnection _ado = null!;
    private System.Data.Common.DbCommand _adoRowsPrepared = null!;
    private ISqlPreparedStatement _apexPrepared = null!;
    private ISqlPreparedStatement _apexRowsPrepared = null!;
    private NpgsqlConnection _npgsql = null!;
    private NpgsqlCommand _npgsqlPrepared = null!;
    private NpgsqlCommand _npgsqlRowsPrepared = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        string connectionString =
            Environment.GetEnvironmentVariable("APEX_PG_CONNECTION_STRING") ??
            throw new InvalidOperationException(
                "Set APEX_PG_CONNECTION_STRING before running database benchmarks.");
        NpgsqlConnectionStringBuilder builder = new(connectionString);
        _npgsql = new NpgsqlConnection(builder.ConnectionString);
        await _npgsql.OpenAsync();
        string username = builder.Username ??
            throw new InvalidOperationException("The benchmark connection string requires Username.");
        _apex = await Apex.PgClient.PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = builder.Host ??
                throw new InvalidOperationException("The benchmark connection string requires Host."),
            Port = builder.Port,
            Database = builder.Database ?? username,
            Username = username,
            Password = builder.Password ?? string.Empty,
            SslMode = builder.SslMode == SslMode.Disable ? PgSslMode.Disable : PgSslMode.Prefer,
        });
        _apexPrepared = await _apex.PrepareAsync("SELECT $1::int4");
        _apexRowsPrepared = await _apex.PrepareAsync(RowsSql);
        _ado = new PgDbConnection(connectionString);
        await _ado.OpenAsync();
        _adoRowsPrepared = _ado.CreateCommand();
        _adoRowsPrepared.CommandText = RowsSql;
        await _adoRowsPrepared.PrepareAsync();
        _npgsqlPrepared = new NpgsqlCommand("SELECT $1::int4", _npgsql);
        _npgsqlPrepared.Parameters.Add(new NpgsqlParameter<int> { TypedValue = 42 });
        await _npgsqlPrepared.PrepareAsync();
        _npgsqlRowsPrepared = new NpgsqlCommand(RowsSql, _npgsql);
        await _npgsqlRowsPrepared.PrepareAsync();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _apexRowsPrepared.DisposeAsync();
        await _apexPrepared.DisposeAsync();
        await _npgsqlRowsPrepared.DisposeAsync();
        await _npgsqlPrepared.DisposeAsync();
        await _apex.DisposeAsync();
        await _adoRowsPrepared.DisposeAsync();
        await _ado.DisposeAsync();
        await _npgsql.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> NpgsqlSimpleQueryAsync()
    {
        await using NpgsqlCommand command = new("SELECT 1", _npgsql);
        return Convert.ToInt32(
          await command.ExecuteScalarAsync(),
          CultureInfo.InvariantCulture);
    }

    [Benchmark]
    public async Task<int> ApexSimpleQueryAsync()
    {
        SqlRowSet rows = await _apex.QueryAsync("SELECT 1");
        return rows[0].Get<int>(0);
    }

    [Benchmark]
    public async Task<int> NpgsqlPreparedQueryAsync() =>
      Convert.ToInt32(
        await _npgsqlPrepared.ExecuteScalarAsync(),
        CultureInfo.InvariantCulture);

    [Benchmark]
    public async Task<int> ApexPreparedQueryAsync()
    {
        SqlRowSet rows = await _apexPrepared.QueryAsync(SqlParameters.Create(42));
        return rows[0].Get<int>(0);
    }

    [Benchmark]
    public async Task<int> ApexTypedQueryAsync()
    {
        SqlRowSet rows = await _apex.QueryTypedAsync(
            "SELECT $1",
            PgParameters.Create(PgParameter.Create(PgType.Integer, 42)));
        return rows[0].Get<int>(0);
    }

    [Benchmark]
    public async Task<int> ApexTwoCommandBatchAsync()
    {
        PgBatch batch = new();
        batch.Add(
            "SELECT $1",
            PgParameters.Create(PgParameter.Create(PgType.Integer, 20)));
        batch.Add(
            "SELECT $1",
            PgParameters.Create(PgParameter.Create(PgType.Integer, 22)));
        PgBatchReader results = await _apex.ExecuteBatchAsync(batch);
        var sum = results.Current[0].Get<int>(0);
        await results.NextResultAsync();
        return sum + results.Current[0].Get<int>(0);
    }

    [Benchmark]
    public async Task<int> NpgsqlStream100RowsAsync()
    {
        await using NpgsqlDataReader reader = await _npgsqlRowsPrepared.ExecuteReaderAsync();
        int sum = 0;
        while (await reader.ReadAsync())
        {
            sum += reader.GetInt32(0);
        }

        return sum;
    }

    [Benchmark]
    public async Task<int> ApexStream100RowsAsync()
    {
        int sum = 0;
        await foreach (SqlRow row in _apex.StreamAsync(
                         RowsSql,
                         fetchSize: 16))
        {
            sum += row.Get<int>(0);
        }

        return sum;
    }

    [Benchmark]
    public async Task<int> ApexBorrowedReader100RowsAsync()
    {
        int sum = 0;
        await using ISqlRowReader reader = await _apexRowsPrepared.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sum += reader.GetInt32(0);
        }

        return sum;
    }

    [Benchmark]
    public async Task<int> ApexAdoReader100RowsAsync()
    {
        await using var reader = await _adoRowsPrepared.ExecuteReaderAsync();
        var sum = 0;
        while (await reader.ReadAsync())
        {
            sum += reader.GetInt32(0);
        }

        return sum;
    }
}

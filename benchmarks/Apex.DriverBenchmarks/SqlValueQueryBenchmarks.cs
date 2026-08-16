using Apex.PgClient;
using Apex.SqlClient;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Npgsql;

namespace Apex.DriverBenchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class SqlValueQueryBenchmarks
{
    private const string Query = "SELECT $1::int4, $2::text";
    private const string Text = "apex-value";

    private PgConnection _connection = null!;
    private ISqlPreparedStatement _prepared = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        string connectionString =
            Environment.GetEnvironmentVariable("APEX_PG_CONNECTION_STRING") ??
            throw new InvalidOperationException(
                "Set APEX_PG_CONNECTION_STRING before running database benchmarks.");
        NpgsqlConnectionStringBuilder builder = new(connectionString);
        string username = builder.Username ??
            throw new InvalidOperationException("The benchmark connection string requires Username.");
        _connection = await Apex.PgClient.PgClient.ConnectAsync(new PgConnectOptions
        {
            Host = builder.Host ??
                throw new InvalidOperationException("The benchmark connection string requires Host."),
            Port = builder.Port,
            Database = builder.Database ?? username,
            Username = username,
            Password = builder.Password ?? string.Empty,
            SslMode = PgSslMode.Disable,
        });
        _prepared = await _connection.PrepareAsync(Query);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _prepared.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> PreparedTextParameters()
    {
        SqlRowSet rows = await _prepared.QueryAsync(SqlParameters.Create(42, Text));
        return rows[0].GetInt32(0) + rows[0].GetString(1).Length;
    }

    [Benchmark]
    public async Task<int> TypedBinaryParameters()
    {
        SqlRowSet rows = await _connection.QueryTypedAsync(
            Query,
            PgParameters.Create(
                new PgParameter(PgType.Integer, 42),
                new PgParameter(PgType.Text, Text)));
        return rows[0].GetInt32(0) + rows[0].GetString(1).Length;
    }
}

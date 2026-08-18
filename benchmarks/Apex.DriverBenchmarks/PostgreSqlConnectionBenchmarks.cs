using Apex.PgClient;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Npgsql;

namespace Apex.DriverBenchmarks;

[MemoryDiagnoser]
[Config(typeof(PostgreSqlConnectionBenchmarkConfig))]
public class PostgreSqlConnectionBenchmarks
{
    private PgConnectOptions _options = null!;

    [Params(PgSslMode.Disable, PgSslMode.Require)]
    public PgSslMode SslMode { get; set; }

    [GlobalSetup]
    public void Setup() => _options = PostgreSqlBenchmarkOptions.FromEnvironment();

    [Benchmark]
    public async Task ConnectAndDisposeAsync()
    {
        await using PgConnection connection = await Apex.PgClient.PgClient.ConnectAsync(
            _options with { SslMode = SslMode });
    }
}

internal static class PostgreSqlBenchmarkOptions
{
    internal static PgConnectOptions FromEnvironment()
    {
        string connectionString =
            Environment.GetEnvironmentVariable("APEX_PG_CONNECTION_STRING") ??
            throw new InvalidOperationException(
                "Set APEX_PG_CONNECTION_STRING before running database benchmarks.");
        NpgsqlConnectionStringBuilder builder = new(connectionString);
        string username = builder.Username ??
            throw new InvalidOperationException("The benchmark connection string requires Username.");
        return new PgConnectOptions
        {
            Host = builder.Host ??
                throw new InvalidOperationException("The benchmark connection string requires Host."),
            Port = builder.Port,
            Database = builder.Database ?? username,
            Username = username,
            Password = builder.Password ?? string.Empty,
        };
    }
}

internal sealed class PostgreSqlConnectionBenchmarkConfig : ManualConfig
{
    public PostgreSqlConnectionBenchmarkConfig()
    {
        // BenchmarkDotNet 0.15.8 does not recognize .NET 11 in its SDK toolchain.
        AddJob(Job.Default
            .WithToolchain(InProcessNoEmitToolchain.Instance)
            .WithLaunchCount(1)
            .WithWarmupCount(20)
            .WithIterationCount(100)
            .WithInvocationCount(1)
            .WithUnrollFactor(1));
    }
}

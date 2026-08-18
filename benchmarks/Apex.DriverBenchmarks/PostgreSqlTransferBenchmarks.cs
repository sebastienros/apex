using Apex.PgClient;
using Apex.SqlClient;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

namespace Apex.DriverBenchmarks;

[MemoryDiagnoser]
[Config(typeof(PostgreSqlTransferBenchmarkConfig))]
public class PostgreSqlTransferBenchmarks
{
    public const int PayloadLength = 4 * 1024 * 1024;

    private PgConnection _connection = null!;
    private ISqlPreparedStatement _download = null!;
    private PgParameters _uploadParameters;

    [Params(PgSslMode.Disable, PgSslMode.Require)]
    public PgSslMode SslMode { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        PgConnectOptions options = PostgreSqlBenchmarkOptions.FromEnvironment() with
        {
            SslMode = SslMode,
        };
        _connection = await Apex.PgClient.PgClient.ConnectAsync(options);

        byte[] payload = GC.AllocateUninitializedArray<byte>(PayloadLength);
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)index;
        }

        _uploadParameters = PgParameters.Create(PgParameter.Create(PgType.Bytea, payload));
        await _connection.ExecuteAsync(
            "CREATE TEMP TABLE apex_transfer_benchmark(payload bytea NOT NULL)");
        await _connection.ExecuteTypedAsync(
            "INSERT INTO apex_transfer_benchmark(payload) VALUES ($1)",
            _uploadParameters);
        _download = await _connection.PrepareAsync(
            "SELECT payload FROM apex_transfer_benchmark");
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _download.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Benchmark]
    public async Task<int> DownloadAsync()
    {
        SqlRowSet rows = await _download.QueryAsync();
        return rows[0].Get<byte[]>(0).Length;
    }

    [Benchmark]
    public async Task<int> UploadAsync()
    {
        SqlRowSet rows = await _connection.QueryTypedAsync(
            "SELECT octet_length($1)",
            _uploadParameters);
        return rows[0].Get<int>(0);
    }
}

internal sealed class PostgreSqlTransferBenchmarkConfig : ManualConfig
{
    public PostgreSqlTransferBenchmarkConfig()
    {
        // BenchmarkDotNet 0.15.8 does not recognize .NET 11 in its SDK toolchain.
        AddJob(Job.Default
            .WithToolchain(InProcessNoEmitToolchain.Instance)
            .WithLaunchCount(1)
            .WithWarmupCount(10)
            .WithIterationCount(50)
            .WithInvocationCount(1)
            .WithUnrollFactor(1));
    }
}

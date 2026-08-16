using Apex.PgClient;
using Apex.SqlClient;
using BenchmarkDotNet.Attributes;

namespace Apex.DriverBenchmarks;

[MemoryDiagnoser]
public class RowGetterBenchmarks
{
    private PgConnection _connection = null!;
    private SqlRow _textRow;
    private SqlRow _binaryRow;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var connectionString =
          Environment.GetEnvironmentVariable("APEX_PG_CONNECTION_STRING") ??
          throw new InvalidOperationException(
            "Set APEX_PG_CONNECTION_STRING before running database benchmarks.");
        _connection =
          await Apex.PgClient.PgClient.ConnectAsync(connectionString);
        const string projection =
          """
      42::int4,
      '12345678-1234-5678-9012-123456789abc'::uuid,
      point(1.5, -2.25),
      'hello'::text
      """;
        _textRow =
          (await _connection.QueryAsync("SELECT " + projection))[0];
        _binaryRow =
          (await _connection.QueryAsync(
            "SELECT " + projection + ", $1::int4",
            SqlParameters.Create(0)))[0];

        // Promote the repeated string in the decoder cache before measurement.
        _ = _textRow.Get<string>(3);
        _ = _textRow.Get<string>(3);
        _ = _binaryRow.Get<string>(3);
        _ = _binaryRow.Get<string>(3);
    }

    [GlobalCleanup]
    public ValueTask CleanupAsync() => _connection.DisposeAsync();

    [Benchmark]
    public int GetInt32Text() => _textRow.Get<int>(0);

    [Benchmark]
    public int GetInt32Binary() => _binaryRow.Get<int>(0);

    [Benchmark]
    public Guid GetGuidText() => _textRow.Get<Guid>(1);

    [Benchmark]
    public Guid GetGuidBinary() => _binaryRow.Get<Guid>(1);

    [Benchmark]
    public PgPoint GetPointText() => _textRow.Get<PgPoint>(2);

    [Benchmark]
    public PgPoint GetPointBinary() => _binaryRow.Get<PgPoint>(2);

    [Benchmark]
    public string GetStringText() => _textRow.Get<string>(3);

    [Benchmark]
    public string GetStringBinary() => _binaryRow.Get<string>(3);

    [Benchmark]
    public object GetObjectBinary() => _binaryRow.Get<object>(1);

    [Benchmark]
    public int GetInt32SpecificBinary() => _binaryRow.GetInt32(0);

    [Benchmark]
    public Guid GetGuidSpecificBinary() => _binaryRow.GetGuid(1);
}

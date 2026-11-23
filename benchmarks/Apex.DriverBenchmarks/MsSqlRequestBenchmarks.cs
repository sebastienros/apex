using System.Buffers;
using Apex.MsSqlClient.Internal;
using Apex.SqlClient;
using BenchmarkDotNet.Attributes;

namespace Apex.DriverBenchmarks;

// Builds SQL Server RPC payloads without I/O, so request-side allocations and CPU are
// measured without round-trip noise.
[MemoryDiagnoser]
public class MsSqlRequestBenchmarks
{
    private const string Sql =
      "SELECT CAST(@P1 AS int), CAST(@P2 AS nvarchar(32)), CAST(@P3 AS decimal(18,4)), @P4";

    private readonly SqlParameters _parameters = SqlParameters.Create(
      42,
      "a short string value",
      1234.5678m,
      SqlValue.Null);

    // The connection reuses one request buffer, so the benchmarks do too.
    private readonly ArrayBufferWriter<byte> _payload = new(1024);

    [Benchmark(Baseline = true)]
    public int BuildExecute()
    {
        _payload.ResetWrittenCount();
        TdsRequestWriter.WriteExecute(_payload, 1, _parameters, 0);
        return _payload.WrittenCount;
    }

    [Benchmark]
    public int BuildExecuteSql()
    {
        _payload.ResetWrittenCount();
        TdsRequestWriter.WriteExecuteSql(_payload, Sql, _parameters, 0);
        return _payload.WrittenCount;
    }

    [Benchmark]
    public int BuildPrepareExecute()
    {
        _payload.ResetWrittenCount();
        TdsRequestWriter.WritePrepareExecute(
          _payload,
          Sql,
          TdsRequestWriter.BuildDefinitions(_parameters),
          _parameters,
          0);
        return _payload.WrittenCount;
    }

    [Benchmark]
    public int BuildSqlBatch()
    {
        _payload.ResetWrittenCount();
        TdsRequestWriter.WriteSqlBatch(_payload, Sql, 0);
        return _payload.WrittenCount;
    }
}

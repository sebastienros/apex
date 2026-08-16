using System.Globalization;
using Apex.MsSqlClient;
using Apex.SqlClient;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient;

namespace Apex.DriverBenchmarks;

[MemoryDiagnoser]
public class MsSqlBenchmarks
{
    private const int BatchDepth = 16;
    private const string RowsSql =
      """
    WITH numbers AS (
      SELECT 1 AS value
      UNION ALL
      SELECT value + 1 FROM numbers WHERE value < 100
    )
    SELECT value FROM numbers OPTION (MAXRECURSION 100)
    """;
    private const string StringsSql =
      """
    WITH numbers AS (
      SELECT 1 AS value
      UNION ALL
      SELECT value + 1 FROM numbers WHERE value < 100
    )
    SELECT CAST(N'repeated-value' AS nvarchar(32)) FROM numbers OPTION (MAXRECURSION 100)
    """;

    private MsSqlConnection _apex = null!;
    private ISqlPreparedStatement _apexPrepared = null!;
    private ISqlPreparedStatement _apexBatch = null!;
    private IReadOnlyList<SqlParameters> _apexBatchParameters = null!;
    private SqlConnection _microsoft = null!;
    private SqlCommand _microsoftSimple = null!;
    private SqlCommand _microsoftPrepared = null!;
    private SqlCommand _microsoftRows = null!;
    private SqlCommand _microsoftStrings = null!;
    private SqlCommand _microsoftBatch = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var connectionString =
          Environment.GetEnvironmentVariable("APEX_MSSQL_CONNECTION_STRING") ??
          throw new InvalidOperationException(
            "Set APEX_MSSQL_CONNECTION_STRING before running SQL Server benchmarks.");

        _apex = await Apex.MsSqlClient.MsSqlClient.ConnectAsync(
          MsSqlConnectOptions.Parse(connectionString));
        _apexPrepared = await _apex.PrepareAsync("SELECT CAST(@P1 AS int)");
        await _apex.ExecuteAsync(
          "CREATE TABLE #apex_batch (value int NOT NULL); " +
          "INSERT INTO #apex_batch VALUES (0)");
        _apexBatch = await _apex.PrepareAsync(
          "UPDATE #apex_batch SET value = @P1");
        _apexBatchParameters = Enumerable.Range(1, BatchDepth)
          .Select(static value => SqlParameters.Create(value))
          .ToArray();

        _microsoft = new SqlConnection(connectionString);
        await _microsoft.OpenAsync();
        _microsoftSimple = new SqlCommand("SELECT 1", _microsoft);
        _microsoftPrepared = new SqlCommand("SELECT CAST(@value AS int)", _microsoft);
        _microsoftPrepared.Parameters.Add(new SqlParameter("@value", System.Data.SqlDbType.Int)
        {
            Value = 42,
        });
        await _microsoftPrepared.PrepareAsync();
        _microsoftRows = new SqlCommand(RowsSql, _microsoft);
        _microsoftStrings = new SqlCommand(StringsSql, _microsoft);
        await using (SqlCommand setup = new(
          "CREATE TABLE #microsoft_batch (value int NOT NULL); " +
          "INSERT INTO #microsoft_batch VALUES (0)",
          _microsoft))
        {
            _ = await setup.ExecuteNonQueryAsync();
        }

        _microsoftBatch = new SqlCommand(
          "UPDATE #microsoft_batch SET value = @value",
          _microsoft);
        _microsoftBatch.Parameters.Add(
          new SqlParameter("@value", System.Data.SqlDbType.Int));
        await _microsoftBatch.PrepareAsync();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _apexPrepared.DisposeAsync();
        await _apexBatch.DisposeAsync();
        await _apex.DisposeAsync();
        await _microsoftSimple.DisposeAsync();
        await _microsoftPrepared.DisposeAsync();
        await _microsoftRows.DisposeAsync();
        await _microsoftStrings.DisposeAsync();
        await _microsoftBatch.DisposeAsync();
        await _microsoft.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> MicrosoftSimpleQueryAsync() =>
      Convert.ToInt32(
        await _microsoftSimple.ExecuteScalarAsync(),
        CultureInfo.InvariantCulture);

    [Benchmark]
    public async Task<int> ApexSimpleQueryAsync() =>
      (await _apex.QueryAsync("SELECT 1"))[0].Get<int>(0);

    [Benchmark]
    public async Task<int> MicrosoftPreparedQueryAsync() =>
      Convert.ToInt32(
        await _microsoftPrepared.ExecuteScalarAsync(),
        CultureInfo.InvariantCulture);

    [Benchmark]
    public async Task<int> ApexPreparedQueryAsync() =>
      (await _apexPrepared.QueryAsync(SqlParameters.Create(42)))[0].Get<int>(0);

    [Benchmark]
    public async Task<int> MicrosoftSafeStream100RowsAsync() =>
      await ReadMicrosoftRowsAsync(_microsoftRows);

    [Benchmark]
    public async Task<int> ApexSafeStream100RowsAsync()
    {
        var sum = 0;
        await foreach (var row in _apex.StreamAsync(RowsSql, fetchSize: 16))
        {
            sum += row.Get<int>(0);
        }

        return sum;
    }

    [Benchmark]
    public async Task<int> MicrosoftBorrowedReader100RowsAsync() =>
      await ReadMicrosoftRowsAsync(_microsoftRows);

    [Benchmark]
    public async Task<int> ApexBorrowedReader100RowsAsync()
    {
        var sum = 0;
        await using var reader = await _apex.ExecuteReaderAsync(RowsSql);
        while (await reader.ReadAsync())
        {
            sum += reader.GetInt32(0);
        }

        return sum;
    }

    [Benchmark(OperationsPerInvoke = BatchDepth)]
    public async Task<int> MicrosoftSerialBatchAsync()
    {
        var affected = 0;
        for (var value = 1; value <= BatchDepth; value++)
        {
            _microsoftBatch.Parameters[0].Value = value;
            affected += await _microsoftBatch.ExecuteNonQueryAsync();
        }

        return affected;
    }

    [Benchmark(OperationsPerInvoke = BatchDepth)]
    public async Task<int> ApexSerialBatchAsync()
    {
        var results =
          await _apexBatch.ExecuteBatchAsync(_apexBatchParameters);
        if (results.Any(static result => result.AffectedRows != 1))
        {
            throw new InvalidOperationException("Unexpected Apex SQL Server batch result.");
        }

        return results.Count;
    }

    [Benchmark]
    public async Task<int> MicrosoftRepeatedStrings100RowsAsync()
    {
        await using var reader = await _microsoftStrings.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            if (reader.GetString(0) != "repeated-value")
            {
                throw new InvalidOperationException("Unexpected SQL Server string value.");
            }

            count++;
        }

        return count;
    }

    [Benchmark]
    public async Task<int> ApexRepeatedStrings100RowsAsync()
    {
        var count = 0;
        await foreach (var row in _apex.StreamAsync(StringsSql, fetchSize: 16))
        {
            if (row.GetString(0) != "repeated-value")
            {
                throw new InvalidOperationException("Unexpected Apex SQL Server string value.");
            }

            count++;
        }

        return count;
    }

    private static async Task<int> ReadMicrosoftRowsAsync(SqlCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        var sum = 0;
        while (await reader.ReadAsync())
        {
            sum += reader.GetInt32(0);
        }

        return sum;
    }
}

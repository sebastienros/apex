using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Apex.MsSqlClient;
using Apex.PgClient;
using Apex.SqlClient;
using Microsoft.Data.SqlClient;
using Microsoft.Crank.EventSources;
using MySqlConnector;
using Npgsql;
using ApexMySql = Apex.MySqlClient;

var requestedDatabase = Environment.GetEnvironmentVariable("APEX_BENCH_DATABASE");
var driver = args.ElementAtOrDefault(0) ??
  (requestedDatabase?.ToLowerInvariant() switch
  {
      "mysql" => "apex-mysql",
      "mssql" or "sqlserver" => "apex-mssql",
      _ => "apex",
  });
var database = requestedDatabase?.ToLowerInvariant() switch
{
    "sqlserver" => "mssql",
    { } selected => selected,
    _ => driver.ToLowerInvariant() switch
    {
        "apex-mysql" or "mysqlconnector" => "mysql",
        "apex-mssql" or "microsoft-data-sqlclient" => "mssql",
        _ => "postgres",
    },
};
var workload =
  Environment.GetEnvironmentVariable("APEX_BENCH_WORKLOAD") ?? "query";
if (workload is not ("query" or "stream100" or "borrowed100" or "pipeline" or "batch" or
    "string100" or "download" or "upload"))
{
    throw new ArgumentException($"Unknown workload '{workload}'.");
}

var fetchSize = int.Parse(
  Environment.GetEnvironmentVariable("APEX_BENCH_FETCH_SIZE") ?? "16",
  CultureInfo.InvariantCulture);
var rowCount = int.Parse(
  Environment.GetEnvironmentVariable("APEX_BENCH_ROW_COUNT") ?? "100",
  CultureInfo.InvariantCulture);
var payloadBytes = int.Parse(
  Environment.GetEnvironmentVariable("APEX_BENCH_PAYLOAD_BYTES") ?? "4194304",
  CultureInfo.InvariantCulture);
var pipelineDepth = int.Parse(
  Environment.GetEnvironmentVariable("APEX_BENCH_PIPELINE_DEPTH") ?? "64",
  CultureInfo.InvariantCulture);
var concurrency = int.Parse(
  Environment.GetEnvironmentVariable("APEX_BENCH_CONCURRENCY") ?? "16",
  CultureInfo.InvariantCulture);
TimeSpan warmup = TimeSpan.FromSeconds(double.Parse(
  Environment.GetEnvironmentVariable("APEX_BENCH_WARMUP_SECONDS") ?? "2",
  CultureInfo.InvariantCulture));
TimeSpan duration = TimeSpan.FromSeconds(double.Parse(
  Environment.GetEnvironmentVariable("APEX_BENCH_DURATION_SECONDS") ?? "10",
  CultureInfo.InvariantCulture));
var connectionVariable = database switch
{
    "postgres" => "APEX_PG_CONNECTION_STRING",
    "mysql" => "APEX_MYSQL_CONNECTION_STRING",
    "mssql" or "sqlserver" => "APEX_MSSQL_CONNECTION_STRING",
    _ => throw new ArgumentException(
      $"Unknown database '{database}'. Use 'postgres', 'mysql', or 'mssql'."),
};
var connectionString =
  Environment.GetEnvironmentVariable(connectionVariable) ??
  throw new InvalidOperationException($"Set {connectionVariable}.");

var runners = await Task.WhenAll(
  Enumerable.Range(0, concurrency)
    .Select(_ => CreateRunnerAsync(
      database,
      driver,
      workload,
      fetchSize,
      rowCount,
      pipelineDepth,
      payloadBytes,
      connectionString).AsTask()));
try
{
    await RunPhaseAsync(driver, runners, warmup, record: false);
    RegisterCrankMetrics();
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    int[] collectionsBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    var result = await RunPhaseAsync(
      driver,
      runners,
      duration,
      record: true);
    result = result with
    {
        AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
        Gen0Collections = GC.CollectionCount(0) - collectionsBefore[0],
        Gen1Collections = GC.CollectionCount(1) - collectionsBefore[1],
        Gen2Collections = GC.CollectionCount(2) - collectionsBefore[2],
    };
    result = result with
    {
        AllocatedBytesPerOperation = result.Operations == 0
          ? 0
          : (double)result.AllocatedBytes / result.Operations,
        BytesPerOperation = workload is "download" or "upload" ? payloadBytes : 0,
        TransferMibPerSecond = workload is "download" or "upload"
          ? result.OperationsPerSecond * payloadBytes / (1024d * 1024d)
          : 0,
    };
    ReportCrankMetrics(result);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    foreach (var runner in runners)
    {
        await runner.DisposeAsync();
    }
}

static async Task<HarnessResult> RunPhaseAsync(
    string driver,
    IQueryRunner[] runners,
    TimeSpan duration,
    bool record)
{
    ConcurrentBag<long> latencies = [];
    long operations = 0;
    using CancellationTokenSource stop = new(duration);
    var workers = runners
      .Select(runner => RunWorkerAsync(
        runner,
        stop.Token,
        record ? latencies : null,
        count => Interlocked.Add(ref operations, count)))
      .ToArray();
    Stopwatch elapsed = Stopwatch.StartNew();
    await Task.WhenAll(workers);
    elapsed.Stop();
    var ordered = latencies.Order().ToArray();
    return new HarnessResult(
      driver,
      runners.Length,
      operations,
      elapsed.Elapsed.TotalSeconds,
      operations / elapsed.Elapsed.TotalSeconds,
      Percentile(ordered, 0.50),
      Percentile(ordered, 0.95),
      Percentile(ordered, 0.99),
      0,
      0,
      0,
      0,
      0,
      0,
      0,
      Environment.Version.ToString(),
      System.Runtime.InteropServices.RuntimeInformation.OSDescription,
      System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
}

static async Task RunWorkerAsync(
    IQueryRunner runner,
    CancellationToken cancellationToken,
    ConcurrentBag<long>? latencies,
    Action<int> completed)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await runner.QueryAsync(CancellationToken.None);
        }

        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            break;
        }

        latencies?.Add(Stopwatch.GetTimestamp() - started);
        completed(runner.OperationsPerInvocation);
    }
}

static ValueTask<IQueryRunner> CreateRunnerAsync(
    string database,
    string driver,
    string workload,
    int fetchSize,
    int rowCount,
    int pipelineDepth,
    int payloadBytes,
    string connectionString) =>
  (database, driver.ToLowerInvariant()) switch
  {
      ("postgres", "apex") when workload is "download" or "upload" =>
        WrapAsync(ApexTransferRunner.CreateAsync(
          workload,
          payloadBytes,
          connectionString)),
      ("postgres", "apex") => WrapAsync(ApexQueryRunner.CreateAsync(
        workload,
        fetchSize,
        rowCount,
        pipelineDepth,
        connectionString)),
      ("postgres", "npgsql") => WrapAsync(NpgsqlQueryRunner.CreateAsync(
        workload,
        rowCount,
        pipelineDepth,
        connectionString)),
      ("mysql", "apex-mysql") => WrapAsync(ApexMySqlQueryRunner.CreateAsync(
        workload,
        fetchSize,
        rowCount,
        pipelineDepth,
        connectionString)),
      ("mysql", "mysqlconnector") => WrapAsync(MySqlConnectorQueryRunner.CreateAsync(
        workload,
        rowCount,
        pipelineDepth,
        connectionString)),
      ("mssql", "apex-mssql") => WrapAsync(ApexMsSqlQueryRunner.CreateAsync(
        workload,
        fetchSize,
        rowCount,
        pipelineDepth,
        connectionString)),
      ("mssql", "microsoft-data-sqlclient") => WrapAsync(MicrosoftMsSqlQueryRunner.CreateAsync(
        workload,
        rowCount,
        pipelineDepth,
        connectionString)),
      _ => throw new ArgumentException(
        $"Unknown driver '{driver}' for database '{database}'."),
  };

static async ValueTask<IQueryRunner> WrapAsync<T>(ValueTask<T> runner)
  where T : IQueryRunner =>
  await runner;

static void RegisterCrankMetrics()
{
    Register("apex/operations-per-second", "Operations/s", "Completed operations per second", "n2");
    Register("apex/latency-p50", "P50 (ms)", "50th percentile operation latency", "n3");
    Register("apex/latency-p95", "P95 (ms)", "95th percentile operation latency", "n3");
    Register("apex/latency-p99", "P99 (ms)", "99th percentile operation latency", "n3");
    Register("apex/allocated-bytes", "Allocated (B)", "Managed bytes allocated", "n0");
    Register(
      "apex/allocated-bytes-per-operation",
      "Allocated/op (B)",
      "Managed bytes allocated per operation",
      "n2");
    Register(
      "apex/transfer-mib-per-second",
      "Transfer (MiB/s)",
      "Application payload transferred per second",
      "n2");

    static void Register(string name, string shortDescription, string longDescription, string format) =>
      BenchmarksEventSource.Register(
        name,
        Operations.First,
        Operations.First,
        shortDescription,
        longDescription,
        format);
}

static void ReportCrankMetrics(HarnessResult result)
{
    BenchmarksEventSource.Measure("apex/operations-per-second", result.OperationsPerSecond);
    BenchmarksEventSource.Measure("apex/latency-p50", result.P50Milliseconds);
    BenchmarksEventSource.Measure("apex/latency-p95", result.P95Milliseconds);
    BenchmarksEventSource.Measure("apex/latency-p99", result.P99Milliseconds);
    BenchmarksEventSource.Measure("apex/allocated-bytes", result.AllocatedBytes);
    BenchmarksEventSource.Measure(
      "apex/allocated-bytes-per-operation",
      result.AllocatedBytesPerOperation);
    BenchmarksEventSource.Measure("apex/transfer-mib-per-second", result.TransferMibPerSecond);
}

static double Percentile(long[] ordered, double percentile)
{
    if (ordered.Length == 0)
    {
        return 0;
    }

    var index = Math.Clamp(
      (int)Math.Ceiling(percentile * ordered.Length) - 1,
      0,
      ordered.Length - 1);
    return ordered[index] * 1000d / Stopwatch.Frequency;
}

internal interface IQueryRunner : IAsyncDisposable
{
    int OperationsPerInvocation { get; }

    ValueTask QueryAsync(CancellationToken cancellationToken);
}

internal static class ApexPostgreSqlOptions
{
    internal static PgConnectOptions Parse(
        string connectionString,
        bool useConfiguredSslMode = false)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString);
        var username = builder.Username ??
          throw new InvalidOperationException("Connection string requires Username.");
        var stringCacheCapacity = int.Parse(
          Environment.GetEnvironmentVariable("APEX_BENCH_STRING_CACHE_CAPACITY") ??
          "1024",
          CultureInfo.InvariantCulture);
        return new PgConnectOptions
        {
            Host = builder.Host ??
              throw new InvalidOperationException("Connection string requires Host."),
            Port = builder.Port,
            Database = builder.Database ?? username,
            Username = username,
            Password = builder.Password ?? string.Empty,
            PipeliningLimit = 256,
            SslMode = useConfiguredSslMode && builder.SslMode != Npgsql.SslMode.Disable
              ? PgSslMode.Require
              : PgSslMode.Disable,
            StringCacheCapacity = stringCacheCapacity,
        };
    }
}

internal sealed class ApexTransferRunner(
    PgConnection connection,
    string workload,
    int payloadBytes,
    PgParameters uploadParameters,
    ISqlPreparedStatement? downloadStatement) : IQueryRunner
{
    public static async ValueTask<ApexTransferRunner> CreateAsync(
        string workload,
        int payloadBytes,
        string connectionString)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(payloadBytes);
        var connection = await PgClient.ConnectAsync(
          ApexPostgreSqlOptions.Parse(connectionString, useConfiguredSslMode: true));
        byte[] payload = GC.AllocateUninitializedArray<byte>(payloadBytes);
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)index;
        }

        var uploadParameters =
          PgParameters.Create(PgParameter.Create(PgType.Bytea, payload));
        ISqlPreparedStatement? downloadStatement = null;
        if (workload == "download")
        {
            await connection.ExecuteAsync(
              "CREATE TEMP TABLE apex_transfer_benchmark(payload bytea NOT NULL)");
            await connection.ExecuteTypedAsync(
              "INSERT INTO apex_transfer_benchmark(payload) VALUES ($1)",
              uploadParameters);
            downloadStatement = await connection.PrepareAsync(
              "SELECT payload FROM apex_transfer_benchmark");
        }

        return new ApexTransferRunner(
          connection,
          workload,
          payloadBytes,
          uploadParameters,
          downloadStatement);
    }

    public int OperationsPerInvocation => 1;

    public async ValueTask QueryAsync(CancellationToken cancellationToken)
    {
        int transferred;
        if (workload == "download")
        {
            SqlRowSet rows = await downloadStatement!.QueryAsync(
              cancellationToken: cancellationToken);
            transferred = rows[0].Get<byte[]>(0).Length;
        }
        else
        {
            SqlRowSet rows = await connection.QueryTypedAsync(
              "SELECT octet_length($1)",
              uploadParameters,
              cancellationToken);
            transferred = rows[0].Get<int>(0);
        }

        if (transferred != payloadBytes)
        {
            throw new InvalidOperationException(
              $"Expected {payloadBytes} transferred bytes but received {transferred}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (downloadStatement is not null)
        {
            await downloadStatement.DisposeAsync();
        }

        await connection.DisposeAsync();
    }
}

internal sealed class ApexQueryRunner(
    PgConnection connection,
    string workload,
    int fetchSize,
    string streamSql,
    int rowCount,
    int expectedSum,
    int pipelineDepth,
    ISqlPreparedStatement? pipelineStatement) : IQueryRunner
{
    public static async ValueTask<ApexQueryRunner> CreateAsync(
        string workload,
        int fetchSize,
        int rowCount,
        int pipelineDepth,
        string connectionString)
    {
        var connection = await PgClient.ConnectAsync(
          ApexPostgreSqlOptions.Parse(connectionString));
        var pipelineStatement = workload is "pipeline" or "batch"
          ? await connection.PrepareAsync("SELECT 1::int4")
          : null;
        return new ApexQueryRunner(
          connection,
          workload,
          fetchSize,
          workload == "string100"
            ? $"SELECT 'repeated-value'::text FROM generate_series(1, {rowCount})"
            : $"SELECT generate_series(1, {rowCount})::int4",
          rowCount,
          checked(rowCount * (rowCount + 1) / 2),
          pipelineDepth,
          pipelineStatement);
    }

    public int OperationsPerInvocation =>
      workload is "pipeline" or "batch" ? pipelineDepth : 1;

    public async ValueTask QueryAsync(CancellationToken cancellationToken)
    {
        if (workload is "pipeline" or "batch")
        {
            Task<SqlRowSet>[] pending = new Task<SqlRowSet>[pipelineDepth];
            for (var i = 0; i < pending.Length; i++)
            {
                pending[i] = pipelineStatement!.QueryAsync(
                  cancellationToken: CancellationToken.None).AsTask();
            }

            var results = await Task.WhenAll(pending);
            if (results.Any(static rows => rows[0].Get<int>(0) != 1))
            {
                throw new InvalidOperationException("Unexpected Apex pipeline result.");
            }
        }
        else if (workload == "borrowed100")
        {
            var sum = 0;
            await using var reader =
              await connection.ExecuteReaderAsync(streamSql, cancellationToken: cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sum += reader.GetInt32(0);
            }

            if (sum != expectedSum)
            {
                throw new InvalidOperationException($"Unexpected borrowed-reader sum {sum}.");
            }
        }
        else if (workload == "stream100")
        {
            var sum = 0;
            await foreach (var row in connection.StreamAsync(
                             streamSql,
                             fetchSize: fetchSize,
                             cancellationToken: cancellationToken))
            {
                sum += row.Get<int>(0);
            }

            if (sum != expectedSum)
            {
                throw new InvalidOperationException($"Unexpected stream sum {sum}.");
            }
        }
        else if (workload == "string100")
        {
            var count = 0;
            await foreach (var row in connection.StreamAsync(
                             streamSql,
                             fetchSize: fetchSize,
                             cancellationToken: cancellationToken))
            {
                if (row.GetString(0) != "repeated-value")
                {
                    throw new InvalidOperationException("Unexpected string value.");
                }

                count++;
            }

            if (count != rowCount)
            {
                throw new InvalidOperationException($"Unexpected row count {count}.");
            }
        }
        else
        {
            _ = await connection.QueryAsync("SELECT 1", cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (pipelineStatement is not null)
        {
            await pipelineStatement.DisposeAsync();
        }

        await connection.DisposeAsync();
    }
}

internal sealed class NpgsqlQueryRunner(
    NpgsqlConnection connection,
    string workload,
    string streamSql,
    int rowCount,
    int expectedSum,
    int pipelineDepth,
    NpgsqlBatch? pipelineBatch) : IQueryRunner
{
    public static async ValueTask<NpgsqlQueryRunner> CreateAsync(
        string workload,
        int rowCount,
        int pipelineDepth,
        string connectionString)
    {
        NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        NpgsqlBatch? pipelineBatch = null;
        if (workload is "pipeline" or "batch")
        {
            pipelineBatch = new NpgsqlBatch(connection);
            for (var i = 0; i < pipelineDepth; i++)
            {
                pipelineBatch.BatchCommands.Add(
                  new NpgsqlBatchCommand("SELECT 1::int4"));
            }

            await pipelineBatch.PrepareAsync();
        }

        return new NpgsqlQueryRunner(
          connection,
          workload,
          workload == "string100"
            ? $"SELECT 'repeated-value'::text FROM generate_series(1, {rowCount})"
            : $"SELECT generate_series(1, {rowCount})::int4",
          rowCount,
          checked(rowCount * (rowCount + 1) / 2),
          pipelineDepth,
          pipelineBatch);
    }

    public int OperationsPerInvocation =>
      workload is "pipeline" or "batch" ? pipelineDepth : 1;

    public async ValueTask QueryAsync(CancellationToken cancellationToken)
    {
        if (workload is "pipeline" or "batch")
        {
            await using var reader =
              await pipelineBatch!.ExecuteReaderAsync(CancellationToken.None);
            var count = 0;
            do
            {
                if (!await reader.ReadAsync(CancellationToken.None) ||
                    reader.GetInt32(0) != 1)
                {
                    throw new InvalidOperationException("Unexpected Npgsql pipeline result.");
                }

                count++;
            }
            while (await reader.NextResultAsync(CancellationToken.None));

            if (count != pipelineDepth)
            {
                throw new InvalidOperationException(
                  $"Expected {pipelineDepth} Npgsql results but received {count}.");
            }
        }
        else if (workload is "stream100" or "borrowed100")
        {
            await using NpgsqlCommand command =
              new(streamSql, connection);
            await using var reader =
              await command.ExecuteReaderAsync(cancellationToken);
            var sum = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                sum += reader.GetInt32(0);
            }

            if (sum != expectedSum)
            {
                throw new InvalidOperationException($"Unexpected stream sum {sum}.");
            }
        }
        else if (workload == "string100")
        {
            await using NpgsqlCommand command =
              new(streamSql, connection);
            await using var reader =
              await command.ExecuteReaderAsync(cancellationToken);
            var count = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(0) != "repeated-value")
                {
                    throw new InvalidOperationException("Unexpected string value.");
                }

                count++;
            }

            if (count != rowCount)
            {
                throw new InvalidOperationException($"Unexpected row count {count}.");
            }
        }
        else
        {
            await using NpgsqlCommand command = new("SELECT 1", connection);
            _ = await command.ExecuteScalarAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (pipelineBatch is not null)
        {
            await pipelineBatch.DisposeAsync();
        }

        await connection.DisposeAsync();
    }
}

internal sealed class ApexMsSqlQueryRunner(
    MsSqlConnection connection,
    string workload,
    int fetchSize,
    string rowsSql,
    int rowCount,
    int expectedSum,
    int batchDepth,
    ISqlPreparedStatement? batchStatement,
    IReadOnlyList<SqlParameters>? batchParameters) : IQueryRunner
{
    public static async ValueTask<ApexMsSqlQueryRunner> CreateAsync(
        string workload,
        int fetchSize,
        int rowCount,
        int batchDepth,
        string connectionString)
    {
        var connection = await Apex.MsSqlClient.MsSqlClient.ConnectAsync(
          MsSqlConnectOptions.Parse(connectionString));
        ISqlPreparedStatement? batchStatement = null;
        if (workload is "pipeline" or "batch")
        {
            await connection.ExecuteAsync(
              "CREATE TABLE #apex_batch (value int NOT NULL); " +
              "INSERT INTO #apex_batch VALUES (0)");
            batchStatement = await connection.PrepareAsync(
              "UPDATE #apex_batch SET value = @P1");
        }

        IReadOnlyList<SqlParameters>? batchParameters = workload is "pipeline" or "batch"
          ? Enumerable.Range(1, batchDepth)
            .Select(static value => SqlParameters.Create(value))
            .ToArray()
          : null;
        return new ApexMsSqlQueryRunner(
          connection,
          workload,
          fetchSize,
          MsSqlRowsSql(rowCount, workload == "string100"),
          rowCount,
          checked(rowCount * (rowCount + 1) / 2),
          batchDepth,
          batchStatement,
          batchParameters);
    }

    public int OperationsPerInvocation =>
      workload is "pipeline" or "batch" ? batchDepth : 1;

    public async ValueTask QueryAsync(CancellationToken cancellationToken)
    {
        if (workload is "pipeline" or "batch")
        {
            var results =
              await batchStatement!.ExecuteBatchAsync(batchParameters!, cancellationToken);
            if (results.Count != batchDepth ||
                results.Any(static result => result.AffectedRows != 1))
            {
                throw new InvalidOperationException(
                  $"Expected {batchDepth} Apex SQL Server batch results but received {results.Count}.");
            }
        }
        else if (workload == "borrowed100")
        {
            var sum = 0;
            await using var reader =
              await connection.ExecuteReaderAsync(rowsSql, cancellationToken: cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sum += reader.GetInt32(0);
            }

            ValidateSum(sum);
        }
        else if (workload == "stream100")
        {
            var sum = 0;
            await foreach (var row in connection.StreamAsync(
                             rowsSql,
                             fetchSize: fetchSize,
                             cancellationToken: cancellationToken))
            {
                sum += row.Get<int>(0);
            }

            ValidateSum(sum);
        }
        else if (workload == "string100")
        {
            var count = 0;
            await foreach (var row in connection.StreamAsync(
                             rowsSql,
                             fetchSize: fetchSize,
                             cancellationToken: cancellationToken))
            {
                if (row.GetString(0) != "repeated-value")
                {
                    throw new InvalidOperationException("Unexpected Apex SQL Server string value.");
                }

                count++;
            }

            if (count != rowCount)
            {
                throw new InvalidOperationException($"Unexpected SQL Server row count {count}.");
            }
        }
        else
        {
            var rows = await connection.QueryAsync("SELECT 1", cancellationToken);
            if (rows[0].Get<int>(0) != 1)
            {
                throw new InvalidOperationException("Unexpected Apex SQL Server query result.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (batchStatement is not null)
        {
            await batchStatement.DisposeAsync();
        }

        await connection.DisposeAsync();
    }

    private void ValidateSum(int sum)
    {
        if (sum != expectedSum)
        {
            throw new InvalidOperationException($"Unexpected SQL Server stream sum {sum}.");
        }
    }

    internal static string MsSqlRowsSql(int count, bool strings) =>
      $"""
    WITH numbers AS (
      SELECT 1 AS value
      UNION ALL
      SELECT value + 1 FROM numbers WHERE value < {count}
    )
    SELECT {(strings ? "CAST(N'repeated-value' AS nvarchar(32))" : "value")}
    FROM numbers
    OPTION (MAXRECURSION 0)
    """;
}

internal sealed class MicrosoftMsSqlQueryRunner(
    SqlConnection connection,
    string workload,
    int rowCount,
    int expectedSum,
    int batchDepth,
    SqlCommand queryCommand,
    SqlCommand rowsCommand,
    SqlCommand batchCommand) : IQueryRunner
{
    public static async ValueTask<MicrosoftMsSqlQueryRunner> CreateAsync(
        string workload,
        int rowCount,
        int batchDepth,
        string connectionString)
    {
        SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        SqlCommand queryCommand = new("SELECT 1", connection);
        SqlCommand rowsCommand = new(
          ApexMsSqlQueryRunner.MsSqlRowsSql(rowCount, workload == "string100"),
          connection);
        if (workload is "pipeline" or "batch")
        {
            await using SqlCommand setup = new(
              "CREATE TABLE #microsoft_batch (value int NOT NULL); " +
              "INSERT INTO #microsoft_batch VALUES (0)",
              connection);
            _ = await setup.ExecuteNonQueryAsync();
        }

        SqlCommand batchCommand = new(
          "UPDATE #microsoft_batch SET value = @value",
          connection);
        batchCommand.Parameters.Add(
          new SqlParameter("@value", System.Data.SqlDbType.Int));
        if (workload is "pipeline" or "batch")
        {
            await batchCommand.PrepareAsync();
        }

        return new MicrosoftMsSqlQueryRunner(
          connection,
          workload,
          rowCount,
          checked(rowCount * (rowCount + 1) / 2),
          batchDepth,
          queryCommand,
          rowsCommand,
          batchCommand);
    }

    public int OperationsPerInvocation =>
      workload is "pipeline" or "batch" ? batchDepth : 1;

    public async ValueTask QueryAsync(CancellationToken cancellationToken)
    {
        if (workload is "pipeline" or "batch")
        {
            var affected = 0;
            for (var value = 1; value <= batchDepth; value++)
            {
                batchCommand.Parameters[0].Value = value;
                affected += await batchCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            if (affected != batchDepth)
            {
                throw new InvalidOperationException("Unexpected Microsoft.Data.SqlClient batch result.");
            }
        }
        else if (workload is "stream100" or "borrowed100")
        {
            await using var reader =
              await rowsCommand.ExecuteReaderAsync(cancellationToken);
            var sum = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                sum += reader.GetInt32(0);
            }

            if (sum != expectedSum)
            {
                throw new InvalidOperationException($"Unexpected SQL Server stream sum {sum}.");
            }
        }
        else if (workload == "string100")
        {
            await using var reader =
              await rowsCommand.ExecuteReaderAsync(cancellationToken);
            var count = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(0) != "repeated-value")
                {
                    throw new InvalidOperationException(
                      "Unexpected Microsoft.Data.SqlClient string value.");
                }

                count++;
            }

            if (count != rowCount)
            {
                throw new InvalidOperationException($"Unexpected SQL Server row count {count}.");
            }
        }
        else if (Convert.ToInt32(
                   await queryCommand.ExecuteScalarAsync(cancellationToken),
                   CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException(
              "Unexpected Microsoft.Data.SqlClient query result.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await queryCommand.DisposeAsync();
        await rowsCommand.DisposeAsync();
        await batchCommand.DisposeAsync();
        await connection.DisposeAsync();
    }
}

internal sealed class ApexMySqlQueryRunner(
    ApexMySql.MySqlConnection connection,
    string workload,
    int fetchSize,
    string streamSql,
    int rowCount,
    long expectedSum,
    int pipelineDepth,
    ISqlPreparedStatement? pipelineStatement) : IQueryRunner
{
    public static async ValueTask<ApexMySqlQueryRunner> CreateAsync(
        string workload,
        int fetchSize,
        int rowCount,
        int pipelineDepth,
        string connectionString)
    {
        MySqlConnectionStringBuilder builder = new(connectionString);
        var username = string.IsNullOrEmpty(builder.UserID)
          ? throw new InvalidOperationException("Connection string requires UserID.")
          : builder.UserID;
        var stringCacheCapacity = int.Parse(
          Environment.GetEnvironmentVariable("APEX_BENCH_STRING_CACHE_CAPACITY") ??
          "1024",
          CultureInfo.InvariantCulture);
        var connection = await ApexMySql.MySqlClient.ConnectAsync(
          new ApexMySql.MySqlConnectOptions
          {
              Host = string.IsNullOrEmpty(builder.Server)
              ? throw new InvalidOperationException("Connection string requires Server.")
              : builder.Server,
              Port = (int)builder.Port,
              Database = builder.Database,
              Username = username,
              Password = builder.Password,
              PipeliningLimit = 256,
              StringCacheCapacity = stringCacheCapacity,
          });
        var pipelineStatement = workload is "pipeline" or "batch"
          ? await connection.PrepareAsync("SELECT CAST(1 AS SIGNED)")
          : null;
        return new ApexMySqlQueryRunner(
          connection,
          workload,
          fetchSize,
          BuildSequenceSql(rowCount, asString: workload == "string100"),
          rowCount,
          checked((long)rowCount * (rowCount + 1) / 2),
          pipelineDepth,
          pipelineStatement);
    }

    public int OperationsPerInvocation =>
      workload is "pipeline" or "batch" ? pipelineDepth : 1;

    public async ValueTask QueryAsync(CancellationToken cancellationToken)
    {
        if (workload is "pipeline" or "batch")
        {
            Task<SqlRowSet>[] pending = new Task<SqlRowSet>[pipelineDepth];
            for (var i = 0; i < pending.Length; i++)
            {
                pending[i] = pipelineStatement!.QueryAsync(
                  cancellationToken: CancellationToken.None).AsTask();
            }

            var results = await Task.WhenAll(pending);
            if (results.Any(static rows => rows[0].Get<long>(0) != 1L))
            {
                throw new InvalidOperationException("Unexpected Apex pipeline result.");
            }
        }
        else if (workload == "borrowed100")
        {
            long sum = 0;
            await using var reader =
              await connection.ExecuteReaderAsync(streamSql, cancellationToken: cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sum += reader.GetInt64(0);
            }

            if (sum != expectedSum)
            {
                throw new InvalidOperationException($"Unexpected borrowed-reader sum {sum}.");
            }
        }
        else if (workload == "stream100")
        {
            long sum = 0;
            await foreach (var row in connection.StreamAsync(
                             streamSql,
                             fetchSize: fetchSize,
                             cancellationToken: cancellationToken))
            {
                sum += row.GetInt64(0);
            }

            if (sum != expectedSum)
            {
                throw new InvalidOperationException($"Unexpected stream sum {sum}.");
            }
        }
        else if (workload == "string100")
        {
            var count = 0;
            await foreach (var row in connection.StreamAsync(
                             streamSql,
                             fetchSize: fetchSize,
                             cancellationToken: cancellationToken))
            {
                if (row.GetString(0) != "repeated-value")
                {
                    throw new InvalidOperationException("Unexpected string value.");
                }

                count++;
            }

            if (count != rowCount)
            {
                throw new InvalidOperationException($"Unexpected row count {count}.");
            }
        }
        else
        {
            _ = await connection.QueryAsync("SELECT CAST(1 AS SIGNED)", cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (pipelineStatement is not null)
        {
            await pipelineStatement.DisposeAsync();
        }

        await connection.DisposeAsync();
    }

    private static string BuildSequenceSql(int rowCount, bool asString) =>
      asString
        ? $"WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < {rowCount}) " +
          "SELECT 'repeated-value' FROM seq"
        : $"WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < {rowCount}) " +
          "SELECT CAST(n AS SIGNED) FROM seq";
}

/// <summary>
/// Runs the shared workload set against MySqlConnector. MySqlConnector does not permit
/// concurrent commands on one connection, so its closest supported equivalent to pipelining is
/// one reusable <see cref="MySqlBatch"/> containing the same number of prepared commands,
/// matching the PostgreSQL/Npgsql comparison.
/// </summary>
internal sealed class MySqlConnectorQueryRunner(
    MySqlConnection connection,
    string workload,
    string streamSql,
    int rowCount,
    long expectedSum,
    int pipelineDepth,
    MySqlBatch? pipelineBatch) : IQueryRunner
{
    public static async ValueTask<MySqlConnectorQueryRunner> CreateAsync(
        string workload,
        int rowCount,
        int pipelineDepth,
        string connectionString)
    {
        MySqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        MySqlBatch? pipelineBatch = null;
        if (workload is "pipeline" or "batch")
        {
            pipelineBatch = new MySqlBatch(connection);
            for (var i = 0; i < pipelineDepth; i++)
            {
                pipelineBatch.BatchCommands.Add(
                  new MySqlBatchCommand("SELECT CAST(1 AS SIGNED)"));
            }

            await pipelineBatch.PrepareAsync();
        }

        return new MySqlConnectorQueryRunner(
          connection,
          workload,
          BuildSequenceSql(rowCount, asString: workload == "string100"),
          rowCount,
          checked((long)rowCount * (rowCount + 1) / 2),
          pipelineDepth,
          pipelineBatch);
    }

    public int OperationsPerInvocation =>
      workload is "pipeline" or "batch" ? pipelineDepth : 1;

    public async ValueTask QueryAsync(CancellationToken cancellationToken)
    {
        if (workload is "pipeline" or "batch")
        {
            await using var reader =
              await pipelineBatch!.ExecuteReaderAsync(CancellationToken.None);
            var count = 0;
            do
            {
                if (!await reader.ReadAsync(CancellationToken.None) ||
                    reader.GetInt64(0) != 1L)
                {
                    throw new InvalidOperationException("Unexpected MySqlConnector pipeline result.");
                }

                count++;
            }
            while (await reader.NextResultAsync(CancellationToken.None));

            if (count != pipelineDepth)
            {
                throw new InvalidOperationException(
                  $"Expected {pipelineDepth} MySqlConnector results but received {count}.");
            }
        }
        else if (workload is "stream100" or "borrowed100")
        {
            await using MySqlCommand command =
              new(streamSql, connection);
            await using var reader =
              await command.ExecuteReaderAsync(cancellationToken);
            long sum = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                sum += reader.GetInt64(0);
            }

            if (sum != expectedSum)
            {
                throw new InvalidOperationException($"Unexpected stream sum {sum}.");
            }
        }
        else if (workload == "string100")
        {
            await using MySqlCommand command =
              new(streamSql, connection);
            await using var reader =
              await command.ExecuteReaderAsync(cancellationToken);
            var count = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(0) != "repeated-value")
                {
                    throw new InvalidOperationException("Unexpected string value.");
                }

                count++;
            }

            if (count != rowCount)
            {
                throw new InvalidOperationException($"Unexpected row count {count}.");
            }
        }
        else
        {
            await using MySqlCommand command = new("SELECT CAST(1 AS SIGNED)", connection);
            _ = await command.ExecuteScalarAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (pipelineBatch is not null)
        {
            await pipelineBatch.DisposeAsync();
        }

        await connection.DisposeAsync();
    }

    private static string BuildSequenceSql(int rowCount, bool asString) =>
      asString
        ? $"WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < {rowCount}) " +
          "SELECT 'repeated-value' FROM seq"
        : $"WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < {rowCount}) " +
          "SELECT CAST(n AS SIGNED) FROM seq";
}

internal sealed record HarnessResult(
    string Driver,
    int Concurrency,
    long Operations,
    double DurationSeconds,
    double OperationsPerSecond,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    long AllocatedBytes,
    double AllocatedBytesPerOperation,
    int BytesPerOperation,
    double TransferMibPerSecond,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    string Runtime,
    string OperatingSystem,
    string Architecture);

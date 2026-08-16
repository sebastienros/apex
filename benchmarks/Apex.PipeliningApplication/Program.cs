using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Apex.PgClient;
using Apex.SqlClient;

var concurrency = PositiveEnvironment("APEX_BENCH_CONCURRENCY", 64);
var queryCount = PositiveEnvironment("APEX_BENCH_QUERY_COUNT", 100_000);
var warmupQueryCount = NonNegativeEnvironment("APEX_BENCH_WARMUP_QUERY_COUNT", 10_000);
var options = CreateOptions() with
{
    PipeliningLimit = concurrency,
};

await using var connection = await PgClient.ConnectAsync(options);
await using var statement = await connection.PrepareAsync("SELECT 1::int4");

if (warmupQueryCount > 0)
{
    _ = await RunAsync(statement, concurrency, warmupQueryCount);
}

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
Stopwatch elapsed = Stopwatch.StartNew();
long sum = await RunAsync(statement, concurrency, queryCount);
elapsed.Stop();

var result = new PipeliningResult(
  "Apex",
  concurrency,
  queryCount,
  warmupQueryCount,
  elapsed.Elapsed.TotalSeconds,
  queryCount / elapsed.Elapsed.TotalSeconds,
  GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
  sum,
  Environment.Version.ToString(),
  System.Runtime.InteropServices.RuntimeInformation.OSDescription,
  System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
Console.WriteLine(JsonSerializer.Serialize(
  result,
  new JsonSerializerOptions { WriteIndented = true }));

static async Task<long> RunAsync(
    ISqlPreparedStatement statement,
    int concurrency,
    int queryCount)
{
    var next = -1;
    long sum = 0;
    Task[] workers = Enumerable.Range(0, Math.Min(concurrency, queryCount))
      .Select(_ => RunWorkerAsync())
      .ToArray();
    await Task.WhenAll(workers);
    return sum;

    async Task RunWorkerAsync()
    {
        while (Interlocked.Increment(ref next) < queryCount)
        {
            var rows = await statement.QueryAsync();
            int value = rows[0].Get<int>(0);
            if (value != 1)
            {
                throw new InvalidOperationException(
                  $"Unexpected PostgreSQL result {value}.");
            }

            Interlocked.Add(ref sum, value);
        }
    }
}

static int PositiveEnvironment(string name, int fallback)
{
    var value = Environment.GetEnvironmentVariable(name);
    return value is null
      ? fallback
      : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
        parsed > 0
        ? parsed
        : throw new ArgumentOutOfRangeException(name, value, "Value must be a positive integer.");
}

static int NonNegativeEnvironment(string name, int fallback)
{
    var value = Environment.GetEnvironmentVariable(name);
    return value is null
      ? fallback
      : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
        parsed >= 0
        ? parsed
        : throw new ArgumentOutOfRangeException(name, value, "Value must be a nonnegative integer.");
}

static PgConnectOptions CreateOptions()
{
    var connectionString = Environment.GetEnvironmentVariable("APEX_PG_CONNECTION_STRING");
    if (connectionString is null)
    {
        return new PgConnectOptions
        {
            Host = Environment.GetEnvironmentVariable("APEX_PG_HOST") ?? "localhost",
            Port = PositiveEnvironment("APEX_PG_PORT", 5432),
            Database = Environment.GetEnvironmentVariable("APEX_PG_DATABASE") ?? "db",
            Username = Environment.GetEnvironmentVariable("APEX_PG_USERNAME") ?? "user",
            Password = Environment.GetEnvironmentVariable("APEX_PG_PASSWORD") ?? "pass",
        };
    }

    if (!connectionString.Contains(';', StringComparison.Ordinal))
    {
        return PgConnectOptions.Parse(connectionString);
    }

    DbConnectionStringBuilder builder = new() { ConnectionString = connectionString };
    return new PgConnectOptions
    {
        Host = Value(builder, "Host", "Server") ?? "localhost",
        Port = int.Parse(Value(builder, "Port") ?? "5432", CultureInfo.InvariantCulture),
        Database = Value(builder, "Database", "Initial Catalog") ?? "db",
        Username = Value(builder, "Username", "User ID", "UserID", "User") ?? "user",
        Password = Value(builder, "Password") ?? string.Empty,
    };
}

static string? Value(DbConnectionStringBuilder builder, params string[] keys)
{
    foreach (var key in keys)
    {
        if (builder.TryGetValue(key, out var value))
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    return null;
}

internal sealed record PipeliningResult(
    string Driver,
    int Concurrency,
    int QueryCount,
    int WarmupQueryCount,
    double ElapsedSeconds,
    double QueriesPerSecond,
    long AllocatedBytes,
    long ResultSum,
    string Runtime,
    string OperatingSystem,
    string Architecture);

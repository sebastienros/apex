# Benchmarks

`Apex.DriverBenchmarks` contains BenchmarkDotNet suites for the shared row API
and all three direct drivers. Comparator packages are referenced only by the
benchmark project; Apex runtime packages do not depend on them.

## Run

Configure the database used by the selected suite:

```bash
export APEX_PG_CONNECTION_STRING='Host=localhost;Database=db;Username=user;Password=pass'
export APEX_MYSQL_CONNECTION_STRING='Server=localhost;Database=db;User ID=user;Password=pass'
export APEX_MSSQL_CONNECTION_STRING='Server=localhost;Database=master;User ID=sa;Password=secret;Encrypt=True;TrustServerCertificate=True'
```

List available benchmarks or select a driver:

```bash
dotnet run --configuration Release --project benchmarks/Apex.DriverBenchmarks -- --list flat
dotnet run --configuration Release --project benchmarks/Apex.DriverBenchmarks -- --filter '*PostgreSqlBenchmarks*'
dotnet run --configuration Release --project benchmarks/Apex.DriverBenchmarks -- --filter '*MySqlBenchmarks*'
dotnet run --configuration Release --project benchmarks/Apex.DriverBenchmarks -- --filter '*MsSqlBenchmarks*'
```

The suites cover connection reuse, simple and prepared queries, pipelining,
buffered and streaming rows, borrowed readers, repeated strings, type codecs,
and name/ordinal row access. Run one benchmark process at a time against an idle
local database, retain the generated environment metadata, and compare results
from the same machine and server configuration.

`Apex.ComparisonHarness` provides a fixed-duration process harness for throughput
and latency measurements:

```bash
dotnet run --configuration Release --project benchmarks/Apex.ComparisonHarness -- apex
```

Set `APEX_BENCH_DATABASE` to `postgres`, `mysql`, or `mssql` and use
`APEX_BENCH_WORKLOAD` to select the workload. The harness emits JSON suitable
for storing alongside machine, runtime, database, and commit metadata.
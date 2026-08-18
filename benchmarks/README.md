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
dotnet run --configuration Release --framework net10.0 --project benchmarks/Apex.DriverBenchmarks -- --filter '*PostgreSqlConnectionBenchmarks*'
dotnet run --configuration Release --framework net11.0 --project benchmarks/Apex.DriverBenchmarks -- --filter '*PostgreSqlConnectionBenchmarks*'
dotnet run --configuration Release --framework net10.0 --project benchmarks/Apex.DriverBenchmarks -- --filter '*PostgreSqlTransferBenchmarks*'
dotnet run --configuration Release --framework net11.0 --project benchmarks/Apex.DriverBenchmarks -- --filter '*PostgreSqlTransferBenchmarks*'
dotnet run --configuration Release --project benchmarks/Apex.DriverBenchmarks -- --filter '*MySqlBenchmarks*'
dotnet run --configuration Release --project benchmarks/Apex.DriverBenchmarks -- --filter '*MsSqlBenchmarks*'
```

`PostgreSqlConnectionBenchmarks` compares complete plaintext and TLS connection
lifecycles. Run both target-framework commands to produce the .NET 10 versus
.NET 11 matrix. The PostgreSQL endpoint must accept both plaintext and TLS
connections; `PgSslMode.Require` intentionally measures encryption without
certificate-chain or host-name validation.

`PostgreSqlTransferBenchmarks` measures upload and download throughput over one
persistent connection using a 4 MiB binary payload. It uses the same plaintext,
TLS, and target-framework matrix and reports managed allocations.

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

## Crank PostgreSQL transfer matrix

`apex-postgres.benchmarks.yml` runs Apex on the application machine and a
TLS-enabled PostgreSQL 18 container on the database machine. The
`aspnet-gold-lin` profile maps those jobs to `asp-gold-lin` and `asp-gold-db`;
the harness generates its own concurrency, so no load-machine job is needed.

```bash
crank \
  --config benchmarks/apex-postgres.benchmarks.yml \
  --scenario download-raw \
  --profile aspnet-gold-lin \
  --application.framework net10.0 \
  --application.options.collectCounters true \
  --json apex-postgres-download-raw-net10.json
```

Scenarios are `download-raw`, `download-tls`, `upload-raw`, and `upload-tls`.
Change `--application.framework` to `net11.0` for the .NET 11 matrix. Before a
branch is merged, add `--variable branchOrCommit <branch>` so both the
application and PostgreSQL image use that branch. Crank results include
operations per second, transfer MiB/s, latency percentiles, total allocations,
and allocations per operation.
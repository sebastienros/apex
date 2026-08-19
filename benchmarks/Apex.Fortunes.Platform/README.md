# Apex Platform Fortunes

This standalone Platform-style Fortunes application exposes `GET /fortunes`. It reads every
row from `fortune`, adds the standard request-time fortune, sorts by message, and renders the
standard HTML response with RazorSlices HTML encoding.

## Selection

Set all of these environment variables before starting the app:

| Variable | Values |
| --- | --- |
| `DATABASE` | `postgresql`, `mysql`, or `sqlserver` |
| `DRIVER` | PostgreSQL: `apex` or `npgsql`; MySQL: `apex` or `mysqlconnector`; SQL Server: `apex` or `microsoftdatasqlclient` |
| `CONNECTION_STRING` | Connection string for the selected database and driver |

Before announcing readiness, the application loads and validates all 12 canonical database
fortunes plus the request-time fortune, including IDs, exact messages, and sort order.
The Crank config defaults `branchOrCommit` to `main`; override it when benchmarking an
unmerged branch.

`DATABASE_CONNECTIONS` sets each job's pool size. The Crank configuration uses two fewer
connections than database cores except for Npgsql, which uses 256 connections to keep the
remote database busy while operations await network and query latency. `APEX_PIPELINING`
applies only to Apex PostgreSQL and defaults to 64.

## Apex driver strategies

PostgreSQL uses `PgPipelinePool`, a pool-wide prepared statement, and `CollectAsync`; it
keeps `message` as `ReadOnlyMemory<byte>` and renders the `Utf8Fortunes` view on the request
hot path. Npgsql uses a slim data source and reads each message as `byte[]` through the same
UTF-8 view. Drivers that expose text as strings use the string-based `Fortune` model and
`Fortunes` view. MySQL uses the bounded `MySqlPool` query path with its per-connection
prepared-statement cache enabled. SQL Server uses a bounded `MsSqlPool` and a borrowed row reader.

The SQL Server Crank job uses the repaired `aspnet/Benchmarks` image branch and waits for its
`Data imported` marker before starting the application.

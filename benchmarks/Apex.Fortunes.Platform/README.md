# Apex Platform Fortunes

This standalone Platform-style Fortunes application exposes `GET /fortunes`. It reads every
row from `fortune`, adds the standard request-time fortune, sorts by ordinal UTF-8 message
order, and renders the standard HTML response with RazorSlices HTML encoding.

## Selection

Set all of these environment variables before starting the app:

| Variable | Values |
| --- | --- |
| `DATABASE` | `postgresql`, `mysql`, or `sqlserver` |
| `DRIVER` | PostgreSQL: `apex` or `npgsql`; MySQL: `apex` or `mysqlconnector`; SQL Server: `apex` or `microsoftdatasqlclient` |
| `CONNECTION_STRING` | Connection string for the selected database and driver |

`APEX_CONNECTIONS` sets the Apex and corresponding standard-driver pool size: it defaults
to 56 for PostgreSQL and 64 for MySQL and SQL Server. `APEX_PIPELINING` applies only to Apex
PostgreSQL and defaults to 64.

## Apex driver strategies

PostgreSQL uses `PgPipelinePool`, a pool-wide prepared statement, and `CollectAsync`; it
keeps `message` as `ReadOnlyMemory<byte>` on the request hot path. MySQL uses the bounded
`MySqlPool` query path with its per-connection prepared-statement cache enabled. SQL Server
uses a bounded `MsSqlPool` and a borrowed row reader. Neither MySQL nor SQL Server assumes a
PostgreSQL-style pipeline API.

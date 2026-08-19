# Apex Minimal APIs Fortunes

This standalone Minimal APIs benchmark exposes `GET /fortunes`. Every request loads all
`fortune` rows, appends the standard request-time fortune, sorts by message, and renders the
HTML response with RazorSlices HTML encoding.

## Selection

Set the following configuration values as environment variables or equivalent .NET configuration:

| Setting | Values |
| --- | --- |
| `DATABASE` | `postgresql`, `mysql`, or `sqlserver` |
| `DRIVER` | PostgreSQL: `apex` or `npgsql`; MySQL: `apex` or `mysqlconnector`; SQL Server: `apex` or `microsoftdatasqlclient` |
| `CONNECTION_STRING` | Connection string for the selected database |

Invalid, unsupported, or missing selections fail application startup with an explicit error.
The Crank config defaults `branchOrCommit` to `main`; override it when benchmarking an
unmerged branch.

## Apex strategies

PostgreSQL uses `PgPipelinePool`, a pool-wide prepared statement, and `CollectAsync`; its hot
path retains each `message` as `ReadOnlyMemory<byte>` and renders the `Utf8Fortunes` view.
Npgsql uses a slim data source and reads each message as `byte[]` through the same UTF-8 view.
Drivers that expose text as strings use the string-based `Fortune` model and `Fortunes` view.
`DATABASE_CONNECTIONS` defaults to 56 and `APEX_PIPELINING` defaults to 16.

MySQL uses `MySqlPool` with `CachePreparedStatements=true` and a parameterized query, which
activates its per-connection prepared-statement cache. `DATABASE_CONNECTIONS` defaults to 64.

SQL Server uses `MsSqlPool` with a borrowed row reader and no PostgreSQL-style pipelining.
`DATABASE_CONNECTIONS` defaults to 64. Standard drivers use matching maximum pool sizes and
asynchronous commands.

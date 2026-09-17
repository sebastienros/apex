![Apex SQL Client](https://raw.githubusercontent.com/sebastienros/apex/main/assets/Apex.SqlClient/banner.png)

# Apex SQL drivers

Apex is a set of asynchronous .NET 10 and .NET 11 database drivers that implement the
PostgreSQL, MySQL/MariaDB, and Microsoft SQL Server wire protocols directly. The API is a
port of the [Vert.x SQL clients](https://github.com/eclipse-vertx/vertx-sql-client)
for .NET. The direct API remains the lowest-allocation API; each driver also exposes an
optional asynchronous-only ADO.NET adapter.

## Packages

| Package | Database | Documentation |
| --- | --- | --- |
| `Apex.SqlClient` | Shared API, pooling, rows, parameters, and diagnostics | [Common API](src/Apex.SqlClient/README.md) |
| `Apex.PgClient` | PostgreSQL | [PostgreSQL](src/Apex.PgClient/README.md) |
| `Apex.MySqlClient` | MySQL and MariaDB | [MySQL and MariaDB](src/Apex.MySqlClient/README.md) |
| `Apex.MsSqlClient` | Microsoft SQL Server | [Microsoft SQL Server](src/Apex.MsSqlClient/README.md) |
| `Apex.SqlClient.AzureIdentity` | Microsoft Entra authentication for all drivers | [Azure Identity](src/Apex.SqlClient.AzureIdentity/README.md) |

## Install

```bash
dotnet add package Apex.PgClient
# or: Apex.MySqlClient / Apex.MsSqlClient

# Optional Microsoft Entra authentication:
dotnet add package Apex.SqlClient.AzureIdentity
```

## Common API

Every driver offers direct connections and concurrency-safe pools, buffered
queries, backpressured streaming, borrowed row readers, prepared statements,
ordered batches, transactions, cursors, cancellation, and async disposal.

```csharp
await using PgPool pool = PgPool.Create(PgConnectOptions.Parse(
    "postgresql://app:secret@localhost:5432/app"));

SqlRowSet rows = await pool.QueryAsync(
    "SELECT id, message FROM messages WHERE id = $1",
    SqlParameters.Create(42));

foreach (SqlRow row in rows)
{
    Console.WriteLine($"{row.Get<int>("id")}: {row.Get<string>("message")}");
}
```

The placeholder syntax is database-specific: PostgreSQL uses `$1`, MySQL uses
`?`, and Microsoft SQL Server uses `@P1`. Values are sent as protocol parameters and are
never interpolated into SQL.

### ADO.NET adapters

`PgDbConnection`, `MySqlDbConnection`, and `MsSqlDbConnection` are provider-local
`DbConnection` wrappers. Their matching commands, readers, transactions, parameters,
provider factories, data sources, and batches use standard ADO.NET abstractions while
delegating to the existing wire drivers. I/O is asynchronous only; synchronous execution
methods throw `NotSupportedException`.

```csharp
await using var source = new PgDbDataSource("Host=localhost;Database=app;Username=app;Password=secret");
await using DbConnection connection = await source.OpenConnectionAsync();
await using DbCommand command = connection.CreateCommand();
command.CommandText = "SELECT id FROM messages WHERE id = $1";
command.Parameters.Add(new PgDbParameter { ParameterName = "id", Value = 42 });
await using DbDataReader reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
    Console.WriteLine(reader.GetInt32(0));
```

`DbDataSource` instances own and reuse the existing Apex pools. For `ValueTask`,
non-boxing `SqlValue`, and borrowed-reader access, prefer the direct Apex APIs above.
Connections created by a data source remain bound to that source's pool and reject
`ConnectionString` changes. Commands created directly by a data source lease a
connection for execution and return it when execution or its reader completes.
The provider data sources accept `SqlPoolOptions` when pool sizing or lifetime settings
need to be tuned.
`DbBatch` preserves command order and transaction semantics; it does not imply a
single wire round trip when the provider cannot represent the batch natively.

### Transactions

```csharp
await using ISqlConnection connection = await pool.GetConnectionAsync();
await using ISqlTransaction transaction = await connection.BeginTransactionAsync();

await connection.ExecuteAsync(
    "INSERT INTO messages(message) VALUES ($1)",
    SqlParameters.Create("hello"));

await transaction.CommitAsync();
```

Disposing an uncommitted transaction rolls it back. A pooled connection remains
pinned while a transaction, prepared statement, cursor, stream, or row reader
is active.

### Streaming

```csharp
await foreach (SqlRow row in pool.StreamAsync(
    "SELECT id, payload FROM events ORDER BY id",
    pageSize: 128))
{
    Process(row.Get<long>("id"), row.Get<ReadOnlyMemory<byte>>("payload"));
}
```

`StreamAsync` returns rows that remain valid after enumeration advances.
`ExecuteReaderAsync` is the lower-allocation alternative; its current row is
borrowed and must not be retained after the next `ReadAsync`.

## Microsoft Entra authentication

Reuse one `TokenCredential` and the same `UseAzureIdentity` pattern with every
driver:

```csharp
using Apex.MsSqlClient;
using Apex.MySqlClient;
using Apex.PgClient;
using Apex.SqlClient.AzureIdentity;
using Azure.Identity;

var credential = new DefaultAzureCredential();

var postgres = new PgConnectOptions
    { Host = "example.postgres.database.azure.com", Database = "app" }
    .UseAzureIdentity(credential);
var mysql = new MySqlConnectOptions
    { Host = "example.mysql.database.azure.com", Database = "app" }
    .UseAzureIdentity(credential);
var sqlServer = new MsSqlConnectOptions
    { Host = "example.database.windows.net", Database = "app" }
    .UseAzureIdentity(credential);
```

Apex enables the required verified TLS settings and refreshes tokens when a pool
opens new physical connections. PostgreSQL and MySQL usernames are inferred from
the token by default. See the
[Azure Identity package documentation](src/Apex.SqlClient.AzureIdentity/README.md)
for explicit usernames and sovereign-cloud scopes.

## Build and test

```bash
dotnet restore Apex.slnx
dotnet build Apex.slnx --configuration Release --no-restore
dotnet test --solution Apex.slnx --configuration Release --no-build
```

Integration tests use PostgreSQL, MySQL/MariaDB, and Microsoft SQL Server. Docker is
required when running them locally. See [benchmarks](benchmarks/README.md) for the
BenchmarkDotNet suites.

## License

Copyright (c) 2026 Sebastien Ros. Licensed under the [MIT License](LICENSE).
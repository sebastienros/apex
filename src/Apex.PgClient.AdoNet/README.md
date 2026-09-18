# Apex PostgreSQL ADO.NET adapter

Install `Apex.PgClient.AdoNet` for the optional asynchronous ADO.NET surface over
`Apex.PgClient` on .NET 10 and .NET 11.

```bash
dotnet add package Apex.PgClient.AdoNet
```

Types remain in the `Apex.PgClient` namespace: `PgDbConnection`, `PgDbCommand`,
`PgDbDataReader`, `PgDbTransaction`, `PgDbParameter`, `PgDbParameterCollection`,
`PgDbProviderFactory`, `PgDbDataSource`, and the `PgDbBatch` family.

```csharp
using Apex.PgClient;

await using var source = new PgDbDataSource("Host=localhost;Database=app;Username=app");
await using var command = source.CreateCommand("SELECT $1::int4");
command.Parameters.Add(new PgDbParameter { Value = 42 });
await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
    Console.WriteLine(reader.GetInt32(0));
```

`PgDbDataSource` uses `PgPool` and accepts optional `SqlPoolOptions`. Reader result
boundaries use the native protocol implementation. Server failures become
`ApexDbException : DbException`, retaining `PgException` as the inner exception and
exposing its SQLSTATE and transient classification.

See the [shared adapter contract](../Apex.SqlClient.AdoNet/README.md) for asynchronous
I/O requirements, pooling, batching, exception semantics, and type-metadata limits.

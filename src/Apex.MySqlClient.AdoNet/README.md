# Apex MySQL and MariaDB ADO.NET adapter

Install `Apex.MySqlClient.AdoNet` for the optional asynchronous ADO.NET surface over
`Apex.MySqlClient` on .NET 10 and .NET 11.

```bash
dotnet add package Apex.MySqlClient.AdoNet
```

Types remain in the `Apex.MySqlClient` namespace: `MySqlDbConnection`, `MySqlDbCommand`,
`MySqlDbDataReader`, `MySqlDbTransaction`, `MySqlDbParameter`,
`MySqlDbParameterCollection`, `MySqlDbProviderFactory`, `MySqlDbDataSource`, and the
`MySqlDbBatch` family.

```csharp
using Apex.MySqlClient;

await using var source = new MySqlDbDataSource("Server=localhost;Database=app;User ID=app");
await using var command = source.CreateCommand("SELECT ?");
command.Parameters.Add(new MySqlDbParameter { Value = 42 });
await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
    Console.WriteLine(reader.GetInt32(0));
```

`MySqlDbDataSource` uses `MySqlPool` and accepts optional `SqlPoolOptions`. Reader
result boundaries use the native protocol implementation. Server failures become
`ApexDbException : DbException`, retaining `MySqlException` as the inner exception and
exposing its SQLSTATE and server error number (`ErrorCode`).

See the [shared adapter contract](../Apex.SqlClient.AdoNet/README.md) for asynchronous
I/O requirements, pooling, batching, exception semantics, and type-metadata limits.

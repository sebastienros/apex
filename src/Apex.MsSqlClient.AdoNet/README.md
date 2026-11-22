# Apex SQL Server ADO.NET adapter

Install `Apex.MsSqlClient.AdoNet` for the optional asynchronous ADO.NET surface over
`Apex.MsSqlClient` on .NET 10 and .NET 11.

```bash
dotnet add package Apex.MsSqlClient.AdoNet
```

Types remain in the `Apex.MsSqlClient` namespace: `MsSqlDbConnection`, `MsSqlDbCommand`,
`MsSqlDbDataReader`, `MsSqlDbTransaction`, `MsSqlDbParameter`,
`MsSqlDbParameterCollection`, `MsSqlDbProviderFactory`, `MsSqlDbDataSource`, and the
`MsSqlDbBatch` family.

```csharp
using Apex.MsSqlClient;

await using var source = new MsSqlDbDataSource("Server=localhost;Database=app;User ID=app");
await using var command = source.CreateCommand("SELECT @P1");
command.Parameters.Add(new MsSqlDbParameter { Value = 42 });
await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
    Console.WriteLine(reader.GetInt32(0));
```

`MsSqlDbDataSource` uses `MsSqlPool` and accepts optional `SqlPoolOptions`. Reader
result boundaries use the native protocol implementation. Server failures become
`ApexDbException : DbException`, retaining `MsSqlException` as the inner exception and
exposing its server error number (`ErrorCode`). Numeric state, severity, procedure,
and other server details remain available on that inner exception.

See the [shared adapter contract](../Apex.SqlClient.AdoNet/README.md) for asynchronous
I/O requirements, pooling, batching, exception semantics, and type-metadata limits.

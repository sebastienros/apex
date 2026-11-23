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

Connection strings may use ADO.NET `Key=Value;` syntax (values quoted with `'` or `"`,
the quote doubled to include it) as well as the libpq keyword strings and
`postgres://` URIs accepted by `PgConnectOptions.Parse`. The ADO.NET syntax is
translated by this adapter; the native parser keeps libpq semantics.

`PgDbDataSource` uses `PgPool` and accepts optional `SqlPoolOptions`. Reader result
boundaries use the native protocol implementation.

Command text may hold several statements separated by semicolons when the command has
no parameters. Such text is sent with the simple query protocol, which returns each
statement's result in text format. The adapter only counts statements to choose the
protocol; it never splits or rewrites SQL. The limits are PostgreSQL's:

- Parameters (`$1`, ...) require a single statement, since the extended protocol and
  `PrepareAsync` accept only one.
- Statements in one simple query run in one implicit transaction unless the text
  contains its own transaction control. A failure stops the remaining statements.
- The statement count assumes `standard_conforming_strings` is on (the default).
- Text-format values are parsed as the server formats them, so session settings that
  change the text output (a non-ISO `DateStyle` or `IntervalStyle`, for example) can
  make dates, timestamps and intervals unreadable. Single statements return binary
  values and are not affected.
- `COPY ... FROM STDIN` and `COPY ... TO STDOUT` are not supported in command text,
  alone or with other statements; use the native `PgConnection` copy APIs. Server failures become
`ApexDbException : DbException`, retaining `PgException` as the inner exception and
exposing its SQLSTATE and transient classification.

See the [shared adapter contract](../Apex.SqlClient.AdoNet/README.md) for asynchronous
I/O requirements, pooling, batching, exception semantics, and type-metadata limits.

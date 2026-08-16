![Apex.PgClient](https://raw.githubusercontent.com/sebastienros/apex/main/assets/Apex.PgClient/banner.png)

# Apex PostgreSQL client

`Apex.PgClient` is a direct PostgreSQL protocol driver for .NET 10.

## Connect and pool

Connection keyword strings, `postgres://` and `postgresql://` URIs, explicit
options, and the standard `PG*` environment variables are supported.

```csharp
PgConnectOptions options = PgConnectOptions.Parse(
    "postgresql://app:secret@localhost:5432/app?sslmode=require");

await using PgConnection connection = await PgClient.ConnectAsync(options);
SqlRowSet rows = await connection.QueryAsync("SELECT current_database()");
```

Use a pool for concurrent application work:

```csharp
await using PgPool pool = PgPool.Create(
    PgConnectOptions.FromEnvironment(),
    new SqlPoolOptions { MaxSize = 20 });

SqlRowSet rows = await pool.QueryAsync(
    "SELECT id, name FROM users WHERE id = $1",
    SqlParameters.Create(42));
```

## Supported features

- Simple and extended query protocols with ordered automatic pipelining.
- Prepared statements, ordered batches, transactions, and server-side cursors.
- Buffered `SqlRowSet`, safe `StreamAsync`, and borrowed `ISqlRowReader` results.
- Query cancellation through a PostgreSQL cancel request.
- TLS modes, direct TLS negotiation, certificate validation, client
  certificates, and channel binding.
- SCRAM-SHA-256, SCRAM-SHA-256-PLUS, MD5, and clear-text password authentication.
- HTTP CONNECT and SOCKS4/SOCKS5 proxy connections.
- `LISTEN`/`NOTIFY`, notices, and asynchronous notifications.
- Startup properties, bounded string caching, prepared-statement caching, and
  NativeAOT-compatible options and codecs.

## Prepared statements and cursors

```csharp
await using ISqlConnection connection = await pool.GetConnectionAsync();
await using ISqlPreparedStatement statement = await connection.PrepareAsync(
    "SELECT id, message FROM events WHERE id > $1 ORDER BY id");

await using ISqlCursor cursor = await statement.OpenCursorAsync(
    SqlParameters.Create(100));

while (await cursor.ReadAsync(64) is { Count: > 0 } page)
{
    foreach (SqlRow row in page)
    {
        Consume(row);
    }
}
```

## Explicit types and batches

Use `PgParameters` when PostgreSQL needs an explicit parameter type, including
typed `NULL`, JSONB, and arrays:

```csharp
using JsonDocument document = JsonDocument.Parse("""{"name":"Apex"}""");
SqlRowSet rows = await connection.QueryTypedAsync(
    "SELECT $1::jsonb ->> 'name', $2::uuid[]",
    PgParameters.Create(
        PgParameter.Create(PgType.Jsonb, document),
        PgParameter.Create(PgType.UuidArray, ids)));
```

`PgBatch` sends all commands through one extended-protocol synchronization
point and buffers the ordered results:

```csharp
PgBatch batch = new();
batch.Add("INSERT INTO events (data) VALUES ($1)",
    PgParameters.Create(PgParameter.Create(PgType.Jsonb, document)));
batch.Add("SELECT count(*)::int8 FROM events");

PgBatchReader results = await connection.ExecuteBatchAsync(batch);
await results.NextResultAsync();
long count = results.Current[0].Get<long>(0);
```

## Transactions and binary COPY

Provider-specific transaction options include isolation, read-only and
deferrable modes. `PgTransaction` also exposes savepoints. Connections can be
explicitly enlisted in a `System.Transactions.Transaction` with
`EnlistTransactionAsync`.

```csharp
await using PgTransaction transaction = await connection.BeginPgTransactionAsync(
    new PgTransactionOptions { IsolationLevel = PgIsolationLevel.Serializable });
await transaction.CreateSavepointAsync("before_update");
```

Binary COPY uses the same typed parameter codecs:

```csharp
await using PgBinaryImporter importer = await connection.BeginBinaryImportAsync(
    "COPY events (id, data) FROM STDIN (FORMAT BINARY)");
await importer.StartRowAsync();
await importer.WriteAsync(PgParameter.Create(PgType.Uuid, id));
await importer.WriteAsync(PgParameter.Create(PgType.Jsonb, document));
await importer.CompleteAsync();
```

## Custom PostgreSQL types

Call `ReloadTypesAsync` after creating extensions or custom types, then register
a binary codec in the connection's `TypeRegistry`. A registry can also be
provided through `PgConnectOptions.TypeRegistry` and shared by a pool.

## Notifications

```csharp
await using PgSubscriber subscriber = await PgClient.SubscribeAsync(options);
subscriber.Notification += notification =>
    Console.WriteLine($"{notification.Channel}: {notification.Payload}");

await subscriber.SubscribeAsync("events");
```

## Type support

The driver supports binary and text representations for booleans, signed
integers, floating-point values, `numeric`, strings, UUIDs, dates and times,
intervals, `bytea`, JSON/JSONB, geometric values, network addresses, MAC
addresses, bit strings, money, and one-dimensional arrays. Provider-specific
types include `PgNumeric`, `PgInterval`, `PgTimeWithTimeZone`, `PgMoney`,
`PgInet`, `PgCidr`, and the `PgPoint`/line/path/polygon/circle family.

Common alternatives such as `BigInteger`, `Int128`, `UInt128`, `Half`,
`IPAddress`, `PhysicalAddress`, `BitArray`, `JsonElement`, `DateOnly`,
`TimeOnly`, and `DateTimeOffset` are available through typed getters and
parameters. Date and timestamp infinities map to the corresponding .NET minimum
and maximum values.

Multidimensional arrays are not supported. Unknown custom values can be read as
text, but unknown binary OIDs fail explicitly. `xml`, `oid`, and `void` are not
supported.

## Server compatibility

The integration matrix covers PostgreSQL 14, 16, and 18. Direct TLS negotiation
requires PostgreSQL 17 or later. Set `POSTGRES_IMAGE` to select a local test
image.
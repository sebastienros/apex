![Apex.MySqlClient](https://raw.githubusercontent.com/sebastienros/apex/main/assets/Apex.MySqlClient/banner.png)

# Apex MySQL and MariaDB client

`Apex.MySqlClient` is a direct MySQL protocol driver for .NET 10 and .NET 11.

## Connect and pool

The driver accepts `mysql://` and `mariadb://` URIs, keyword connection strings,
explicit options, and standard `MYSQL_*` environment variables.

```csharp
MySqlConnectOptions options = MySqlConnectOptions.Parse(
    "mysql://app:secret@localhost:3306/app?sslMode=required");

await using MySqlPool pool = MySqlPool.Create(
    options,
    new SqlPoolOptions { MaxSize = 20 });

SqlRowSet rows = await pool.QueryAsync(
    "SELECT id, message FROM messages WHERE id = ?",
    SqlParameters.Create(42));
```

## Supported features

- Text and binary query protocols, prepared statements, ordered batches,
  transactions, and paged cursors.
- Buffered `SqlRowSet`, safe `StreamAsync`, and borrowed `ISqlRowReader` results.
- Multiple result sets and optional multi-statement execution.
- `mysql_native_password`, `caching_sha2_password`, `sha256_password`, and
  opt-in `mysql_clear_password` authentication.
- Preferred or required TLS, CA and identity verification, client certificates,
  certificate callbacks, and SHA-2 RSA key exchange.
- Active query cancellation with `KILL QUERY`, connection reset, ping, session
  variables, connection attributes, and bounded string caching.
- Matched-row or changed-row semantics, last insert IDs, warning counts, server
  status, and server information messages.
- Opt-in `LOAD DATA LOCAL INFILE` and NativeAOT-compatible options and codecs.

## Prepared batches

```csharp
await using ISqlConnection connection = await pool.GetConnectionAsync();
await using ISqlPreparedStatement insert = await connection.PrepareAsync(
    "INSERT INTO messages(message) VALUES (?)");

IReadOnlyList<SqlCommandResult> results = await insert.ExecuteBatchAsync(
    [SqlParameters.Create("first"), SqlParameters.Create("second")]);
```

MySQL returns complete result sets rather than fetch portals. `OpenCursorAsync`
pages a backpressured wire reader and pins the connection until the cursor is
exhausted or disposed. `PipeliningLimit` controls how many independent commands
may be in flight; its conservative default is one.

## Security and cancellation

`MySqlSslMode.Preferred` negotiates TLS when offered. `Required`, `VerifyCa`, and
`VerifyIdentity` enforce progressively stronger checks. SHA-2 full
authentication sends a clear password only over TLS; without TLS, configure a
trusted PEM server key or explicitly enable `AllowPublicKeyRetrieval`.

Cancellation uses a short-lived authenticated connection to issue `KILL QUERY`.
If cancellation cannot be delivered, the physical command connection is closed
instead of being returned to a pool in an uncertain protocol state.

`LOAD DATA LOCAL INFILE` is disabled by default. Enable it only for a trusted
server because the server selects the requested local path.

## Type support

Signed and unsigned integers, `YEAR`, `BIT`, floating point, `DECIMAL`, strings,
`ENUM`, `SET`, binary/blob values, dates, times, timestamps, JSON, geometry, and
vector payloads are supported in text and binary rows. Provider-specific values
include `MySqlDecimal`, `MySqlServerVersion`, `MySqlColumnMetadata`, and
`MySqlCommandInfo`.

Typed access includes `BigInteger`, `Int128`, `UInt128`, `Half`, `BitArray`,
`IPAddress`, `PhysicalAddress`, `JsonElement`, `DateOnly`, `TimeOnly`, and
`TimeSpan`. Zero dates can throw, map to `null`, or map to the corresponding .NET
minimum through `ZeroDateBehavior`.

## Server compatibility

The active integration matrix covers MySQL 8.4 and 9.6 and MariaDB 11.8. Set
`MYSQL_IMAGE` to select a local test image.
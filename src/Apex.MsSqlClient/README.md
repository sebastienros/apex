![Apex.MsSqlClient](https://raw.githubusercontent.com/sebastienros/apex/main/assets/Apex.MsSqlClient/banner.png)

# Apex Microsoft SQL Server client

`Apex.MsSqlClient` is a direct Tabular Data Stream (TDS) driver for .NET 10 and .NET 11.

## Connect and pool

Standard Microsoft SQL Server keyword strings, `sqlserver://` URIs, and explicit options
are supported.

```csharp
MsSqlConnectOptions options = MsSqlConnectOptions.Parse(
    "Server=localhost;Database=app;User ID=sa;Password=secret;Encrypt=True");

await using MsSqlPool pool = MsSqlPool.Create(
    options,
    new SqlPoolOptions { MaxSize = 20 });

SqlRowSet rows = await pool.QueryAsync(
    "SELECT id, message FROM messages WHERE id = @P1",
    SqlParameters.Create(42));
```

## Supported features

- TDS 7.x and TDS 8.0 login, routing redirects, SQL authentication, and packet
  payloads that span network reads.
- Parameterized commands through `sp_executesql`.
- Prepared handles through `sp_prepexec`, `sp_execute`, and `sp_unprepare`.
- Ordered batches, transactions, buffered results, safe streaming, borrowed row
  readers, and paged cursors.
- Query cancellation through TDS `ATTENTION` with acknowledgement draining.
- Multiple result sets, output metadata, affected rows, server messages, and
  structured `MsSqlException` details.
- Configurable packet size, application/workstation identity, bounded string
  caching, and NativeAOT-compatible options and codecs.

## Encryption

| Mode | Behavior |
| --- | --- |
| `Disable` | No TLS; credentials are not protected on the wire. |
| `Optional` | Use full-session TLS when the server offers it. |
| `Require` | Require TDS 7.x PRELOGIN-negotiated TLS. |
| `Strict` | Require TDS 8.0 TLS before PRELOGIN with `tds/8.0` ALPN. |

Certificate validation is enabled by default. `TrustServerCertificate` is an
explicit opt-out for controlled environments. A TLS host name, client
certificates, revocation policy, and custom validation callback can be supplied.

Only SQL authentication and federated access tokens are currently supported.
Integrated/Windows authentication is not supported.

## Authentication

`AuthenticationProvider` is resolved once per physical connection, including
after each routing redirect, so rotated secrets and short-lived tokens are
refreshed automatically.

```csharp
MsSqlConnectOptions options = new()
{
    Host = "contoso.database.windows.net",
    Database = "app",
    AuthenticationProvider = async cancellationToken => new SqlAuthenticationCredential(
        await GetAccessTokenAsync(cancellationToken),
        SqlAuthenticationMethod.BearerToken),
};
```

A `BearerToken` credential authenticates with the TDS federated authentication
Security Token library: `FEDAUTHREQUIRED` is advertised in `PRELOGIN`, the token
is carried in the `LOGIN7` `FEDAUTH` feature extension, and a `FEDAUTHINFO`
request is answered with a federated authentication token message. Bearer
credentials require `Encrypt=true` or `Encrypt=strict` with
`TrustServerCertificate=false`. A `Password` credential keeps the standard SQL
authentication login unchanged.

## Transactions and cancellation

```csharp
await using ISqlConnection connection = await pool.GetConnectionAsync();
await using ISqlTransaction transaction = await connection.BeginTransactionAsync();

await connection.ExecuteAsync(
    "UPDATE accounts SET balance = balance - @P1 WHERE id = @P2",
    SqlParameters.Create(10m, 1));
await transaction.CommitAsync();
```

One physical connection processes one command at a time; MARS is not supported.
Use `MsSqlPool` for concurrency. Cancellation sends `ATTENTION` on the command
connection and drains the acknowledgement and final `DONE` before reuse.

## Type support

The driver supports `bit`, integer and floating-point families,
`decimal`/`numeric`, money, UUIDs, dates and times, character and Unicode text,
XML, Microsoft SQL Server 2025 JSON, binary/image values, and opaque UDT payloads.

Typed access includes `BigInteger`, `Int128`, `UInt128`, `Half`, `Guid`,
`DateOnly`, `TimeOnly`, `TimeSpan`, `DateTimeOffset`, `IPAddress`, `BitArray`,
`PhysicalAddress`, `char`, and `char[]`. Legacy and UTF-8 code-page collations
are decoded through the platform code-page provider.

Table-valued parameters, interpreted spatial/CLR UDTs, `sql_variant`, bulk copy,
and automatic prepared-statement caching are not supported.

## Server compatibility

The integration matrix covers Microsoft SQL Server 2019, 2022, and 2025. Strict TDS 8.0
encryption requires a compatible server configuration and certificate. Set
`MSSQL_IMAGE` to select a local test image.
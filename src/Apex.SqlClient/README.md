![Apex.SqlClient](https://raw.githubusercontent.com/sebastienros/apex/main/assets/Apex.SqlClient/banner.png)

# Apex SQL client API contract

## Ownership and lifetime

- `ISqlClient`, `ISqlConnection`, `ISqlPool`, `ISqlPreparedStatement`, `ISqlCursor`, and `ISqlTransaction` are async-disposable.
- A pooled connection lease must remain alive while its transaction, prepared statement, cursor, or stream is in use. The pool pins the physical connection until lease-derived resources complete.
- `SqlRowSet` and its rows own managed storage and remain valid after the originating connection is released.
- Streaming rows are consumed in order. Implementations may reuse internal transport buffers, but a yielded `SqlRow` remains a safe managed value.

## Concurrency

- Pools are safe for concurrent callers.
- A connection preserves command submission order. The configured driver pipelining limit controls the number of in-flight commands; it does not reorder results.
- PostgreSQL can pipeline ordered requests. Microsoft SQL Server does not implement MARS
  or concurrent TDS requests: `Apex.MsSqlClient` serializes commands on each
  physical connection with one command in flight. Use a pool for concurrency.
- Transactions, prepared statements, and cursors are bound to their originating
  connection. Prepared-statement calls may be submitted concurrently and are
  serialized in submission order; transaction and cursor operations must not be
  used concurrently unless their API explicitly states otherwise.

## Cancellation

- Every one-shot asynchronous operation accepts a final `CancellationToken`.
- Cancellation before protocol submission prevents the command from being sent.
- Cancellation after PostgreSQL submission sends a PostgreSQL cancellation request and drains the response so the connection can be reused only after returning to idle state.
- Cancellation after Microsoft SQL Server submission sends TDS `ATTENTION` on the same
  connection. Apex continues reading until the server's attention
  acknowledgement and final `DONE` token are drained. Only then can the
  connection be reused; an invalid or timed-out drain breaks the connection.
- Cancellation after MySQL submission issues `KILL QUERY` from a short-lived authenticated
  connection by default. If the kill cannot be delivered, the physical command connection is
  closed and cannot return to a pool. Disabling active cancellation waits for and drains the
  response before reporting cancellation.
- Commit and rollback use cancellation only before submission. Once sent, they complete deterministically to avoid reporting cancellation after a transaction may already have committed.

## Query results

- `QueryAsync` buffers a `SqlRowSet`; `StreamAsync` is the backpressured alternative.
- `StreamAsync` yields lifetime-safe rows backed by owned pages. A yielded row
  remains usable after enumeration advances. `ExecuteReaderAsync` returns the
  lower-allocation borrowed `ISqlRowReader`; its current row is valid only until
  the next `ReadAsync` or disposal.
- `SqlParameters` stores ordered `SqlValue` instances. Common scalar `SqlValue` conversions avoid boxing at parameter construction.
- PostgreSQL uses `$1` placeholders, MySQL uses `?`, and Microsoft SQL Server uses `@P1`,
  `@P2`, and so on. Microsoft SQL Server parameters are sent with `sp_executesql`, not
  interpolated into SQL.
- Column lookup is ordinal and case-sensitive.
- Mapping and collection helpers execute user delegates synchronously for each buffered or streamed row.
- MySQL affected rows use matched-row semantics by default. `UseAffectedRows` switches to changed
  rows. `SqlCommandResult` carries the last insert identifier, status flags, and warning count;
  `MySqlConnection.LastCommandInfo` additionally exposes the server information string.

## Errors and diagnostics

- Database errors derive from `SqlClientException`; PostgreSQL errors expose
  SQLSTATE and structured server fields through `PgException`, MySQL errors
  expose the numeric server code and SQLSTATE through `MySqlException`, and SQL
  Server errors expose number, state, class, server, procedure, and line through
  `MsSqlException`.
- Activities and metrics never include passwords or parameter values.
- A physical connection is never returned to a pool while PostgreSQL reports an active or failed transaction.
- A physical MySQL connection is never returned while a transaction is active,
  autocommit is disabled, or cancellation left the session unsynchronized.
- A Microsoft SQL Server physical connection is not returned while a transaction,
  response, or `ATTENTION` drain is active.

## Microsoft SQL Server connection contract

- `Apex.MsSqlClient` is a direct TDS implementation and has no runtime
  Microsoft.Data.SqlClient dependency.
- `MsSqlConnectOptions.Parse` accepts standard key/value strings and
  `sqlserver://` URIs. Unknown keys fail rather than being silently ignored.
- `ConnectTimeout` is one deadline for TCP, TLS, PRELOGIN, routing redirects,
  LOGIN7, and authentication rather than a TCP-only timeout.
- `StringCacheCapacity` (default `1024`) and
  `StringCacheMaximumByteLength` (default `128`) bound the per-connection,
  two-hit cache used for repeated UTF-16, UTF-8, and code-page strings. Zero
  disables it, and disposal clears it.
- SQL authentication is supported. Integrated/Windows authentication, Entra ID,
  and access tokens are not.
- `EncryptionMode` values are `Disable`, `Optional`, `Require`, and `Strict`.
  `Require` uses TDS 7.x PRELOGIN TLS negotiation. `Strict` performs TLS before
  PRELOGIN as TDS 8.0 and requests the `tds/8.0` ALPN.
- Certificate validation is enabled by default. `TrustServerCertificate` is an
  explicit opt-out intended for controlled test environments; `TlsHostName`,
  client certificates, revocation mode, and a validation callback are exposed.
- Microsoft SQL Server row readers consume TDS packet payloads incrementally and apply
  pull backpressure at each borrowed row. `StreamAsync` emits rows from every
  result set in order and preserves the matching metadata on each safe row.

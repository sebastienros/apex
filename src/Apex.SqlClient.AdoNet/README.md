# Apex asynchronous ADO.NET adapters

`Apex.SqlClient.AdoNet` is an optional shared adapter package for .NET 10 and .NET 11.
Applications normally install `Apex.PgClient.AdoNet`, `Apex.MySqlClient.AdoNet`, or
`Apex.MsSqlClient.AdoNet`, which bring in this package and the corresponding native
driver. The `Apex.SqlClient` namespace and public `ApexDb*` names are unchanged.

Native packages remain independent of ADO.NET. Adapters reuse the existing protocol
readers via optional neutral result-set capabilities. The ordinary native API does
not route operations through an ADO.NET wrapper.

The opt-in result-boundary state is allocated only when requested. Ordinary native
streaming readers still carry a single nullable state reference, execute null
checks, and retain the `_readSignaled` read-notification correctness guard. They
allocate no result-boundary state, events, or tasks and add no per-row/result locks
or PostgreSQL command-tag parsing on the ordinary path. This is not a claim of
literal zero overhead.

## Behavior

Use asynchronous open, prepare, execute, read, next-result, commit, and rollback
methods. Synchronous I/O methods throw `NotSupportedException`; synchronous close
and disposal are supported for cleanup, but asynchronous disposal is preferred.

Commands support text SQL and input parameters. Parameter metadata is inferred
from `Value` unless `DbType` is set (Dapper and EF Core set it on every parameter):

- An explicit `DbType` is honored where a native value kind represents it. Integers
  and enums are converted to the declared integer width, numbers to the declared
  `Single`, `Double` or `Decimal` (`Currency`, `VarNumeric`), `char` and `char[]` to
  strings, `DateTime` to `Date` and `DateOnly` to `DateTime`/`DateTime2`, and a UTC
  `DateTime` to `DateTimeOffset`.
- String variants (`AnsiString`, `StringFixedLength`, `Xml`, ...) all send a string,
  and `Binary` sends bytes. `Size`, `Precision` and `Scale` are not sent.
- `Byte`, `SByte` and the unsigned integer types have no distinct native kind: the
  converted value is sent as the provider would send an inferred one. `Time` keeps a
  `TimeSpan` value's native mapping. `Object` means inference.
- A value the declared type cannot represent (for example a string for `Int32`, or a
  `double` for an integer type) throws `InvalidCastException`; an out-of-range integer
  throws `OverflowException`. Null and `DBNull` values are always sent as NULL.

Output parameters and schema-only/key-info behaviors are not supported.

Readers report the CLR type that `GetValue` returns: `GetFieldType`,
`GetDataTypeName`, `GetColumnSchema` and `GetSchemaTable` map each provider's
column types (for example PostgreSQL `numeric` is `PgNumeric`, MySQL `DECIMAL` is
`MySqlDecimal`, SQL Server `decimal` is `decimal`). Types without a mapping report
`object` (or `string` for PostgreSQL text-format columns) and their numeric type id
as the name. The schema contains the column name, ordinal, type and type name only;
base table, key, nullability and size details are not reported. Because synchronous
`Read()` is not supported, synchronous metadata consumers such as `DataTable.Load` and
`DbDataAdapter.Fill` cannot be used.

`ExecuteNonQueryAsync` returns the reader's `RecordsAffected`, summed over every
statement of the command text. For a single PostgreSQL statement that returns no rows
(INSERT, UPDATE, DELETE, MERGE, DDL, ...) it uses the native command path instead of a
reader, with the same result.

`PrepareAsync` binds the statement to the connection that is open at the time. Closing
the connection releases it, and the command then executes unprepared until it is
prepared again.

Data sources reuse native pools. Source-created commands open and return their
connection automatically. Connections created by a data source stay pool-bound and
reject connection-string changes. Batches preserve order and transactions but do
not promise a single round trip.

## Timeouts and cancellation

`CommandTimeout` (default 30 seconds, 0 for none) bounds each operation separately:
the execute call and every later `ReadAsync` and `NextResultAsync`, not the time the
caller spends between them. `DbBatch.Timeout` applies the same way to each command
of the batch, for every execution method; it is not a deadline for the whole batch.

When the timeout elapses, the operation fails with `ApexDbException` whose
`InnerException` is a `TimeoutException` and whose `IsTransient` is true, as Npgsql
reports timeouts. Cancelling the caller's `CancellationToken`, or calling
`DbCommand.Cancel`, still fails with `OperationCanceledException`.

## Errors

At adapter operation boundaries, native `SqlClientException` errors are translated
to `ApexDbException : System.Data.Common.DbException`. The original native exception
is retained unchanged as `InnerException`, including its stack trace and
provider-specific fields.

- PostgreSQL supplies `SqlState` and its native `IsTransient` classification.
- MySQL supplies `SqlState` and its server error number as `ErrorCode`.
- SQL Server supplies its server error number as `ErrorCode`. Its numeric state
  remains on `MsSqlException`, not in `SqlState`.

For errors without a provider error number, `ErrorCode` is the native `HResult`.
`IsTransient` is false when the native provider has no transient classification;
it is not a new retry policy. Command timeouts become `ApexDbException` as described
above; cancellation and all other non-`SqlClientException` failures pass through
unchanged. Catch `DbException` for ADO.NET errors and inspect its inner
exception for provider details, not another driver's exception type.

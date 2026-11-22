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

Commands support text SQL and input parameters, with metadata inferred from `Value`.
Explicit `DbType`, output parameters, and schema-only/key-info behavior are not
supported. `GetFieldType` reports `object`; these adapters do not promise the complete
type-metadata behavior of other ADO.NET providers.

Data sources reuse native pools. Source-created commands open and return their
connection automatically. Connections created by a data source stay pool-bound and
reject connection-string changes. Batches preserve order and transactions but do
not promise a single round trip.

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
it is not a new retry policy. Cancellation and all non-`SqlClientException` failures
pass through unchanged. Catch `DbException` for ADO.NET errors and inspect its inner
exception for provider details, not another driver's exception type.

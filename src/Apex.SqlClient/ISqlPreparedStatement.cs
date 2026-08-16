namespace Apex.SqlClient;

/// <summary>A server-side prepared statement bound to one connection.</summary>
public interface ISqlPreparedStatement : IAsyncDisposable
{
    string Sql { get; }

    ValueTask<SqlRowSet> QueryAsync(
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default);

    ValueTask<SqlCommandResult> ExecuteAsync(
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<SqlCommandResult>> ExecuteBatchAsync(
        IReadOnlyList<SqlParameters> batch,
        CancellationToken cancellationToken = default);

    ValueTask<ISqlCursor> OpenCursorAsync(
        SqlParameters parameters = default,
        int fetchSize = 50,
        CancellationToken cancellationToken = default);

    ValueTask<ISqlRowReader> ExecuteReaderAsync(
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<SqlRow> StreamAsync(
        SqlParameters parameters = default,
        int fetchSize = 50,
        CancellationToken cancellationToken = default);
}

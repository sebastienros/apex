namespace Apex.SqlClient;

/// <summary>A server-side prepared statement bound to one connection.</summary>
public interface ISqlPreparedStatement : IAsyncDisposable
{
    string Sql { get; }

    ValueTask<SqlRowSet> QueryAsync(
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Projects each result row into caller-owned state while the row is valid.
    /// The collector must not retain the supplied row.
    /// </summary>
    async ValueTask<TState> CollectAsync<TState>(
        TState state,
        Action<TState, SqlRow> collector,
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collector);
        var rows = await QueryAsync(parameters, cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            collector(state, row);
        }

        return state;
    }

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

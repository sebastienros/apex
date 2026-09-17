using System.Runtime.CompilerServices;
using Apex.SqlClient;

namespace Apex.PgClient;

internal sealed class PgPreparedStatement : ISqlPreparedStatement, IApexAdoPreparedStatement
{
    private readonly PgConnection _connection;
    private readonly string _name;
    private readonly string _operation;
    private readonly IReadOnlyList<SqlColumn> _columns;
    private bool _disposed;

    public PgPreparedStatement(
        PgConnection connection,
        string name,
        string sql,
        string operation,
        IReadOnlyList<SqlColumn> columns)
    {
        _connection = connection;
        _name = name;
        _operation = operation;
        _columns = columns;
        Sql = sql;
    }

    public string Sql { get; }

    public ValueTask<SqlRowSet> QueryAsync(
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _connection.ExecutePreparedAsync(
            _name,
            _operation,
            _columns,
            parameters,
            cancellationToken);
    }

    public ValueTask<TState> CollectAsync<TState>(
        TState state,
        Action<TState, SqlRow> collector,
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(collector);
        return _connection.ExecutePreparedCollectAsync(
            _name,
            _operation,
            _columns,
            state,
            collector,
            parameters,
            cancellationToken);
    }

    public async ValueTask<SqlCommandResult> ExecuteAsync(
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var rows =
                        await _connection.ExecutePreparedAsync(
                            _name,
                            _operation,
                            _columns,
                            parameters,
                            cancellationToken).ConfigureAwait(false);
        return new SqlCommandResult(rows.AffectedRows, rows.CommandTag);
    }

    public async ValueTask<IReadOnlyList<SqlCommandResult>> ExecuteBatchAsync(
        IReadOnlyList<SqlParameters> batch,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(batch);
        Task<SqlCommandResult>[] pending = new Task<SqlCommandResult>[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            pending[i] = ExecuteAsync(batch[i], cancellationToken).AsTask();
        }

        return await Task.WhenAll(pending).ConfigureAwait(false);
    }

    public async ValueTask<ISqlCursor> OpenCursorAsync(
        SqlParameters parameters = default,
        int fetchSize = 50,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fetchSize);

        cancellationToken.ThrowIfCancellationRequested();
        return await _connection.CreateCursorAsync(
          _name,
          parameters,
          fetchSize,
          cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ISqlRowReader> ExecuteReaderAsync(
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _connection.ExecutePreparedReaderAsync(
          _name,
          parameters,
          cancellationToken);
    }

    internal ValueTask<ISqlRowReader> ExecuteAdoReaderAsync(
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _connection.ExecuteAdoPreparedReaderAsync(
          _name,
          parameters,
          cancellationToken);
    }

    ValueTask<ISqlRowReader> IApexAdoPreparedStatement.ExecuteAdoReaderAsync(
        SqlParameters parameters,
        CancellationToken cancellationToken) =>
        ExecuteAdoReaderAsync(parameters, cancellationToken);

    public async IAsyncEnumerable<SqlRow> StreamAsync(
        SqlParameters parameters = default,
        int fetchSize = 50,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fetchSize);

        await foreach (var row in _connection.StreamPreparedRowsAsync(
                         _name,
                         parameters,
                         fetchSize,
                         cancellationToken).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _connection.ClosePreparedAsync(_name).ConfigureAwait(false);
    }
}

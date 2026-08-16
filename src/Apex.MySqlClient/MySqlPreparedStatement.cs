using System.Runtime.CompilerServices;
using Apex.MySqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MySqlClient;

/// <summary>A statement prepared with COM_STMT_PREPARE and bound to one connection.</summary>
internal sealed class MySqlPreparedStatement : ISqlPreparedStatement
{
    private readonly MySqlConnection _connection;
    private readonly MySqlStatement _statement;
    private bool _disposed;

    internal MySqlPreparedStatement(MySqlConnection connection, MySqlStatement statement)
    {
        _connection = connection;
        _statement = statement;
    }

    public string Sql => _statement.Sql;

    /// <summary>Gets the MySQL metadata of the columns reported when the statement was prepared.</summary>
    internal IReadOnlyList<MySqlColumnMetadata> Columns => _statement.Columns;

    public ValueTask<SqlRowSet> QueryAsync(
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _connection.ExecutePreparedAsync(_statement, parameters, cancellationToken);
    }

    public async ValueTask<SqlCommandResult> ExecuteAsync(
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await _connection
          .ExecutePreparedDetailedAsync(_statement, parameters, cancellationToken)
          .ConfigureAwait(false);
        return result.ToCommandResult();
    }

    /// <summary>
    /// Runs every parameter set through the shared command scheduler, which preserves submission
    /// order. Whether the executions overlap on the wire is controlled by
    /// <see cref="MySqlConnectOptions.PipeliningLimit"/>.
    /// </summary>
    public async ValueTask<IReadOnlyList<SqlCommandResult>> ExecuteBatchAsync(
        IReadOnlyList<SqlParameters> batch,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return [];
        }

        Task<SqlCommandResult>[] pending = new Task<SqlCommandResult>[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            pending[i] = ExecuteAsync(batch[i], cancellationToken).AsTask();
        }

        try
        {
            return await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<SqlCommandResult> successful = new(batch.Count);
            for (var i = 0; i < pending.Length; i++)
            {
                var execution = pending[i];
                if (execution.Status == TaskStatus.RanToCompletion)
                {
                    successful.Add(execution.Result);
                    continue;
                }

                if (execution.IsCanceled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                var failure = execution.Exception?.InnerExceptions.FirstOrDefault() ??
                  new SqlClientException("MySQL prepared batch execution failed.");
                throw new MySqlBatchException(i, successful, failure);
            }

            throw;
        }
    }

    public async ValueTask<ISqlCursor> OpenCursorAsync(
        SqlParameters parameters = default,
        int fetchSize = 50,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fetchSize);
        cancellationToken.ThrowIfCancellationRequested();
        return await _connection.OpenCursorAsync(
          _statement,
          parameters,
          fetchSize,
          ownsStatement: false,
          cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ISqlRowReader> ExecuteReaderAsync(
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _connection.ExecutePreparedReaderAsync(_statement, parameters, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Rows are streamed from the wire under backpressure, so the fetch size is validated but
    /// does not change how much the server sends ahead.
    /// </remarks>
    public async IAsyncEnumerable<SqlRow> StreamAsync(
        SqlParameters parameters = default,
        int fetchSize = 50,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fetchSize);
        await foreach (var row in _connection.StreamPreparedRowsAsync(
                         _statement,
                         parameters,
                         ownsStatement: false,
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
        await _connection.CloseStatementAsync(_statement).ConfigureAwait(false);
    }
}

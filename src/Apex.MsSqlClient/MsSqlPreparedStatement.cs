using System.Runtime.CompilerServices;
using Apex.MsSqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MsSqlClient;

internal sealed class MsSqlPreparedStatement : ISqlPreparedStatement
{
    private readonly MsSqlConnection _connection;
    private readonly object _gate = new();
    private Task? _disposeTask;
    private int _handle;
    private bool _hasExecution;
    private bool _preparing;

    internal MsSqlPreparedStatement(MsSqlConnection connection, string sql)
    {
        _connection = connection;
        Sql = sql;
        Operation = GetOperation(sql);
    }

    public string Sql { get; }

    internal string Operation { get; }

    public ValueTask<SqlRowSet> QueryAsync(
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _hasExecution = true;
            return _connection.ExecutePreparedAsync(this, parameters, cancellationToken);
        }
    }

    public async ValueTask<SqlCommandResult> ExecuteAsync(
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default)
    {
        var result =
          await QueryAsync(parameters, cancellationToken).ConfigureAwait(false);
        return new SqlCommandResult(result.AffectedRows, result.CommandTag);
    }

    public async ValueTask<IReadOnlyList<SqlCommandResult>> ExecuteBatchAsync(
        IReadOnlyList<SqlParameters> batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Task<SqlRowSet>[] pending = new Task<SqlRowSet>[batch.Count];
        lock (_gate)
        {
            ThrowIfDisposed();
            for (var i = 0; i < batch.Count; i++)
            {
                _hasExecution = true;
                pending[i] = _connection.ExecutePreparedAsync(
                  this,
                  batch[i],
                  cancellationToken).AsTask();
            }
        }

        var rows = await Task.WhenAll(pending).ConfigureAwait(false);
        SqlCommandResult[] results = new SqlCommandResult[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            results[i] = new SqlCommandResult(rows[i].AffectedRows, rows[i].CommandTag);
        }

        return results;
    }

    public async ValueTask<ISqlCursor> OpenCursorAsync(
        SqlParameters parameters = default,
        int fetchSize = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fetchSize);
        var rows = await QueryAsync(parameters, cancellationToken).ConfigureAwait(false);
        return new MsSqlCursor(rows, fetchSize);
    }

    public ValueTask<ISqlRowReader> ExecuteReaderAsync(
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _hasExecution = true;
            return ValueTask.FromResult<ISqlRowReader>(
              new MsSqlRowReader(_connection, this, parameters, cancellationToken));
        }
    }

    public async IAsyncEnumerable<SqlRow> StreamAsync(
        SqlParameters parameters = default,
        int fetchSize = 50,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fetchSize);
        MsSqlRowReader reader;
        lock (_gate)
        {
            ThrowIfDisposed();
            _hasExecution = true;
            reader = new MsSqlRowReader(_connection, this, parameters, cancellationToken);
        }

        await foreach (var row in _connection.StreamRowsAsync(
                         reader,
                         fetchSize,
                         cancellationToken).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposeTask = _hasExecution
              ? _connection.ClosePreparedAsync(this).AsTask()
              : Task.CompletedTask;
            return new ValueTask(_disposeTask);
        }
    }

    internal ReadOnlyMemory<byte> BuildRequest(
        SqlParameters parameters,
        long transactionDescriptor,
        out bool preparesHandle)
    {
        lock (_gate)
        {
            if (_handle > 0)
            {
                preparesHandle = false;
                return TdsRequestWriter.BuildExecute(
                  _handle,
                  parameters,
                  transactionDescriptor);
            }

            var request = TdsRequestWriter.BuildPrepareExecute(
              Sql,
              parameters,
              transactionDescriptor);
            _preparing = true;
            preparesHandle = true;
            return request;
        }
    }

    internal void CaptureReturnValue(TdsReturnValue returnValue)
    {
        lock (_gate)
        {
            if (!_preparing ||
                !returnValue.IsOutput ||
                returnValue.Name.Length != 0 ||
                returnValue.Value is null)
            {
                return;
            }

            _handle = returnValue.GetPreparedHandle();
            _preparing = false;
        }
    }

    internal void EnsureHandleInitialized()
    {
        lock (_gate)
        {
            if (_handle <= 0)
            {
                _preparing = false;
                throw new InvalidDataException(
                  "SQL Server did not return a handle for sp_prepexec.");
            }
        }
    }

    internal int GetHandleForClose()
    {
        lock (_gate)
        {
            return _handle;
        }
    }

    internal void MarkUnprepared(int handle)
    {
        lock (_gate)
        {
            if (_handle == handle)
            {
                _handle = 0;
                _preparing = false;
            }
        }
    }

    private static string GetOperation(string sql)
    {
        var text = sql.AsSpan().TrimStart();
        var separator = text.IndexOfAny(" \t\r\n");
        return (separator < 0 ? text : text[..separator]).ToString().ToUpperInvariant();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
    }
}

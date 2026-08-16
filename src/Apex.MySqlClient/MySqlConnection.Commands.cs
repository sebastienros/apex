using System.Diagnostics;
using System.Runtime.CompilerServices;
using Apex.MySqlClient.Internal;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MySqlClient;

public sealed partial class MySqlConnection
{
    private async ValueTask<MySqlExecutionResult> ExecuteQueryCoreAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var operation = GetOperation(sql);
        using var activity = SqlClientDiagnostics.StartQuery(
          "mysql",
          _options.Database,
          _options.Host,
          _options.Port,
          operation);
        var started = Stopwatch.GetTimestamp();
        Exception? error = null;
        try
        {
            if (parameters.Count == 0)
            {
                return await _scheduler.ExecuteAsync(
                  async token =>
                  {
                      token.ThrowIfCancellationRequested();
                      _writer.WriteTextCommand(MySqlCommand.Query, sql);
                      await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                  },
                  _ => ReceiveExecutionWithCancellationAsync(
                    binary: false,
                    cancellationToken),
                  barrier: cancellationToken.CanBeCanceled || _options.AllowLoadLocalInfile,
                  cancellationToken).ConfigureAwait(false);
            }

            return await _scheduler.ExecuteAsync(
              static _ => ValueTask.CompletedTask,
              _ => PrepareAndExecuteAsync(sql, parameters, cancellationToken),
              barrier: true,
              cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = exception;
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw;
        }
        finally
        {
            SqlClientDiagnostics.RecordQuery(
              Stopwatch.GetElapsedTime(started),
              "mysql",
              operation,
              error);
        }
    }

    private async ValueTask<MySqlExecutionResult> PrepareAndExecuteAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var statement = await GetOrPrepareAsync(sql).ConfigureAwait(false);
        try
        {
            try
            {
                return await ExecuteStatementDirectAsync(statement, parameters, cancellationToken)
                  .ConfigureAwait(false);
            }
            catch (MySqlException exception) when (
              statement.IsCached &&
              !cancellationToken.IsCancellationRequested &&
              exception.ErrorNumber is 1243 or 1615)
            {
                RemoveCachedStatement(sql, statement);
                statement = await GetOrPrepareAsync(sql).ConfigureAwait(false);
                return await ExecuteStatementDirectAsync(statement, parameters, cancellationToken)
                  .ConfigureAwait(false);
            }
        }
        finally
        {
            if (!statement.IsCached && IsUsable)
            {
                WriteStatementClose(statement);
                await FlushWriterAsync().ConfigureAwait(false);
            }
        }
    }

    private ValueTask<MySqlExecutionResult> ExecuteStatementDirectAsync(
        MySqlStatement statement,
        SqlParameters parameters,
        CancellationToken cancellationToken) =>
      ReceiveWithCancellationAsync(
        async () =>
        {
            WriteExecute(statement, parameters, MySqlCursorType.NoCursor);
            await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            var rows = await ReadResultsAsync(binary: true, CancellationToken.None)
            .ConfigureAwait(false);
            return new MySqlExecutionResult(rows, _lastCommandInfo);
        },
        cancellationToken);

    internal async ValueTask<SqlRowSet> ExecutePreparedAsync(
        MySqlStatement statement,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        var result = await ExecutePreparedDetailedAsync(
          statement,
          parameters,
          cancellationToken).ConfigureAwait(false);
        return result.Rows;
    }

    internal async ValueTask<MySqlExecutionResult> ExecutePreparedDetailedAsync(
        MySqlStatement statement,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var operation = statement.Operation;
        using var activity = SqlClientDiagnostics.StartQuery(
            "mysql",
            _options.Database,
            _options.Host,
            _options.Port,
            operation);
        var started = Stopwatch.GetTimestamp();
        Exception? error = null;
        try
        {
            return await _scheduler.ExecuteAsync(
                    token =>
                {
                    token.ThrowIfCancellationRequested();
                    WriteExecute(statement, parameters, MySqlCursorType.NoCursor);
                    return ValueTask.CompletedTask;
                },
                _ => ReceiveExecutionWithCancellationAsync(
                    binary: true,
                    cancellationToken),
                barrier: cancellationToken.CanBeCanceled || _options.AllowLoadLocalInfile,
                cancellationToken,
                flushBatch: true).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = exception;
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw;
        }
        finally
        {
            SqlClientDiagnostics.RecordQuery(
                Stopwatch.GetElapsedTime(started),
                "mysql",
                operation,
                error);
        }
    }

    internal async ValueTask<TState> ExecutePreparedCollectAsync<TState>(
        MySqlStatement statement,
        TState state,
        Action<TState, SqlRow> collector,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var operation = statement.Operation;
        using var activity = SqlClientDiagnostics.StartQuery(
            "mysql",
            _options.Database,
            _options.Host,
            _options.Port,
            operation);
        var started = Stopwatch.GetTimestamp();
        Exception? error = null;
        try
        {
            return await _scheduler.ExecuteAsync(
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    WriteExecute(statement, parameters, MySqlCursorType.NoCursor);
                    return ValueTask.CompletedTask;
                },
                _ => ReceiveWithCancellationAsync(
                    () => ReadCollectedResultsAsync(
                        state,
                        collector,
                        CancellationToken.None),
                    cancellationToken),
                barrier: cancellationToken.CanBeCanceled || _options.AllowLoadLocalInfile,
                cancellationToken,
                flushBatch: true).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = exception;
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw;
        }
        finally
        {
            SqlClientDiagnostics.RecordQuery(
                Stopwatch.GetElapsedTime(started),
                "mysql",
                operation,
                error);
        }
    }

    private ValueTask<MySqlExecutionResult> ReceiveExecutionWithCancellationAsync(
        bool binary,
        CancellationToken cancellationToken) =>
      ReceiveWithCancellationAsync(
        async () =>
        {
            var rows = await ReadResultsAsync(binary, CancellationToken.None)
            .ConfigureAwait(false);
            return new MySqlExecutionResult(rows, _lastCommandInfo);
        },
        cancellationToken);

    internal async ValueTask ExecuteTransactionControlAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _scheduler.ExecuteAsync(
          async token =>
          {
              token.ThrowIfCancellationRequested();
              _writer.WriteTextCommand(MySqlCommand.Query, sql);
              await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
          },
          async _ =>
          {
              await ReadResultsAsync(binary: false, CancellationToken.None).ConfigureAwait(false);
              return true;
          },
          barrier: true,
          cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<MySqlStatement> PrepareCoreAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        return await _scheduler.ExecuteAsync(
          async token =>
          {
              token.ThrowIfCancellationRequested();
              _writer.WriteTextCommand(MySqlCommand.StatementPrepare, sql);
              await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
          },
          _ => ReadPrepareResponseAsync(sql, CancellationToken.None),
          barrier: true,
          cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask CloseStatementAsync(MySqlStatement statement)
    {
        if (_disposed || statement.IsCached || !IsUsable)
        {
            return;
        }

        try
        {
            await _scheduler.ExecuteAsync(
              async token =>
              {
                  token.ThrowIfCancellationRequested();
                  WriteStatementClose(statement);
                  await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
              },
              static _ => ValueTask.FromResult(true),
              barrier: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFatalConnectionError(exception) ||
                                          exception is ObjectDisposedException)
        {
        }
    }

    internal async ValueTask<ISqlCursor> OpenCursorAsync(
        MySqlStatement statement,
        SqlParameters parameters,
        int fetchSize,
        bool ownsStatement,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await MySqlCursor.CreateAsync(
          CreatePreparedReader(statement, parameters, ownsStatement, cancellationToken),
          fetchSize,
          cancellationToken).ConfigureAwait(false);
    }

    internal async IAsyncEnumerable<SqlRow> StreamPreparedRowsAsync(
        MySqlStatement statement,
        SqlParameters parameters,
        bool ownsStatement,
        int fetchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader =
          CreatePreparedReader(statement, parameters, ownsStatement, cancellationToken);
        await using var _ = reader.ConfigureAwait(false);
        await foreach (var row in StreamRowsAsync(reader, fetchSize, cancellationToken)
                         .ConfigureAwait(false))
        {
            yield return row;
        }
    }

    internal ValueTask<ISqlRowReader> ExecutePreparedReaderAsync(
        MySqlStatement statement,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.FromResult<ISqlRowReader>(CreatePreparedReader(
          statement,
          parameters,
          ownsStatement: false,
          cancellationToken));
    }

    private async ValueTask<ISqlRowReader> ExecutePreparedReaderCoreAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        var statement = await GetOrPrepareViaSchedulerAsync(sql, cancellationToken)
          .ConfigureAwait(false);
        return CreatePreparedReader(
          statement,
          parameters,
          ownsStatement: !statement.IsCached,
          cancellationToken);
    }

    private MySqlRowReader CreatePreparedReader(
        MySqlStatement statement,
        SqlParameters parameters,
        bool ownsStatement,
        CancellationToken cancellationToken) =>
      new(
        this,
        writeCommand: () => WriteExecute(statement, parameters, MySqlCursorType.NoCursor),
        binary: true,
        cancellationToken,
        statement,
        ownsStatement);

    private MySqlRowReader CreateTextReader(string sql, CancellationToken cancellationToken) =>
      new(
        this,
        writeCommand: () => _writer.WriteTextCommand(MySqlCommand.Query, sql),
        binary: false,
        cancellationToken);

    private async IAsyncEnumerable<SqlRow> StreamTextRowsAsync(
        string sql,
        int fetchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = CreateTextReader(sql, cancellationToken);
        await using var _ = reader.ConfigureAwait(false);
        await foreach (var row in StreamRowsAsync(reader, fetchSize, cancellationToken)
                         .ConfigureAwait(false))
        {
            yield return row;
        }
    }

    private static async IAsyncEnumerable<SqlRow> StreamRowsAsync(
        MySqlRowReader reader,
        int fetchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pageCapacity = Math.Min(fetchSize, 256);
        var hasCurrent = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        while (hasCurrent)
        {
            var decoder = reader.Decoder;
            var columns = reader.Columns;
            SqlRowPageBuilder page = new(
              decoder,
              rowCapacity: pageCapacity,
              byteCapacity: Math.Max(256, pageCapacity * 16));
            while (hasCurrent &&
                   page.Count < pageCapacity &&
                   ReferenceEquals(decoder, reader.Decoder))
            {
                reader.CopyCurrentTo(page);
                hasCurrent = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }

            var batch = page.BuildBatch(columns);
            for (var i = 0; i < batch.Count; i++)
            {
                yield return batch.CreateRow(i);
            }
        }
    }

    private async ValueTask<MySqlStatement> GetOrPrepareViaSchedulerAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        if (_statementCache is null)
        {
            return await PrepareCoreAsync(sql, cancellationToken).ConfigureAwait(false);
        }

        MySqlStatement? existing;
        lock (_statementCacheGate)
        {
            _statementCache.TryGet(sql, out existing);
        }

        return existing ?? await _scheduler.ExecuteAsync(
          static _ => ValueTask.CompletedTask,
          _ => GetOrPrepareAsync(sql),
          barrier: true,
          cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a prepared statement, reusing the bounded cache when it is enabled. The caller
    /// must already hold the scheduler barrier because this writes to the wire directly.
    /// </summary>
    private async ValueTask<MySqlStatement> GetOrPrepareAsync(string sql)
    {
        if (_statementCache is null || sql.Length > _options.PreparedStatementCacheSqlLengthLimit)
        {
            return await PrepareRawAsync(sql).ConfigureAwait(false);
        }

        MySqlStatement? existing;
        lock (_statementCacheGate)
        {
            _statementCache.TryGet(sql, out existing);
        }

        if (existing is not null)
        {
            return existing;
        }

        var prepared = await PrepareRawAsync(sql).ConfigureAwait(false);
        prepared.IsCached = true;
        MySqlStatement? evicted;
        lock (_statementCacheGate)
        {
            _statementCache.Add(sql, prepared, out evicted);
        }

        if (evicted is not null)
        {
            evicted.IsCached = false;
            WriteStatementClose(evicted);
            await FlushWriterAsync().ConfigureAwait(false);
        }

        return prepared;
    }

    private async ValueTask<MySqlStatement> PrepareRawAsync(string sql)
    {
        _writer.WriteTextCommand(MySqlCommand.StatementPrepare, sql);
        await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return await ReadPrepareResponseAsync(sql, CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask FlushWriterAsync() =>
      await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);

    private void WriteStatementClose(MySqlStatement statement)
    {
        _payload.Reset();
        _payload.WriteByte((byte)MySqlCommand.StatementClose);
        _payload.WriteUInt32(statement.Id);
        _writer.WritePacket(0, _payload.WrittenSpan);
    }

    private void WriteExecute(
        MySqlStatement statement,
        SqlParameters parameters,
        MySqlCursorType cursorType)
    {
        _payload.Reset();
        MySqlParameterEncoder.WriteExecute(
          _payload,
          statement.Id,
          cursorType,
          parameters,
          statement.ParameterCount);
        _writer.WritePacket(0, _payload.WrittenSpan);
    }

    private void ClearStatementCache()
    {
        if (_statementCache is null)
        {
            return;
        }

        lock (_statementCacheGate)
        {
            foreach (var statement in _statementCache.DrainValues())
            {
                statement.IsCached = false;
            }
        }
    }

    private void RemoveCachedStatement(string sql, MySqlStatement statement)
    {
        if (_statementCache is null)
        {
            return;
        }

        lock (_statementCacheGate)
        {
            if (_statementCache.TryGet(sql, out var cached) &&
                ReferenceEquals(cached, statement))
            {
                _statementCache.Remove(sql, out _);
                statement.IsCached = false;
            }
        }
    }

    private async ValueTask<T> ReceiveWithCancellationAsync<T>(
        Func<ValueTask<T>> receiveAsync,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await receiveAsync().ConfigureAwait(false);
        }

        CancellationState state = new(this);
        var registration = cancellationToken.Register(state.Cancel);
        try
        {
            T result;
            try
            {
                result = await receiveAsync().ConfigureAwait(false);
            }
            finally
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }

            await state.WaitAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception exception) when (
          cancellationToken.IsCancellationRequested &&
          (exception is MySqlException { IsInterrupted: true } || IsFatalConnectionError(exception)))
        {
            await state.WaitAsync().ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>
    /// Cancels a command that already reached the server. MySQL has no side channel, so the
    /// command is either killed through a second connection or the physical connection is
    /// discarded, which keeps a desynchronized connection out of the pool.
    /// </summary>
    private Task CancelRunningCommandAsync() =>
      _options.QueryCancellation switch
      {
          MySqlQueryCancellation.Disabled => Task.CompletedTask,
          MySqlQueryCancellation.CloseConnection => InvalidateForCancellation(),
          _ => KillRunningQueryAsync(),
      };

    private Task InvalidateForCancellation()
    {
        Invalidate(new MySqlConnectionAbortedException(
          "The MySQL connection was closed to cancel a running command."));
        return Task.CompletedTask;
    }

    private async Task KillRunningQueryAsync()
    {
        var connectionId = _connectionId;
        if (connectionId == 0)
        {
            await InvalidateForCancellation().ConfigureAwait(false);
            return;
        }

        try
        {
            var adminOptions = _options with
            {
                CachePreparedStatements = false,
                PipeliningLimit = 1,
                QueryCancellation = MySqlQueryCancellation.Disabled,
                SessionVariables = new Dictionary<string, string>(StringComparer.Ordinal),
            };
            var admin =
              await ConnectAsync(adminOptions, CancellationToken.None).ConfigureAwait(false);
            await using var _ = admin.ConfigureAwait(false);
            await admin.ExecuteAsync(
              $"KILL QUERY {connectionId.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
              CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
          exception is SqlClientException or
            TimeoutException or
            OperationCanceledException ||
          IsFatalConnectionError(exception))
        {
            await InvalidateForCancellation().ConfigureAwait(false);
        }
    }

    private sealed class CancellationState
    {
        private readonly MySqlConnection _connection;
        private readonly object _gate = new();
        private Task? _cancellation;

        internal CancellationState(MySqlConnection connection)
        {
            _connection = connection;
            Cancel = RequestCancellation;
        }

        internal Action Cancel { get; }

        internal async ValueTask WaitAsync()
        {
            Task? cancellation;
            lock (_gate)
            {
                cancellation = _cancellation;
            }

            if (cancellation is not null)
            {
                await cancellation.ConfigureAwait(false);
            }
        }

        private void RequestCancellation()
        {
            lock (_gate)
            {
                _cancellation ??= _connection.CancelRunningCommandAsync();
            }
        }
    }
}

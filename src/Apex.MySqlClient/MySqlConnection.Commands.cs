using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Apex.MySqlClient.Internal;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MySqlClient;

public sealed partial class MySqlConnection
{
    private static readonly Task<bool> s_notKilled = Task.FromResult(false);

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

    private async IAsyncEnumerable<SqlRow> StreamPreparedRowsAsync(
        string sql,
        SqlParameters parameters,
        int fetchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = CreatePreparedReader(sql, parameters, cancellationToken);
        await using var _ = reader.ConfigureAwait(false);
        await foreach (var row in StreamRowsAsync(reader, fetchSize, cancellationToken)
                         .ConfigureAwait(false))
        {
            yield return row;
        }
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

    internal ValueTask<ISqlRowReader> ExecuteResultReaderAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return parameters.Count == 0
          ? ValueTask.FromResult<ISqlRowReader>(
            CreateTextReader(sql, cancellationToken, preserveResultBoundaries: true))
          : ExecutePreparedReaderCoreAsync(
            sql,
            parameters,
            cancellationToken,
            preserveResultBoundaries: true);
    }

    ValueTask<ISqlRowReader> ISqlResultReaderConnection.ExecuteResultReaderAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken) =>
        ExecuteResultReaderAsync(sql, parameters, cancellationToken);

    internal ValueTask<ISqlRowReader> ExecuteResultPreparedReaderAsync(
        MySqlStatement statement,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.FromResult<ISqlRowReader>(CreatePreparedReader(
          statement,
          parameters,
          ownsStatement: false,
          cancellationToken,
          preserveResultBoundaries: true));
    }

    private ValueTask<ISqlRowReader> ExecutePreparedReaderCoreAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken,
        bool preserveResultBoundaries = false) =>
      ValueTask.FromResult<ISqlRowReader>(CreatePreparedReader(
        sql,
        parameters,
        cancellationToken,
        preserveResultBoundaries));

    /// <summary>
    /// Creates a reader that resolves <paramref name="sql"/> to a prepared statement inside its
    /// own scheduler barrier, so an earlier command cannot evict and close a cached statement
    /// between the cache lookup and the execute.
    /// </summary>
    private MySqlRowReader CreatePreparedReader(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken,
        bool preserveResultBoundaries = false) =>
      preserveResultBoundaries
        ? new MySqlResultReader(this, writeCommand: null, binary: true, cancellationToken, preparedSql: sql, preparedParameters: parameters)
        : new MySqlRowReader(this, writeCommand: null, binary: true, cancellationToken, preparedSql: sql, preparedParameters: parameters);

    private MySqlRowReader CreatePreparedReader(
        MySqlStatement statement,
        SqlParameters parameters,
        bool ownsStatement,
        CancellationToken cancellationToken,
        bool preserveResultBoundaries = false)
    {
        Action writeCommand = () => WriteExecute(statement, parameters, MySqlCursorType.NoCursor);
        return preserveResultBoundaries
            ? new MySqlResultReader(this, writeCommand, binary: true, cancellationToken, statement, ownsStatement)
            : new MySqlRowReader(this, writeCommand, binary: true, cancellationToken, statement, ownsStatement);
    }

    private MySqlRowReader CreateTextReader(
        string sql,
        CancellationToken cancellationToken,
        bool preserveResultBoundaries = false)
    {
        Action writeCommand = () => _writer.WriteTextCommand(MySqlCommand.Query, sql);
        return preserveResultBoundaries
            ? new MySqlResultReader(this, writeCommand, binary: false, cancellationToken)
            : new MySqlRowReader(this, writeCommand, binary: false, cancellationToken);
    }

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

    internal void WriteExecute(
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

    /// <summary>
    /// Runs the receive phase of a command that may be cancelled while it runs on the server.
    /// The caller must hold the scheduler barrier whenever the token can be cancelled: a
    /// <c>KILL QUERY</c> issued for this command is always awaited before the barrier is
    /// released, on every exit path, so it can never land on the next command instead.
    /// </summary>
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
        T result = default!;
        Exception? failure = null;
        try
        {
            result = await receiveAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        // Disposing the registration waits for a running callback, so no KILL can start after
        // this point; the one that may have started is awaited next.
        await registration.DisposeAsync().ConfigureAwait(false);
        if (await state.WaitAsync().ConfigureAwait(false))
        {
            await AbsorbPendingKillAsync(failure).ConfigureAwait(false);
        }

        if (failure is not null)
        {
            if (cancellationToken.IsCancellationRequested &&
                (failure is MySqlException { IsInterrupted: true } || IsFatalConnectionError(failure)))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            ExceptionDispatchInfo.Throw(failure);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>
    /// Consumes a <c>KILL QUERY</c> that the server acknowledged but that did not interrupt the
    /// command it targeted, because the command had already finished. The kill flag can then
    /// still be pending on the session, and the server would interrupt the next statement with
    /// error 1317. A <c>DO SLEEP(0)</c> sent inside the same barrier absorbs it, as MySqlConnector
    /// does. The caller must hold the scheduler barrier.
    /// </summary>
    /// <param name="outcome">How the targeted command ended, <see langword="null"/> on success.</param>
    private async ValueTask AbsorbPendingKillAsync(Exception? outcome)
    {
        if (outcome is MySqlException { ErrorNumber: 1317 } ||
            (outcome is not null && IsFatalConnectionError(outcome)) ||
            !IsUsable)
        {
            // The kill already interrupted the command, or the session is gone.
            return;
        }

        var commandInfo = _lastCommandInfo;
        var columns = _lastColumns;
        try
        {
            _writer.WriteTextCommand(MySqlCommand.Query, "DO SLEEP(0)");
            await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await ReadResultsAsync(binary: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (MySqlException exception) when (exception.ErrorNumber == 1317)
        {
        }
        catch (Exception exception)
        {
            // The kill may still be pending, so keep the connection out of the pool. The caller
            // reports the outcome of the command itself.
            Invalidate(exception);
        }
        finally
        {
            _lastCommandInfo = commandInfo;
            _lastColumns = columns;
        }
    }

    /// <summary>
    /// Cancels a command that already reached the server. MySQL has no side channel, so the
    /// command is either killed through a second connection or the physical connection is
    /// discarded, which keeps a desynchronized connection out of the pool.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the server acknowledged a <c>KILL QUERY</c> and the connection
    /// is still in use, so the caller must absorb a kill that the command finished before.
    /// </returns>
    private Task<bool> CancelRunningCommandAsync() =>
      _options.QueryCancellation switch
      {
          MySqlQueryCancellation.Disabled => s_notKilled,
          MySqlQueryCancellation.CloseConnection => InvalidateForCancellation(),
          _ => KillRunningQueryAsync(),
      };

    private Task<bool> InvalidateForCancellation()
    {
        Invalidate(new MySqlConnectionAbortedException(
          "The MySQL connection was closed to cancel a running command."));
        return s_notKilled;
    }

    private async Task<bool> KillRunningQueryAsync()
    {
        var connectionId = _connectionId;
        if (connectionId == 0)
        {
            return await InvalidateForCancellation().ConfigureAwait(false);
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
            return true;
        }
        catch (Exception exception) when (
          exception is SqlClientException or
            TimeoutException or
            OperationCanceledException ||
          IsFatalConnectionError(exception))
        {
            return await InvalidateForCancellation().ConfigureAwait(false);
        }
    }

    private sealed class CancellationState
    {
        private readonly MySqlConnection _connection;
        private readonly object _gate = new();
        private Task<bool>? _cancellation;

        internal CancellationState(MySqlConnection connection)
        {
            _connection = connection;
            Cancel = RequestCancellation;
        }

        internal Action Cancel { get; }

        /// <summary>Waits for a started cancellation and reports whether it killed a query.</summary>
        internal async ValueTask<bool> WaitAsync()
        {
            Task<bool>? cancellation;
            lock (_gate)
            {
                cancellation = _cancellation;
            }

            return cancellation is not null && await cancellation.ConfigureAwait(false);
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

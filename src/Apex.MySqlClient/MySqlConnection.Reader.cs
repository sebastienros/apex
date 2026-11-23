using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;
using Apex.MySqlClient.Internal;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MySqlClient;

public sealed partial class MySqlConnection
{
    private sealed class MySqlResultReader(
        MySqlConnection connection,
        Action? writeCommand,
        bool binary,
        CancellationToken cancellationToken,
        MySqlStatement? statement = null,
        bool ownsStatement = false,
        string? preparedSql = null,
        SqlParameters preparedParameters = default) :
        MySqlRowReader(connection, writeCommand, binary, cancellationToken, statement, ownsStatement,
            preserveResultBoundaries: true, preparedSql, preparedParameters),
        ISqlResultBoundaryReader,
        ISqlRecordsAffectedReader
    {
        int ISqlRecordsAffectedReader.RecordsAffected => RecordsAffected;
    }

    /// <summary>
    /// Streams rows without buffering them. The current row points straight at the pooled wire
    /// buffer, so its values are only valid until the next read. The pump and the consumer meet
    /// through an auto reset event instead of a channel, which keeps the hand off allocation free.
    /// </summary>
    internal class MySqlRowReader : ISqlRowReader, IValueTaskSource<bool>
    {
        private readonly MySqlConnection _connection;
        private readonly AsyncAutoResetEvent _advance = new();
        private readonly SqlResultReaderState? _results;
        private readonly object _gate = new();
        private readonly Action _cancelAction;
        private readonly CancellationTokenRegistration _operationCancellation;
        private readonly CancellationToken _operationCancellationToken;
        private readonly bool _binary;
        private readonly string? _preparedSql;
        private readonly SqlParameters _preparedParameters;
        private readonly Task<bool> _operation;
        private MySqlStatement? _statement;
        private MySqlStatement? _ownedStatement;
        private ManualResetValueTaskSourceCore<bool> _readCompletion;
        private CancellationTokenRegistration _readCancellation;
        private CancellationToken _readCancellationToken;
        private CancellationToken _cancellationToken;
        private MySqlPacket _current;
        private MySqlRowDecoder? _decoder;
        private IReadOnlyList<SqlColumn> _columns = Array.Empty<SqlColumn>();
        private Exception? _error;
        private Task<bool>? _cancelRequest;
        private bool _hasCurrent;
        private bool _currentDelivered;
        private bool _completed;
        private bool _stopped;
        private bool _canceled;
        private bool _sent;
        private bool _readPending;
        private bool _readSignaled;
        private bool _pumpFinished;
        private int _disposed;

        // writeCommand writes the command. It is null when preparedSql is resolved to a statement
        // and executed inside the reader's own barrier instead.
        internal MySqlRowReader(
            MySqlConnection connection,
            Action? writeCommand,
            bool binary,
            CancellationToken cancellationToken,
            MySqlStatement? statement = null,
            bool ownsStatement = false,
            bool preserveResultBoundaries = false,
            string? preparedSql = null,
            SqlParameters preparedParameters = default)
        {
            Debug.Assert((writeCommand is null) == (preparedSql is not null));
            _preparedSql = preparedSql;
            _preparedParameters = preparedParameters;
            _connection = connection;
            _results = preserveResultBoundaries ? new SqlResultReaderState() : null;
            _binary = binary;
            _statement = statement;
            _ownedStatement = ownsStatement ? statement : null;
            _operationCancellationToken = cancellationToken;
            _cancelAction = Cancel;
            _readCompletion.RunContinuationsAsynchronously = true;
            _operationCancellation = cancellationToken.CanBeCanceled
              ? cancellationToken.Register(_cancelAction)
              : default;
            _operation = connection._scheduler.ExecuteAsync(
              async token =>
              {
                  token.ThrowIfCancellationRequested();
                  if (writeCommand is null)
                  {
                      // Prepared from SQL: resolved, written and flushed by the receive phase.
                      return;
                  }

                  writeCommand();
                  await connection._writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                  MarkSent();
              },
              _ => PumpAsync(),
              barrier: true,
              cancellationToken).AsTask();
            _ = ObserveOperationAsync();
        }

        public IReadOnlyList<SqlColumn> Columns => _columns;

        public int FieldCount => _columns.Count;

        protected int RecordsAffected => _results?.RecordsAffected ?? -1;

        public ValueTask<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            var results = _results ?? throw new NotSupportedException("This reader does not preserve result boundaries.");
            Task<bool> wait;
            lock (_gate)
            {
                ThrowIfError();
                if (_hasCurrent)
                {
                    _currentDelivered = true;
                    return ValueTask.FromResult(true);
                }
                if (results.Ended || _completed) return ValueTask.FromResult(false);
                wait = results.Initialize();
            }

            // Cancel the operation, not just the wait, so an abandoned initializer cannot later
            // consume the first row of the result.
            return AwaitResultTransitionAsync(wait, cancellationToken);
        }

        public ValueTask<bool> NextResultAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            var results = _results ?? throw new NotSupportedException("This reader does not preserve result boundaries.");
            Task<bool> wait;
            bool advanceRow;
            bool advanceResult;
            lock (_gate)
            {
                ThrowIfError();
                if (_completed) return ValueTask.FromResult(false);
                wait = results.NextResult();
                advanceRow = _hasCurrent;
                advanceResult = results.Ended;
            }

            if (advanceRow) _advance.Set();
            if (advanceResult) results.Advance.Set();
            return AwaitResultTransitionAsync(wait, cancellationToken);
        }

        private async ValueTask<bool> AwaitResultTransitionAsync(
            Task<bool> wait,
            CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(_cancelAction)
                : default;
            return await wait.ConfigureAwait(false);
        }

        internal MySqlRowDecoder Decoder =>
          _decoder ?? throw new InvalidOperationException("ReadAsync must return true first.");

        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            bool advance;
            lock (_gate)
            {
                ThrowIfError();
                if (_canceled)
                {
                    if (_hasCurrent)
                    {
                        _advance.Set();
                    }

                    return ValueTask.FromException<bool>(
                      new OperationCanceledException(_cancellationToken));
                }

                if (_completed)
                {
                    return ValueTask.FromResult(false);
                }

                if (_results is { Ended: true })
                {
                    return ValueTask.FromResult(false);
                }

                if (_hasCurrent && !_currentDelivered)
                {
                    _currentDelivered = true;
                    return ValueTask.FromResult(true);
                }

                if (_readPending)
                {
                    throw new InvalidOperationException("Concurrent row reads are not supported.");
                }

                advance = _hasCurrent;
                _readPending = true;
                _readCompletion.Reset();
                _readCancellationToken = cancellationToken;
                _readCancellation = cancellationToken.CanBeCanceled
                  ? cancellationToken.Register(_cancelAction)
                  : default;
            }

            if (advance)
            {
                _advance.Set();
            }

            return new ValueTask<bool>(this, _readCompletion.Version);
        }

        public bool GetResult(short token)
        {
            CancellationTokenRegistration registration;
            CancellationToken cancellationToken;
            bool result;
            try
            {
                result = _readCompletion.GetResult(token);
            }
            finally
            {
                lock (_gate)
                {
                    registration = _readCancellation;
                    cancellationToken = _readCancellationToken;
                    _readCancellation = default;
                    _readCancellationToken = default;
                    _readPending = false;
                    _readSignaled = false;
                }

                registration.Dispose();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        public ValueTaskSourceStatus GetStatus(short token) => _readCompletion.GetStatus(token);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags) =>
          _readCompletion.OnCompleted(continuation, state, token, flags);

        public bool IsNull(int ordinal)
        {
            EnsureCurrent();
            return _decoder!.IsNull(_current.Memory, ordinal);
        }

        [SuppressMessage(
            "Usage",
            "CA2201:Do not raise reserved exception types",
            Justification = "Matches the IDataRecord.GetOrdinal contract.")]
        public int GetOrdinal(string name)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            for (var i = 0; i < _columns.Count; i++)
            {
                if (string.Equals(_columns[i].Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            throw new IndexOutOfRangeException($"Column '{name}' does not exist.");
        }

        public T Get<T>(int ordinal)
        {
            EnsureCurrent();
            return SqlRowDecoder.Decode<T>(
              _decoder!,
              _current.Memory,
              ordinal,
              _columns[ordinal],
              copyReadOnlyMemory: true);
        }

        public TElement[]? GetArray<TElement>(int ordinal)
        {
            EnsureCurrent();
            return _decoder!.DecodeArray<TElement>(
              _current.Memory,
              ordinal,
              _columns[ordinal]);
        }

        public bool GetBoolean(int ordinal)
        {
            EnsureCurrent();
            return _decoder!.DecodeBoolean(_current.Memory, ordinal, _columns[ordinal]);
        }

        public short GetInt16(int ordinal)
        {
            EnsureCurrent();
            return _decoder!.DecodeInt16(_current.Memory, ordinal, _columns[ordinal]);
        }

        public int GetInt32(int ordinal)
        {
            EnsureCurrent();
            return _decoder!.DecodeInt32(_current.Memory, ordinal, _columns[ordinal]);
        }

        public long GetInt64(int ordinal)
        {
            EnsureCurrent();
            return _decoder!.DecodeInt64(_current.Memory, ordinal, _columns[ordinal]);
        }

        public float GetFloat(int ordinal)
        {
            EnsureCurrent();
            return _decoder!.DecodeFloat(_current.Memory, ordinal, _columns[ordinal]);
        }

        public double GetDouble(int ordinal)
        {
            EnsureCurrent();
            return _decoder!.DecodeDouble(_current.Memory, ordinal, _columns[ordinal]);
        }

        public string GetString(int ordinal)
        {
            EnsureCurrent();
            return _decoder!.DecodeString(_current.Memory, ordinal, _columns[ordinal])!;
        }

        public Guid GetGuid(int ordinal)
        {
            EnsureCurrent();
            return _decoder!.DecodeGuid(_current.Memory, ordinal, _columns[ordinal]);
        }

        public DateOnly GetDateOnly(int ordinal)
        {
            EnsureCurrent();
            return _decoder!.DecodeDateOnly(_current.Memory, ordinal, _columns[ordinal]);
        }

        public TimeOnly GetTimeOnly(int ordinal)
        {
            EnsureCurrent();
            return _decoder!.DecodeTimeOnly(_current.Memory, ordinal, _columns[ordinal]);
        }

        public DateTime GetDateTime(int ordinal)
        {
            EnsureCurrent();
            return _decoder!.DecodeDateTime(_current.Memory, ordinal, _columns[ordinal]);
        }

        public DateTimeOffset GetDateTimeOffset(int ordinal)
        {
            EnsureCurrent();
            return _decoder!.DecodeDateTimeOffset(_current.Memory, ordinal, _columns[ordinal]);
        }

        public byte[] GetBytes(int ordinal)
        {
            EnsureCurrent();
            return _decoder!.DecodeBytes(_current.Memory, ordinal, _columns[ordinal])!;
        }

        internal void CopyCurrentTo(SqlRowPageBuilder page)
        {
            EnsureCurrent();
            page.Add(_current.Span);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (_gate)
            {
                _stopped = true;
            }

            _advance.Set();
            _results?.Advance.Set();
            try
            {
                await _operation.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _operationCancellation.Dispose();
                DisposeCurrent();
                if (_ownedStatement is not null)
                {
                    await _connection.CloseStatementAsync(_ownedStatement).ConfigureAwait(false);
                }
            }
        }

        private void MarkSent()
        {
            lock (_gate)
            {
                _sent = true;
                _cancelRequest ??= _canceled ? _connection.CancelRunningCommandAsync() : null;
            }
        }

        /// <summary>
        /// Resolves the SQL to a prepared statement and sends the execute. This runs in the
        /// receive phase of the reader's barrier, so the statement cannot be evicted (and closed on
        /// the server) by another command before the execute reaches the server.
        /// </summary>
        private async ValueTask<bool> SendPreparedAsync(string sql)
        {
            var statement = await _connection.GetOrPrepareAsync(sql).ConfigureAwait(false);
            _statement = statement;
            if (!statement.IsCached)
            {
                _ownedStatement = statement;
            }

            lock (_gate)
            {
                if (_canceled)
                {
                    return false;
                }
            }

            _connection.WriteExecute(statement, _preparedParameters, MySqlCursorType.NoCursor);
            await _connection._writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            MarkSent();
            return true;
        }

        private async ValueTask<bool> PumpAsync()
        {
            Exception? failure = null;
            try
            {
                if (_preparedSql is not null && !await SendPreparedAsync(_preparedSql).ConfigureAwait(false))
                {
                    failure = new OperationCanceledException(_cancellationToken);
                }
                else
                {
                    await PumpResultsAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (failure is MySqlException { ErrorNumber: 1243 or 1615 } &&
                _statement is { IsCached: true } statement)
            {
                _connection.RemoveCachedStatement(statement.Sql, statement);
            }

            // From here on Cancel() no longer starts a KILL QUERY, and the one it may already have
            // started is awaited before the barrier is released, on every exit path. Otherwise the
            // KILL could land on the next command on this connection.
            Task<bool>? cancelRequest;
            bool canceled;
            lock (_gate)
            {
                _pumpFinished = true;
                cancelRequest = _cancelRequest;
                canceled = _canceled;
            }

            if (cancelRequest is not null && await cancelRequest.ConfigureAwait(false))
            {
                await _connection.AbsorbPendingKillAsync(failure).ConfigureAwait(false);
            }

            if (canceled &&
                (failure is null or OperationCanceledException or MySqlException { IsInterrupted: true } ||
                 IsFatalConnectionError(failure)))
            {
                OperationCanceledException cancellation = new(_cancellationToken);
                Complete(cancellation);
                throw cancellation;
            }

            Complete(failure);
            if (failure is not null)
            {
                ExceptionDispatchInfo.Throw(failure);
            }

            return true;
        }

        private async ValueTask PumpResultsAsync()
        {
            while (true)
            {
                int columnCount;
                long affectedRows = -1;
                using (var header =
                  await _connection._reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    var result = _connection.ReadResultHeader(header.Span);
                    if (result.IsLocalInfile)
                    {
                        var fileName = s_utf8.GetString(header.Span[1..]);
                        await _connection.HandleLocalInfileAsync(
                            fileName,
                            header.Sequence,
                            CancellationToken.None)
                          .ConfigureAwait(false);
                        // The rows loaded are reported by the OK packet that follows the file.
                        affectedRows = _connection._lastCommandInfo.AffectedRows;
                    }

                    columnCount = result.IsCompletion ? 0 : result.ColumnCount;
                    if (result.IsCompletion)
                    {
                        affectedRows = result.AffectedRows;
                    }
                }

                if (columnCount > 0)
                {
                    await PumpResultSetAsync(columnCount).ConfigureAwait(false);
                }
                else if (_results is not null)
                {
                    _results.AddRecordsAffected(affectedRows);
                    _columns = Array.Empty<SqlColumn>();
                    ResultStarted();
                    await ResultCompletedAsync().ConfigureAwait(false);
                }

                if ((_connection._status & MySqlServerStatus.MoreResultsExist) == 0)
                {
                    return;
                }
            }
        }

        private async ValueTask PumpResultSetAsync(int columnCount)
        {
            var decoder = await _connection.ReadColumnDefinitionsAsync(
              columnCount,
              _binary,
              CancellationToken.None).ConfigureAwait(false);
            _decoder = decoder;
            _columns = decoder.Columns;
            ResultStarted();
            while (true)
            {
                var packet = await _connection._reader.ReadAsync(CancellationToken.None)
                  .ConfigureAwait(false);
                var retained = false;
                try
                {
                    if (_connection.TryCompleteResultSet(packet.Span))
                    {
                        if (_results is not null)
                        {
                            await ResultCompletedAsync().ConfigureAwait(false);
                        }
                        return;
                    }

                    decoder.ValidateRow(packet.Span);
                    lock (_gate)
                    {
                        if (!_stopped)
                        {
                            _current = packet;
                            _hasCurrent = true;
                            _currentDelivered = _results?.PublishRow() ?? false;
                            retained = true;
                        }
                    }

                    if (retained)
                    {
                        SignalRead(result: true, error: null);
                        await _advance.WaitAsync().ConfigureAwait(false);
                        DisposeCurrent();
                    }
                }
                finally
                {
                    if (!retained)
                    {
                        packet.Dispose();
                    }
                }
            }
        }

        private async Task ObserveOperationAsync()
        {
            try
            {
                await _operation.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Complete(exception);
            }
        }

        private void Cancel()
        {
            bool advance;
            lock (_gate)
            {
                if (_completed || _canceled || _pumpFinished)
                {
                    return;
                }

                _canceled = true;
                _stopped = true;
                _cancellationToken = _readCancellationToken.IsCancellationRequested
                  ? _readCancellationToken
                  : _operationCancellationToken;
                advance = !_hasCurrent || !_currentDelivered;
                if (_sent)
                {
                    _cancelRequest ??= _connection.CancelRunningCommandAsync();
                }
            }

            if (advance) _advance.Set();
            _results?.Advance.Set();
        }

        private void Complete(Exception? error)
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _error = error;
                _completed = true;
                _results?.Complete(error);
            }

            SignalRead(result: false, error);
        }

        private void ResultStarted()
        {
            if (_results is null) return;
            lock (_gate)
            {
                _results.Start();
            }
        }

        private ValueTask ResultCompletedAsync()
        {
            if (_results is null) return ValueTask.CompletedTask;
            bool waitForNext;
            lock (_gate)
            {
                waitForNext = _results.End(_stopped);
            }

            SignalRead(result: false, error: null);
            return waitForNext ? _results.Advance.WaitAsync() : ValueTask.CompletedTask;
        }

        private void DisposeCurrent()
        {
            lock (_gate)
            {
                if (!_hasCurrent)
                {
                    return;
                }

                _current.Dispose();
                _current = default;
                _hasCurrent = false;
                _currentDelivered = false;
            }
        }

        private void EnsureCurrent()
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            lock (_gate)
            {
                ThrowIfError();
                if (!_hasCurrent || _decoder is null)
                {
                    throw new InvalidOperationException("ReadAsync must return true first.");
                }
            }
        }

        private void ThrowIfError()
        {
            if (_error is not null)
            {
                ExceptionDispatchInfo.Capture(_error).Throw();
            }
        }

        private void SignalRead(bool result, Exception? error)
        {
            bool signal;
            lock (_gate)
            {
                signal = _readPending && !_readSignaled && (!result || !_currentDelivered);
                _readSignaled |= signal;
                if (signal && result)
                {
                    _currentDelivered = true;
                }
            }

            if (!signal)
            {
                return;
            }

            if (error is not null)
            {
                _readCompletion.SetException(error);
            }
            else
            {
                _readCompletion.SetResult(result);
            }
        }
    }
}

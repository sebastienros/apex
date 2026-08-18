using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;
using Apex.MySqlClient.Internal;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MySqlClient;

public sealed partial class MySqlConnection
{
    /// <summary>
    /// Streams rows without buffering them. The current row points straight at the pooled wire
    /// buffer, so its values are only valid until the next read. The pump and the consumer meet
    /// through an auto reset event instead of a channel, which keeps the hand off allocation free.
    /// </summary>
    internal sealed class MySqlRowReader :
        IApexResultBoundaryReader,
        IApexRecordsAffectedReader,
        IValueTaskSource<bool>
    {
        private readonly MySqlConnection _connection;
        private readonly AsyncAutoResetEvent _advance = new();
        private readonly AsyncAutoResetEvent? _resultAdvance;
        private readonly object _gate = new();
        private readonly Action _cancelAction;
        private readonly CancellationTokenRegistration _operationCancellation;
        private readonly CancellationToken _operationCancellationToken;
        private readonly bool _binary;
        private readonly MySqlStatement? _statement;
        private readonly MySqlStatement? _ownedStatement;
        private readonly Task<bool> _operation;
        private ManualResetValueTaskSourceCore<bool> _readCompletion;
        private CancellationTokenRegistration _readCancellation;
        private CancellationToken _readCancellationToken;
        private CancellationToken _cancellationToken;
        private MySqlPacket _current;
        private MySqlRowDecoder? _decoder;
        private IReadOnlyList<SqlColumn> _columns = Array.Empty<SqlColumn>();
        private Exception? _error;
        private Task? _cancelRequest;
        private TaskCompletionSource<bool>? _initialization;
        private TaskCompletionSource<bool>? _nextResult;
        private bool _hasCurrent;
        private bool _currentDelivered;
        private bool _completed;
        private bool _stopped;
        private bool _canceled;
        private bool _sent;
        private bool _readPending;
        private bool _readSignaled;
        private bool _resultEnded;
        private readonly bool _adoResultBoundaries;
        private bool _nextAwaitingStart;
        private long _recordsAffected = -1;
        private int _disposed;

        internal MySqlRowReader(
            MySqlConnection connection,
            Action writeCommand,
            bool binary,
            CancellationToken cancellationToken,
            MySqlStatement? statement = null,
            bool ownsStatement = false,
            bool adoResultBoundaries = false)
        {
            _connection = connection;
            _adoResultBoundaries = adoResultBoundaries;
            _resultAdvance = adoResultBoundaries ? new AsyncAutoResetEvent() : null;
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
                  writeCommand();
                  await connection._writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                  lock (_gate)
                  {
                      _sent = true;
                      _cancelRequest ??= _canceled ? connection.CancelRunningCommandAsync() : null;
                  }
              },
              _ => PumpAsync(),
              barrier: true,
              cancellationToken).AsTask();
            _ = ObserveOperationAsync();
        }

        public IReadOnlyList<SqlColumn> Columns => _columns;

        public int FieldCount => _columns.Count;

        int IApexRecordsAffectedReader.RecordsAffected => GetRecordsAffected();

        public ValueTask<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            Task<bool>? wait = null;
            lock (_gate)
            {
                ThrowIfError();
                if (_hasCurrent) return ValueTask.FromResult(true);
                if (_resultEnded || _completed) return ValueTask.FromResult(false);
                _initialization ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _initialization.Task;
            }

            return new ValueTask<bool>(wait.WaitAsync(cancellationToken));
        }

        public ValueTask<bool> NextResultAsync(CancellationToken cancellationToken = default)
        {
            Task<bool>? wait = null;
            var advanceRow = false;
            var advanceResult = false;
            lock (_gate)
            {
                ThrowIfError();
                if (_completed) return ValueTask.FromResult(false);
                if (_nextResult is not null)
                {
                    throw new InvalidOperationException("Concurrent result transitions are not supported.");
                }

                _nextResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _nextAwaitingStart = true;
                wait = _nextResult.Task;
                advanceRow = _hasCurrent;
                advanceResult = _resultEnded;
            }

            if (advanceRow) _advance.Set();
            if (advanceResult) _resultAdvance!.Set();
            return AwaitNextResultAsync(wait, cancellationToken);
        }

        private async ValueTask<bool> AwaitNextResultAsync(
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

                if (_adoResultBoundaries && _resultEnded)
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
            _resultAdvance?.Set();
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

        private async ValueTask<bool> PumpAsync()
        {
            try
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
                    else
                    {
                        bool useResultBoundaries;
                        lock (_gate)
                        {
                            useResultBoundaries = _adoResultBoundaries;
                        }

                        if (useResultBoundaries)
                        {
                            if (affectedRows >= 0) AddRecordsAffected(affectedRows);
                            _columns = Array.Empty<SqlColumn>();
                            ResultStarted();
                            await ResultCompletedAsync().ConfigureAwait(false);
                        }
                    }

                    if ((_connection._status & MySqlServerStatus.MoreResultsExist) == 0)
                    {
                        break;
                    }
                }

                await AwaitCancellationAsync().ConfigureAwait(false);
                bool canceled;
                lock (_gate)
                {
                    canceled = _canceled;
                }

                if (canceled)
                {
                    throw new OperationCanceledException(_cancellationToken);
                }

                Complete(error: null);
                return true;
            }
            catch (Exception exception)
            {
                if (exception is MySqlException { ErrorNumber: 1243 or 1615 } &&
                    _statement is { IsCached: true } statement)
                {
                    _connection.RemoveCachedStatement(statement.Sql, statement);
                }

                bool canceled;
                lock (_gate)
                {
                    canceled = _canceled;
                }

                if (canceled &&
                    (exception is MySqlException { IsInterrupted: true } ||
                     IsFatalConnectionError(exception)))
                {
                    await AwaitCancellationAsync().ConfigureAwait(false);
                    OperationCanceledException cancellation = new(_cancellationToken);
                    Complete(cancellation);
                    throw cancellation;
                }

                Complete(exception);
                throw;
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
                        await ResultCompletedAsync().ConfigureAwait(false);
                        return;
                    }

                    decoder.ValidateRow(packet.Span);
                    lock (_gate)
                    {
                        if (!_stopped)
                        {
                            _current = packet;
                            _hasCurrent = true;
                            _currentDelivered = false;
                            retained = true;
                        }
                    }

                    CompleteInitialization(hasRows: retained);

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

        private async ValueTask AwaitCancellationAsync()
        {
            Task? cancelRequest;
            lock (_gate)
            {
                cancelRequest = _cancelRequest;
            }

            if (cancelRequest is not null)
            {
                await cancelRequest.ConfigureAwait(false);
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
                if (_completed || _canceled)
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
            _resultAdvance?.Set();
        }

        private void Complete(Exception? error)
        {
            TaskCompletionSource<bool>? initialization;
            TaskCompletionSource<bool>? nextResult;
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _error = error;
                _completed = true;
                initialization = _initialization;
                _initialization = null;
                nextResult = _nextResult;
                _nextResult = null;
            }

            if (error is null)
            {
                initialization?.TrySetResult(false);
                nextResult?.TrySetResult(false);
            }
            else
            {
                initialization?.TrySetException(error);
                nextResult?.TrySetException(error);
            }
            SignalRead(result: false, error);
        }

        private void ResultStarted()
        {
            lock (_gate)
            {
                _resultEnded = false;
                if (_nextResult is not null)
                {
                    _nextAwaitingStart = false;
                }
            }
        }

        private async ValueTask ResultCompletedAsync()
        {
            TaskCompletionSource<bool>? initialization;
            TaskCompletionSource<bool>? nextResult;
            var waitForNext = false;
            lock (_gate)
            {
                if (!_adoResultBoundaries) return;
                _resultEnded = true;
                initialization = _initialization;
                _initialization = null;
                nextResult = !_nextAwaitingStart ? _nextResult : null;
                if (nextResult is not null)
                {
                    _nextResult = null;
                }
                waitForNext = _nextResult is null && !_stopped;
            }

            initialization?.TrySetResult(false);
            nextResult?.TrySetResult(true);
            SignalRead(result: false, error: null);
            if (waitForNext)
            {
                await _resultAdvance!.WaitAsync().ConfigureAwait(false);
            }
        }

        private void CompleteInitialization(bool hasRows)
        {
            TaskCompletionSource<bool>? initialization;
            TaskCompletionSource<bool>? nextResult;
            lock (_gate)
            {
                if (!_adoResultBoundaries) return;
                initialization = _initialization;
                _initialization = null;
                nextResult = !_nextAwaitingStart ? _nextResult : null;
                if (nextResult is not null)
                {
                    _nextResult = null;
                }
            }

            initialization?.TrySetResult(hasRows);
            nextResult?.TrySetResult(true);
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

        private void AddRecordsAffected(long affectedRows)
        {
            while (true)
            {
                var current = Interlocked.Read(ref _recordsAffected);
                long updated;
                try
                {
                    updated = current < 0
                        ? affectedRows
                        : checked(current + affectedRows);
                }
                catch (OverflowException)
                {
                    updated = -1;
                }

                if (Interlocked.CompareExchange(ref _recordsAffected, updated, current) == current)
                {
                    return;
                }
            }
        }

        private int GetRecordsAffected()
        {
            var affectedRows = Interlocked.Read(ref _recordsAffected);
            return affectedRows is >= int.MinValue and <= int.MaxValue
                ? (int)affectedRows
                : -1;
        }

        private void SignalRead(bool result, Exception? error)
        {
            bool signal;
            lock (_gate)
            {
                signal = _readPending && !_readSignaled;
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

using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks.Sources;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MsSqlClient.Internal;

internal sealed class MsSqlRowReader :
    IApexResultBoundaryReader,
    IApexRecordsAffectedReader,
    IValueTaskSource<bool>
{
    private readonly MsSqlConnection _connection;
    private readonly AsyncAutoResetEvent _advance = new();
    private readonly AsyncAutoResetEvent? _resultAdvance;
    private readonly object _gate = new();
    private readonly Action _cancelAction;
    private readonly CancellationTokenRegistration _operationCancellation;
    private readonly CancellationToken _operationCancellationToken;
    private readonly Task<bool> _operation;
    private readonly TdsRowBuffer _current = new();
    private readonly MsSqlPreparedStatement? _statement;
    private ManualResetValueTaskSourceCore<bool> _readCompletion;
    private CancellationTokenRegistration _readCancellation;
    private CancellationToken _readCancellationToken;
    private CancellationToken _cancellationToken;
    private MsSqlConnection.AttentionState? _attention;
    private TaskCompletionSource<bool>? _initialization;
    private TaskCompletionSource<bool>? _nextResult;
    private IReadOnlyList<TdsColumn> _tdsColumns = Array.Empty<TdsColumn>();
    private IReadOnlyList<SqlColumn> _columns = Array.Empty<SqlColumn>();
    private SqlColumnOrdinalMap? _ordinals;
    private int _resultSetGeneration;
    private int _resultCount;
    private int _currentResultIndex;
    private bool _hasCurrentResult;
    private Exception? _error;
    private bool _cancelBeforeAttention;
    private bool _hasCurrent;
    private bool _currentDelivered;
    private bool _completed;
    private bool _stopped;
    private bool _canceled;
    private bool _readPending;
    private bool _readSignaled;
    private bool _resultEnded;
    private readonly bool _adoResultBoundaries;
    private bool _nextAwaitingStart;
    private bool _preparesHandle;
    private bool _countCurrentResult;
    private long _recordsAffected = -1;
    private int _disposed;

    internal MsSqlRowReader(
        MsSqlConnection connection,
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken,
        bool adoResultBoundaries = false)
      : this(connection, sql, statement: null, parameters, cancellationToken, adoResultBoundaries)
    {
    }

    internal MsSqlRowReader(
        MsSqlConnection connection,
        MsSqlPreparedStatement statement,
        SqlParameters parameters,
        CancellationToken cancellationToken,
        bool adoResultBoundaries = false)
      : this(connection, statement.Sql, statement, parameters, cancellationToken, adoResultBoundaries)
    {
    }

    private MsSqlRowReader(
        MsSqlConnection connection,
        string sql,
        MsSqlPreparedStatement? statement,
        SqlParameters parameters,
        CancellationToken cancellationToken,
        bool adoResultBoundaries)
    {
        _connection = connection;
        _statement = statement;
        _adoResultBoundaries = adoResultBoundaries;
        _resultAdvance = adoResultBoundaries ? new AsyncAutoResetEvent() : null;
        _countCurrentResult = adoResultBoundaries && StartsWithModifyingKeyword(sql);
        _operationCancellationToken = cancellationToken;
        _cancelAction = Cancel;
        _readCompletion.RunContinuationsAsynchronously = true;
        _operationCancellation = cancellationToken.CanBeCanceled
          ? cancellationToken.Register(_cancelAction)
          : default;
        _operation = connection.Scheduler.ExecuteAsync(
          async token =>
          {
              token.ThrowIfCancellationRequested();
              if (statement is null)
              {
                  await connection.WriteRequestAsync(
                sql,
                parameters,
                CancellationToken.None).ConfigureAwait(false);
              }
              else
              {
                  _preparesHandle = await connection.WritePreparedRequestAsync(
                statement,
                parameters,
                CancellationToken.None).ConfigureAwait(false);
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
            if (_hasCurrent)
            {
                _currentDelivered = true;
                return ValueTask.FromResult(true);
            }
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

    internal int ResultSetGeneration => _resultSetGeneration;

    internal int CurrentResultIndex => _currentResultIndex;

    internal SqlRow CurrentRow
    {
        get
        {
            EnsureCurrent();
            return new SqlRow(
                _columns,
                _ordinals!,
                _connection.RowDecoder,
                _current.WrittenMemory);
        }
    }

    public ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        bool advance;
        lock (_gate)
        {
            ThrowIfError();
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

    public ValueTaskSourceStatus GetStatus(short token) =>
      _readCompletion.GetStatus(token);

    public void OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags) =>
      _readCompletion.OnCompleted(continuation, state, token, flags);

    public bool IsNull(int ordinal)
    {
        EnsureCurrent();
        return _connection.RowDecoder.IsNull(_current.WrittenMemory, ordinal);
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
          _connection.RowDecoder,
          _current.WrittenMemory,
          ordinal,
          _columns[ordinal],
          copyReadOnlyMemory: true);
    }

    public TElement[]? GetArray<TElement>(int ordinal)
    {
        EnsureCurrent();
        return _connection.RowDecoder.DecodeArray<TElement>(
          _current.WrittenMemory,
          ordinal,
          _columns[ordinal]);
    }

    public bool GetBoolean(int ordinal)
    {
        EnsureCurrent();
        return _connection.RowDecoder.DecodeBoolean(
          _current.WrittenMemory,
          ordinal,
          _columns[ordinal]);
    }

    public short GetInt16(int ordinal)
    {
        EnsureCurrent();
        return _connection.RowDecoder.DecodeInt16(
          _current.WrittenMemory,
          ordinal,
          _columns[ordinal]);
    }

    public int GetInt32(int ordinal)
    {
        EnsureCurrent();
        return _connection.RowDecoder.DecodeInt32(
          _current.WrittenMemory,
          ordinal,
          _columns[ordinal]);
    }

    public long GetInt64(int ordinal)
    {
        EnsureCurrent();
        return _connection.RowDecoder.DecodeInt64(
          _current.WrittenMemory,
          ordinal,
          _columns[ordinal]);
    }

    public float GetFloat(int ordinal)
    {
        EnsureCurrent();
        return _connection.RowDecoder.DecodeFloat(
          _current.WrittenMemory,
          ordinal,
          _columns[ordinal]);
    }

    public double GetDouble(int ordinal)
    {
        EnsureCurrent();
        return _connection.RowDecoder.DecodeDouble(
          _current.WrittenMemory,
          ordinal,
          _columns[ordinal]);
    }

    public string GetString(int ordinal)
    {
        EnsureCurrent();
        return _connection.RowDecoder.DecodeString(
          _current.WrittenMemory,
          ordinal,
          _columns[ordinal])!;
    }

    public Guid GetGuid(int ordinal)
    {
        EnsureCurrent();
        return _connection.RowDecoder.DecodeGuid(
          _current.WrittenMemory,
          ordinal,
          _columns[ordinal]);
    }

    public DateOnly GetDateOnly(int ordinal)
    {
        EnsureCurrent();
        return _connection.RowDecoder.DecodeDateOnly(
          _current.WrittenMemory,
          ordinal,
          _columns[ordinal]);
    }

    public TimeOnly GetTimeOnly(int ordinal)
    {
        EnsureCurrent();
        return _connection.RowDecoder.DecodeTimeOnly(
          _current.WrittenMemory,
          ordinal,
          _columns[ordinal]);
    }

    public DateTime GetDateTime(int ordinal)
    {
        EnsureCurrent();
        return _connection.RowDecoder.DecodeDateTime(
          _current.WrittenMemory,
          ordinal,
          _columns[ordinal]);
    }

    public DateTimeOffset GetDateTimeOffset(int ordinal)
    {
        EnsureCurrent();
        return _connection.RowDecoder.DecodeDateTimeOffset(
          _current.WrittenMemory,
          ordinal,
          _columns[ordinal]);
    }

    public byte[] GetBytes(int ordinal)
    {
        EnsureCurrent();
        return _connection.RowDecoder.DecodeBytes(
          _current.WrittenMemory,
          ordinal,
          _columns[ordinal])!;
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

        Cancel();
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
        }
    }

    internal void CopyCurrentTo(SqlRowPageBuilder page)
    {
        EnsureCurrent();
        page.Add(_current.WrittenSpan);
    }

    private async ValueTask<bool> PumpAsync()
    {
        List<MsSqlInfo> errors = [];
        var final = false;
        var attention =
          _connection.BeginAttention(_operationCancellationToken);
        lock (_gate)
        {
            _attention = attention;
            if (_cancelBeforeAttention)
            {
                attention.Cancel(_cancellationToken);
            }
        }

        try
        {
            while (true)
            {
                var messageType = await _connection.Reader.BeginMessageAsync(
                  attention.ReadCancellationToken).ConfigureAwait(false);
                if (messageType != TdsMessageType.TabularResult)
                {
                    throw new InvalidDataException(
                      $"Expected SQL Server result, received TDS type 0x{messageType:X2}.");
                }

                TdsStreamingTokenReader tokens = new(
                  _connection.Reader,
                  attention.ReadCancellationToken);
                while (tokens.HasRemaining)
                {
                    var token = await tokens.ReadTokenTypeAsync().ConfigureAwait(false);
                    switch (token)
                    {
                        case TdsTokenType.ColumnMetadata:
                            _tdsColumns = await tokens.ReadColumnsAsync().ConfigureAwait(false);
                            _columns = _tdsColumns
                              .Select(static column => column.Column)
                              .ToArray();
                            _ordinals = SqlColumnOrdinalMapCache.GetOrAdd(_columns);
                            _currentResultIndex = _resultCount;
                            _hasCurrentResult = true;
                            _resultSetGeneration++;
                            ResultStarted();
                            break;
                        case TdsTokenType.Row:
                        case TdsTokenType.NbcRow:
                            if (_tdsColumns.Count == 0)
                            {
                                throw new InvalidDataException(
                                  "SQL Server sent a ROW token before COLMETADATA.");
                            }

                            await tokens.ReadRowAsync(
                              _tdsColumns,
                              nullCompressed: token == TdsTokenType.NbcRow,
                              _current).ConfigureAwait(false);
                            var retained = false;
                            lock (_gate)
                            {
                                if (!_stopped)
                                {
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

                            break;
                        case TdsTokenType.Done:
                        case TdsTokenType.DoneProc:
                        case TdsTokenType.DoneInProc:
                            var done = await tokens.ReadDoneAsync().ConfigureAwait(false);
                            final |= (done.Status & TdsDoneStatus.More) == 0;
                            var hadCurrentResult = _hasCurrentResult;
                            var hasCountResult = (done.Status & TdsDoneStatus.Count) != 0;
                            if (hasCountResult && (!hadCurrentResult || _countCurrentResult))
                            {
                                AddRecordsAffected(done.RowCount);
                            }
                            if (hadCurrentResult)
                            {
                                _countCurrentResult = false;
                            }
                            var isTerminal = (done.Status & TdsDoneStatus.More) == 0;
                            var hasSyntheticResult = !hadCurrentResult &&
                                (hasCountResult || (_resultCount == 0 && isTerminal));
                            if (hadCurrentResult)
                            {
                                _resultCount++;
                                _hasCurrentResult = false;
                            }
                            else if (hasSyntheticResult)
                            {
                                _resultCount++;
                            }

                            _tdsColumns = Array.Empty<TdsColumn>();
                            if (hasSyntheticResult)
                            {
                                _columns = Array.Empty<SqlColumn>();
                                ResultStarted();
                            }
                            if ((done.Status & TdsDoneStatus.Attention) != 0)
                            {
                                attention.Acknowledge();
                                await attention.GetSendTask().ConfigureAwait(false);
                                throw new OperationCanceledException(attention.CancellationToken);
                            }

                            if (hadCurrentResult || hasSyntheticResult)
                            {
                                await ResultCompletedAsync().ConfigureAwait(false);
                            }

                            break;
                        case TdsTokenType.ReturnValue:
                            var returnValue =
                              await tokens.ReadReturnValueAsync().ConfigureAwait(false);
                            if (_preparesHandle)
                            {
                                _statement!.CaptureReturnValue(returnValue);
                            }

                            break;
                        default:
                            await MsSqlConnection.ProcessAncillaryTokenAsync(
                              token,
                              tokens,
                              _connection,
                              errors).ConfigureAwait(false);
                            break;
                    }
                }

                if (attention.IsCancellationRequested ||
                    !final ||
                    !attention.TryCompleteCommand())
                {
                    continue;
                }

                if (errors.Count > 0)
                {
                    throw MsSqlConnection.CreateException(errors);
                }

                if (_preparesHandle)
                {
                    _statement!.EnsureHandleInitialized();
                }

                Complete(error: null);
                return true;
            }
        }
        catch (Exception exception)
        {
            if (!_connection.Reader.EndOfMessage ||
                attention.IsCancellationRequested &&
                !attention.IsAcknowledged)
            {
                _connection.MarkBroken();
            }

            Complete(exception);
            throw;
        }
        finally
        {
            _connection.EndAttention(attention);
            attention.Dispose();
            lock (_gate)
            {
                _attention = null;
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
            if (_attention is not null)
            {
                _attention.Cancel(_cancellationToken);
            }
            else
            {
                _cancelBeforeAttention = true;
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
            if (hasRows && (initialization is not null || nextResult is not null))
            {
                _currentDelivered = true;
            }
        }

        initialization?.TrySetResult(hasRows);
        nextResult?.TrySetResult(true);
    }

    private void DisposeCurrent()
    {
        lock (_gate)
        {
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
            if (!_hasCurrent)
            {
                throw new InvalidOperationException("ReadAsync must return true first.");
            }
        }
    }

    private void ThrowIfError()
    {
        if (_error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
              .Capture(_error)
              .Throw();
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

    private static bool StartsWithModifyingKeyword(string sql)
    {
        var statement = sql.AsSpan().TrimStart();
        return StartsWithKeyword(statement, "INSERT") ||
            StartsWithKeyword(statement, "UPDATE") ||
            StartsWithKeyword(statement, "DELETE") ||
            StartsWithKeyword(statement, "MERGE");
    }

    private static bool StartsWithKeyword(ReadOnlySpan<char> statement, ReadOnlySpan<char> keyword) =>
        statement.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) &&
        (statement.Length == keyword.Length ||
            !(char.IsLetterOrDigit(statement[keyword.Length]) || statement[keyword.Length] == '_'));

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

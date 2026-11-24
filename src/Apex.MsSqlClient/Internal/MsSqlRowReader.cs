using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks.Sources;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MsSqlClient.Internal;

internal sealed class MsSqlResultReader :
    MsSqlRowReader,
    ISqlResultBoundaryReader,
    ISqlRecordsAffectedReader
{
    internal MsSqlResultReader(
        MsSqlConnection connection,
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken,
        string[]? parameterNames = null)
        : base(connection, sql, parameters, cancellationToken, preserveResultBoundaries: true, parameterNames)
    {
    }

    internal MsSqlResultReader(
        MsSqlConnection connection,
        MsSqlPreparedStatement statement,
        SqlParameters parameters,
        CancellationToken cancellationToken,
        string[]? parameterNames = null)
        : base(connection, statement, parameters, cancellationToken, preserveResultBoundaries: true, parameterNames)
    {
    }

    int ISqlRecordsAffectedReader.RecordsAffected => RecordsAffected;
}

internal class MsSqlRowReader : ISqlRowReader, IValueTaskSource<bool>
{
    private readonly MsSqlConnection _connection;
    private readonly AsyncAutoResetEvent _advance = new();
    private readonly SqlResultReaderState? _results;
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
    private TdsColumn[] _tdsColumns = Array.Empty<TdsColumn>();
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
    private bool _preparesHandle;
    private int _disposed;

    internal MsSqlRowReader(
        MsSqlConnection connection,
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken,
        bool preserveResultBoundaries = false,
        string[]? parameterNames = null)
      : this(connection, sql, statement: null, parameters, cancellationToken, preserveResultBoundaries, parameterNames)
    {
    }

    internal MsSqlRowReader(
        MsSqlConnection connection,
        MsSqlPreparedStatement statement,
        SqlParameters parameters,
        CancellationToken cancellationToken,
        bool preserveResultBoundaries = false,
        string[]? parameterNames = null)
      : this(connection, statement.Sql, statement, parameters, cancellationToken, preserveResultBoundaries, parameterNames)
    {
    }

    private MsSqlRowReader(
        MsSqlConnection connection,
        string sql,
        MsSqlPreparedStatement? statement,
        SqlParameters parameters,
        CancellationToken cancellationToken,
        bool preserveResultBoundaries,
        string[]? parameterNames)
    {
        _connection = connection;
        _statement = statement;
        _results = preserveResultBoundaries ? new SqlResultReaderState() : null;
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
                CancellationToken.None,
                parameterNames).ConfigureAwait(false);
              }
              else
              {
                  _preparesHandle = await connection.WritePreparedRequestAsync(
                statement,
                parameters,
                CancellationToken.None,
                parameterNames).ConfigureAwait(false);
              }
          },
          receiveToken => PumpAsync(receiveToken),
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
        short version;
        lock (_gate)
        {
            ThrowIfError();
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

            if (_hasCurrent && TryReadBufferedRow())
            {
                return ValueTask.FromResult(true);
            }

            advance = _hasCurrent;
            _readPending = true;
            _readCompletion.Reset();
            version = _readCompletion.Version;
            _readCancellationToken = cancellationToken;
            _readCancellation = cancellationToken.CanBeCanceled
              ? cancellationToken.Register(_cancelAction)
              : default;
        }

        if (advance)
        {
            // The pump resumes on this thread and handles what is already received (a token other
            // than a row, a row split across packets, the end of the result) before this returns.
            // The read is then already complete and its await does not yield; the pump only goes
            // asynchronous, and the read with it, when it must wait for the network.
            _advance.Set(runContinuationInline: true);
        }

        return new ValueTask<bool>(this, version);
    }

    /// <summary>
    /// Replaces the delivered row with the next one when that row is already received in full,
    /// without waking the pump. Runs under <see cref="_gate"/> once the current row is delivered:
    /// the pump then has nothing left to do with the protocol until it is resumed through
    /// <see cref="_advance"/>, which only happens after this reader stops taking this path
    /// (a read that falls through, <see cref="Cancel"/>, <see cref="NextResultAsync"/> or dispose).
    /// Anything other than a complete row leaves the reader position untouched for the pump.
    /// The row may be decoded straight into <see cref="_current"/>, even partly on failure: once the
    /// consumer asks for the next row the delivered one is no longer valid, and a read that falls
    /// through stays pending until the pump has decoded the row again.
    /// </summary>
    private bool TryReadBufferedRow()
    {
        if (_stopped ||
            _canceled ||
            _attention?.IsCancellationRequested == true ||
            _tdsColumns.Length == 0 ||
            _results is { HasPendingTransition: true })
        {
            return false;
        }

        try
        {
            return TdsStreamingTokenReader.TryReadBufferedRow(_connection.Reader, _tdsColumns, _current);
        }
        catch (Exception)
        {
            // An invalid packet header throws before anything is consumed: the pump reads the
            // same bytes again and reports the failure the usual way, breaking the connection.
            return false;
        }
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

        // Result-preserving (ADO.NET) readers drain like the PostgreSQL and MySQL readers so the
        // remaining statements of a batch still execute and a reader parked at its final result
        // boundary does not pay an attention round trip. Explicit cancellation and timeouts still
        // reach Cancel() through the operation token.
        if (_results is null)
        {
            Cancel();
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
        }
    }

    internal void CopyCurrentTo(SqlRowPageBuilder page)
    {
        EnsureCurrent();
        page.Add(_current.WrittenSpan);
    }

    private async ValueTask<bool> PumpAsync(CancellationToken shutdownToken)
    {
        List<MsSqlInfo> errors = [];
        var final = false;
        var drainingError = false;
        var attention =
          _connection.BeginAttention(_operationCancellationToken, AbortForShutdown);
        bool cancelBeforeAttention;
        CancellationToken cancelToken;
        lock (_gate)
        {
            _attention = attention;
            cancelBeforeAttention = _cancelBeforeAttention;
            cancelToken = _cancellationToken;
        }

        if (cancelBeforeAttention)
        {
            attention.Cancel(cancelToken);
        }

        using var shutdownRegistration = shutdownToken.CanBeCanceled
          ? shutdownToken.Register(
            static state =>
            {
                var (reader, activeAttention) = ((MsSqlRowReader, MsSqlConnection.AttentionState))state!;
                if (reader._connection.Scheduler.IsStopped || reader._connection.IsDisposed)
                {
                    activeAttention.AbortRead();
                }
            },
            (this, attention))
          : default;

        try
        {
            while (true)
            {
                if (_connection.Scheduler.IsStopped || _connection.IsDisposed)
                {
                    throw new OperationCanceledException(shutdownToken);
                }
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
                            var shape = await tokens.ReadColumnMetadataAsync(
                              _connection.ResultShapes).ConfigureAwait(false);
                            _tdsColumns = shape.Columns;
                            if (!drainingError && !attention.IsCancellationRequested)
                            {
                                _columns = shape.SqlColumns;
                                _ordinals = shape.Ordinals;
                                _currentResultIndex = _resultCount;
                                _hasCurrentResult = true;
                                _resultSetGeneration++;
                                ResultStarted();
                            }

                            break;
                        case TdsTokenType.Row:
                        case TdsTokenType.NbcRow:
                            if (_tdsColumns.Length == 0)
                            {
                                throw new InvalidDataException(
                                  "SQL Server sent a ROW token before COLMETADATA.");
                            }

                            await tokens.ReadRowAsync(
                              _tdsColumns,
                              nullCompressed: token == TdsTokenType.NbcRow,
                              _current).ConfigureAwait(false);
                            var retained = false;
                            var attentionRequested = attention.IsCancellationRequested;
                            lock (_gate)
                            {
                                // A pending NextResultAsync skips the rest of this result.
                                if (!_stopped &&
                                    !drainingError &&
                                    !attentionRequested &&
                                    _results is not { SkipsRows: true })
                                {
                                    _hasCurrent = true;
                                    _currentDelivered = _results?.PublishRow() ?? false;
                                    retained = true;
                                }
                            }

                            if (retained)
                            {
                                SignalRead(result: true, error: null);
                                attention.SetConsumerParked(true);
                                try
                                {
                                    if (_connection.Scheduler.IsStopped || _connection.IsDisposed)
                                    {
                                        throw new OperationCanceledException(shutdownToken);
                                    }

                                    await _advance.WaitAsync().ConfigureAwait(false);
                                }
                                finally
                                {
                                    attention.SetConsumerParked(false);
                                }

                                if (_connection.Scheduler.IsStopped || _connection.IsDisposed)
                                {
                                    throw new OperationCanceledException(shutdownToken);
                                }

                                DisposeCurrent();
                            }

                            break;
                        case TdsTokenType.Done:
                        case TdsTokenType.DoneProc:
                        case TdsTokenType.DoneInProc:
                            var done = await tokens.ReadDoneAsync().ConfigureAwait(false);
                            final |= (done.Status & TdsDoneStatus.More) == 0;
                            if ((done.Status & TdsDoneStatus.Error) != 0)
                            {
                                drainingError = true;
                            }

                            var hadCurrentResult = _hasCurrentResult;
                            var hasCountResult = (done.Status & TdsDoneStatus.Count) != 0;
                            // Like SqlClient, count every DONE_COUNT except those reported for SELECT
                            // statements, so OUTPUT clauses and multi-statement batches are classified
                            // by the server rather than by the batch text.
                            if (_results is not null &&
                                hasCountResult &&
                                done.CurrentCommand != TdsSelectCommand)
                            {
                                _results.AddRecordsAffected(done.RowCount);
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
                            // A failed statement must not surface as an empty result or complete a
                            // started result without error: either would park the pump at the boundary
                            // and defer the server error until NextResult or dispose.
                            var suppressingResults = drainingError || attention.IsCancellationRequested;
                            var publishSyntheticResult = _results is not null &&
                                !suppressingResults &&
                                hasSyntheticResult;
                            if (publishSyntheticResult)
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

                            if (_results is not null && !suppressingResults && (hadCurrentResult || publishSyntheticResult))
                            {
                                attention.SetConsumerParked(true);
                                try
                                {
                                    if (_connection.Scheduler.IsStopped || _connection.IsDisposed)
                                    {
                                        throw new OperationCanceledException(shutdownToken);
                                    }

                                    await ResultCompletedAsync().ConfigureAwait(false);
                                }
                                finally
                                {
                                    attention.SetConsumerParked(false);
                                }

                                if (_connection.Scheduler.IsStopped || _connection.IsDisposed)
                                {
                                    throw new OperationCanceledException(shutdownToken);
                                }
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
            lock (_gate)
            {
                _attention = null;
            }

            _connection.EndAttention(attention);
            attention.Dispose();
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
        MsSqlConnection.AttentionState? attention;
        CancellationToken cancellationToken;
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
            cancellationToken = _cancellationToken;
            advance = !_hasCurrent || !_currentDelivered;
            attention = _attention;
            if (attention is null)
            {
                _cancelBeforeAttention = true;
            }
        }

        attention?.Cancel(cancellationToken);
        if (advance) _advance.Set();
        _results?.Advance.Set();
    }

    private void AbortForShutdown()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _stopped = true;
        }

        _advance.Set();
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

    // TDS DONE CurCmd value for SELECT statements (TdsEnums.SELECT in SqlClient).
    private const ushort TdsSelectCommand = 0xC1;

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

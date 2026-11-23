using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Apex.SqlClient.Internal;

/// <summary>
/// A fixed set of pipelined connections that hands out operations round-robin.
/// </summary>
/// <remarks>
/// <para>
/// A connection that stops being usable (a fatal protocol error, a server restart, a network
/// failure) is skipped by dispatch and replaced in the background: the pool reconnects with
/// backoff, prepares every pool-wide statement on the new connection, and only then swaps it
/// into the slot. Operations already running on the dead connection still fail with its error;
/// the pool never retries them because they may not be idempotent.
/// </para>
/// <para>
/// The dispatch path does one interlocked increment, one volatile read, and one predicate call.
/// Everything else happens on the slow path, which only runs once a connection is unusable.
/// </para>
/// </remarks>
internal sealed class SqlPipelinePool : ISqlPipelinePool
{
    internal static readonly TimeSpan DefaultInitialReconnectDelay = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan DefaultMaximumReconnectDelay = TimeSpan.FromSeconds(5);

    private static readonly Func<ISqlConnection, bool> s_alwaysUsable = static _ => true;

    private readonly ISqlConnection[] _connections;
    private readonly Func<CancellationToken, ValueTask<ISqlConnection>> _connectionFactory;
    private readonly Func<ISqlConnection, bool> _isUsable;
    private readonly TimeSpan _initialReconnectDelay;
    private readonly TimeSpan _maximumReconnectDelay;
    private readonly int[] _replacing;
    private readonly Task?[] _replacements;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _disposeGate = new();
    private readonly object _statementsGate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly HashSet<PipelinePreparedStatement> _statements = [];
    private int _nextConnection = -1;
    private int _disposed;
    private Task? _disposeTask;

    private SqlPipelinePool(
        ISqlConnection[] connections,
        Func<CancellationToken, ValueTask<ISqlConnection>> connectionFactory,
        Func<ISqlConnection, bool> isUsable,
        TimeSpan initialReconnectDelay,
        TimeSpan maximumReconnectDelay)
    {
        _connections = connections;
        _connectionFactory = connectionFactory;
        _isUsable = isUsable;
        _initialReconnectDelay = initialReconnectDelay;
        _maximumReconnectDelay = maximumReconnectDelay;
        _replacing = new int[connections.Length];
        _replacements = new Task?[connections.Length];
    }

    public int Size => _connections.Length;

    internal static ValueTask<SqlPipelinePool> CreateAsync(
        SqlPipelinePoolOptions options,
        Func<CancellationToken, ValueTask<ISqlConnection>> connectionFactory,
        CancellationToken cancellationToken) =>
      CreateAsync(options, connectionFactory, isUsable: null, cancellationToken);

    // isUsable reports whether a connection can still run commands. It runs on every dispatch,
    // so it must be cheap and must not allocate. When it is null, connections are assumed usable
    // forever and are never replaced.
    internal static async ValueTask<SqlPipelinePool> CreateAsync(
        SqlPipelinePoolOptions options,
        Func<CancellationToken, ValueTask<ISqlConnection>> connectionFactory,
        Func<ISqlConnection, bool>? isUsable,
        CancellationToken cancellationToken,
        TimeSpan? initialReconnectDelay = null,
        TimeSpan? maximumReconnectDelay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ConnectionCount);

        var connections = await CreateAllAsync(
            options.ConnectionCount,
            connectionFactory,
            static connection => connection.DisposeAsync(),
            cancellationToken).ConfigureAwait(false);
        var initialDelay = initialReconnectDelay ?? DefaultInitialReconnectDelay;
        return new SqlPipelinePool(
            connections,
            connectionFactory,
            isUsable ?? s_alwaysUsable,
            initialDelay,
            TimeSpan.FromTicks(Math.Max(
                initialDelay.Ticks,
                (maximumReconnectDelay ?? DefaultMaximumReconnectDelay).Ticks)));
    }

    public ValueTask<SqlRowSet> QueryAsync(
        string sql,
        CancellationToken cancellationToken = default) =>
      GetConnection().QueryAsync(sql, cancellationToken);

    public ValueTask<SqlRowSet> QueryAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken = default) =>
      GetConnection().QueryAsync(sql, parameters, cancellationToken);

    public ValueTask<SqlCommandResult> ExecuteAsync(
        string sql,
        CancellationToken cancellationToken = default) =>
      GetConnection().ExecuteAsync(sql, cancellationToken);

    public ValueTask<SqlCommandResult> ExecuteAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken = default) =>
      GetConnection().ExecuteAsync(sql, parameters, cancellationToken);

    public async IAsyncEnumerable<SqlRow> StreamAsync(
        string sql,
        SqlParameters parameters = default,
        int fetchSize = 50,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var row in GetConnection().StreamAsync(
                           sql,
                           parameters,
                           fetchSize,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    public async ValueTask<ISqlPreparedStatement> PrepareAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            // A dead slot gets a hole instead of failing the whole prepare. The replacement for
            // that slot runs under _lifecycle too, so it starts after this statement is
            // registered and fills the hole when it swaps the new connection in. When every slot
            // is dead, prepare on all of them anyway so the caller sees the real error.
            var connections = new ISqlConnection[_connections.Length];
            var usable = new bool[connections.Length];
            var anyUsable = false;
            for (var i = 0; i < connections.Length; i++)
            {
                connections[i] = Volatile.Read(ref _connections[i]);
                usable[i] = _isUsable(connections[i]);
                anyUsable |= usable[i];
            }

            for (var i = 0; i < connections.Length; i++)
            {
                if (!usable[i])
                {
                    ScheduleReplacement(i, connections[i]);
                }
            }

            var statements = await CreateAllAsync<ISqlPreparedStatement?>(
                connections.Length,
                async (index, token) => !anyUsable || usable[index]
                    ? await connections[index].PrepareAsync(sql, token).ConfigureAwait(false)
                    : null,
                static statement => statement?.DisposeAsync() ?? ValueTask.CompletedTask,
                cancellationToken).ConfigureAwait(false);
            var result = new PipelinePreparedStatement(
                this,
                sql,
                statements);
            var disposeResult = false;
            lock (_disposeGate)
            {
                if (_disposed != 0)
                {
                    disposeResult = true;
                }
                else
                {
                    lock (_statementsGate)
                    {
                        _statements.Add(result);
                    }
                }
            }

            if (disposeResult)
            {
                await result.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(GetType().Name);
            }

            return result;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposed, 1);
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Stop the replacements before taking _lifecycle: a replacement holds it while it
        // re-prepares, and it observes the cancellation and gives up its new connection.
        // CancelAsync runs the callbacks off this thread, so none run under _disposeGate.
        await _disposeCancellation.CancelAsync().ConfigureAwait(false);
        foreach (var replacement in _replacements)
        {
            if (replacement is not null)
            {
                await replacement.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        List<Exception>? errors = null;
        try
        {
            PipelinePreparedStatement[] statements;
            lock (_statementsGate)
            {
                statements = _statements.ToArray();
            }

            foreach (var statement in statements)
            {
                try
                {
                    await statement.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (errors ??= []).Add(exception);
                }
            }

            foreach (var connection in _connections)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (errors ??= []).Add(exception);
                }
            }
        }
        finally
        {
            _lifecycle.Release();
            _disposeCancellation.Dispose();
        }

        if (errors is not null)
        {
            throw new AggregateException(errors);
        }
    }

    private ISqlConnection GetConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var index = (int)((uint)Interlocked.Increment(ref _nextConnection) %
                          (uint)_connections.Length);
        var connection = Volatile.Read(ref _connections[index]);
        return _isUsable(connection) ? connection : GetUsableConnectionSlow(index, connection);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ISqlConnection GetUsableConnectionSlow(int index, ISqlConnection unusable)
    {
        ScheduleReplacement(index, unusable);
        var count = _connections.Length;
        for (var offset = 1; offset < count; offset++)
        {
            var candidateIndex = (index + offset) % count;
            var candidate = Volatile.Read(ref _connections[candidateIndex]);
            if (_isUsable(candidate))
            {
                return candidate;
            }

            ScheduleReplacement(candidateIndex, candidate);
        }

        // Every slot is down. Hand back the original pick so the caller gets that connection's
        // own terminal error instead of a generic one, and let the replacements catch up.
        return unusable;
    }

    private ISqlConnection ReadConnection(int index) => Volatile.Read(ref _connections[index]);

    private bool IsUsable(ISqlConnection connection) => _isUsable(connection);

    private void ScheduleReplacement(int index, ISqlConnection unusable)
    {
        if (Volatile.Read(ref _replacing[index]) != 0 ||
            !ReferenceEquals(Volatile.Read(ref _connections[index]), unusable) ||
            Interlocked.CompareExchange(ref _replacing[index], 1, 0) != 0)
        {
            return;
        }

        lock (_disposeGate)
        {
            // Re-check under the flag: a replacement that just finished may have swapped the slot
            // after the caller read it, and dispose must not miss a task started after it began.
            if (_disposed != 0 ||
                !ReferenceEquals(Volatile.Read(ref _connections[index]), unusable))
            {
                Volatile.Write(ref _replacing[index], 0);
                return;
            }

            _replacements[index] = Task.Run(() => ReplaceAsync(index, unusable));
        }
    }

    private async Task ReplaceAsync(int index, ISqlConnection unusable)
    {
        var cancellationToken = _disposeCancellation.Token;
        var delay = _initialReconnectDelay;
        try
        {
            while (true)
            {
                var replacement = await TryConnectAsync(cancellationToken).ConfigureAwait(false);
                if (replacement is not null &&
                    await TryInstallAsync(index, unusable, replacement, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return;
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _maximumReconnectDelay.Ticks));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref _replacing[index], 0);
        }
    }

    private async ValueTask<ISqlConnection?> TryConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _connectionFactory(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async ValueTask<bool> TryInstallAsync(
        int index,
        ISqlConnection unusable,
        ISqlConnection replacement,
        CancellationToken cancellationToken)
    {
        var installed = false;
        List<ISqlPreparedStatement>? stale = null;
        try
        {
            await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed != 0 || !_isUsable(replacement))
                {
                    return false;
                }

                PipelinePreparedStatement[] statements;
                lock (_statementsGate)
                {
                    statements = _statements.ToArray();
                }

                var prepared = new ISqlPreparedStatement?[statements.Length];
                try
                {
                    for (var i = 0; i < statements.Length; i++)
                    {
                        try
                        {
                            prepared[i] = await replacement
                                .PrepareAsync(statements[i].Sql, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (Exception) when (
                            !cancellationToken.IsCancellationRequested && _isUsable(replacement))
                        {
                            // The connection is fine but this statement no longer prepares
                            // (for example, its table was dropped). Leave a hole for this slot;
                            // the statement keeps running on the other connections.
                        }
                    }

                    if (!_isUsable(replacement))
                    {
                        throw new SqlClientException(
                            "The replacement connection failed while preparing statements.");
                    }
                }
                catch (Exception)
                {
                    foreach (var statement in prepared)
                    {
                        await DisposeQuietlyAsync(statement).ConfigureAwait(false);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    return false;
                }

                // Publish each statement before the connection. GetStatement reads the connection
                // first, so once it sees the new connection it also sees the new statements.
                for (var i = 0; i < statements.Length; i++)
                {
                    var previous = statements[i].Install(index, prepared[i]);
                    if (previous is not null)
                    {
                        (stale ??= []).Add(previous);
                    }
                }

                Volatile.Write(ref _connections[index], replacement);
                installed = true;
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        finally
        {
            if (!installed)
            {
                await DisposeQuietlyAsync(replacement).ConfigureAwait(false);
            }
        }

        if (stale is not null)
        {
            foreach (var statement in stale)
            {
                await DisposeQuietlyAsync(statement).ConfigureAwait(false);
            }
        }

        await DisposeQuietlyAsync(unusable).ConfigureAwait(false);
        return true;
    }

    private static async ValueTask DisposeQuietlyAsync(IAsyncDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the resource belongs to a connection that already failed.
        }
    }

    private void Remove(PipelinePreparedStatement statement)
    {
        lock (_statementsGate)
        {
            _statements.Remove(statement);
        }
    }

    private static ValueTask<T[]> CreateAllAsync<T>(
        int count,
        Func<CancellationToken, ValueTask<T>> factory,
        Func<T, ValueTask> dispose,
        CancellationToken cancellationToken) =>
      CreateAllAsync(count, (_, token) => factory(token), dispose, cancellationToken);

    private static async ValueTask<T[]> CreateAllAsync<T>(
        int count,
        Func<int, CancellationToken, ValueTask<T>> factory,
        Func<T, ValueTask> dispose,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource cancellation =
          CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new Task<T>[count];
        for (var i = 0; i < pending.Length; i++)
        {
            pending[i] = RunFactoryAsync(i, factory, cancellation);
        }

        try
        {
            return await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception creationError)
        {
            List<Exception>? cleanupErrors = null;
            foreach (var task in pending)
            {
                if (task.Status != TaskStatus.RanToCompletion)
                {
                    continue;
                }

                try
                {
                    await dispose(task.Result).ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    (cleanupErrors ??= []).Add(cleanupError);
                }
            }

            if (cleanupErrors is not null)
            {
                cleanupErrors.Insert(0, creationError);
                throw new AggregateException(cleanupErrors);
            }

            ExceptionDispatchInfo.Capture(creationError).Throw();
            throw;
        }
    }

    private static async Task<T> RunFactoryAsync<T>(
        int index,
        Func<int, CancellationToken, ValueTask<T>> factory,
        CancellationTokenSource cancellation)
    {
        try
        {
            return await factory(index, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception creationError)
        {
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception cancellationError)
            {
                throw new AggregateException(creationError, cancellationError);
            }

            throw;
        }
    }

    private sealed class PipelinePreparedStatement : ISqlPreparedStatement
    {
        private readonly SqlPipelinePool _owner;

        // One entry per pool slot, prepared on the connection in that slot. An entry is null
        // while its slot has no usable statement (the slot was dead at prepare time, or the
        // statement failed to re-prepare on the replacement connection).
        private readonly ISqlPreparedStatement?[] _statements;
        private readonly object _disposeGate = new();
        private int _nextStatement = -1;
        private int _disposed;
        private Task? _disposeTask;

        internal PipelinePreparedStatement(
            SqlPipelinePool owner,
            string sql,
            ISqlPreparedStatement?[] statements)
        {
            _owner = owner;
            Sql = sql;
            _statements = statements;
        }

        public string Sql { get; }

        public ValueTask<SqlRowSet> QueryAsync(
            SqlParameters parameters = default,
            CancellationToken cancellationToken = default) =>
          GetStatement().QueryAsync(parameters, cancellationToken);

        public ValueTask<TState> CollectAsync<TState>(
            TState state,
            Action<TState, SqlRow> collector,
            SqlParameters parameters = default,
            CancellationToken cancellationToken = default) =>
          GetStatement().CollectAsync(state, collector, parameters, cancellationToken);

        public ValueTask<SqlCommandResult> ExecuteAsync(
            SqlParameters parameters = default,
            CancellationToken cancellationToken = default) =>
          GetStatement().ExecuteAsync(parameters, cancellationToken);

        public ValueTask<IReadOnlyList<SqlCommandResult>> ExecuteBatchAsync(
            IReadOnlyList<SqlParameters> batch,
            CancellationToken cancellationToken = default) =>
          GetStatement().ExecuteBatchAsync(batch, cancellationToken);

        public ValueTask<ISqlCursor> OpenCursorAsync(
            SqlParameters parameters = default,
            int fetchSize = 50,
            CancellationToken cancellationToken = default) =>
          GetStatement().OpenCursorAsync(parameters, fetchSize, cancellationToken);

        public ValueTask<ISqlRowReader> ExecuteReaderAsync(
            SqlParameters parameters = default,
            CancellationToken cancellationToken = default) =>
          GetStatement().ExecuteReaderAsync(parameters, cancellationToken);

        public async IAsyncEnumerable<SqlRow> StreamAsync(
            SqlParameters parameters = default,
            int fetchSize = 50,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var row in GetStatement().StreamAsync(
                               parameters,
                               fetchSize,
                               cancellationToken).ConfigureAwait(false))
            {
                yield return row;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                if (_disposeTask is null)
                {
                    Volatile.Write(ref _disposed, 1);
                    _disposeTask = DisposeCoreAsync();
                }

                return new ValueTask(_disposeTask);
            }
        }

        /// <summary>
        /// Puts the statement prepared on a slot's replacement connection in place and returns
        /// the one it supersedes, for the caller to dispose. After this statement is disposed it
        /// installs nothing and hands <paramref name="statement"/> back instead.
        /// </summary>
        internal ISqlPreparedStatement? Install(int index, ISqlPreparedStatement? statement)
        {
            lock (_disposeGate)
            {
                return _disposed != 0
                    ? statement
                    : Interlocked.Exchange(ref _statements[index], statement);
            }
        }

        private async Task DisposeCoreAsync()
        {
            List<Exception>? errors = null;
            try
            {
                for (var i = 0; i < _statements.Length; i++)
                {
                    var statement = Volatile.Read(ref _statements[i]);
                    if (statement is null)
                    {
                        continue;
                    }

                    try
                    {
                        await statement.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        (errors ??= []).Add(exception);
                    }
                }
            }
            finally
            {
                _owner.Remove(this);
            }

            if (errors is not null)
            {
                throw new AggregateException(errors);
            }
        }

        private ISqlPreparedStatement GetStatement()
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var index = (int)((uint)Interlocked.Increment(ref _nextStatement) %
                              (uint)_statements.Length);

            // Read the connection before the statement: a replacement publishes the statement
            // first, so a usable connection here is never paired with a stale statement.
            var connection = _owner.ReadConnection(index);
            var statement = Volatile.Read(ref _statements[index]);
            return statement is not null && _owner.IsUsable(connection)
                ? statement
                : GetStatementSlow(index, connection, statement);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ISqlPreparedStatement GetStatementSlow(
            int index,
            ISqlConnection connection,
            ISqlPreparedStatement? statement)
        {
            if (!_owner.IsUsable(connection))
            {
                _owner.ScheduleReplacement(index, connection);
            }

            var count = _statements.Length;
            ISqlPreparedStatement? fallback = statement;
            for (var offset = 1; offset < count; offset++)
            {
                var candidateIndex = (index + offset) % count;
                var candidateConnection = _owner.ReadConnection(candidateIndex);
                var candidate = Volatile.Read(ref _statements[candidateIndex]);
                if (candidate is not null && _owner.IsUsable(candidateConnection))
                {
                    return candidate;
                }

                if (!_owner.IsUsable(candidateConnection))
                {
                    _owner.ScheduleReplacement(candidateIndex, candidateConnection);
                }

                fallback ??= candidate;
            }

            // Nothing usable. A statement on a dead connection fails with that connection's own
            // terminal error, which says more than anything this pool could throw.
            return fallback ?? throw new SqlClientException(
                "No connection in the pipeline pool can run this prepared statement right now; " +
                "the pool is reconnecting.");
        }
    }
}

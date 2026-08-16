using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Apex.SqlClient.Internal;

internal sealed class SqlPipelinePool : ISqlPipelinePool
{
    private readonly ISqlConnection[] _connections;
    private readonly object _disposeGate = new();
    private readonly object _statementsGate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly HashSet<PipelinePreparedStatement> _statements = [];
    private int _nextConnection = -1;
    private int _disposed;
    private Task? _disposeTask;

    private SqlPipelinePool(ISqlConnection[] connections)
    {
        _connections = connections;
    }

    public int Size => _connections.Length;

    internal static async ValueTask<SqlPipelinePool> CreateAsync(
        SqlPipelinePoolOptions options,
        Func<CancellationToken, ValueTask<ISqlConnection>> connectionFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ConnectionCount);

        var connections = await CreateAllAsync(
            options.ConnectionCount,
            connectionFactory,
            static connection => connection.DisposeAsync(),
            cancellationToken).ConfigureAwait(false);
        return new SqlPipelinePool(connections);
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
            var statements = await CreateAllAsync(
                _connections.Length,
                (index, token) => _connections[index].PrepareAsync(sql, token),
                static statement => statement.DisposeAsync(),
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
        return _connections[index];
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
        private readonly ISqlPreparedStatement[] _statements;
        private readonly object _disposeGate = new();
        private int _nextStatement = -1;
        private int _disposed;
        private Task? _disposeTask;

        internal PipelinePreparedStatement(
            SqlPipelinePool owner,
            string sql,
            ISqlPreparedStatement[] statements)
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

        private async Task DisposeCoreAsync()
        {
            List<Exception>? errors = null;
            try
            {
                foreach (var statement in _statements)
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
            return _statements[index];
        }
    }
}

using Apex.SqlClient.Internal;

namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class SqlPipelinePoolRecoveryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    internal static readonly AsyncLocal<string?> Flow = new();

    [TestMethod]
    public async Task DeadConnectionIsSkippedWhileItsReplacementIsPending()
    {
        ConnectionFactory factory = new(initialConnections: 2);
        await using var pool = await factory.CreatePoolAsync();
        var (first, second) = (factory.Created[0], factory.Created[1]);
        factory.Block();

        first.Kill();
        for (var i = 0; i < 10; i++)
        {
            await pool.QueryAsync("SELECT 1");
        }

        Assert.AreEqual(0, first.QueryCount);
        Assert.AreEqual(10, second.QueryCount);
        await factory.WaitForAttemptsAsync(1);
        Assert.AreEqual(1, factory.ReplacementAttempts);
        Assert.AreEqual(2, pool.Size);
    }

    [TestMethod]
    public async Task ReplacementSwapsInAndDisposesTheDeadConnection()
    {
        ConnectionFactory factory = new(initialConnections: 2);
        await using var pool = await factory.CreatePoolAsync();
        var (first, second) = (factory.Created[0], factory.Created[1]);

        first.Kill();
        await pool.QueryAsync("SELECT 1");
        await pool.QueryAsync("SELECT 1");
        await first.Disposed.Task.WaitAsync(Timeout);

        var replacement = factory.Created[2];
        var before = second.QueryCount;
        var replacementBefore = replacement.QueryCount;
        for (var i = 0; i < 10; i++)
        {
            await pool.QueryAsync("SELECT 1");
        }

        Assert.AreEqual(1, first.DisposeCount);
        Assert.AreEqual(replacementBefore + 5, replacement.QueryCount);
        Assert.AreEqual(before + 5, second.QueryCount);
        Assert.AreEqual(1, factory.ReplacementAttempts);
    }

    [TestMethod]
    public async Task PoolWidePreparedStatementIsPreparedAgainOnTheReplacement()
    {
        ConnectionFactory factory = new(initialConnections: 2);
        await using var pool = await factory.CreatePoolAsync();
        var (first, second) = (factory.Created[0], factory.Created[1]);
        await using var statement = await pool.PrepareAsync("SELECT value");
        factory.Block();

        first.Kill();
        for (var i = 0; i < 4; i++)
        {
            await statement.ExecuteAsync();
        }

        Assert.AreEqual(0, first.Statements[0].ExecuteCount);
        Assert.AreEqual(4, second.Statements[0].ExecuteCount);

        factory.Release();
        await first.Disposed.Task.WaitAsync(Timeout);
        var replacement = factory.Created[2];
        for (var i = 0; i < 4; i++)
        {
            await statement.ExecuteAsync();
        }

        Assert.HasCount(1, replacement.Statements);
        Assert.AreEqual("SELECT value", replacement.Statements[0].Sql);
        Assert.AreEqual(2, replacement.Statements[0].ExecuteCount);
        Assert.AreEqual(6, second.Statements[0].ExecuteCount);
        Assert.AreEqual(1, first.Statements[0].DisposeCount);

        await statement.DisposeAsync();
        Assert.AreEqual(1, replacement.Statements[0].DisposeCount);
        Assert.AreEqual(1, second.Statements[0].DisposeCount);
    }

    [TestMethod]
    public async Task StatementPreparedDuringAnOutageLandsOnTheReplacement()
    {
        ConnectionFactory factory = new(initialConnections: 2);
        await using var pool = await factory.CreatePoolAsync();
        var (first, second) = (factory.Created[0], factory.Created[1]);
        factory.Block();

        first.Kill();
        await using var statement = await pool.PrepareAsync("SELECT value");

        Assert.IsEmpty(first.Statements);
        Assert.HasCount(1, second.Statements);
        await statement.ExecuteAsync();
        await statement.ExecuteAsync();
        Assert.AreEqual(2, second.Statements[0].ExecuteCount);

        factory.Release();
        await first.Disposed.Task.WaitAsync(Timeout);
        var replacement = factory.Created[2];
        for (var i = 0; i < 4; i++)
        {
            await statement.ExecuteAsync();
        }

        Assert.HasCount(1, replacement.Statements);
        Assert.AreEqual(2, replacement.Statements[0].ExecuteCount);
        Assert.AreEqual(4, second.Statements[0].ExecuteCount);
    }

    [TestMethod]
    public async Task FailedReconnectsAreRetriedByASingleTaskPerSlot()
    {
        ConnectionFactory factory = new(initialConnections: 2) { FailuresBeforeSuccess = 3 };
        await using var pool = await factory.CreatePoolAsync();
        var first = factory.Created[0];

        first.Kill();
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                await pool.QueryAsync("SELECT 1");
            }
        })));
        await first.Disposed.Task.WaitAsync(Timeout);

        Assert.AreEqual(4, factory.ReplacementAttempts);
        Assert.AreEqual(1, factory.MaximumConcurrentAttempts);
        Assert.HasCount(3, factory.Created);
    }

    [TestMethod]
    public async Task ConnectionThatDiesWhileStatementsArePreparedIsReplacedAgain()
    {
        ConnectionFactory factory = new(initialConnections: 2);
        await using var pool = await factory.CreatePoolAsync();
        var first = factory.Created[0];
        await using var statement = await pool.PrepareAsync("SELECT value");
        factory.DieDuringNextPrepare = true;

        first.Kill();
        await pool.QueryAsync("SELECT 1");
        await first.Disposed.Task.WaitAsync(Timeout);

        Assert.HasCount(4, factory.Created);
        Assert.AreEqual(1, factory.Created[2].DisposeCount);
        var replacement = factory.Created[3];
        Assert.IsTrue(replacement.IsAlive);
        Assert.HasCount(1, replacement.Statements);
        await statement.ExecuteAsync();
        await statement.ExecuteAsync();
        Assert.AreEqual(1, replacement.Statements[0].ExecuteCount);
    }

    [TestMethod]
    public async Task StatementThatNoLongerPreparesKeepsRunningOnHealthyConnections()
    {
        ConnectionFactory factory = new(initialConnections: 2);
        await using var pool = await factory.CreatePoolAsync();
        var (first, second) = (factory.Created[0], factory.Created[1]);
        await using var dropped = await pool.PrepareAsync("SELECT dropped");
        await using var kept = await pool.PrepareAsync("SELECT kept");
        factory.RejectedSql = "SELECT dropped";

        first.Kill();
        await pool.QueryAsync("SELECT 1");
        await first.Disposed.Task.WaitAsync(Timeout);

        var replacement = factory.Created[2];
        for (var i = 0; i < 4; i++)
        {
            await dropped.ExecuteAsync();
            await kept.ExecuteAsync();
        }

        Assert.AreEqual(4, second.Statements.Single(s => s.Sql == "SELECT dropped").ExecuteCount);
        Assert.AreEqual(2, replacement.Statements.Single(s => s.Sql == "SELECT kept").ExecuteCount);
        Assert.IsFalse(replacement.Statements.Any(s => s.Sql == "SELECT dropped"));
    }

    [TestMethod]
    public async Task WhenEveryConnectionIsDeadCommandsFailWithTheirErrorUntilRecovery()
    {
        ConnectionFactory factory = new(initialConnections: 2);
        await using var pool = await factory.CreatePoolAsync();
        var (first, second) = (factory.Created[0], factory.Created[1]);
        await using var statement = await pool.PrepareAsync("SELECT value");
        factory.Block();

        first.Kill();
        second.Kill();
        await Assert.ThrowsExactlyAsync<IOException>(() => pool.QueryAsync("SELECT 1").AsTask());
        await Assert.ThrowsExactlyAsync<IOException>(() => statement.ExecuteAsync().AsTask());
        await Assert.ThrowsExactlyAsync<IOException>(
            () => pool.PrepareAsync("SELECT other").AsTask());

        factory.Release();
        await Task.WhenAll(first.Disposed.Task, second.Disposed.Task).WaitAsync(Timeout);
        await pool.QueryAsync("SELECT 1");
        await statement.ExecuteAsync();
        Assert.HasCount(4, factory.Created);
    }

    [TestMethod]
    public async Task ConnectionsThatDieMidRunAreReplacedWhileTrafficContinues()
    {
        ConnectionFactory factory = new(initialConnections: 4);
        await using var pool = await factory.CreatePoolAsync();
        await using var statement = await pool.PrepareAsync("SELECT value");
        var failures = 0;
        var started = 0;

        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 500; i++)
            {
                if (Interlocked.Increment(ref started) == 400)
                {
                    factory.Created[0].Kill();
                    factory.Created[2].Kill();
                }

                try
                {
                    await pool.QueryAsync("SELECT 1");
                    await statement.ExecuteAsync();
                }
                catch (IOException)
                {
                    // Only a command that raced the kill on the dying connection may fail.
                    Interlocked.Increment(ref failures);
                }
            }
        })).ToArray();
        await Task.WhenAll(workers).WaitAsync(Timeout);
        await Task.WhenAll(factory.Created[0].Disposed.Task, factory.Created[2].Disposed.Task)
            .WaitAsync(Timeout);

        Assert.IsLessThanOrEqualTo(16, failures);
        for (var i = 0; i < 8; i++)
        {
            await pool.QueryAsync("SELECT 1");
            await statement.ExecuteAsync();
        }

        var live = Enumerable.Range(0, factory.Created.Count)
            .Select(i => factory.Created[i])
            .Where(static c => c.IsAlive)
            .ToArray();
        Assert.HasCount(4, live);
        Assert.IsTrue(live.All(static c => c.Statements.Count == 1));
    }

    [TestMethod]
    public async Task DisposeCancelsAPendingReconnect()
    {
        ConnectionFactory factory = new(initialConnections: 2);
        var pool = await factory.CreatePoolAsync();
        var first = factory.Created[0];
        factory.Block();

        first.Kill();
        await pool.QueryAsync("SELECT 1");
        await factory.WaitForAttemptsAsync(1);
        await pool.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.IsTrue(factory.Created.All(static c => c.DisposeCount == 1));
        Assert.HasCount(2, factory.Created);
    }

    [TestMethod]
    public async Task DisposeReleasesAConnectionThatArrivesAfterIt()
    {
        ConnectionFactory factory = new(initialConnections: 2) { IgnoreCancellation = true };
        var pool = await factory.CreatePoolAsync();
        var first = factory.Created[0];
        factory.Block();

        first.Kill();
        await pool.QueryAsync("SELECT 1");
        await factory.WaitForAttemptsAsync(1);
        var dispose = pool.DisposeAsync().AsTask();
        Assert.IsFalse(dispose.IsCompleted);
        factory.Release();
        await dispose.WaitAsync(Timeout);

        Assert.HasCount(3, factory.Created);
        Assert.IsTrue(factory.Created.All(static c => c.DisposeCount == 1));
    }

    [TestMethod]
    public async Task RejectsReconnectDelaysThatCannotBackOff()
    {
        (TimeSpan Initial, TimeSpan? Maximum)[] invalid =
        [
            (TimeSpan.Zero, null),
            (TimeSpan.FromMilliseconds(-1), null),
            (TimeSpan.FromMilliseconds(-5), null),
            (TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(5)),
        ];
        var attempts = 0;
        foreach (var (initial, maximum) in invalid)
        {
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
                () => SqlPipelinePool.CreateAsync(
                    new SqlPipelinePoolOptions { ConnectionCount = 1 },
                    _ =>
                    {
                        attempts++;
                        return ValueTask.FromResult<ISqlConnection>(new FakeConnection(new(1)));
                    },
                    static _ => true,
                    CancellationToken.None,
                    initial,
                    maximum).AsTask());
        }

        Assert.AreEqual(0, attempts);
    }

    [TestMethod]
    public async Task ReconnectFailureIsReportedOnceEveryConnectionIsDown()
    {
        ConnectionFactory factory = new(initialConnections: 2) { FailuresBeforeSuccess = int.MaxValue };
        await using var pool = await factory.CreatePoolAsync();
        var (first, second) = (factory.Created[0], factory.Created[1]);
        await using var statement = await pool.PrepareAsync("SELECT value");

        first.Kill();
        second.Kill();
        await Assert.ThrowsExactlyAsync<IOException>(() => pool.QueryAsync("SELECT 1").AsTask());

        // A third attempt means one slot already failed once and recorded why.
        await factory.WaitForAttemptsAsync(3);
        var queryError = await Assert.ThrowsExactlyAsync<SqlClientException>(
            () => pool.QueryAsync("SELECT 1").AsTask());
        Assert.IsInstanceOfType<IOException>(queryError.InnerException);
        Assert.Contains("Connection refused.", queryError.Message);
        var statementError = await Assert.ThrowsExactlyAsync<SqlClientException>(
            () => statement.ExecuteAsync().AsTask());
        Assert.IsInstanceOfType<IOException>(statementError.InnerException);

        factory.FailuresBeforeSuccess = 0;
        await Task.WhenAll(first.Disposed.Task, second.Disposed.Task).WaitAsync(Timeout);
        await pool.QueryAsync("SELECT 1");
        await statement.ExecuteAsync();
    }

    [TestMethod]
    public async Task StatementHoleIsPreparedAgainOnceThePrepareWorks()
    {
        ConnectionFactory factory = new(initialConnections: 1);
        await using var pool = await factory.CreatePoolAsync();
        var first = factory.Created[0];
        await using var statement = await pool.PrepareAsync("SELECT migrating");
        factory.RejectedSql = "SELECT migrating";

        first.Kill();
        await Assert.ThrowsExactlyAsync<IOException>(() => pool.QueryAsync("SELECT 1").AsTask());
        await first.Disposed.Task.WaitAsync(Timeout);
        var replacement = factory.Created[1];
        Assert.IsTrue(replacement.IsAlive);

        var error = await Assert.ThrowsExactlyAsync<SqlClientException>(
            () => statement.ExecuteAsync().AsTask());
        Assert.Contains("could not be prepared", error.Message);
        Assert.AreEqual("relation does not exist", error.InnerException?.Message);
        Assert.DoesNotContain("reconnecting", error.Message);

        factory.RejectedSql = null;
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            try
            {
                await statement.ExecuteAsync();
                break;
            }
            catch (SqlClientException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(5);
            }
        }

        Assert.HasCount(1, replacement.Statements);
        Assert.AreEqual("SELECT migrating", replacement.Statements[0].Sql);
        Assert.AreEqual(1, replacement.Statements[0].ExecuteCount);
    }

    [TestMethod]
    public async Task DeadSlotTrafficSpreadsAcrossEveryLiveSlot()
    {
        ConnectionFactory factory = new(initialConnections: 4);
        await using var pool = await factory.CreatePoolAsync();
        var created = factory.Created;
        await using var statement = await pool.PrepareAsync("SELECT value");
        factory.Block();

        created[0].Kill();
        for (var i = 0; i < 400; i++)
        {
            await pool.QueryAsync("SELECT 1");
            await statement.ExecuteAsync();
        }

        for (var slot = 1; slot < 4; slot++)
        {
            Assert.IsInRange(120, 150, created[slot].QueryCount);
            Assert.IsInRange(120, 150, created[slot].Statements[0].ExecuteCount);
        }
    }

    [TestMethod]
    public async Task PrepareSucceedsWhenAConnectionDiesWhilePreparing()
    {
        ConnectionFactory factory = new(initialConnections: 2);
        await using var pool = await factory.CreatePoolAsync();
        var (first, second) = (factory.Created[0], factory.Created[1]);
        factory.DieDuringNextPrepare = true;

        await using var statement = await pool.PrepareAsync("SELECT value");

        Assert.IsFalse(first.IsAlive);
        Assert.HasCount(1, second.Statements);
        await first.Disposed.Task.WaitAsync(Timeout);
        var replacement = factory.Created[2];
        Assert.HasCount(1, replacement.Statements);
        await statement.ExecuteAsync();
        await statement.ExecuteAsync();
        Assert.AreEqual(1, replacement.Statements[0].ExecuteCount);
        Assert.AreEqual(1, second.Statements[0].ExecuteCount);
    }

    [TestMethod]
    public async Task ReconnectDoesNotRunInTheCallersExecutionContext()
    {
        ConnectionFactory factory = new(initialConnections: 2) { FailuresBeforeSuccess = 1 };
        await using var pool = await factory.CreatePoolAsync();
        var first = factory.Created[0];

        Flow.Value = "request";
        try
        {
            first.Kill();
            await pool.QueryAsync("SELECT 1");
        }
        finally
        {
            Flow.Value = null;
        }

        await first.Disposed.Task.WaitAsync(Timeout);
        Assert.HasCount(2, factory.ObservedFlow);
        Assert.IsTrue(factory.ObservedFlow.All(static value => value is null));
    }

    private sealed class ConnectionFactory(int initialConnections)
    {
        private readonly object _gate = new();
        private readonly List<FakeConnection> _created = [];
        private readonly List<string?> _observedFlow = [];
        private TaskCompletionSource _release = CompletedSource();
        private TaskCompletionSource _attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;
        private int _concurrentAttempts;
        private int _maximumConcurrentAttempts;
        private int _failures;

        internal int FailuresBeforeSuccess { get; set; }

        internal bool IgnoreCancellation { get; init; }

        internal bool DieDuringNextPrepare { get; set; }

        internal string? RejectedSqlField;

        internal string? RejectedSql
        {
            get => Volatile.Read(ref RejectedSqlField);
            set => Volatile.Write(ref RejectedSqlField, value);
        }

        internal int ReplacementAttempts => Math.Max(0, Volatile.Read(ref _attempts) - initialConnections);

        internal int MaximumConcurrentAttempts => Volatile.Read(ref _maximumConcurrentAttempts);

        internal IReadOnlyList<string?> ObservedFlow
        {
            get
            {
                lock (_gate)
                {
                    return _observedFlow.ToArray();
                }
            }
        }

        internal IReadOnlyList<FakeConnection> Created
        {
            get
            {
                lock (_gate)
                {
                    return _created.ToArray();
                }
            }
        }

        internal ValueTask<SqlPipelinePool> CreatePoolAsync() =>
            SqlPipelinePool.CreateAsync(
                new SqlPipelinePoolOptions { ConnectionCount = initialConnections },
                ConnectAsync,
                static connection => ((FakeConnection)connection).IsAlive,
                CancellationToken.None,
                initialReconnectDelay: TimeSpan.FromMilliseconds(1),
                maximumReconnectDelay: TimeSpan.FromMilliseconds(2));

        internal void Block()
        {
            lock (_gate)
            {
                _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        internal void Release()
        {
            lock (_gate)
            {
                _release.TrySetResult();
            }
        }

        internal async Task WaitForAttemptsAsync(int replacementAttempts)
        {
            while (ReplacementAttempts < replacementAttempts)
            {
                Task attempted;
                lock (_gate)
                {
                    attempted = _attempted.Task;
                }

                await attempted.WaitAsync(Timeout);
            }
        }

        private async ValueTask<ISqlConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            var concurrent = Interlocked.Increment(ref _concurrentAttempts);
            InterlockedMax(ref _maximumConcurrentAttempts, attempt > initialConnections ? concurrent : 0);
            try
            {
                Task release;
                lock (_gate)
                {
                    release = _release.Task;
                    var attempted = _attempted;
                    _attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    attempted.TrySetResult();
                }

                if (attempt > initialConnections)
                {
                    lock (_gate)
                    {
                        _observedFlow.Add(Flow.Value);
                    }

                    await (IgnoreCancellation ? release : release.WaitAsync(cancellationToken));
                    if (Interlocked.Increment(ref _failures) <= FailuresBeforeSuccess)
                    {
                        throw new IOException("Connection refused.");
                    }
                }

                FakeConnection connection = new(this);
                lock (_gate)
                {
                    _created.Add(connection);
                }

                return connection;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentAttempts);
            }
        }

        internal bool TakeDieDuringPrepare()
        {
            lock (_gate)
            {
                var value = DieDuringNextPrepare;
                DieDuringNextPrepare = false;
                return value;
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }

        private static TaskCompletionSource CompletedSource()
        {
            TaskCompletionSource source = new(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }

    private sealed class FakeConnection(ConnectionFactory factory) : ISqlConnection
    {
        private readonly object _gate = new();
        private readonly List<FakeStatement> _statements = [];
        private int _alive = 1;
        private int _queryCount;
        private int _disposeCount;

        internal bool IsAlive => Volatile.Read(ref _alive) != 0;

        internal int QueryCount => Volatile.Read(ref _queryCount);

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal IReadOnlyList<FakeStatement> Statements
        {
            get
            {
                lock (_gate)
                {
                    return _statements.ToArray();
                }
            }
        }

        public bool IsSecure => false;

        public DatabaseMetadata DatabaseMetadata { get; } = new("Fake", "1.0", 1, 0);

        internal void Kill() => Volatile.Write(ref _alive, 0);

        internal void ThrowIfDead()
        {
            if (!IsAlive)
            {
                throw new IOException("The server closed the connection.");
            }
        }

        public ValueTask<SqlRowSet> QueryAsync(
            string sql,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDead();
            Interlocked.Increment(ref _queryCount);
            return ValueTask.FromResult(SqlRowSet.Empty);
        }

        public ValueTask<SqlRowSet> QueryAsync(
            string sql,
            SqlParameters parameters,
            CancellationToken cancellationToken = default) =>
            QueryAsync(sql, cancellationToken);

        public ValueTask<SqlCommandResult> ExecuteAsync(
            string sql,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDead();
            return ValueTask.FromResult(new SqlCommandResult(0, string.Empty));
        }

        public ValueTask<SqlCommandResult> ExecuteAsync(
            string sql,
            SqlParameters parameters,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(sql, cancellationToken);

        public IAsyncEnumerable<SqlRow> StreamAsync(
            string sql,
            SqlParameters parameters = default,
            int fetchSize = 50,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ISqlPreparedStatement> PrepareAsync(
            string sql,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDead();
            if (factory.TakeDieDuringPrepare())
            {
                Kill();
                ThrowIfDead();
            }

            if (sql == Volatile.Read(ref factory.RejectedSqlField))
            {
                throw new SqlClientException("relation does not exist");
            }

            FakeStatement statement = new(this, sql);
            lock (_gate)
            {
                _statements.Add(statement);
            }

            return ValueTask.FromResult<ISqlPreparedStatement>(statement);
        }

        public ValueTask<ISqlRowReader> ExecuteReaderAsync(
            string sql,
            SqlParameters parameters = default,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ISqlTransaction> BeginTransactionAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Kill();
            Interlocked.Increment(ref _disposeCount);
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeStatement(FakeConnection connection, string sql) : ISqlPreparedStatement
    {
        private int _executeCount;
        private int _disposeCount;

        internal int ExecuteCount => Volatile.Read(ref _executeCount);

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public string Sql { get; } = sql;

        public ValueTask<SqlRowSet> QueryAsync(
            SqlParameters parameters = default,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<SqlCommandResult> ExecuteAsync(
            SqlParameters parameters = default,
            CancellationToken cancellationToken = default)
        {
            connection.ThrowIfDead();
            Interlocked.Increment(ref _executeCount);
            return ValueTask.FromResult(new SqlCommandResult(0, string.Empty));
        }

        public ValueTask<IReadOnlyList<SqlCommandResult>> ExecuteBatchAsync(
            IReadOnlyList<SqlParameters> batch,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ISqlCursor> OpenCursorAsync(
            SqlParameters parameters = default,
            int fetchSize = 50,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ISqlRowReader> ExecuteReaderAsync(
            SqlParameters parameters = default,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<SqlRow> StreamAsync(
            SqlParameters parameters = default,
            int fetchSize = 50,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}

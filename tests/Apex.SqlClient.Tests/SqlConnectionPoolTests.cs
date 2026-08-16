using System.Runtime.CompilerServices;
using Apex.SqlClient.Internal;

namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class SqlConnectionPoolTests
{
    [TestMethod]
    public async Task ReusesConnectionsAndTracksPhysicalSize()
    {
        List<FakeConnection> created = [];
        await using var pool = CreatePool(created);

        await using (var first = await pool.GetConnectionAsync())
        {
            Assert.AreEqual(1, pool.Size);
        }

        await using (var second = await pool.GetConnectionAsync())
        {
            Assert.AreEqual(1, pool.Size);
        }

        Assert.HasCount(1, created);
    }

    [TestMethod]
    public async Task EnforcesWaitQueueBoundAndCancellation()
    {
        List<FakeConnection> created = [];
        await using var pool = CreatePool(
            created,
            new SqlPoolOptions
            {
                MaximumSize = 1,
                MaximumWaitQueueSize = 1,
                AcquisitionTimeout = TimeSpan.FromSeconds(10),
                CleanerPeriod = TimeSpan.Zero,
            });
        await using var lease = await pool.GetConnectionAsync();
        using CancellationTokenSource cancellation = new();

        var queued =
            pool.GetConnectionAsync(cancellation.Token).AsTask();
        await Assert.ThrowsExactlyAsync<SqlClientException>(
            () => pool.GetConnectionAsync().AsTask());

        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => queued);
    }

    [TestMethod]
    public async Task AppliesAcquisitionTimeout()
    {
        List<FakeConnection> created = [];
        await using var pool = CreatePool(
            created,
            new SqlPoolOptions
            {
                MaximumSize = 1,
                AcquisitionTimeout = TimeSpan.FromMilliseconds(20),
                CleanerPeriod = TimeSpan.Zero,
            });
        await using var lease = await pool.GetConnectionAsync();

        await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => pool.GetConnectionAsync().AsTask());
    }

    [TestMethod]
    public async Task EvictsExpiredAndNonReusableConnections()
    {
        ManualTimeProvider timeProvider = new();
        List<FakeConnection> created = [];
        await using var pool = CreatePool(
            created,
            new SqlPoolOptions
            {
                MaximumSize = 1,
                IdleTimeout = TimeSpan.FromMinutes(1),
                MaximumLifetime = TimeSpan.FromMinutes(2),
                CleanerPeriod = TimeSpan.Zero,
            },
            timeProvider);

        var firstLease = await pool.GetConnectionAsync();
        await firstLease.DisposeAsync();
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await pool.CleanAsync();

        Assert.AreEqual(0, pool.Size);
        Assert.AreEqual(1, created[0].DisposeCount);

        var secondLease = await pool.GetConnectionAsync();
        created[1].IsReusable = false;
        await secondLease.DisposeAsync();

        Assert.AreEqual(0, pool.Size);
        Assert.AreEqual(1, created[1].DisposeCount);

        var thirdLease = await pool.GetConnectionAsync();
        await thirdLease.DisposeAsync();
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await pool.CleanAsync();

        Assert.AreEqual(0, pool.Size);
        Assert.AreEqual(1, created[2].DisposeCount);
    }

    [TestMethod]
    public async Task EvictsConnectionAtMaximumLifetime()
    {
        ManualTimeProvider timeProvider = new();
        List<FakeConnection> created = [];
        await using var pool = CreatePool(
            created,
            new SqlPoolOptions
            {
                MaximumSize = 1,
                IdleTimeout = TimeSpan.FromMinutes(10),
                MaximumLifetime = TimeSpan.FromMinutes(2),
                CleanerPeriod = TimeSpan.Zero,
            },
            timeProvider);
        var lease = await pool.GetConnectionAsync();
        await lease.DisposeAsync();

        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await pool.CleanAsync();

        Assert.AreEqual(0, pool.Size);
        Assert.AreEqual(1, created[0].DisposeCount);
    }

    [TestMethod]
    public async Task OperationPinsLeaseUntilCompletion()
    {
        List<FakeConnection> created = [];
        await using var pool = CreatePool(created);
        var lease = await pool.GetConnectionAsync();
        var connection = created[0];
        connection.BlockQuery();

        var query = lease.QueryAsync("SELECT 1").AsTask();
        await connection.QueryStarted.Task;
        await lease.DisposeAsync();
        var queued = pool.GetConnectionAsync().AsTask();
        Assert.IsFalse(queued.IsCompleted);

        connection.ReleaseQuery();
        await query;
        await using var next = await queued.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.HasCount(1, created);
    }

    [TestMethod]
    public async Task PreparedStatementPinsLeaseUntilDisposed()
    {
        List<FakeConnection> created = [];
        await using var pool = CreatePool(created);
        var lease = await pool.GetConnectionAsync();
        var statement = await lease.PrepareAsync("SELECT 1");

        await lease.DisposeAsync();
        var queued = pool.GetConnectionAsync().AsTask();
        Assert.IsFalse(queued.IsCompleted);

        await statement.DisposeAsync();
        await using var next = await queued.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.HasCount(1, created);
    }

    [TestMethod]
    public async Task PreparedCursorPinsLeaseIndependentlyOfStatement()
    {
        List<FakeConnection> created = [];
        await using var pool = CreatePool(created);
        var lease = await pool.GetConnectionAsync();
        var statement = await lease.PrepareAsync("SELECT 1");
        var cursor = await statement.OpenCursorAsync();

        await lease.DisposeAsync();
        await statement.DisposeAsync();
        var queued = pool.GetConnectionAsync().AsTask();
        Assert.IsFalse(queued.IsCompleted);

        await cursor.DisposeAsync();
        await using var next = await queued.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.HasCount(1, created);
    }

    [TestMethod]
    public async Task PreparedStatementStreamDelegatesToInner()
    {
        List<FakeConnection> created = [];
        await using var pool = CreatePool(created);
        await using var lease = await pool.GetConnectionAsync();
        await using var statement = await lease.PrepareAsync("SELECT 1");

        await ConsumeAsync(statement.StreamAsync());

        Assert.AreEqual(1, created[0].PreparedStatement!.StreamCount);
    }

    [TestMethod]
    public async Task TransactionPinsLeaseUntilCompletion()
    {
        List<FakeConnection> created = [];
        await using var pool = CreatePool(created);
        var lease = await pool.GetConnectionAsync();
        var transaction = await lease.BeginTransactionAsync();

        await lease.DisposeAsync();
        var queued = pool.GetConnectionAsync().AsTask();
        Assert.IsFalse(queued.IsCompleted);

        await transaction.CommitAsync();
        await using var next = await queued.WaitAsync(TimeSpan.FromSeconds(1));
        await transaction.DisposeAsync();

        Assert.HasCount(1, created);
    }

    [TestMethod]
    public async Task StreamPinsLeaseUntilEnumerationCompletes()
    {
        List<FakeConnection> created = [];
        await using var pool = CreatePool(created);
        var lease = await pool.GetConnectionAsync();
        var connection = created[0];
        connection.BlockStream();

        var stream = ConsumeAsync(lease.StreamAsync("SELECT 1"));
        await connection.StreamStarted.Task;
        await lease.DisposeAsync();
        var queued = pool.GetConnectionAsync().AsTask();
        Assert.IsFalse(queued.IsCompleted);

        connection.ReleaseStream();
        await stream;
        await using var next = await queued.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.HasCount(1, created);
    }

    [TestMethod]
    public async Task DisposalClosesIdleConnectionsAndRejectsWaiters()
    {
        List<FakeConnection> created = [];
        var pool = CreatePool(created);
        var lease = await pool.GetConnectionAsync();
        var queued = pool.GetConnectionAsync().AsTask();

        await pool.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => queued);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => pool.GetConnectionAsync().AsTask());
        Assert.AreEqual(0, created[0].DisposeCount);

        await lease.DisposeAsync();
        Assert.AreEqual(1, created[0].DisposeCount);
        Assert.AreEqual(0, pool.Size);
    }

    private static SqlConnectionPool<FakeConnection> CreatePool(
        List<FakeConnection> created,
        SqlPoolOptions? options = null,
        TimeProvider? timeProvider = null) =>
        new(
            options ??
            new SqlPoolOptions
            {
                MaximumSize = 1,
                AcquisitionTimeout = TimeSpan.FromSeconds(5),
                CleanerPeriod = TimeSpan.Zero,
            },
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                FakeConnection connection = new();
                created.Add(connection);
                return ValueTask.FromResult(connection);
            },
            static connection => connection.IsReusable,
            timeProvider);

    private static async Task ConsumeAsync(IAsyncEnumerable<SqlRow> rows)
    {
        await foreach (var _ in rows)
        {
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class FakeConnection : ISqlConnection
    {
        private TaskCompletionSource? _queryRelease;
        private TaskCompletionSource? _streamRelease;

        internal bool IsReusable { get; set; } = true;

        internal int DisposeCount { get; private set; }

        internal FakePreparedStatement? PreparedStatement { get; private set; }

        internal TaskCompletionSource QueryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource StreamStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsSecure => false;

        public DatabaseMetadata DatabaseMetadata { get; } =
            new("Fake", "1.0", 1, 0);

        public async ValueTask<SqlRowSet> QueryAsync(
            string sql,
            CancellationToken cancellationToken = default)
        {
            QueryStarted.TrySetResult();
            if (_queryRelease is not null)
            {
                await _queryRelease.Task.WaitAsync(cancellationToken);
            }

            return SqlRowSet.Empty;
        }

        public ValueTask<SqlRowSet> QueryAsync(
            string sql,
            SqlParameters parameters,
            CancellationToken cancellationToken = default) =>
            QueryAsync(sql, cancellationToken);

        public ValueTask<SqlCommandResult> ExecuteAsync(
            string sql,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SqlCommandResult(0, string.Empty));

        public ValueTask<SqlCommandResult> ExecuteAsync(
            string sql,
            SqlParameters parameters,
            CancellationToken cancellationToken = default) =>
            ExecuteAsync(sql, cancellationToken);

        public async IAsyncEnumerable<SqlRow> StreamAsync(
            string sql,
            SqlParameters parameters = default,
            int fetchSize = 50,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamStarted.TrySetResult();
            if (_streamRelease is not null)
            {
                await _streamRelease.Task.WaitAsync(cancellationToken);
            }

            yield break;
        }

        public ValueTask<ISqlPreparedStatement> PrepareAsync(
            string sql,
            CancellationToken cancellationToken = default)
        {
            PreparedStatement = new FakePreparedStatement(sql);
            return ValueTask.FromResult<ISqlPreparedStatement>(PreparedStatement);
        }

        public ValueTask<ISqlRowReader> ExecuteReaderAsync(
            string sql,
            SqlParameters parameters = default,
            CancellationToken cancellationToken = default) =>
          ValueTask.FromResult<ISqlRowReader>(new FakeRowReader());

        public ValueTask<ISqlTransaction> BeginTransactionAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ISqlTransaction>(new FakeTransaction());

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        internal void BlockQuery() =>
            _queryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseQuery() => _queryRelease!.TrySetResult();

        internal void BlockStream() =>
            _streamRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseStream() => _streamRelease!.TrySetResult();
    }

    private sealed class FakePreparedStatement(string sql) : ISqlPreparedStatement
    {
        internal int StreamCount { get; private set; }

        public string Sql { get; } = sql;

        public ValueTask<SqlRowSet> QueryAsync(
            SqlParameters parameters = default,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SqlRowSet.Empty);

        public ValueTask<SqlCommandResult> ExecuteAsync(
            SqlParameters parameters = default,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SqlCommandResult(0, string.Empty));

        public ValueTask<IReadOnlyList<SqlCommandResult>> ExecuteBatchAsync(
            IReadOnlyList<SqlParameters> batch,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SqlCommandResult>>([]);

        public ValueTask<ISqlCursor> OpenCursorAsync(
            SqlParameters parameters = default,
            int fetchSize = 50,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ISqlCursor>(new FakeCursor());

        public ValueTask<ISqlRowReader> ExecuteReaderAsync(
            SqlParameters parameters = default,
            CancellationToken cancellationToken = default) =>
          ValueTask.FromResult<ISqlRowReader>(new FakeRowReader());

        public IAsyncEnumerable<SqlRow> StreamAsync(
            SqlParameters parameters = default,
            int fetchSize = 50,
            CancellationToken cancellationToken = default) =>
            StreamRows();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async IAsyncEnumerable<SqlRow> StreamRows()
        {
            StreamCount++;
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeCursor : ISqlCursor
    {
        public bool HasMore => false;

        public IReadOnlyList<SqlColumn> Columns => [];

        public ValueTask<SqlRowSet> ReadAsync(
            int count,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SqlRowSet.Empty);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRowReader : ISqlRowReader
    {
        public IReadOnlyList<SqlColumn> Columns => [];

        public int FieldCount => 0;

        public ValueTask<bool> ReadAsync(
            CancellationToken cancellationToken = default) =>
          ValueTask.FromResult(false);

        public bool IsNull(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

        public int GetOrdinal(string name) => throw new IndexOutOfRangeException(name);

        public T Get<T>(int ordinal) => throw new ArgumentOutOfRangeException(nameof(ordinal));

        public TElement[]? GetArray<TElement>(int ordinal) =>
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        public bool GetBoolean(int ordinal) => Get<bool>(ordinal);

        public short GetInt16(int ordinal) => Get<short>(ordinal);

        public int GetInt32(int ordinal) => Get<int>(ordinal);

        public long GetInt64(int ordinal) => Get<long>(ordinal);

        public float GetFloat(int ordinal) => Get<float>(ordinal);

        public double GetDouble(int ordinal) => Get<double>(ordinal);

        public string GetString(int ordinal) => Get<string>(ordinal);

        public Guid GetGuid(int ordinal) => Get<Guid>(ordinal);

        public DateOnly GetDateOnly(int ordinal) => Get<DateOnly>(ordinal);

        public TimeOnly GetTimeOnly(int ordinal) => Get<TimeOnly>(ordinal);

        public DateTime GetDateTime(int ordinal) => Get<DateTime>(ordinal);

        public DateTimeOffset GetDateTimeOffset(int ordinal) =>
          Get<DateTimeOffset>(ordinal);

        public byte[] GetBytes(int ordinal) => Get<byte[]>(ordinal);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTransaction : ISqlTransaction
    {
        public bool IsCompleted { get; private set; }

        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            IsCompleted = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            IsCompleted = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

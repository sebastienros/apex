using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Apex.PgClient;

namespace Apex.SqlClient.AdoNet.Tests;

[TestClass]
public sealed class AdoNetReviewRegressionTests
{
    [TestMethod]
    public async Task ReaderGetOrdinalPrefersExactCaseMatchAndValidatesNull()
    {
        var columns = new SqlColumn[]
        {
            new("ID", 23, 4, -1, SqlDataFormat.Binary),
            new("id", 23, 4, -1, SqlDataFormat.Binary),
        };
        await using var reader = new ApexDbDataReader(
            new ColumnRowReader(columns, [[1, 2]]),
            CommandBehavior.Default,
            null);

        Assert.AreEqual(0, reader.GetOrdinal("ID"));
        Assert.AreEqual(1, reader.GetOrdinal("id"));
        Assert.AreEqual(0, reader.GetOrdinal("Id"));
        Assert.ThrowsExactly<ArgumentNullException>(() => reader.GetOrdinal(null!));
    }

    [TestMethod]
    public async Task ReaderRejectsGettersBeforeReadAfterSingleRowAndClearsCaches()
    {
        var columns = new SqlColumn[]
        {
            new("bin", 17, -1, -1, SqlDataFormat.Binary),
            new("txt", 25, -1, -1, SqlDataFormat.Text),
        };
        await using var reader = new ApexDbDataReader(
            new ColumnRowReader(columns, [[new byte[] { 1, 2, 3 }, "abc"], [new byte[] { 4, 5 }, "xy"]]),
            CommandBehavior.SingleRow,
            null);

        await reader.InitializeAsync(CancellationToken.None);
        Assert.IsTrue(reader.HasRows);

        // Before first ReadAsync on a prefetched row, getters must throw.
        Assert.ThrowsExactly<InvalidOperationException>(() => reader.GetString(1));
        Assert.ThrowsExactly<InvalidOperationException>(() => reader.GetBytes(0, 0, null, 0, 0));
        Assert.ThrowsExactly<InvalidOperationException>(() => reader.IsDBNull(0));

        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(3L, reader.GetBytes(0, 0, null, 0, 0));
        Assert.AreEqual(3L, reader.GetChars(1, 0, null, 0, 0));

        // Second ReadAsync returns false under SingleRow and clears cached values.
        Assert.IsFalse(await reader.ReadAsync(CancellationToken.None));
        Assert.ThrowsExactly<InvalidOperationException>(() => reader.GetBytes(0, 0, null, 0, 0));
        Assert.ThrowsExactly<InvalidOperationException>(() => reader.GetChars(1, 0, null, 0, 0));
        Assert.ThrowsExactly<InvalidOperationException>(() => reader.GetString(1));

        await reader.CloseAsync();
        Assert.ThrowsExactly<InvalidOperationException>(() => reader.GetBytes(0, 0, null, 0, 0));
    }

    [TestMethod]
    public async Task ReaderNextResultChecksClosedBeforeSingleResultAndUpdatesMetadata()
    {
        var columns1 = new SqlColumn[] { new("a", 23, 4, -1, SqlDataFormat.Binary) };
        var columns2 = new SqlColumn[]
        {
            new("x", 23, 4, -1, SqlDataFormat.Binary),
            new("y", 23, 4, -1, SqlDataFormat.Binary),
        };
        var deferred = new DeferredBoundaryMultiResultReader(columns1, columns2);
        await using (var reader = new ApexDbDataReader(deferred, CommandBehavior.Default, null))
        {
            await reader.InitializeAsync(CancellationToken.None);
            Assert.AreEqual(1, reader.FieldCount);
            Assert.IsTrue(reader.HasRows);

            Assert.IsTrue(await reader.NextResultAsync(CancellationToken.None));
            Assert.AreEqual(2, reader.FieldCount);
            Assert.IsTrue(reader.HasRows);

            Assert.IsFalse(await reader.NextResultAsync(CancellationToken.None));
            Assert.IsFalse(reader.HasRows);
            Assert.AreEqual(0, reader.FieldCount);
        }

        await using var singleResult = new ApexDbDataReader(
            new ColumnRowReader(columns1, [[1]]),
            CommandBehavior.SingleResult,
            null);
        await singleResult.CloseAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => singleResult.NextResultAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ReaderAsyncFieldMethodsRespectCancellationAndReturnFaultedTasks()
    {
        var columns = new SqlColumn[] { new("a", 23, 4, -1, SqlDataFormat.Binary) };
        await using var reader = new ApexDbDataReader(
            new ColumnRowReader(columns, [[1]]),
            CommandBehavior.Default,
            null);
        await reader.InitializeAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var canceledNullTask = reader.IsDBNullAsync(0, cts.Token);
        Assert.IsTrue(canceledNullTask.IsCanceled);
        var canceledValTask = reader.GetFieldValueAsync<int>(0, cts.Token);
        Assert.IsTrue(canceledValTask.IsCanceled);

        // Prefetched (not yet ReadAsync) -> must return faulted Task rather than throwing synchronously.
        var faultedTask = reader.GetFieldValueAsync<int>(0, CancellationToken.None);
        Assert.IsTrue(faultedTask.IsFaulted);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => faultedTask);
    }

    [TestMethod]
    public async Task ConcurrentOpenAsyncWhileConnectingThrowsInvalidOperationException()
    {
        var openStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOpen = new TaskCompletionSource<ISqlConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var openCount = 0;

        await using var connection = new TestApexConnection(async ct =>
        {
            Interlocked.Increment(ref openCount);
            openStarted.TrySetResult();
            return await releaseOpen.Task.WaitAsync(ct);
        });

        var firstOpen = connection.OpenAsync();
        await openStarted.Task;
        Assert.AreEqual(ConnectionState.Connecting, connection.State);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => connection.OpenAsync());
        Assert.AreEqual(1, Volatile.Read(ref openCount));

        releaseOpen.SetResult(new DummyNativeConnection());
        await firstOpen;
        Assert.AreEqual(ConnectionState.Open, connection.State);
    }

    [TestMethod]
    public async Task BatchReaderSingleResultKeepsIsClosedFalseUntilCloseAndStopsOnDrainError()
    {
        var executed = new List<int>();
        var closeCalled = false;
        await using var connection = new TestApexConnection(_ => Task.FromResult<ISqlConnection>(new DummyNativeConnection()));
        await connection.OpenAsync();

        var batchReader = await ApexDbBatchDataReader.CreateAsync(
            commandCount: 3,
            CommandBehavior.SingleResult | CommandBehavior.CloseConnection,
            connection,
            (index, _) =>
            {
                executed.Add(index);
                var cmd = new TestApexCommand(connection);
                var batchCmd = new TestBatchCommand();
                var subReader = new ApexDbDataReader(
                    new ColumnRowReader([new SqlColumn("v", 23, 4, -1, SqlDataFormat.Binary)], [[index]]),
                    CommandBehavior.Default,
                    connection);
                return Task.FromResult((batchCmd as ApexDbBatchCommand, cmd as ApexDbCommand, subReader as DbDataReader));
            },
            cmd => cmd.DisposeAsync(),
            () =>
            {
                closeCalled = true;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.IsFalse(await batchReader.NextResultAsync(CancellationToken.None));
        Assert.IsFalse(batchReader.IsClosed);
        Assert.IsFalse(batchReader.HasRows);
        Assert.AreEqual(0, batchReader.FieldCount);
        Assert.IsFalse(await batchReader.ReadAsync(CancellationToken.None));
        Assert.IsFalse(closeCalled);
        Assert.AreEqual(ConnectionState.Open, connection.State);

        await batchReader.CloseAsync();
        Assert.IsTrue(batchReader.IsClosed);
        Assert.IsTrue(closeCalled);
        Assert.AreEqual(ConnectionState.Closed, connection.State);

        // Now verify DrainRemainingAsync error stops subsequent commands on DisposeAsync.
        executed.Clear();
        await connection.OpenAsync();
        var failingReader = await ApexDbBatchDataReader.CreateAsync(
            commandCount: 3,
            CommandBehavior.SingleResult,
            connection,
            (index, _) =>
            {
                executed.Add(index);
                if (index == 1)
                {
                    throw new InvalidOperationException("command 1 failed");
                }

                var cmd = new TestApexCommand(connection);
                var batchCmd = new TestBatchCommand();
                var subReader = new ApexDbDataReader(
                    new ColumnRowReader([new SqlColumn("v", 23, 4, -1, SqlDataFormat.Binary)], [[index]]),
                    CommandBehavior.Default,
                    connection);
                return Task.FromResult((batchCmd as ApexDbBatchCommand, cmd as ApexDbCommand, subReader as DbDataReader));
            },
            cmd => cmd.DisposeAsync(),
            null,
            CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => failingReader.NextResultAsync(CancellationToken.None));
        await failingReader.DisposeAsync();
        CollectionAssert.AreEqual(new[] { 0, 1 }, executed);
    }

    [TestMethod]
    public async Task PreparedBatchExecuteReaderErrorDoesNotDisposePreparedCommand()
    {
        var native = new CountingPreparedConnection();
        await using var connection = new TestApexConnection(_ => Task.FromResult<ISqlConnection>(native));
        await connection.OpenAsync();

        await using var batch = new TestApexBatch(connection);
        var c1 = batch.CreateBatchCommand();
        c1.CommandText = "SELECT 1";
        batch.BatchCommands.Add(c1);

        await batch.PrepareAsync();
        Assert.AreEqual(1, native.PreparedCount);
        Assert.AreEqual(0, native.PreparedDisposedCount);

        native.FailNextPreparedExecute = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => batch.ExecuteReaderAsync());
        Assert.AreEqual(0, native.PreparedDisposedCount);

        native.FailNextPreparedExecute = false;
        await using var reader = await batch.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(1, reader.GetInt32(0));
        Assert.AreEqual(1, native.PreparedCount);
    }

    [TestMethod]
    public async Task BatchPrepareAsyncPropagatesTimeoutAndCancel()
    {
        var prepareStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new SlowPrepareConnection(prepareStarted);
        await using var connection = new TestApexConnection(_ => Task.FromResult<ISqlConnection>(native));
        await connection.OpenAsync();

        await using var batch = new TestApexBatch(connection) { Timeout = 10 };
        var cmd = batch.CreateBatchCommand();
        cmd.CommandText = "SELECT 1";
        batch.BatchCommands.Add(cmd);

        var prepareTask = batch.PrepareAsync();
        await prepareStarted.Task;
        batch.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => prepareTask);
    }

    [TestMethod]
    public void ParameterCollectionValidatesNullAndBinaryRejectsSByteArray()
    {
        using var cmd = new PgDbCommand();
        Assert.ThrowsExactly<ArgumentNullException>(() => cmd.Parameters.Insert(0, null!));
        Assert.ThrowsExactly<ArgumentException>(() => cmd.Parameters.Insert(0, "not-a-param"));
        cmd.Parameters.Remove("not-a-param"); // must not throw InvalidCastException
        cmd.Parameters.Add(new PgDbParameter { ParameterName = "p1", Value = 1 });
        Assert.ThrowsExactly<ArgumentNullException>(() => cmd.Parameters[0] = null!);
        Assert.ThrowsExactly<ArgumentNullException>(() => cmd.Parameters["p1"] = null!);

        var binaryParam = new PgDbParameter { DbType = DbType.Binary, Value = new sbyte[] { -1, 2 } };
        Assert.ThrowsExactly<InvalidCastException>(() => binaryParam.ToSqlValue());
    }

    private sealed class TestApexConnection(Func<CancellationToken, Task<ISqlConnection>> open)
        : ApexDbConnection("test", "db", "ds", TimeSpan.FromSeconds(5))
    {
        internal ISqlConnection CurrentNativeConnection => NativeConnection;
        protected override Task<ISqlConnection> OpenCoreAsync(CancellationToken cancellationToken) => open(cancellationToken);
        protected override DbCommand CreateCommandCore() => new TestApexCommand(this);
        protected override void SetConnectionStringCore(string connectionString) { }
    }

    private sealed class TestApexCommand : ApexDbCommand
    {
        public TestApexCommand(DbConnection connection) : base(new PgDbParameterCollection())
        {
            base.Connection = connection;
        }

        public TestApexCommand(DbConnection connection, string sql) : this(connection)
        {
            CommandText = sql;
        }

        protected override ApexDbParameter CreateParameterCore() => new PgDbParameter();
        protected override ISqlConnection GetConnection() =>
            ((TestApexConnection)Connection!).CurrentNativeConnection;
    }

    private sealed class TestBatchCommand : ApexDbBatchCommand
    {
        public TestBatchCommand() : base(new PgDbParameterCollection()) { }
        protected override ApexDbParameter CreateParameterCore() => new PgDbParameter();
    }

    private sealed class TestApexBatch : ApexDbBatch
    {
        public TestApexBatch(DbConnection connection) : base(new PgDbBatchCommandCollection())
        {
            DbConnection = connection;
        }

        protected override ApexDbBatchCommand CreateBatchCommandCore() => new TestBatchCommand();
        protected override ApexDbCommand CreateCommandCore(ApexDbBatchCommand command, DbConnection connection) =>
            new TestApexCommand(connection, command.CommandText);
    }

    private sealed class ColumnRowReader(IReadOnlyList<SqlColumn> columns, object[][] rows) : ISqlRowReader
    {
        private int _index = -1;
        public IReadOnlyList<SqlColumn> Columns => columns;
        public int FieldCount => columns.Count;
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(++_index < rows.Length);
        public int GetOrdinal(string name) => 0;
        public bool IsNull(int ordinal) => rows[_index][ordinal] is null;
        public bool GetBoolean(int ordinal) => (bool)rows[_index][ordinal];
        public short GetInt16(int ordinal) => (short)rows[_index][ordinal];
        public int GetInt32(int ordinal) => (int)rows[_index][ordinal];
        public long GetInt64(int ordinal) => (long)rows[_index][ordinal];
        public float GetFloat(int ordinal) => (float)rows[_index][ordinal];
        public double GetDouble(int ordinal) => (double)rows[_index][ordinal];
        public decimal GetDecimal(int ordinal) => (decimal)rows[_index][ordinal];
        public string GetString(int ordinal) => (string)rows[_index][ordinal];
        public Guid GetGuid(int ordinal) => (Guid)rows[_index][ordinal];
        public DateOnly GetDateOnly(int ordinal) => (DateOnly)rows[_index][ordinal];
        public TimeOnly GetTimeOnly(int ordinal) => (TimeOnly)rows[_index][ordinal];
        public DateTime GetDateTime(int ordinal) => (DateTime)rows[_index][ordinal];
        public DateTimeOffset GetDateTimeOffset(int ordinal) => (DateTimeOffset)rows[_index][ordinal];
        public byte[] GetBytes(int ordinal) => (byte[])rows[_index][ordinal];
        public TElement[] GetArray<TElement>(int ordinal) => (TElement[])rows[_index][ordinal];
        public T Get<T>(int ordinal) => (T)rows[_index][ordinal];
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DeferredBoundaryMultiResultReader(SqlColumn[] first, SqlColumn[] second)
        : ISqlRowReader, ISqlMultiResultReader, ISqlResultBoundaryReader
    {
        private int _result = 0;
        private bool _initialized;
        private bool _read;
        public IReadOnlyList<SqlColumn> Columns => !_initialized ? [] : _result == 0 ? first : second;
        public int FieldCount => Columns.Count;
        public ValueTask<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            _initialized = true;
            _read = false;
            return ValueTask.FromResult(true);
        }
        public ValueTask<bool> NextResultAsync(CancellationToken cancellationToken = default)
        {
            if (_result >= 1) return ValueTask.FromResult(false);
            _result++;
            _initialized = false;
            _read = false;
            return ValueTask.FromResult(true);
        }
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (_read) return ValueTask.FromResult(false);
            _read = true;
            return ValueTask.FromResult(true);
        }
        public int GetOrdinal(string name) => 0;
        public bool IsNull(int ordinal) => false;
        public bool GetBoolean(int ordinal) => false;
        public short GetInt16(int ordinal) => 1;
        public int GetInt32(int ordinal) => 1;
        public long GetInt64(int ordinal) => 1;
        public float GetFloat(int ordinal) => 1;
        public double GetDouble(int ordinal) => 1;
        public decimal GetDecimal(int ordinal) => 1;
        public string GetString(int ordinal) => "1";
        public Guid GetGuid(int ordinal) => Guid.Empty;
        public DateOnly GetDateOnly(int ordinal) => default;
        public TimeOnly GetTimeOnly(int ordinal) => default;
        public DateTime GetDateTime(int ordinal) => default;
        public DateTimeOffset GetDateTimeOffset(int ordinal) => default;
        public byte[] GetBytes(int ordinal) => [];
        public TElement[] GetArray<TElement>(int ordinal) => [];
        public T Get<T>(int ordinal) => (T)(object)1;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class DummyNativeConnection : ISqlConnection
    {
        public bool IsSecure => false;
        public DatabaseMetadata DatabaseMetadata => new("test", "1.0", 1, 0);
        public ValueTask<SqlRowSet> QueryAsync(string sql, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SqlRowSet> QueryAsync(string sql, SqlParameters parameters, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SqlCommandResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SqlCommandResult> ExecuteAsync(string sql, SqlParameters parameters, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<SqlCommandResult>> ExecuteBatchAsync(string sql, IReadOnlyList<SqlParameters> batch, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<SqlRow> StreamAsync(string sql, int pageSize = 128, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<SqlRow> StreamAsync(string sql, SqlParameters parameters, int pageSize = 128, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public virtual ValueTask<ISqlRowReader> ExecuteReaderAsync(string sql, SqlParameters parameters = default, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public virtual ValueTask<ISqlPreparedStatement> PrepareAsync(string sql, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ISqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingPreparedConnection : DummyNativeConnection
    {
        public int PreparedCount { get; private set; }
        public int PreparedDisposedCount { get; private set; }
        public bool FailNextPreparedExecute { get; set; }

        public override ValueTask<ISqlPreparedStatement> PrepareAsync(string sql, CancellationToken cancellationToken = default)
        {
            PreparedCount++;
            return ValueTask.FromResult<ISqlPreparedStatement>(new CountingPreparedStatement(this, sql));
        }

        private sealed class CountingPreparedStatement(CountingPreparedConnection owner, string sql) : ISqlPreparedStatement
        {
            public string Sql => sql;
            public ValueTask<SqlRowSet> QueryAsync(SqlParameters parameters = default, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<SqlCommandResult> ExecuteAsync(SqlParameters parameters = default, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<IReadOnlyList<SqlCommandResult>> ExecuteBatchAsync(IReadOnlyList<SqlParameters> batch, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public IAsyncEnumerable<SqlRow> StreamAsync(SqlParameters parameters = default, int fetchSize = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<ISqlCursor> OpenCursorAsync(SqlParameters parameters = default, int fetchSize = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<ISqlRowReader> ExecuteReaderAsync(SqlParameters parameters = default, CancellationToken cancellationToken = default)
            {
                if (owner.FailNextPreparedExecute)
                {
                    throw new InvalidOperationException("prepared execute failed");
                }

                return ValueTask.FromResult<ISqlRowReader>(new ColumnRowReader(
                    [new SqlColumn("v", 23, 4, -1, SqlDataFormat.Binary)],
                    [[1]]));
            }
            public ValueTask DisposeAsync()
            {
                owner.PreparedDisposedCount++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class SlowPrepareConnection(TaskCompletionSource started) : DummyNativeConnection
    {
        public override async ValueTask<ISqlPreparedStatement> PrepareAsync(string sql, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }
}

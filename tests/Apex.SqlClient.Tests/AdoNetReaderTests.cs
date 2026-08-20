using System.Data;
using System.Data.Common;
using Apex.SqlClient;

namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class AdoNetReaderTests
{
    [TestMethod]
    public async Task HasRowsTracksEmptyAndExhaustedReaders()
    {
        await using var empty = new ApexDbDataReader(
            new TestRowReader([]), CommandBehavior.Default, null);
        Assert.IsFalse(empty.HasRows);
        Assert.IsFalse(await empty.ReadAsync(CancellationToken.None));
        Assert.IsFalse(empty.HasRows);

        await using var reader = new ApexDbDataReader(
            new TestRowReader([[1]]), CommandBehavior.Default, null);
        Assert.IsFalse(reader.HasRows);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.IsTrue(reader.HasRows);
        Assert.IsFalse(await reader.ReadAsync(CancellationToken.None));
        Assert.IsTrue(reader.HasRows);
    }

    [TestMethod]
    public async Task GetFieldValueUsesNativeTypedAccessor()
    {
        ReadOnlyMemory<byte> value = "fortune"u8.ToArray();
        await using var reader = new ApexDbDataReader(
            new TestRowReader([[value]]), CommandBehavior.Default, null);

        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(value, reader.GetFieldValue<ReadOnlyMemory<byte>>(0));
    }

    [TestMethod]
    public async Task InitializationAndNextResultPreserveRowsAndMetadata()
    {
        await using var reader = new ApexDbDataReader(
            new TestMultiResultReader(
                [[], [[2]], [[3]]]),
            CommandBehavior.Default,
            null);

        await reader.InitializeAsync(CancellationToken.None);
        Assert.AreEqual(1, reader.FieldCount);
        Assert.IsFalse(reader.HasRows);
        Assert.IsFalse(await reader.ReadAsync(CancellationToken.None));

        Assert.IsTrue(await reader.NextResultAsync(CancellationToken.None));
        Assert.AreEqual(1, reader.FieldCount);
        Assert.IsTrue(reader.HasRows);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(2, reader.GetInt32(0));
        Assert.IsFalse(await reader.ReadAsync(CancellationToken.None));
        Assert.IsTrue(reader.HasRows);

        Assert.IsTrue(await reader.NextResultAsync(CancellationToken.None));
        Assert.IsTrue(reader.HasRows);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(3, reader.GetInt32(0));
    }

    [TestMethod]
    public async Task NextResultBeforeDrainingDiscardsCurrentResult()
    {
        await using var reader = new ApexDbDataReader(
            new TestMultiResultReader([[[1]], [[2]]]),
            CommandBehavior.Default,
            null);

        await reader.InitializeAsync(CancellationToken.None);
        Assert.IsTrue(reader.HasRows);
        Assert.IsTrue(await reader.NextResultAsync(CancellationToken.None));
        Assert.IsTrue(reader.HasRows);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(2, reader.GetInt32(0));
    }

    [TestMethod]
    public async Task NextResultDrainsEveryUnreadRowBeforeAdvancing()
    {
        await using var reader = new ApexDbDataReader(
            new DrainingMultiResultReader([[[1], [2], [3]], [[4]]]),
            CommandBehavior.Default,
            null);

        await reader.InitializeAsync(CancellationToken.None);

        Assert.IsTrue(await reader.NextResultAsync(CancellationToken.None));
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(4, reader.GetInt32(0));
    }

    [TestMethod]
    public async Task BinaryAndCharacterLengthProbesValidateOffsets()
    {
        var source = new TestRowReader([[new byte[] { 1, 2, 3 }, "abcd"]]);
        await using var reader = new ApexDbDataReader(
            source,
            CommandBehavior.Default,
            null);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));

        Assert.AreEqual(3L, reader.GetBytes(0, 0, null, -1, -1));
        Assert.AreEqual(4L, reader.GetChars(1, 0, null, -1, -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => reader.GetBytes(0, 4, new byte[1], 0, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => reader.GetChars(1, -1, new char[1], 0, 1));

        byte[] bytes = new byte[2];
        Assert.AreEqual(2L, reader.GetBytes(0, 1, bytes, 0, bytes.Length));
        CollectionAssert.AreEqual(new byte[] { 2, 3 }, bytes);
        _ = reader.GetBytes(0, 0, null, 0, 0);
        _ = reader.GetChars(1, 0, null, 0, 0);
        _ = reader.GetChars(1, 1, new char[1], 0, 1);
        Assert.AreEqual(1, source.BytesReadCount);
        Assert.AreEqual(1, source.StringReadCount);
    }

    [TestMethod]
    public async Task CloseConnectionClosesAsynchronously()
    {
        var connection = new TrackingConnection();
        await using var reader = new ApexDbDataReader(
            new TestRowReader([]), CommandBehavior.CloseConnection, connection);

        await reader.CloseAsync();

        Assert.AreEqual(1, connection.AsyncCloseCount);
        Assert.AreEqual(0, connection.SyncCloseCount);
    }

    [TestMethod]
    public async Task CommandCancellationRemainsActiveUntilTheStreamingReaderCloses()
    {
        var native = new CancellationTrackingConnection();
        await using var connection = new TestDbConnection(native);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new TestDbCommand(connection)
        {
            CommandText = "select 1",
            CommandTimeout = 60,
        };

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(reader.HasRows);
        command.Cancel();

        Assert.IsTrue(native.ReaderToken.IsCancellationRequested);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.AreEqual(1, reader.GetInt32(0));
    }

    [TestMethod]
    public async Task BatchDisposeReleasesPreparedStatementsThroughDbBatchReference()
    {
        var native = new CancellationTrackingConnection();
        await using var connection = new TestDbConnection(native);
        await connection.OpenAsync(CancellationToken.None);
        DbBatch batch = new TestDbBatch { Connection = connection };
        var command = batch.CreateBatchCommand();
        command.CommandText = "select 1";
        batch.BatchCommands.Add(command);

        await batch.PrepareAsync(CancellationToken.None);
        batch.Dispose();

        Assert.AreEqual(1, native.PreparedDisposeCount);
    }

    [TestMethod]
    public async Task BatchCommandMutationInvalidatesPreparedExecution()
    {
        var native = new CancellationTrackingConnection();
        await using var connection = new TestDbConnection(native);
        await connection.OpenAsync(CancellationToken.None);
        await using var batch = new TestDbBatch { Connection = connection };
        var command = batch.CreateBatchCommand();
        command.CommandText = "select 1";
        batch.BatchCommands.Add(command);

        await batch.PrepareAsync(CancellationToken.None);
        command.CommandText = "select 2";

        Assert.AreEqual(1, await batch.ExecuteNonQueryAsync(CancellationToken.None));
        Assert.AreEqual(1, native.DirectExecuteCount);
        Assert.AreEqual(1, native.PreparedDisposeCount);
    }

    [TestMethod]
    public async Task SourceOwnedCommandLeasesAndReturnsItsConnection()
    {
        var native = new CancellationTrackingConnection();
        await using var connection = new TestDbConnection(
            native,
            isPoolBound: true,
            autoOpenForCommands: true);
        await using var command = new TestDbCommand(connection) { CommandText = "update test" };

        Assert.AreEqual(1, await command.ExecuteNonQueryAsync(CancellationToken.None));
        Assert.AreEqual(1, native.DirectExecuteCount);
        Assert.AreEqual(ConnectionState.Closed, connection.State);
        Assert.ThrowsExactly<InvalidOperationException>(() => connection.ConnectionString = "other");
    }

    [TestMethod]
    public async Task ReaderAndScalarValidateTransactionOwnershipBeforeExecution()
    {
        var native = new CancellationTrackingConnection();
        await using var connection = new TestDbConnection(native);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new TestDbCommand(connection) { CommandText = "select 1" };
        command.Transaction = new ForeignTransaction();

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => command.ExecuteReaderAsync(CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => command.ExecuteScalarAsync(CancellationToken.None));
        Assert.AreEqual(0, native.ReaderExecuteCount);
    }

    [TestMethod]
    public async Task BatchScalarExecutesAllCommandsAndFindsTheFirstResultWithARow()
    {
        var native = new CancellationTrackingConnection(
            sql => sql switch
            {
                "empty" => new TestMultiResultReader([[]]),
                "scalar" => new TestMultiResultReader([[], [[42]]]),
                _ => new TestMultiResultReader([[[99]]]),
            });
        await using var connection = new TestDbConnection(native);
        await connection.OpenAsync(CancellationToken.None);
        await using var batch = new TestDbBatch { Connection = connection };
        AddBatchCommand(batch, "empty");
        AddBatchCommand(batch, "scalar");
        AddBatchCommand(batch, "later");

        Assert.AreEqual(42, await batch.ExecuteScalarAsync(CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "empty", "scalar", "later" }, native.ReaderCommands);
    }

    [TestMethod]
    public async Task BatchSingleResultDrainsAndExecutesRemainingCommands()
    {
        var native = new CancellationTrackingConnection(
            _ => new DrainingMultiResultReader([[[1], [2]], [[3]]]));
        await using var connection = new TestDbConnection(native);
        await connection.OpenAsync(CancellationToken.None);
        await using var batch = new TestDbBatch { Connection = connection };
        AddBatchCommand(batch, "first");
        AddBatchCommand(batch, "second");
        AddBatchCommand(batch, "third");

        await using var reader = await batch.ExecuteReaderAsync(
            CommandBehavior.SingleResult,
            CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        Assert.IsFalse(await reader.NextResultAsync(CancellationToken.None));

        CollectionAssert.AreEqual(new[] { "first", "second", "third" }, native.ReaderCommands);
    }

    [TestMethod]
    public async Task BatchCloseDrainsAndExecutesRemainingCommands()
    {
        var native = new CancellationTrackingConnection(
            _ => new DrainingMultiResultReader([[[1], [2]], [[3]]]));
        await using var connection = new TestDbConnection(native);
        await connection.OpenAsync(CancellationToken.None);
        await using var batch = new TestDbBatch { Connection = connection };
        AddBatchCommand(batch, "first");
        AddBatchCommand(batch, "second");
        AddBatchCommand(batch, "third");

        var reader = await batch.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        await reader.CloseAsync();

        CollectionAssert.AreEqual(new[] { "first", "second", "third" }, native.ReaderCommands);
    }

    [TestMethod]
    public async Task ReaderCompletionUpdatesRecordsAffectedOnBatchCommands()
    {
        var native = new CancellationTrackingConnection(
            _ => new DrainingMultiResultReader([[[1]]], recordsAffected: 7));
        await using var connection = new TestDbConnection(native);
        await connection.OpenAsync(CancellationToken.None);
        await using var batch = new TestDbBatch { Connection = connection };
        var command = AddBatchCommand(batch, "update");

        await using var reader = await batch.ExecuteReaderAsync(CancellationToken.None);
        await reader.CloseAsync();

        Assert.AreEqual(7, command.RecordsAffected);
    }

    [TestMethod]
    public async Task PreparedBatchInvalidationAwaitsAsynchronousDisposal()
    {
        TaskCompletionSource releasePrepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new CancellationTrackingConnection(preparedDisposeTask: releasePrepared.Task);
        await using var connection = new TestDbConnection(native);
        await connection.OpenAsync(CancellationToken.None);
        await using var batch = new TestDbBatch { Connection = connection };
        var command = AddBatchCommand(batch, "select 1");

        await batch.PrepareAsync(CancellationToken.None);
        command.CommandText = "select 2";
        var execution = batch.ExecuteNonQueryAsync(CancellationToken.None);

        var started = await Task.WhenAny(
            native.PreparedDisposeStarted.Task,
            Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.AreSame(native.PreparedDisposeStarted.Task, started);
        Assert.AreEqual(0, native.DirectExecuteCount);

        releasePrepared.SetResult();
        Assert.AreEqual(1, await execution);
    }

    private static DbBatchCommand AddBatchCommand(DbBatch batch, string commandText)
    {
        var command = batch.CreateBatchCommand();
        command.CommandText = commandText;
        batch.BatchCommands.Add(command);
        return command;
    }

    private sealed class TrackingConnection : DbConnection
    {
        public int AsyncCloseCount { get; private set; }
        public int SyncCloseCount { get; private set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => string.Empty;
        public override string DataSource => string.Empty;
        public override string ServerVersion => string.Empty;
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => SyncCloseCount++;
        public override Task CloseAsync()
        {
            AsyncCloseCount++;
            return Task.CompletedTask;
        }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private sealed class ForeignTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class TestDbConnection : ApexDbConnection
        {
            private readonly ISqlConnection _native;

            public TestDbConnection(
                ISqlConnection native,
                bool isPoolBound = false,
                bool autoOpenForCommands = false)
                : base(
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    TimeSpan.Zero,
                    isPoolBound,
                    autoOpenForCommands) => _native = native;

            internal ISqlConnection GetNativeConnection() => NativeConnection;
            protected override Task<ISqlConnection> OpenCoreAsync(CancellationToken cancellationToken) =>
                Task.FromResult(_native);
            protected override DbCommand CreateCommandCore() => new TestDbCommand(this);
            protected override void SetConnectionStringCore(string connectionString) { }
        }

        private sealed class TestDbCommand : ApexDbCommand
        {
            public TestDbCommand(TestDbConnection connection) : base(new TestParameterCollection())
            {
                Connection = connection;
            }

            protected override ApexDbParameter CreateParameterCore() => new TestParameter();
            protected override ISqlConnection GetConnection() =>
                Connection is TestDbConnection connection && connection.State == ConnectionState.Open
                    ? connection.GetNativeConnection()
                    : throw new InvalidOperationException("The test connection must be open.");
        }

        private sealed class TestParameter : ApexDbParameter { }
        private sealed class TestParameterCollection : ApexDbParameterCollection { }
        private sealed class TestDbBatch : ApexDbBatch
        {
            public TestDbBatch() : base(new TestDbBatchCommandCollection()) { }
            protected override ApexDbBatchCommand CreateBatchCommandCore() => new TestDbBatchCommand();
            protected override ApexDbCommand CreateCommandCore(
                ApexDbBatchCommand command,
                DbConnection connection)
            {
                var result = new TestDbCommand((TestDbConnection)connection)
                {
                    CommandText = command.CommandText,
                };
                foreach (DbParameter parameter in command.Parameters) result.Parameters.Add(parameter);
                return result;
            }
        }
        private sealed class TestDbBatchCommand : ApexDbBatchCommand
        {
            public TestDbBatchCommand() : base(new TestParameterCollection()) { }
            protected override ApexDbParameter CreateParameterCore() => new TestParameter();
        }
        private sealed class TestDbBatchCommandCollection : ApexDbBatchCommandCollection { }

        private sealed class CancellationTrackingConnection : ISqlConnection
        {
            private readonly Func<string, ISqlRowReader> _readerFactory;
            private readonly Task? _preparedDisposeTask;

            public CancellationToken ReaderToken { get; private set; }
            public int PreparedDisposeCount { get; private set; }
            public int DirectExecuteCount { get; private set; }
            public int ReaderExecuteCount { get; private set; }
            public List<string> ReaderCommands { get; } = [];
            public TaskCompletionSource PreparedDisposeStarted { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationTrackingConnection(
                Func<string, ISqlRowReader>? readerFactory = null,
                Task? preparedDisposeTask = null)
            {
                _readerFactory = readerFactory ?? (static _ => new TestRowReader([[1]]));
                _preparedDisposeTask = preparedDisposeTask;
            }
            public bool IsSecure => false;
            public DatabaseMetadata DatabaseMetadata => new("test", "1", 1, 0);
            public ValueTask<ISqlPreparedStatement> PrepareAsync(
                string sql,
                CancellationToken cancellationToken = default) =>
                ValueTask.FromResult<ISqlPreparedStatement>(new TestPreparedStatement(this, sql));
            public ValueTask<ISqlRowReader> ExecuteReaderAsync(
                string sql,
                SqlParameters parameters = default,
                CancellationToken cancellationToken = default)
            {
                ReaderToken = cancellationToken;
                ReaderExecuteCount++;
                ReaderCommands.Add(sql);
                return ValueTask.FromResult(_readerFactory(sql));
            }
            public ValueTask<ISqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
            public ValueTask<SqlRowSet> QueryAsync(string sql, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
            public ValueTask<SqlRowSet> QueryAsync(
                string sql,
                SqlParameters parameters,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
            public ValueTask<SqlCommandResult> ExecuteAsync(
                string sql,
                CancellationToken cancellationToken = default)
            {
                DirectExecuteCount++;
                return ValueTask.FromResult(new SqlCommandResult(1, string.Empty));
            }
            public ValueTask<SqlCommandResult> ExecuteAsync(
                string sql,
                SqlParameters parameters,
                CancellationToken cancellationToken = default) =>
                ExecuteAsync(sql, cancellationToken);
            public async IAsyncEnumerable<SqlRow> StreamAsync(
                string sql,
                SqlParameters parameters = default,
                int fetchSize = 50,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            private sealed class TestPreparedStatement : ISqlPreparedStatement
            {
                private readonly CancellationTrackingConnection _connection;
                public TestPreparedStatement(CancellationTrackingConnection connection, string sql)
                {
                    _connection = connection;
                    Sql = sql;
                }
                public string Sql { get; }
                public ValueTask<SqlRowSet> QueryAsync(
                    SqlParameters parameters = default,
                    CancellationToken cancellationToken = default) =>
                    throw new NotSupportedException();
                public ValueTask<SqlCommandResult> ExecuteAsync(
                    SqlParameters parameters = default,
                    CancellationToken cancellationToken = default) =>
                    throw new InvalidOperationException("A stale prepared command was executed.");
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
                public async IAsyncEnumerable<SqlRow> StreamAsync(
                    SqlParameters parameters = default,
                    int fetchSize = 50,
                    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
                {
                    await Task.CompletedTask;
                    yield break;
                }
                public ValueTask DisposeAsync()
                {
                    _connection.PreparedDisposeCount++;
                    _connection.PreparedDisposeStarted.TrySetResult();
                    return _connection._preparedDisposeTask is null
                        ? ValueTask.CompletedTask
                        : new ValueTask(_connection._preparedDisposeTask);
                }
            }
        }
    private sealed class TestRowReader : ISqlRowReader
    {
        private static readonly SqlColumn[] ColumnDefinitions =
            [new("value", 0, 0, 0, SqlDataFormat.Binary), new("text", 0, 0, 0, SqlDataFormat.Text)];
        private readonly object?[][] _rows;
        private int _position = -1;

        public TestRowReader(object?[][] rows) => _rows = rows;
        public IReadOnlyList<SqlColumn> Columns => ColumnDefinitions;
        public int FieldCount => Columns.Count;
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _position++;
            return ValueTask.FromResult(_position < _rows.Length);
        }
        public bool IsNull(int ordinal) => Value(ordinal) is null;
        public int GetOrdinal(string name) =>
            string.Equals(name, "value", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        public T Get<T>(int ordinal) => (T)Value(ordinal)!;
        public bool GetBoolean(int ordinal) => Get<bool>(ordinal);
        public short GetInt16(int ordinal) => Get<short>(ordinal);
        public int GetInt32(int ordinal) => Get<int>(ordinal);
        public long GetInt64(int ordinal) => Get<long>(ordinal);
        public float GetFloat(int ordinal) => Get<float>(ordinal);
        public double GetDouble(int ordinal) => Get<double>(ordinal);
        public Guid GetGuid(int ordinal) => Get<Guid>(ordinal);
        public DateOnly GetDateOnly(int ordinal) => Get<DateOnly>(ordinal);
        public TimeOnly GetTimeOnly(int ordinal) => Get<TimeOnly>(ordinal);
        public DateTime GetDateTime(int ordinal) => Get<DateTime>(ordinal);
        public DateTimeOffset GetDateTimeOffset(int ordinal) => Get<DateTimeOffset>(ordinal);
        public int BytesReadCount { get; private set; }
        public int StringReadCount { get; private set; }
        public string GetString(int ordinal)
        {
            StringReadCount++;
            return Get<string>(ordinal);
        }
        public byte[] GetBytes(int ordinal)
        {
            BytesReadCount++;
            return Get<byte[]>(ordinal);
        }
        public TElement[]? GetArray<TElement>(int ordinal) => Get<TElement[]>(ordinal);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private object? Value(int ordinal) => _rows[_position][ordinal];
    }

    private sealed class DrainingMultiResultReader :
        IApexResultBoundaryReader,
        IApexRecordsAffectedReader
    {
        private static readonly SqlColumn[] ColumnDefinitions =
            [new("value", 0, 0, 0, SqlDataFormat.Binary)];
        private readonly object?[][][] _results;
        private int _result;
        private int _nextRow;
        private int _currentRow = -1;
        private readonly int _recordsAffected;

        public DrainingMultiResultReader(object?[][][] results, int recordsAffected = -1)
        {
            _results = results;
            _recordsAffected = recordsAffected;
        }
        public IReadOnlyList<SqlColumn> Columns => ColumnDefinitions;
        public int FieldCount => Columns.Count;
        int IApexRecordsAffectedReader.RecordsAffected => _recordsAffected;
        public ValueTask<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                _result < _results.Length && _results[_result].Length > 0);
        }
        public ValueTask<bool> NextResultAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_result < _results.Length && _nextRow < _results[_result].Length)
            {
                throw new InvalidOperationException("The current result must be drained first.");
            }

            _result++;
            _nextRow = 0;
            _currentRow = -1;
            return ValueTask.FromResult(_result < _results.Length);
        }
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_result >= _results.Length || _nextRow >= _results[_result].Length)
            {
                return ValueTask.FromResult(false);
            }

            _currentRow = _nextRow++;
            return ValueTask.FromResult(true);
        }
        public bool IsNull(int ordinal) => Value(ordinal) is null;
        public int GetOrdinal(string name) => 0;
        public T Get<T>(int ordinal) => (T)Value(ordinal)!;
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
        public DateTimeOffset GetDateTimeOffset(int ordinal) => Get<DateTimeOffset>(ordinal);
        public byte[] GetBytes(int ordinal) => Get<byte[]>(ordinal);
        public TElement[]? GetArray<TElement>(int ordinal) => Get<TElement[]>(ordinal);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private object? Value(int ordinal) => _results[_result][_currentRow][ordinal];
    }

    private sealed class TestMultiResultReader : IApexResultBoundaryReader
    {
        private static readonly SqlColumn[] ColumnDefinitions =
            [new("value", 0, 0, 0, SqlDataFormat.Binary)];
        private readonly object?[][][] _results;
        private int _result;
        private int _row = -1;
        private bool _prefetched;

        public TestMultiResultReader(object?[][][] results) => _results = results;
        public IReadOnlyList<SqlColumn> Columns => ColumnDefinitions;
        public int FieldCount => Columns.Count;
        public ValueTask<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_result >= _results.Length || _results[_result].Length == 0)
            {
                return ValueTask.FromResult(false);
            }

            _row = 0;
            _prefetched = true;
            return ValueTask.FromResult(true);
        }
        public ValueTask<bool> NextResultAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _result++;
            _row = -1;
            _prefetched = false;
            return ValueTask.FromResult(_result < _results.Length);
        }
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_result >= _results.Length) return ValueTask.FromResult(false);
            if (_prefetched)
            {
                _prefetched = false;
                return ValueTask.FromResult(true);
            }

            _row++;
            return ValueTask.FromResult(_row < _results[_result].Length);
        }
        public bool IsNull(int ordinal) => Value(ordinal) is null;
        public int GetOrdinal(string name) => 0;
        public T Get<T>(int ordinal) => (T)Value(ordinal)!;
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
        public DateTimeOffset GetDateTimeOffset(int ordinal) => Get<DateTimeOffset>(ordinal);
        public byte[] GetBytes(int ordinal) => Get<byte[]>(ordinal);
        public TElement[]? GetArray<TElement>(int ordinal) => Get<TElement[]>(ordinal);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private object? Value(int ordinal) => _results[_result][_row][ordinal];
    }
}

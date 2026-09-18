using System.Data;
using System.Data.Common;
using Apex.MsSqlClient;
using Apex.MySqlClient;
using Apex.PgClient;

namespace Apex.SqlClient.AdoNet.Tests;

[TestClass]
public sealed class ExceptionBoundaryTests
{
    [TestMethod]
    [DataRow("PostgreSql")]
    [DataRow("MySql")]
    [DataRow("SqlServer")]
    public async Task NativeErrorsAreTranslatedAtEveryAdapterBoundary(string provider)
    {
        foreach (var boundary in Enum.GetValues<Boundary>())
        {
            var native = CreateNativeException(provider);
            var error = await Assert.ThrowsExactlyAsync<ApexDbException>(
                () => ExerciseAsync(provider, boundary, native), $"{provider}: {boundary}");

            Assert.AreSame(native, error.InnerException);
            Assert.AreEqual(native.Message, error.Message);
            Assert.IsNotNull(native.StackTrace);
            switch (native)
            {
                case PgException postgres:
                    Assert.AreEqual(postgres.SqlState, error.SqlState);
                    Assert.AreEqual(postgres.IsTransient, error.IsTransient);
                    Assert.AreEqual(postgres.HResult, error.ErrorCode);
                    break;
                case MySqlException mysql:
                    Assert.AreEqual(mysql.SqlState, error.SqlState);
                    Assert.AreEqual(mysql.ErrorNumber, error.ErrorCode);
                    Assert.IsFalse(error.IsTransient);
                    break;
                case MsSqlException sqlServer:
                    Assert.IsNull(error.SqlState);
                    Assert.AreEqual(sqlServer.Number, error.ErrorCode);
                    Assert.IsFalse(error.IsTransient);
                    break;
            }
        }
    }

    [TestMethod]
    [DataRow("PostgreSql")]
    [DataRow("MySql")]
    [DataRow("SqlServer")]
    public async Task CancellationAndUnrelatedFailuresPassThroughUnchanged(string provider)
    {
        foreach (var boundary in Enum.GetValues<Boundary>())
        {
            var cancellation = new OperationCanceledException(new CancellationToken(true));
            Assert.AreSame(cancellation, await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => ExerciseAsync(provider, boundary, cancellation), $"{provider}: {boundary}"));
            var unrelated = new InvalidOperationException("Not a database error.");
            Assert.AreSame(unrelated, await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => ExerciseAsync(provider, boundary, unrelated), $"{provider}: {boundary}"));
        }
    }

    [TestMethod]
    public async Task NativeCallsKeepTheirOriginalException()
    {
        var error = new SqlClientException("native");
        var native = new TestConnection(error) { Failure = Boundary.Execute };
        Assert.AreSame(error, await Assert.ThrowsExactlyAsync<SqlClientException>(
            () => native.ExecuteAsync("test").AsTask()));
    }

    [TestMethod]
    public async Task StandaloneCommonReaderWrapsNativeErrors()
    {
        var error = new SqlClientException("reader");
        var native = new TestConnection(error) { Failure = Boundary.Read };
        await using var reader = new ApexDbDataReader(native.Reader, CommandBehavior.Default, null);
        var actual = await Assert.ThrowsExactlyAsync<ApexDbException>(() => reader.ReadAsync());
        Assert.AreSame(error, actual.InnerException);
        Assert.AreEqual(error.HResult, actual.ErrorCode);
    }

    private static SqlClientException CreateNativeException(string provider) => provider switch
    {
        "PostgreSql" => new PgException(new Dictionary<char, string>
        {
            ['M'] = "serialization failure", ['C'] = "40001", ['D'] = "retained detail",
        }),
        "MySql" => new MySqlException(1062, "23000", "duplicate key"),
        "SqlServer" => new MsSqlException(2627, 1, 14, "duplicate key", "server", "procedure", 42),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static async Task ExerciseAsync(string provider, Boundary boundary, Exception error)
    {
        var native = new TestConnection(error);
        ValueTask<ISqlConnection> Open(CancellationToken _) => native.ResultAsync<ISqlConnection>(Boundary.Open, native);
        await using DbConnection connection = provider switch
        {
            "PostgreSql" => new PgDbConnection("Host=localhost;Database=test", Open),
            "MySql" => new MySqlDbConnection("Server=localhost;Database=test", Open),
            "SqlServer" => new MsSqlDbConnection("Server=localhost;Database=test", Open),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

        if (boundary == Boundary.Open)
        {
            native.Failure = boundary;
            await connection.OpenAsync();
            return;
        }

        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "test";
        switch (boundary)
        {
            case Boundary.Prepare:
                native.Failure = boundary;
                await command.PrepareAsync();
                break;
            case Boundary.PreparedExecute:
            case Boundary.PreparedDispose:
            case Boundary.PreparedSyncDispose:
                await command.PrepareAsync();
                native.Failure = boundary;
                if (boundary == Boundary.PreparedExecute) await command.ExecuteReaderAsync();
                else if (boundary == Boundary.PreparedDispose) await command.DisposeAsync();
                else command.Dispose();
                break;
            case Boundary.Execute:
            case Boundary.NonQuery:
            case Boundary.Scalar:
            case Boundary.Initialize:
                native.Failure = boundary == Boundary.NonQuery || boundary == Boundary.Scalar
                    ? Boundary.Execute
                    : boundary;
                if (boundary == Boundary.NonQuery) await command.ExecuteNonQueryAsync();
                else if (boundary == Boundary.Scalar) await command.ExecuteScalarAsync();
                else await command.ExecuteReaderAsync();
                break;
            case Boundary.Read:
            case Boundary.NextResult:
            case Boundary.GetValue:
            case Boundary.ReaderDispose:
            case Boundary.ReaderSyncDispose:
                await using (var reader = await command.ExecuteReaderAsync())
                {
                    native.Failure = boundary;
                    if (boundary == Boundary.Read) await reader.ReadAsync();
                    else if (boundary == Boundary.NextResult) await reader.NextResultAsync();
                    else if (boundary == Boundary.GetValue) _ = reader.GetValue(0);
                    else if (boundary == Boundary.ReaderDispose) await reader.DisposeAsync();
                    else reader.Dispose();
                }
                break;
            case Boundary.BeginTransaction:
                native.Failure = boundary;
                await connection.BeginTransactionAsync();
                break;
            case Boundary.Commit:
            case Boundary.Rollback:
            case Boundary.TransactionDispose:
            case Boundary.TransactionSyncDispose:
                await using (var transaction = await connection.BeginTransactionAsync())
                {
                    native.Failure = boundary;
                    if (boundary == Boundary.Commit) await transaction.CommitAsync();
                    else if (boundary == Boundary.Rollback) await transaction.RollbackAsync();
                    else if (boundary == Boundary.TransactionDispose) await transaction.DisposeAsync();
                    else transaction.Dispose();
                }
                break;
            case Boundary.Close:
            case Boundary.SyncClose:
            case Boundary.ConnectionDispose:
                native.Failure = boundary;
                if (boundary == Boundary.Close) await connection.CloseAsync();
                else if (boundary == Boundary.SyncClose) connection.Close();
                else await connection.DisposeAsync();
                break;
            case Boundary.BatchPrepare:
            case Boundary.BatchExecute:
            case Boundary.BatchDispose:
                await using (var batch = connection.CreateBatch())
                {
                    var batchCommand = batch.CreateBatchCommand();
                    batchCommand.CommandText = "test";
                    batch.BatchCommands.Add(batchCommand);
                    if (boundary == Boundary.BatchDispose) await batch.PrepareAsync();
                    native.Failure = boundary;
                    if (boundary == Boundary.BatchPrepare) await batch.PrepareAsync();
                    else if (boundary == Boundary.BatchExecute) await batch.ExecuteNonQueryAsync();
                    else await batch.DisposeAsync();
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(boundary));
        }
    }

    private enum Boundary
    {
        Open, Prepare, Execute, NonQuery, Scalar, PreparedExecute, Initialize, Read,
        NextResult, GetValue, BeginTransaction, Commit, Rollback, ReaderDispose,
        ReaderSyncDispose, PreparedDispose, PreparedSyncDispose, TransactionDispose,
        TransactionSyncDispose, Close, SyncClose, ConnectionDispose, BatchPrepare,
        BatchExecute, BatchDispose,
    }

    private sealed class TestConnection(Exception error) : ISqlConnection, ISqlResultReaderConnection
    {
        public Boundary? Failure { get; set; }
        public TestReader Reader => new(this);
        public bool IsSecure => false;
        public DatabaseMetadata DatabaseMetadata => new("test", "1", 1, 0);

        public void Check(params Boundary[] boundaries)
        {
            if (Failure is { } failure && boundaries.Contains(failure))
            {
                Failure = null;
                throw error;
            }
        }

        public async ValueTask<T> ResultAsync<T>(Boundary boundary, T result)
        {
            await Task.Yield();
            Check(boundary);
            return result;
        }

        public async ValueTask CompleteAsync(params Boundary[] boundaries)
        {
            await Task.Yield();
            Check(boundaries);
        }

        public async ValueTask<ISqlPreparedStatement> PrepareAsync(string sql, CancellationToken cancellationToken = default)
        {
            await CompleteAsync(Boundary.Prepare, Boundary.BatchPrepare);
            return new TestStatement(this);
        }

        public ValueTask<ISqlRowReader> ExecuteReaderAsync(
            string sql, SqlParameters parameters = default, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Provider adapters must opt into the result-set capability.");

        public async ValueTask<ISqlRowReader> ExecuteResultReaderAsync(
            string sql, SqlParameters parameters, CancellationToken cancellationToken)
        {
            await CompleteAsync(Boundary.Execute, Boundary.BatchExecute);
            return Reader;
        }

        public ValueTask<ISqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            ResultAsync<ISqlTransaction>(Boundary.BeginTransaction, new TestTransaction(this));
        public ValueTask<SqlCommandResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default) =>
            ResultAsync(Boundary.Execute, new SqlCommandResult(1, string.Empty));
        public ValueTask<SqlCommandResult> ExecuteAsync(
            string sql, SqlParameters parameters, CancellationToken cancellationToken = default) =>
            ExecuteAsync(sql, cancellationToken);
        public ValueTask<SqlRowSet> QueryAsync(string sql, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<SqlRowSet> QueryAsync(
            string sql, SqlParameters parameters, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<SqlRow> StreamAsync(
            string sql, SqlParameters parameters = default, int fetchSize = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() =>
            CompleteAsync(Boundary.Close, Boundary.SyncClose, Boundary.ConnectionDispose);
    }

    private sealed class TestStatement(TestConnection connection) : ISqlPreparedStatement, ISqlResultPreparedStatement
    {
        public string Sql => "test";
        public ValueTask<ISqlRowReader> ExecuteResultReaderAsync(SqlParameters parameters, CancellationToken cancellationToken) =>
            connection.ResultAsync<ISqlRowReader>(Boundary.PreparedExecute, connection.Reader);
        public ValueTask<ISqlRowReader> ExecuteReaderAsync(
            SqlParameters parameters = default, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Prepared execution must opt into the result-set capability.");
        public ValueTask<SqlCommandResult> ExecuteAsync(
            SqlParameters parameters = default, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<SqlRowSet> QueryAsync(
            SqlParameters parameters = default, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<IReadOnlyList<SqlCommandResult>> ExecuteBatchAsync(
            IReadOnlyList<SqlParameters> batch, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<ISqlCursor> OpenCursorAsync(
            SqlParameters parameters = default, int fetchSize = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<SqlRow> StreamAsync(
            SqlParameters parameters = default, int fetchSize = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() =>
            connection.CompleteAsync(Boundary.PreparedDispose, Boundary.PreparedSyncDispose, Boundary.BatchDispose);
    }

    private sealed class TestTransaction(TestConnection connection) : ISqlTransaction
    {
        public bool IsCompleted => false;
        public ValueTask CommitAsync(CancellationToken cancellationToken = default) =>
            connection.CompleteAsync(Boundary.Commit);
        public ValueTask RollbackAsync(CancellationToken cancellationToken = default) =>
            connection.CompleteAsync(Boundary.Rollback);
        public ValueTask DisposeAsync() =>
            connection.CompleteAsync(Boundary.TransactionDispose, Boundary.TransactionSyncDispose);
    }

    private sealed class TestReader(TestConnection connection) : ISqlResultBoundaryReader
    {
        public IReadOnlyList<SqlColumn> Columns { get; } = [new("value", 0, 0, 0, SqlDataFormat.Binary)];
        public int FieldCount => 1;
        public ValueTask<bool> InitializeAsync(CancellationToken cancellationToken = default) =>
            connection.ResultAsync(Boundary.Initialize, false);
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default) =>
            connection.ResultAsync(Boundary.Read, false);
        public ValueTask<bool> NextResultAsync(CancellationToken cancellationToken = default) =>
            connection.ResultAsync(Boundary.NextResult, false);
        public T Get<T>(int ordinal)
        {
            connection.Check(Boundary.GetValue);
            throw new InvalidOperationException("The test has no current row.");
        }
        public int GetOrdinal(string name) => 0;
        public bool IsNull(int ordinal) => Get<bool>(ordinal);
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
        public ValueTask DisposeAsync() =>
            connection.CompleteAsync(Boundary.ReaderDispose, Boundary.ReaderSyncDispose);
    }
}

using System.Collections;
using System.Data;
using System.Data.Common;

namespace Apex.SqlClient;

/// <summary>Shared implementation details for the optional asynchronous ADO.NET adapters.</summary>
public abstract class ApexDbParameter : DbParameter
{
    private DbType _dbType = DbType.Object;
    private ParameterDirection _direction = ParameterDirection.Input;
    private bool _dbTypeWasSet;

    public override DbType DbType
    {
        get => _dbType;
        set
        {
            _dbType = value;
            _dbTypeWasSet = true;
        }
    }
    public override ParameterDirection Direction
    {
        get => _direction;
        set => _direction = value;
    }
    public override bool IsNullable { get; set; }
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ParameterName { get; set; } = string.Empty;
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;
    public override object? Value { get; set; }
    public override bool SourceColumnNullMapping { get; set; }
    public override int Size { get; set; }
    public override DataRowVersion SourceVersion { get; set; } = DataRowVersion.Current;
    public override void ResetDbType()
    {
        _dbType = DbType.Object;
        _dbTypeWasSet = false;
    }

    internal SqlValue ToSqlValue()
    {
        if (Direction != ParameterDirection.Input)
        {
            throw new NotSupportedException("Only input parameters are supported.");
        }

        if (_dbTypeWasSet)
        {
            throw new NotSupportedException(
                "Explicit DbType is not supported; Apex infers parameter metadata from Value.");
        }

        return Value is null or DBNull ? SqlValue.Null : SqlValue.From(Value);
    }
}

/// <summary>Shared parameter collection implementation for the ADO.NET adapters.</summary>
public abstract class ApexDbParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _parameters = [];

    public override int Count => _parameters.Count;
    public override object SyncRoot => ((ICollection)_parameters).SyncRoot;
    internal int Version { get; private set; }

    private void Changed() => Version++;

    public override int Add(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is not DbParameter parameter)
        {
            throw new ArgumentException("Only DbParameter instances can be added.", nameof(value));
        }

        _parameters.Add(parameter);
        Changed();
        return _parameters.Count - 1;
    }

    public override void AddRange(Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (object? value in values)
        {
            Add(value!);
        }
    }

    public override void Clear()
    {
        if (_parameters.Count == 0) return;
        _parameters.Clear();
        Changed();
    }
    public override bool Contains(object value) => _parameters.Contains((DbParameter)value);
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)_parameters).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();
    protected override DbParameter GetParameter(int index) => _parameters[index];
    protected override DbParameter GetParameter(string parameterName) =>
        _parameters[IndexOf(parameterName)];
    public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName) =>
        _parameters.FindIndex(x => string.Equals(x.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));
    public override void Insert(int index, object value)
    {
        _parameters.Insert(index, (DbParameter)value);
        Changed();
    }
    public override void Remove(object value)
    {
        if (_parameters.Remove((DbParameter)value)) Changed();
    }
    public override void RemoveAt(int index)
    {
        _parameters.RemoveAt(index);
        Changed();
    }
    public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));
    protected override void SetParameter(int index, DbParameter value)
    {
        _parameters[index] = value;
        Changed();
    }
    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index < 0) _parameters.Add(value); else _parameters[index] = value;
        Changed();
    }

    internal SqlParameters ToSqlParameters()
    {
        if (_parameters.Count == 0) return SqlParameters.Empty;
        var values = new SqlValue[_parameters.Count];
        for (var i = 0; i < values.Length; i++)
        {
            if (_parameters[i] is not ApexDbParameter parameter)
            {
                throw new ArgumentException(
                    "Parameters must be created by the Apex provider.", nameof(_parameters));
            }

            values[i] = parameter.ToSqlValue();
        }
        return SqlParameters.From(values);
    }
}

/// <summary>Base class for asynchronous-only ADO.NET commands over an Apex connection.</summary>
public abstract class ApexDbCommand : DbCommand
{
    private readonly ApexDbParameterCollection _parameters;
    private CancellationTokenSource? _activeCancellation;
    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private ISqlPreparedStatement? _preparedStatement;
    private ISqlConnection? _preparedConnection;
    private ApexDbConnection? _preparedConnectionOwner;
    private bool _preparedConnectionWasAutoOpened;
    private bool _preparedInvalidated;
    private readonly IApexAdoReaderFactory? _adoReaderFactory;

    protected ApexDbCommand(ApexDbParameterCollection parameters) : this(parameters, null) { }

    internal ApexDbCommand(
        ApexDbParameterCollection parameters,
        IApexAdoReaderFactory? adoReaderFactory)
    {
        _parameters = parameters;
        _adoReaderFactory = adoReaderFactory;
    }

    private string _commandText = string.Empty;
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set
        {
            if (!string.Equals(_commandText, value, StringComparison.Ordinal))
            {
                _preparedInvalidated = _preparedStatement is not null;
                _commandText = value ?? string.Empty;
            }
        }
    }
    public override int CommandTimeout { get; set; } = 30;
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection
    {
        get => _connection;
        set
        {
            if (!ReferenceEquals(_connection, value))
            {
                _preparedInvalidated = _preparedStatement is not null;
                _connection = value;
            }
        }
    }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get => _transaction; set => _transaction = value; }

    protected abstract ApexDbParameter CreateParameterCore();
    protected abstract ISqlConnection GetConnection();

    public override void Cancel() => _activeCancellation?.Cancel();
    public override void Prepare() => throw AsyncOnly();
    public override Task PrepareAsync(CancellationToken cancellationToken = default) => PrepareCoreAsync(cancellationToken);
    private async Task PrepareCoreAsync(CancellationToken cancellationToken)
    {
        Validate();
        await DisposePreparedAsync().ConfigureAwait(false);
        using var timeout = CreateTimeoutToken(cancellationToken);
        var execution = await OpenForExecutionAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            _preparedStatement = await execution.Connection
                .PrepareAsync(CommandText!, timeout.Token).ConfigureAwait(false);
            _preparedConnection = execution.Connection;
            _preparedConnectionOwner = execution.ConnectionOwner;
            _preparedConnectionWasAutoOpened = execution.CloseWhenDone;
        }
        catch
        {
            if (execution.CloseWhenDone)
            {
                await execution.ConnectionOwner.CloseAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            CompleteOperation(timeout);
        }
    }

    protected override DbParameter CreateDbParameter() => CreateParameterCore();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw AsyncOnly();
    public override int ExecuteNonQuery() => throw AsyncOnly();
    public override object? ExecuteScalar() => throw AsyncOnly();

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        await DisposeInvalidatedPreparedAsync().ConfigureAwait(false);
        Validate();
        ValidateBehavior(behavior);
        var timeout = CreateTimeoutToken(cancellationToken);
        ExecutionConnection execution = default;
        ISqlRowReader? nativeReader = null;
        var transferred = false;
        try
        {
            var parameters = _parameters.ToSqlParameters();
            execution = await OpenForExecutionAsync(timeout.Token).ConfigureAwait(false);
            var prepared = _preparedStatement is not null &&
                ReferenceEquals(execution.Connection, _preparedConnection)
                ? _preparedStatement
                : null;
            nativeReader = _adoReaderFactory is not null
                ? await _adoReaderFactory.ExecuteReaderAsync(
                    execution.Connection,
                    CommandText!,
                    parameters,
                    prepared,
                    timeout.Token).ConfigureAwait(false)
                : prepared is not null
                    ? await prepared.ExecuteReaderAsync(parameters, timeout.Token).ConfigureAwait(false)
                    : await execution.Connection.ExecuteReaderAsync(
                        CommandText!,
                        parameters,
                        timeout.Token).ConfigureAwait(false);
            var result = CreateReader(
                nativeReader,
                behavior,
                execution.ConnectionOwner,
                timeout,
                () => CompleteReaderAsync(timeout, execution, behavior));
            result.ConfigureCommandTimeout(CommandTimeout);
            await result.InitializeAsync(timeout.Token).ConfigureAwait(false);
            transferred = true;
            return result;
        }
        finally
        {
            if (!transferred)
            {
                timeout.Cancel();
                try
                {
                    if (nativeReader is not null)
                    {
                        await nativeReader.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    try
                    {
                        if (execution.CloseWhenDone)
                        {
                            await execution.ConnectionOwner.CloseAsync().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        CompleteOperation(timeout);
                    }
                }
            }
        }
    }

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        if (_adoReaderFactory is null)
        {
            await DisposeInvalidatedPreparedAsync().ConfigureAwait(false);
            Validate();
            using var timeout = CreateTimeoutToken(cancellationToken);
            ExecutionConnection execution = default;
            try
            {
                var parameters = _parameters.ToSqlParameters();
                execution = await OpenForExecutionAsync(timeout.Token).ConfigureAwait(false);
                var result = _preparedStatement is not null &&
                    ReferenceEquals(execution.Connection, _preparedConnection)
                    ? await _preparedStatement.ExecuteAsync(parameters, timeout.Token).ConfigureAwait(false)
                    : await execution.Connection.ExecuteAsync(
                        CommandText!,
                        parameters,
                        timeout.Token).ConfigureAwait(false);
                return checked((int)result.AffectedRows);
            }
            finally
            {
                try
                {
                    if (execution.CloseWhenDone)
                    {
                        await execution.ConnectionOwner.CloseAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    CompleteOperation(timeout);
                }
            }
        }

        var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            do
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                }
            }
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

            return reader.RecordsAffected;
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var reader = await ExecuteDbDataReaderAsync(CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            do
            {
                if (reader.FieldCount > 0)
                {
                    return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                        ? reader.GetValue(0)
                        : null;
                }
            }
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

            return null;
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    protected virtual ApexDbDataReader CreateReader(
        ISqlRowReader reader,
        CommandBehavior behavior,
        DbConnection executedConnection,
        CancellationTokenSource operationCancellation,
        Func<ValueTask> onClose) =>
        new(reader, behavior, executedConnection, operationCancellation, onClose);

    private CancellationTokenSource CreateTimeoutToken(CancellationToken cancellationToken)
    {
        _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (CommandTimeout > 0) _activeCancellation.CancelAfter(TimeSpan.FromSeconds(CommandTimeout));
        return _activeCancellation;
    }

    private void Validate()
    {
        if (CommandType != CommandType.Text) throw new NotSupportedException("Only CommandType.Text is supported.");
        if (string.IsNullOrWhiteSpace(CommandText)) throw new InvalidOperationException("CommandText is required.");
        if (_transaction is ApexDbTransaction transaction &&
            !ReferenceEquals(transaction.Connection, _connection))
        {
            throw new InvalidOperationException("The transaction belongs to a different connection.");
        }

        if (_transaction is not null && _transaction is not ApexDbTransaction)
        {
            throw new ArgumentException("The transaction must be created by the Apex provider.");
        }

    }

    private async ValueTask<ExecutionConnection> OpenForExecutionAsync(CancellationToken cancellationToken)
    {
        if (_connection is not ApexDbConnection connection)
        {
            _ = GetConnection();
            throw new InvalidOperationException("The command connection must be an Apex provider connection.");
        }

        var closeWhenDone = await connection.OpenForCommandAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var native = GetConnection();
            if (_preparedStatement is not null && !ReferenceEquals(native, _preparedConnection))
            {
                throw new InvalidOperationException("The prepared statement belongs to a different connection.");
            }

            return new ExecutionConnection(native, connection, closeWhenDone);
        }
        catch
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private async ValueTask CompleteReaderAsync(
        CancellationTokenSource cancellation,
        ExecutionConnection execution,
        CommandBehavior behavior)
    {
        try
        {
            if (execution.CloseWhenDone || (behavior & CommandBehavior.CloseConnection) != 0)
            {
                await execution.ConnectionOwner.CloseAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            CompleteOperation(cancellation);
        }
    }

    private void CompleteOperation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref _activeCancellation, null, cancellation), cancellation))
        {
            cancellation.Dispose();
        }
    }

    private readonly record struct ExecutionConnection(
        ISqlConnection Connection,
        ApexDbConnection ConnectionOwner,
        bool CloseWhenDone);

    private static void ValidateBehavior(CommandBehavior behavior)
    {
        const CommandBehavior allowed = CommandBehavior.Default | CommandBehavior.SingleRow |
            CommandBehavior.SingleResult | CommandBehavior.SequentialAccess | CommandBehavior.CloseConnection;
        if ((behavior & ~allowed) != 0) throw new NotSupportedException($"CommandBehavior '{behavior}' is not supported.");
    }

    internal static NotSupportedException AsyncOnly() =>
        new("Apex ADO.NET adapters support asynchronous I/O only. Use the corresponding Async method.");

    private void DisposePreparedSynchronously()
    {
        if (_preparedStatement is null) return;
        var statement = _preparedStatement;
        _preparedStatement = null;
        _preparedConnection = null;
        var connectionOwner = _preparedConnectionOwner;
        _preparedConnectionOwner = null;
        try
        {
            statement.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            if (_preparedConnectionWasAutoOpened && connectionOwner is not null)
            {
                connectionOwner.Close();
            }

            _preparedConnectionWasAutoOpened = false;
            _preparedInvalidated = false;
        }
    }

    private async ValueTask DisposePreparedAsync()
    {
        if (_preparedStatement is null)
        {
            _preparedInvalidated = false;
            return;
        }
        var statement = _preparedStatement;
        _preparedStatement = null;
        _preparedConnection = null;
        var connectionOwner = _preparedConnectionOwner;
        _preparedConnectionOwner = null;
        try
        {
            await statement.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_preparedConnectionWasAutoOpened && connectionOwner is not null)
            {
                await connectionOwner.CloseAsync().ConfigureAwait(false);
            }

            _preparedConnectionWasAutoOpened = false;
            _preparedInvalidated = false;
        }
    }

    private ValueTask DisposeInvalidatedPreparedAsync() =>
        _preparedInvalidated ? DisposePreparedAsync() : ValueTask.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposePreparedSynchronously();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await DisposePreparedAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Streaming ADO.NET reader over an Apex borrowed reader.</summary>
public class ApexDbDataReader : DbDataReader
{
    private ISqlRowReader _reader;
    private readonly CommandBehavior _behavior;
    private readonly DbConnection? _connection;
    private readonly CancellationTokenSource? _operationCancellation;
    private readonly Func<ValueTask>? _onClose;
    private int _commandTimeout;
    private bool _closed;
    private bool _returnedSingleRow;
    private IReadOnlyList<SqlColumn> _columns;
    private bool _hasRowsInResult;
    private bool _prefetchedRow;
    private bool _prefetchedNeedsDelivery;
    private byte[]?[]? _bytes;
    private string?[]? _chars;

    public ApexDbDataReader(ISqlRowReader reader, CommandBehavior behavior, DbConnection? connection)
        : this(reader, behavior, connection, null, null)
    {
    }

    internal ApexDbDataReader(
        ISqlRowReader reader,
        CommandBehavior behavior,
        DbConnection? connection,
        CancellationTokenSource? operationCancellation,
        Func<ValueTask>? onClose)
    {
        _reader = reader;
        _behavior = behavior;
        _connection = connection;
        _operationCancellation = operationCancellation;
        _onClose = onClose;
        _columns = reader.Columns;
    }

    public override int FieldCount => _columns.Count;
    public override bool HasRows => !_closed && _hasRowsInResult;
    public override int Depth => 0;
    public override bool IsClosed => _closed;
    public override int RecordsAffected =>
        _reader is IApexRecordsAffectedReader recordsAffected
            ? recordsAffected.RecordsAffected
            : -1;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
    public override string GetName(int ordinal) => _columns[ordinal].Name;
#pragma warning disable CA2201 // DbDataReader's GetOrdinal contract requires IndexOutOfRangeException.
    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < _columns.Count; i++)
        {
            if (string.Equals(_columns[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        throw new IndexOutOfRangeException($"Column '{name}' does not exist.");
    }
#pragma warning restore CA2201
#pragma warning disable IL2093 // DbDataReader's annotation differs between supported frameworks.
    public override Type GetFieldType(int ordinal) => typeof(object);
#pragma warning restore IL2093
    public override string GetDataTypeName(int ordinal) => _columns[ordinal].TypeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public override bool GetBoolean(int ordinal) => _reader.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => _reader.Get<byte>(ordinal);
    public override char GetChar(int ordinal) => _reader.Get<char>(ordinal);
    public override DateTime GetDateTime(int ordinal) => _reader.GetDateTime(ordinal);
    public override decimal GetDecimal(int ordinal) => _reader.GetDecimal(ordinal);
    public override double GetDouble(int ordinal) => _reader.GetDouble(ordinal);
    public override float GetFloat(int ordinal) => _reader.GetFloat(ordinal);
    public override Guid GetGuid(int ordinal) => _reader.GetGuid(ordinal);
    public override short GetInt16(int ordinal) => _reader.GetInt16(ordinal);
    public override int GetInt32(int ordinal) => _reader.GetInt32(ordinal);
    public override long GetInt64(int ordinal) => _reader.GetInt64(ordinal);
    public override string GetString(int ordinal) => _reader.GetString(ordinal);
    public override bool IsDBNull(int ordinal) => _reader.IsNull(ordinal);
    public override object GetValue(int ordinal) => _reader.Get<object>(ordinal) ?? DBNull.Value;
    public override T GetFieldValue<T>(int ordinal) => _reader.Get<T>(ordinal);
    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++) values[i] = GetValue(i);
        return count;
    }
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = GetCachedBytes(ordinal);
        ValidateDataOffset(dataOffset, bytes.Length);
        if (buffer is null) return bytes.Length;
        ValidateBuffer(buffer.Length, bufferOffset, length);
        var count = Math.Min(length, bytes.Length - (int)dataOffset);
        Array.Copy(bytes, dataOffset, buffer, bufferOffset, count);
        return count;
    }
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var value = GetCachedChars(ordinal);
        ValidateDataOffset(dataOffset, value.Length);
        if (buffer is null) return value.Length;
        ValidateBuffer(buffer.Length, bufferOffset, length);
        var count = Math.Min(length, value.Length - (int)dataOffset);
        value.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }
    public override IEnumerator GetEnumerator() => throw ApexDbCommand.AsyncOnly();
    public override bool Read() => throw ApexDbCommand.AsyncOnly();
    public override bool NextResult() => throw ApexDbCommand.AsyncOnly();
    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (_closed) throw new InvalidOperationException("The reader is closed.");
        if (_returnedSingleRow && (_behavior & CommandBehavior.SingleRow) != 0) return Task.FromResult(false);
        if (_prefetchedRow)
        {
            if (_prefetchedNeedsDelivery)
            {
                return DeliverPrefetchedRowAsync(cancellationToken);
            }

            _prefetchedRow = false;
            _returnedSingleRow = true;
            return Task.FromResult(true);
        }

        return ReadCoreAsync(cancellationToken);
    }
    private async Task<bool> DeliverPrefetchedRowAsync(CancellationToken cancellationToken)
    {
        ArmTimeout();
        try
        {
            var read = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            _prefetchedRow = false;
            _prefetchedNeedsDelivery = false;
            _returnedSingleRow |= read;
            return read;
        }
        finally
        {
            DisarmTimeout();
        }
    }
    private async Task<bool> ReadCoreAsync(CancellationToken cancellationToken)
    {
        ArmTimeout();
        try
        {
            var read = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (read)
            {
                _columns = _reader.Columns;
                _hasRowsInResult = true;
            }
            _returnedSingleRow |= read;
            if (read) ClearValueCaches();
            return read;
        }
        finally
        {
            DisarmTimeout();
        }
    }
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) =>
        NextResultCoreAsync(cancellationToken);

    private async Task<bool> NextResultCoreAsync(CancellationToken cancellationToken)
    {
        if ((_behavior & CommandBehavior.SingleResult) != 0) return false;
        if (_closed) throw new InvalidOperationException("The reader is closed.");
        if (_reader is not IApexMultiResultReader multiResultReader)
        {
            return false;
        }

        ArmTimeout();
        try
        {
            await DrainCurrentResultAsync(cancellationToken).ConfigureAwait(false);
            if (!await multiResultReader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            _columns = _reader.Columns;
            _returnedSingleRow = false;
            if (_reader is IApexResultBoundaryReader bounded)
            {
                _hasRowsInResult = await bounded.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _prefetchedRow = _hasRowsInResult;
                _prefetchedNeedsDelivery = _prefetchedRow;
            }
            else
            {
                _prefetchedRow = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                _hasRowsInResult = _prefetchedRow;
                _prefetchedNeedsDelivery = false;
            }
            ClearValueCaches();
            return true;
        }
        finally
        {
            DisarmTimeout();
        }
    }

    private async ValueTask DrainCurrentResultAsync(CancellationToken cancellationToken)
    {
        if (_prefetchedRow)
        {
            if (_prefetchedNeedsDelivery)
            {
                _ = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }

            _prefetchedRow = false;
            _prefetchedNeedsDelivery = false;
        }

        while (await _reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
        }
    }
    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) =>
        Task.FromResult(IsDBNull(ordinal));
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) =>
        Task.FromResult(_reader.Get<T>(ordinal));
    public override void Close() => CloseAsync().GetAwaiter().GetResult();
    public override async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (_onClose is not null)
                {
                    await _onClose().ConfigureAwait(false);
                }
                else if ((_behavior & CommandBehavior.CloseConnection) != 0 && _connection is not null)
                {
                    await _connection.CloseAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _operationCancellation?.Dispose();
            }
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed) Close();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected async ValueTask ReplaceReaderAsync(ISqlRowReader reader)
    {
        await _reader.DisposeAsync().ConfigureAwait(false);
        _reader = reader;
        _columns = reader.Columns;
        _returnedSingleRow = false;
        _hasRowsInResult = false;
        _prefetchedRow = false;
        _prefetchedNeedsDelivery = false;
        ClearValueCaches();
    }

    internal async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_reader is IApexResultBoundaryReader bounded)
            {
                _hasRowsInResult = await bounded.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _columns = _reader.Columns;
                _prefetchedRow = _hasRowsInResult;
                _prefetchedNeedsDelivery = _prefetchedRow;
                return;
            }

            _prefetchedRow = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            _prefetchedNeedsDelivery = false;
            _hasRowsInResult = _prefetchedRow;
            _columns = _reader.Columns;
        }
        finally
        {
            DisarmTimeout();
        }
    }

    internal void ConfigureCommandTimeout(int commandTimeout) => _commandTimeout = commandTimeout;

    private void ArmTimeout()
    {
        if (_commandTimeout > 0)
        {
            _operationCancellation?.CancelAfter(TimeSpan.FromSeconds(_commandTimeout));
        }
    }

    private void DisarmTimeout() => _operationCancellation?.CancelAfter(Timeout.InfiniteTimeSpan);

    private byte[] GetCachedBytes(int ordinal)
    {
        EnsureValueCacheCapacity();
        return _bytes![ordinal] ??= _reader.GetBytes(ordinal);
    }

    private string GetCachedChars(int ordinal)
    {
        EnsureValueCacheCapacity();
        return _chars![ordinal] ??= _reader.GetString(ordinal);
    }

    private void EnsureValueCacheCapacity()
    {
        if (_bytes?.Length == FieldCount) return;
        _bytes = new byte[]?[FieldCount];
        _chars = new string?[FieldCount];
    }

    private void ClearValueCaches()
    {
        _bytes = null;
        _chars = null;
    }

    private static void ValidateDataOffset(long dataOffset, int dataLength)
    {
        if (dataOffset < 0 || dataOffset > dataLength)
        {
            throw new ArgumentOutOfRangeException(nameof(dataOffset));
        }
    }

    private static void ValidateBuffer(int bufferLength, int bufferOffset, int length)
    {
        if (bufferOffset < 0 || length < 0 || bufferOffset > bufferLength - length)
        {
            throw new ArgumentOutOfRangeException(
                bufferOffset < 0 || bufferOffset > bufferLength ? nameof(bufferOffset) : nameof(length));
        }
    }
}

/// <summary>Optional bridge for drivers that expose result-set transitions.</summary>
public interface IApexMultiResultReader : ISqlRowReader
{
    ValueTask<bool> NextResultAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A multi-result reader that can establish the first result's metadata and row availability
/// before it is exposed through ADO.NET.
/// </summary>
public interface IApexResultBoundaryReader : IApexMultiResultReader
{
    ValueTask<bool> InitializeAsync(CancellationToken cancellationToken = default);
}

internal interface IApexAdoReaderFactory
{
    ValueTask<ISqlRowReader> ExecuteReaderAsync(
        ISqlConnection connection,
        string sql,
        SqlParameters parameters,
        ISqlPreparedStatement? preparedStatement,
        CancellationToken cancellationToken);
}

internal interface IApexAdoReaderConnection
{
    ValueTask<ISqlRowReader> ExecuteAdoReaderAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken);
}

internal interface IApexAdoPreparedStatement
{
    ValueTask<ISqlRowReader> ExecuteAdoReaderAsync(
        SqlParameters parameters,
        CancellationToken cancellationToken);
}

internal interface IApexRecordsAffectedReader
{
    int RecordsAffected { get; }
}

/// <summary>Async-only ADO.NET transaction wrapper.</summary>
public class ApexDbTransaction : DbTransaction
{
    private readonly ISqlTransaction _transaction;
    private readonly DbConnection _connection;
    private readonly IsolationLevel _isolationLevel;
    public ApexDbTransaction(ISqlTransaction transaction, DbConnection connection, IsolationLevel isolationLevel)
    {
        _transaction = transaction;
        _connection = connection;
        _isolationLevel = isolationLevel;
    }
    public override IsolationLevel IsolationLevel => _isolationLevel;
    protected override DbConnection DbConnection => _connection;
    public override void Commit() => throw ApexDbCommand.AsyncOnly();
    public override void Rollback() => throw ApexDbCommand.AsyncOnly();
    public override Task CommitAsync(CancellationToken cancellationToken = default) =>
        _transaction.CommitAsync(cancellationToken).AsTask();
    public override Task RollbackAsync(CancellationToken cancellationToken = default) =>
        _transaction.RollbackAsync(cancellationToken).AsTask();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _transaction.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
    public override ValueTask DisposeAsync() => _transaction.DisposeAsync();
}

/// <summary>Base connection implementation for provider-local asynchronous ADO.NET adapters.</summary>
public abstract class ApexDbConnection : DbConnection
{
    private ISqlConnection? _connection;
    private ConnectionState _state = ConnectionState.Closed;
    private string _connectionString;
    private string _database;
    private string _dataSource;
    private int _connectionTimeout;
    private readonly bool _isPoolBound;
    private readonly bool _autoOpenForCommands;

    protected ApexDbConnection(
        string connectionString,
        string database,
        string dataSource,
        TimeSpan connectionTimeout,
        bool isPoolBound = false,
        bool autoOpenForCommands = false)
    {
        _connectionString = connectionString;
        _database = database;
        _dataSource = dataSource;
        _connectionTimeout = connectionTimeout <= TimeSpan.Zero ? 0 : checked((int)connectionTimeout.TotalSeconds);
        _isPoolBound = isPoolBound;
        _autoOpenForCommands = autoOpenForCommands;
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (_state != ConnectionState.Closed)
            {
                throw new InvalidOperationException("ConnectionString cannot be changed while the connection is open.");
            }

            if (_isPoolBound)
            {
                throw new InvalidOperationException(
                    "ConnectionString cannot be changed on a connection owned by a DbDataSource.");
            }

            SetConnectionStringCore(value ?? string.Empty);
        }
    }
    public override string Database => _database;
    public override string DataSource => _dataSource;
    public override string ServerVersion => _connection?.DatabaseMetadata.FullVersion ?? string.Empty;
    public override int ConnectionTimeout => _connectionTimeout;
    public override ConnectionState State => _state;
    protected ISqlConnection NativeConnection =>
        _connection ?? throw new InvalidOperationException("The connection is not open.");

    protected abstract Task<ISqlConnection> OpenCoreAsync(CancellationToken cancellationToken);
    protected abstract DbCommand CreateCommandCore();
    protected abstract void SetConnectionStringCore(string connectionString);
    protected void SetConnectionMetadata(
        string connectionString,
        string database,
        string dataSource,
        TimeSpan connectionTimeout)
    {
        _connectionString = connectionString;
        _database = database;
        _dataSource = dataSource;
        _connectionTimeout = connectionTimeout <= TimeSpan.Zero
            ? 0
            : checked((int)connectionTimeout.TotalSeconds);
    }
    protected void AttachOpenConnection(ISqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (_connection is not null) throw new InvalidOperationException("The connection is already open.");
        _connection = connection;
        _state = ConnectionState.Open;
    }
    internal async ValueTask<bool> OpenForCommandAsync(CancellationToken cancellationToken)
    {
        if (_state == ConnectionState.Open) return false;
        if (!_autoOpenForCommands)
        {
            throw new InvalidOperationException("The connection must be open before executing a command.");
        }

        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
    protected virtual DbTransaction CreateTransaction(
        ISqlTransaction transaction,
        IsolationLevel isolationLevel) => new ApexDbTransaction(transaction, this, isolationLevel);

    public override void Open() => throw ApexDbCommand.AsyncOnly();
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_state == ConnectionState.Open) return;
        var previous = _state;
        _state = ConnectionState.Connecting;
        OnStateChange(new StateChangeEventArgs(previous, _state));
        try
        {
            _connection = await OpenCoreAsync(cancellationToken).ConfigureAwait(false);
            previous = _state;
            _state = ConnectionState.Open;
            OnStateChange(new StateChangeEventArgs(previous, _state));
        }
        catch
        {
            previous = _state;
            _state = ConnectionState.Closed;
            OnStateChange(new StateChangeEventArgs(previous, _state));
            throw;
        }
    }
    public override void Close()
    {
        if (_connection is null) return;
        var connection = _connection;
        _connection = null;
        var previous = _state;
        _state = ConnectionState.Closed;
        OnStateChange(new StateChangeEventArgs(previous, _state));
        connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    public override async Task CloseAsync()
    {
        if (_connection is null) return;
        var connection = _connection;
        _connection = null;
        var previous = _state;
        _state = ConnectionState.Closed;
        OnStateChange(new StateChangeEventArgs(previous, _state));
        await connection.DisposeAsync().ConfigureAwait(false);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) Close();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
    protected override DbCommand CreateDbCommand() => CreateCommandCore();
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw ApexDbCommand.AsyncOnly();
    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default)
    {
        if (isolationLevel is not (IsolationLevel.Unspecified or IsolationLevel.ReadCommitted))
            throw new NotSupportedException($"Isolation level '{isolationLevel}' is not supported.");
        var transaction = await NativeConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        return CreateTransaction(transaction, isolationLevel);
    }
    public override void ChangeDatabase(string databaseName) =>
        throw new NotSupportedException("Changing databases on an open connection is not supported.");
}

/// <summary>Shared batch command implementation for asynchronous ADO.NET adapters.</summary>
public abstract class ApexDbBatchCommand : DbBatchCommand
{
    private readonly ApexDbParameterCollection _parameters;
    private int _recordsAffected = -1;
    protected ApexDbBatchCommand(ApexDbParameterCollection parameters) => _parameters = parameters;
    private string _commandText = string.Empty;
    private CommandType _commandType = CommandType.Text;
    public override string CommandText
    {
        get => _commandText;
        set
        {
            if (!string.Equals(_commandText, value, StringComparison.Ordinal))
            {
                _commandText = value;
                Version++;
            }
        }
    }
    public override CommandType CommandType
    {
        get => _commandType;
        set
        {
            if (_commandType != value)
            {
                _commandType = value;
                Version++;
            }
        }
    }
    public override int RecordsAffected => _recordsAffected;
    protected override DbParameterCollection DbParameterCollection => _parameters;
    public override bool CanCreateParameter => true;
    public override DbParameter CreateParameter() => CreateParameterCore();
    protected abstract ApexDbParameter CreateParameterCore();
    internal void SetRecordsAffected(int value) => _recordsAffected = value;
    internal int Version { get; private set; }
    internal int ParameterVersion => _parameters.Version;
}

/// <summary>Collection used by the asynchronous ADO.NET batch adapters.</summary>
public abstract class ApexDbBatchCommandCollection : DbBatchCommandCollection
{
    private readonly List<DbBatchCommand> _commands = [];
    public override int Count => _commands.Count;
    public override bool IsReadOnly => false;
    internal int Version { get; private set; }
    private void Changed() => Version++;
    public override void Add(DbBatchCommand item)
    {
        _commands.Add(item);
        Changed();
    }
    public override void Clear()
    {
        if (_commands.Count == 0) return;
        _commands.Clear();
        Changed();
    }
    public override bool Contains(DbBatchCommand item) => _commands.Contains(item);
    public override void CopyTo(DbBatchCommand[] array, int arrayIndex) => _commands.CopyTo(array, arrayIndex);
    public override IEnumerator<DbBatchCommand> GetEnumerator() => _commands.GetEnumerator();
    public override int IndexOf(DbBatchCommand item) => _commands.IndexOf(item);
    public override void Insert(int index, DbBatchCommand item)
    {
        _commands.Insert(index, item);
        Changed();
    }
    public override bool Remove(DbBatchCommand item)
    {
        var removed = _commands.Remove(item);
        if (removed) Changed();
        return removed;
    }
    public override void RemoveAt(int index)
    {
        _commands.RemoveAt(index);
        Changed();
    }
    protected override DbBatchCommand GetBatchCommand(int index) => _commands[index];
    protected override void SetBatchCommand(int index, DbBatchCommand batchCommand)
    {
        _commands[index] = batchCommand;
        Changed();
    }
}

internal sealed class ApexDbBatchDataReader : DbDataReader
{
    private readonly int _commandCount;
    private readonly CommandBehavior _behavior;
    private readonly DbConnection? _connection;
    private readonly Func<int, CancellationToken, Task<(
        ApexDbBatchCommand BatchCommand,
        ApexDbCommand Command,
        DbDataReader Reader)>> _create;
    private readonly Func<ApexDbCommand, ValueTask> _release;
    private readonly Func<ValueTask>? _onClose;
    private int _index;
    private ApexDbBatchCommand _batchCommand;
    private ApexDbCommand _command;
    private DbDataReader _reader;
    private bool _closed;
    private bool _allCommandsDrained;
    private bool _currentReleased;
    private int _recordsAffected = -1;

    private ApexDbBatchDataReader(
        int commandCount,
        CommandBehavior behavior,
        DbConnection? connection,
        Func<int, CancellationToken, Task<(
            ApexDbBatchCommand BatchCommand,
            ApexDbCommand Command,
            DbDataReader Reader)>> create,
        Func<ApexDbCommand, ValueTask> release,
        Func<ValueTask>? onClose,
        ApexDbBatchCommand batchCommand,
        ApexDbCommand command,
        DbDataReader reader)
    {
        _commandCount = commandCount;
        _behavior = behavior;
        _connection = connection;
        _create = create;
        _release = release;
        _onClose = onClose;
        _batchCommand = batchCommand;
        _command = command;
        _reader = reader;
    }

    public static async Task<DbDataReader> CreateAsync(
        int commandCount,
        CommandBehavior behavior,
        DbConnection? connection,
        Func<int, CancellationToken, Task<(
            ApexDbBatchCommand BatchCommand,
            ApexDbCommand Command,
            DbDataReader Reader)>> create,
        Func<ApexDbCommand, ValueTask> release,
        Func<ValueTask>? onClose,
        CancellationToken cancellationToken)
    {
        var (batchCommand, command, reader) =
            await create(0, cancellationToken).ConfigureAwait(false);
        return new ApexDbBatchDataReader(
            commandCount,
            behavior,
            connection,
            create,
            release,
            onClose,
            batchCommand,
            command,
            reader);
    }

    public override int Depth => _reader.Depth;
    public override int FieldCount => _reader.FieldCount;
    public override bool HasRows => _reader.HasRows;
    public override bool IsClosed => _closed || _reader.IsClosed;
    public override int RecordsAffected =>
        _currentReleased
            ? _recordsAffected
            : CombineRecordsAffected(_recordsAffected, _reader.RecordsAffected);
    public override object this[int ordinal] => _reader[ordinal];
    public override object this[string name] => _reader[name];
    public override bool GetBoolean(int ordinal) => _reader.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => _reader.GetByte(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        _reader.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    public override char GetChar(int ordinal) => _reader.GetChar(ordinal);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        _reader.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    public override string GetDataTypeName(int ordinal) => _reader.GetDataTypeName(ordinal);
    public override DateTime GetDateTime(int ordinal) => _reader.GetDateTime(ordinal);
    public override decimal GetDecimal(int ordinal) => _reader.GetDecimal(ordinal);
    public override double GetDouble(int ordinal) => _reader.GetDouble(ordinal);
#pragma warning disable IL2093
    public override Type GetFieldType(int ordinal) => _reader.GetFieldType(ordinal);
#pragma warning restore IL2093
    public override float GetFloat(int ordinal) => _reader.GetFloat(ordinal);
    public override Guid GetGuid(int ordinal) => _reader.GetGuid(ordinal);
    public override short GetInt16(int ordinal) => _reader.GetInt16(ordinal);
    public override int GetInt32(int ordinal) => _reader.GetInt32(ordinal);
    public override long GetInt64(int ordinal) => _reader.GetInt64(ordinal);
    public override string GetName(int ordinal) => _reader.GetName(ordinal);
    public override int GetOrdinal(string name) => _reader.GetOrdinal(name);
    public override string GetString(int ordinal) => _reader.GetString(ordinal);
    public override object GetValue(int ordinal) => _reader.GetValue(ordinal);
    public override int GetValues(object[] values) => _reader.GetValues(values);
    public override bool IsDBNull(int ordinal) => _reader.IsDBNull(ordinal);
    public override IEnumerator GetEnumerator() => throw ApexDbCommand.AsyncOnly();
    public override bool Read() => throw ApexDbCommand.AsyncOnly();
    public override bool NextResult() => throw ApexDbCommand.AsyncOnly();
    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => _reader.ReadAsync(cancellationToken);
    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) =>
        _reader.IsDBNullAsync(ordinal, cancellationToken);
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) =>
        _reader.GetFieldValueAsync<T>(ordinal, cancellationToken);

    public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        if (_closed) return false;
        if ((_behavior & CommandBehavior.SingleResult) != 0)
        {
            await DrainRemainingAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (await _reader.NextResultAsync(cancellationToken).ConfigureAwait(false)) return true;
        if (_index + 1 >= _commandCount)
        {
            _batchCommand.SetRecordsAffected(_reader.RecordsAffected);
            return false;
        }

        await ReleaseCurrentAsync(drain: false, cancellationToken).ConfigureAwait(false);
        _index++;
        var next = await _create(_index, cancellationToken).ConfigureAwait(false);
        _batchCommand = next.BatchCommand;
        _command = next.Command;
        _reader = next.Reader;
        _currentReleased = false;
        return true;
    }

    public override void Close() => CloseAsync().GetAwaiter().GetResult();
    public override async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            await DrainRemainingAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if ((_behavior & CommandBehavior.CloseConnection) != 0 && _connection is not null)
                {
                    await _connection.CloseAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (_onClose is not null)
                {
                    await _onClose().ConfigureAwait(false);
                }
            }
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) Close();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private async ValueTask DrainRemainingAsync(CancellationToken cancellationToken)
    {
        if (_allCommandsDrained) return;
        await ReleaseCurrentAsync(drain: true, cancellationToken).ConfigureAwait(false);
        while (_index + 1 < _commandCount)
        {
            _index++;
            var next = await _create(_index, cancellationToken).ConfigureAwait(false);
            _batchCommand = next.BatchCommand;
            _command = next.Command;
            _reader = next.Reader;
            _currentReleased = false;
            await ReleaseCurrentAsync(drain: true, cancellationToken).ConfigureAwait(false);
        }

        _allCommandsDrained = true;
    }

    private async ValueTask ReleaseCurrentAsync(bool drain, CancellationToken cancellationToken)
    {
        try
        {
            if (drain)
            {
                await DrainReaderAsync(_reader, cancellationToken).ConfigureAwait(false);
            }

            var current = _reader.RecordsAffected;
            _batchCommand.SetRecordsAffected(current);
            _recordsAffected = CombineRecordsAffected(_recordsAffected, current);
            _currentReleased = true;
        }
        finally
        {
            try
            {
                await _reader.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _release(_command).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask DrainReaderAsync(
        DbDataReader reader,
        CancellationToken cancellationToken)
    {
        do
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
            }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
    }

    private static int CombineRecordsAffected(int total, int current)
    {
        if (current < 0) return total;
        return total < 0 ? current : checked(total + current);
    }
}

/// <summary>Executes a sequence of parameterized commands without SQL concatenation.</summary>
public abstract class ApexDbBatch : DbBatch
{
    private readonly ApexDbBatchCommandCollection _commands;
    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private CancellationTokenSource? _activeCancellation;
    private List<ApexDbCommand>? _preparedCommands;
    private PreparedBatchState? _preparedState;

    protected ApexDbBatch(ApexDbBatchCommandCollection commands) => _commands = commands;
    protected override DbBatchCommandCollection DbBatchCommands => _commands;
    protected override DbConnection? DbConnection
    {
        get => _connection;
        set
        {
            if (!ReferenceEquals(_connection, value))
            {
                _preparedState = null;
                _connection = value;
            }
        }
    }
    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set
        {
            if (!ReferenceEquals(_transaction, value))
            {
                _preparedState = null;
                _transaction = value;
            }
        }
    }
    public override int Timeout { get; set; } = 30;
    protected abstract ApexDbBatchCommand CreateBatchCommandCore();
    protected abstract ApexDbCommand CreateCommandCore(ApexDbBatchCommand command, DbConnection connection);
    protected virtual void ValidateProviderConnection(DbConnection connection) { }
    protected virtual void ValidateProviderTransaction(DbTransaction transaction) { }
    protected override DbBatchCommand CreateDbBatchCommand() => CreateBatchCommandCore();
    public override void Cancel() => _activeCancellation?.Cancel();
    public override void Prepare() => throw ApexDbCommand.AsyncOnly();
    public override async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        await DisposePreparedCommandsAsync().ConfigureAwait(false);
        var prepared = new List<ApexDbCommand>(_commands.Count);
        try
        {
            foreach (ApexDbBatchCommand command in _commands)
            {
                var dbCommand = CreateCommand(command);
                prepared.Add(dbCommand);
                await dbCommand.PrepareAsync(cancellationToken).ConfigureAwait(false);
            }
            _preparedCommands = prepared;
            _preparedState = PreparedBatchState.Capture(_commands, _connection!, _transaction);
        }
        catch
        {
            foreach (var command in prepared)
            {
                await command.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }
    public override int ExecuteNonQuery() => throw ApexDbCommand.AsyncOnly();
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        var total = -1;
        _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Timeout > 0) _activeCancellation.CancelAfter(TimeSpan.FromSeconds(Timeout));
        var closeWhenDone = false;
        try
        {
            closeWhenDone = await OpenConnectionForExecutionAsync(_activeCancellation.Token)
                .ConfigureAwait(false);
            for (var i = 0; i < _commands.Count; i++)
            {
                var command = (ApexDbBatchCommand)_commands[i];
                var dbCommand = await CreateCommandForExecutionAsync(i, command)
                    .ConfigureAwait(false);
                try
                {
                    dbCommand.CommandTimeout = Timeout;
                    command.SetRecordsAffected(await dbCommand.ExecuteNonQueryAsync(_activeCancellation.Token)
                        .ConfigureAwait(false));
                    if (command.RecordsAffected >= 0)
                    {
                        total = total < 0
                            ? command.RecordsAffected
                            : checked(total + command.RecordsAffected);
                    }
                }
                finally
                {
                    await ReleaseExecutionCommandAsync(dbCommand).ConfigureAwait(false);
                }
            }
            return total;
        }
        finally
        {
            try
            {
                if (closeWhenDone)
                {
                    await _connection!.CloseAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _activeCancellation.Dispose();
                _activeCancellation = null;
            }
        }
    }
    public override object? ExecuteScalar() => throw ApexDbCommand.AsyncOnly();
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        if (_commands.Count == 0) return null;
        _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Timeout > 0) _activeCancellation.CancelAfter(TimeSpan.FromSeconds(Timeout));
        var closeWhenDone = false;
        try
        {
            closeWhenDone = await OpenConnectionForExecutionAsync(_activeCancellation.Token)
                .ConfigureAwait(false);
            object? scalar = null;
            var foundScalar = false;
            for (var i = 0; i < _commands.Count; i++)
            {
                var command = (ApexDbBatchCommand)_commands[i];
                var dbCommand = await CreateCommandForExecutionAsync(i, command)
                    .ConfigureAwait(false);
                DbDataReader? reader = null;
                try
                {
                    dbCommand.CommandTimeout = Timeout;
                    reader = await dbCommand.ExecuteReaderAsync(
                        CommandBehavior.Default,
                        _activeCancellation.Token).ConfigureAwait(false);
                    do
                    {
                        if (!foundScalar &&
                            reader.FieldCount > 0 &&
                            await reader.ReadAsync(_activeCancellation.Token).ConfigureAwait(false))
                        {
                            scalar = reader.GetValue(0);
                            foundScalar = true;
                        }
                    }
                    while (await reader.NextResultAsync(_activeCancellation.Token).ConfigureAwait(false));
                }
                finally
                {
                    if (reader is not null)
                    {
                        command.SetRecordsAffected(reader.RecordsAffected);
                        await reader.DisposeAsync().ConfigureAwait(false);
                    }

                    await ReleaseExecutionCommandAsync(dbCommand).ConfigureAwait(false);
                }
            }

            return scalar;
        }
        finally
        {
            try
            {
                if (closeWhenDone)
                {
                    await _connection!.CloseAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _activeCancellation.Dispose();
                _activeCancellation = null;
            }
        }
    }
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw ApexDbCommand.AsyncOnly();
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration();
        if (_commands.Count == 0) throw new InvalidOperationException("The batch is empty.");
        if ((behavior & ~(
                CommandBehavior.Default | CommandBehavior.SingleRow | CommandBehavior.SingleResult |
                CommandBehavior.SequentialAccess | CommandBehavior.CloseConnection)) != 0)
            throw new NotSupportedException($"CommandBehavior '{behavior}' is not supported.");

        _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var closeWhenDone = false;
        try
        {
            closeWhenDone = await OpenConnectionForExecutionAsync(_activeCancellation.Token)
                .ConfigureAwait(false);
            var result = await ApexDbBatchDataReader.CreateAsync(
                _commands.Count,
                behavior,
                _connection,
                async (index, token) =>
                {
                    var batchCommand = (ApexDbBatchCommand)_commands[index];
                    var command = await CreateCommandForExecutionAsync(index, batchCommand)
                        .ConfigureAwait(false);
                    command.CommandTimeout = Timeout;
                    try
                    {
                        var reader = await command.ExecuteReaderAsync(
                            behavior & ~(CommandBehavior.CloseConnection | CommandBehavior.SingleResult),
                            token).ConfigureAwait(false);
                        return (batchCommand, command, reader);
                    }
                    catch
                    {
                        await command.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                },
                ReleaseExecutionCommandAsync,
                async () =>
                {
                    try
                    {
                        if (closeWhenDone)
                        {
                            await _connection!.CloseAsync().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        CompleteReaderOperation();
                    }
                },
                _activeCancellation.Token).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try
            {
                if (closeWhenDone)
                {
                    await _connection!.CloseAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                CompleteReaderOperation();
            }
            throw;
        }
    }
    private ApexDbCommand CreateCommand(ApexDbBatchCommand command)
    {
        if (_connection is null) throw new InvalidOperationException("The batch connection is required.");
        if (command.CommandType != CommandType.Text) throw new NotSupportedException("Only CommandType.Text is supported.");
        var result = CreateCommandCore(command, _connection);
        result.Transaction = _transaction;
        return result;
    }

    private async ValueTask<ApexDbCommand> CreateCommandForExecutionAsync(
        int index,
        ApexDbBatchCommand command)
    {
        if (_preparedCommands is { } prepared && _preparedState?.Matches(_commands, _connection, _transaction) == true)
        {
            return prepared[index];
        }

        if (_preparedCommands is not null)
        {
            await DisposePreparedCommandsAsync().ConfigureAwait(false);
        }

        return CreateCommand(command);
    }

    private ValueTask ReleaseExecutionCommandAsync(ApexDbCommand command) =>
        _preparedCommands is { } prepared && prepared.Contains(command)
            ? ValueTask.CompletedTask
            : command.DisposeAsync();

    private async ValueTask DisposePreparedCommandsAsync()
    {
        if (_preparedCommands is not { } prepared) return;
        _preparedCommands = null;
        _preparedState = null;
        foreach (var command in prepared)
        {
            await command.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void DisposePreparedCommandsSynchronously()
    {
        if (_preparedCommands is not { } prepared) return;
        _preparedCommands = null;
        _preparedState = null;
        foreach (var command in prepared)
        {
            command.Dispose();
        }
    }

    private void ValidateConfiguration()
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("The batch connection is required.");
        }

        ValidateProviderConnection(_connection);
        if (_transaction is null) return;
        ValidateProviderTransaction(_transaction);
        if (_transaction is not ApexDbTransaction transaction ||
            !ReferenceEquals(transaction.Connection, _connection))
        {
            throw new InvalidOperationException("The transaction belongs to a different connection.");
        }
    }

    private ValueTask<bool> OpenConnectionForExecutionAsync(CancellationToken cancellationToken) =>
        _connection is ApexDbConnection connection
            ? connection.OpenForCommandAsync(cancellationToken)
            : ValueTask.FromResult(false);

    private void CompleteReaderOperation()
    {
        var cancellation = Interlocked.Exchange(ref _activeCancellation, null);
        cancellation?.Dispose();
    }

    private sealed class PreparedBatchState
    {
        private readonly DbConnection _connection;
        private readonly DbTransaction? _transaction;
        private readonly int _collectionVersion;
        private readonly ApexDbBatchCommand[] _commands;
        private readonly int[] _commandVersions;
        private readonly int[] _parameterVersions;

        private PreparedBatchState(
            ApexDbBatchCommandCollection collection,
            DbConnection connection,
            DbTransaction? transaction)
        {
            _connection = connection;
            _transaction = transaction;
            _collectionVersion = collection.Version;
            _commands = new ApexDbBatchCommand[collection.Count];
            _commandVersions = new int[collection.Count];
            _parameterVersions = new int[collection.Count];
            for (var i = 0; i < collection.Count; i++)
            {
                var command = (ApexDbBatchCommand)collection[i];
                _commands[i] = command;
                _commandVersions[i] = command.Version;
                _parameterVersions[i] = command.ParameterVersion;
            }
        }

        public static PreparedBatchState Capture(
            ApexDbBatchCommandCollection collection,
            DbConnection connection,
            DbTransaction? transaction) =>
            new(collection, connection, transaction);

        public bool Matches(
            ApexDbBatchCommandCollection collection,
            DbConnection? connection,
            DbTransaction? transaction)
        {
            if (!ReferenceEquals(_connection, connection) ||
                !ReferenceEquals(_transaction, transaction) ||
                _collectionVersion != collection.Version ||
                _commands.Length != collection.Count)
            {
                return false;
            }

            for (var i = 0; i < _commands.Length; i++)
            {
                var command = (ApexDbBatchCommand)collection[i];
                if (!ReferenceEquals(_commands[i], command) ||
                    _commandVersions[i] != command.Version ||
                    _parameterVersions[i] != command.ParameterVersion)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await DisposePreparedCommandsAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    public override void Dispose()
    {
        DisposePreparedCommandsSynchronously();
        base.Dispose();
    }
}

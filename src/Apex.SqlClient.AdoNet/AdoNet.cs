using System.Collections;
using System.Collections.ObjectModel;
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

        var value = Value;
        if (value is null or DBNull) return SqlValue.Null;
        return _dbTypeWasSet
            ? ApexDbTypeConverter.ToSqlValue(_dbType, value, ParameterName)
            : SqlValue.From(value);
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
    public override bool Contains(object value) => value is DbParameter parameter && _parameters.Contains(parameter);
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)_parameters).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();
    protected override DbParameter GetParameter(int index) => _parameters[index];
    protected override DbParameter GetParameter(string parameterName) =>
        _parameters[IndexOf(parameterName)];
    public override int IndexOf(object value) => value is DbParameter parameter ? _parameters.IndexOf(parameter) : -1;
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
    private ApexOperationCancellation? _activeCancellation;
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
    internal virtual DbException TranslateException(SqlClientException exception) =>
        _connection is ApexDbConnection connection
            ? connection.TranslateException(exception)
            : new ApexDbException(exception);

    public override void Cancel() => Volatile.Read(ref _activeCancellation)?.Cancel();
    public override void Prepare() => throw AsyncOnly();
    public override Task PrepareAsync(CancellationToken cancellationToken = default) =>
        ApexExceptionBoundary.RunAsync(() => PrepareCoreAsync(cancellationToken), TranslateException);
    private async Task PrepareCoreAsync(CancellationToken cancellationToken)
    {
        Validate();
        await DisposePreparedAsync().ConfigureAwait(false);
        var cancellation = BeginOperation(cancellationToken);
        try
        {
            var execution = await OpenForExecutionAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                _preparedStatement = await execution.Connection
                    .PrepareAsync(CommandText!, cancellation.Token).ConfigureAwait(false);
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
        }
        catch (Exception exception) when (cancellation.TranslateTimeout(exception, cancellationToken) is { } timeout)
        {
            throw timeout;
        }
        finally
        {
            CompleteOperation(cancellation);
        }
    }

    protected override DbParameter CreateDbParameter() => CreateParameterCore();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw AsyncOnly();
    public override int ExecuteNonQuery() => throw AsyncOnly();
    public override object? ExecuteScalar() => throw AsyncOnly();

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken) =>
        ApexExceptionBoundary.RunAsync(() => ExecuteReaderCoreAsync(behavior, cancellationToken), TranslateException);

    private async Task<DbDataReader> ExecuteReaderCoreAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        await DisposeInvalidatedPreparedAsync().ConfigureAwait(false);
        Validate();
        ValidateBehavior(behavior);
        var cancellation = BeginOperation(cancellationToken);
        ExecutionConnection execution = default;
        ISqlRowReader? nativeReader = null;
        var transferred = false;
        try
        {
            var parameters = _parameters.ToSqlParameters();
            execution = await OpenForExecutionAsync(cancellation.Token).ConfigureAwait(false);
            var prepared = GetPreparedStatement(execution.Connection);
            nativeReader = _adoReaderFactory is not null
                ? await _adoReaderFactory.ExecuteReaderAsync(
                    execution.Connection,
                    CommandText!,
                    parameters,
                    prepared,
                    cancellation.Token).ConfigureAwait(false)
                : prepared is not null
                    ? await prepared.ExecuteReaderAsync(parameters, cancellation.Token).ConfigureAwait(false)
                    : await execution.Connection.ExecuteReaderAsync(
                        CommandText!,
                        parameters,
                        cancellation.Token).ConfigureAwait(false);
            var result = CreateReader(
                nativeReader,
                behavior,
                execution.ConnectionOwner,
                cancellation.Source,
                () => CompleteReaderAsync(cancellation, execution, behavior));
            result.ConfigureCancellation(cancellation);
            await result.InitializeAsync(cancellation.Token).ConfigureAwait(false);
            transferred = true;
            return result;
        }
        catch (Exception exception) when (cancellation.TranslateTimeout(exception, cancellationToken) is { } timeout)
        {
            throw timeout;
        }
        finally
        {
            if (!transferred)
            {
                ApexCancellation.TryCancel(cancellation.Source);
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
                        CompleteOperation(cancellation);
                    }
                }
            }
        }
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
        ApexExceptionBoundary.RunAsync(() => ExecuteNonQueryCoreAsync(cancellationToken), TranslateException);

    private async Task<int> ExecuteNonQueryCoreAsync(CancellationToken cancellationToken)
    {
        // Providers whose native command result is equivalent to draining a result reader (a single
        // statement whose affected-row count the provider can classify) skip the reader machinery.
        if (_adoReaderFactory is null || _adoReaderFactory.CanExecuteNonQuery(CommandText, _parameters.Count))
        {
            return await ExecuteNativeNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<int> ExecuteNativeNonQueryAsync(CancellationToken cancellationToken)
    {
        await DisposeInvalidatedPreparedAsync().ConfigureAwait(false);
        Validate();
        var cancellation = BeginOperation(cancellationToken);
        ExecutionConnection execution = default;
        try
        {
            var parameters = _parameters.ToSqlParameters();
            execution = await OpenForExecutionAsync(cancellation.Token).ConfigureAwait(false);
            var prepared = GetPreparedStatement(execution.Connection);
            var result = prepared is not null
                ? await prepared.ExecuteAsync(parameters, cancellation.Token).ConfigureAwait(false)
                : await execution.Connection.ExecuteAsync(
                    CommandText!,
                    parameters,
                    cancellation.Token).ConfigureAwait(false);
            return _adoReaderFactory?.GetRecordsAffected(result) ?? checked((int)result.AffectedRows);
        }
        catch (Exception exception) when (cancellation.TranslateTimeout(exception, cancellationToken) is { } timeout)
        {
            throw timeout;
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
                CompleteOperation(cancellation);
            }
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

    private ApexOperationCancellation BeginOperation(CancellationToken cancellationToken)
    {
        var cancellation = new ApexOperationCancellation(cancellationToken, CommandTimeout);
        cancellation.Arm();
        Volatile.Write(ref _activeCancellation, cancellation);
        return cancellation;
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

    private ISqlPreparedStatement? GetPreparedStatement(ISqlConnection connection) =>
        _preparedStatement is not null && ReferenceEquals(connection, _preparedConnection)
            ? _preparedStatement
            : null;

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
                // The connection was closed and reopened since PrepareAsync, so the statement's native
                // connection is gone. Release it (a pooled lease stays pinned until then) and execute
                // unprepared, as other providers do; PrepareAsync prepares it on the new connection.
                await DisposePreparedAsync().ConfigureAwait(false);
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
        ApexOperationCancellation cancellation,
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

    private void CompleteOperation(ApexOperationCancellation cancellation)
    {
        Interlocked.CompareExchange(ref _activeCancellation, null, cancellation);
        cancellation.Dispose();
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

    // A prepared statement is stale once its owner was closed (and possibly reopened on another native
    // connection): it can no longer be closed on the server, and closing the owner would close the
    // caller's new connection.
    private bool PreparedIsStale() =>
        _preparedConnectionOwner is not { } owner ||
        !ReferenceEquals(owner.CurrentConnection, _preparedConnection);

    private void DisposePreparedSynchronously()
    {
        if (_preparedStatement is null) return;
        var statement = _preparedStatement;
        var stale = PreparedIsStale();
        _preparedStatement = null;
        _preparedConnection = null;
        var connectionOwner = _preparedConnectionOwner;
        _preparedConnectionOwner = null;
        try
        {
            ApexExceptionBoundary.RunValueAsync(statement.DisposeAsync, TranslateException)
                .AsTask().GetAwaiter().GetResult();
        }
        catch (Exception) when (stale)
        {
            // The statement's connection is gone; there is nothing left to release on the server.
        }
        finally
        {
            if (!stale && _preparedConnectionWasAutoOpened && connectionOwner is not null)
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
        var stale = PreparedIsStale();
        _preparedStatement = null;
        _preparedConnection = null;
        var connectionOwner = _preparedConnectionOwner;
        _preparedConnectionOwner = null;
        try
        {
            await ApexExceptionBoundary.RunValueAsync(statement.DisposeAsync, TranslateException).ConfigureAwait(false);
        }
        catch (Exception) when (stale)
        {
            // The statement's connection is gone; there is nothing left to release on the server.
        }
        finally
        {
            if (!stale && _preparedConnectionWasAutoOpened && connectionOwner is not null)
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
public class ApexDbDataReader : DbDataReader, IDbColumnSchemaGenerator
{
    private ISqlRowReader _reader;
    private readonly CommandBehavior _behavior;
    private readonly DbConnection? _connection;
    private readonly CancellationTokenSource? _operationCancellation;
    private readonly Func<ValueTask>? _onClose;
    private ApexOperationCancellation? _cancellation;
    private bool _closed;
    private bool _returnedSingleRow;
    private IReadOnlyList<SqlColumn> _columns;
    private bool _hasRowsInResult;
    private bool _prefetchedRow;
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
        ApexExceptionBoundary.Invoke(
            () => _reader is ISqlRecordsAffectedReader recordsAffected ? recordsAffected.RecordsAffected : -1,
            TranslateException);
    internal virtual DbException TranslateException(SqlClientException exception) =>
        _connection is ApexDbConnection connection
            ? connection.TranslateException(exception)
            : new ApexDbException(exception);
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
    /// <summary>Gets the CLR type that <see cref="GetValue"/> returns for the column's provider type.</summary>
    public override Type GetFieldType(int ordinal) => GetColumnFieldType(_columns[ordinal]);
#pragma warning restore IL2093
    public override string GetDataTypeName(int ordinal) => GetColumnDataTypeName(_columns[ordinal]);

    /// <summary>
    /// Maps a provider column to the CLR type its <see cref="GetValue"/> returns. Readers without a provider
    /// mapping report <see cref="object"/>.
    /// </summary>
    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
    internal virtual Type GetColumnFieldType(SqlColumn column) => typeof(object);

    /// <summary>Maps a provider column to its provider type name; without a mapping, the numeric type id.</summary>
    internal virtual string GetColumnDataTypeName(SqlColumn column) =>
        column.TypeId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Describes the current result's columns from the protocol metadata: name, ordinal, CLR type and provider
    /// type name. Base table, key, nullability and size details are not reported.
    /// </summary>
    public ReadOnlyCollection<DbColumn> GetColumnSchema()
    {
        var columns = new DbColumn[_columns.Count];
        for (var i = 0; i < columns.Length; i++)
        {
            var column = _columns[i];
            columns[i] = new ApexDbColumn(column.Name, i, GetColumnFieldType(column), GetColumnDataTypeName(column));
        }

        return new ReadOnlyCollection<DbColumn>(columns);
    }

    /// <summary>Returns <see cref="GetColumnSchema"/> as a schema table with the standard column set.</summary>
    public override DataTable GetSchemaTable() => ApexSchemaTable.Create(GetColumnSchema());
    public override bool GetBoolean(int ordinal) =>
        ApexExceptionBoundary.Invoke(() => _reader.GetBoolean(ordinal), TranslateException);
    public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);
    public override char GetChar(int ordinal) => GetFieldValue<char>(ordinal);
    public override DateTime GetDateTime(int ordinal) =>
        ApexExceptionBoundary.Invoke(() => _reader.GetDateTime(ordinal), TranslateException);
    public override decimal GetDecimal(int ordinal) =>
        ApexExceptionBoundary.Invoke(() => _reader.GetDecimal(ordinal), TranslateException);
    public override double GetDouble(int ordinal) =>
        ApexExceptionBoundary.Invoke(() => _reader.GetDouble(ordinal), TranslateException);
    public override float GetFloat(int ordinal) =>
        ApexExceptionBoundary.Invoke(() => _reader.GetFloat(ordinal), TranslateException);
    public override Guid GetGuid(int ordinal) =>
        ApexExceptionBoundary.Invoke(() => _reader.GetGuid(ordinal), TranslateException);
    public override short GetInt16(int ordinal) =>
        ApexExceptionBoundary.Invoke(() => _reader.GetInt16(ordinal), TranslateException);
    public override int GetInt32(int ordinal) =>
        ApexExceptionBoundary.Invoke(() => _reader.GetInt32(ordinal), TranslateException);
    public override long GetInt64(int ordinal) =>
        ApexExceptionBoundary.Invoke(() => _reader.GetInt64(ordinal), TranslateException);
    public override string GetString(int ordinal) =>
        ApexExceptionBoundary.Invoke(() => _reader.GetString(ordinal), TranslateException);
    public override bool IsDBNull(int ordinal) =>
        ApexExceptionBoundary.Invoke(() => _reader.IsNull(ordinal), TranslateException);
    public override object GetValue(int ordinal) => GetFieldValue<object>(ordinal) ?? DBNull.Value;
    public override T GetFieldValue<T>(int ordinal) =>
        ApexExceptionBoundary.Invoke(() => _reader.Get<T>(ordinal), TranslateException);
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
            _prefetchedRow = false;
            _returnedSingleRow = true;
            return Task.FromResult(true);
        }

        return ApexExceptionBoundary.RunAsync(() => ReadCoreAsync(cancellationToken), TranslateException);
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
        catch (Exception exception) when (TranslateTimeout(exception, cancellationToken) is { } timeout)
        {
            throw timeout;
        }
        finally
        {
            DisarmTimeout();
        }
    }
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) =>
        ApexExceptionBoundary.RunAsync(() => NextResultCoreAsync(cancellationToken), TranslateException);

    private async Task<bool> NextResultCoreAsync(CancellationToken cancellationToken)
    {
        if ((_behavior & CommandBehavior.SingleResult) != 0) return false;
        if (_closed) throw new InvalidOperationException("The reader is closed.");
        if (_reader is not ISqlMultiResultReader multiResultReader)
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
            if (_reader is ISqlResultBoundaryReader bounded)
            {
                _hasRowsInResult = await bounded.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _prefetchedRow = _hasRowsInResult;
            }
            else
            {
                _prefetchedRow = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                _hasRowsInResult = _prefetchedRow;
            }
            ClearValueCaches();
            return true;
        }
        catch (Exception exception) when (TranslateTimeout(exception, cancellationToken) is { } timeout)
        {
            throw timeout;
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
            _prefetchedRow = false;
        }

        while (await _reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
        }
    }
    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) =>
        Task.FromResult(IsDBNull(ordinal));
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) =>
        Task.FromResult(GetFieldValue<T>(ordinal));
    public override void Close() => CloseAsync().GetAwaiter().GetResult();
    public override Task CloseAsync() =>
        ApexExceptionBoundary.RunAsync(CloseCoreAsync, TranslateException);
    private async Task CloseCoreAsync()
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

    protected ValueTask ReplaceReaderAsync(ISqlRowReader reader) =>
        ApexExceptionBoundary.RunValueAsync(() => ReplaceReaderCoreAsync(reader), TranslateException);

    private async ValueTask ReplaceReaderCoreAsync(ISqlRowReader reader)
    {
        await _reader.DisposeAsync().ConfigureAwait(false);
        _reader = reader;
        _columns = reader.Columns;
        _returnedSingleRow = false;
        _hasRowsInResult = false;
        _prefetchedRow = false;
        ClearValueCaches();
    }

    internal ValueTask InitializeAsync(CancellationToken cancellationToken) =>
        ApexExceptionBoundary.RunValueAsync(() => InitializeCoreAsync(cancellationToken), TranslateException);

    private async ValueTask InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_reader is ISqlResultBoundaryReader bounded)
            {
                _hasRowsInResult = await bounded.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _columns = _reader.Columns;
                _prefetchedRow = _hasRowsInResult;
                return;
            }

            _prefetchedRow = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            _hasRowsInResult = _prefetchedRow;
            _columns = _reader.Columns;
        }
        catch (Exception exception) when (TranslateTimeout(exception, cancellationToken) is { } timeout)
        {
            throw timeout;
        }
        finally
        {
            DisarmTimeout();
        }
    }

    internal void ConfigureCancellation(ApexOperationCancellation cancellation) => _cancellation = cancellation;

    private void ArmTimeout() => _cancellation?.Arm();

    private void DisarmTimeout() => _cancellation?.Disarm();

    // The native reader surfaces a timeout as cancellation of the operation token (or as the server's
    // error for the cancel it triggered); report it as a provider timeout instead.
    private ApexDbException? TranslateTimeout(Exception exception, CancellationToken cancellationToken) =>
        _cancellation?.TranslateTimeout(exception, cancellationToken);

    private byte[] GetCachedBytes(int ordinal)
    {
        EnsureValueCacheCapacity();
        return _bytes![ordinal] ??= ApexExceptionBoundary.Invoke(() => _reader.GetBytes(ordinal), TranslateException);
    }

    private string GetCachedChars(int ordinal)
    {
        EnsureValueCacheCapacity();
        return _chars![ordinal] ??= GetString(ordinal);
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

internal static class ApexCancellation
{
    // DbCommand.Cancel must be a no-op when nothing is running; the operation may complete and
    // dispose its source concurrently with a Cancel call from another thread.
    internal static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

internal interface IApexAdoReaderFactory
{
    ValueTask<ISqlRowReader> ExecuteReaderAsync(
        ISqlConnection connection,
        string sql,
        SqlParameters parameters,
        ISqlPreparedStatement? preparedStatement,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns whether the native <c>ExecuteAsync</c> result is equivalent to draining a result reader for this
    /// command text, so ExecuteNonQueryAsync can skip the reader and its result-boundary state.
    /// </summary>
    bool CanExecuteNonQuery(string sql, int parameterCount);

    /// <summary>Converts a native command result to <see cref="DbDataReader.RecordsAffected"/> semantics.</summary>
    int GetRecordsAffected(SqlCommandResult result);
}

/// <summary>Executes commands through the native result-preserving reader capabilities.</summary>
internal class ApexResultReaderFactory(string providerName) : IApexAdoReaderFactory
{
    public virtual bool CanExecuteNonQuery(string sql, int parameterCount) => false;

    public virtual int GetRecordsAffected(SqlCommandResult result) => checked((int)result.AffectedRows);

    public ValueTask<ISqlRowReader> ExecuteReaderAsync(
        ISqlConnection connection,
        string sql,
        SqlParameters parameters,
        ISqlPreparedStatement? preparedStatement,
        CancellationToken cancellationToken)
    {
        if (connection is not ISqlResultReaderConnection resultConnection)
        {
            throw new ArgumentException(
                $"The command connection does not support {providerName} ADO.NET readers.",
                nameof(connection));
        }

        return preparedStatement switch
        {
            null => resultConnection.ExecuteResultReaderAsync(sql, parameters, cancellationToken),
            ISqlResultPreparedStatement statement => statement.ExecuteResultReaderAsync(parameters, cancellationToken),
            _ => throw new ArgumentException(
                $"The prepared statement must be created by the {providerName} provider.",
                nameof(preparedStatement)),
        };
    }
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
        ApexExceptionBoundary.RunValueAsync(() => _transaction.CommitAsync(cancellationToken), TranslateException).AsTask();
    public override Task RollbackAsync(CancellationToken cancellationToken = default) =>
        ApexExceptionBoundary.RunValueAsync(() => _transaction.RollbackAsync(cancellationToken), TranslateException).AsTask();
    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
    public override ValueTask DisposeAsync() =>
        ApexExceptionBoundary.RunValueAsync(_transaction.DisposeAsync, TranslateException);
    private DbException TranslateException(SqlClientException exception) =>
        _connection is ApexDbConnection connection
            ? connection.TranslateException(exception)
            : new ApexDbException(exception);
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
    internal ISqlConnection? CurrentConnection => _connection;

    protected abstract Task<ISqlConnection> OpenCoreAsync(CancellationToken cancellationToken);
    internal virtual DbException TranslateException(SqlClientException exception) => new ApexDbException(exception);
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
    public override Task OpenAsync(CancellationToken cancellationToken) =>
        ApexExceptionBoundary.RunAsync(() => OpenConnectionAsync(cancellationToken), TranslateException);

    private async Task OpenConnectionAsync(CancellationToken cancellationToken)
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
    public override void Close() => CloseAsync().GetAwaiter().GetResult();
    public override Task CloseAsync() =>
        ApexExceptionBoundary.RunAsync(CloseConnectionAsync, TranslateException);

    private async Task CloseConnectionAsync()
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
        var transaction = await ApexExceptionBoundary.RunValueAsync(
            () => NativeConnection.BeginTransactionAsync(cancellationToken), TranslateException).ConfigureAwait(false);
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

internal sealed class ApexDbBatchDataReader : DbDataReader, IDbColumnSchemaGenerator
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
    public override DataTable? GetSchemaTable() => _reader.GetSchemaTable();
    public ReadOnlyCollection<DbColumn> GetColumnSchema() => _reader.GetColumnSchema();
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
        (ApexDbBatchCommand BatchCommand, ApexDbCommand Command, DbDataReader Reader) next;
        try
        {
            next = await _create(_index, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The batch stops at the failed command; closing must not re-drain the released
            // reader or execute the commands that follow it.
            _allCommandsDrained = true;
            throw;
        }

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
        if (_currentReleased) return;
        try
        {
            if (drain)
            {
                await DrainReaderAsync(_reader, cancellationToken).ConfigureAwait(false);
            }

            var current = _reader.RecordsAffected;
            _batchCommand.SetRecordsAffected(current);
            _recordsAffected = CombineRecordsAffected(_recordsAffected, current);
        }
        finally
        {
            _currentReleased = true;
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
    private ApexDbCommand? _executingCommand;
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
    /// <summary>
    /// Gets or sets the timeout, in seconds, of each command in the batch: like
    /// <see cref="DbCommand.CommandTimeout"/>, it bounds each command's execution and each subsequent
    /// read or next-result call, for every execution method. It is not a deadline for the whole batch.
    /// Zero disables it.
    /// </summary>
    public override int Timeout { get; set; } = 30;
    protected abstract ApexDbBatchCommand CreateBatchCommandCore();
    protected abstract ApexDbCommand CreateCommandCore(ApexDbBatchCommand command, DbConnection connection);
    protected virtual void ValidateProviderConnection(DbConnection connection) { }
    protected virtual void ValidateProviderTransaction(DbTransaction transaction) { }
    protected override DbBatchCommand CreateDbBatchCommand() => CreateBatchCommandCore();
    public override void Cancel()
    {
        ApexCancellation.TryCancel(_activeCancellation);
        // Reader commands after the first run under the NextResultAsync caller's token, so the
        // batch also cancels whichever command is currently executing.
        Volatile.Read(ref _executingCommand)?.Cancel();
    }
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
                    Volatile.Write(ref _executingCommand, command);
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
        Volatile.Write(ref _executingCommand, null);
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

/// <summary>Converts an explicitly typed parameter value to the native value the <see cref="DbType"/> implies.</summary>
internal static class ApexDbTypeConverter
{
    /// <summary>
    /// Honors the <see cref="DbType"/> where a native <see cref="SqlValue"/> kind represents it, converting
    /// compatible values (integers and enums to the declared integer width, numbers to the declared
    /// floating-point or decimal type, characters to strings, date/time variants to the declared kind).
    /// DbTypes without a distinct native kind (unsigned and 8-bit integers, <see cref="DbType.Object"/>)
    /// keep the inferred value, and string and binary variants share the native string and binary kinds.
    /// Values the declared type cannot represent are rejected.
    /// </summary>
    internal static SqlValue ToSqlValue(DbType dbType, object value, string? parameterName)
    {
        if (value is SqlValue sqlValue) return sqlValue;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        switch (dbType)
        {
            case DbType.Object:
                return SqlValue.From(value);
            case DbType.Boolean when value is bool:
                return SqlValue.From(value);
            case DbType.Byte when IsInteger(value):
                return SqlValue.From(Convert.ToByte(value, culture));
            case DbType.SByte when IsInteger(value):
                return SqlValue.From(Convert.ToSByte(value, culture));
            case DbType.Int16 when IsInteger(value):
                return Convert.ToInt16(value, culture);
            case DbType.Int32 when IsInteger(value):
                return Convert.ToInt32(value, culture);
            case DbType.Int64 when IsInteger(value):
                return Convert.ToInt64(value, culture);
            case DbType.UInt16 when IsInteger(value):
                return SqlValue.From(Convert.ToUInt16(value, culture));
            case DbType.UInt32 when IsInteger(value):
                return SqlValue.From(Convert.ToUInt32(value, culture));
            case DbType.UInt64 when IsInteger(value):
                return SqlValue.From(Convert.ToUInt64(value, culture));
            case DbType.Single when IsNumber(value):
                return Convert.ToSingle(value, culture);
            case DbType.Double when IsNumber(value):
                return Convert.ToDouble(value, culture);
            case DbType.Decimal or DbType.Currency or DbType.VarNumeric when IsNumber(value):
                return Convert.ToDecimal(value, culture);
            case DbType.String or DbType.StringFixedLength or DbType.AnsiString or
                DbType.AnsiStringFixedLength or DbType.Xml:
                switch (value)
                {
                    case string:
                        return SqlValue.From(value);
                    case char character:
                        return character.ToString();
                    case char[] characters:
                        return new string(characters);
                }

                break;
            case DbType.Binary:
                switch (value)
                {
                    case byte[] or ReadOnlyMemory<byte>:
                        return SqlValue.From(value);
                    case Memory<byte> memory:
                        return (ReadOnlyMemory<byte>)memory;
                    case ArraySegment<byte> segment:
                        return (ReadOnlyMemory<byte>)segment;
                }

                break;
            case DbType.Guid when value is Guid:
                return SqlValue.From(value);
            case DbType.Date:
                switch (value)
                {
                    case DateOnly:
                        return SqlValue.From(value);
                    case DateTime dateTime:
                        return DateOnly.FromDateTime(dateTime);
                }

                break;
            case DbType.Time when value is TimeOnly or TimeSpan:
                // TimeSpan keeps its native mapping (a duration or time of day, depending on the provider).
                return SqlValue.From(value);
            case DbType.DateTime or DbType.DateTime2:
                switch (value)
                {
                    case DateTime:
                        return SqlValue.From(value);
                    case DateOnly date:
                        return date.ToDateTime(TimeOnly.MinValue);
                }

                break;
            case DbType.DateTimeOffset:
                switch (value)
                {
                    case DateTimeOffset:
                        return SqlValue.From(value);
                    case DateTime { Kind: DateTimeKind.Utc } utc:
                        return new DateTimeOffset(utc);
                }

                break;
        }

        throw new InvalidCastException(
            $"Parameter '{parameterName}' has a value of type '{value.GetType().FullName}', " +
            $"which cannot be sent as DbType.{dbType}.");
    }

    private static bool IsInteger(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or Enum;

    private static bool IsNumber(object value) =>
        IsInteger(value) || value is float or double or decimal;
}

/// <summary>Column metadata reported by <see cref="ApexDbDataReader.GetColumnSchema"/>.</summary>
internal sealed class ApexDbColumn : DbColumn
{
    internal ApexDbColumn(string name, int ordinal, Type dataType, string dataTypeName)
    {
        ColumnName = name;
        ColumnOrdinal = ordinal;
        DataType = dataType;
        DataTypeName = dataTypeName;
    }
}

internal static class ApexSchemaTable
{
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2111",
        Justification = "The DataType column only stores Type instances; nothing reflects over System.Type's members.")]
    internal static DataTable Create(ReadOnlyCollection<DbColumn> columns)
    {
        DataTable table = new("SchemaTable") { Locale = System.Globalization.CultureInfo.InvariantCulture };
        var name = table.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        var ordinal = table.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        var size = table.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
        var precision = table.Columns.Add(SchemaTableColumn.NumericPrecision, typeof(int));
        var scale = table.Columns.Add(SchemaTableColumn.NumericScale, typeof(int));
        var dataType = table.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        var dataTypeName = table.Columns.Add("DataTypeName", typeof(string));
        var allowDbNull = table.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));
        var isKey = table.Columns.Add(SchemaTableColumn.IsKey, typeof(bool));
        var isUnique = table.Columns.Add(SchemaTableColumn.IsUnique, typeof(bool));
        var isLong = table.Columns.Add(SchemaTableColumn.IsLong, typeof(bool));
        var isAliased = table.Columns.Add(SchemaTableColumn.IsAliased, typeof(bool));
        var isExpression = table.Columns.Add(SchemaTableColumn.IsExpression, typeof(bool));
        var baseColumnName = table.Columns.Add(SchemaTableColumn.BaseColumnName, typeof(string));
        var baseTableName = table.Columns.Add(SchemaTableColumn.BaseTableName, typeof(string));
        var baseSchemaName = table.Columns.Add(SchemaTableColumn.BaseSchemaName, typeof(string));
        var isReadOnly = table.Columns.Add(SchemaTableOptionalColumn.IsReadOnly, typeof(bool));
        var isAutoIncrement = table.Columns.Add(SchemaTableOptionalColumn.IsAutoIncrement, typeof(bool));
        foreach (var column in columns)
        {
            var row = table.NewRow();
            row[name] = column.ColumnName;
            row[ordinal] = column.ColumnOrdinal ?? -1;
            row[size] = (object?)column.ColumnSize ?? DBNull.Value;
            row[precision] = (object?)column.NumericPrecision ?? DBNull.Value;
            row[scale] = (object?)column.NumericScale ?? DBNull.Value;
            row[dataType] = (object?)column.DataType ?? DBNull.Value;
            row[dataTypeName] = (object?)column.DataTypeName ?? DBNull.Value;
            row[allowDbNull] = (object?)column.AllowDBNull ?? DBNull.Value;
            row[isKey] = (object?)column.IsKey ?? DBNull.Value;
            row[isUnique] = (object?)column.IsUnique ?? DBNull.Value;
            row[isLong] = (object?)column.IsLong ?? DBNull.Value;
            row[isAliased] = (object?)column.IsAliased ?? DBNull.Value;
            row[isExpression] = (object?)column.IsExpression ?? DBNull.Value;
            row[baseColumnName] = (object?)column.BaseColumnName ?? DBNull.Value;
            row[baseTableName] = (object?)column.BaseTableName ?? DBNull.Value;
            row[baseSchemaName] = (object?)column.BaseSchemaName ?? DBNull.Value;
            row[isReadOnly] = (object?)column.IsReadOnly ?? DBNull.Value;
            row[isAutoIncrement] = (object?)column.IsAutoIncrement ?? DBNull.Value;
            table.Rows.Add(row);
        }

        return table;
    }
}

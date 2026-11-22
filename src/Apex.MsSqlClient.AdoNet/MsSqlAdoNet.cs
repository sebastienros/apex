using System.Data;
using System.Data.Common;
using Apex.SqlClient;

namespace Apex.MsSqlClient;

/// <summary>Asynchronous-only ADO.NET connection adapter for SQL Server.</summary>
public sealed class MsSqlDbConnection : ApexDbConnection
{
    internal override DbException TranslateException(SqlClientException exception) => MsSqlAdoErrors.Translate(exception);
    private MsSqlConnectOptions _options;
    private readonly Func<CancellationToken, ValueTask<ISqlConnection>>? _pooledOpen;
    public MsSqlDbConnection() : this(string.Empty) { }
    public MsSqlDbConnection(string connectionString)
        : this(
            connectionString,
            string.IsNullOrWhiteSpace(connectionString) ? new MsSqlConnectOptions() : MsSqlConnectOptions.Parse(connectionString),
            null) { }
    internal MsSqlDbConnection(
        string connectionString,
        Func<CancellationToken, ValueTask<ISqlConnection>> pooledOpen)
        : this(connectionString, MsSqlConnectOptions.Parse(connectionString), pooledOpen) { }
    internal MsSqlDbConnection(
        string connectionString,
        MsSqlConnectOptions options,
        Func<CancellationToken, ValueTask<ISqlConnection>>? pooledOpen,
        bool autoOpenForCommands = false)
        : base(
            connectionString,
            options.Database,
            options.Host,
            options.ConnectTimeout,
            isPoolBound: pooledOpen is not null,
            autoOpenForCommands)
    {
        _options = options;
        _pooledOpen = pooledOpen;
    }
    protected override async Task<ISqlConnection> OpenCoreAsync(CancellationToken cancellationToken) =>
        _pooledOpen is not null
            ? await _pooledOpen(cancellationToken).ConfigureAwait(false)
            : await MsSqlClient.ConnectAsync(_options, cancellationToken).ConfigureAwait(false);
    protected override void SetConnectionStringCore(string connectionString)
    {
        _options = string.IsNullOrWhiteSpace(connectionString)
            ? new MsSqlConnectOptions()
            : MsSqlConnectOptions.Parse(connectionString);
        SetConnectionMetadata(connectionString, _options.Database, _options.Host, _options.ConnectTimeout);
    }
    protected override DbCommand CreateCommandCore() => new MsSqlDbCommand(this);
    protected override DbBatch CreateDbBatch() => new MsSqlDbBatch { Connection = this };
    protected override DbTransaction CreateTransaction(ISqlTransaction transaction, IsolationLevel isolationLevel) =>
        new MsSqlDbTransaction(transaction, this, isolationLevel);
    internal ISqlConnection GetConnectionForCommand() => NativeConnection;
}

public sealed class MsSqlDbCommand : ApexDbCommand
{
    internal override DbException TranslateException(SqlClientException exception) => MsSqlAdoErrors.Translate(exception);
    public MsSqlDbCommand() : base(new MsSqlDbParameterCollection(), MsSqlAdoReaderFactory.Instance) { }
    public MsSqlDbCommand(MsSqlDbConnection connection)
        : base(new MsSqlDbParameterCollection(), MsSqlAdoReaderFactory.Instance)
    {
        Connection = connection;
    }
    protected override ApexDbParameter CreateParameterCore() => new MsSqlDbParameter();
    protected override ISqlConnection GetConnection()
    {
        if (Connection is not MsSqlDbConnection connection)
        {
            throw new InvalidOperationException("The command connection must be a MsSqlDbConnection.");
        }

        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The command connection must be open.");
        }

        return connection.GetConnectionForCommand();
    }
    protected override ApexDbDataReader CreateReader(
        ISqlRowReader reader,
        CommandBehavior behavior,
        DbConnection executedConnection,
        CancellationTokenSource operationCancellation,
        Func<ValueTask> onClose) =>
        new MsSqlDbDataReader(reader, behavior, executedConnection, operationCancellation, onClose);
}

public sealed class MsSqlDbParameter : ApexDbParameter { }
public sealed class MsSqlDbParameterCollection : ApexDbParameterCollection { }
public sealed class MsSqlDbDataReader : ApexDbDataReader
{
    internal override DbException TranslateException(SqlClientException exception) => MsSqlAdoErrors.Translate(exception);
    public MsSqlDbDataReader(ISqlRowReader reader, CommandBehavior behavior, DbConnection? connection)
        : base(reader, behavior, connection) { }
    internal MsSqlDbDataReader(
        ISqlRowReader reader,
        CommandBehavior behavior,
        DbConnection connection,
        CancellationTokenSource operationCancellation,
        Func<ValueTask> onClose)
        : base(reader, behavior, connection, operationCancellation, onClose) { }
}
public sealed class MsSqlDbTransaction : ApexDbTransaction
{
    internal MsSqlDbTransaction(ISqlTransaction transaction, DbConnection connection, IsolationLevel isolationLevel)
        : base(transaction, connection, isolationLevel) { }
}
public sealed class MsSqlDbProviderFactory : DbProviderFactory
{
    public static readonly MsSqlDbProviderFactory Instance = new();
    public override bool CanCreateBatch => true;
    public override DbConnection CreateConnection() => new MsSqlDbConnection();
    public override DbCommand CreateCommand() => new MsSqlDbCommand();
    public override DbParameter CreateParameter() => new MsSqlDbParameter();
    public override DbBatch CreateBatch() => new MsSqlDbBatch();
    public override DbBatchCommand CreateBatchCommand() => new MsSqlDbBatchCommand();
    public override DbDataSource CreateDataSource(string connectionString) => new MsSqlDbDataSource(connectionString);
}

/// <summary>Pool-backed source of SQL Server ADO.NET connections.</summary>
public sealed class MsSqlDbDataSource : DbDataSource
{
    private readonly string _connectionString;
    private readonly MsSqlConnectOptions _options;
    private readonly MsSqlPool _pool;
    public MsSqlDbDataSource(string connectionString) : this(connectionString, null) { }
    public MsSqlDbDataSource(string connectionString, SqlPoolOptions? poolOptions)
    {
        _connectionString = connectionString;
        _options = MsSqlConnectOptions.Parse(connectionString);
        _pool = MsSqlPool.Create(_options, poolOptions);
    }

    public override string ConnectionString => _connectionString;
    protected override DbConnection CreateDbConnection() =>
        new MsSqlDbConnection(_connectionString, _options, _pool.GetConnectionAsync);
    protected override DbConnection OpenDbConnection() => throw ApexDbCommand.AsyncOnly();
    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = (MsSqlDbConnection)CreateDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    protected override DbCommand CreateDbCommand(string? commandText)
    {
        var command = new MsSqlDbCommand(new MsSqlDbConnection(
            _connectionString,
            _options,
            _pool.GetConnectionAsync,
            autoOpenForCommands: true));
        command.CommandText = commandText;
        return command;
    }
    protected override DbBatch CreateDbBatch() =>
        new MsSqlDbBatch
        {
            Connection = new MsSqlDbConnection(
                _connectionString,
                _options,
                _pool.GetConnectionAsync,
                autoOpenForCommands: true),
        };
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            ApexExceptionBoundary.RunValueAsync(_pool.DisposeAsync, MsSqlAdoErrors.Translate).AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
    protected override async ValueTask DisposeAsyncCore()
    {
        await ApexExceptionBoundary.RunValueAsync(_pool.DisposeAsync, MsSqlAdoErrors.Translate).ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}

internal static class MsSqlAdoReaderFactory
{
    internal static readonly IApexAdoReaderFactory Instance = new ApexResultReaderFactory("SQL Server");
}

internal static class MsSqlAdoErrors
{
    internal static DbException Translate(SqlClientException exception) =>
        exception is MsSqlException sqlServer
            ? new ApexDbException(sqlServer, errorCode: sqlServer.Number)
            : new ApexDbException(exception);
}

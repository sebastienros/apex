using System.Data;
using System.Data.Common;
using Apex.SqlClient;

namespace Apex.MySqlClient;

/// <summary>Asynchronous-only ADO.NET connection adapter for MySQL and MariaDB.</summary>
public sealed class MySqlDbConnection : ApexDbConnection
{
    private MySqlConnectOptions _options;
    private readonly Func<CancellationToken, ValueTask<ISqlConnection>>? _pooledOpen;
    public MySqlDbConnection() : this(string.Empty) { }
    public MySqlDbConnection(string connectionString)
        : this(
            connectionString,
            string.IsNullOrWhiteSpace(connectionString) ? new MySqlConnectOptions() : MySqlConnectOptions.Parse(connectionString),
            null) { }
    internal MySqlDbConnection(
        string connectionString,
        Func<CancellationToken, ValueTask<ISqlConnection>> pooledOpen)
        : this(connectionString, MySqlConnectOptions.Parse(connectionString), pooledOpen) { }
    internal MySqlDbConnection(
        string connectionString,
        MySqlConnectOptions options,
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
            : await MySqlClient.ConnectAsync(_options, cancellationToken).ConfigureAwait(false);
    protected override void SetConnectionStringCore(string connectionString)
    {
        _options = string.IsNullOrWhiteSpace(connectionString)
            ? new MySqlConnectOptions()
            : MySqlConnectOptions.Parse(connectionString);
        SetConnectionMetadata(connectionString, _options.Database, _options.Host, _options.ConnectTimeout);
    }
    protected override DbCommand CreateCommandCore() => new MySqlDbCommand(this);
    protected override DbBatch CreateDbBatch() => new MySqlDbBatch { Connection = this };
    protected override DbTransaction CreateTransaction(ISqlTransaction transaction, IsolationLevel isolationLevel) =>
        new MySqlDbTransaction(transaction, this, isolationLevel);
    internal ISqlConnection GetConnectionForCommand() => NativeConnection;
}

public sealed class MySqlDbCommand : ApexDbCommand
{
    public MySqlDbCommand() : base(new MySqlDbParameterCollection(), MySqlAdoReaderFactory.Instance) { }
    public MySqlDbCommand(MySqlDbConnection connection)
        : base(new MySqlDbParameterCollection(), MySqlAdoReaderFactory.Instance)
    {
        Connection = connection;
    }
    protected override ApexDbParameter CreateParameterCore() => new MySqlDbParameter();
    protected override ISqlConnection GetConnection()
    {
        if (Connection is not MySqlDbConnection connection)
        {
            throw new InvalidOperationException("The command connection must be a MySqlDbConnection.");
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
        new MySqlDbDataReader(reader, behavior, executedConnection, operationCancellation, onClose);
}

public sealed class MySqlDbParameter : ApexDbParameter { }
public sealed class MySqlDbParameterCollection : ApexDbParameterCollection { }
public sealed class MySqlDbDataReader : ApexDbDataReader
{
    public MySqlDbDataReader(ISqlRowReader reader, CommandBehavior behavior, DbConnection? connection)
        : base(reader, behavior, connection) { }
    internal MySqlDbDataReader(
        ISqlRowReader reader,
        CommandBehavior behavior,
        DbConnection connection,
        CancellationTokenSource operationCancellation,
        Func<ValueTask> onClose)
        : base(reader, behavior, connection, operationCancellation, onClose) { }
}
public sealed class MySqlDbTransaction : ApexDbTransaction
{
    internal MySqlDbTransaction(ISqlTransaction transaction, DbConnection connection, IsolationLevel isolationLevel)
        : base(transaction, connection, isolationLevel) { }
}
public sealed class MySqlDbProviderFactory : DbProviderFactory
{
    public static readonly MySqlDbProviderFactory Instance = new();
    public override bool CanCreateBatch => true;
    public override DbConnection CreateConnection() => new MySqlDbConnection();
    public override DbCommand CreateCommand() => new MySqlDbCommand();
    public override DbParameter CreateParameter() => new MySqlDbParameter();
    public override DbBatch CreateBatch() => new MySqlDbBatch();
    public override DbBatchCommand CreateBatchCommand() => new MySqlDbBatchCommand();
    public override DbDataSource CreateDataSource(string connectionString) => new MySqlDbDataSource(connectionString);
}

/// <summary>Pool-backed source of MySQL and MariaDB ADO.NET connections.</summary>
public sealed class MySqlDbDataSource : DbDataSource
{
    private readonly string _connectionString;
    private readonly MySqlPool _pool;
    public MySqlDbDataSource(string connectionString)
    {
        _connectionString = connectionString;
        _pool = MySqlPool.Create(connectionString);
    }

    public override string ConnectionString => _connectionString;
    protected override DbConnection CreateDbConnection() =>
        new MySqlDbConnection(_connectionString, _pool.GetConnectionAsync);
    protected override DbConnection OpenDbConnection() => throw ApexDbCommand.AsyncOnly();
    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = (MySqlDbConnection)CreateDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    protected override DbCommand CreateDbCommand(string? commandText)
    {
        var command = new MySqlDbCommand(new MySqlDbConnection(
            _connectionString,
            MySqlConnectOptions.Parse(_connectionString),
            _pool.GetConnectionAsync,
            autoOpenForCommands: true));
        command.CommandText = commandText;
        return command;
    }
    protected override DbBatch CreateDbBatch() =>
        new MySqlDbBatch
        {
            Connection = new MySqlDbConnection(
                _connectionString,
                MySqlConnectOptions.Parse(_connectionString),
                _pool.GetConnectionAsync,
                autoOpenForCommands: true),
        };
    protected override void Dispose(bool disposing)
    {
        if (disposing) _pool.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
    protected override async ValueTask DisposeAsyncCore()
    {
        await _pool.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}

internal sealed class MySqlAdoReaderFactory : IApexAdoReaderFactory
{
    internal static readonly MySqlAdoReaderFactory Instance = new();

    public ValueTask<ISqlRowReader> ExecuteReaderAsync(
        ISqlConnection connection,
        string sql,
        SqlParameters parameters,
        ISqlPreparedStatement? preparedStatement,
        CancellationToken cancellationToken)
    {
        if (connection is not IApexAdoReaderConnection adoConnection)
        {
            throw new ArgumentException("The command connection does not support MySQL ADO.NET readers.", nameof(connection));
        }

        return preparedStatement switch
        {
            null => adoConnection.ExecuteAdoReaderAsync(sql, parameters, cancellationToken),
            IApexAdoPreparedStatement statement => statement.ExecuteAdoReaderAsync(parameters, cancellationToken),
            _ => throw new ArgumentException(
                "The prepared statement must be created by the MySQL provider.",
                nameof(preparedStatement)),
        };
    }
}

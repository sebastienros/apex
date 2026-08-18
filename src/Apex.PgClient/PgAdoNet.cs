using System.Data;
using System.Data.Common;
using Apex.SqlClient;

namespace Apex.PgClient;

/// <summary>Asynchronous-only ADO.NET connection adapter for PostgreSQL.</summary>
public sealed class PgDbConnection : ApexDbConnection
{
    private PgConnectOptions _options;
    private readonly Func<CancellationToken, ValueTask<ISqlConnection>>? _pooledOpen;

    public PgDbConnection() : this(string.Empty) { }
    public PgDbConnection(string connectionString)
        : this(
            connectionString,
            string.IsNullOrWhiteSpace(connectionString) ? new PgConnectOptions() : PgConnectOptions.Parse(connectionString),
            null) { }
    internal PgDbConnection(
        string connectionString,
        Func<CancellationToken, ValueTask<ISqlConnection>> pooledOpen)
        : this(connectionString, PgConnectOptions.Parse(connectionString), pooledOpen) { }
    internal PgDbConnection(
        string connectionString,
        PgConnectOptions options,
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
            : await PgClient.ConnectAsync(_options, cancellationToken).ConfigureAwait(false);
    protected override void SetConnectionStringCore(string connectionString)
    {
        _options = string.IsNullOrWhiteSpace(connectionString)
            ? new PgConnectOptions()
            : PgConnectOptions.Parse(connectionString);
        SetConnectionMetadata(connectionString, _options.Database, _options.Host, _options.ConnectTimeout);
    }
    protected override DbCommand CreateCommandCore() => new PgDbCommand(this);
    protected override DbBatch CreateDbBatch() => new PgDbBatch { Connection = this };
    protected override DbTransaction CreateTransaction(ISqlTransaction transaction, IsolationLevel isolationLevel) =>
        new PgDbTransaction(transaction, this, isolationLevel);
    internal ISqlConnection GetConnectionForCommand() => NativeConnection;
}

/// <summary>Asynchronous-only ADO.NET command adapter for PostgreSQL.</summary>
public sealed class PgDbCommand : ApexDbCommand
{
    public PgDbCommand() : base(new PgDbParameterCollection(), PgAdoReaderFactory.Instance) { }
    public PgDbCommand(PgDbConnection connection)
        : base(new PgDbParameterCollection(), PgAdoReaderFactory.Instance)
    {
        Connection = connection;
    }
    protected override ApexDbParameter CreateParameterCore() => new PgDbParameter();
    protected override ISqlConnection GetConnection()
    {
        if (Connection is not PgDbConnection connection)
        {
            throw new InvalidOperationException("The command connection must be a PgDbConnection.");
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
        new PgDbDataReader(reader, behavior, executedConnection, operationCancellation, onClose);
}

public sealed class PgDbParameter : ApexDbParameter { }
public sealed class PgDbParameterCollection : ApexDbParameterCollection { }
public sealed class PgDbDataReader : ApexDbDataReader
{
    public PgDbDataReader(ISqlRowReader reader, CommandBehavior behavior, DbConnection? connection)
        : base(reader, behavior, connection) { }
    internal PgDbDataReader(
        ISqlRowReader reader,
        CommandBehavior behavior,
        DbConnection connection,
        CancellationTokenSource operationCancellation,
        Func<ValueTask> onClose)
        : base(reader, behavior, connection, operationCancellation, onClose) { }
}

/// <summary>ADO.NET transaction adapter for PostgreSQL.</summary>
public sealed class PgDbTransaction : ApexDbTransaction
{
    internal PgDbTransaction(ISqlTransaction transaction, DbConnection connection, IsolationLevel isolationLevel)
        : base(transaction, connection, isolationLevel) { }
}

/// <summary>Factory for the PostgreSQL asynchronous ADO.NET adapter.</summary>
public sealed class PgDbProviderFactory : DbProviderFactory
{
    public static readonly PgDbProviderFactory Instance = new();
    public override bool CanCreateBatch => true;
    public override bool CanCreateDataSourceEnumerator => false;
    public override DbConnection CreateConnection() => new PgDbConnection();
    public override DbCommand CreateCommand() => new PgDbCommand();
    public override DbParameter CreateParameter() => new PgDbParameter();
    public override DbBatch CreateBatch() => new PgDbBatch();
    public override DbBatchCommand CreateBatchCommand() => new PgDbBatchCommand();
    public override DbDataSource CreateDataSource(string connectionString) => new PgDbDataSource(connectionString);
}

/// <summary>Pool-backed source of PostgreSQL ADO.NET connections.</summary>
public sealed class PgDbDataSource : DbDataSource
{
    private readonly string _connectionString;
    private readonly PgPool _pool;
    public PgDbDataSource(string connectionString)
    {
        _connectionString = connectionString;
        _pool = PgPool.Create(PgConnectOptions.Parse(connectionString));
    }

    public override string ConnectionString => _connectionString;
    protected override DbConnection CreateDbConnection() =>
        new PgDbConnection(_connectionString, _pool.GetConnectionAsync);
    protected override DbConnection OpenDbConnection() => throw ApexDbCommand.AsyncOnly();
    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = (PgDbConnection)CreateDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    protected override DbCommand CreateDbCommand(string? commandText)
    {
        var command = new PgDbCommand(new PgDbConnection(
            _connectionString,
            PgConnectOptions.Parse(_connectionString),
            _pool.GetConnectionAsync,
            autoOpenForCommands: true));
        command.CommandText = commandText;
        return command;
    }
    protected override DbBatch CreateDbBatch() =>
        new PgDbBatch
        {
            Connection = new PgDbConnection(
                _connectionString,
                PgConnectOptions.Parse(_connectionString),
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

internal sealed class PgAdoReaderFactory : IApexAdoReaderFactory
{
    internal static readonly PgAdoReaderFactory Instance = new();

    public ValueTask<ISqlRowReader> ExecuteReaderAsync(
        ISqlConnection connection,
        string sql,
        SqlParameters parameters,
        ISqlPreparedStatement? preparedStatement,
        CancellationToken cancellationToken)
    {
        if (connection is not IApexAdoReaderConnection adoConnection)
        {
            throw new ArgumentException("The command connection does not support PostgreSQL ADO.NET readers.", nameof(connection));
        }

        return preparedStatement switch
        {
            null => adoConnection.ExecuteAdoReaderAsync(sql, parameters, cancellationToken),
            IApexAdoPreparedStatement statement => statement.ExecuteAdoReaderAsync(parameters, cancellationToken),
            _ => throw new ArgumentException(
                "The prepared statement must be created by the PostgreSQL provider.",
                nameof(preparedStatement)),
        };
    }
}

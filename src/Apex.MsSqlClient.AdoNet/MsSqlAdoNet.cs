using System.Data;
using System.Data.Common;
using Apex.SqlClient;
using Tds = Apex.MsSqlClient.Internal.TdsDataType;

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

    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
    internal override Type GetColumnFieldType(SqlColumn column) => MsSqlAdoColumnTypes.GetFieldType(column);
    internal override string GetColumnDataTypeName(SqlColumn column) => MsSqlAdoColumnTypes.GetDataTypeName(column);
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

/// <summary>Maps SQL Server (TDS) result columns to the CLR type and type name of the native object decoder.</summary>
internal static class MsSqlAdoColumnTypes
{
    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
    internal static Type GetFieldType(SqlColumn column) => column.TypeId switch
    {
        Tds.Int1 => typeof(byte),
        Tds.Bit or Tds.BitN => typeof(bool),
        Tds.Int2 => typeof(short),
        Tds.Int4 => typeof(int),
        Tds.Int8 => typeof(long),
        Tds.IntN => column.TypeSize switch
        {
            1 => typeof(byte),
            2 => typeof(short),
            4 => typeof(int),
            8 => typeof(long),
            _ => typeof(object),
        },
        Tds.Float4 => typeof(float),
        Tds.Float8 => typeof(double),
        Tds.FloatN => column.TypeSize == 4 ? typeof(float) : typeof(double),
        Tds.Decimal or Tds.Numeric or Tds.DecimalN or Tds.NumericN or Tds.Money or Tds.Money4 or Tds.MoneyN => typeof(decimal),
        Tds.Guid => typeof(System.Guid),
        Tds.Date => typeof(DateOnly),
        Tds.Time => typeof(TimeOnly),
        Tds.DateTime2 or Tds.DateTime or Tds.DateTime4 or Tds.DateTimeN => typeof(System.DateTime),
        Tds.DateTimeOffset => typeof(System.DateTimeOffset),
        Tds.NVarChar or Tds.NChar or Tds.NText or Tds.Xml or Tds.Json or Tds.Char or Tds.VarChar or Tds.BigChar or Tds.BigVarChar or Tds.Text =>
            typeof(string),
        Tds.Binary or Tds.VarBinary or Tds.BigBinary or Tds.BigVarBinary or Tds.Image or Tds.Udt => typeof(byte[]),
        _ => typeof(object),
    };

    internal static string GetDataTypeName(SqlColumn column) => column.TypeId switch
    {
        Tds.Int1 => "tinyint",
        Tds.Bit or Tds.BitN => "bit",
        Tds.Int2 => "smallint",
        Tds.Int4 => "int",
        Tds.Int8 => "bigint",
        Tds.IntN => column.TypeSize switch
        {
            1 => "tinyint",
            2 => "smallint",
            8 => "bigint",
            _ => "int",
        },
        Tds.Float4 => "real",
        Tds.Float8 => "float",
        Tds.FloatN => column.TypeSize == 4 ? "real" : "float",
        Tds.Decimal or Tds.DecimalN => "decimal",
        Tds.Numeric or Tds.NumericN => "numeric",
        Tds.Money => "money",
        Tds.Money4 => "smallmoney",
        Tds.MoneyN => column.TypeSize == 4 ? "smallmoney" : "money",
        Tds.Guid => "uniqueidentifier",
        Tds.Date => "date",
        Tds.Time => "time",
        Tds.DateTime2 => "datetime2",
        Tds.DateTimeOffset => "datetimeoffset",
        Tds.DateTime => "datetime",
        Tds.DateTime4 => "smalldatetime",
        Tds.DateTimeN => column.TypeSize == 4 ? "smalldatetime" : "datetime",
        Tds.NVarChar => "nvarchar",
        Tds.NChar => "nchar",
        Tds.NText => "ntext",
        Tds.Xml => "xml",
        Tds.Json => "json",
        Tds.Char or Tds.BigChar => "char",
        Tds.VarChar or Tds.BigVarChar => "varchar",
        Tds.Text => "text",
        Tds.Binary or Tds.BigBinary => "binary",
        Tds.VarBinary or Tds.BigVarBinary => "varbinary",
        Tds.Image => "image",
        Tds.Udt => "udt",
        _ => column.TypeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

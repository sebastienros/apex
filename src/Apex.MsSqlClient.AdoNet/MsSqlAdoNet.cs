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
    // TDS data type tokens (MS-TDS 2.2.5.4).
    private const byte Image = 0x22, Text = 0x23, Guid = 0x24, VarBinary = 0x25, IntN = 0x26, VarChar = 0x27,
        Date = 0x28, Time = 0x29, DateTime2 = 0x2A, DateTimeOffset = 0x2B, Binary = 0x2D, Char = 0x2F,
        Int1 = 0x30, Bit = 0x32, Int2 = 0x34, Decimal = 0x37, Int4 = 0x38, DateTime4 = 0x3A, Float4 = 0x3B,
        Money = 0x3C, DateTime = 0x3D, Float8 = 0x3E, Numeric = 0x3F, NText = 0x63, BitN = 0x68,
        DecimalN = 0x6A, NumericN = 0x6C, FloatN = 0x6D, MoneyN = 0x6E, DateTimeN = 0x6F, Money4 = 0x7A,
        Int8 = 0x7F, BigVarBinary = 0xA5, BigVarChar = 0xA7, BigBinary = 0xAD, BigChar = 0xAF,
        NVarChar = 0xE7, NChar = 0xEF, Udt = 0xF0, Xml = 0xF1, Json = 0xF4;

    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
    internal static Type GetFieldType(SqlColumn column) => column.TypeId switch
    {
        Int1 => typeof(byte),
        Bit or BitN => typeof(bool),
        Int2 => typeof(short),
        Int4 => typeof(int),
        Int8 => typeof(long),
        IntN => column.TypeSize switch
        {
            1 => typeof(byte),
            2 => typeof(short),
            4 => typeof(int),
            8 => typeof(long),
            _ => typeof(object),
        },
        Float4 => typeof(float),
        Float8 => typeof(double),
        FloatN => column.TypeSize == 4 ? typeof(float) : typeof(double),
        Decimal or Numeric or DecimalN or NumericN or Money or Money4 or MoneyN => typeof(decimal),
        Guid => typeof(System.Guid),
        Date => typeof(DateOnly),
        Time => typeof(TimeOnly),
        DateTime2 or DateTime or DateTime4 or DateTimeN => typeof(System.DateTime),
        DateTimeOffset => typeof(System.DateTimeOffset),
        NVarChar or NChar or NText or Xml or Json or Char or VarChar or BigChar or BigVarChar or Text =>
            typeof(string),
        Binary or VarBinary or BigBinary or BigVarBinary or Image or Udt => typeof(byte[]),
        _ => typeof(object),
    };

    internal static string GetDataTypeName(SqlColumn column) => column.TypeId switch
    {
        Int1 => "tinyint",
        Bit or BitN => "bit",
        Int2 => "smallint",
        Int4 => "int",
        Int8 => "bigint",
        IntN => column.TypeSize switch
        {
            1 => "tinyint",
            2 => "smallint",
            8 => "bigint",
            _ => "int",
        },
        Float4 => "real",
        Float8 => "float",
        FloatN => column.TypeSize == 4 ? "real" : "float",
        Decimal or DecimalN => "decimal",
        Numeric or NumericN => "numeric",
        Money => "money",
        Money4 => "smallmoney",
        MoneyN => column.TypeSize == 4 ? "smallmoney" : "money",
        Guid => "uniqueidentifier",
        Date => "date",
        Time => "time",
        DateTime2 => "datetime2",
        DateTimeOffset => "datetimeoffset",
        DateTime => "datetime",
        DateTime4 => "smalldatetime",
        DateTimeN => column.TypeSize == 4 ? "smalldatetime" : "datetime",
        NVarChar => "nvarchar",
        NChar => "nchar",
        NText => "ntext",
        Xml => "xml",
        Json => "json",
        Char or BigChar => "char",
        VarChar or BigVarChar => "varchar",
        Text => "text",
        Binary or BigBinary => "binary",
        VarBinary or BigVarBinary => "varbinary",
        Image => "image",
        Udt => "udt",
        _ => column.TypeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

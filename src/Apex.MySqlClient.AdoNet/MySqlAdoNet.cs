using System.Data;
using System.Data.Common;
using Apex.SqlClient;

namespace Apex.MySqlClient;

/// <summary>Asynchronous-only ADO.NET connection adapter for MySQL and MariaDB.</summary>
public sealed class MySqlDbConnection : ApexDbConnection
{
    internal override DbException TranslateException(SqlClientException exception) => MySqlAdoErrors.Translate(exception);
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
    internal override DbException TranslateException(SqlClientException exception) => MySqlAdoErrors.Translate(exception);
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
    internal override DbException TranslateException(SqlClientException exception) => MySqlAdoErrors.Translate(exception);
    public MySqlDbDataReader(ISqlRowReader reader, CommandBehavior behavior, DbConnection? connection)
        : base(reader, behavior, connection) { }
    internal MySqlDbDataReader(
        ISqlRowReader reader,
        CommandBehavior behavior,
        DbConnection connection,
        CancellationTokenSource operationCancellation,
        Func<ValueTask> onClose)
        : base(reader, behavior, connection, operationCancellation, onClose) { }

    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
    internal override Type GetColumnFieldType(SqlColumn column) => MySqlAdoColumnTypes.GetFieldType(column);
    internal override string GetColumnDataTypeName(SqlColumn column) => MySqlAdoColumnTypes.GetDataTypeName(column);
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
    private readonly MySqlConnectOptions _options;
    private readonly MySqlPool _pool;
    public MySqlDbDataSource(string connectionString) : this(connectionString, null) { }
    public MySqlDbDataSource(string connectionString, SqlPoolOptions? poolOptions)
    {
        _connectionString = connectionString;
        _options = MySqlConnectOptions.Parse(connectionString);
        _pool = MySqlPool.Create(_options, poolOptions);
    }

    public override string ConnectionString => _connectionString;
    protected override DbConnection CreateDbConnection() =>
        new MySqlDbConnection(_connectionString, _options, _pool.GetConnectionAsync);
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
            _options,
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
                _options,
                _pool.GetConnectionAsync,
                autoOpenForCommands: true),
        };
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            ApexExceptionBoundary.RunValueAsync(_pool.DisposeAsync, MySqlAdoErrors.Translate).AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
    protected override async ValueTask DisposeAsyncCore()
    {
        await ApexExceptionBoundary.RunValueAsync(_pool.DisposeAsync, MySqlAdoErrors.Translate).ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}

internal static class MySqlAdoReaderFactory
{
    internal static readonly IApexAdoReaderFactory Instance = new ApexResultReaderFactory("MySQL");
}

internal static class MySqlAdoErrors
{
    internal static DbException Translate(SqlClientException exception) =>
        exception is MySqlException mysql
            ? new ApexDbException(mysql, mysql.SqlState, errorCode: mysql.ErrorNumber)
            : new ApexDbException(exception);
}

/// <summary>Maps MySQL result columns to the CLR type and type name of the native object decoder.</summary>
internal static class MySqlAdoColumnTypes
{
    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
    internal static Type GetFieldType(SqlColumn column)
    {
        var unsigned = IsUnsigned(column);
        return (MySqlType)column.TypeId switch
        {
            MySqlType.Tiny => unsigned ? typeof(byte) : typeof(sbyte),
            MySqlType.Short => unsigned ? typeof(ushort) : typeof(short),
            MySqlType.Year => typeof(int),
            MySqlType.Int24 or MySqlType.Long => unsigned ? typeof(uint) : typeof(int),
            MySqlType.LongLong => unsigned ? typeof(ulong) : typeof(long),
            MySqlType.Bit => typeof(ulong),
            MySqlType.Float => typeof(float),
            MySqlType.Double => typeof(double),
            MySqlType.Decimal or MySqlType.NewDecimal => typeof(MySqlDecimal),
            MySqlType.Date or MySqlType.NewDate => typeof(DateOnly),
            MySqlType.DateTime or MySqlType.DateTime2 or MySqlType.Timestamp or MySqlType.Timestamp2 =>
                typeof(DateTime),
            MySqlType.Time or MySqlType.Time2 => typeof(TimeSpan),
            MySqlType.Json => typeof(System.Text.Json.JsonElement),
            MySqlType.Null => typeof(object),
            MySqlType.Geometry or MySqlType.Vector => typeof(byte[]),
            _ => IsBinary(column) ? typeof(byte[]) : typeof(string),
        };
    }

    // The driver packs the column flags into TypeModifier and the character set into TypeSize (see
    // MySqlColumnMetadata.FromColumn); read them directly so metadata calls allocate nothing.
    private const int BinaryCharacterSet = 63;

    private static MySqlColumnFlags GetFlags(SqlColumn column) => (MySqlColumnFlags)unchecked((ushort)column.TypeModifier);

    private static bool IsUnsigned(SqlColumn column) => (GetFlags(column) & MySqlColumnFlags.Unsigned) != 0;

    private static bool IsBinary(SqlColumn column) => unchecked((ushort)column.TypeSize) == BinaryCharacterSet;

    internal static string GetDataTypeName(SqlColumn column)
    {
        var type = (MySqlType)column.TypeId;
        var flags = GetFlags(column);
        var binary = IsBinary(column);
        var name = type switch
        {
            MySqlType.Tiny => "TINYINT",
            MySqlType.Short => "SMALLINT",
            MySqlType.Int24 => "MEDIUMINT",
            MySqlType.Long => "INT",
            MySqlType.LongLong => "BIGINT",
            MySqlType.Year => "YEAR",
            MySqlType.Bit => "BIT",
            MySqlType.Float => "FLOAT",
            MySqlType.Double => "DOUBLE",
            MySqlType.Decimal or MySqlType.NewDecimal => "DECIMAL",
            MySqlType.Date or MySqlType.NewDate => "DATE",
            MySqlType.DateTime or MySqlType.DateTime2 => "DATETIME",
            MySqlType.Timestamp or MySqlType.Timestamp2 => "TIMESTAMP",
            MySqlType.Time or MySqlType.Time2 => "TIME",
            MySqlType.Json => "JSON",
            MySqlType.Null => "NULL",
            MySqlType.Geometry => "GEOMETRY",
            MySqlType.Vector => "VECTOR",
            MySqlType.Enum => "ENUM",
            MySqlType.Set => "SET",
            MySqlType.TinyBlob => binary ? "TINYBLOB" : "TINYTEXT",
            MySqlType.MediumBlob => binary ? "MEDIUMBLOB" : "MEDIUMTEXT",
            MySqlType.LongBlob => binary ? "LONGBLOB" : "LONGTEXT",
            MySqlType.Blob => binary ? "BLOB" : "TEXT",
            MySqlType.VarChar or MySqlType.VarString => binary ? "VARBINARY" : "VARCHAR",
            MySqlType.String when (flags & MySqlColumnFlags.Enum) != 0 => "ENUM",
            MySqlType.String when (flags & MySqlColumnFlags.Set) != 0 => "SET",
            MySqlType.String => binary ? "BINARY" : "CHAR",
            _ => column.TypeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return IsUnsigned(column) && type is
            MySqlType.Tiny or MySqlType.Short or MySqlType.Int24 or MySqlType.Long or MySqlType.LongLong or
            MySqlType.Float or MySqlType.Double or MySqlType.Decimal or MySqlType.NewDecimal
            ? name + " UNSIGNED"
            : name;
    }
}

using System.Data;
using System.Data.Common;
using Apex.SqlClient;

namespace Apex.PgClient;

/// <summary>Asynchronous-only ADO.NET connection adapter for PostgreSQL.</summary>
public sealed class PgDbConnection : ApexDbConnection
{
    internal override DbException TranslateException(SqlClientException exception) => PgAdoErrors.Translate(exception);
    private PgConnectOptions _options;
    private readonly Func<CancellationToken, ValueTask<ISqlConnection>>? _pooledOpen;

    public PgDbConnection() : this(string.Empty) { }
    public PgDbConnection(string connectionString)
        : this(
            connectionString,
            PgAdoConnectionString.Parse(connectionString),
            null) { }
    internal PgDbConnection(
        string connectionString,
        Func<CancellationToken, ValueTask<ISqlConnection>> pooledOpen)
        : this(connectionString, PgAdoConnectionString.Parse(connectionString), pooledOpen) { }
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
        _options = PgAdoConnectionString.Parse(connectionString);
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
    internal override DbException TranslateException(SqlClientException exception) => PgAdoErrors.Translate(exception);
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
    internal override DbException TranslateException(SqlClientException exception) => PgAdoErrors.Translate(exception);
    public PgDbDataReader(ISqlRowReader reader, CommandBehavior behavior, DbConnection? connection)
        : base(reader, behavior, connection) { }
    internal PgDbDataReader(
        ISqlRowReader reader,
        CommandBehavior behavior,
        DbConnection connection,
        CancellationTokenSource operationCancellation,
        Func<ValueTask> onClose)
        : base(reader, behavior, connection, operationCancellation, onClose) { }

    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
    internal override Type GetColumnFieldType(SqlColumn column) => PgAdoColumnTypes.GetFieldType(column);
    internal override string GetColumnDataTypeName(SqlColumn column) => PgAdoColumnTypes.GetDataTypeName(column);
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
    private readonly PgConnectOptions _options;
    private readonly PgPool _pool;
    public PgDbDataSource(string connectionString) : this(connectionString, null) { }
    public PgDbDataSource(string connectionString, SqlPoolOptions? poolOptions)
    {
        _connectionString = connectionString;
        _options = PgAdoConnectionString.Parse(connectionString);
        _pool = PgPool.Create(_options, poolOptions);
    }

    public override string ConnectionString => _connectionString;
    protected override DbConnection CreateDbConnection() =>
        new PgDbConnection(_connectionString, _options, _pool.GetConnectionAsync);
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
            _options,
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
                _options,
                _pool.GetConnectionAsync,
                autoOpenForCommands: true),
        };
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            ApexExceptionBoundary.RunValueAsync(_pool.DisposeAsync, PgAdoErrors.Translate).AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
    protected override async ValueTask DisposeAsyncCore()
    {
        await ApexExceptionBoundary.RunValueAsync(_pool.DisposeAsync, PgAdoErrors.Translate).ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}

internal sealed class PgAdoReaderFactory : ApexResultReaderFactory
{
    internal static readonly PgAdoReaderFactory Instance = new();

    private PgAdoReaderFactory() : base("PostgreSQL") { }

    // A single statement that cannot return rows has the same outcome through the native ExecuteAsync
    // as through a drained reader, without allocating result-boundary state. Multi-statement text keeps
    // the reader so RecordsAffected sums every statement, and so does RETURNING (even a false match in a
    // literal or identifier), whose rows ExecuteAsync would buffer instead of streaming past.
    public override bool CanExecuteNonQuery(string sql) =>
        IsNonQueryKeyword(Internal.PgSqlText.GetLeadingKeyword(sql)) &&
        !sql.Contains("RETURNING", StringComparison.OrdinalIgnoreCase) &&
        Internal.PgSqlText.IsSingleStatement(sql);

    // The reader's RecordsAffected rule: only INSERT, UPDATE, DELETE and MERGE report a count.
    public override int GetRecordsAffected(SqlCommandResult result) =>
        PgConnection.ReportsRecordsAffected(result.CommandTag) ? checked((int)result.AffectedRows) : -1;

    private static bool IsNonQueryKeyword(ReadOnlySpan<char> keyword) =>
        keyword.Equals("INSERT", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("DELETE", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("MERGE", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("CREATE", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("ALTER", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("DROP", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("TRUNCATE", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("GRANT", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("REVOKE", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("COMMENT", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("SET", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("RESET", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("LOCK", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("DO", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Maps PostgreSQL result columns to the CLR type and type name of the native object decoder.</summary>
internal static class PgAdoColumnTypes
{
    private const System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes ReflectedMembers =
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties;

    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(ReflectedMembers)]
    internal static Type GetFieldType(SqlColumn column)
    {
        var type = GetBuiltInType(column.TypeId);
        if (type is not null) return type;
        var element = Internal.PgTextCodec.GetArrayElementType(column.TypeId);
        if (element != 0 && GetArrayType(element) is { } arrayType) return arrayType;

        // Other types decode as their text in text format; in binary format they need a registered codec.
        return column.Format == SqlDataFormat.Text ? typeof(string) : typeof(object);
    }

    internal static string GetDataTypeName(SqlColumn column)
    {
        var element = Internal.PgTextCodec.GetArrayElementType(column.TypeId);
        if (element != 0 && GetTypeName(element) is { } elementName) return elementName + "[]";
        return GetTypeName(column.TypeId) ??
            column.TypeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(ReflectedMembers)]
    private static Type? GetBuiltInType(uint typeId) => typeId switch
    {
        16 => typeof(bool),
        17 => typeof(byte[]),
        18 or 19 or 25 or 1042 or 1043 => typeof(string),
        20 => typeof(long),
        21 => typeof(short),
        23 => typeof(int),
        700 => typeof(float),
        701 => typeof(double),
        790 => typeof(PgMoney),
        1082 => typeof(DateOnly),
        1083 => typeof(TimeOnly),
        1114 => typeof(DateTime),
        1184 => typeof(DateTimeOffset),
        1186 => typeof(PgInterval),
        1266 => typeof(PgTimeWithTimeZone),
        1700 => typeof(PgNumeric),
        2950 => typeof(Guid),
        600 => typeof(PgPoint),
        601 => typeof(PgLineSegment),
        602 => typeof(PgPath),
        603 => typeof(PgBox),
        604 => typeof(PgPolygon),
        628 => typeof(PgLine),
        650 => typeof(PgCidr),
        718 => typeof(PgCircle),
        869 => typeof(PgInet),
        774 or 829 => typeof(System.Net.NetworkInformation.PhysicalAddress),
        1560 or 1562 => typeof(System.Collections.BitArray),
        114 or 3802 => typeof(System.Text.Json.JsonElement),
        _ => null,
    };

    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(ReflectedMembers)]
    private static Type? GetArrayType(uint elementTypeId) => elementTypeId switch
    {
        16 => typeof(bool?[]),
        17 => typeof(byte[][]),
        18 or 19 or 25 or 1042 or 1043 => typeof(string[]),
        20 => typeof(long?[]),
        21 => typeof(short?[]),
        23 => typeof(int?[]),
        700 => typeof(float?[]),
        701 => typeof(double?[]),
        790 => typeof(PgMoney?[]),
        1082 => typeof(DateOnly?[]),
        1083 => typeof(TimeOnly?[]),
        1114 => typeof(DateTime?[]),
        1184 => typeof(DateTimeOffset?[]),
        1186 => typeof(PgInterval?[]),
        1266 => typeof(PgTimeWithTimeZone?[]),
        1700 => typeof(PgNumeric?[]),
        2950 => typeof(Guid?[]),
        600 => typeof(PgPoint?[]),
        601 => typeof(PgLineSegment?[]),
        602 => typeof(PgPath[]),
        603 => typeof(PgBox?[]),
        604 => typeof(PgPolygon[]),
        628 => typeof(PgLine?[]),
        650 => typeof(PgCidr?[]),
        718 => typeof(PgCircle?[]),
        869 => typeof(PgInet?[]),
        774 or 829 => typeof(System.Net.NetworkInformation.PhysicalAddress[]),
        1560 or 1562 => typeof(System.Collections.BitArray[]),
        114 or 3802 => typeof(System.Text.Json.JsonElement?[]),
        _ => null,
    };

    private static string? GetTypeName(uint typeId) => typeId switch
    {
        16 => "boolean",
        17 => "bytea",
        18 => "\"char\"",
        19 => "name",
        20 => "bigint",
        21 => "smallint",
        23 => "integer",
        25 => "text",
        26 => "oid",
        114 => "json",
        142 => "xml",
        600 => "point",
        601 => "lseg",
        602 => "path",
        603 => "box",
        604 => "polygon",
        628 => "line",
        650 => "cidr",
        700 => "real",
        701 => "double precision",
        705 => "unknown",
        718 => "circle",
        774 => "macaddr8",
        790 => "money",
        829 => "macaddr",
        869 => "inet",
        1042 => "character",
        1043 => "character varying",
        1082 => "date",
        1083 => "time without time zone",
        1114 => "timestamp without time zone",
        1184 => "timestamp with time zone",
        1186 => "interval",
        1266 => "time with time zone",
        1560 => "bit",
        1562 => "bit varying",
        1700 => "numeric",
        2950 => "uuid",
        3614 => "tsvector",
        3615 => "tsquery",
        3802 => "jsonb",
        3904 => "int4range",
        3906 => "numrange",
        3908 => "tsrange",
        3910 => "tstzrange",
        3912 => "daterange",
        3926 => "int8range",
        _ => null,
    };
}

internal static class PgAdoErrors
{
    internal static DbException Translate(SqlClientException exception) =>
        exception is PgException postgres
            ? new ApexDbException(postgres, postgres.SqlState, postgres.IsTransient)
            : new ApexDbException(exception);
}

using System.Data.Common;
using System.Globalization;
using Apex.MsSqlClient;
using Apex.MySqlClient;
using Apex.PgClient;
using Apex.SqlClient;
using Microsoft.Data.SqlClient;
using Npgsql;
using MySqlConnectorDriver = MySqlConnector;

namespace Apex.Fortunes.Minimal;

internal abstract class FortuneDatabase : IAsyncDisposable
{
    protected const string Query = "SELECT id, message FROM fortune";
    public abstract ValueTask DisposeAsync();

    public static ValueTask<FortuneDatabase> CreateAsync(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var database = RequiredDatabase(configuration["DATABASE"]);
        var driver = RequiredDriver(database, configuration["DRIVER"]);
        var connectionString = RequiredConnectionString(configuration["CONNECTION_STRING"]);

        return (database, driver) switch
        {
            ("postgresql", "apex") => ApexPostgreSqlFortuneDatabase.CreateAsync(
                connectionString,
                PositiveSetting(configuration, "DATABASE_CONNECTIONS", 56),
                PositiveSetting(configuration, "APEX_PIPELINING", 16)),
            ("postgresql", "apex-ado") => ValueTask.FromResult<FortuneDatabase>(
                new ApexAdoPostgreSqlFortuneDatabase(
                    connectionString,
                    PositiveSetting(configuration, "DATABASE_CONNECTIONS", 56))),
            ("postgresql", "npgsql") => ValueTask.FromResult<FortuneDatabase>(
                new NpgsqlFortuneDatabase(
                    connectionString,
                    PositiveSetting(configuration, "DATABASE_CONNECTIONS", 256))),
            ("mysql", "apex") => ValueTask.FromResult<FortuneDatabase>(
                new ApexMySqlFortuneDatabase(
                    connectionString,
                    PositiveSetting(configuration, "DATABASE_CONNECTIONS", 64))),
            ("mysql", "apex-ado") => ValueTask.FromResult<FortuneDatabase>(
                new ApexAdoStringFortuneDatabase(
                    new MySqlDbDataSource(
                        connectionString,
                        PoolOptions(configuration, 64)),
                    unsignedId: true)),
            ("mysql", "mysqlconnector") => ValueTask.FromResult<FortuneDatabase>(
                new MySqlConnectorFortuneDatabase(
                    connectionString,
                    PositiveSetting(configuration, "DATABASE_CONNECTIONS", 64))),
            ("sqlserver", "apex") => ValueTask.FromResult<FortuneDatabase>(
                new ApexSqlServerFortuneDatabase(
                    connectionString,
                    PositiveSetting(configuration, "DATABASE_CONNECTIONS", 64))),
            ("sqlserver", "apex-ado") => ValueTask.FromResult<FortuneDatabase>(
                new ApexAdoStringFortuneDatabase(
                    new MsSqlDbDataSource(
                        connectionString,
                        PoolOptions(configuration, 64)))),
            ("sqlserver", "microsoftdatasqlclient") => ValueTask.FromResult<FortuneDatabase>(
                new MicrosoftDataSqlClientFortuneDatabase(
                    connectionString,
                    PositiveSetting(configuration, "DATABASE_CONNECTIONS", 64))),
            _ => throw new InvalidOperationException("The database selection is invalid."),
        };
    }

    protected static PgConnectOptions CreatePostgreSqlOptions(
        string connectionString,
        int pipeliningLimit)
    {
        if (!connectionString.Contains(';', StringComparison.Ordinal))
        {
            return PgConnectOptions.Parse(connectionString) with
            {
                PipeliningLimit = pipeliningLimit,
            };
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return new PgConnectOptions
        {
            Host = builder.Host ??
                throw new InvalidOperationException("PostgreSQL Host is required."),
            Port = builder.Port,
            Database = builder.Database ??
                throw new InvalidOperationException("PostgreSQL Database is required."),
            Username = builder.Username ??
                throw new InvalidOperationException("PostgreSQL Username is required."),
            Password = builder.Password ?? string.Empty,
            PipeliningLimit = pipeliningLimit,
        };
    }

    protected static string CreatePostgreSqlUri(string connectionString)
    {
        var options = CreatePostgreSqlOptions(
            connectionString,
            new PgConnectOptions().PipeliningLimit);
        return new UriBuilder("postgresql", options.Host, options.Port, options.Database)
        {
            UserName = options.Username,
            Password = options.Password,
        }.Uri.AbsoluteUri;
    }

    private static string RequiredDatabase(string? value)
    {
        var database = RequiredValue("DATABASE", value);
        return database is "postgresql" or "mysql" or "sqlserver"
            ? database
            : throw new InvalidOperationException(
                "DATABASE must be 'postgresql', 'mysql', or 'sqlserver'.");
    }

    private static string RequiredDriver(string database, string? value)
    {
        var driver = RequiredValue("DRIVER", value);
        return (database, driver) switch
        {
            ("postgresql", "apex" or "apex-ado" or "npgsql") or
            ("mysql", "apex" or "apex-ado" or "mysqlconnector") or
            ("sqlserver", "apex" or "apex-ado" or "microsoftdatasqlclient") => driver,
            _ => throw new InvalidOperationException(
                $"DRIVER '{driver}' is not valid for DATABASE '{database}'."),
        };
    }

    private static string RequiredConnectionString(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("CONNECTION_STRING is required.")
            : value;

    private static string RequiredValue(string name, string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} is required.")
            : value.Trim().ToLowerInvariant();

    private static int PositiveSetting(IConfiguration configuration, string name, int fallback)
    {
        var value = configuration[name];
        if (value is null)
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0
            ? parsed
            : throw new InvalidOperationException($"{name} must be a positive integer.");
    }

    private static SqlPoolOptions PoolOptions(IConfiguration configuration, int fallback) =>
        new()
        {
            MaximumSize = PositiveSetting(configuration, "DATABASE_CONNECTIONS", fallback),
        };
}

internal abstract class Utf8FortuneDatabase : FortuneDatabase
{
    private static readonly ReadOnlyMemory<byte> s_additionalFortune =
        "Additional fortune added at request time."u8.ToArray();

    public abstract ValueTask<List<Utf8Fortune>> LoadAsync(
        CancellationToken cancellationToken);

    protected static List<Utf8Fortune> Complete(List<Utf8Fortune> fortunes)
    {
        fortunes.Add(new Utf8Fortune(0, s_additionalFortune));
        fortunes.Sort();
        return fortunes;
    }
}

internal abstract class StringFortuneDatabase : FortuneDatabase
{
    private const string AdditionalFortune = "Additional fortune added at request time.";

    public abstract ValueTask<List<Fortune>> LoadAsync(
        CancellationToken cancellationToken);

    protected static List<Fortune> Complete(List<Fortune> fortunes)
    {
        fortunes.Add(new Fortune(0, AdditionalFortune));
        fortunes.Sort();
        return fortunes;
    }
}

internal sealed class ApexPostgreSqlFortuneDatabase : Utf8FortuneDatabase
{
    private readonly PgPipelinePool _pool;
    private readonly ISqlPreparedStatement _statement;

    private ApexPostgreSqlFortuneDatabase(PgPipelinePool pool, ISqlPreparedStatement statement)
    {
        _pool = pool;
        _statement = statement;
    }

    public static async ValueTask<FortuneDatabase> CreateAsync(
        string connectionString,
        int connectionCount,
        int pipeliningLimit)
    {
        var options = CreatePostgreSqlOptions(connectionString, pipeliningLimit);
        var pool = await PgPipelinePool.CreateAsync(
            options,
            new SqlPipelinePoolOptions
            {
                ConnectionCount = connectionCount,
            });
        try
        {
            var statement = await pool.PrepareAsync(Query);
            return new ApexPostgreSqlFortuneDatabase(pool, statement);
        }
        catch
        {
            await pool.DisposeAsync();
            throw;
        }
    }

    public override async ValueTask<List<Utf8Fortune>> LoadAsync(
        CancellationToken cancellationToken)
    {
        List<Utf8Fortune> fortunes = [];
        await _statement.CollectAsync(
            fortunes,
            static (results, row) => results.Add(new Utf8Fortune(
                row.GetInt32(0),
                row.Get<ReadOnlyMemory<byte>>(1))),
            cancellationToken: cancellationToken);
        return Complete(fortunes);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await _statement.DisposeAsync();
        }
        finally
        {
            await _pool.DisposeAsync();
        }
    }
}

internal sealed class ApexAdoPostgreSqlFortuneDatabase : Utf8FortuneDatabase
{
    private readonly PgDbDataSource _dataSource;

    public ApexAdoPostgreSqlFortuneDatabase(string connectionString, int connectionCount)
    {
        _dataSource = new PgDbDataSource(
            CreatePostgreSqlUri(connectionString),
            new SqlPoolOptions { MaximumSize = connectionCount });
    }

    public override async ValueTask<List<Utf8Fortune>> LoadAsync(
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(Query);
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<Utf8Fortune> fortunes = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            fortunes.Add(new Utf8Fortune(
                reader.GetInt32(0),
                reader.GetFieldValue<ReadOnlyMemory<byte>>(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

internal sealed class ApexAdoStringFortuneDatabase : StringFortuneDatabase
{
    private readonly DbDataSource _dataSource;
    private readonly bool _unsignedId;

    public ApexAdoStringFortuneDatabase(DbDataSource dataSource, bool unsignedId = false)
    {
        _dataSource = dataSource;
        _unsignedId = unsignedId;
    }

    public override async ValueTask<List<Fortune>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(Query);
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<Fortune> fortunes = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = _unsignedId
                ? checked((int)reader.GetFieldValue<uint>(0))
                : reader.GetInt32(0);
            fortunes.Add(new Fortune(id, reader.GetString(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

internal sealed class ApexMySqlFortuneDatabase : StringFortuneDatabase
{
    private const string PreparedQuery = Query + " WHERE ? = 1";
    private static readonly SqlParameters s_one = SqlParameters.Create(1);
    private readonly MySqlPool _pool;

    public ApexMySqlFortuneDatabase(string connectionString, int connectionCount)
    {
        var options = MySqlConnectOptions.Parse(connectionString) with
        {
            CachePreparedStatements = true,
        };
        _pool = MySqlPool.Create(
            options,
            new SqlPoolOptions
            {
                MaximumSize = connectionCount,
            });
    }

    public override async ValueTask<List<Fortune>> LoadAsync(CancellationToken cancellationToken)
    {
        var rows = await _pool.QueryAsync(PreparedQuery, s_one, cancellationToken);
        List<Fortune> fortunes = [];
        foreach (var row in rows)
        {
            fortunes.Add(new Fortune(checked((int)row.Get<uint>(0)), row.GetString(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => _pool.DisposeAsync();
}

internal sealed class ApexSqlServerFortuneDatabase : StringFortuneDatabase
{
    private const string ParameterizedQuery = Query + " WHERE @P1 = 1";
    private static readonly SqlParameters s_one = SqlParameters.Create(1);
    private readonly MsSqlPool _pool;

    public ApexSqlServerFortuneDatabase(string connectionString, int connectionCount)
    {
        _pool = MsSqlPool.Create(
            MsSqlConnectOptions.Parse(connectionString),
            new SqlPoolOptions
            {
                MaximumSize = connectionCount,
            });
    }

    public override async ValueTask<List<Fortune>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _pool.GetConnectionAsync(cancellationToken);
        await using var reader = await connection.ExecuteReaderAsync(
            ParameterizedQuery,
            s_one,
            cancellationToken);
        List<Fortune> fortunes = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            fortunes.Add(new Fortune(reader.GetInt32(0), reader.GetString(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => _pool.DisposeAsync();
}

internal sealed class NpgsqlFortuneDatabase : Utf8FortuneDatabase
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlFortuneDatabase(string connectionString, int connectionCount)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = connectionCount,
        };
        _dataSource = new NpgsqlSlimDataSourceBuilder(builder.ConnectionString).Build();
    }

    public override async ValueTask<List<Utf8Fortune>> LoadAsync(
        CancellationToken cancellationToken)
    {
        using var connection = _dataSource.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var command = new NpgsqlCommand(Query, connection);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<Utf8Fortune> fortunes = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            fortunes.Add(new Utf8Fortune(
                reader.GetInt32(0),
                reader.GetFieldValue<byte[]>(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

internal sealed class MySqlConnectorFortuneDatabase : StringFortuneDatabase
{
    private readonly MySqlConnectorDriver.MySqlDataSource _dataSource;

    public MySqlConnectorFortuneDatabase(string connectionString, int connectionCount)
    {
        var builder = new MySqlConnectorDriver.MySqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            ConnectionReset = false,
            AutoEnlist = false,
            DefaultCommandTimeout = 0,
            UseAffectedRows = true,
            MinimumPoolSize = (uint)connectionCount,
            MaximumPoolSize = (uint)connectionCount,
        };
        _dataSource = new MySqlConnectorDriver.MySqlDataSource(builder.ConnectionString);
    }

    public override async ValueTask<List<Fortune>> LoadAsync(CancellationToken cancellationToken)
    {
        using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        using var command = new MySqlConnectorDriver.MySqlCommand(Query, connection);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<Fortune> fortunes = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            fortunes.Add(new Fortune(reader.GetInt32(0), reader.GetString(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

internal sealed class MicrosoftDataSqlClientFortuneDatabase : StringFortuneDatabase
{
    private readonly string _connectionString;

    public MicrosoftDataSqlClientFortuneDatabase(string connectionString, int connectionCount)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            Enlist = false,
            CommandTimeout = 0,
            MinPoolSize = connectionCount,
            MaxPoolSize = connectionCount,
        };
        _connectionString = builder.ConnectionString;
    }

    public override async ValueTask<List<Fortune>> LoadAsync(CancellationToken cancellationToken)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = new SqlCommand(Query, connection);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<Fortune> fortunes = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            fortunes.Add(new Fortune(reader.GetInt32(0), reader.GetString(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

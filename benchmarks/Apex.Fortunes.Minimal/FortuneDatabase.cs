using System.Data;
using System.Globalization;
using System.Text;
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
    private static readonly ReadOnlyMemory<byte> s_additionalFortune =
        "Additional fortune added at request time."u8.ToArray();

    public abstract ValueTask<List<Fortune>> LoadAsync(CancellationToken cancellationToken);

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
                PositiveSetting(configuration, "APEX_CONNECTIONS", 56),
                PositiveSetting(configuration, "APEX_PIPELINING", 16)),
            ("postgresql", "npgsql") => ValueTask.FromResult<FortuneDatabase>(
                new NpgsqlFortuneDatabase(
                    connectionString,
                    PositiveSetting(configuration, "APEX_CONNECTIONS", 56))),
            ("mysql", "apex") => ValueTask.FromResult<FortuneDatabase>(
                new ApexMySqlFortuneDatabase(
                    connectionString,
                    PositiveSetting(configuration, "APEX_CONNECTIONS", 64))),
            ("mysql", "mysqlconnector") => ValueTask.FromResult<FortuneDatabase>(
                new MySqlConnectorFortuneDatabase(
                    connectionString,
                    PositiveSetting(configuration, "APEX_CONNECTIONS", 64))),
            ("sqlserver", "apex") => ValueTask.FromResult<FortuneDatabase>(
                new ApexSqlServerFortuneDatabase(
                    connectionString,
                    PositiveSetting(configuration, "APEX_CONNECTIONS", 64))),
            ("sqlserver", "microsoftdatasqlclient") => ValueTask.FromResult<FortuneDatabase>(
                new MicrosoftDataSqlClientFortuneDatabase(
                    connectionString,
                    PositiveSetting(configuration, "APEX_CONNECTIONS", 64))),
            _ => throw new InvalidOperationException("The database selection is invalid."),
        };
    }

    protected static List<Fortune> Complete(List<Fortune> fortunes)
    {
        fortunes.Add(new Fortune(0, s_additionalFortune));
        fortunes.Sort();
        return fortunes;
    }

    protected static Fortune Utf8Fortune(int id, string message) =>
        new(id, Encoding.UTF8.GetBytes(message));

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
            ("postgresql", "apex" or "npgsql") or
            ("mysql", "apex" or "mysqlconnector") or
            ("sqlserver", "apex" or "microsoftdatasqlclient") => driver,
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
}

internal sealed class ApexPostgreSqlFortuneDatabase : FortuneDatabase
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

    public override async ValueTask<List<Fortune>> LoadAsync(CancellationToken cancellationToken)
    {
        List<Fortune> fortunes = [];
        await _statement.CollectAsync(
            fortunes,
            static (results, row) => results.Add(new Fortune(
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

internal sealed class ApexMySqlFortuneDatabase : FortuneDatabase
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
            fortunes.Add(Utf8Fortune(row.GetInt32(0), row.GetString(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => _pool.DisposeAsync();
}

internal sealed class ApexSqlServerFortuneDatabase : FortuneDatabase
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
            fortunes.Add(Utf8Fortune(reader.GetInt32(0), reader.GetString(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => _pool.DisposeAsync();
}

internal sealed class NpgsqlFortuneDatabase : FortuneDatabase
{
    private const string ParameterizedQuery = Query + " WHERE $1 = 1";
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlFortuneDatabase(string connectionString, int connectionCount)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = connectionCount,
        };
        _dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public override async ValueTask<List<Fortune>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ParameterizedQuery, connection);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = 1 });
        await command.PrepareAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<Fortune> fortunes = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            fortunes.Add(Utf8Fortune(reader.GetInt32(0), reader.GetString(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

internal sealed class MySqlConnectorFortuneDatabase : FortuneDatabase
{
    private const string ParameterizedQuery = Query + " WHERE @one = 1";
    private readonly string _connectionString;

    public MySqlConnectorFortuneDatabase(string connectionString, int connectionCount)
    {
        var builder = new MySqlConnectorDriver.MySqlConnectionStringBuilder(connectionString)
        {
            MaximumPoolSize = (uint)connectionCount,
        };
        _connectionString = builder.ConnectionString;
    }

    public override async ValueTask<List<Fortune>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection =
            new MySqlConnectorDriver.MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command =
            new MySqlConnectorDriver.MySqlCommand(ParameterizedQuery, connection);
        command.Parameters.Add(new MySqlConnectorDriver.MySqlParameter("@one", 1));
        await command.PrepareAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<Fortune> fortunes = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            fortunes.Add(Utf8Fortune(reader.GetInt32(0), reader.GetString(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class MicrosoftDataSqlClientFortuneDatabase : FortuneDatabase
{
    private const string ParameterizedQuery = Query + " WHERE @one = 1";
    private readonly string _connectionString;

    public MicrosoftDataSqlClientFortuneDatabase(string connectionString, int connectionCount)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = connectionCount,
        };
        _connectionString = builder.ConnectionString;
    }

    public override async ValueTask<List<Fortune>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(ParameterizedQuery, connection);
        command.Parameters.Add(new SqlParameter("@one", SqlDbType.Int)
        {
            Value = 1,
        });
        await command.PrepareAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<Fortune> fortunes = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            fortunes.Add(Utf8Fortune(reader.GetInt32(0), reader.GetString(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

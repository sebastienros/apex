using System.Globalization;
using Apex.MsSqlClient;
using Apex.MySqlClient;
using Apex.PgClient;
using Apex.SqlClient;
using Microsoft.Data.SqlClient;
using Npgsql;
using MySqlConnectorDriver = MySqlConnector;

namespace Apex.Fortunes.Platform;

internal abstract class FortuneDatabase : IAsyncDisposable
{
    internal const string Query = "SELECT id, message FROM fortune";

    public abstract ValueTask DisposeAsync();

    public static ValueTask<FortuneDatabase> CreateAsync(
        string? database,
        string? driver,
        string? connectionString)
    {
        var selectedDatabase = RequiredSelection("DATABASE", database);
        var selectedDriver = RequiredSelection("DRIVER", driver);
        var requiredConnectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException("CONNECTION_STRING is required.")
            : connectionString;

        return (selectedDatabase, selectedDriver) switch
        {
            ("postgresql", "apex") =>
                ApexPostgreSqlFortuneDatabase.CreateAsync(requiredConnectionString),
            ("postgresql", "npgsql") =>
                ValueTask.FromResult<FortuneDatabase>(
                    new NpgsqlFortuneDatabase(
                        requiredConnectionString,
                        PositiveEnvironment("APEX_CONNECTIONS", 56))),
            ("mysql", "apex") =>
                ValueTask.FromResult<FortuneDatabase>(
                    new ApexMySqlFortuneDatabase(requiredConnectionString)),
            ("mysql", "mysqlconnector") =>
                ValueTask.FromResult<FortuneDatabase>(
                    new MySqlConnectorFortuneDatabase(requiredConnectionString)),
            ("sqlserver", "apex") =>
                ValueTask.FromResult<FortuneDatabase>(
                    new ApexSqlServerFortuneDatabase(requiredConnectionString)),
            ("sqlserver", "microsoftdatasqlclient") =>
                ValueTask.FromResult<FortuneDatabase>(
                    new MicrosoftDataSqlClientFortuneDatabase(requiredConnectionString)),
            ("postgresql" or "mysql" or "sqlserver", _) =>
                throw new InvalidOperationException(
                    $"DRIVER '{selectedDriver}' is not valid for DATABASE '{selectedDatabase}'."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(database),
                selectedDatabase,
                "DATABASE must be 'postgresql', 'mysql', or 'sqlserver'."),
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

    protected static int PositiveEnvironment(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null)
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0
            ? parsed
            : throw new ArgumentOutOfRangeException(
                name,
                value,
                "Value must be a positive integer.");
    }

    private static string RequiredSelection(string name, string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} is required.")
            : value.Trim().ToLowerInvariant();
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

    private ApexPostgreSqlFortuneDatabase(
        PgPipelinePool pool,
        ISqlPreparedStatement statement)
    {
        _pool = pool;
        _statement = statement;
    }

    public static async ValueTask<FortuneDatabase> CreateAsync(string connectionString)
    {
        var options = CreatePostgreSqlOptions(
            connectionString,
            PositiveEnvironment("APEX_PIPELINING", 64));
        var pool = await PgPipelinePool.CreateAsync(
            options,
            new SqlPipelinePoolOptions
            {
                ConnectionCount = PositiveEnvironment("APEX_CONNECTIONS", 56),
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

internal sealed class ApexMySqlFortuneDatabase : StringFortuneDatabase
{
    private const string PreparedQuery = Query + " WHERE ? = 1";
    private static readonly SqlParameters s_one = SqlParameters.Create(1);
    private readonly MySqlPool _pool;

    public ApexMySqlFortuneDatabase(string connectionString)
    {
        var options = MySqlConnectOptions.Parse(connectionString) with
        {
            CachePreparedStatements = true,
        };
        _pool = MySqlPool.Create(
            options,
            new SqlPoolOptions
            {
                MaximumSize = PositiveEnvironment("APEX_CONNECTIONS", 64),
            });
    }

    public override async ValueTask<List<Fortune>> LoadAsync(
        CancellationToken cancellationToken)
    {
        var rows = await _pool.QueryAsync(PreparedQuery, s_one, cancellationToken);
        List<Fortune> fortunes = [];
        foreach (var row in rows)
        {
            fortunes.Add(new Fortune(row.GetInt32(0), row.GetString(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => _pool.DisposeAsync();
}

internal sealed class ApexSqlServerFortuneDatabase : StringFortuneDatabase
{
    private readonly MsSqlPool _pool;

    public ApexSqlServerFortuneDatabase(string connectionString)
    {
        _pool = MsSqlPool.Create(
            MsSqlConnectOptions.Parse(connectionString),
            new SqlPoolOptions
            {
                MaximumSize = PositiveEnvironment("APEX_CONNECTIONS", 64),
            });
    }

    public override async ValueTask<List<Fortune>> LoadAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _pool.GetConnectionAsync(cancellationToken);
        await using var reader = await connection.ExecuteReaderAsync(
            Query,
            cancellationToken: cancellationToken);
        List<Fortune> fortunes = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            fortunes.Add(new Fortune(reader.GetInt32(0), reader.GetString(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => _pool.DisposeAsync();
}

internal sealed class NpgsqlFortuneDatabase : StringFortuneDatabase
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlFortuneDatabase(string connectionString, int connectionCount)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = connectionCount,
        };
        _dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public override async ValueTask<List<Fortune>> LoadAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(Query, connection);
        await command.PrepareAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<Fortune> fortunes = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            fortunes.Add(new Fortune(reader.GetInt32(0), reader.GetString(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

internal sealed class MySqlConnectorFortuneDatabase : StringFortuneDatabase
{
    private readonly string _connectionString;

    public MySqlConnectorFortuneDatabase(string connectionString)
    {
        var builder = new MySqlConnectorDriver.MySqlConnectionStringBuilder(connectionString)
        {
            MaximumPoolSize = (uint)PositiveEnvironment("APEX_CONNECTIONS", 64),
        };
        _connectionString = builder.ConnectionString;
    }

    public override async ValueTask<List<Fortune>> LoadAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnectorDriver.MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlConnectorDriver.MySqlCommand(Query, connection);
        await command.PrepareAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<Fortune> fortunes = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            fortunes.Add(new Fortune(reader.GetInt32(0), reader.GetString(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class MicrosoftDataSqlClientFortuneDatabase : StringFortuneDatabase
{
    private readonly string _connectionString;

    public MicrosoftDataSqlClientFortuneDatabase(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = PositiveEnvironment("APEX_CONNECTIONS", 64),
        };
        _connectionString = builder.ConnectionString;
    }

    public override async ValueTask<List<Fortune>> LoadAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(Query, connection);
        await command.PrepareAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<Fortune> fortunes = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            fortunes.Add(new Fortune(reader.GetInt32(0), reader.GetString(1)));
        }

        return Complete(fortunes);
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

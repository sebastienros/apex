namespace Apex.SqlClient;

/// <summary>A physical or leased connection to a database server.</summary>
public interface ISqlConnection : ISqlClient
{
    bool IsSecure { get; }

    DatabaseMetadata DatabaseMetadata { get; }

    ValueTask<ISqlPreparedStatement> PrepareAsync(
        string sql,
        CancellationToken cancellationToken = default);

    ValueTask<ISqlRowReader> ExecuteReaderAsync(
        string sql,
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default);

    ValueTask<ISqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}

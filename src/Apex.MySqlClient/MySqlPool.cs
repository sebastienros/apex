using System.Runtime.CompilerServices;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MySqlClient;

/// <summary>A bounded pool of MySQL connections.</summary>
public sealed class MySqlPool : ISqlPool
{
    private readonly SqlConnectionPool<MySqlConnection> _pool;

    private MySqlPool(MySqlConnectOptions connectOptions, SqlPoolOptions poolOptions)
    {
        _pool = new SqlConnectionPool<MySqlConnection>(
          poolOptions,
          cancellationToken => MySqlClient.ConnectAsync(connectOptions, cancellationToken),
          static connection => connection.IsReadyForPool);
    }

    /// <summary>Gets the number of physical connections the pool currently owns.</summary>
    public int Size => _pool.Size;

    /// <summary>Creates a pool for the supplied connection settings.</summary>
    /// <param name="connectOptions">How to reach the server.</param>
    /// <param name="poolOptions">The pool sizing and lifetime policy.</param>
    /// <returns>A new pool.</returns>
    public static MySqlPool Create(
        MySqlConnectOptions connectOptions,
        SqlPoolOptions? poolOptions = null)
    {
        ArgumentNullException.ThrowIfNull(connectOptions);
        return new MySqlPool(connectOptions, poolOptions ?? new SqlPoolOptions());
    }

    /// <summary>Creates a pool from a connection string.</summary>
    /// <param name="connectionString">A <c>mysql://</c> URI or a keyword connection string.</param>
    /// <param name="poolOptions">The pool sizing and lifetime policy.</param>
    /// <returns>A new pool.</returns>
    public static MySqlPool Create(string connectionString, SqlPoolOptions? poolOptions = null) =>
      Create(MySqlConnectOptions.Parse(connectionString), poolOptions);

    /// <inheritdoc />
    public ValueTask<ISqlConnection> GetConnectionAsync(
        CancellationToken cancellationToken = default) =>
      _pool.GetConnectionAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<SqlRowSet> QueryAsync(
        string sql,
        CancellationToken cancellationToken = default) =>
      _pool.QueryAsync(sql, cancellationToken);

    /// <inheritdoc />
    public ValueTask<SqlRowSet> QueryAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken = default) =>
      _pool.QueryAsync(sql, parameters, cancellationToken);

    /// <inheritdoc />
    public ValueTask<SqlCommandResult> ExecuteAsync(
        string sql,
        CancellationToken cancellationToken = default) =>
      _pool.ExecuteAsync(sql, cancellationToken);

    /// <inheritdoc />
    public ValueTask<SqlCommandResult> ExecuteAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken = default) =>
      _pool.ExecuteAsync(sql, parameters, cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<SqlRow> StreamAsync(
        string sql,
        SqlParameters parameters = default,
        int fetchSize = 50,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var row in _pool.StreamAsync(
                         sql,
                         parameters,
                         fetchSize,
                         cancellationToken).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}

using System.Runtime.CompilerServices;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MsSqlClient;

public sealed class MsSqlPool : ISqlPool
{
    private readonly SqlConnectionPool<MsSqlConnection> _pool;

    private MsSqlPool(MsSqlConnectOptions connectOptions, SqlPoolOptions poolOptions)
    {
        _pool = new SqlConnectionPool<MsSqlConnection>(
          poolOptions,
          cancellationToken => MsSqlClient.ConnectAsync(connectOptions, cancellationToken),
          static connection => connection.IsReadyForPool);
    }

    public int Size => _pool.Size;

    public static MsSqlPool Create(
        MsSqlConnectOptions connectOptions,
        SqlPoolOptions? poolOptions = null)
    {
        ArgumentNullException.ThrowIfNull(connectOptions);
        return new MsSqlPool(connectOptions, poolOptions ?? new SqlPoolOptions());
    }

    public ValueTask<ISqlConnection> GetConnectionAsync(
        CancellationToken cancellationToken = default) =>
      _pool.GetConnectionAsync(cancellationToken);

    public ValueTask<SqlRowSet> QueryAsync(
        string sql,
        CancellationToken cancellationToken = default) =>
      _pool.QueryAsync(sql, cancellationToken);

    public ValueTask<SqlRowSet> QueryAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken = default) =>
      _pool.QueryAsync(sql, parameters, cancellationToken);

    public ValueTask<SqlCommandResult> ExecuteAsync(
        string sql,
        CancellationToken cancellationToken = default) =>
      _pool.ExecuteAsync(sql, cancellationToken);

    public ValueTask<SqlCommandResult> ExecuteAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken = default) =>
      _pool.ExecuteAsync(sql, parameters, cancellationToken);

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

    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}

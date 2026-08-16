using System.Runtime.CompilerServices;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.PgClient;

/// <summary>
/// A fixed PostgreSQL connection pool that preserves per-connection pipelining.
/// </summary>
public sealed class PgPipelinePool : ISqlPipelinePool
{
    private readonly SqlPipelinePool _pool;

    private PgPipelinePool(SqlPipelinePool pool)
    {
        _pool = pool;
    }

    public int Size => _pool.Size;

    public static async ValueTask<PgPipelinePool> CreateAsync(
        PgConnectOptions connectOptions,
        SqlPipelinePoolOptions? poolOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectOptions);
        var pool = await SqlPipelinePool.CreateAsync(
            poolOptions ?? new SqlPipelinePoolOptions(),
            async token => await PgClient.ConnectAsync(connectOptions, token)
                .ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return new PgPipelinePool(pool);
    }

    public static ValueTask<PgPipelinePool> CreateAsync(
        string connectionString,
        SqlPipelinePoolOptions? poolOptions = null,
        CancellationToken cancellationToken = default) =>
      CreateAsync(
          PgConnectOptions.Parse(connectionString),
          poolOptions,
          cancellationToken);

    public ValueTask<ISqlPreparedStatement> PrepareAsync(
        string sql,
        CancellationToken cancellationToken = default) =>
      _pool.PrepareAsync(sql, cancellationToken);

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

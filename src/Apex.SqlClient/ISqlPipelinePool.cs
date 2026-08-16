namespace Apex.SqlClient;

/// <summary>
/// A fixed pool that dispatches concurrent operations directly to its connections.
/// </summary>
/// <remarks>
/// Unlike <see cref="ISqlPool"/>, connections are not leased exclusively. Drivers
/// may therefore pipeline multiple operations on each physical connection.
/// </remarks>
public interface ISqlPipelinePool : ISqlClient
{
    int Size { get; }

    ValueTask<ISqlPreparedStatement> PrepareAsync(
        string sql,
        CancellationToken cancellationToken = default);
}

namespace Apex.SqlClient;

/// <summary>A transaction whose disposal rolls back when it has not committed.</summary>
public interface ISqlTransaction : IAsyncDisposable
{
    bool IsCompleted { get; }

    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}

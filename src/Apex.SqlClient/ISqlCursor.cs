namespace Apex.SqlClient;

/// <summary>A progressively fetched result set.</summary>
public interface ISqlCursor : IAsyncDisposable
{
    bool HasMore { get; }

    IReadOnlyList<SqlColumn> Columns { get; }

    ValueTask<SqlRowSet> ReadAsync(int count, CancellationToken cancellationToken = default);
}

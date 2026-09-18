namespace Apex.SqlClient;

/// <summary>A streaming reader that preserves individual result sets.</summary>
public interface ISqlMultiResultReader : ISqlRowReader
{
    /// <summary>Advances to the next result after the current result has been drained.</summary>
    ValueTask<bool> NextResultAsync(CancellationToken cancellationToken = default);
}

/// <summary>A result reader that can establish metadata without losing its first row.</summary>
public interface ISqlResultBoundaryReader : ISqlMultiResultReader
{
    /// <summary>
    /// Establishes the current result's metadata and positions on its first row, if any.
    /// A true return value consumes that row's read notification; its values remain borrowed
    /// until the next read, result transition, or disposal.
    /// </summary>
    ValueTask<bool> InitializeAsync(CancellationToken cancellationToken = default);
}

internal interface ISqlResultReaderConnection
{
    ValueTask<ISqlRowReader> ExecuteResultReaderAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken);
}

internal interface ISqlResultPreparedStatement
{
    ValueTask<ISqlRowReader> ExecuteResultReaderAsync(
        SqlParameters parameters,
        CancellationToken cancellationToken);
}

internal interface ISqlRecordsAffectedReader
{
    int RecordsAffected { get; }
}

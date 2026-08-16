using Apex.SqlClient;

namespace Apex.MySqlClient;

/// <summary>
/// Reports the first failed execution in a prepared batch and the results completed before it.
/// </summary>
public sealed class MySqlBatchException : SqlClientException
{
    internal MySqlBatchException(
        int failedIndex,
        IReadOnlyList<SqlCommandResult> successfulResults,
        Exception innerException)
      : base($"MySQL prepared batch execution {failedIndex} failed.", innerException)
    {
        FailedIndex = failedIndex;
        SuccessfulResults = successfulResults;
    }

    /// <summary>Gets the zero-based index of the first failed parameter set.</summary>
    public int FailedIndex { get; }

    /// <summary>Gets the ordered command results completed before the failure.</summary>
    public IReadOnlyList<SqlCommandResult> SuccessfulResults { get; }
}

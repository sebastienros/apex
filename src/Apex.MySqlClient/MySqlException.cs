using Apex.SqlClient;

namespace Apex.MySqlClient;

/// <summary>An error reported by the MySQL or MariaDB server.</summary>
public sealed class MySqlException : SqlClientException
{
    internal MySqlException(int errorNumber, string? sqlState, string message)
      : base(message)
    {
        ErrorNumber = errorNumber;
        SqlState = sqlState;
    }

    /// <summary>Gets the server error number, for example 1062 for a duplicate key.</summary>
    public int ErrorNumber { get; }

    /// <summary>Gets the five character SQLSTATE, when the server sent one.</summary>
    public string? SqlState { get; }

    /// <summary>
    /// Gets a value indicating whether the error terminated the session so the physical
    /// connection can no longer be used.
    /// </summary>
    public bool IsFatal =>
      ErrorNumber is 1053 or 1077 or 1078 or 1079 or 1080 or 1152 or 1153 or 1159 or
        1160 or 1161 or 1184 or 1927 or 2006 or 2013 or 2014 or 2055 or 4031 ||
      SqlState?.StartsWith("08", StringComparison.Ordinal) == true;

    /// <summary>
    /// Gets a value indicating whether the server aborted the command because it was
    /// interrupted, which is how a cancelled command surfaces.
    /// </summary>
    public bool IsInterrupted => ErrorNumber is 1317 or 3024;
}

/// <summary>
/// Raised on a connection that the driver deliberately discarded, for example because a running
/// command was cancelled and MySQL offered no way to abort it in band.
/// </summary>
internal sealed class MySqlConnectionAbortedException : SqlClientException
{
    internal MySqlConnectionAbortedException(string message)
      : base(message)
    {
    }
}

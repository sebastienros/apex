namespace Apex.PgClient;

internal static class PgErrorClassifier
{
    internal static bool IsTransient(string? sqlState) =>
      sqlState is PgErrorCodes.SerializationFailure or
        PgErrorCodes.DeadlockDetected or
        PgErrorCodes.LockNotAvailable or
        PgErrorCodes.ObjectInUse or
        PgErrorCodes.ObjectNotInPrerequisiteState or
        PgErrorCodes.QueryCanceled or
        PgErrorCodes.AdminShutdown or
        PgErrorCodes.CrashShutdown or
        PgErrorCodes.CannotConnectNow or
        PgErrorCodes.DatabaseDropped or
        PgErrorCodes.IdleSessionTimeout ||
      IsConnectionException(sqlState);

    internal static bool IsFatal(string? sqlState) =>
      sqlState is PgErrorCodes.AdminShutdown or
        PgErrorCodes.CrashShutdown or
        PgErrorCodes.CannotConnectNow or
        PgErrorCodes.DatabaseDropped or
        PgErrorCodes.IdleSessionTimeout ||
      IsConnectionException(sqlState);

    internal static bool IsTransactionAbort(string? sqlState) =>
      sqlState == PgErrorCodes.InFailedSqlTransaction ||
      sqlState?.StartsWith("40", StringComparison.Ordinal) == true;

    private static bool IsConnectionException(string? sqlState) =>
      sqlState?.StartsWith("08", StringComparison.Ordinal) == true;
}

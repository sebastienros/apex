namespace Apex.PgClient;

/// <summary>Common PostgreSQL SQLSTATE error codes.</summary>
public static class PgErrorCodes
{
    public const string ConnectionException = "08000";
    public const string SqlClientUnableToEstablishSqlConnection = "08001";
    public const string ConnectionDoesNotExist = "08003";
    public const string SqlServerRejectedEstablishmentOfSqlConnection = "08004";
    public const string ConnectionFailure = "08006";
    public const string TransactionResolutionUnknown = "08007";
    public const string ProtocolViolation = "08P01";

    public const string InFailedSqlTransaction = "25P02";
    public const string UniqueViolation = "23505";
    public const string TransactionRollback = "40000";
    public const string SerializationFailure = "40001";
    public const string DeadlockDetected = "40P01";
    public const string UndefinedColumn = "42703";
    public const string UndefinedTable = "42P01";
    public const string ObjectNotInPrerequisiteState = "55000";
    public const string ObjectInUse = "55006";
    public const string LockNotAvailable = "55P03";
    public const string QueryCanceled = "57014";
    public const string AdminShutdown = "57P01";
    public const string CrashShutdown = "57P02";
    public const string CannotConnectNow = "57P03";
    public const string DatabaseDropped = "57P04";
    public const string IdleSessionTimeout = "57P05";
}

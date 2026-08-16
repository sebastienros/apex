namespace Apex.MsSqlClient;

public sealed record MsSqlInfo(
    int Number,
    byte State,
    byte Severity,
    string Message,
    string ServerName,
    string ProcedureName,
    int LineNumber);

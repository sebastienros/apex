using Apex.SqlClient;

namespace Apex.MsSqlClient;

public sealed class MsSqlException : SqlClientException
{
    internal MsSqlException(
        int number,
        byte state,
        byte severity,
        string message,
        string serverName,
        string procedureName,
        int lineNumber,
        IReadOnlyList<MsSqlInfo>? errors = null)
      : base(message)
    {
        Number = number;
        State = state;
        Severity = severity;
        ServerName = serverName;
        ProcedureName = procedureName;
        LineNumber = lineNumber;
        Errors = errors ?? Array.Empty<MsSqlInfo>();
    }

    public int Number { get; }

    public byte State { get; }

    public byte Severity { get; }

    public string ServerName { get; }

    public string ProcedureName { get; }

    public int LineNumber { get; }

    public IReadOnlyList<MsSqlInfo> Errors { get; }
}

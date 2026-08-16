namespace Apex.SqlClient;

/// <summary>The outcome of a command that does not require a buffered row result.</summary>
public readonly record struct SqlCommandResult(
    long AffectedRows,
    string CommandTag,
    ulong? LastInsertId,
    uint StatusFlags,
    int WarningCount)
{
    public SqlCommandResult(long affectedRows, string commandTag)
      : this(affectedRows, commandTag, null, 0, 0)
    {
    }

    public void Deconstruct(out long affectedRows, out string commandTag)
    {
        affectedRows = AffectedRows;
        commandTag = CommandTag;
    }
}

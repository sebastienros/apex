using Apex.SqlClient;

namespace Apex.PgClient;

public sealed class PgException : SqlClientException
{
    internal PgException(IReadOnlyDictionary<char, string> fields)
        : base(fields.TryGetValue('M', out var message) ? message : "PostgreSQL error")
    {
        Severity = Get(fields, 'V') ?? Get(fields, 'S');
        SqlState = Get(fields, 'C');
        Detail = Get(fields, 'D');
        Hint = Get(fields, 'H');
        SchemaName = Get(fields, 's');
        TableName = Get(fields, 't');
        ColumnName = Get(fields, 'c');
        DataTypeName = Get(fields, 'd');
        ConstraintName = Get(fields, 'n');
        Position = int.TryParse(Get(fields, 'P'), out var position) ? position : null;
        InternalPosition = int.TryParse(Get(fields, 'p'), out var internalPosition)
          ? internalPosition
          : null;
        InternalQuery = Get(fields, 'q');
        Where = Get(fields, 'W');
        File = Get(fields, 'F');
        Line = Get(fields, 'L');
        Routine = Get(fields, 'R');
    }

    public string? Severity { get; }

    public string? SqlState { get; }

    public string? Detail { get; }

    public string? Hint { get; }

    public int? Position { get; }

    public int? InternalPosition { get; }

    public string? InternalQuery { get; }

    public string? Where { get; }

    public string? SchemaName { get; }

    public string? TableName { get; }

    public string? ColumnName { get; }

    public string? DataTypeName { get; }

    public string? ConstraintName { get; }

    public string? File { get; }

    public string? Line { get; }

    public string? Routine { get; }

    /// <summary>
    /// Gets a value indicating whether retrying the operation may succeed without changing it.
    /// </summary>
    public bool IsTransient => PgErrorClassifier.IsTransient(SqlState);

    /// <summary>
    /// Gets a value indicating whether the server error makes the connection unusable.
    /// </summary>
    public bool IsFatal => PgErrorClassifier.IsFatal(SqlState);

    /// <summary>
    /// Gets a value indicating whether the SQLSTATE reports an aborted or rolled-back transaction.
    /// </summary>
    public bool IsTransactionAbort => PgErrorClassifier.IsTransactionAbort(SqlState);

    private static string? Get(IReadOnlyDictionary<char, string> fields, char key) =>
        fields.TryGetValue(key, out var value) ? value : null;
}

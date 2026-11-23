namespace Apex.MySqlClient.Internal;

/// <summary>
/// Extracts the leading SQL keyword used as the diagnostic operation name. Common keywords map to
/// shared constants so routine commands do not allocate two strings each.
/// </summary>
internal static class MySqlOperationName
{
    private static readonly string[] s_known =
    [
        "SELECT",
        "INSERT",
        "UPDATE",
        "DELETE",
        "REPLACE",
        "WITH",
        "CALL",
        "SET",
        "SHOW",
        "BEGIN",
        "COMMIT",
        "ROLLBACK",
        "START",
        "CREATE",
        "DROP",
        "ALTER",
        "TRUNCATE",
        "DO",
        "LOAD",
        "EXPLAIN",
    ];

    internal static string Get(string sql)
    {
        var text = sql.AsSpan().TrimStart();
        var separator = text.IndexOfAny(" \t\r\n");
        var keyword = separator < 0 ? text : text[..separator];
        foreach (var known in s_known)
        {
            if (keyword.Equals(known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return keyword.ToString().ToUpperInvariant();
    }
}

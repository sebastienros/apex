namespace Apex.SqlClient.Internal;

internal static class ConnectionStringQueryParser
{
    internal static IReadOnlyDictionary<string, string> Parse(string query)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split(
                   '&',
                   StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            var value = separator < 0 ? string.Empty : part[(separator + 1)..];
            values[Decode(key)] = Decode(value);
        }

        return values;
    }

    private static string Decode(string value) =>
      Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));
}

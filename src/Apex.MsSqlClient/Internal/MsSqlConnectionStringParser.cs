using Apex.SqlClient.Internal;

namespace Apex.MsSqlClient.Internal;

internal static class MsSqlConnectionStringParser
{
    internal static IReadOnlyDictionary<string, string> Parse(string connectionString)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        while (position < connectionString.Length)
        {
            SkipWhitespaceAndSeparators(connectionString, ref position);
            if (position == connectionString.Length)
            {
                break;
            }

            var equals = connectionString.IndexOf('=', position);
            if (equals < 0)
            {
                throw new FormatException("SQL Server connection options must use key=value syntax.");
            }

            var key = connectionString[position..equals].Trim();
            if (key.Length == 0)
            {
                throw new FormatException("SQL Server connection option name cannot be empty.");
            }

            position = equals + 1;
            var value = ReadValue(connectionString, ref position);
            values[key] = value;
        }

        return values;
    }

    internal static IReadOnlyDictionary<string, string> ParseQuery(string query) =>
      ConnectionStringQueryParser.Parse(query);

    private static string ReadValue(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        if (position == text.Length)
        {
            return string.Empty;
        }

        var quote = text[position];
        if (quote is '\'' or '"' or '{')
        {
            position++;
            var terminator = quote == '{' ? '}' : quote;
            System.Text.StringBuilder value = new();
            while (position < text.Length)
            {
                var current = text[position++];
                if (current == terminator)
                {
                    if (position < text.Length && text[position] == terminator)
                    {
                        value.Append(terminator);
                        position++;
                        continue;
                    }

                    while (position < text.Length && char.IsWhiteSpace(text[position]))
                    {
                        position++;
                    }

                    if (position < text.Length && text[position] != ';')
                    {
                        throw new FormatException("Unexpected characters after quoted SQL Server option.");
                    }

                    return value.ToString();
                }

                value.Append(current);
            }

            throw new FormatException("Unterminated quoted SQL Server connection option.");
        }

        var end = text.IndexOf(';', position);
        if (end < 0)
        {
            end = text.Length;
        }

        var result = text[position..end].Trim();
        position = end;
        return result;
    }

    private static void SkipWhitespaceAndSeparators(string text, ref int position)
    {
        while (position < text.Length &&
               (char.IsWhiteSpace(text[position]) || text[position] == ';'))
        {
            position++;
        }
    }
}

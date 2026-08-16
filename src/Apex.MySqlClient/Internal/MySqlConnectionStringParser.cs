using System.Text;
using Apex.SqlClient.Internal;

namespace Apex.MySqlClient.Internal;

/// <summary>Parses the keyword and URI query forms of a MySQL connection string.</summary>
internal static class MySqlConnectionStringParser
{
    /// <summary>Parses a semicolon separated <c>key=value</c> connection string.</summary>
    internal static IReadOnlyDictionary<string, string> ParseKeywords(string connectionString)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        var input = connectionString.AsSpan();
        var position = 0;
        while (position < input.Length)
        {
            SkipWhitespace(input, ref position);
            while (position < input.Length && input[position] == ';')
            {
                position++;
                SkipWhitespace(input, ref position);
            }

            if (position == input.Length)
            {
                break;
            }

            var keyStart = position;
            while (position < input.Length && input[position] != '=')
            {
                position++;
            }

            if (position == input.Length)
            {
                throw new FormatException(
                  $"MySQL connection-string key '{input[keyStart..].Trim().ToString()}' has no value.");
            }

            var key = input[keyStart..position].Trim().ToString();
            if (key.Length == 0)
            {
                throw new FormatException("MySQL connection-string key is empty.");
            }

            position++;
            SkipWhitespace(input, ref position);
            var value = position < input.Length && input[position] is '\'' or '"'
              ? ParseQuoted(input, ref position)
              : ParseUnquoted(input, ref position);
            values[key] = value;
        }

        return values;
    }

    /// <summary>Parses the query component of a <c>mysql://</c> or <c>mariadb://</c> URI.</summary>
    internal static IReadOnlyDictionary<string, string> ParseQuery(string query)
      => ConnectionStringQueryParser.Parse(query);

    private static string ParseQuoted(ReadOnlySpan<char> input, ref int position)
    {
        var quote = input[position++];
        StringBuilder value = new();
        while (position < input.Length)
        {
            var current = input[position++];
            if (current == quote)
            {
                if (position < input.Length && input[position] == quote)
                {
                    position++;
                    value.Append(quote);
                    continue;
                }

                SkipWhitespace(input, ref position);
                if (position < input.Length && input[position] != ';')
                {
                    throw new FormatException(
                      "MySQL quoted connection-string value must be followed by a semicolon.");
                }

                if (position < input.Length)
                {
                    position++;
                }

                return value.ToString();
            }

            value.Append(current);
        }

        throw new FormatException("MySQL quoted connection-string value is unterminated.");
    }

    private static string ParseUnquoted(ReadOnlySpan<char> input, ref int position)
    {
        var start = position;
        while (position < input.Length && input[position] != ';')
        {
            position++;
        }

        var value = input[start..position];
        if (position < input.Length)
        {
            position++;
        }

        return value.TrimEnd().ToString();
    }

    private static void SkipWhitespace(ReadOnlySpan<char> input, ref int position)
    {
        while (position < input.Length && char.IsWhiteSpace(input[position]))
        {
            position++;
        }
    }

}

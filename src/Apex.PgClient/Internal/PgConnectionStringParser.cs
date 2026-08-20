namespace Apex.PgClient.Internal;

internal static class PgConnectionStringParser
{
    public static IReadOnlyDictionary<string, string> ParseKeywords(string connectionString)
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
            while (position < input.Length &&
                input[position] != '=' &&
                input[position] != ';' &&
                !char.IsWhiteSpace(input[position]))
            {
                position++;
            }

            if (position == keyStart)
            {
                throw new FormatException("PostgreSQL connection-string key is empty.");
            }

            var key = input[keyStart..position].ToString();
            SkipWhitespace(input, ref position);
            if (position == input.Length || input[position++] != '=')
            {
                throw new FormatException($"PostgreSQL connection-string key '{key}' has no value.");
            }

            SkipWhitespace(input, ref position);
            var value = position < input.Length && input[position] is '\'' or '"'
              ? ParseQuoted(input, ref position, input[position])
              : ParseUnquoted(input, ref position);
            values[key] = value;
        }

        return values;
    }

    private static string ParseQuoted(
        ReadOnlySpan<char> input,
        ref int position,
        char quote)
    {
        position++;
        System.Text.StringBuilder value = new();
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

                return value.ToString();
            }

            if (current == '\\')
            {
                if (position == input.Length)
                {
                    throw new FormatException("PostgreSQL quoted connection-string value has a trailing escape.");
                }

                current = input[position++];
            }

            value.Append(current);
        }

        throw new FormatException("PostgreSQL quoted connection-string value is unterminated.");
    }

    private static string ParseUnquoted(ReadOnlySpan<char> input, ref int position)
    {
        System.Text.StringBuilder value = new();
        while (position < input.Length &&
            input[position] != ';' &&
            !char.IsWhiteSpace(input[position]))
        {
            var current = input[position++];
            if (current == '\\')
            {
                if (position == input.Length)
                {
                    throw new FormatException("PostgreSQL connection-string value has a trailing escape.");
                }

                current = input[position++];
            }

            value.Append(current);
        }

        return value.ToString();
    }

    private static void SkipWhitespace(ReadOnlySpan<char> input, ref int position)
    {
        while (position < input.Length && char.IsWhiteSpace(input[position]))
        {
            position++;
        }
    }
}

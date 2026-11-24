namespace Apex.PgClient.Internal;

internal static class PgArrayParser
{
    public static string?[] Parse(string value, char delimiter = ',')
    {
        var input = value.AsSpan();
        // Only a leading dimension decoration such as "[1:3]=" is skipped; '=' inside elements
        // (for example {a=b}) is ordinary data.
        if (!input.IsEmpty && input[0] == '[')
        {
            var dimensions = input.IndexOf('=');
            if (dimensions >= 0)
            {
                input = input[(dimensions + 1)..];
            }
        }

        if (input.Length < 2 || input[0] != '{' || input[^1] != '}')
        {
            throw new FormatException("Invalid PostgreSQL array.");
        }

        input = input[1..^1];
        if (input.IsEmpty)
        {
            return [];
        }

        List<string?> values = [];
        var position = 0;
        var expectingElement = false;
        while (position < input.Length)
        {
            expectingElement = false;
            if (input[position] == '{')
            {
                throw new NotSupportedException("Multidimensional PostgreSQL arrays are not supported yet.");
            }

            var quoted = input[position] == '"';
            if (quoted)
            {
                position++;
            }

            System.Text.StringBuilder item = new();
            var closed = !quoted;
            while (position < input.Length)
            {
                var current = input[position++];
                if (current == '\\')
                {
                    if (position == input.Length)
                    {
                        throw new FormatException("PostgreSQL array has a trailing escape.");
                    }

                    item.Append(input[position++]);
                    continue;
                }

                if (quoted && current == '"')
                {
                    closed = true;
                    break;
                }

                if (!quoted && current == delimiter)
                {
                    position--;
                    break;
                }

                item.Append(current);
            }

            if (!closed)
            {
                throw new FormatException("PostgreSQL array has an unterminated quoted value.");
            }

            var parsed = item.ToString();
            if (!quoted && parsed.Length == 0)
            {
                throw new FormatException("PostgreSQL array contains an empty unquoted value.");
            }

            values.Add(!quoted && parsed == "NULL" ? null : parsed);
            if (position < input.Length)
            {
                if (input[position++] != delimiter)
                {
                    throw new FormatException("PostgreSQL array values must be comma-separated.");
                }

                expectingElement = true;
            }
        }

        if (expectingElement)
        {
            throw new FormatException("PostgreSQL array has a trailing delimiter.");
        }

        return values.ToArray();
    }
}

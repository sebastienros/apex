namespace Apex.PgClient.Internal;

internal static class PgArrayParser
{
    public static string?[] Parse(string value, char delimiter = ',')
    {
        var input = value.AsSpan();
        var dimensions = input.IndexOf('=');
        if (dimensions >= 0)
        {
            input = input[(dimensions + 1)..];
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
        while (position < input.Length)
        {
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
            values.Add(!quoted && parsed == "NULL" ? null : parsed);
            if (position < input.Length)
            {
                if (input[position++] != delimiter)
                {
                    throw new FormatException("PostgreSQL array values must be comma-separated.");
                }
            }
        }

        return values.ToArray();
    }
}

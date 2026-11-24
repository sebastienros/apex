using System.Text;

namespace Apex.PgClient;

/// <summary>
/// Accepts ADO.NET-style <c>Key=Value;</c> connection strings in addition to the libpq keyword
/// strings and URIs understood by <see cref="PgConnectOptions.Parse(string)"/>.
/// </summary>
/// <remarks>
/// The native parser keeps libpq semantics (whitespace-separated, <c>;</c> is an ordinary value
/// character). Only this adapter translates ADO.NET syntax, by re-emitting it as an equivalent
/// libpq keyword string, so the native driver's parsing is unaffected by ADO.NET conventions.
/// </remarks>
internal static class PgAdoConnectionString
{
    internal static PgConnectOptions Parse(string connectionString) =>
        string.IsNullOrWhiteSpace(connectionString)
            ? new PgConnectOptions()
            : PgConnectOptions.Parse(ToNative(connectionString));

    internal static string ToNative(string connectionString)
    {
        if (IsUri(connectionString) || !IsAdoStyle(connectionString))
        {
            return connectionString;
        }

        StringBuilder result = new(connectionString.Length + 16);
        var input = connectionString.AsSpan();
        var position = 0;
        while (position < input.Length)
        {
            SkipSeparators(input, ref position);
            if (position == input.Length)
            {
                break;
            }

            var keyStart = position;
            while (position < input.Length && input[position] is not ('=' or ';'))
            {
                position++;
            }

            var key = input[keyStart..position].Trim();
            if (key.IsEmpty)
            {
                throw new FormatException("PostgreSQL connection-string key is empty.");
            }

            if (position == input.Length || input[position] != '=')
            {
                throw new FormatException("PostgreSQL connection-string key has no value.");
            }

            position++;
            SkipWhitespace(input, ref position);
            var value = position < input.Length && input[position] is '\'' or '"'
                ? ParseQuoted(input, ref position)
                : ParseUnquoted(input, ref position);

            if (result.Length > 0)
            {
                result.Append(' ');
            }

            result.Append(key).Append("='");
            foreach (var current in value)
            {
                if (current is '\\' or '\'')
                {
                    result.Append('\\');
                }

                result.Append(current);
            }

            result.Append('\'');
        }

        return result.ToString();
    }

    private static bool IsUri(string connectionString) =>
        connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

    private static bool IsAdoStyle(string connectionString)
    {
        var input = connectionString.AsSpan();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var sawDoubleQuotedValue = false;
        for (var i = 0; i < input.Length; i++)
        {
            var current = input[i];
            if (inSingleQuote)
            {
                if (current == '\\' && i + 1 < input.Length)
                {
                    i++;
                }
                else if (current == '\'')
                {
                    inSingleQuote = false;
                }
            }
            else if (inDoubleQuote)
            {
                if (current == '"')
                {
                    if (i + 1 < input.Length && input[i + 1] == '"')
                    {
                        i++;
                    }
                    else
                    {
                        inDoubleQuote = false;
                    }
                }
            }
            else if (current == ';')
            {
                return true;
            }
            else if (current == '\'')
            {
                inSingleQuote = true;
            }
            else if (current == '"')
            {
                inDoubleQuote = true;
                sawDoubleQuotedValue = true;
            }
        }

        return sawDoubleQuotedValue;
    }

    // ADO.NET quoting: the value is wrapped in ' or ", and the quote character is doubled to
    // include it literally. Backslashes have no special meaning.
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
                    throw new FormatException("PostgreSQL connection-string quoted value is followed by unexpected text.");
                }

                return value.ToString();
            }

            value.Append(current);
        }

        throw new FormatException("PostgreSQL connection-string quoted value is unterminated.");
    }

    private static string ParseUnquoted(ReadOnlySpan<char> input, ref int position)
    {
        var start = position;
        while (position < input.Length && input[position] != ';')
        {
            position++;
        }

        return input[start..position].Trim().ToString();
    }

    private static void SkipSeparators(ReadOnlySpan<char> input, ref int position)
    {
        while (position < input.Length && (input[position] == ';' || char.IsWhiteSpace(input[position])))
        {
            position++;
        }
    }

    private static void SkipWhitespace(ReadOnlySpan<char> input, ref int position)
    {
        while (position < input.Length && char.IsWhiteSpace(input[position]))
        {
            position++;
        }
    }
}

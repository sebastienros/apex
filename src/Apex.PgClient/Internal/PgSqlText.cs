namespace Apex.PgClient.Internal;

/// <summary>
/// Lexes PostgreSQL command text just far enough to find top-level statement boundaries and the leading
/// keyword. It skips standard and escape (<c>E'...'</c>) string literals, quoted identifiers, dollar-quoted
/// strings, and line and nested block comments. It assumes <c>standard_conforming_strings</c> is on, the
/// server default: with it off, a backslash before a quote in an ordinary literal can make it count a
/// semicolon inside the literal. A miscount only selects a protocol; it never rewrites or splits SQL.
/// </summary>
internal static class PgSqlText
{
    /// <summary>Returns whether the text contains more than one non-empty statement.</summary>
    internal static bool HasMultipleStatements(string sql) => CountStatements(sql, limit: 2) > 1;

    /// <summary>Returns whether the text contains exactly one non-empty statement.</summary>
    internal static bool IsSingleStatement(string sql) => CountStatements(sql, limit: 2) == 1;

    /// <summary>Returns the first keyword after leading whitespace and comments, or an empty span.</summary>
    internal static ReadOnlySpan<char> GetLeadingKeyword(string sql)
    {
        var text = sql.AsSpan();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && Next(text, i) == '-')
            {
                i = SkipLineComment(text, i);
            }
            else if (c == '/' && Next(text, i) == '*')
            {
                i = SkipBlockComment(text, i);
            }
            else
            {
                break;
            }
        }

        var start = i;
        while (i < text.Length && char.IsAsciiLetter(text[i]))
        {
            i++;
        }

        return text[start..i];
    }

    private static int CountStatements(ReadOnlySpan<char> sql, int limit)
    {
        var count = 0;
        var hasContent = false;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            switch (c)
            {
                case ';':
                    if (hasContent)
                    {
                        if (++count >= limit) return count;
                        hasContent = false;
                    }

                    i++;
                    break;
                case '-' when Next(sql, i) == '-':
                    i = SkipLineComment(sql, i);
                    break;
                case '/' when Next(sql, i) == '*':
                    i = SkipBlockComment(sql, i);
                    break;
                case '\'':
                    hasContent = true;
                    i = SkipQuoted(sql, i, '\'', backslashEscapes: IsEscapeStringPrefix(sql, i));
                    break;
                case '"':
                    hasContent = true;
                    i = SkipQuoted(sql, i, '"', backslashEscapes: false);
                    break;
                case '$':
                    hasContent = true;
                    i = TryGetDollarTagLength(sql, i, out var tagLength)
                        ? SkipDollarQuoted(sql, i, tagLength)
                        : i + 1;
                    break;
                default:
                    hasContent |= !char.IsWhiteSpace(c);
                    i++;
                    break;
            }
        }

        return hasContent ? count + 1 : count;
    }

    private static char Next(ReadOnlySpan<char> sql, int i) => i + 1 < sql.Length ? sql[i + 1] : '\0';

    private static int SkipLineComment(ReadOnlySpan<char> sql, int i)
    {
        var end = sql[i..].IndexOfAny('\n', '\r');
        return end < 0 ? sql.Length : i + end + 1;
    }

    private static int SkipBlockComment(ReadOnlySpan<char> sql, int i)
    {
        var depth = 1;
        i += 2;
        while (i < sql.Length)
        {
            if (sql[i] == '/' && Next(sql, i) == '*')
            {
                depth++;
                i += 2;
            }
            else if (sql[i] == '*' && Next(sql, i) == '/')
            {
                i += 2;
                if (--depth == 0) return i;
            }
            else
            {
                i++;
            }
        }

        return sql.Length;
    }

    private static int SkipQuoted(ReadOnlySpan<char> sql, int i, char quote, bool backslashEscapes)
    {
        var j = i + 1;
        while (j < sql.Length)
        {
            var c = sql[j];
            if (backslashEscapes && c == '\\')
            {
                j += 2;
            }
            else if (c == quote)
            {
                if (Next(sql, j) != quote) return j + 1;
                j += 2;
            }
            else
            {
                j++;
            }
        }

        return sql.Length;
    }

    // E'...' (or e'...') is an escape string unless the E ends a longer identifier.
    private static bool IsEscapeStringPrefix(ReadOnlySpan<char> sql, int quote) =>
        quote > 0 &&
        sql[quote - 1] is 'E' or 'e' &&
        (quote == 1 || !IsIdentifierChar(sql[quote - 2]));

    // A dollar quote opens with $$ or $tag$, where tag is an identifier without '$'. A '$' that continues
    // an identifier (foo$bar) or starts a positional parameter ($1) opens nothing.
    private static bool TryGetDollarTagLength(ReadOnlySpan<char> sql, int i, out int length)
    {
        length = 0;
        if (i > 0 && IsIdentifierChar(sql[i - 1])) return false;
        var j = i + 1;
        if (j < sql.Length && sql[j] != '$')
        {
            if (!(char.IsLetter(sql[j]) || sql[j] == '_')) return false;
            while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
        }

        if (j >= sql.Length || sql[j] != '$') return false;
        length = j - i + 1;
        return true;
    }

    private static int SkipDollarQuoted(ReadOnlySpan<char> sql, int i, int tagLength)
    {
        var tag = sql.Slice(i, tagLength);
        var bodyStart = i + tagLength;
        var end = sql[bodyStart..].IndexOf(tag);
        return end < 0 ? sql.Length : bodyStart + end + tagLength;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';
}

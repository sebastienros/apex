namespace Apex.PgClient;

public enum PgSslMode
{
    Disable,
    Allow,
    Prefer,
    Require,
    VerifyCa,
    VerifyFull,
}

public enum PgSslNegotiation
{
    Postgres,
    Direct,
}

public enum PgChannelBinding
{
    Disable,
    Prefer,
    Require,
}

internal static class PgEnumParser
{
    public static T Parse<T>(string value)
        where T : struct, Enum
    {
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse(normalized, ignoreCase: true, out T result)
            ? result
            : throw new ArgumentException($"Unknown {typeof(T).Name} value '{value}'.", nameof(value));
    }
}

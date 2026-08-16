namespace Apex.PgClient;

public sealed record PgProxyOptions
{
    public required PgProxyType Type { get; init; }

    public required string Host { get; init; }

    public required int Port { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }
}

public enum PgProxyType
{
    HttpConnect,
    Socks4a,
    Socks5,
}

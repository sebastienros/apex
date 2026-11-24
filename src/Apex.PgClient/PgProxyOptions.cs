using System.Text;

namespace Apex.PgClient;

public sealed record PgProxyOptions
{
    public required PgProxyType Type { get; init; }

    public required string Host { get; init; }

    public required int Port { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Type = ").Append(Type)
            .Append(", Host = ").Append(Host)
            .Append(", Port = ").Append(Port)
            .Append(", Username = ").Append(Username)
            .Append(", Password = ").Append(string.IsNullOrEmpty(Password) ? string.Empty : "***");
        return true;
    }
}

public enum PgProxyType
{
    HttpConnect,
    Socks4a,
    Socks5,
}

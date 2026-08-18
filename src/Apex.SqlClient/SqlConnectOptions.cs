namespace Apex.SqlClient;

public abstract record SqlConnectOptions
{
    public string Host { get; init; } = "localhost";

    public int Port { get; init; }

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public SqlAuthenticationProvider? AuthenticationProvider { get; init; }

    public string Database { get; init; } = string.Empty;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public int ReconnectAttempts { get; init; }

    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets a value indicating whether .NET 11's experimental low-level TLS state machine
    /// should be used instead of <see cref="System.Net.Security.SslStream"/>.
    /// </summary>
    /// <remarks>
    /// This option is disabled by default and requires .NET 11 or later.
    /// </remarks>
    public bool UseExperimentalLowLevelTls { get; init; }

    public bool CachePreparedStatements { get; init; }

    public int PreparedStatementCacheSize { get; init; } = 256;

    public int PreparedStatementCacheSqlLengthLimit { get; init; } = 2048;
}

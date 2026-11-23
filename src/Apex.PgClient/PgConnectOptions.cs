using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Apex.PgClient.Internal;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.PgClient;

public sealed record PgConnectOptions : SqlConnectOptions
{
    // Keys Apply maps to driver options, used to suggest a fix for a misspelled key.
    private static readonly string[] s_driverKeys =
    [
        "host", "port", "user", "username", "password", "database", "dbname", "sslmode",
        "sslnegotiation", "channel_binding", "pipelininglimit", "useexperimentallowleveltls",
        "cachepreparedstatements",
    ];

    // Server parameters accepted directly as connection-string keys, sent in the startup
    // message under these canonical names. Any other server parameter goes through
    // options='-c name=value' or the Properties option, so that a misspelled driver key (or a
    // secret under a misspelled key) is rejected instead of being sent to the server.
    private static readonly string[] s_startupParameters =
    [
        "application_name", "client_encoding", "options", "search_path", "TimeZone",
        "DateStyle", "IntervalStyle", "extra_float_digits",
    ];

    public PgConnectOptions()
    {
        Port = 5432;
        Username = "user";
        Password = "pass";
        Database = "db";
    }

    public int PipeliningLimit { get; init; } = 256;

    public int StringCacheCapacity { get; init; } = 1024;

    public int StringCacheMaximumByteLength { get; init; } = 64;

    public int Utf8BytesCacheCapacity { get; init; } = 1024;

    public int Utf8BytesCacheMaximumByteLength { get; init; } = 1024;

    public PgSslMode SslMode { get; init; } = PgSslMode.Disable;

    public PgSslNegotiation SslNegotiation { get; init; } = PgSslNegotiation.Postgres;

    public PgChannelBinding ChannelBinding { get; init; } = PgChannelBinding.Prefer;

    public bool UseLayer7Proxy { get; init; }

    public PgProxyOptions? Proxy { get; init; }

    public RemoteCertificateValidationCallback? CertificateValidationCallback { get; init; }

    public IReadOnlyList<X509Certificate2> ClientCertificates { get; init; } =
      Array.Empty<X509Certificate2>();

    public X509RevocationMode CertificateRevocationCheckMode { get; init; } =
      X509RevocationMode.NoCheck;

    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["application_name"] = "apex-pg-client",
            ["client_encoding"] = "UTF8",
            ["DateStyle"] = "ISO",
            ["IntervalStyle"] = "iso_8601",
            ["extra_float_digits"] = "2",
        };

    public PgTypeRegistry? TypeRegistry { get; init; }

    public static PgConnectOptions FromEnvironment()
    {
        PgConnectOptions options = new();
        return options with
        {
            Host = GetEnvironment("PGHOSTADDR") ?? GetEnvironment("PGHOST") ?? options.Host,
            Port = ParsePort(GetEnvironment("PGPORT"), options.Port),
            Database = GetEnvironment("PGDATABASE") ?? options.Database,
            Username = GetEnvironment("PGUSER") ?? options.Username,
            Password = GetEnvironment("PGPASSWORD") ?? options.Password,
            SslMode = ParseSslMode(GetEnvironment("PGSSLMODE"), options.SslMode),
            SslNegotiation = ParseSslNegotiation(
                GetEnvironment("PGSSLNEGOTIATION"),
                options.SslNegotiation),
            ChannelBinding = ParseChannelBinding(
                GetEnvironment("PGCHANNELBINDING"),
                options.ChannelBinding),
        };
    }

    public static PgConnectOptions Parse(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
               connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
          ? ParseUri(connectionString)
          : Apply(new PgConnectOptions(), PgConnectionStringParser.ParseKeywords(connectionString));
    }

    private static PgConnectOptions ParseUri(string connectionString)
    {
        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("postgres" or "postgresql"))
        {
            throw new FormatException("Invalid PostgreSQL connection URI.");
        }

        PgConnectOptions options = new()
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
        };
        if (options.Database.Length == 0)
        {
            options = options with { Database = new PgConnectOptions().Database };
        }

        if (uri.UserInfo.Length > 0)
        {
            var separator = uri.UserInfo.IndexOf(':');
            options = options with
            {
                Username = Uri.UnescapeDataString(
                separator < 0 ? uri.UserInfo : uri.UserInfo[..separator]),
                Password = separator < 0
                ? string.Empty
                : Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]),
            };
        }

        return Apply(options, ConnectionStringQueryParser.Parse(uri.Query));
    }

    private static PgConnectOptions Apply(
        PgConnectOptions options,
        IReadOnlyDictionary<string, string> values)
    {
        Dictionary<string, string> properties =
          new(options.Properties, StringComparer.Ordinal);
        foreach ((var key, var value) in values)
        {
            switch (key.ToLowerInvariant())
            {
                case "host":
                    options = options with { Host = value };
                    break;
                case "port":
                    options = options with { Port = ParsePort(value, options.Port) };
                    break;
                case "user":
                case "username":
                    options = options with { Username = value };
                    break;
                case "password":
                    options = options with { Password = value };
                    break;
                case "database":
                case "dbname":
                    options = options with { Database = value };
                    break;
                case "sslmode":
                    options = options with { SslMode = ParseSslMode(value, options.SslMode) };
                    break;
                case "sslnegotiation":
                    options = options with
                    {
                        SslNegotiation = ParseSslNegotiation(value, options.SslNegotiation),
                    };
                    break;
                case "channel_binding":
                case "channelbinding":
                    options = options with
                    {
                        ChannelBinding = ParseChannelBinding(value, options.ChannelBinding),
                    };
                    break;
                case "pipelininglimit":
                    options = options with
                    {
                        PipeliningLimit = ParsePositiveInt(value, key),
                    };
                    break;
                case "useexperimentallowleveltls":
                    options = options with
                    {
                        UseExperimentalLowLevelTls = bool.Parse(value),
                    };
                    break;
                case "cachepreparedstatements":
                    options = options with
                    {
                        CachePreparedStatements = bool.Parse(value),
                    };
                    break;
                default:
                    properties[GetStartupParameterName(key)] = value;
                    break;
            }
        }

        return options with { Properties = properties };
    }

    private static string GetStartupParameterName(string key)
    {
        foreach (var name in s_startupParameters)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        // The message names the key but never its value, which may be a secret.
        var suggestion = FindSuggestion(key);
        throw new FormatException(
            $"Unsupported PostgreSQL connection option '{key}'." +
            (suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?") +
            " To send another server parameter at startup, use options='-c name=value'.");
    }

    private static string? FindSuggestion(string key)
    {
        if (key.Length < 4)
        {
            return null;
        }

        var lowered = key.ToLowerInvariant();
        string? best = null;
        var bestDistance = 3;
        foreach (var candidate in s_driverKeys.Concat(s_startupParameters))
        {
            var distance = EditDistance(lowered, candidate.ToLowerInvariant());
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static int EditDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var substitution = previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j], current[j - 1]) + 1);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static string? GetEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static int ParsePort(string? value, int fallback) =>
        value is null
            ? fallback
            : int.TryParse(value, out var port) && port is > 0 and <= ushort.MaxValue
                ? port
                : throw new FormatException($"Invalid PostgreSQL port '{value}'.");

    private static PgSslMode ParseSslMode(string? value, PgSslMode fallback) =>
        value is null ? fallback : PgEnumParser.Parse<PgSslMode>(value);

    private static PgSslNegotiation ParseSslNegotiation(string? value, PgSslNegotiation fallback) =>
        value is null ? fallback : PgEnumParser.Parse<PgSslNegotiation>(value);

    private static PgChannelBinding ParseChannelBinding(string? value, PgChannelBinding fallback) =>
        value is null ? fallback : PgEnumParser.Parse<PgChannelBinding>(value);

    private static int ParsePositiveInt(string value, string name) =>
      int.TryParse(value, out var parsed) && parsed > 0
        ? parsed
        : throw new FormatException($"Invalid PostgreSQL {name} value '{value}'.");
}

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Apex.MySqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MySqlClient;

/// <summary>Describes how to reach and authenticate against a MySQL or MariaDB server.</summary>
public sealed record MySqlConnectOptions : SqlConnectOptions
{
    /// <summary>Initializes a new instance of the <see cref="MySqlConnectOptions"/> class.</summary>
    public MySqlConnectOptions()
    {
        Port = 3306;
        Username = "root";
        Password = string.Empty;
        Database = string.Empty;
    }

    /// <summary>
    /// Gets the number of commands that may be in flight on one connection. MySQL replies in
    /// submission order, so pipelining is safe, but the conservative default of one keeps error
    /// attribution and cancellation simple.
    /// </summary>
    public int PipeliningLimit { get; init; } = 1;

    /// <summary>Gets the number of decoded strings cached per connection.</summary>
    public int StringCacheCapacity { get; init; } = 1024;

    /// <summary>Gets the largest UTF-8 length that may enter the string cache.</summary>
    public int StringCacheMaximumByteLength { get; init; } = 64;

    /// <summary>Gets the TLS negotiation mode.</summary>
    public MySqlSslMode SslMode { get; init; } = MySqlSslMode.Preferred;

    /// <summary>Gets the authentication plugin offered to the server.</summary>
    public MySqlAuthenticationPlugin AuthenticationPlugin { get; init; } =
      MySqlAuthenticationPlugin.Default;

    /// <summary>
    /// Gets a value indicating whether the driver may send the password in the clear. It is only
    /// honoured over TLS and is required for <c>mysql_clear_password</c>.
    /// </summary>
    public bool AllowCleartextPassword { get; init; }

    /// <summary>
    /// Gets a value indicating whether the driver may ask an unencrypted connection for the
    /// server RSA public key during SHA-2 full authentication. Supplying
    /// <see cref="ServerRsaPublicKey"/> instead removes the trust-on-first-use exposure.
    /// </summary>
    public bool AllowPublicKeyRetrieval { get; init; }

    /// <summary>Gets the PEM encoded server RSA public key used for SHA-2 full authentication.</summary>
    public string? ServerRsaPublicKey { get; init; }

    /// <summary>
    /// Gets a value indicating whether DML reports only changed rows. The default reports matched
    /// rows.
    /// </summary>
    public bool UseAffectedRows { get; init; }

    /// <summary>Gets a value indicating whether one command may contain several statements.</summary>
    public bool AllowMultiStatements { get; init; }

    /// <summary>Gets a value indicating whether the server may request a local file upload.</summary>
    public bool AllowLoadLocalInfile { get; init; }

    /// <summary>Gets the collation identifier sent during the handshake, utf8mb4 by default.</summary>
    public byte Collation { get; init; } = MySqlProtocol.Utf8Mb4Collation;

    /// <summary>Gets how <c>0000-00-00</c> values are surfaced.</summary>
    public MySqlZeroDateBehavior ZeroDateBehavior { get; init; } = MySqlZeroDateBehavior.Error;

    /// <summary>Gets how a command that already reached the server is cancelled.</summary>
    public MySqlQueryCancellation QueryCancellation { get; init; } =
      MySqlQueryCancellation.KillQuery;

    /// <summary>Gets the callback that validates the server certificate.</summary>
    public RemoteCertificateValidationCallback? CertificateValidationCallback { get; init; }

    /// <summary>Gets the client certificates offered during the TLS handshake.</summary>
    public IReadOnlyList<X509Certificate2> ClientCertificates { get; init; } =
      Array.Empty<X509Certificate2>();

    /// <summary>Gets the revocation policy applied to the server certificate chain.</summary>
    public X509RevocationMode CertificateRevocationCheckMode { get; init; } =
      X509RevocationMode.NoCheck;

    /// <summary>Gets the connection attributes reported to <c>performance_schema</c>.</summary>
    public IReadOnlyDictionary<string, string> ConnectionAttributes { get; init; } =
      new Dictionary<string, string>(StringComparer.Ordinal)
      {
          ["_client_name"] = "apex-mysql-client",
      };

    /// <summary>Gets session variables applied with a single <c>SET</c> after authentication.</summary>
    public IReadOnlyDictionary<string, string> SessionVariables { get; init; } =
      new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Reads connection settings from the standard MySQL environment variables.</summary>
    public static MySqlConnectOptions FromEnvironment()
    {
        MySqlConnectOptions options = new();
        return options with
        {
            Host = GetEnvironment("MYSQL_HOST") ?? options.Host,
            Port = ParsePort(GetEnvironment("MYSQL_TCP_PORT") ?? GetEnvironment("MYSQL_PORT"), options.Port),
            Database = GetEnvironment("MYSQL_DATABASE") ?? options.Database,
            Username = GetEnvironment("MYSQL_USER") ?? options.Username,
            Password = GetEnvironment("MYSQL_PWD") ?? options.Password,
        };
    }

    /// <summary>
    /// Parses a <c>mysql://</c> or <c>mariadb://</c> URI, or a semicolon separated keyword string
    /// such as <c>Server=localhost;Port=3306;User ID=root;Password=secret;Database=test</c>.
    /// </summary>
    public static MySqlConnectOptions Parse(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return connectionString.StartsWith("mysql://", StringComparison.OrdinalIgnoreCase) ||
               connectionString.StartsWith("mariadb://", StringComparison.OrdinalIgnoreCase)
          ? ParseUri(connectionString)
          : Apply(new MySqlConnectOptions(), MySqlConnectionStringParser.ParseKeywords(connectionString));
    }

    private static MySqlConnectOptions ParseUri(string connectionString)
    {
        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("mysql" or "mariadb"))
        {
            throw new FormatException("Invalid MySQL connection URI.");
        }

        MySqlConnectOptions options = new()
        {
            Host = uri.Host.Length == 0 ? new MySqlConnectOptions().Host : uri.Host,
            Port = uri.IsDefaultPort ? 3306 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
        };

        if (uri.UserInfo.Length > 0)
        {
            var separator = uri.UserInfo.IndexOf(':', StringComparison.Ordinal);
            options = options with
            {
                Username = Uri.UnescapeDataString(
                separator < 0 ? uri.UserInfo : uri.UserInfo[..separator]),
                Password = separator < 0
                ? string.Empty
                : Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]),
            };
        }

        return Apply(options, MySqlConnectionStringParser.ParseQuery(uri.Query));
    }

    private static MySqlConnectOptions Apply(
        MySqlConnectOptions options,
        IReadOnlyDictionary<string, string> values)
    {
        Dictionary<string, string> connectionAttributes =
          new(options.ConnectionAttributes, StringComparer.Ordinal);
        foreach ((var key, var value) in values)
        {
            switch (Normalize(key))
            {
                case "host":
                case "server":
                case "datasource":
                case "address":
                    options = options with { Host = value };
                    break;
                case "port":
                    options = options with { Port = ParsePort(value, options.Port) };
                    break;
                case "user":
                case "userid":
                case "uid":
                case "username":
                    options = options with { Username = value };
                    break;
                case "password":
                case "pwd":
                    options = options with { Password = value };
                    break;
                case "database":
                case "dbname":
                case "initialcatalog":
                case "schema":
                    options = options with { Database = value };
                    break;
                case "sslmode":
                    options = options with { SslMode = ParseSslMode(value) };
                    break;
                case "authenticationplugin":
                case "authplugin":
                    options = options with
                    {
                        AuthenticationPlugin = ParseAuthenticationPlugin(value),
                    };
                    break;
                case "zerodatebehavior":
                    options = options with
                    {
                        ZeroDateBehavior = MySqlEnumParser.Parse<MySqlZeroDateBehavior>(value),
                    };
                    break;
                case "querycancellation":
                    options = options with
                    {
                        QueryCancellation = MySqlEnumParser.Parse<MySqlQueryCancellation>(value),
                    };
                    break;
                case "pipelininglimit":
                    options = options with { PipeliningLimit = ParsePositiveInt(value, key) };
                    break;
                case "cachepreparedstatements":
                    options = options with { CachePreparedStatements = ParseBoolean(value, key) };
                    break;
                case "preparedstatementcachesize":
                    options = options with { PreparedStatementCacheSize = ParseNonNegativeInt(value, key) };
                    break;
                case "preparedstatementcachesqllengthlimit":
                    options = options with
                    {
                        PreparedStatementCacheSqlLengthLimit = ParseNonNegativeInt(value, key),
                    };
                    break;
                case "stringcachecapacity":
                    options = options with { StringCacheCapacity = ParseNonNegativeInt(value, key) };
                    break;
                case "stringcachemaximumbytelength":
                    options = options with
                    {
                        StringCacheMaximumByteLength = ParseNonNegativeInt(value, key),
                    };
                    break;
                case "allowpublickeyretrieval":
                    options = options with { AllowPublicKeyRetrieval = ParseBoolean(value, key) };
                    break;
                case "allowcleartextpassword":
                    options = options with { AllowCleartextPassword = ParseBoolean(value, key) };
                    break;
                case "allowloadlocalinfile":
                    options = options with { AllowLoadLocalInfile = ParseBoolean(value, key) };
                    break;
                case "allowmultistatements":
                case "allowmultiqueries":
                    options = options with { AllowMultiStatements = ParseBoolean(value, key) };
                    break;
                case "useaffectedrows":
                    options = options with { UseAffectedRows = ParseBoolean(value, key) };
                    break;
                case "usefoundrows":
                    options = options with { UseAffectedRows = !ParseBoolean(value, key) };
                    break;
                case "serverrsapublickey":
                    options = options with { ServerRsaPublicKey = value };
                    break;
                case "collation":
                    options = options with { Collation = ParseCollation(value) };
                    break;
                case "charset":
                case "characterset":
                    if (Normalize(value) is not ("utf8" or "utf8mb4"))
                    {
                        throw new FormatException(
                          $"MySQL character set '{value}' is not supported; use utf8mb4.");
                    }

                    break;
                case "socket":
                case "unixsocket":
                    options = options with { Host = value };
                    break;
                case "connecttimeout":
                case "connectiontimeout":
                    options = options with
                    {
                        ConnectTimeout = TimeSpan.FromSeconds(ParsePositiveInt(value, key)),
                    };
                    break;
                default:
                    connectionAttributes[key] = value;
                    break;
            }
        }

        return options with { ConnectionAttributes = connectionAttributes };
    }

    private static string Normalize(string key) =>
      key.Replace(" ", string.Empty, StringComparison.Ordinal)
        .Replace("_", string.Empty, StringComparison.Ordinal)
        .Replace("-", string.Empty, StringComparison.Ordinal)
        .ToLowerInvariant();

    private static string? GetEnvironment(string name) =>
      Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static int ParsePort(string? value, int fallback) =>
      value is null
        ? fallback
        : int.TryParse(value, out var port) && port is > 0 and <= ushort.MaxValue
          ? port
          : throw new FormatException($"Invalid MySQL port '{value}'.");

    private static int ParsePositiveInt(string value, string name) =>
      int.TryParse(value, out var parsed) && parsed > 0
        ? parsed
        : throw new FormatException($"Invalid MySQL {name} value '{value}'.");

    private static int ParseNonNegativeInt(string value, string name) =>
      int.TryParse(value, out var parsed) && parsed >= 0
        ? parsed
        : throw new FormatException($"Invalid MySQL {name} value '{value}'.");

    private static bool ParseBoolean(string value, string name) =>
      value.ToLowerInvariant() switch
      {
          "true" or "1" or "yes" or "on" => true,
          "false" or "0" or "no" or "off" => false,
          _ => throw new FormatException($"Invalid MySQL {name} value '{value}'."),
      };

    private static byte ParseCollation(string value) =>
      Normalize(value) switch
      {
          "utf8mb4generalci" => 45,
          "utf8mb4bin" => 46,
          "utf8mb4unicodeci" => 224,
          "utf8mb40900aici" => 255,
          _ when byte.TryParse(value, out var parsed) && parsed != 0 => parsed,
          _ => throw new FormatException($"Invalid MySQL collation '{value}'."),
      };

    private static MySqlAuthenticationPlugin ParseAuthenticationPlugin(string value) =>
      Normalize(value) switch
      {
          "mysqlnativepassword" => MySqlAuthenticationPlugin.NativePassword,
          "cachingsha2password" => MySqlAuthenticationPlugin.CachingSha2Password,
          "sha256password" => MySqlAuthenticationPlugin.Sha256Password,
          "mysqlclearpassword" => MySqlAuthenticationPlugin.ClearPassword,
          _ => MySqlEnumParser.Parse<MySqlAuthenticationPlugin>(value),
      };

    private static MySqlSslMode ParseSslMode(string value) =>
      Normalize(value) switch
      {
          "none" or "disable" => MySqlSslMode.Disabled,
          "prefer" => MySqlSslMode.Preferred,
          "require" => MySqlSslMode.Required,
          "verifyfull" => MySqlSslMode.VerifyIdentity,
          _ => MySqlEnumParser.Parse<MySqlSslMode>(value),
      };
}

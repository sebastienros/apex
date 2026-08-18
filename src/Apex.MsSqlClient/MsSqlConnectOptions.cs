using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Apex.MsSqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MsSqlClient;

public sealed record MsSqlConnectOptions : SqlConnectOptions
{
    public MsSqlConnectOptions()
    {
        Port = 1433;
        Username = "sa";
        Database = string.Empty;
    }

    public MsSqlEncryptionMode EncryptionMode { get; init; } = MsSqlEncryptionMode.Require;

    public bool TrustServerCertificate { get; init; }

    public string? TlsHostName { get; init; }

    public RemoteCertificateValidationCallback? CertificateValidationCallback { get; init; }

    public IReadOnlyList<X509Certificate2> ClientCertificates { get; init; } =
      Array.Empty<X509Certificate2>();

    public X509RevocationMode CertificateRevocationCheckMode { get; init; } =
      X509RevocationMode.NoCheck;

    public string ApplicationName { get; init; } = "apex-mssql-client";

    public string ClientInterfaceName { get; init; } = "Apex.MsSqlClient";

    public string? WorkstationId { get; init; }

    public int PacketSize { get; init; } = 4096;

    public int StringCacheCapacity { get; init; } = 1024;

    public int StringCacheMaximumByteLength { get; init; } = 128;

    public static MsSqlConnectOptions Parse(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return connectionString.StartsWith("sqlserver://", StringComparison.OrdinalIgnoreCase)
          ? ParseUri(connectionString)
          : Apply(new MsSqlConnectOptions(), MsSqlConnectionStringParser.Parse(connectionString));
    }

    private static MsSqlConnectOptions ParseUri(string connectionString)
    {
        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Invalid SQL Server connection URI.");
        }

        MsSqlConnectOptions options = new()
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 1433 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
        };
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

        return Apply(options, MsSqlConnectionStringParser.ParseQuery(uri.Query));
    }

    private static MsSqlConnectOptions Apply(
        MsSqlConnectOptions options,
        IReadOnlyDictionary<string, string> values)
    {
        foreach ((var key, var value) in values)
        {
            var normalized = NormalizeKey(key);
            switch (normalized)
            {
                case "server":
                case "datasource":
                case "address":
                case "addr":
                case "networkaddress":
                    (var host, var port) = ParseServer(value);
                    options = options with
                    {
                        Host = host,
                        Port = port ?? options.Port,
                    };
                    break;
                case "port":
                    options = options with { Port = ParsePort(value) };
                    break;
                case "userid":
                case "uid":
                case "user":
                case "username":
                    options = options with { Username = value };
                    break;
                case "password":
                case "pwd":
                    options = options with { Password = value };
                    break;
                case "database":
                case "initialcatalog":
                    options = options with { Database = value };
                    break;
                case "encrypt":
                case "encryptionmode":
                    options = options with { EncryptionMode = ParseEncryptionMode(value) };
                    break;
                case "trustservercertificate":
                    options = options with { TrustServerCertificate = ParseBoolean(value, key) };
                    break;
                case "hostnameincertificate":
                case "tlshostname":
                    options = options with { TlsHostName = value };
                    break;
                case "applicationname":
                case "app":
                    options = options with { ApplicationName = value };
                    break;
                case "workstationid":
                    options = options with { WorkstationId = value };
                    break;
                case "packetsize":
                    options = options with { PacketSize = ParsePositiveInt(value, key) };
                    break;
                case "connecttimeout":
                case "connectiontimeout":
                    options = options with
                    {
                        ConnectTimeout = TimeSpan.FromSeconds(ParseNonNegativeInt(value, key)),
                    };
                    break;
                case "useexperimentallowleveltls":
                    options = options with
                    {
                        UseExperimentalLowLevelTls = ParseBoolean(value, key),
                    };
                    break;
                case "stringcachecapacity":
                    options = options with
                    {
                        StringCacheCapacity = ParseNonNegativeInt(value, key),
                    };
                    break;
                case "stringcachemaximumbytelength":
                    options = options with
                    {
                        StringCacheMaximumByteLength = ParseNonNegativeInt(value, key),
                    };
                    break;
                default:
                    throw new FormatException($"Unsupported SQL Server connection option '{key}'.");
            }
        }

        return options;
    }

    private static (string Host, int? Port) ParseServer(string value)
    {
        var server = value.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)
          ? value[4..]
          : value;
        if (server.Length == 0)
        {
            throw new FormatException("SQL Server host cannot be empty.");
        }

        var separator = server.LastIndexOf(',');
        if (separator < 0)
        {
            return (server, null);
        }

        var host = server[..separator].Trim();
        var port = server[(separator + 1)..].Trim();
        if (host.Length == 0)
        {
            throw new FormatException("SQL Server host cannot be empty.");
        }

        return (host, ParsePort(port));
    }

    private static int ParsePort(string value) =>
      int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
      port is > 0 and <= ushort.MaxValue
        ? port
        : throw new FormatException($"Invalid SQL Server port '{value}'.");

    private static int ParsePositiveInt(string value, string name) =>
      int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
      parsed > 0
        ? parsed
        : throw new FormatException($"Invalid SQL Server {name} value '{value}'.");

    private static int ParseNonNegativeInt(string value, string name) =>
      int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
      parsed >= 0
        ? parsed
        : throw new FormatException($"Invalid SQL Server {name} value '{value}'.");

    private static bool ParseBoolean(string value, string name) =>
      value.Trim().ToLowerInvariant() switch
      {
          "true" or "yes" or "1" => true,
          "false" or "no" or "0" => false,
          _ => throw new FormatException($"Invalid SQL Server {name} value '{value}'."),
      };

    private static MsSqlEncryptionMode ParseEncryptionMode(string value) =>
      value.Trim().ToLowerInvariant() switch
      {
          "false" or "no" or "off" or "disable" or "disabled" =>
          MsSqlEncryptionMode.Disable,
          "optional" => MsSqlEncryptionMode.Optional,
          "true" or "yes" or "on" or "mandatory" or "require" or "required" =>
          MsSqlEncryptionMode.Require,
          "strict" => MsSqlEncryptionMode.Strict,
          _ => throw new FormatException($"Invalid SQL Server encryption mode '{value}'."),
      };

    private static string NormalizeKey(string key)
    {
        const int maximumStackLength = 256;
        var buffer = key.Length <= maximumStackLength
          ? stackalloc char[key.Length]
          : new char[key.Length];
        var length = 0;
        foreach (var character in key)
        {
            if (character is not (' ' or '_' or '-'))
            {
                buffer[length++] = char.ToLowerInvariant(character);
            }
        }

        return new string(buffer[..length]);
    }
}

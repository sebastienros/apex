using Apex.MsSqlClient;
using Apex.MySqlClient;
using Apex.PgClient;
using Apex.SqlClient;
using Azure.Core;

namespace Apex.SqlClient.AzureIdentity;

public static class AzureIdentityExtensions
{
    private const string AzureSqlScope = "https://database.windows.net/.default";
    private const string AzureOssDatabaseScope =
        "https://ossrdbms-aad.database.windows.net/.default";

    public static PgConnectOptions UseAzureIdentity(
        this PgConnectOptions options,
        TokenCredential credential,
        AzureIdentityOptions? identityOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);
        identityOptions ??= new AzureIdentityOptions();
        return options with
        {
            AuthenticationProvider = CreateProvider(
                credential,
                identityOptions,
                AzureOssDatabaseScope,
                inferUsername: true),
            SslMode = PgSslMode.VerifyFull,
        };
    }

    public static MySqlConnectOptions UseAzureIdentity(
        this MySqlConnectOptions options,
        TokenCredential credential,
        AzureIdentityOptions? identityOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);
        identityOptions ??= new AzureIdentityOptions();
        return options with
        {
            AuthenticationProvider = CreateProvider(
                credential,
                identityOptions,
                AzureOssDatabaseScope,
                inferUsername: true),
            SslMode = MySqlSslMode.VerifyIdentity,
            AuthenticationPlugin = MySqlAuthenticationPlugin.ClearPassword,
            AllowCleartextPassword = true,
        };
    }

    public static MsSqlConnectOptions UseAzureIdentity(
        this MsSqlConnectOptions options,
        TokenCredential credential,
        AzureIdentityOptions? identityOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);
        identityOptions ??= new AzureIdentityOptions();
        return options with
        {
            AuthenticationProvider = CreateProvider(
                credential,
                identityOptions,
                AzureSqlScope,
                inferUsername: false),
            EncryptionMode = options.EncryptionMode == MsSqlEncryptionMode.Strict
                ? MsSqlEncryptionMode.Strict
                : MsSqlEncryptionMode.Require,
            TrustServerCertificate = false,
        };
    }

    private static SqlAuthenticationProvider CreateProvider(
        TokenCredential credential,
        AzureIdentityOptions options,
        string defaultScope,
        bool inferUsername)
    {
        var scope = options.DatabaseScope ?? defaultScope;
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (inferUsername)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ManagementScope);
        }

        AzureTokenAuthenticationProvider provider = new(
            credential,
            scope,
            options.ManagementScope,
            inferUsername ? options.Username : null,
            inferUsername);
        return provider.GetCredentialAsync;
    }
}

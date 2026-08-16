using System.Text;
using System.Text.Json;
using Apex.SqlClient;
using Azure.Core;

namespace Apex.SqlClient.AzureIdentity;

internal sealed class AzureTokenAuthenticationProvider
{
    private readonly TokenCredential _credential;
    private readonly TokenRequestContext _databaseRequest;
    private readonly TokenRequestContext _managementRequest;
    private readonly bool _inferUsername;
    private readonly SemaphoreSlim _usernameGate = new(1, 1);
    private string? _username;

    internal AzureTokenAuthenticationProvider(
        TokenCredential credential,
        string databaseScope,
        string managementScope,
        string? username,
        bool inferUsername)
    {
        _credential = credential;
        _databaseRequest = new TokenRequestContext([databaseScope]);
        _managementRequest = new TokenRequestContext([managementScope]);
        _username = username;
        _inferUsername = inferUsername;
    }

    internal async ValueTask<SqlAuthenticationCredential> GetCredentialAsync(
        CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(_databaseRequest, cancellationToken)
          .ConfigureAwait(false);
        var username = _inferUsername
            ? await GetUsernameAsync(token.Token, cancellationToken).ConfigureAwait(false)
            : null;
        return new SqlAuthenticationCredential(
            token.Token,
            SqlAuthenticationMethod.BearerToken,
            username,
            token.ExpiresOn);
    }

    private async ValueTask<string> GetUsernameAsync(
        string databaseToken,
        CancellationToken cancellationToken)
    {
        if (_username is not null)
        {
            return _username;
        }

        await _usernameGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_username is not null)
            {
                return _username;
            }

            _username = ReadUsername(databaseToken);
            if (_username is null)
            {
                var managementToken = await _credential.GetTokenAsync(
                    _managementRequest,
                    cancellationToken).ConfigureAwait(false);
                _username = ReadUsername(managementToken.Token);
            }

            return _username ?? throw new InvalidOperationException(
                "The Microsoft Entra token does not contain a supported username claim. " +
                "Specify AzureIdentityOptions.Username explicitly.");
        }
        finally
        {
            _usernameGate.Release();
        }
    }

    private static string? ReadUsername(string token)
    {
        var firstSeparator = token.IndexOf('.');
        var secondSeparator = firstSeparator < 0
            ? -1
            : token.IndexOf('.', firstSeparator + 1);
        if (firstSeparator < 0 || secondSeparator <= firstSeparator + 1)
        {
            return null;
        }

        byte[] payload;
        try
        {
            payload = DecodeBase64Url(token[(firstSeparator + 1)..secondSeparator]);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var managedIdentityResource = ReadStringClaim(root, "xms_mirid");
            if (managedIdentityResource is not null)
            {
                var separator = managedIdentityResource.LastIndexOf('/');
                var resourceName = separator < 0
                    ? managedIdentityResource
                    : managedIdentityResource[(separator + 1)..];
                if (resourceName.Length > 0)
                {
                    return Uri.UnescapeDataString(resourceName);
                }
            }

            return ReadStringClaim(root, "upn") ??
                   ReadStringClaim(root, "preferred_username") ??
                   ReadStringClaim(root, "unique_name");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadStringClaim(JsonElement root, string name) =>
        root.TryGetProperty(name, out var claim) &&
        claim.ValueKind == JsonValueKind.String &&
        claim.GetString() is { Length: > 0 } value
            ? value
            : null;

    private static byte[] DecodeBase64Url(string value)
    {
        var paddedLength = checked((value.Length + 3) / 4 * 4);
        Span<char> padded = paddedLength <= 1024
            ? stackalloc char[paddedLength]
            : new char[paddedLength];
        value.AsSpan().CopyTo(padded);
        for (var i = 0; i < value.Length; i++)
        {
            padded[i] = padded[i] switch
            {
                '-' => '+',
                '_' => '/',
                var character => character,
            };
        }

        padded[value.Length..].Fill('=');
        return Convert.FromBase64CharArray(padded.ToArray(), 0, padded.Length);
    }
}

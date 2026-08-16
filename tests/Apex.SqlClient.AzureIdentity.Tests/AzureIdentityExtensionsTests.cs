using System.Text.Json;
using Apex.MsSqlClient;
using Apex.MySqlClient;
using Apex.PgClient;
using Apex.SqlClient;
using Azure.Core;

namespace Apex.SqlClient.AzureIdentity.Tests;

[TestClass]
public sealed class AzureIdentityExtensionsTests
{
    [TestMethod]
    public async Task PostgreSqlUsesExplicitUsernameAndDefaultScope()
    {
        RecordingCredential credential = new(CreateToken("ignored"));
        var options = new PgConnectOptions().UseAzureIdentity(
            credential,
            new AzureIdentityOptions { Username = "app-role" });

        var resolved = await options.AuthenticationProvider!(CancellationToken.None);

        Assert.AreEqual("app-role", resolved.Username);
        Assert.AreEqual(SqlAuthenticationMethod.BearerToken, resolved.Method);
        Assert.AreEqual(PgSslMode.VerifyFull, options.SslMode);
        CollectionAssert.AreEqual(
            new[] { "https://ossrdbms-aad.database.windows.net/.default" },
            credential.Requests[0].Scopes.ToArray());
    }

    [TestMethod]
    public async Task MySqlInfersManagedIdentityNameAndCachesIt()
    {
        RecordingCredential credential = new(CreateToken(
            "ignored",
            "xms_mirid",
            "/subscriptions/id/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/app-mi"));
        var options = new MySqlConnectOptions().UseAzureIdentity(credential);

        var first = await options.AuthenticationProvider!(CancellationToken.None);
        var second = await options.AuthenticationProvider!(CancellationToken.None);

        Assert.AreEqual("app-mi", first.Username);
        Assert.AreEqual("app-mi", second.Username);
        Assert.AreEqual(2, credential.Requests.Count);
        Assert.AreEqual(MySqlSslMode.VerifyIdentity, options.SslMode);
        Assert.AreEqual(MySqlAuthenticationPlugin.ClearPassword, options.AuthenticationPlugin);
        Assert.IsTrue(options.AllowCleartextPassword);
    }

    [TestMethod]
    public async Task UsernameFallsBackToManagementToken()
    {
        RecordingCredential credential = new(
            CreateToken("database"),
            CreateToken("management", "preferred_username", "developer@contoso.com"));
        var options = new PgConnectOptions().UseAzureIdentity(credential);

        var resolved = await options.AuthenticationProvider!(CancellationToken.None);

        Assert.AreEqual("developer@contoso.com", resolved.Username);
        Assert.AreEqual(2, credential.Requests.Count);
        CollectionAssert.AreEqual(
            new[] { "https://management.azure.com/.default" },
            credential.Requests[1].Scopes.ToArray());
    }

    [TestMethod]
    public async Task ScopeOverridesSupportSovereignClouds()
    {
        RecordingCredential credential = new(CreateToken("sql"));
        var options = new MsSqlConnectOptions
        {
            EncryptionMode = MsSqlEncryptionMode.Disable,
            TrustServerCertificate = true,
        }.UseAzureIdentity(
            credential,
            new AzureIdentityOptions
            {
                DatabaseScope = "https://database.usgovcloudapi.net/.default",
            });

        var resolved = await options.AuthenticationProvider!(CancellationToken.None);

        Assert.AreEqual(SqlAuthenticationMethod.BearerToken, resolved.Method);
        Assert.AreEqual(MsSqlEncryptionMode.Require, options.EncryptionMode);
        Assert.IsFalse(options.TrustServerCertificate);
        CollectionAssert.AreEqual(
            new[] { "https://database.usgovcloudapi.net/.default" },
            credential.Requests[0].Scopes.ToArray());
    }

    [TestMethod]
    public async Task MissingUsernameClaimsFailsClearly()
    {
        RecordingCredential credential = new(CreateToken("database"), CreateToken("management"));
        var options = new PgConnectOptions().UseAzureIdentity(credential);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => options.AuthenticationProvider!(CancellationToken.None).AsTask());

        StringAssert.Contains(exception.Message, nameof(AzureIdentityOptions.Username));
    }

    [TestMethod]
    public async Task CancellationFlowsToTokenCredential()
    {
        RecordingCredential credential = new(CreateToken("unused"))
        {
            ThrowOnCancellation = true,
        };
        var options = new MsSqlConnectOptions().UseAzureIdentity(credential);
        using CancellationTokenSource source = new();
        source.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => options.AuthenticationProvider!(source.Token).AsTask());
    }

    private static AccessToken CreateToken(
        string value,
        string? claimName = null,
        string? claimValue = null)
    {
        Dictionary<string, string> claims = [];
        if (claimName is not null)
        {
            claims[claimName] = claimValue!;
        }

        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { value }));
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims));
        return new AccessToken(
            $"{header}.{payload}.signature",
            DateTimeOffset.UtcNow.AddHours(1));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class RecordingCredential(params AccessToken[] tokens) : TokenCredential
    {
        private readonly Queue<AccessToken> _tokens = new(tokens);

        internal List<TokenRequestContext> Requests { get; } = [];

        internal bool ThrowOnCancellation { get; init; }

        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            Requests.Add(requestContext);
            if (ThrowOnCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return ValueTask.FromResult(_tokens.Count > 1 ? _tokens.Dequeue() : _tokens.Peek());
        }
    }
}

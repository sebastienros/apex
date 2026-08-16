using System.Security.Authentication;
using Apex.SqlClient;

namespace Apex.PgClient.Tests;

[TestClass]
public sealed class PgDynamicAuthenticationTests
{
    [TestMethod]
    public async Task BearerTokenRejectsUnverifiedTransportBeforeConnecting()
    {
        var calls = 0;
        PgConnectOptions options = new()
        {
            SslMode = PgSslMode.Disable,
            AuthenticationProvider = _ =>
            {
                calls++;
                return ValueTask.FromResult(
                    new SqlAuthenticationCredential(
                        "token",
                        SqlAuthenticationMethod.BearerToken,
                        "identity"));
            },
        };

        await Assert.ThrowsExactlyAsync<AuthenticationException>(
            () => PgClient.ConnectAsync(options).AsTask());

        Assert.AreEqual(1, calls);
    }
}

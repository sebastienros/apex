using System.Security.Authentication;
using Apex.SqlClient;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlDynamicAuthenticationTests
{
    [TestMethod]
    public async Task BearerTokenRequiresClearPasswordPluginBeforeConnecting()
    {
        var calls = 0;
        MySqlConnectOptions options = new()
        {
            SslMode = MySqlSslMode.Required,
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
            () => MySqlClient.ConnectAsync(options).AsTask());

        Assert.AreEqual(1, calls);
    }
}

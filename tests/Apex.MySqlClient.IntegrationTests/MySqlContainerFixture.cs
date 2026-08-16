using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Testcontainers.MySql;

namespace Apex.MySqlClient.IntegrationTests;

/// <summary>
/// Starts and configures the MySQL or MariaDB containers shared by the integration tests.
/// </summary>
/// <remarks>
/// <see cref="MySqlBuilder"/>'s built-in readiness check execs
/// <c>mysql &lt;database&gt; --wait --silent --execute="SELECT 1;"</c> as the implicit root OS user
/// over the Unix socket. That check is unusable here: it fails with an access-denied error once a
/// root password is configured (which this fixture always does), and the <c>mariadb:11.8</c>
/// image no longer ships a <c>mysql</c> client binary at all, so the exec never even starts. This
/// fixture instead waits for the mapped port to accept connections and then confirms real
/// readiness by opening a connection with the driver under test and retrying <c>SELECT 1</c>,
/// which is both accurate and exercises the exact handshake path the tests rely on.
/// </remarks>
internal static class MySqlContainerFixture
{
    internal const string DefaultImage = "mysql:8.4";
    internal const string Database = "apex_test";
    internal const string Username = "apex_user";
    internal const string Password = "apex_pass";

    /// <summary>Resolves the image the primary integration suite runs against.</summary>
    internal static string ResolveImage() =>
      Environment.GetEnvironmentVariable("MYSQL_IMAGE") is { Length: > 0 } image ? image : DefaultImage;

    /// <summary>Starts a freshly configured, ready-to-use container for the given image tag.</summary>
    internal static async Task<MySqlContainer> StartAsync(
      string? image = null,
      INetwork? network = null,
      string? networkAlias = null)
    {
      var builder = new MySqlBuilder(image ?? ResolveImage())
          .WithDatabase(Database)
          .WithUsername(Username)
          .WithPassword(Password)
          .WithCommand("--local-infile=1")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(MySqlBuilder.MySqlPort));
      if (network is not null)
      {
        builder = builder.WithNetwork(network);
      }

      if (!string.IsNullOrWhiteSpace(networkAlias))
      {
        builder = builder.WithNetworkAliases(networkAlias);
      }

      var container = builder.Build();
        await container.StartAsync();
        await WaitUntilAcceptingConnectionsAsync(container);
        return container;
    }

    /// <summary>Builds connect options that reach the supplied container.</summary>
    internal static MySqlConnectOptions CreateOptions(MySqlContainer container, int pipeliningLimit = 8) =>
      new()
      {
          Host = container.Hostname,
          Port = container.GetMappedPublicPort(MySqlBuilder.MySqlPort),
          Database = Database,
          Username = Username,
          Password = Password,
          PipeliningLimit = pipeliningLimit,
          AllowPublicKeyRetrieval = true,
      };

    private static async Task WaitUntilAcceptingConnectionsAsync(MySqlContainer container)
    {
        var options = CreateOptions(container) with
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        TimeSpan deadline = TimeSpan.FromSeconds(60);
        var start = DateTime.UtcNow;
        Exception? lastError = null;
        while (DateTime.UtcNow - start < deadline)
        {
            try
            {
                await using var probe = await MySqlClient.ConnectAsync(options);
                await probe.QueryAsync("SELECT 1");
                return;
            }
            catch (Exception exception) when (
              exception is MySqlException or
                System.Net.Sockets.SocketException or
                System.Security.Authentication.AuthenticationException or
                TimeoutException or
                OperationCanceledException)
            {
                lastError = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }

        throw new TimeoutException(
          $"The MySQL container did not become ready within {deadline}.",
          lastError);
    }
}

using Testcontainers.MsSql;

namespace Apex.MsSqlClient.IntegrationTests;

internal static class MsSqlTestEnvironment
{
    private const string Password = "Apex_Testcontainers!2026#Sql";
    private static MsSqlContainer? s_container;

    internal static MsSqlConnectOptions Options
    {
        get
        {
            var container = s_container ??
              throw new InvalidOperationException("The SQL Server container is not running.");
            return new MsSqlConnectOptions
            {
                Host = container.Hostname,
                Port = container.GetMappedPublicPort(1433),
                Database = "master",
                Username = "sa",
                Password = Password,
                EncryptionMode = MsSqlEncryptionMode.Require,
                TrustServerCertificate = true,
            };
        }
    }

    internal static async Task StartAsync()
    {
        var image = Environment.GetEnvironmentVariable("MSSQL_IMAGE") ??
          "mcr.microsoft.com/mssql/server:2025-latest";
        s_container = new MsSqlBuilder(image)
          .WithPassword(Password)
          .Build();
        await s_container.StartAsync();
    }

    internal static async Task StopAsync()
    {
        if (s_container is not null)
        {
            await s_container.DisposeAsync();
            s_container = null;
        }
    }
}

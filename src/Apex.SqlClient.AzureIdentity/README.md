![Apex.SqlClient.AzureIdentity](https://raw.githubusercontent.com/sebastienros/apex/main/assets/Apex.SqlClient.AzureIdentity/banner.png)

# Apex SQL Client Azure Identity

`Apex.SqlClient.AzureIdentity` adds Microsoft Entra authentication to all Apex database
drivers without adding Azure SDK dependencies to the base driver packages.

Pass a reusable `TokenCredential`; Apex requests a fresh cached token whenever a
pool opens a new physical connection.

```csharp
using Apex.SqlClient.AzureIdentity;
using Apex.PgClient;
using Azure.Identity;

TokenCredential credential = new DefaultAzureCredential();
PgConnectOptions options = new PgConnectOptions
{
    Host = "example.postgres.database.azure.com",
    Database = "app",
}.UseAzureIdentity(
    credential,
    new AzureIdentityOptions { Username = "app-identity" });

await using PgPool pool = PgPool.Create(options);
```

The same `UseAzureIdentity` method is available for `MySqlConnectOptions` and
`MsSqlConnectOptions`. PostgreSQL and MySQL use the standard Azure OSS database
scope; SQL Server uses the Azure SQL scope and native TDS federated
authentication.

When `Username` is omitted for PostgreSQL or MySQL, Apex infers it once from the
database or Azure management token. Specify it explicitly if the principal's
database role differs from its token claims.

Use `DatabaseScope` and `ManagementScope` overrides for sovereign clouds.
Azure identity enables certificate-verifying TLS and does not support static
access tokens in connection strings.

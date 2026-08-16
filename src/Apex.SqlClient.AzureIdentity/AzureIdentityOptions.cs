namespace Apex.SqlClient.AzureIdentity;

public sealed record AzureIdentityOptions
{
    public string? Username { get; init; }

    public string? DatabaseScope { get; init; }

    public string ManagementScope { get; init; } =
        "https://management.azure.com/.default";
}

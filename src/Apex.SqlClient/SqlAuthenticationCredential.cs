namespace Apex.SqlClient;

public enum SqlAuthenticationMethod
{
    Password,
    BearerToken,
}

public sealed class SqlAuthenticationCredential
{
    public SqlAuthenticationCredential(
        string secret,
        SqlAuthenticationMethod method = SqlAuthenticationMethod.Password,
        string? username = null,
        DateTimeOffset? expiresOn = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        Secret = secret;
        Method = method;
        Username = username;
        ExpiresOn = expiresOn;
    }

    public string Secret { get; }

    public SqlAuthenticationMethod Method { get; }

    public string? Username { get; }

    public DateTimeOffset? ExpiresOn { get; }
}

public delegate ValueTask<SqlAuthenticationCredential> SqlAuthenticationProvider(
    CancellationToken cancellationToken);

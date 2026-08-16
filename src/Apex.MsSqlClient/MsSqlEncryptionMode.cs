namespace Apex.MsSqlClient;

/// <summary>Controls transport encryption negotiation with SQL Server.</summary>
public enum MsSqlEncryptionMode
{
    /// <summary>Do not negotiate TLS. SQL credentials are not protected on the wire.</summary>
    Disable,

    /// <summary>Use TLS when the server supports full-session encryption.</summary>
    Optional,

    /// <summary>Require full-session TLS using TDS 7.x encryption negotiation.</summary>
    Require,

    /// <summary>Require TDS 8.0 TLS before PRELOGIN and negotiate the <c>tds/8.0</c> ALPN.</summary>
    Strict,
}

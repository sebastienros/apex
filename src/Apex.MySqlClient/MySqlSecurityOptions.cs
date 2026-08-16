namespace Apex.MySqlClient;

/// <summary>Controls how the driver negotiates TLS with the server.</summary>
public enum MySqlSslMode
{
    /// <summary>Never negotiate TLS.</summary>
    Disabled,

    /// <summary>Negotiate TLS when the server advertises support, otherwise continue in the clear.</summary>
    Preferred,

    /// <summary>Require TLS but accept any server certificate.</summary>
    Required,

    /// <summary>Require TLS and validate the certificate chain.</summary>
    VerifyCa,

    /// <summary>Require TLS and validate both the certificate chain and the host name.</summary>
    VerifyIdentity,
}

/// <summary>Selects the authentication plugin the client offers to the server.</summary>
public enum MySqlAuthenticationPlugin
{
    /// <summary>Use the plugin the server requests.</summary>
    Default,

    /// <summary>Use <c>mysql_native_password</c>.</summary>
    NativePassword,

    /// <summary>Use <c>caching_sha2_password</c>.</summary>
    CachingSha2Password,

    /// <summary>Use <c>sha256_password</c>.</summary>
    Sha256Password,

    /// <summary>Use <c>mysql_clear_password</c>, which requires TLS and an explicit opt-in.</summary>
    ClearPassword,
}

/// <summary>
/// Selects what the driver does when a command is cancelled after it reached the server.
/// MySQL has no in-band cancellation channel, so a cancelled command either has to be killed
/// through a second connection or the physical connection has to be discarded.
/// </summary>
public enum MySqlQueryCancellation
{
    /// <summary>Wait for the running command to finish, then report cancellation.</summary>
    Disabled,

    /// <summary>
    /// Open a short lived administrative connection and issue <c>KILL QUERY</c>. The physical
    /// connection stays synchronized and reusable. Falls back to <see cref="CloseConnection"/>
    /// when the kill cannot be delivered.
    /// </summary>
    KillQuery,

    /// <summary>Close the physical connection so it is never reused in a desynchronized state.</summary>
    CloseConnection,
}

/// <summary>Selects how <c>0000-00-00</c> dates and date times are surfaced.</summary>
public enum MySqlZeroDateBehavior
{
    /// <summary>Throw a <see cref="FormatException"/> describing the invalid value.</summary>
    Error,

    /// <summary>Return <see langword="null"/>.</summary>
    Null,

    /// <summary>Return <see cref="DateTime.MinValue"/> or <see cref="DateOnly.MinValue"/>.</summary>
    MinValue,
}

internal static class MySqlEnumParser
{
    internal static T Parse<T>(string value)
      where T : struct, Enum
    {
        var normalized = value
          .Replace("-", string.Empty, StringComparison.Ordinal)
          .Replace("_", string.Empty, StringComparison.Ordinal)
          .Replace(" ", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse(normalized, ignoreCase: true, out T result)
          ? result
          : throw new ArgumentException($"Unknown {typeof(T).Name} value '{value}'.", nameof(value));
    }
}

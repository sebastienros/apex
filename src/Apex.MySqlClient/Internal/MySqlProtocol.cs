namespace Apex.MySqlClient.Internal;

/// <summary>Constants of the MySQL client/server protocol version 10.</summary>
internal static class MySqlProtocol
{
    internal const int PacketHeaderLength = 4;

    /// <summary>The largest payload a single wire frame can carry.</summary>
    internal const int MaximumFramePayloadLength = 0xFFFFFF;

    /// <summary>The largest logical payload the driver reassembles from split frames.</summary>
    internal const int MaximumPayloadLength = 256 * 1024 * 1024;

    internal const int NonceLength = 20;

    internal const byte OkHeader = 0x00;
    internal const byte AuthMoreDataHeader = 0x01;
    internal const byte LocalInfileHeader = 0xFB;
    internal const byte NullHeader = 0xFB;
    internal const byte EofHeader = 0xFE;
    internal const byte ErrorHeader = 0xFF;

    internal const byte AuthPublicKeyRequest = 0x02;
    internal const byte Sha256PublicKeyRequest = 0x01;
    internal const byte AuthFastSuccess = 0x03;
    internal const byte AuthFullAuthentication = 0x04;

    internal const string NativePasswordPlugin = "mysql_native_password";
    internal const string CachingSha2PasswordPlugin = "caching_sha2_password";
    internal const string Sha256PasswordPlugin = "sha256_password";
    internal const string ClearPasswordPlugin = "mysql_clear_password";

    /// <summary>The utf8mb4_general_ci collation, supported by every MySQL and MariaDB release.</summary>
    internal const byte Utf8Mb4Collation = 45;

    /// <summary>Identifies a column holding binary rather than character data.</summary>
    internal const int BinaryCollation = 63;
}

[Flags]
internal enum MySqlCapabilities : uint
{
    None = 0,
    LongPassword = 0x00000001,
    FoundRows = 0x00000002,
    LongFlag = 0x00000004,
    ConnectWithDatabase = 0x00000008,
    Compress = 0x00000020,
    LocalFiles = 0x00000080,
    IgnoreSpace = 0x00000100,
    Protocol41 = 0x00000200,
    Interactive = 0x00000400,
    Ssl = 0x00000800,
    Transactions = 0x00002000,
    SecureConnection = 0x00008000,
    MultiStatements = 0x00010000,
    MultiResults = 0x00020000,
    PreparedStatementMultiResults = 0x00040000,
    PluginAuth = 0x00080000,
    ConnectAttributes = 0x00100000,
    PluginAuthLengthEncodedClientData = 0x00200000,
    CanHandleExpiredPasswords = 0x00400000,
    SessionTrack = 0x00800000,
    DeprecateEof = 0x01000000,
    OptionalResultSetMetadata = 0x02000000,
}

internal enum MySqlCommand : byte
{
    Quit = 0x01,
    InitDatabase = 0x02,
    Query = 0x03,
    Ping = 0x0E,
    StatementPrepare = 0x16,
    StatementExecute = 0x17,
    StatementSendLongData = 0x18,
    StatementClose = 0x19,
    StatementReset = 0x1A,
    StatementFetch = 0x1C,
    ResetConnection = 0x1F,
}

internal enum MySqlCursorType : byte
{
    NoCursor = 0x00,
    ReadOnly = 0x01,
    ForUpdate = 0x02,
    Scrollable = 0x04,
}

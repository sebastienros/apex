using Apex.MySqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MySqlClient;

/// <summary>The wire type of a MySQL column or parameter.</summary>
public enum MySqlType : byte
{
    Decimal = 0x00,
    Tiny = 0x01,
    Short = 0x02,
    Long = 0x03,
    Float = 0x04,
    Double = 0x05,
    Null = 0x06,
    Timestamp = 0x07,
    LongLong = 0x08,
    Int24 = 0x09,
    Date = 0x0A,
    Time = 0x0B,
    DateTime = 0x0C,
    Year = 0x0D,
    NewDate = 0x0E,
    VarChar = 0x0F,
    Bit = 0x10,
    Timestamp2 = 0x11,
    DateTime2 = 0x12,
    Time2 = 0x13,
    Vector = 0xF2,
    Json = 0xF5,
    NewDecimal = 0xF6,
    Enum = 0xF7,
    Set = 0xF8,
    TinyBlob = 0xF9,
    MediumBlob = 0xFA,
    LongBlob = 0xFB,
    Blob = 0xFC,
    VarString = 0xFD,
    String = 0xFE,
    Geometry = 0xFF,
}

/// <summary>The column definition flags reported by the server.</summary>
[Flags]
public enum MySqlColumnFlags : ushort
{
    None = 0,
    NotNull = 0x0001,
    PrimaryKey = 0x0002,
    UniqueKey = 0x0004,
    MultipleKey = 0x0008,
    Blob = 0x0010,
    Unsigned = 0x0020,
    ZeroFill = 0x0040,
    Binary = 0x0080,
    Enum = 0x0100,
    AutoIncrement = 0x0200,
    Timestamp = 0x0400,
    Set = 0x0800,
    NoDefaultValue = 0x1000,
    OnUpdateNow = 0x2000,
    Numeric = 0x8000,
}

/// <summary>The session status flags carried by OK and EOF packets.</summary>
[Flags]
public enum MySqlServerStatus : ushort
{
    None = 0,
    InTransaction = 0x0001,
    AutoCommit = 0x0002,
    MoreResultsExist = 0x0008,
    NoGoodIndexUsed = 0x0010,
    NoIndexUsed = 0x0020,
    CursorExists = 0x0040,
    LastRowSent = 0x0080,
    DatabaseDropped = 0x0100,
    NoBackslashEscapes = 0x0200,
    MetadataChanged = 0x0400,
    QueryWasSlow = 0x0800,
    PreparedStatementOutParameters = 0x1000,
    InReadOnlyTransaction = 0x2000,
    SessionStateChanged = 0x4000,
}

/// <summary>Describes one MySQL result column beyond the common <see cref="SqlColumn"/> contract.</summary>
public sealed record MySqlColumnMetadata(
    string Name,
    string OriginalName,
    string Table,
    string OriginalTable,
    string Schema,
    MySqlType Type,
    MySqlColumnFlags Flags,
    int CharacterSet,
    uint ColumnLength,
    byte Decimals)
{
    /// <summary>Gets a value indicating whether the column holds an unsigned integer.</summary>
    public bool IsUnsigned => (Flags & MySqlColumnFlags.Unsigned) != 0;

    /// <summary>Gets a value indicating whether the column holds binary rather than character data.</summary>
    public bool IsBinary => CharacterSet == MySqlProtocol.BinaryCollation;

    /// <summary>
    /// Recovers the type, flags, character set and scale that this driver packs into a
    /// <see cref="SqlColumn"/>. Names of the source table and column are not preserved.
    /// </summary>
    public static MySqlColumnMetadata FromColumn(SqlColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return new MySqlColumnMetadata(
          column.Name,
          column.Name,
          string.Empty,
          string.Empty,
          string.Empty,
          (MySqlType)column.TypeId,
          MySqlColumnCodec.GetFlags(column.TypeModifier),
          unchecked((ushort)column.TypeSize),
          0,
          MySqlColumnCodec.GetDecimals(column.TypeModifier));
    }
}

/// <summary>The parsed version of the connected MySQL or MariaDB server.</summary>
public readonly record struct MySqlServerVersion(
    string FullVersion,
    int Major,
    int Minor,
    int Micro,
    bool IsMariaDb)
{
    /// <summary>Gets the product name reported through <see cref="DatabaseMetadata"/>.</summary>
    public string ProductName => IsMariaDb ? "MariaDB" : "MySQL";
}

/// <summary>The MySQL specific outcome of the most recently completed command.</summary>
public readonly record struct MySqlCommandInfo(
    long AffectedRows,
    ulong LastInsertId,
    MySqlServerStatus Status,
    int WarningCount,
    string Info)
{
    internal static MySqlCommandInfo Empty { get; } =
      new(0, 0, MySqlServerStatus.None, 0, string.Empty);
}

internal readonly record struct MySqlExecutionResult(
    SqlRowSet Rows,
    MySqlCommandInfo CommandInfo)
{
    internal SqlCommandResult ToCommandResult() =>
      new(
        Rows.AffectedRows,
        Rows.CommandTag,
        CommandInfo.LastInsertId,
        (uint)CommandInfo.Status,
        CommandInfo.WarningCount);
}

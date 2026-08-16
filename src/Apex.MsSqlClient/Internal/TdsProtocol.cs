namespace Apex.MsSqlClient.Internal;

internal static class TdsMessageType
{
    internal const byte SqlBatch = 0x01;
    internal const byte Rpc = 0x03;
    internal const byte TabularResult = 0x04;
    internal const byte Attention = 0x06;
    internal const byte Login7 = 0x10;
    internal const byte PreLogin = 0x12;
}

internal static class TdsProcedureId
{
    internal const ushort ExecuteSql = 10;
    internal const ushort Execute = 12;
    internal const ushort PrepExec = 13;
    internal const ushort Unprepare = 15;
}

internal static class TdsTokenType
{
    internal const byte ReturnStatus = 0x79;
    internal const byte ColumnMetadata = 0x81;
    internal const byte TableName = 0xA4;
    internal const byte ColumnInfo = 0xA5;
    internal const byte Order = 0xA9;
    internal const byte Error = 0xAA;
    internal const byte Info = 0xAB;
    internal const byte ReturnValue = 0xAC;
    internal const byte LoginAck = 0xAD;
    internal const byte FeatureExtAck = 0xAE;
    internal const byte Row = 0xD1;
    internal const byte NbcRow = 0xD2;
    internal const byte EnvironmentChange = 0xE3;
    internal const byte SessionState = 0xE4;
    internal const byte Sspi = 0xED;
    internal const byte FedAuthInfo = 0xEE;
    internal const byte Done = 0xFD;
    internal const byte DoneProc = 0xFE;
    internal const byte DoneInProc = 0xFF;
}

[Flags]
internal enum TdsDoneStatus : ushort
{
    More = 0x0001,
    Error = 0x0002,
    InTransaction = 0x0004,
    Count = 0x0010,
    Attention = 0x0020,
    ServerError = 0x0100,
}

internal static class TdsEnvironmentChange
{
    internal const byte Database = 1;
    internal const byte PacketSize = 4;
    internal const byte BeginTransaction = 8;
    internal const byte CommitTransaction = 9;
    internal const byte RollbackTransaction = 10;
    internal const byte EnlistDtc = 11;
    internal const byte DefectDtc = 12;
    internal const byte Routing = 20;
}

internal static class TdsDataType
{
    internal const byte Null = 0x1F;
    internal const byte Image = 0x22;
    internal const byte Text = 0x23;
    internal const byte Guid = 0x24;
    internal const byte VarBinary = 0x25;
    internal const byte IntN = 0x26;
    internal const byte VarChar = 0x27;
    internal const byte Date = 0x28;
    internal const byte Time = 0x29;
    internal const byte DateTime2 = 0x2A;
    internal const byte DateTimeOffset = 0x2B;
    internal const byte Binary = 0x2D;
    internal const byte Char = 0x2F;
    internal const byte Int1 = 0x30;
    internal const byte Bit = 0x32;
    internal const byte Int2 = 0x34;
    internal const byte Decimal = 0x37;
    internal const byte Int4 = 0x38;
    internal const byte DateTime4 = 0x3A;
    internal const byte Float4 = 0x3B;
    internal const byte Money = 0x3C;
    internal const byte DateTime = 0x3D;
    internal const byte Float8 = 0x3E;
    internal const byte Numeric = 0x3F;
    internal const byte NText = 0x63;
    internal const byte BitN = 0x68;
    internal const byte DecimalN = 0x6A;
    internal const byte NumericN = 0x6C;
    internal const byte FloatN = 0x6D;
    internal const byte MoneyN = 0x6E;
    internal const byte DateTimeN = 0x6F;
    internal const byte Money4 = 0x7A;
    internal const byte Int8 = 0x7F;
    internal const byte BigVarBinary = 0xA5;
    internal const byte BigVarChar = 0xA7;
    internal const byte BigBinary = 0xAD;
    internal const byte BigChar = 0xAF;
    internal const byte NVarChar = 0xE7;
    internal const byte NChar = 0xEF;
    internal const byte Udt = 0xF0;
    internal const byte Xml = 0xF1;
    internal const byte Json = 0xF4;
}

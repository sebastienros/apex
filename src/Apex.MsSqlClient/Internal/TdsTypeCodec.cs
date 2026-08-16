using Apex.SqlClient;

namespace Apex.MsSqlClient.Internal;

internal readonly record struct TdsTypeInfo(
    byte Type,
    int MaximumLength = 0,
    byte Precision = 0,
    byte Scale = 0,
    TdsCollation? Collation = null)
{
    internal SqlColumn ToColumn(string name)
    {
        var size = MaximumLength is < 0 or > short.MaxValue
          ? (short)-1
          : (short)MaximumLength;
        var modifier = Scale | Precision << 8 | (Collation?.CodePage ?? 0) << 16;
        return new SqlColumn(name, Type, size, modifier, SqlDataFormat.Binary);
    }
}

internal static class TdsTypeCodec
{
    internal static TdsTypeInfo ReadTypeInfo(ref TdsPayloadReader reader)
    {
        var type = reader.ReadByte();
        return type switch
        {
            TdsDataType.Null or
            TdsDataType.Int1 or
            TdsDataType.Bit or
            TdsDataType.Int2 or
            TdsDataType.Int4 or
            TdsDataType.DateTime4 or
            TdsDataType.Float4 or
            TdsDataType.Money or
            TdsDataType.DateTime or
            TdsDataType.Float8 or
            TdsDataType.Money4 or
            TdsDataType.Int8 => new TdsTypeInfo(type, FixedLength(type)),

            TdsDataType.Guid or
            TdsDataType.IntN or
            TdsDataType.BitN or
            TdsDataType.FloatN or
            TdsDataType.MoneyN or
            TdsDataType.DateTimeN =>
              new TdsTypeInfo(type, reader.ReadByte()),

            TdsDataType.Decimal or
            TdsDataType.Numeric or
            TdsDataType.DecimalN or
            TdsDataType.NumericN =>
              new TdsTypeInfo(
                type,
                reader.ReadByte(),
                reader.ReadByte(),
                reader.ReadByte()),

            TdsDataType.Date => new TdsTypeInfo(type, 3),
            TdsDataType.Time => ReadScaled(type, ref reader, dateBytes: 0, offsetBytes: 0),
            TdsDataType.DateTime2 => ReadScaled(type, ref reader, dateBytes: 3, offsetBytes: 0),
            TdsDataType.DateTimeOffset => ReadScaled(type, ref reader, dateBytes: 3, offsetBytes: 2),

            TdsDataType.Binary or
            TdsDataType.VarBinary or
            TdsDataType.BigBinary or
            TdsDataType.BigVarBinary =>
              new TdsTypeInfo(type, ReadMaximumLength(ref reader)),

            TdsDataType.Char or
            TdsDataType.VarChar or
            TdsDataType.BigChar or
            TdsDataType.BigVarChar =>
              ReadCharacterType(type, ref reader, unicode: false),

            TdsDataType.NChar or
            TdsDataType.NVarChar =>
              ReadCharacterType(type, ref reader, unicode: true),

            TdsDataType.Text or TdsDataType.NText =>
              ReadLegacyLob(type, ref reader, hasCollation: true),
            TdsDataType.Image => ReadLegacyLob(type, ref reader, hasCollation: false),
            TdsDataType.Xml => ReadXmlType(ref reader),
            TdsDataType.Json => new TdsTypeInfo(type, ushort.MaxValue),
            TdsDataType.Udt => ReadUdtType(ref reader),
            _ => throw new NotSupportedException(
              $"SQL Server TDS data type 0x{type:X2} is not supported."),
        };
    }

    internal static IReadOnlyList<TdsColumn> ReadColumns(ref TdsPayloadReader reader)
    {
        int count = reader.ReadUInt16LittleEndian();
        if (count == ushort.MaxValue)
        {
            return Array.Empty<TdsColumn>();
        }

        TdsColumn[] columns = new TdsColumn[count];
        for (var i = 0; i < count; i++)
        {
            _ = reader.ReadUInt32LittleEndian();
            _ = reader.ReadUInt16LittleEndian();
            var typeInfo = ReadTypeInfo(ref reader);
            var name = reader.ReadBVarChar();
            columns[i] = new TdsColumn(typeInfo.ToColumn(name), typeInfo);
        }

        return columns;
    }

    internal static int FixedLength(byte type) =>
      type switch
      {
          TdsDataType.Null => 0,
          TdsDataType.Int1 or TdsDataType.Bit => 1,
          TdsDataType.Int2 => 2,
          TdsDataType.Int4 or
        TdsDataType.DateTime4 or
        TdsDataType.Float4 or
        TdsDataType.Money4 => 4,
          TdsDataType.Money or
        TdsDataType.DateTime or
        TdsDataType.Float8 or
        TdsDataType.Int8 => 8,
          _ => -1,
      };

    internal static bool UsesPlp(TdsTypeInfo typeInfo) =>
      typeInfo.MaximumLength == ushort.MaxValue &&
      typeInfo.Type is
        TdsDataType.BigVarBinary or
        TdsDataType.BigBinary or
        TdsDataType.BigVarChar or
        TdsDataType.BigChar or
        TdsDataType.NVarChar or
        TdsDataType.NChar or
        TdsDataType.Json;

    private static TdsTypeInfo ReadScaled(
        byte type,
        ref TdsPayloadReader reader,
        int dateBytes,
        int offsetBytes)
    {
        var scale = reader.ReadByte();
        if (scale > 7)
        {
            throw new InvalidDataException($"Invalid SQL Server temporal scale {scale}.");
        }

        var timeBytes = scale <= 2 ? 3 : scale <= 4 ? 4 : 5;
        return new TdsTypeInfo(type, timeBytes + dateBytes + offsetBytes, Scale: scale);
    }

    private static int ReadMaximumLength(ref TdsPayloadReader reader) =>
      reader.ReadUInt16LittleEndian();

    private static TdsTypeInfo ReadCharacterType(
        byte type,
        ref TdsPayloadReader reader,
        bool unicode)
    {
        int maximumLength = reader.ReadUInt16LittleEndian();
        var collation = TdsCollationCodec.Read(ref reader, unicode);
        return new TdsTypeInfo(
          type,
          maximumLength,
          Collation: collation);
    }

    private static TdsTypeInfo ReadLegacyLob(
        byte type,
        ref TdsPayloadReader reader,
        bool hasCollation)
    {
        var maximumLength = reader.ReadInt32LittleEndian();
        TdsCollation? collation = null;
        if (hasCollation)
        {
            collation = TdsCollationCodec.Read(
              ref reader,
              unicode: type == TdsDataType.NText);
        }

        SkipTableName(ref reader);
        return new TdsTypeInfo(type, maximumLength, Collation: collation);
    }

    private static TdsTypeInfo ReadXmlType(ref TdsPayloadReader reader)
    {
        if (reader.ReadByte() != 0)
        {
            _ = reader.ReadBVarChar();
            _ = reader.ReadBVarChar();
            _ = reader.ReadUsVarChar();
        }

        return new TdsTypeInfo(TdsDataType.Xml, ushort.MaxValue);
    }

    private static TdsTypeInfo ReadUdtType(ref TdsPayloadReader reader)
    {
        int maximumLength = reader.ReadUInt16LittleEndian();
        _ = reader.ReadBVarChar();
        _ = reader.ReadBVarChar();
        _ = reader.ReadBVarChar();
        _ = reader.ReadUsVarChar();
        return new TdsTypeInfo(TdsDataType.Udt, maximumLength);
    }

    private static void SkipTableName(ref TdsPayloadReader reader)
    {
        int parts = reader.ReadByte();
        for (var i = 0; i < parts; i++)
        {
            _ = reader.ReadUsVarChar();
        }
    }
}

internal readonly record struct TdsColumn(SqlColumn Column, TdsTypeInfo TypeInfo);

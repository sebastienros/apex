using System.Buffers.Binary;
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
            _ => throw UnsupportedType(type),
        };
    }

    internal static TdsColumn[] ReadColumns(ref TdsPayloadReader reader)
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

    internal static int FixedLength(byte type) => TdsFieldLayout.FixedLengthOf(type);

    /// <summary>
    /// Measures a COLMETADATA token body (the bytes after the token type) at the start of
    /// <paramref name="source"/> without decoding it. Returns its length, or -1 when it continues
    /// past the end of <paramref name="source"/>.
    /// </summary>
    internal static int MeasureColumns(ReadOnlySpan<byte> source)
    {
        if (source.Length < sizeof(ushort))
        {
            return -1;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(source);
        var position = sizeof(ushort);
        if (count == ushort.MaxValue)
        {
            return position;
        }

        for (var i = 0; i < count; i++)
        {
            // User type (4 bytes) and flags (2 bytes), TYPE_INFO, then the column name.
            if (!TrySkip(source, ref position, sizeof(uint) + sizeof(ushort)) ||
                !TrySkipTypeInfo(source, ref position) ||
                !TrySkipBVarChar(source, ref position))
            {
                return -1;
            }
        }

        return position;
    }

    /// <summary>
    /// Measures the part of a RETURNVALUE token before its value: ordinal, name, status, user
    /// type, flags and TYPE_INFO. Returns its length, or -1 when it continues past the end of
    /// <paramref name="source"/>.
    /// </summary>
    internal static int MeasureReturnValueHeader(ReadOnlySpan<byte> source)
    {
        var position = 0;
        return TrySkip(source, ref position, sizeof(ushort)) &&
          TrySkipBVarChar(source, ref position) &&
          TrySkip(source, ref position, sizeof(byte) + sizeof(uint) + sizeof(ushort)) &&
          TrySkipTypeInfo(source, ref position)
          ? position
          : -1;
    }

    /// <summary>
    /// Skips one TYPE_INFO. Its shape comes from <see cref="TdsFieldLayout.TypeInfoKindOf"/>, and
    /// must match what <see cref="ReadTypeInfo"/> reads.
    /// </summary>
    private static bool TrySkipTypeInfo(ReadOnlySpan<byte> source, ref int position)
    {
        if (position >= source.Length)
        {
            return false;
        }

        var type = source[position++];
        switch (TdsFieldLayout.TypeInfoKindOf(type))
        {
            case TdsTypeInfoKind.None:
                return true;
            case TdsTypeInfoKind.OneByte:
                return TrySkip(source, ref position, 1);
            case TdsTypeInfoKind.Decimal:
                return TrySkip(source, ref position, 3);
            case TdsTypeInfoKind.UShortLength:
                return TrySkip(source, ref position, sizeof(ushort));
            case TdsTypeInfoKind.CollatedUShortLength:
                return TrySkip(source, ref position, sizeof(ushort) + TdsCollationLength);
            case TdsTypeInfoKind.Lob:
                return TrySkip(source, ref position, sizeof(int)) &&
                  TrySkipTableName(source, ref position);
            case TdsTypeInfoKind.CollatedLob:
                return TrySkip(source, ref position, sizeof(int) + TdsCollationLength) &&
                  TrySkipTableName(source, ref position);
            case TdsTypeInfoKind.Xml:
                if (position >= source.Length)
                {
                    return false;
                }

                return source[position++] == 0 ||
                  TrySkipBVarChar(source, ref position) &&
                  TrySkipBVarChar(source, ref position) &&
                  TrySkipUsVarChar(source, ref position);
            case TdsTypeInfoKind.Udt:
                return TrySkip(source, ref position, sizeof(ushort)) &&
                  TrySkipBVarChar(source, ref position) &&
                  TrySkipBVarChar(source, ref position) &&
                  TrySkipBVarChar(source, ref position) &&
                  TrySkipUsVarChar(source, ref position);
            default:
                throw UnsupportedType(type);
        }
    }

    private static bool TrySkipTableName(ReadOnlySpan<byte> source, ref int position)
    {
        if (position >= source.Length)
        {
            return false;
        }

        int parts = source[position++];
        for (var i = 0; i < parts; i++)
        {
            if (!TrySkipUsVarChar(source, ref position))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TrySkipBVarChar(ReadOnlySpan<byte> source, ref int position)
    {
        if (position >= source.Length)
        {
            return false;
        }

        int characters = source[position++];
        return TrySkip(source, ref position, characters * sizeof(char));
    }

    private static bool TrySkipUsVarChar(ReadOnlySpan<byte> source, ref int position)
    {
        if (source.Length - position < sizeof(ushort))
        {
            return false;
        }

        int characters = BinaryPrimitives.ReadUInt16LittleEndian(source[position..]);
        position += sizeof(ushort);
        return TrySkip(source, ref position, characters * sizeof(char));
    }

    private static bool TrySkip(ReadOnlySpan<byte> source, ref int position, int count)
    {
        if (count > source.Length - position)
        {
            return false;
        }

        position += count;
        return true;
    }

    private const int TdsCollationLength = sizeof(uint) + sizeof(byte);

    internal static NotSupportedException UnsupportedType(byte type) =>
      new($"SQL Server TDS data type 0x{type:X2} is not supported.");

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

internal readonly record struct TdsColumn
{
    internal TdsColumn(SqlColumn column, TdsTypeInfo typeInfo)
    {
        Column = column;
        TypeInfo = typeInfo;
        Layout = TdsFieldLayout.For(typeInfo);
    }

    internal SqlColumn Column { get; }

    // Get-only, so a `with` expression cannot change the type without the layout.
    internal TdsTypeInfo TypeInfo { get; }

    /// <summary>The wire layout of this column's values, resolved once from its type.</summary>
    internal TdsFieldLayout Layout { get; }
}

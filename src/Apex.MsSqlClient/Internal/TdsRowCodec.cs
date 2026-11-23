using System.Buffers;
using System.Buffers.Binary;

namespace Apex.MsSqlClient.Internal;

internal static class TdsRowCodec
{
    internal static void ReadRow(
        ref TdsPayloadReader reader,
        IReadOnlyList<TdsColumn> columns,
        bool nullCompressed,
        TdsRowBuffer row)
    {
        row.Clear();
        row.WriteUInt16LittleEndian(checked((ushort)columns.Count));
        var nullBitmap = nullCompressed
          ? reader.ReadSpan((columns.Count + 7) / 8)
          : default;
        for (var i = 0; i < columns.Count; i++)
        {
            var isNull = nullCompressed && (nullBitmap[i >> 3] & (1 << (i & 7))) != 0;
            WriteField(row, ref reader, columns[i].TypeInfo, isNull);
        }
    }

    /// <summary>
    /// Decodes a ROW or NBCROW token from <paramref name="source"/> when the whole row is present.
    /// Returns <see langword="false"/> without writing to <paramref name="row"/> when the row is
    /// incomplete, or when its lengths are invalid, so the caller can fall back to the incremental
    /// reader, which reads across packets and reports the error.
    /// </summary>
    internal static bool TryReadRow(
        ReadOnlySpan<byte> source,
        IReadOnlyList<TdsColumn> columns,
        bool nullCompressed,
        TdsRowBuffer row,
        out int consumed)
    {
        if (!TryGetRowLength(source, columns, nullCompressed, out consumed))
        {
            return false;
        }

        TdsPayloadReader reader = new(source[..consumed]);
        ReadRow(ref reader, columns, nullCompressed, row);
        return true;
    }

    /// <summary>
    /// Measures the wire length of a row without decoding it. This must accept exactly the layouts
    /// that <see cref="WriteField"/> accepts.
    /// </summary>
    internal static bool TryGetRowLength(
        ReadOnlySpan<byte> source,
        IReadOnlyList<TdsColumn> columns,
        bool nullCompressed,
        out int length)
    {
        length = 0;
        var position = 0;
        ReadOnlySpan<byte> nullBitmap = default;
        if (nullCompressed)
        {
            var bitmapLength = (columns.Count + 7) / 8;
            if (source.Length < bitmapLength)
            {
                return false;
            }

            nullBitmap = source[..bitmapLength];
            position = bitmapLength;
        }

        for (var i = 0; i < columns.Count; i++)
        {
            if (nullCompressed && (nullBitmap[i >> 3] & (1 << (i & 7))) != 0)
            {
                continue;
            }

            if (!TrySkipField(source, ref position, columns[i].TypeInfo))
            {
                return false;
            }
        }

        length = position;
        return true;
    }

    private static bool TrySkipField(
        ReadOnlySpan<byte> source,
        ref int position,
        TdsTypeInfo typeInfo)
    {
        if (typeInfo.Type == TdsDataType.Null)
        {
            return true;
        }

        var fixedLength = TdsTypeCodec.FixedLength(typeInfo.Type);
        if (fixedLength >= 0)
        {
            return TrySkip(source, ref position, fixedLength);
        }

        switch (typeInfo.Type)
        {
            case TdsDataType.Guid:
            case TdsDataType.IntN:
            case TdsDataType.BitN:
            case TdsDataType.Decimal:
            case TdsDataType.Numeric:
            case TdsDataType.DecimalN:
            case TdsDataType.NumericN:
            case TdsDataType.FloatN:
            case TdsDataType.MoneyN:
            case TdsDataType.DateTimeN:
            case TdsDataType.Date:
            case TdsDataType.Time:
            case TdsDataType.DateTime2:
            case TdsDataType.DateTimeOffset:
                if (position >= source.Length)
                {
                    return false;
                }

                int byteLength = source[position++];
                return TrySkip(source, ref position, byteLength);
            case TdsDataType.Binary:
            case TdsDataType.VarBinary:
            case TdsDataType.BigBinary:
            case TdsDataType.BigVarBinary:
            case TdsDataType.Char:
            case TdsDataType.VarChar:
            case TdsDataType.BigChar:
            case TdsDataType.BigVarChar:
            case TdsDataType.NVarChar:
            case TdsDataType.NChar:
                if (TdsTypeCodec.UsesPlp(typeInfo))
                {
                    return TrySkipPlp(source, ref position);
                }

                if (source.Length - position < sizeof(ushort))
                {
                    return false;
                }

                int length = BinaryPrimitives.ReadUInt16LittleEndian(source[position..]);
                position += sizeof(ushort);
                return length == ushort.MaxValue || TrySkip(source, ref position, length);
            case TdsDataType.Text:
            case TdsDataType.NText:
            case TdsDataType.Image:
                if (position >= source.Length)
                {
                    return false;
                }

                int pointerLength = source[position++];
                if (pointerLength == 0)
                {
                    return true;
                }

                if (!TrySkip(source, ref position, pointerLength + 8) ||
                    source.Length - position < sizeof(int))
                {
                    return false;
                }

                var dataLength = BinaryPrimitives.ReadInt32LittleEndian(source[position..]);
                position += sizeof(int);
                return dataLength >= 0 && TrySkip(source, ref position, dataLength);
            case TdsDataType.Xml:
            case TdsDataType.Json:
            case TdsDataType.Udt:
                return TrySkipPlp(source, ref position);
            default:
                return false;
        }
    }

    private static bool TrySkipPlp(ReadOnlySpan<byte> source, ref int position)
    {
        if (source.Length - position < sizeof(ulong))
        {
            return false;
        }

        var totalLength = BinaryPrimitives.ReadUInt64LittleEndian(source[position..]);
        position += sizeof(ulong);
        if (totalLength == ulong.MaxValue)
        {
            return true;
        }

        while (true)
        {
            if (source.Length - position < sizeof(uint))
            {
                return false;
            }

            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(source[position..]);
            position += sizeof(uint);
            if (chunkLength == 0)
            {
                return true;
            }

            if (chunkLength > (uint)(source.Length - position))
            {
                return false;
            }

            position += (int)chunkLength;
        }
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

    internal static void ReadValue(
        ref TdsPayloadReader reader,
        TdsTypeInfo typeInfo,
        TdsRowBuffer value)
    {
        value.Clear();
        WriteField(value, ref reader, typeInfo, isNull: false);
    }

    private static void WriteField(
        TdsRowBuffer row,
        ref TdsPayloadReader reader,
        TdsTypeInfo typeInfo,
        bool isNull)
    {
        if (isNull || typeInfo.Type == TdsDataType.Null)
        {
            row.WriteInt32LittleEndian(-1);
            return;
        }

        var fixedLength = TdsTypeCodec.FixedLength(typeInfo.Type);
        if (fixedLength >= 0)
        {
            WriteValue(row, reader.ReadSpan(fixedLength));
            return;
        }

        switch (typeInfo.Type)
        {
            case TdsDataType.Guid:
            case TdsDataType.IntN:
            case TdsDataType.BitN:
            case TdsDataType.Decimal:
            case TdsDataType.Numeric:
            case TdsDataType.DecimalN:
            case TdsDataType.NumericN:
            case TdsDataType.FloatN:
            case TdsDataType.MoneyN:
            case TdsDataType.DateTimeN:
            case TdsDataType.Date:
            case TdsDataType.Time:
            case TdsDataType.DateTime2:
            case TdsDataType.DateTimeOffset:
                int byteLength = reader.ReadByte();
                if (byteLength == 0)
                {
                    row.WriteInt32LittleEndian(-1);
                }
                else
                {
                    WriteValue(row, reader.ReadSpan(byteLength));
                }

                return;
            case TdsDataType.Binary:
            case TdsDataType.VarBinary:
            case TdsDataType.BigBinary:
            case TdsDataType.BigVarBinary:
            case TdsDataType.Char:
            case TdsDataType.VarChar:
            case TdsDataType.BigChar:
            case TdsDataType.BigVarChar:
            case TdsDataType.NVarChar:
            case TdsDataType.NChar:
                if (TdsTypeCodec.UsesPlp(typeInfo))
                {
                    WritePlp(row, ref reader);
                    return;
                }

                int length = reader.ReadUInt16LittleEndian();
                if (length == ushort.MaxValue)
                {
                    row.WriteInt32LittleEndian(-1);
                }
                else
                {
                    WriteValue(row, reader.ReadSpan(length));
                }

                return;
            case TdsDataType.Text:
            case TdsDataType.NText:
            case TdsDataType.Image:
                int pointerLength = reader.ReadByte();
                if (pointerLength == 0)
                {
                    row.WriteInt32LittleEndian(-1);
                    return;
                }

                reader.Skip(pointerLength);
                reader.Skip(8);
                var dataLength = reader.ReadInt32LittleEndian();
                WriteValue(row, reader.ReadSpan(dataLength));
                return;
            case TdsDataType.Xml:
            case TdsDataType.Json:
            case TdsDataType.Udt:
                WritePlp(row, ref reader);
                return;
            default:
                throw new NotSupportedException(
                  $"Cannot decode SQL Server row type 0x{typeInfo.Type:X2}.");
        }
    }

    private static void WritePlp(
        TdsRowBuffer row,
        ref TdsPayloadReader reader)
    {
        var totalLength = reader.ReadUInt64LittleEndian();
        if (totalLength == ulong.MaxValue)
        {
            row.WriteInt32LittleEndian(-1);
            return;
        }

        var lengthOffset = row.WrittenCount;
        row.WriteInt32LittleEndian(0);
        var valueOffset = row.WrittenCount;
        while (true)
        {
            var chunkLength = reader.ReadUInt32LittleEndian();
            if (chunkLength == 0)
            {
                break;
            }

            if (chunkLength > int.MaxValue)
            {
                throw new InvalidDataException("A SQL Server PLP chunk is too large.");
            }

            row.Write(reader.ReadSpan((int)chunkLength));
        }

        var valueLength = checked(row.WrittenCount - valueOffset);
        if (totalLength != 0xFFFF_FFFF_FFFF_FFFE && totalLength != (ulong)valueLength)
        {
            throw new InvalidDataException(
              $"SQL Server PLP value declared {totalLength} bytes but contained {valueLength}.");
        }

        row.PatchInt32LittleEndian(lengthOffset, valueLength);
    }

    private static void WriteValue(
        TdsRowBuffer row,
        ReadOnlySpan<byte> value)
    {
        row.WriteInt32LittleEndian(value.Length);
        row.Write(value);
    }
}

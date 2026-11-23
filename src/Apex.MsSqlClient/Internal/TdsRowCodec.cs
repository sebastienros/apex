using System.Buffers.Binary;

namespace Apex.MsSqlClient.Internal;

/// <summary>
/// Decodes ROW, NBCROW and RETURNVALUE values that are fully in memory into the
/// <see cref="TdsRowBuffer"/> row format: a 2-byte field count, then per field a 4-byte length
/// (-1 for null) and the value bytes. Field framing comes from <see cref="TdsFieldLayout"/>;
/// <see cref="TdsStreamingTokenReader"/> decodes the same layouts across packets.
/// </summary>
internal static class TdsRowCodec
{
    internal static void ReadRow(
        ref TdsPayloadReader reader,
        IReadOnlyList<TdsColumn> columns,
        bool nullCompressed,
        TdsRowBuffer row)
    {
        if (!TryReadRow(reader.RemainingSpan, columns, nullCompressed, row, out var consumed))
        {
            throw TdsPayloadReader.Truncated();
        }

        reader.Position += consumed;
    }

    /// <summary>
    /// Decodes a ROW or NBCROW token from <paramref name="source"/> in one pass. Returns
    /// <see langword="false"/> when the row continues past the end of <paramref name="source"/>,
    /// leaving <paramref name="row"/> partly written, so the caller can fall back to the
    /// incremental reader. Throws <see cref="InvalidDataException"/> when the row is complete but
    /// invalid.
    /// </summary>
    internal static bool TryReadRow(
        ReadOnlySpan<byte> source,
        IReadOnlyList<TdsColumn> columns,
        bool nullCompressed,
        TdsRowBuffer row,
        out int consumed)
    {
        consumed = 0;
        var count = columns.Count;
        var position = 0;
        ReadOnlySpan<byte> nullBitmap = default;
        if (nullCompressed)
        {
            var bitmapLength = (count + 7) / 8;
            if (source.Length < bitmapLength)
            {
                return false;
            }

            nullBitmap = source[..bitmapLength];
            position = bitmapLength;
        }

        row.Clear();
        row.WriteUInt16LittleEndian(checked((ushort)count));
        for (var i = 0; i < count; i++)
        {
            if (nullCompressed && (nullBitmap[i >> 3] & (1 << (i & 7))) != 0)
            {
                row.WriteNull();
                continue;
            }

            if (!TryReadField(source, ref position, columns[i].Layout, row))
            {
                return false;
            }
        }

        consumed = position;
        return true;
    }

    internal static void ReadValue(
        ref TdsPayloadReader reader,
        TdsTypeInfo typeInfo,
        TdsRowBuffer value)
    {
        value.Clear();
        var position = 0;
        if (!TryReadField(reader.RemainingSpan, ref position, TdsFieldLayout.For(typeInfo), value))
        {
            throw TdsPayloadReader.Truncated();
        }

        reader.Position += position;
    }

    /// <summary>
    /// Appends one field from <paramref name="source"/> at <paramref name="position"/>. Returns
    /// <see langword="false"/> when the field is incomplete.
    /// </summary>
    private static bool TryReadField(
        ReadOnlySpan<byte> source,
        ref int position,
        TdsFieldLayout layout,
        TdsRowBuffer row)
    {
        int length;
        switch (layout.Kind)
        {
            case TdsFieldKind.Fixed:
                length = layout.FixedLength;
                break;
            case TdsFieldKind.ByteLength:
                if (position >= source.Length)
                {
                    return false;
                }

                length = source[position++];
                if (length == 0)
                {
                    row.WriteNull();
                    return true;
                }

                break;
            case TdsFieldKind.UShortLength:
                if (source.Length - position < sizeof(ushort))
                {
                    return false;
                }

                length = BinaryPrimitives.ReadUInt16LittleEndian(source[position..]);
                position += sizeof(ushort);
                if (length == ushort.MaxValue)
                {
                    row.WriteNull();
                    return true;
                }

                break;
            case TdsFieldKind.Plp:
                return TryReadPlp(source, ref position, row);
            case TdsFieldKind.TextPointer:
                if (!TryReadTextPointerLength(source, ref position, out length))
                {
                    return false;
                }

                if (length < 0)
                {
                    row.WriteNull();
                    return true;
                }

                break;
            case TdsFieldKind.Null:
                row.WriteNull();
                return true;
            default:
                throw layout.Unsupported();
        }

        if (length > source.Length - position)
        {
            return false;
        }

        row.WriteValue(source.Slice(position, length));
        position += length;
        return true;
    }

    /// <summary>
    /// Reads a legacy LOB header. <paramref name="length"/> is -1 for a null value (no text
    /// pointer).
    /// </summary>
    private static bool TryReadTextPointerLength(
        ReadOnlySpan<byte> source,
        ref int position,
        out int length)
    {
        length = -1;
        if (position >= source.Length)
        {
            return false;
        }

        int pointerLength = source[position];
        if (pointerLength == 0)
        {
            position++;
            return true;
        }

        // Pointer length byte, pointer, 8-byte timestamp, 4-byte data length.
        var header = 1 + pointerLength + 8 + sizeof(int);
        if (header > source.Length - position)
        {
            return false;
        }

        length = BinaryPrimitives.ReadInt32LittleEndian(source[(position + header - sizeof(int))..]);
        if (length < 0)
        {
            throw NegativeLobLength();
        }

        position += header;
        return true;
    }

    private static bool TryReadPlp(ReadOnlySpan<byte> source, ref int position, TdsRowBuffer row)
    {
        if (source.Length - position < sizeof(ulong))
        {
            return false;
        }

        var totalLength = BinaryPrimitives.ReadUInt64LittleEndian(source[position..]);
        position += sizeof(ulong);
        if (totalLength == ulong.MaxValue)
        {
            row.WriteNull();
            return true;
        }

        var lengthOffset = row.WrittenCount;
        row.WriteInt32LittleEndian(0);
        var valueOffset = row.WrittenCount;
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
                break;
            }

            if (chunkLength > (uint)(source.Length - position))
            {
                return false;
            }

            row.Write(source.Slice(position, (int)chunkLength));
            position += (int)chunkLength;
        }

        CompletePlp(row, lengthOffset, valueOffset, totalLength);
        return true;
    }

    /// <summary>Checks a PLP value against its declared length and writes its field length.</summary>
    internal static void CompletePlp(
        TdsRowBuffer row,
        int lengthOffset,
        int valueOffset,
        ulong totalLength)
    {
        var valueLength = checked(row.WrittenCount - valueOffset);
        if (totalLength != UnknownPlpLength && totalLength != (ulong)valueLength)
        {
            throw new InvalidDataException(
              $"SQL Server PLP value declared {totalLength} bytes but contained {valueLength}.");
        }

        row.PatchInt32LittleEndian(lengthOffset, valueLength);
    }

    internal const ulong UnknownPlpLength = 0xFFFF_FFFF_FFFF_FFFE;

    internal static InvalidDataException NegativeLobLength() =>
      new("SQL Server LOB value has a negative length.");
}

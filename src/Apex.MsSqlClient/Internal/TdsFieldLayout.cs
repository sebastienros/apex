namespace Apex.MsSqlClient.Internal;

/// <summary>How a value is framed on the wire in a ROW, NBCROW or RETURNVALUE token.</summary>
internal enum TdsFieldKind : byte
{
    /// <summary>The type cannot be decoded.</summary>
    Unsupported,

    /// <summary>NULLTYPE: no bytes, always null.</summary>
    Null,

    /// <summary><see cref="TdsFieldLayout.FixedLength"/> bytes, never null.</summary>
    Fixed,

    /// <summary>A 1-byte length, then the value. A zero length is null.</summary>
    ByteLength,

    /// <summary>A 2-byte length, then the value. <c>0xFFFF</c> is null.</summary>
    UShortLength,

    /// <summary>
    /// Partially length-prefixed: an 8-byte total length (all ones is null), then chunks with
    /// 4-byte lengths, ended by a zero-length chunk.
    /// </summary>
    Plp,

    /// <summary>
    /// Legacy text, ntext and image: a 1-byte text pointer length (zero is null), the pointer, an
    /// 8-byte timestamp, a 4-byte length and the value.
    /// </summary>
    TextPointer,
}

/// <summary>
/// The wire layout of one column's values. It is resolved once per column from
/// <see cref="TdsTypeInfo"/>, so row decoders switch on a handful of layouts instead of on every
/// data type.
/// </summary>
internal readonly record struct TdsFieldLayout(TdsFieldKind Kind, byte FixedLength, byte Type)
{
    // One entry per TDS data type. This table is the only place that maps a type to its value
    // framing; the row decoders, the length probe and the streaming reader all read it.
    private static readonly Entry[] s_entries = CreateEntries();

    internal static TdsFieldLayout For(TdsTypeInfo typeInfo)
    {
        var entry = s_entries[typeInfo.Type];
        var kind = entry.PlpWhenMaximum && typeInfo.MaximumLength == ushort.MaxValue
          ? TdsFieldKind.Plp
          : entry.Kind;
        return new TdsFieldLayout(kind, entry.FixedLength, typeInfo.Type);
    }

    /// <summary>Returns the value length of a fixed-length type, or -1 for any other type.</summary>
    internal static int FixedLengthOf(byte type)
    {
        var entry = s_entries[type];
        return entry.Kind is TdsFieldKind.Fixed or TdsFieldKind.Null ? entry.FixedLength : -1;
    }

    internal NotSupportedException Unsupported() =>
      new($"Cannot decode SQL Server row type 0x{Type:X2}.");

    private static Entry[] CreateEntries()
    {
        var entries = new Entry[256];
        entries[TdsDataType.Null] = new(TdsFieldKind.Null, 0);

        entries[TdsDataType.Int1] = new(TdsFieldKind.Fixed, 1);
        entries[TdsDataType.Bit] = new(TdsFieldKind.Fixed, 1);
        entries[TdsDataType.Int2] = new(TdsFieldKind.Fixed, 2);
        entries[TdsDataType.Int4] = new(TdsFieldKind.Fixed, 4);
        entries[TdsDataType.DateTime4] = new(TdsFieldKind.Fixed, 4);
        entries[TdsDataType.Float4] = new(TdsFieldKind.Fixed, 4);
        entries[TdsDataType.Money4] = new(TdsFieldKind.Fixed, 4);
        entries[TdsDataType.Money] = new(TdsFieldKind.Fixed, 8);
        entries[TdsDataType.DateTime] = new(TdsFieldKind.Fixed, 8);
        entries[TdsDataType.Float8] = new(TdsFieldKind.Fixed, 8);
        entries[TdsDataType.Int8] = new(TdsFieldKind.Fixed, 8);

        foreach (var type in (ReadOnlySpan<byte>)
                 [
                     TdsDataType.Guid, TdsDataType.IntN, TdsDataType.BitN,
                     TdsDataType.Decimal, TdsDataType.Numeric,
                     TdsDataType.DecimalN, TdsDataType.NumericN,
                     TdsDataType.FloatN, TdsDataType.MoneyN, TdsDataType.DateTimeN,
                     TdsDataType.Date, TdsDataType.Time,
                     TdsDataType.DateTime2, TdsDataType.DateTimeOffset,
                 ])
        {
            entries[type] = new(TdsFieldKind.ByteLength, 0);
        }

        // The pre-7.0 short types keep a 2-byte length here, as they always have in this driver.
        foreach (var type in (ReadOnlySpan<byte>)
                 [TdsDataType.Binary, TdsDataType.VarBinary, TdsDataType.Char, TdsDataType.VarChar])
        {
            entries[type] = new(TdsFieldKind.UShortLength, 0);
        }

        // varchar(max), nvarchar(max) and varbinary(max) declare 0xFFFF and switch to PLP.
        foreach (var type in (ReadOnlySpan<byte>)
                 [
                     TdsDataType.BigBinary, TdsDataType.BigVarBinary,
                     TdsDataType.BigChar, TdsDataType.BigVarChar,
                     TdsDataType.NChar, TdsDataType.NVarChar,
                 ])
        {
            entries[type] = new(TdsFieldKind.UShortLength, 0, PlpWhenMaximum: true);
        }

        entries[TdsDataType.Text] = new(TdsFieldKind.TextPointer, 0);
        entries[TdsDataType.NText] = new(TdsFieldKind.TextPointer, 0);
        entries[TdsDataType.Image] = new(TdsFieldKind.TextPointer, 0);

        entries[TdsDataType.Xml] = new(TdsFieldKind.Plp, 0);
        entries[TdsDataType.Json] = new(TdsFieldKind.Plp, 0);
        entries[TdsDataType.Udt] = new(TdsFieldKind.Plp, 0);
        return entries;
    }

    private readonly record struct Entry(
        TdsFieldKind Kind,
        byte FixedLength,
        bool PlpWhenMaximum = false);
}

using System.Buffers;
using Apex.MsSqlClient.Internal;

namespace Apex.MsSqlClient.Tests;

/// <summary>
/// Checks that the whole-payload decoder, the single-pass buffered decoder and the incremental
/// cross-packet reader agree on every supported data type, because all three read the layout
/// from <see cref="TdsFieldLayout"/>.
/// </summary>
[TestClass]
public sealed class TdsFieldLayoutTests
{
    private const int UnicodeCollation = 0x00D00409;

    [TestMethod]
    public void EverySupportedTypeHasALayout()
    {
        foreach (var column in AllTypesColumns())
        {
            Assert.AreNotEqual(
              TdsFieldKind.Unsupported,
              column.Layout.Kind,
              $"Type 0x{column.TypeInfo.Type:X2} has no wire layout.");
        }
    }

    [TestMethod]
    public void MaximumLengthSwitchesLargeTypesToPlp()
    {
        Assert.AreEqual(
          TdsFieldKind.Plp,
          TdsFieldLayout.For(new TdsTypeInfo(TdsDataType.NVarChar, ushort.MaxValue)).Kind);
        Assert.AreEqual(
          TdsFieldKind.UShortLength,
          TdsFieldLayout.For(new TdsTypeInfo(TdsDataType.NVarChar, 8000)).Kind);
        Assert.AreEqual(
          TdsFieldKind.UShortLength,
          TdsFieldLayout.For(new TdsTypeInfo(TdsDataType.VarChar, ushort.MaxValue)).Kind);
        Assert.AreEqual(8, TdsTypeCodec.FixedLength(TdsDataType.Int8));
        Assert.AreEqual(0, TdsTypeCodec.FixedLength(TdsDataType.Null));
        Assert.AreEqual(-1, TdsTypeCodec.FixedLength(TdsDataType.IntN));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SinglePassDecoderRejectsEveryTruncation(bool nullCompressed)
    {
        var columns = AllTypesColumns();
        foreach (var variant in new[] { 0, 1 })
        {
            var encoded = EncodeRow(variant, nullCompressed);
            TdsRowBuffer expected = new();
            TdsPayloadReader whole = new(encoded);
            TdsRowCodec.ReadRow(ref whole, columns, nullCompressed, expected);
            Assert.AreEqual(encoded.Length, whole.Position);

            TdsRowBuffer row = new(8);
            Assert.IsTrue(TdsRowCodec.TryReadRow(encoded, columns, nullCompressed, row, out var consumed));
            Assert.AreEqual(encoded.Length, consumed);
            CollectionAssert.AreEqual(expected.WrittenSpan.ToArray(), row.WrittenSpan.ToArray());

            for (var length = 0; length < encoded.Length; length++)
            {
                Assert.IsFalse(
                  TdsRowCodec.TryReadRow(encoded.AsSpan(0, length), columns, nullCompressed, row, out _),
                  $"A row cut to {length} of {encoded.Length} bytes was accepted.");
                var truncated = encoded[..length];
                Assert.ThrowsExactly<InvalidDataException>(() =>
                {
                    TdsPayloadReader reader = new(truncated);
                    TdsRowCodec.ReadRow(ref reader, columns, nullCompressed, new TdsRowBuffer());
                });
            }
        }
    }

    [TestMethod]
    [DataRow(9, 1)]
    [DataRow(11, 7)]
    [DataRow(16, int.MaxValue)]
    [DataRow(512, 3)]
    [DataRow(4096, int.MaxValue)]
    public async Task IncrementalReaderMatchesSinglePassDecoder(int packetSize, int readChunk)
    {
        var columns = AllTypesColumns();
        ArrayBufferWriter<byte> payload = new();
        List<byte[]> expected = [];
        for (var i = 0; i < 6; i++)
        {
            var nullCompressed = i % 2 == 1;
            var encoded = EncodeRow(i / 2 % 2, nullCompressed);
            payload.WriteByte(nullCompressed ? TdsTokenType.NbcRow : TdsTokenType.Row);
            payload.Write(encoded);
            TdsRowBuffer row = new();
            Assert.IsTrue(TdsRowCodec.TryReadRow(encoded, columns, nullCompressed, row, out _));
            expected.Add(row.WrittenSpan.ToArray());
        }

        var reader = TdsPacketReader.CreateBuffered(
          new ChunkedStream(await FrameAsync(packetSize, payload.WrittenSpan.ToArray()), readChunk),
          512);
        _ = await reader.BeginMessageAsync(default);
        TdsStreamingTokenReader tokens = new(reader, default);
        TdsRowBuffer actual = new(8);
        foreach (var row in expected)
        {
            var token = await tokens.ReadTokenTypeAsync();
            await tokens.ReadRowAsync(columns, token == TdsTokenType.NbcRow, actual);
            CollectionAssert.AreEqual(row, actual.WrittenSpan.ToArray());
        }

        Assert.IsFalse(tokens.HasRemaining);
        reader.Release();
    }

    [TestMethod]
    public async Task IncrementalReaderRejectsRowsTruncatedAtEndOfMessage()
    {
        var columns = AllTypesColumns();
        var encoded = EncodeRow(variant: 0, nullCompressed: false);
        for (var length = 0; length < encoded.Length; length += 3)
        {
            var reader = TdsPacketReader.CreateBuffered(
              new ChunkedStream(await FrameAsync(16, encoded[..length]), 5),
              512);
            _ = await reader.BeginMessageAsync(default);
            TdsStreamingTokenReader tokens = new(reader, default);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(
              () => tokens.ReadRowAsync(columns, nullCompressed: false, new TdsRowBuffer()).AsTask(),
              $"A row cut to {length} of {encoded.Length} bytes was accepted.");
            reader.Release();
        }
    }

    [TestMethod]
    public async Task NegativeLegacyLobLengthIsRejectedByEveryDecoder()
    {
        TdsColumn[] columns =
        [
            Column(new TdsTypeInfo(TdsDataType.Image, int.MaxValue), "image"),
        ];
        ArrayBufferWriter<byte> encoded = new();
        encoded.WriteByte(16);
        encoded.Write(new byte[16 + 8]);
        encoded.WriteInt32LittleEndian(-5);
        var bytes = encoded.WrittenSpan.ToArray();

        Assert.ThrowsExactly<InvalidDataException>(
          () => TdsRowCodec.TryReadRow(bytes, columns, nullCompressed: false, new TdsRowBuffer(), out _));

        // Split the row across packets so the incremental reader decodes it.
        var reader = TdsPacketReader.CreateBuffered(
          new ChunkedStream(await FrameAsync(12, bytes), 1),
          512);
        _ = await reader.BeginMessageAsync(default);
        TdsStreamingTokenReader tokens = new(reader, default);
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(
          () => tokens.ReadRowAsync(columns, nullCompressed: false, new TdsRowBuffer()).AsTask());
        StringAssert.Contains(error.Message, "negative length");
        reader.Release();
    }

    [TestMethod]
    public void UnsupportedTypeIsReportedByType()
    {
        TdsColumn[] columns = [Column(new TdsTypeInfo(0x99), "unknown")];
        var error = Assert.ThrowsExactly<NotSupportedException>(
          () => TdsRowCodec.TryReadRow([1, 2, 3], columns, nullCompressed: false, new TdsRowBuffer(), out _));
        StringAssert.Contains(error.Message, "0x99");
    }

    /// <summary>One column of every data type the driver decodes, with representative metadata.</summary>
    internal static TdsColumn[] AllTypesColumns() =>
    [
        Column(new TdsTypeInfo(TdsDataType.Null), "null"),
        Column(new TdsTypeInfo(TdsDataType.Int1, 1), "int1"),
        Column(new TdsTypeInfo(TdsDataType.Bit, 1), "bit"),
        Column(new TdsTypeInfo(TdsDataType.Int2, 2), "int2"),
        Column(new TdsTypeInfo(TdsDataType.Int4, 4), "int4"),
        Column(new TdsTypeInfo(TdsDataType.Int8, 8), "int8"),
        Column(new TdsTypeInfo(TdsDataType.DateTime4, 4), "smalldatetime"),
        Column(new TdsTypeInfo(TdsDataType.Float4, 4), "real"),
        Column(new TdsTypeInfo(TdsDataType.Money4, 4), "smallmoney"),
        Column(new TdsTypeInfo(TdsDataType.Money, 8), "money"),
        Column(new TdsTypeInfo(TdsDataType.DateTime, 8), "datetime"),
        Column(new TdsTypeInfo(TdsDataType.Float8, 8), "float"),
        Column(new TdsTypeInfo(TdsDataType.Guid, 16), "guid"),
        Column(new TdsTypeInfo(TdsDataType.IntN, 4), "intn"),
        Column(new TdsTypeInfo(TdsDataType.BitN, 1), "bitn"),
        Column(new TdsTypeInfo(TdsDataType.Decimal, 17, 38, 4), "decimal"),
        Column(new TdsTypeInfo(TdsDataType.Numeric, 17, 38, 4), "numeric"),
        Column(new TdsTypeInfo(TdsDataType.DecimalN, 9, 18, 2), "decimaln"),
        Column(new TdsTypeInfo(TdsDataType.NumericN, 9, 18, 2), "numericn"),
        Column(new TdsTypeInfo(TdsDataType.FloatN, 8), "floatn"),
        Column(new TdsTypeInfo(TdsDataType.MoneyN, 8), "moneyn"),
        Column(new TdsTypeInfo(TdsDataType.DateTimeN, 8), "datetimen"),
        Column(new TdsTypeInfo(TdsDataType.Date, 3), "date"),
        Column(new TdsTypeInfo(TdsDataType.Time, 5, Scale: 7), "time"),
        Column(new TdsTypeInfo(TdsDataType.DateTime2, 8, Scale: 7), "datetime2"),
        Column(new TdsTypeInfo(TdsDataType.DateTimeOffset, 10, Scale: 7), "datetimeoffset"),
        Column(new TdsTypeInfo(TdsDataType.Binary, 4), "binary"),
        Column(new TdsTypeInfo(TdsDataType.VarBinary, 4), "varbinary"),
        Column(new TdsTypeInfo(TdsDataType.BigBinary, 4), "bigbinary"),
        Column(new TdsTypeInfo(TdsDataType.BigVarBinary, 16), "bigvarbinary"),
        Column(new TdsTypeInfo(TdsDataType.BigVarBinary, ushort.MaxValue), "varbinarymax"),
        Column(Character(TdsDataType.Char, 4, UnicodeCollation), "char"),
        Column(Character(TdsDataType.VarChar, 4, UnicodeCollation), "varchar"),
        Column(Character(TdsDataType.BigChar, 4, UnicodeCollation), "bigchar"),
        Column(Character(TdsDataType.BigVarChar, 16, UnicodeCollation), "bigvarchar"),
        Column(Character(TdsDataType.BigVarChar, ushort.MaxValue, UnicodeCollation), "varcharmax"),
        Column(Character(TdsDataType.NChar, 8, UnicodeCollation), "nchar"),
        Column(Character(TdsDataType.NVarChar, 64, UnicodeCollation), "nvarchar"),
        Column(Character(TdsDataType.NVarChar, ushort.MaxValue, UnicodeCollation), "nvarcharmax"),
        Column(Character(TdsDataType.Text, int.MaxValue, UnicodeCollation), "text"),
        Column(Character(TdsDataType.NText, int.MaxValue, UnicodeCollation), "ntext"),
        Column(new TdsTypeInfo(TdsDataType.Image, int.MaxValue), "image"),
        Column(new TdsTypeInfo(TdsDataType.Xml, ushort.MaxValue), "xml"),
        Column(new TdsTypeInfo(TdsDataType.Json, ushort.MaxValue), "json"),
        Column(new TdsTypeInfo(TdsDataType.Udt, 8000), "udt"),
    ];

    /// <summary>
    /// Encodes one row for <see cref="AllTypesColumns"/>. Variant 0 carries a value in every
    /// nullable column; variant 1 sends each nullable column's null form. NBCROW rows also mark
    /// every third column null in the bitmap.
    /// </summary>
    internal static byte[] EncodeRow(int variant, bool nullCompressed)
    {
        var columns = AllTypesColumns();
        ArrayBufferWriter<byte> row = new();
        var bitmap = new byte[(columns.Length + 7) / 8];
        if (nullCompressed)
        {
            for (var i = 0; i < columns.Length; i += 3)
            {
                bitmap[i >> 3] |= (byte)(1 << (i & 7));
            }

            row.Write(bitmap);
        }

        var nulls = variant == 1;
        for (var i = 0; i < columns.Length; i++)
        {
            if (nullCompressed && (bitmap[i >> 3] & (1 << (i & 7))) != 0)
            {
                continue;
            }

            var layout = columns[i].Layout;
            switch (layout.Kind)
            {
                case TdsFieldKind.Null:
                    break;
                case TdsFieldKind.Fixed:
                    row.Write(Bytes(layout.FixedLength, i));
                    break;
                case TdsFieldKind.ByteLength:
                    if (nulls)
                    {
                        row.WriteByte(0);
                    }
                    else
                    {
                        var length = columns[i].TypeInfo.MaximumLength;
                        row.WriteByte(checked((byte)length));
                        row.Write(Bytes(length, i));
                    }

                    break;
                case TdsFieldKind.UShortLength:
                    if (nulls)
                    {
                        row.WriteUInt16LittleEndian(ushort.MaxValue);
                    }
                    else
                    {
                        row.WriteUInt16LittleEndian(4);
                        row.Write(Bytes(4, i));
                    }

                    break;
                case TdsFieldKind.Plp:
                    if (nulls)
                    {
                        row.WriteUInt64LittleEndian(ulong.MaxValue);
                    }
                    else
                    {
                        // Alternate between a known and an unknown total length.
                        row.WriteUInt64LittleEndian(i % 2 == 0 ? 7 : TdsRowCodec.UnknownPlpLength);
                        row.WriteUInt32LittleEndian(3);
                        row.Write(Bytes(3, i));
                        row.WriteUInt32LittleEndian(4);
                        row.Write(Bytes(4, i + 1));
                        row.WriteUInt32LittleEndian(0);
                    }

                    break;
                case TdsFieldKind.TextPointer:
                    if (nulls)
                    {
                        row.WriteByte(0);
                    }
                    else
                    {
                        row.WriteByte(16);
                        row.Write(Bytes(16, i));
                        row.Write(Bytes(8, i + 1));
                        row.WriteInt32LittleEndian(5);
                        row.Write(Bytes(5, i + 2));
                    }

                    break;
                default:
                    throw new InvalidOperationException(layout.Kind.ToString());
            }
        }

        return row.WrittenSpan.ToArray();
    }

    private static TdsColumn Column(TdsTypeInfo typeInfo, string name) =>
      new(typeInfo.ToColumn(name), typeInfo);

    private static TdsTypeInfo Character(byte type, int maximumLength, int collation) =>
      new(type, maximumLength, Collation: new TdsCollation((uint)collation, 0, 1252));

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = unchecked((byte)(seed * 31 + i));
        }

        return bytes;
    }

    private static async Task<byte[]> FrameAsync(int packetSize, byte[] payload)
    {
        using MemoryStream framed = new();
        await TdsPacketWriter.WriteMessageCoreAsync(
          framed,
          TdsMessageType.TabularResult,
          payload,
          packetSize,
          default);
        return framed.ToArray();
    }

    /// <summary>Returns at most <c>chunk</c> bytes per read, like a socket delivering segments.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
          Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(Math.Min(buffer.Length, chunk), data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
          new(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
          throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
          throw new NotSupportedException();
    }
}

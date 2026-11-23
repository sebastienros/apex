using System.Buffers;
using Apex.MsSqlClient.Internal;

namespace Apex.MsSqlClient.Tests;

[TestClass]
public sealed class TdsResultShapeCacheTests
{
    private const uint UnicodeCollation = 0x00D00409;

    [TestMethod]
    public void MeasureMatchesTheParserForEveryType()
    {
        var metadata = BuildAllTypesMetadata();

        Assert.AreEqual(metadata.Length, TdsTypeCodec.MeasureColumns(metadata));
        for (var length = 0; length < metadata.Length; length++)
        {
            Assert.AreEqual(
              -1,
              TdsTypeCodec.MeasureColumns(metadata.AsSpan(0, length)),
              $"COLMETADATA cut to {length} of {metadata.Length} bytes measured as complete.");
        }

        var shape = TdsResultShapeCache.Parse(metadata);
        Assert.HasCount(AllTypeInfos().Length, shape.Columns);
        for (var i = 0; i < shape.Columns.Length; i++)
        {
            Assert.AreEqual($"c{i}", shape.SqlColumns[i].Name);
            Assert.AreNotEqual(TdsFieldKind.Unsupported, shape.Columns[i].Layout.Kind);
        }
    }

    [TestMethod]
    public void MeasureMatchesTheParserForEveryReturnValueType()
    {
        foreach (var typeInfo in AllTypeInfos())
        {
            ArrayBufferWriter<byte> header = new();
            header.WriteUInt16LittleEndian(3);
            header.WriteBVarChar("@value");
            header.WriteByte(1);
            header.WriteUInt32LittleEndian(0);
            header.WriteUInt16LittleEndian(0);
            header.Write(typeInfo);
            var bytes = header.WrittenSpan.ToArray();

            Assert.AreEqual(bytes.Length, TdsTypeCodec.MeasureReturnValueHeader(bytes));
            for (var length = 0; length < bytes.Length; length++)
            {
                Assert.AreEqual(-1, TdsTypeCodec.MeasureReturnValueHeader(bytes.AsSpan(0, length)));
            }
        }
    }

    [TestMethod]
    public void MeasureRejectsUnsupportedTypes()
    {
        ArrayBufferWriter<byte> metadata = new();
        metadata.WriteUInt16LittleEndian(1);
        metadata.WriteUInt32LittleEndian(0);
        metadata.WriteUInt16LittleEndian(0);
        metadata.WriteByte(0x62);
        metadata.WriteBVarChar("variant");
        var bytes = metadata.WrittenSpan.ToArray();

        var error = Assert.ThrowsExactly<NotSupportedException>(() => TdsTypeCodec.MeasureColumns(bytes));
        StringAssert.Contains(error.Message, "0x62");
    }

    [TestMethod]
    public void RepeatedMetadataReusesTheParsedShape()
    {
        TdsResultShapeCache cache = new();
        var metadata = BuildAllTypesMetadata();
        var other = BuildIntMetadata("other");

        var first = cache.GetOrAdd(metadata);
        Assert.AreSame(first, cache.GetOrAdd(metadata.ToArray()));
        Assert.AreSame(first.SqlColumns, cache.GetOrAdd(metadata).SqlColumns);
        Assert.AreNotSame(first, cache.GetOrAdd(other));
        Assert.AreSame(first, cache.GetOrAdd(metadata));
        Assert.AreSame(TdsResultShape.Empty, cache.GetOrAdd([0xFF, 0xFF]));
    }

    [TestMethod]
    public void OversizedMetadataIsParsedButNotCached()
    {
        TdsResultShapeCache cache = new();
        ArrayBufferWriter<byte> metadata = new();
        const int count = 200;
        metadata.WriteUInt16LittleEndian(count);
        for (var i = 0; i < count; i++)
        {
            metadata.WriteUInt32LittleEndian(0);
            metadata.WriteUInt16LittleEndian(0);
            metadata.WriteByte(TdsDataType.Int4);
            metadata.WriteBVarChar(new string((char)('a' + i % 26), 50) + i);
        }

        var bytes = metadata.WrittenSpan.ToArray();
        Assert.IsGreaterThan(TdsResultShapeCache.MaximumCachedKeyLength, bytes.Length);

        var first = cache.GetOrAdd(bytes);
        var second = cache.GetOrAdd(bytes);
        Assert.HasCount(count, first.Columns);
        Assert.AreNotSame(first, second);
    }

    [TestMethod]
    [DataRow(9, 1)]
    [DataRow(10, 7)]
    [DataRow(16, int.MaxValue)]
    [DataRow(64, 3)]
    [DataRow(4096, int.MaxValue)]
    public async Task MetadataSplitAcrossPacketsIsCollectedAndCached(int packetSize, int readChunk)
    {
        var metadata = BuildAllTypesMetadata();
        ArrayBufferWriter<byte> payload = new();
        for (var i = 0; i < 3; i++)
        {
            payload.WriteByte(TdsTokenType.ColumnMetadata);
            payload.Write(metadata);
            WriteDone(payload);
        }

        TdsResultShapeCache cache = new();
        var reader = TdsPacketReader.CreateBuffered(
          new ChunkedStream(await FrameAsync(packetSize, payload.WrittenSpan.ToArray()), readChunk),
          512);
        _ = await reader.BeginMessageAsync(default);
        TdsStreamingTokenReader tokens = new(reader, default);
        var expected = TdsResultShapeCache.Parse(metadata);
        TdsResultShape? first = null;
        for (var i = 0; i < 3; i++)
        {
            Assert.AreEqual(TdsTokenType.ColumnMetadata, await tokens.ReadTokenTypeAsync());
            var shape = await tokens.ReadColumnMetadataAsync(cache);
            CollectionAssert.AreEqual(expected.Columns, shape.Columns);
            first ??= shape;
            Assert.AreSame(first, shape);
            Assert.AreEqual(TdsTokenType.Done, await tokens.ReadTokenTypeAsync());
            _ = await tokens.ReadDoneAsync();
        }

        Assert.IsFalse(tokens.HasRemaining);
        reader.Release();
    }

    [TestMethod]
    public async Task MetadataTruncatedAtEndOfMessageIsRejected()
    {
        var metadata = BuildAllTypesMetadata();
        for (var length = 0; length < metadata.Length; length += 5)
        {
            var reader = TdsPacketReader.CreateBuffered(
              new ChunkedStream(await FrameAsync(32, metadata[..length]), 4),
              512);
            _ = await reader.BeginMessageAsync(default);
            TdsStreamingTokenReader tokens = new(reader, default);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(
              () => tokens.ReadColumnMetadataAsync(new TdsResultShapeCache()).AsTask());
            reader.Release();
        }
    }

    [TestMethod]
    [DataRow(9, 1)]
    [DataRow(12, 5)]
    [DataRow(4096, int.MaxValue)]
    public async Task ReturnValueSplitAcrossPacketsMatchesWholeMessage(int packetSize, int readChunk)
    {
        ArrayBufferWriter<byte> payload = new();
        payload.WriteByte(TdsTokenType.ReturnValue);
        payload.WriteUInt16LittleEndian(2);
        payload.WriteBVarChar("@greeting");
        payload.WriteByte(1);
        payload.WriteUInt32LittleEndian(0);
        payload.WriteUInt16LittleEndian(0);
        payload.WriteByte(TdsDataType.NVarChar);
        payload.WriteUInt16LittleEndian(200);
        payload.WriteUInt32LittleEndian(UnicodeCollation);
        payload.WriteByte(0);
        payload.WriteUInt16LittleEndian(10);
        payload.WriteUtf16("hello");
        var bytes = payload.WrittenSpan.ToArray();

        TdsTokenReader whole = new(bytes);
        Assert.AreEqual(TdsTokenType.ReturnValue, whole.ReadTokenType());
        var expected = whole.ReadReturnValue();

        var reader = TdsPacketReader.CreateBuffered(
          new ChunkedStream(await FrameAsync(packetSize, bytes), readChunk),
          512);
        _ = await reader.BeginMessageAsync(default);
        TdsStreamingTokenReader tokens = new(reader, default);
        Assert.AreEqual(TdsTokenType.ReturnValue, await tokens.ReadTokenTypeAsync());
        var actual = await tokens.ReadReturnValueAsync();

        Assert.AreEqual(expected.Ordinal, actual.Ordinal);
        Assert.AreEqual("@greeting", actual.Name);
        Assert.IsTrue(actual.IsOutput);
        Assert.AreEqual(expected.TypeInfo, actual.TypeInfo);
        CollectionAssert.AreEqual(expected.Value!.Value.ToArray(), actual.Value!.Value.ToArray());
        Assert.IsFalse(tokens.HasRemaining);
        reader.Release();
    }

    [TestMethod]
    public void ReusedParserDropsARowBufferGrownForALargeValue()
    {
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        response.WriteUInt32LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteByte(TdsDataType.BigVarBinary);
        response.WriteUInt16LittleEndian(ushort.MaxValue);
        response.WriteBVarChar("blob");
        var value = new byte[200_000];
        response.WriteByte(TdsTokenType.Row);
        response.WriteUInt64LittleEndian((ulong)value.Length);
        response.WriteUInt32LittleEndian((uint)value.Length);
        response.Write(value);
        response.WriteUInt32LittleEndian(0);
        response.WriteByte(TdsTokenType.Done);
        response.WriteUInt16LittleEndian((ushort)TdsDoneStatus.Count);
        response.WriteUInt16LittleEndian(0xC1);
        response.WriteInt64LittleEndian(1);
        TdsQueryParser parser = new(new MsSqlRowDecoder(), new TdsResultShapeCache());

        var rows = parser.Parse(response.WrittenMemory).Rows;

        Assert.HasCount(1, rows);
        Assert.IsGreaterThan(value.Length, parser.RowBufferCapacity);
        parser.Reset();
        Assert.IsLessThanOrEqualTo(64 * 1024, parser.RowBufferCapacity);
    }

    /// <summary>The TYPE_INFO of every supported type, with optional parts present.</summary>
    internal static byte[][] AllTypeInfos()
    {
        List<byte[]> types = [];
        void Add(Action<ArrayBufferWriter<byte>> write)
        {
            ArrayBufferWriter<byte> info = new();
            write(info);
            types.Add(info.WrittenSpan.ToArray());
        }

        foreach (var type in (byte[])
                 [
                     TdsDataType.Null, TdsDataType.Int1, TdsDataType.Bit, TdsDataType.Int2,
                     TdsDataType.Int4, TdsDataType.Int8, TdsDataType.DateTime4, TdsDataType.Float4,
                     TdsDataType.Money4, TdsDataType.Money, TdsDataType.DateTime, TdsDataType.Float8,
                     TdsDataType.Date, TdsDataType.Json,
                 ])
        {
            Add(info => info.WriteByte(type));
        }

        foreach (var (type, length) in (ReadOnlySpan<(byte, byte)>)
                 [
                     (TdsDataType.Guid, 16), (TdsDataType.IntN, 4), (TdsDataType.BitN, 1),
                     (TdsDataType.FloatN, 8), (TdsDataType.MoneyN, 8), (TdsDataType.DateTimeN, 8),
                     (TdsDataType.Time, 7), (TdsDataType.DateTime2, 3), (TdsDataType.DateTimeOffset, 0),
                 ])
        {
            Add(info =>
            {
                info.WriteByte(type);
                info.WriteByte(length);
            });
        }

        foreach (var type in (byte[])
                 [TdsDataType.Decimal, TdsDataType.Numeric, TdsDataType.DecimalN, TdsDataType.NumericN])
        {
            Add(info =>
            {
                info.WriteByte(type);
                info.WriteByte(17);
                info.WriteByte(38);
                info.WriteByte(4);
            });
        }

        foreach (var type in (byte[])
                 [TdsDataType.Binary, TdsDataType.VarBinary, TdsDataType.BigBinary, TdsDataType.BigVarBinary])
        {
            Add(info =>
            {
                info.WriteByte(type);
                info.WriteUInt16LittleEndian(ushort.MaxValue);
            });
        }

        foreach (var type in (byte[])
                 [
                     TdsDataType.Char, TdsDataType.VarChar, TdsDataType.BigChar, TdsDataType.BigVarChar,
                     TdsDataType.NChar, TdsDataType.NVarChar,
                 ])
        {
            Add(info =>
            {
                info.WriteByte(type);
                info.WriteUInt16LittleEndian(80);
                info.WriteUInt32LittleEndian(UnicodeCollation);
                info.WriteByte(0);
            });
        }

        foreach (var type in (byte[])[TdsDataType.Text, TdsDataType.NText, TdsDataType.Image])
        {
            Add(info =>
            {
                info.WriteByte(type);
                info.WriteInt32LittleEndian(int.MaxValue);
                if (type != TdsDataType.Image)
                {
                    info.WriteUInt32LittleEndian(UnicodeCollation);
                    info.WriteByte(0);
                }

                info.WriteByte(2);
                WriteUsVarChar(info, "dbo");
                WriteUsVarChar(info, "documents");
            });
        }

        Add(info =>
        {
            info.WriteByte(TdsDataType.Xml);
            info.WriteByte(0);
        });
        Add(info =>
        {
            info.WriteByte(TdsDataType.Xml);
            info.WriteByte(1);
            info.WriteBVarChar("db");
            info.WriteBVarChar("dbo");
            WriteUsVarChar(info, "schema");
        });
        Add(info =>
        {
            info.WriteByte(TdsDataType.Udt);
            info.WriteUInt16LittleEndian(8000);
            info.WriteBVarChar("db");
            info.WriteBVarChar("sys");
            info.WriteBVarChar("hierarchyid");
            WriteUsVarChar(info, "Microsoft.SqlServer.Types.SqlHierarchyId");
        });
        return [.. types];
    }

    private static byte[] BuildAllTypesMetadata()
    {
        var types = AllTypeInfos();
        ArrayBufferWriter<byte> metadata = new();
        metadata.WriteUInt16LittleEndian(checked((ushort)types.Length));
        for (var i = 0; i < types.Length; i++)
        {
            metadata.WriteUInt32LittleEndian(0);
            metadata.WriteUInt16LittleEndian(0x09);
            metadata.Write(types[i]);
            metadata.WriteBVarChar($"c{i}");
        }

        return metadata.WrittenSpan.ToArray();
    }

    private static byte[] BuildIntMetadata(string name)
    {
        ArrayBufferWriter<byte> metadata = new();
        metadata.WriteUInt16LittleEndian(1);
        metadata.WriteUInt32LittleEndian(0);
        metadata.WriteUInt16LittleEndian(0);
        metadata.WriteByte(TdsDataType.Int4);
        metadata.WriteBVarChar(name);
        return metadata.WrittenSpan.ToArray();
    }

    private static void WriteUsVarChar(ArrayBufferWriter<byte> writer, string value)
    {
        writer.WriteUInt16LittleEndian(checked((ushort)value.Length));
        writer.WriteUtf16(value);
    }

    private static void WriteDone(ArrayBufferWriter<byte> response)
    {
        response.WriteByte(TdsTokenType.Done);
        response.WriteUInt16LittleEndian((ushort)TdsDoneStatus.More);
        response.WriteUInt16LittleEndian(0xC1);
        response.WriteInt64LittleEndian(0);
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

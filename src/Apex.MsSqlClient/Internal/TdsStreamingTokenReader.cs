using System.Buffers.Binary;

namespace Apex.MsSqlClient.Internal;

/// <summary>Returns the length of a complete token body at the start of a span, or -1.</summary>
internal delegate int TdsTokenMeasure(ReadOnlySpan<byte> source);

internal sealed class TdsStreamingTokenReader
{
    private const int DoneTokenLength = 12;
    private static readonly TdsTokenMeasure s_measureColumns = TdsTypeCodec.MeasureColumns;
    private static readonly TdsTokenMeasure s_measureReturnValueHeader =
      TdsTypeCodec.MeasureReturnValueHeader;
    private const int RetainedValueBufferLimit = 64 * 1024;
    private readonly TdsPacketReader _reader;
    private CancellationToken _cancellationToken;
    private byte[]? _primitive;
    private TdsRowBuffer? _discarded;
    private byte[] _nullBitmap = [];

    internal TdsStreamingTokenReader(
        TdsPacketReader reader,
        CancellationToken cancellationToken)
    {
        _reader = reader;
        _cancellationToken = cancellationToken;
    }

    internal bool HasRemaining => !_reader.EndOfMessage;

    /// <summary>
    /// Prepares a reader kept by its connection for the next command, which reads under
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    internal void Reset(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        if (_discarded?.Capacity > RetainedValueBufferLimit)
        {
            _discarded = null;
        }
    }

    // Only reads that cross a packet or receive boundary need it.
    private byte[] Primitive => _primitive ??= new byte[8];

    internal ValueTask<byte> ReadTokenTypeAsync() => ReadByteAsync();

    /// <summary>Reads COLMETADATA without caching it.</summary>
    internal async ValueTask<IReadOnlyList<TdsColumn>> ReadColumnsAsync() =>
      (await ReadColumnMetadataAsync(cache: null).ConfigureAwait(false)).Columns;

    /// <summary>
    /// Reads COLMETADATA. With a <paramref name="cache"/>, a result shape seen before is reused
    /// instead of parsed again.
    /// </summary>
    internal ValueTask<TdsResultShape> ReadColumnMetadataAsync(TdsResultShapeCache? cache)
    {
        var buffered = _reader.GetBufferedPayload();
        var length = TdsTypeCodec.MeasureColumns(buffered);
        if (length >= 0)
        {
            var shape = GetShape(cache, buffered[..length]);
            _reader.AdvancePayload(length);
            return new ValueTask<TdsResultShape>(shape);
        }

        return ReadColumnMetadataSlowAsync(cache);
    }

    private async ValueTask<TdsResultShape> ReadColumnMetadataSlowAsync(TdsResultShapeCache? cache)
    {
        try
        {
            var metadata = await CollectAsync(s_measureColumns, cache).ConfigureAwait(false);
            return GetShape(cache, metadata.Span);
        }
        finally
        {
            cache?.TrimScratch();
        }
    }

    private static TdsResultShape GetShape(TdsResultShapeCache? cache, ReadOnlySpan<byte> metadata) =>
      cache is null ? TdsResultShapeCache.Parse(metadata) : cache.GetOrAdd(metadata);

    internal ValueTask ReadRowAsync(
        IReadOnlyList<TdsColumn> columns,
        bool nullCompressed,
        TdsRowBuffer row)
    {
        // Rows that are already received in the current packet decode synchronously; rows that
        // cross a buffer or packet boundary take the incremental path.
        if (TdsRowCodec.TryReadRow(
              _reader.GetBufferedPayload(),
              AsSpan(columns),
              nullCompressed,
              row,
              out var consumed))
        {
            _reader.AdvancePayload(consumed);
            return ValueTask.CompletedTask;
        }

        return ReadRowSlowAsync(columns, nullCompressed, row);
    }

    /// <summary>
    /// Decodes the next token when it is a ROW or NBCROW that is already fully received, so a
    /// caller can drain buffered rows without an <see langword="await"/> per row. Returns
    /// <see langword="false"/> for any other token or a row that is not yet complete (or is
    /// invalid). It then consumes nothing from the packet reader, but <paramref name="row"/> may
    /// be partly overwritten.
    /// </summary>
    internal bool TryReadBufferedRow(ReadOnlySpan<TdsColumn> columns, TdsRowBuffer row)
    {
        var buffered = _reader.GetBufferedPayload();
        if (buffered.IsEmpty)
        {
            return false;
        }

        var token = buffered[0];
        if (token is not (TdsTokenType.Row or TdsTokenType.NbcRow) ||
            !TdsRowCodec.TryReadRow(
              buffered[1..],
              columns,
              nullCompressed: token == TdsTokenType.NbcRow,
              row,
              out var consumed))
        {
            return false;
        }

        _reader.AdvancePayload(1 + consumed);
        return true;
    }

    private static ReadOnlySpan<TdsColumn> AsSpan(IReadOnlyList<TdsColumn> columns) =>
      columns as TdsColumn[] ?? [.. columns];

    private async ValueTask ReadRowSlowAsync(
        IReadOnlyList<TdsColumn> columns,
        bool nullCompressed,
        TdsRowBuffer row)
    {
        row.Clear();
        row.WriteUInt16LittleEndian(checked((ushort)columns.Count));
        if (nullCompressed)
        {
            var bitmapLength = (columns.Count + 7) / 8;
            if (_nullBitmap.Length < bitmapLength)
            {
                _nullBitmap = new byte[bitmapLength];
            }

            await ReadExactlyAsync(_nullBitmap.AsMemory(0, bitmapLength))
              .ConfigureAwait(false);
        }

        for (var i = 0; i < columns.Count; i++)
        {
            var isNull =
              nullCompressed &&
              (_nullBitmap[i >> 3] & (1 << (i & 7))) != 0;
            if (isNull)
            {
                row.WriteNull();
                continue;
            }

            await WriteFieldAsync(row, columns[i].Layout).ConfigureAwait(false);
        }
    }

    internal ValueTask<TdsDoneToken> ReadDoneAsync()
    {
        if (_reader.TryReadPayload(DoneTokenLength, out var done))
        {
            return new ValueTask<TdsDoneToken>(new TdsDoneToken(
              (TdsDoneStatus)BinaryPrimitives.ReadUInt16LittleEndian(done),
              BinaryPrimitives.ReadUInt16LittleEndian(done[2..]),
              BinaryPrimitives.ReadInt64LittleEndian(done[4..])));
        }

        return ReadDoneSlowAsync();
    }

    private async ValueTask<TdsDoneToken> ReadDoneSlowAsync()
    {
        TdsDoneStatus status =
          (TdsDoneStatus)await ReadUInt16LittleEndianAsync().ConfigureAwait(false);
        var currentCommand = await ReadUInt16LittleEndianAsync().ConfigureAwait(false);
        var count = await ReadInt64LittleEndianAsync().ConfigureAwait(false);
        return new TdsDoneToken(status, currentCommand, count);
    }

    internal async ValueTask<MsSqlInfo> ReadMessageAsync()
    {
        int length = await ReadUInt16LittleEndianAsync().ConfigureAwait(false);
        if (_reader.TryReadPayload(length, out var buffered))
        {
            return TdsTokenReader.ParseMessage(buffered);
        }

        return TdsTokenReader.ParseMessage(await ReadBytesAsync(length).ConfigureAwait(false));
    }

    internal async ValueTask<TdsEnvironmentChangeInfo> ReadEnvironmentChangeAsync()
    {
        int length = await ReadUInt16LittleEndianAsync().ConfigureAwait(false);
        if (_reader.TryReadPayload(length, out var buffered))
        {
            return TdsTokenReader.ParseEnvironmentChange(buffered);
        }

        return TdsTokenReader.ParseEnvironmentChange(
          await ReadBytesAsync(length).ConfigureAwait(false));
    }

    internal async ValueTask SkipUShortLengthTokenAsync()
    {
        int length = await ReadUInt16LittleEndianAsync().ConfigureAwait(false);
        await SkipAsync(length).ConfigureAwait(false);
    }

    internal async ValueTask SkipUIntLengthTokenAsync()
    {
        var length = await ReadUInt32LittleEndianAsync().ConfigureAwait(false);
        if (length > int.MaxValue)
        {
            throw new InvalidDataException("TDS token length exceeds the supported limit.");
        }

        await SkipAsync((int)length).ConfigureAwait(false);
    }

    internal ValueTask SkipReturnStatusAsync() => SkipAsync(sizeof(int));

    /// <summary>
    /// Skips a token whose contents the driver does not use. Returns <see langword="false"/>,
    /// reading nothing, for any other token. Both the streaming pump and the buffered parser use
    /// this, so they accept the same responses.
    /// </summary>
    internal async ValueTask<bool> TrySkipTokenAsync(byte token)
    {
        switch (token)
        {
            case TdsTokenType.ReturnStatus:
                await SkipReturnStatusAsync().ConfigureAwait(false);
                return true;
            case TdsTokenType.Order:
            case TdsTokenType.TableName:
            case TdsTokenType.ColumnInfo:
            case TdsTokenType.Sspi:
            case TdsTokenType.LoginAck:
                await SkipUShortLengthTokenAsync().ConfigureAwait(false);
                return true;
            case TdsTokenType.SessionState:
            case TdsTokenType.FedAuthInfo:
                await SkipUIntLengthTokenAsync().ConfigureAwait(false);
                return true;
            case TdsTokenType.FeatureExtAck:
                await SkipFeatureExtAckAsync().ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    internal async ValueTask SkipFeatureExtAckAsync()
    {
        while (true)
        {
            var feature = await ReadByteAsync().ConfigureAwait(false);
            if (feature == byte.MaxValue)
            {
                return;
            }

            var length = await ReadUInt32LittleEndianAsync().ConfigureAwait(false);
            if (length > int.MaxValue)
            {
                throw new InvalidDataException(
                  $"SQL Server feature 0x{feature:X2} acknowledgement is too large.");
            }

            await SkipAsync((int)length).ConfigureAwait(false);
        }
    }

    internal async ValueTask<TdsReturnValue> ReadReturnValueAsync()
    {
        var header = TryReadReturnValueHeader(out var parsed)
          ? parsed
          : ParseReturnValueHeader(
              (await CollectAsync(s_measureReturnValueHeader, cache: null).ConfigureAwait(false)).Span);
        var value = _discarded ??= new TdsRowBuffer(32);
        value.Clear();
        await WriteFieldAsync(value, TdsFieldLayout.For(header.TypeInfo)).ConfigureAwait(false);
        return TdsReturnValue.Create(
          header.Ordinal,
          header.Name,
          header.Status,
          header.UserType,
          header.Flags,
          header.TypeInfo,
          value);
    }

    private bool TryReadReturnValueHeader(out ReturnValueHeader header)
    {
        var buffered = _reader.GetBufferedPayload();
        var length = TdsTypeCodec.MeasureReturnValueHeader(buffered);
        if (length < 0)
        {
            header = default;
            return false;
        }

        header = ParseReturnValueHeader(buffered[..length]);
        _reader.AdvancePayload(length);
        return true;
    }

    private static ReturnValueHeader ParseReturnValueHeader(ReadOnlySpan<byte> source)
    {
        TdsPayloadReader reader = new(source);
        return new ReturnValueHeader(
          reader.ReadUInt16LittleEndian(),
          reader.ReadBVarChar(),
          reader.ReadByte(),
          reader.ReadUInt32LittleEndian(),
          reader.ReadUInt16LittleEndian(),
          TdsTypeCodec.ReadTypeInfo(ref reader));
    }

    internal async ValueTask SkipReturnValueAsync() =>
      _ = await ReadReturnValueAsync().ConfigureAwait(false);

    /// <summary>
    /// Collects a token body that continues past the received bytes, one receive at a time, into
    /// a scratch buffer. <paramref name="measure"/> returns the length of a complete body at the
    /// start of its argument, or -1 when it is incomplete. The result is valid until the scratch
    /// buffer is used again.
    /// </summary>
    private async ValueTask<ReadOnlyMemory<byte>> CollectAsync(
        TdsTokenMeasure measure,
        TdsResultShapeCache? cache)
    {
        byte[] scratch = [];
        var written = 0;
        while (true)
        {
            if (TryCollectBuffered(measure, cache, ref scratch, ref written, out var length))
            {
                return scratch.AsMemory(0, length);
            }

            // Nothing more is received yet. The body is incomplete, so its next byte is part of
            // it; reading that one byte waits for the next receive.
            scratch = GetScratch(cache, scratch, written + 1, written);
            await ReadExactlyAsync(scratch.AsMemory(written, 1)).ConfigureAwait(false);
            written++;
            length = measure(scratch.AsSpan(0, written));
            if (length >= 0)
            {
                return scratch.AsMemory(0, length);
            }
        }
    }

    private bool TryCollectBuffered(
        TdsTokenMeasure measure,
        TdsResultShapeCache? cache,
        ref byte[] scratch,
        ref int written,
        out int length)
    {
        length = -1;
        while (true)
        {
            var buffered = _reader.GetBufferedPayload();
            if (buffered.IsEmpty)
            {
                return false;
            }

            scratch = GetScratch(cache, scratch, written + buffered.Length, written);
            buffered.CopyTo(scratch.AsSpan(written));
            length = measure(scratch.AsSpan(0, written + buffered.Length));
            // Consume only this token's bytes: the rest of the packet belongs to later tokens.
            var consumed = length >= 0 ? length - written : buffered.Length;
            _reader.AdvancePayload(consumed);
            written += consumed;
            if (length >= 0)
            {
                return true;
            }
        }
    }

    private static byte[] GetScratch(
        TdsResultShapeCache? cache,
        byte[] scratch,
        int length,
        int preserve)
    {
        if (cache is not null)
        {
            return cache.GetScratch(length, preserve);
        }

        if (scratch.Length >= length)
        {
            return scratch;
        }

        var grown = new byte[Math.Max(length, Math.Max(256, scratch.Length * 2))];
        scratch.AsSpan(0, preserve).CopyTo(grown);
        return grown;
    }

    /// <summary>
    /// Appends one field, reading across packets. This is the incremental counterpart of
    /// <see cref="TdsRowCodec.TryReadRow"/> and must accept the same layouts.
    /// </summary>
    private async ValueTask WriteFieldAsync(TdsRowBuffer row, TdsFieldLayout layout)
    {
        int length;
        switch (layout.Kind)
        {
            case TdsFieldKind.Fixed:
                length = layout.FixedLength;
                break;
            case TdsFieldKind.ByteLength:
                length = await ReadByteAsync().ConfigureAwait(false);
                if (length == 0)
                {
                    row.WriteNull();
                    return;
                }

                break;
            case TdsFieldKind.UShortLength:
                length = await ReadUInt16LittleEndianAsync().ConfigureAwait(false);
                if (length == ushort.MaxValue)
                {
                    row.WriteNull();
                    return;
                }

                break;
            case TdsFieldKind.Plp:
                await WritePlpAsync(row).ConfigureAwait(false);
                return;
            case TdsFieldKind.TextPointer:
                int pointerLength = await ReadByteAsync().ConfigureAwait(false);
                if (pointerLength == 0)
                {
                    row.WriteNull();
                    return;
                }

                await SkipAsync(pointerLength + 8).ConfigureAwait(false);
                length = await ReadInt32LittleEndianAsync().ConfigureAwait(false);
                if (length < 0)
                {
                    throw TdsRowCodec.NegativeLobLength();
                }

                break;
            case TdsFieldKind.Null:
                row.WriteNull();
                return;
            default:
                throw layout.Unsupported();
        }

        await WriteRawValueAsync(row, length).ConfigureAwait(false);
    }

    private async ValueTask WriteRawValueAsync(TdsRowBuffer row, int length)
    {
        row.WriteInt32LittleEndian(length);
        var destination = row.GetMemory(length)[..length];
        await ReadExactlyAsync(destination).ConfigureAwait(false);
        row.Advance(length);
    }

    private async ValueTask WritePlpAsync(TdsRowBuffer row)
    {
        var totalLength = await ReadUInt64LittleEndianAsync().ConfigureAwait(false);
        if (totalLength == ulong.MaxValue)
        {
            row.WriteNull();
            return;
        }

        var lengthOffset = row.WrittenCount;
        row.WriteInt32LittleEndian(0);
        var valueOffset = row.WrittenCount;
        while (true)
        {
            var chunkLength = await ReadUInt32LittleEndianAsync().ConfigureAwait(false);
            if (chunkLength == 0)
            {
                break;
            }

            if (chunkLength > int.MaxValue)
            {
                throw new InvalidDataException("A SQL Server PLP chunk is too large.");
            }

            var length = checked((int)chunkLength);
            var destination = row.GetMemory(length)[..length];
            await ReadExactlyAsync(destination).ConfigureAwait(false);
            row.Advance(length);
        }

        TdsRowCodec.CompletePlp(row, lengthOffset, valueOffset, totalLength);
    }

    private ValueTask<byte> ReadByteAsync() =>
      _reader.TryReadPayload(1, out var value)
        ? new ValueTask<byte>(value[0])
        : ReadByteSlowAsync();

    private async ValueTask<byte> ReadByteSlowAsync()
    {
        await ReadExactlyAsync(Primitive.AsMemory(0, 1)).ConfigureAwait(false);
        return Primitive[0];
    }

    private ValueTask<ushort> ReadUInt16LittleEndianAsync() =>
      _reader.TryReadPayload(2, out var value)
        ? new ValueTask<ushort>(BinaryPrimitives.ReadUInt16LittleEndian(value))
        : ReadUInt16LittleEndianSlowAsync();

    private async ValueTask<ushort> ReadUInt16LittleEndianSlowAsync()
    {
        await ReadExactlyAsync(Primitive.AsMemory(0, 2)).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16LittleEndian(Primitive);
    }

    private ValueTask<int> ReadInt32LittleEndianAsync() =>
      _reader.TryReadPayload(4, out var value)
        ? new ValueTask<int>(BinaryPrimitives.ReadInt32LittleEndian(value))
        : ReadInt32LittleEndianSlowAsync();

    private async ValueTask<int> ReadInt32LittleEndianSlowAsync()
    {
        await ReadExactlyAsync(Primitive.AsMemory(0, 4)).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(Primitive);
    }

    private ValueTask<uint> ReadUInt32LittleEndianAsync() =>
      _reader.TryReadPayload(4, out var value)
        ? new ValueTask<uint>(BinaryPrimitives.ReadUInt32LittleEndian(value))
        : ReadUInt32LittleEndianSlowAsync();

    private async ValueTask<uint> ReadUInt32LittleEndianSlowAsync()
    {
        await ReadExactlyAsync(Primitive.AsMemory(0, 4)).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(Primitive);
    }

    private ValueTask<long> ReadInt64LittleEndianAsync() =>
      _reader.TryReadPayload(8, out var value)
        ? new ValueTask<long>(BinaryPrimitives.ReadInt64LittleEndian(value))
        : ReadInt64LittleEndianSlowAsync();

    private async ValueTask<long> ReadInt64LittleEndianSlowAsync()
    {
        await ReadExactlyAsync(Primitive.AsMemory(0, 8)).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt64LittleEndian(Primitive);
    }

    private ValueTask<ulong> ReadUInt64LittleEndianAsync() =>
      _reader.TryReadPayload(8, out var value)
        ? new ValueTask<ulong>(BinaryPrimitives.ReadUInt64LittleEndian(value))
        : ReadUInt64LittleEndianSlowAsync();

    private async ValueTask<ulong> ReadUInt64LittleEndianSlowAsync()
    {
        await ReadExactlyAsync(Primitive.AsMemory(0, 8)).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt64LittleEndian(Primitive);
    }

    private async ValueTask<byte[]> ReadBytesAsync(int length)
    {
        if (length < 0)
        {
            throw new InvalidDataException("TDS token contains a negative length.");
        }

        var value = new byte[length];
        await ReadExactlyAsync(value).ConfigureAwait(false);
        return value;
    }

    private ValueTask ReadExactlyAsync(Memory<byte> destination) =>
      _reader.ReadPayloadExactlyAsync(destination, _cancellationToken);

    private ValueTask SkipAsync(int length) =>
      _reader.SkipPayloadAsync(length, _cancellationToken);

    private readonly record struct ReturnValueHeader(
        ushort Ordinal,
        string Name,
        byte Status,
        uint UserType,
        ushort Flags,
        TdsTypeInfo TypeInfo);
}

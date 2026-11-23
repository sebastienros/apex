using System.Buffers.Binary;

namespace Apex.MsSqlClient.Internal;

internal sealed class TdsStreamingTokenReader
{
    private const int DoneTokenLength = 12;
    private readonly TdsPacketReader _reader;
    private readonly CancellationToken _cancellationToken;
    private readonly byte[] _primitive = new byte[8];
    private readonly TdsRowBuffer _discarded = new(32);
    private byte[] _nullBitmap = [];

    internal TdsStreamingTokenReader(
        TdsPacketReader reader,
        CancellationToken cancellationToken)
    {
        _reader = reader;
        _cancellationToken = cancellationToken;
    }

    internal bool HasRemaining => !_reader.EndOfMessage;

    internal ValueTask<byte> ReadTokenTypeAsync() => ReadByteAsync();

    internal async ValueTask<IReadOnlyList<TdsColumn>> ReadColumnsAsync()
    {
        int count = await ReadUInt16LittleEndianAsync().ConfigureAwait(false);
        if (count == ushort.MaxValue)
        {
            return Array.Empty<TdsColumn>();
        }

        TdsColumn[] columns = new TdsColumn[count];
        for (var i = 0; i < columns.Length; i++)
        {
            _ = await ReadUInt32LittleEndianAsync().ConfigureAwait(false);
            _ = await ReadUInt16LittleEndianAsync().ConfigureAwait(false);
            var typeInfo = await ReadTypeInfoAsync().ConfigureAwait(false);
            var name = await ReadBVarCharAsync().ConfigureAwait(false);
            columns[i] = new TdsColumn(typeInfo.ToColumn(name), typeInfo);
        }

        return columns;
    }

    internal ValueTask ReadRowAsync(
        IReadOnlyList<TdsColumn> columns,
        bool nullCompressed,
        TdsRowBuffer row)
    {
        // Rows that are already received in the current packet decode synchronously; rows that
        // cross a buffer or packet boundary take the incremental path.
        if (TdsRowCodec.TryReadRow(
              _reader.GetBufferedPayload(),
              columns,
              nullCompressed,
              row,
              out var consumed))
        {
            _reader.AdvancePayload(consumed);
            return ValueTask.CompletedTask;
        }

        return ReadRowSlowAsync(columns, nullCompressed, row);
    }

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
            await WriteFieldAsync(row, columns[i].TypeInfo, isNull).ConfigureAwait(false);
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
        var ordinal = await ReadUInt16LittleEndianAsync().ConfigureAwait(false);
        var name = await ReadBVarCharAsync().ConfigureAwait(false);
        var status = await ReadByteAsync().ConfigureAwait(false);
        var userType = await ReadUInt32LittleEndianAsync().ConfigureAwait(false);
        var flags = await ReadUInt16LittleEndianAsync().ConfigureAwait(false);
        var typeInfo = await ReadTypeInfoAsync().ConfigureAwait(false);
        _discarded.Clear();
        await WriteFieldAsync(_discarded, typeInfo, isNull: false).ConfigureAwait(false);
        return TdsReturnValue.Create(
          ordinal,
          name,
          status,
          userType,
          flags,
          typeInfo,
          _discarded);
    }

    internal async ValueTask SkipReturnValueAsync() =>
      _ = await ReadReturnValueAsync().ConfigureAwait(false);

    private async ValueTask<TdsTypeInfo> ReadTypeInfoAsync()
    {
        var type = await ReadByteAsync().ConfigureAwait(false);
        switch (type)
        {
            case TdsDataType.Null:
            case TdsDataType.Int1:
            case TdsDataType.Bit:
            case TdsDataType.Int2:
            case TdsDataType.Int4:
            case TdsDataType.DateTime4:
            case TdsDataType.Float4:
            case TdsDataType.Money:
            case TdsDataType.DateTime:
            case TdsDataType.Float8:
            case TdsDataType.Money4:
            case TdsDataType.Int8:
                return new TdsTypeInfo(type, TdsTypeCodec.FixedLength(type));

            case TdsDataType.Guid:
            case TdsDataType.IntN:
            case TdsDataType.BitN:
            case TdsDataType.FloatN:
            case TdsDataType.MoneyN:
            case TdsDataType.DateTimeN:
                return new TdsTypeInfo(
                  type,
                  await ReadByteAsync().ConfigureAwait(false));

            case TdsDataType.Decimal:
            case TdsDataType.Numeric:
            case TdsDataType.DecimalN:
            case TdsDataType.NumericN:
                return new TdsTypeInfo(
                  type,
                  await ReadByteAsync().ConfigureAwait(false),
                  await ReadByteAsync().ConfigureAwait(false),
                  await ReadByteAsync().ConfigureAwait(false));

            case TdsDataType.Date:
                return new TdsTypeInfo(type, 3);

            case TdsDataType.Time:
                return await ReadScaledAsync(type, 0, 0).ConfigureAwait(false);
            case TdsDataType.DateTime2:
                return await ReadScaledAsync(type, 3, 0).ConfigureAwait(false);
            case TdsDataType.DateTimeOffset:
                return await ReadScaledAsync(type, 3, 2).ConfigureAwait(false);

            case TdsDataType.Binary:
            case TdsDataType.VarBinary:
            case TdsDataType.BigBinary:
            case TdsDataType.BigVarBinary:
                return new TdsTypeInfo(
                  type,
                  await ReadUInt16LittleEndianAsync().ConfigureAwait(false));

            case TdsDataType.Char:
            case TdsDataType.VarChar:
            case TdsDataType.BigChar:
            case TdsDataType.BigVarChar:
                return await ReadCharacterTypeAsync(type, unicode: false).ConfigureAwait(false);

            case TdsDataType.NChar:
            case TdsDataType.NVarChar:
                return await ReadCharacterTypeAsync(type, unicode: true).ConfigureAwait(false);

            case TdsDataType.Text:
            case TdsDataType.NText:
                return await ReadLegacyLobAsync(type, hasCollation: true).ConfigureAwait(false);
            case TdsDataType.Image:
                return await ReadLegacyLobAsync(type, hasCollation: false).ConfigureAwait(false);

            case TdsDataType.Xml:
                if (await ReadByteAsync().ConfigureAwait(false) != 0)
                {
                    _ = await ReadBVarCharAsync().ConfigureAwait(false);
                    _ = await ReadBVarCharAsync().ConfigureAwait(false);
                    _ = await ReadUsVarCharAsync().ConfigureAwait(false);
                }

                return new TdsTypeInfo(type, ushort.MaxValue);

            case TdsDataType.Json:
                return new TdsTypeInfo(type, ushort.MaxValue);

            case TdsDataType.Udt:
                int maximumLength = await ReadUInt16LittleEndianAsync().ConfigureAwait(false);
                _ = await ReadBVarCharAsync().ConfigureAwait(false);
                _ = await ReadBVarCharAsync().ConfigureAwait(false);
                _ = await ReadBVarCharAsync().ConfigureAwait(false);
                _ = await ReadUsVarCharAsync().ConfigureAwait(false);
                return new TdsTypeInfo(type, maximumLength);

            default:
                throw new NotSupportedException(
                  $"SQL Server TDS data type 0x{type:X2} is not supported.");
        }
    }

    private async ValueTask<TdsTypeInfo> ReadScaledAsync(
        byte type,
        int dateBytes,
        int offsetBytes)
    {
        var scale = await ReadByteAsync().ConfigureAwait(false);
        if (scale > 7)
        {
            throw new InvalidDataException($"Invalid SQL Server temporal scale {scale}.");
        }

        var timeBytes = scale <= 2 ? 3 : scale <= 4 ? 4 : 5;
        return new TdsTypeInfo(
          type,
          timeBytes + dateBytes + offsetBytes,
          Scale: scale);
    }

    private async ValueTask<TdsTypeInfo> ReadCharacterTypeAsync(
        byte type,
        bool unicode)
    {
        int maximumLength = await ReadUInt16LittleEndianAsync().ConfigureAwait(false);
        var info = await ReadUInt32LittleEndianAsync().ConfigureAwait(false);
        var sortId = await ReadByteAsync().ConfigureAwait(false);
        TdsCollation collation = new(
          info,
          sortId,
          unicode ? 1200 : TdsCollationCodec.ResolveCodePage(info, sortId));
        return new TdsTypeInfo(type, maximumLength, Collation: collation);
    }

    private async ValueTask<TdsTypeInfo> ReadLegacyLobAsync(
        byte type,
        bool hasCollation)
    {
        var maximumLength = await ReadInt32LittleEndianAsync().ConfigureAwait(false);
        TdsCollation? collation = null;
        if (hasCollation)
        {
            var info = await ReadUInt32LittleEndianAsync().ConfigureAwait(false);
            var sortId = await ReadByteAsync().ConfigureAwait(false);
            var unicode = type == TdsDataType.NText;
            collation = new TdsCollation(
              info,
              sortId,
              unicode ? 1200 : TdsCollationCodec.ResolveCodePage(info, sortId));
        }

        int parts = await ReadByteAsync().ConfigureAwait(false);
        for (var i = 0; i < parts; i++)
        {
            _ = await ReadUsVarCharAsync().ConfigureAwait(false);
        }

        return new TdsTypeInfo(type, maximumLength, Collation: collation);
    }

    private async ValueTask WriteFieldAsync(
        TdsRowBuffer row,
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
            await WriteRawValueAsync(row, fixedLength).ConfigureAwait(false);
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
                int byteLength = await ReadByteAsync().ConfigureAwait(false);
                if (byteLength == 0)
                {
                    row.WriteInt32LittleEndian(-1);
                }
                else
                {
                    await WriteRawValueAsync(row, byteLength).ConfigureAwait(false);
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
                    await WritePlpAsync(row).ConfigureAwait(false);
                    return;
                }

                int length = await ReadUInt16LittleEndianAsync().ConfigureAwait(false);
                if (length == ushort.MaxValue)
                {
                    row.WriteInt32LittleEndian(-1);
                }
                else
                {
                    await WriteRawValueAsync(row, length).ConfigureAwait(false);
                }

                return;

            case TdsDataType.Text:
            case TdsDataType.NText:
            case TdsDataType.Image:
                int pointerLength = await ReadByteAsync().ConfigureAwait(false);
                if (pointerLength == 0)
                {
                    row.WriteInt32LittleEndian(-1);
                    return;
                }

                await SkipAsync(pointerLength + 8).ConfigureAwait(false);
                var dataLength = await ReadInt32LittleEndianAsync().ConfigureAwait(false);
                if (dataLength < 0)
                {
                    throw new InvalidDataException("SQL Server LOB value has a negative length.");
                }

                await WriteRawValueAsync(row, dataLength).ConfigureAwait(false);
                return;

            case TdsDataType.Xml:
            case TdsDataType.Json:
            case TdsDataType.Udt:
                await WritePlpAsync(row).ConfigureAwait(false);
                return;

            default:
                throw new NotSupportedException(
                  $"Cannot decode SQL Server row type 0x{typeInfo.Type:X2}.");
        }
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
            row.WriteInt32LittleEndian(-1);
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

        var valueLength = checked(row.WrittenCount - valueOffset);
        if (totalLength != 0xFFFF_FFFF_FFFF_FFFE && totalLength != (ulong)valueLength)
        {
            throw new InvalidDataException(
              $"SQL Server PLP value declared {totalLength} bytes but contained {valueLength}.");
        }

        row.PatchInt32LittleEndian(lengthOffset, valueLength);
    }

    private ValueTask<byte> ReadByteAsync() =>
      _reader.TryReadPayload(1, out var value)
        ? new ValueTask<byte>(value[0])
        : ReadByteSlowAsync();

    private async ValueTask<byte> ReadByteSlowAsync()
    {
        await ReadExactlyAsync(_primitive.AsMemory(0, 1)).ConfigureAwait(false);
        return _primitive[0];
    }

    private ValueTask<ushort> ReadUInt16LittleEndianAsync() =>
      _reader.TryReadPayload(2, out var value)
        ? new ValueTask<ushort>(BinaryPrimitives.ReadUInt16LittleEndian(value))
        : ReadUInt16LittleEndianSlowAsync();

    private async ValueTask<ushort> ReadUInt16LittleEndianSlowAsync()
    {
        await ReadExactlyAsync(_primitive.AsMemory(0, 2)).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16LittleEndian(_primitive);
    }

    private ValueTask<int> ReadInt32LittleEndianAsync() =>
      _reader.TryReadPayload(4, out var value)
        ? new ValueTask<int>(BinaryPrimitives.ReadInt32LittleEndian(value))
        : ReadInt32LittleEndianSlowAsync();

    private async ValueTask<int> ReadInt32LittleEndianSlowAsync()
    {
        await ReadExactlyAsync(_primitive.AsMemory(0, 4)).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(_primitive);
    }

    private ValueTask<uint> ReadUInt32LittleEndianAsync() =>
      _reader.TryReadPayload(4, out var value)
        ? new ValueTask<uint>(BinaryPrimitives.ReadUInt32LittleEndian(value))
        : ReadUInt32LittleEndianSlowAsync();

    private async ValueTask<uint> ReadUInt32LittleEndianSlowAsync()
    {
        await ReadExactlyAsync(_primitive.AsMemory(0, 4)).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(_primitive);
    }

    private ValueTask<long> ReadInt64LittleEndianAsync() =>
      _reader.TryReadPayload(8, out var value)
        ? new ValueTask<long>(BinaryPrimitives.ReadInt64LittleEndian(value))
        : ReadInt64LittleEndianSlowAsync();

    private async ValueTask<long> ReadInt64LittleEndianSlowAsync()
    {
        await ReadExactlyAsync(_primitive.AsMemory(0, 8)).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt64LittleEndian(_primitive);
    }

    private ValueTask<ulong> ReadUInt64LittleEndianAsync() =>
      _reader.TryReadPayload(8, out var value)
        ? new ValueTask<ulong>(BinaryPrimitives.ReadUInt64LittleEndian(value))
        : ReadUInt64LittleEndianSlowAsync();

    private async ValueTask<ulong> ReadUInt64LittleEndianSlowAsync()
    {
        await ReadExactlyAsync(_primitive.AsMemory(0, 8)).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt64LittleEndian(_primitive);
    }

    private async ValueTask<string> ReadBVarCharAsync()
    {
        int characterCount = await ReadByteAsync().ConfigureAwait(false);
        return await ReadUtf16Async(characterCount).ConfigureAwait(false);
    }

    private async ValueTask<string> ReadUsVarCharAsync()
    {
        int characterCount = await ReadUInt16LittleEndianAsync().ConfigureAwait(false);
        return await ReadUtf16Async(characterCount).ConfigureAwait(false);
    }

    private async ValueTask<string> ReadUtf16Async(int characterCount)
    {
        if (_reader.TryReadPayload(checked(characterCount * sizeof(char)), out var buffered))
        {
            return TdsCollationCodec.Decode(buffered, 1200);
        }

        var value = await ReadBytesAsync(
          checked(characterCount * sizeof(char))).ConfigureAwait(false);
        return TdsCollationCodec.Decode(value, 1200);
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
}

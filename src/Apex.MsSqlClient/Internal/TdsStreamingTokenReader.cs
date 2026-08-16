using System.Buffers.Binary;

namespace Apex.MsSqlClient.Internal;

internal sealed class TdsStreamingTokenReader
{
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

    internal async ValueTask ReadRowAsync(
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

    internal async ValueTask<TdsDoneToken> ReadDoneAsync()
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
        var body = await ReadBytesAsync(length).ConfigureAwait(false);
        TdsPayloadReader reader = new(body);
        var number = reader.ReadInt32LittleEndian();
        var state = reader.ReadByte();
        var severity = reader.ReadByte();
        var message = reader.ReadUsVarChar();
        var serverName = reader.ReadBVarChar();
        var procedureName = reader.ReadBVarChar();
        var lineNumber = reader.Remaining switch
        {
            >= 4 => reader.ReadInt32LittleEndian(),
            2 => reader.ReadUInt16LittleEndian(),
            _ => throw new InvalidDataException(
              "SQL Server INFO/ERROR token omitted its line number."),
        };
        return new MsSqlInfo(
          number,
          state,
          severity,
          message,
          serverName,
          procedureName,
          lineNumber);
    }

    internal async ValueTask<TdsEnvironmentChangeInfo> ReadEnvironmentChangeAsync()
    {
        int length = await ReadUInt16LittleEndianAsync().ConfigureAwait(false);
        var body = await ReadBytesAsync(length).ConfigureAwait(false);
        TdsPayloadReader reader = new(body);
        var type = reader.ReadByte();
        return type switch
        {
            TdsEnvironmentChange.Database =>
              new TdsEnvironmentChangeInfo(Database: reader.ReadBVarChar()),
            TdsEnvironmentChange.PacketSize =>
              new TdsEnvironmentChangeInfo(
                PacketSize: ParsePacketSize(reader.ReadBVarChar())),
            TdsEnvironmentChange.BeginTransaction or TdsEnvironmentChange.EnlistDtc =>
              new TdsEnvironmentChangeInfo(
                TransactionDescriptor: ReadTransactionDescriptor(ref reader)),
            TdsEnvironmentChange.CommitTransaction or
            TdsEnvironmentChange.RollbackTransaction or
            TdsEnvironmentChange.DefectDtc =>
              new TdsEnvironmentChangeInfo(TransactionDescriptor: 0),
            TdsEnvironmentChange.Routing =>
              new TdsEnvironmentChangeInfo(Routing: ReadRouting(ref reader)),
            _ => default,
        };
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

    private async ValueTask<byte> ReadByteAsync()
    {
        await ReadExactlyAsync(_primitive.AsMemory(0, 1)).ConfigureAwait(false);
        return _primitive[0];
    }

    private async ValueTask<ushort> ReadUInt16LittleEndianAsync()
    {
        await ReadExactlyAsync(_primitive.AsMemory(0, 2)).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt16LittleEndian(_primitive);
    }

    private async ValueTask<int> ReadInt32LittleEndianAsync()
    {
        await ReadExactlyAsync(_primitive.AsMemory(0, 4)).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(_primitive);
    }

    private async ValueTask<uint> ReadUInt32LittleEndianAsync()
    {
        await ReadExactlyAsync(_primitive.AsMemory(0, 4)).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(_primitive);
    }

    private async ValueTask<long> ReadInt64LittleEndianAsync()
    {
        await ReadExactlyAsync(_primitive.AsMemory(0, 8)).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt64LittleEndian(_primitive);
    }

    private async ValueTask<ulong> ReadUInt64LittleEndianAsync()
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
        var value = await ReadBytesAsync(
          checked(characterCount * sizeof(char))).ConfigureAwait(false);
        return TdsCollationCodec.GetEncoding(1200).GetString(value);
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

    private static int ParsePacketSize(string value) =>
      int.TryParse(
        value,
        System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture,
        out var packetSize) &&
      packetSize is >= 512 and <= 32767
        ? packetSize
        : throw new InvalidDataException(
          $"SQL Server sent invalid packet size '{value}'.");

    private static long ReadTransactionDescriptor(ref TdsPayloadReader reader)
    {
        int length = reader.ReadByte();
        if (length != sizeof(long))
        {
            throw new InvalidDataException(
              $"SQL Server transaction descriptor has length {length}; expected 8.");
        }

        return reader.ReadInt64LittleEndian();
    }

    private static MsSqlRoutingInfo ReadRouting(ref TdsPayloadReader reader)
    {
        int length = reader.ReadUInt16LittleEndian();
        var start = reader.Position;
        if (length < 5)
        {
            throw new InvalidDataException("SQL Server routing data is too short.");
        }

        var protocol = reader.ReadByte();
        if (protocol != 0)
        {
            throw new NotSupportedException(
              $"SQL Server routing protocol {protocol} is not TCP/IP.");
        }

        int port = reader.ReadUInt16LittleEndian();
        if (port == 0)
        {
            throw new InvalidDataException("SQL Server routing port cannot be zero.");
        }

        var host = reader.ReadUsVarChar();
        reader.Position = checked(start + length);
        return new MsSqlRoutingInfo(host, port);
    }
}

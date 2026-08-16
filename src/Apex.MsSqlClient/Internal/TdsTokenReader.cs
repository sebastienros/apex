using System.Buffers.Binary;

namespace Apex.MsSqlClient.Internal;

internal readonly record struct TdsDoneToken(
    TdsDoneStatus Status,
    ushort CurrentCommand,
    long RowCount);

internal readonly record struct TdsLoginAckInfo(
    uint TdsVersion,
    string ProductName,
    Version ProductVersion);

internal readonly record struct MsSqlRoutingInfo(string Host, int Port);

internal readonly record struct TdsFeatureExtAck(
    bool FedAuthAcknowledged,
    int FedAuthDataLength);

internal readonly record struct TdsEnvironmentChangeInfo(
    string? Database = null,
    int? PacketSize = null,
    long? TransactionDescriptor = null,
    MsSqlRoutingInfo? Routing = null);

internal readonly record struct TdsReturnValue(
    ushort Ordinal,
    string Name,
    byte Status,
    uint UserType,
    ushort Flags,
    TdsTypeInfo TypeInfo,
    ReadOnlyMemory<byte>? Value)
{
    internal bool IsOutput => (Status & 0x01) != 0;

    internal int GetPreparedHandle()
    {
        if (TypeInfo.Type != TdsDataType.IntN ||
            TypeInfo.MaximumLength != sizeof(int))
        {
            throw new InvalidDataException(
              "SQL Server returned a prepared handle with non-INTN(4) type information.");
        }

        if (Value is not { } value)
        {
            throw new InvalidDataException("SQL Server returned a null prepared handle.");
        }

        if (value.Length != sizeof(int))
        {
            throw new InvalidDataException(
              $"SQL Server returned a prepared handle with length {value.Length}; expected 4.");
        }

        var handle = BinaryPrimitives.ReadInt32LittleEndian(value.Span);
        if (handle <= 0)
        {
            throw new InvalidDataException(
              $"SQL Server returned invalid prepared handle {handle}.");
        }

        return handle;
    }

    internal static TdsReturnValue Create(
        ushort ordinal,
        string name,
        byte status,
        uint userType,
        ushort flags,
        TdsTypeInfo typeInfo,
        TdsRowBuffer encodedValue)
    {
        var encoded = encodedValue.WrittenSpan;
        if (encoded.Length < sizeof(int))
        {
            throw new InvalidDataException("SQL Server RETURNVALUE data is truncated.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(encoded);
        ReadOnlyMemory<byte>? value;
        if (length == -1)
        {
            value = null;
        }
        else
        {
            if (length < 0 || encoded.Length != sizeof(int) + length)
            {
                throw new InvalidDataException(
                  "SQL Server RETURNVALUE data has an invalid encoded length.");
            }

            value = encoded.Slice(sizeof(int), length).ToArray();
        }

        return new TdsReturnValue(
          ordinal,
          name,
          status,
          userType,
          flags,
          typeInfo,
          value);
    }
}

internal sealed class TdsTokenReader
{
    private readonly ReadOnlyMemory<byte> _payload;
    private int _position;

    internal TdsTokenReader(ReadOnlyMemory<byte> payload)
    {
        _payload = payload;
    }

    internal bool HasRemaining => _position < _payload.Length;

    internal byte ReadTokenType()
    {
        Ensure(1);
        return _payload.Span[_position++];
    }

    internal IReadOnlyList<TdsColumn> ReadColumns()
    {
        var reader = CreatePayloadReader();
        var columns = TdsTypeCodec.ReadColumns(ref reader);
        Commit(reader);
        return columns;
    }

    internal void ReadRow(
        IReadOnlyList<TdsColumn> columns,
        bool nullCompressed,
        TdsRowBuffer row)
    {
        var reader = CreatePayloadReader();
        TdsRowCodec.ReadRow(ref reader, columns, nullCompressed, row);
        Commit(reader);
    }

    internal TdsDoneToken ReadDone()
    {
        var reader = CreatePayloadReader();
        TdsDoneStatus status = (TdsDoneStatus)reader.ReadUInt16LittleEndian();
        var currentCommand = reader.ReadUInt16LittleEndian();
        var count = reader.ReadInt64LittleEndian();
        Commit(reader);
        return new TdsDoneToken(status, currentCommand, count);
    }

    internal MsSqlInfo ReadMessage()
    {
        var outer = CreatePayloadReader();
        int length = outer.ReadUInt16LittleEndian();
        var body = outer.ReadSpan(length);
        Commit(outer);

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
            _ => throw new InvalidDataException("SQL Server INFO/ERROR token omitted its line number."),
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

    internal TdsLoginAckInfo ReadLoginAck()
    {
        var outer = CreatePayloadReader();
        int length = outer.ReadUInt16LittleEndian();
        var body = outer.ReadSpan(length);
        Commit(outer);

        TdsPayloadReader reader = new(body);
        _ = reader.ReadByte();
        var tdsVersionBytes = reader.ReadSpan(4);
        var tdsVersion = BinaryPrimitives.ReadUInt32BigEndian(tdsVersionBytes);
        var product = reader.ReadBVarChar();
        int major = reader.ReadByte();
        int minor = reader.ReadByte();
        int build = reader.ReadUInt16BigEndian();
        return new TdsLoginAckInfo(
          tdsVersion,
          product,
          new Version(major, minor, build));
    }

    internal TdsEnvironmentChangeInfo ReadEnvironmentChange()
    {
        var outer = CreatePayloadReader();
        int length = outer.ReadUInt16LittleEndian();
        var body = outer.ReadSpan(length);
        Commit(outer);

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

    internal void SkipUShortLengthToken()
    {
        var reader = CreatePayloadReader();
        reader.Skip(reader.ReadUInt16LittleEndian());
        Commit(reader);
    }

    internal void SkipUIntLengthToken()
    {
        var reader = CreatePayloadReader();
        var length = reader.ReadUInt32LittleEndian();
        if (length > int.MaxValue)
        {
            throw new InvalidDataException("TDS token length exceeds the supported limit.");
        }

        reader.Skip((int)length);
        Commit(reader);
    }

    internal void SkipReturnStatus()
    {
        Ensure(4);
        _position += 4;
    }

    internal void SkipFeatureExtAck() => _ = ReadFeatureExtAck();

    internal TdsFeatureExtAck ReadFeatureExtAck()
    {
        var reader = CreatePayloadReader();
        var fedAuthAcknowledged = false;
        var fedAuthDataLength = 0;
        while (true)
        {
            var feature = reader.ReadByte();
            if (feature == TdsFeatureId.Terminator)
            {
                break;
            }

            var length = reader.ReadUInt32LittleEndian();
            if (length > int.MaxValue)
            {
                throw new InvalidDataException(
                  $"SQL Server feature 0x{feature:X2} acknowledgement is too large.");
            }

            reader.Skip((int)length);
            if (feature == TdsFeatureId.FedAuth)
            {
                fedAuthAcknowledged = true;
                fedAuthDataLength = (int)length;
            }
        }

        Commit(reader);
        return new TdsFeatureExtAck(fedAuthAcknowledged, fedAuthDataLength);
    }

    internal TdsFedAuthInfo ReadFedAuthInfo()
    {
        var outer = CreatePayloadReader();
        var length = outer.ReadUInt32LittleEndian();
        if (length > int.MaxValue)
        {
            throw new InvalidDataException("SQL Server FEDAUTHINFO token is too large.");
        }

        var body = outer.ReadSpan((int)length);
        Commit(outer);
        return TdsFedAuth.ParseInfo(body);
    }

    internal TdsReturnValue ReadReturnValue()
    {
        var reader = CreatePayloadReader();
        var ordinal = reader.ReadUInt16LittleEndian();
        var name = reader.ReadBVarChar();
        var status = reader.ReadByte();
        var userType = reader.ReadUInt32LittleEndian();
        var flags = reader.ReadUInt16LittleEndian();
        var typeInfo = TdsTypeCodec.ReadTypeInfo(ref reader);
        TdsRowBuffer value = new(32);
        TdsRowCodec.ReadValue(ref reader, typeInfo, value);
        Commit(reader);
        return TdsReturnValue.Create(
          ordinal,
          name,
          status,
          userType,
          flags,
          typeInfo,
          value);
    }

    internal void SkipReturnValue() => _ = ReadReturnValue();

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

    private TdsPayloadReader CreatePayloadReader()
    {
        TdsPayloadReader reader = new(_payload.Span);
        reader.Position = _position;
        return reader;
    }

    private void Commit(TdsPayloadReader reader) => _position = reader.Position;

    private void Ensure(int length)
    {
        if (length < 0 || _position > _payload.Length - length)
        {
            throw new InvalidDataException("TDS token stream is truncated.");
        }
    }
}

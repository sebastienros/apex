using System.Buffers.Binary;

namespace Apex.MsSqlClient.Internal;

internal enum TdsEncryptionLevel : byte
{
    Off = 0,
    On = 1,
    NotSupported = 2,
    Required = 3,
}

internal readonly record struct TdsPreLoginResponse(
    Version? ServerVersion,
    TdsEncryptionLevel EncryptionLevel,
    bool MarsSupported,
    bool FederatedAuthenticationRequired = false,
    ReadOnlyMemory<byte> Nonce = default);

internal static class TdsPreLogin
{
    internal const int NonceLength = 32;
    private const byte VersionOption = 0x00;
    private const byte EncryptionOption = 0x01;
    private const byte MarsOption = 0x04;
    private const byte FedAuthRequiredOption = 0x06;
    private const byte NonceOption = 0x07;
    private const byte Terminator = 0xFF;

    internal static byte[] Encode(
        TdsEncryptionLevel encryptionLevel,
        bool requestFederatedAuthentication = false)
    {
        var optionCount = requestFederatedAuthentication ? 4 : 3;
        var tableLength = optionCount * 5 + 1;
        var payload = new byte[tableLength + (requestFederatedAuthentication ? 9 : 8)];
        var table = 0;
        var data = tableLength;
        WriteOption(payload, ref table, VersionOption, data, 6);
        payload[data] = 16;
        payload[data + 1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(data + 2), 1000);
        data += 6;

        WriteOption(payload, ref table, EncryptionOption, data, 1);
        payload[data++] = (byte)encryptionLevel;

        WriteOption(payload, ref table, MarsOption, data, 1);
        payload[data++] = 0;

        if (requestFederatedAuthentication)
        {
            WriteOption(payload, ref table, FedAuthRequiredOption, data, 1);
            payload[data] = 1;
        }

        payload[table] = Terminator;
        return payload;
    }

    internal static TdsPreLoginResponse Parse(ReadOnlySpan<byte> payload)
    {
        Version? version = null;
        TdsEncryptionLevel? encryption = null;
        var mars = false;
        var federatedAuthenticationRequired = false;
        ReadOnlyMemory<byte> nonce = default;
        var position = 0;
        while (true)
        {
            Ensure(payload, position, 1);
            var option = payload[position++];
            if (option == Terminator)
            {
                break;
            }

            Ensure(payload, position, 4);
            int offset = BinaryPrimitives.ReadUInt16BigEndian(payload[position..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(payload[(position + 2)..]);
            position += 4;
            Ensure(payload, offset, length);
            var value = payload.Slice(offset, length);
            switch (option)
            {
                case VersionOption when length == 6:
                    version = new Version(
                      value[0],
                      value[1],
                      BinaryPrimitives.ReadUInt16BigEndian(value[2..]),
                      BinaryPrimitives.ReadUInt16BigEndian(value[4..]));
                    break;
                case EncryptionOption when length == 1:
                    encryption = value[0] <= (byte)TdsEncryptionLevel.Required
                      ? (TdsEncryptionLevel)value[0]
                      : throw new InvalidDataException(
                        $"Invalid SQL Server PRELOGIN encryption level {value[0]}.");
                    break;
                case MarsOption when length == 1:
                    mars = value[0] != 0;
                    break;
                case FedAuthRequiredOption:
                    federatedAuthenticationRequired = length == 1 && value[0] <= 1
                      ? value[0] == 1
                      : throw new InvalidDataException(
                        "SQL Server PRELOGIN response contains an invalid FEDAUTHREQUIRED option.");
                    break;
                case NonceOption:
                    nonce = length == NonceLength
                      ? value.ToArray()
                      : throw new InvalidDataException(
                        $"SQL Server PRELOGIN NONCEOPT option has length {length}; expected {NonceLength}.");
                    break;
            }
        }

        return new TdsPreLoginResponse(
          version,
          encryption ?? throw new InvalidDataException(
            "SQL Server PRELOGIN response omitted the ENCRYPTION option."),
          mars,
          federatedAuthenticationRequired,
          nonce);
    }

    private static void WriteOption(
        Span<byte> payload,
        ref int tablePosition,
        byte option,
        int offset,
        int length)
    {
        payload[tablePosition++] = option;
        BinaryPrimitives.WriteUInt16BigEndian(payload[tablePosition..], checked((ushort)offset));
        tablePosition += 2;
        BinaryPrimitives.WriteUInt16BigEndian(payload[tablePosition..], checked((ushort)length));
        tablePosition += 2;
    }

    private static void Ensure(ReadOnlySpan<byte> payload, int position, int length)
    {
        if (position < 0 || length < 0 || position > payload.Length - length)
        {
            throw new InvalidDataException("SQL Server PRELOGIN option table is truncated.");
        }
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Apex.MsSqlClient.Internal;

internal static class TdsLogin7
{
    private const int FixedLength = 94;
    private const int MaximumFieldCharacters = 128;

    internal static byte[] Encode(MsSqlConnectOptions options)
    {
        var hostName = options.WorkstationId ?? Environment.MachineName;
        ValidateField(hostName, nameof(options.WorkstationId));
        ValidateField(options.Username, nameof(options.Username));
        ValidateField(options.Password, nameof(options.Password));
        ValidateField(options.ApplicationName, nameof(options.ApplicationName));
        ValidateField(options.Host, nameof(options.Host));
        ValidateField(options.ClientInterfaceName, nameof(options.ClientInterfaceName));
        ValidateField(options.Database, nameof(options.Database));

        var dataLength = checked(
          (hostName.Length +
           options.Username.Length +
           options.Password.Length +
           options.ApplicationName.Length +
           options.Host.Length +
           options.ClientInterfaceName.Length +
           options.Database.Length) * 2 +
          11);
        var login = new byte[checked(FixedLength + dataLength)];
        BinaryPrimitives.WriteInt32LittleEndian(login, login.Length);
        BinaryPrimitives.WriteUInt32BigEndian(login.AsSpan(4), 0x04000074);
        BinaryPrimitives.WriteInt32LittleEndian(login.AsSpan(8), options.PacketSize);
        BinaryPrimitives.WriteInt32LittleEndian(login.AsSpan(16), Environment.ProcessId);
        login[24] = 0xC0;
        login[25] = 0x02;
        login[27] = 0x10;

        var data = FixedLength;
        WriteField(login, 36, ref data, hostName, obfuscate: false);
        WriteField(login, 40, ref data, options.Username, obfuscate: false);
        WriteField(login, 44, ref data, options.Password, obfuscate: true);
        WriteField(login, 48, ref data, options.ApplicationName, obfuscate: false);
        WriteField(login, 52, ref data, options.Host, obfuscate: false);
        WriteExtensionPointer(login, 56, ref data, out var featureExtensionOffset);
        WriteField(login, 60, ref data, options.ClientInterfaceName, obfuscate: false);
        WriteEmptyField(login, 64, data);
        WriteField(login, 68, ref data, options.Database, obfuscate: false);

        RandomNumberGenerator.Fill(login.AsSpan(72, 6));
        WriteEmptyField(login, 78, data);
        WriteEmptyField(login, 82, data);
        WriteEmptyField(login, 86, data);
        BinaryPrimitives.WriteInt32LittleEndian(
          login.AsSpan(featureExtensionOffset),
          data);
        login[data++] = 0x0D;
        BinaryPrimitives.WriteInt32LittleEndian(login.AsSpan(data), 1);
        data += 4;
        login[data++] = 1;
        login[data] = byte.MaxValue;
        return login;
    }

    internal static byte[] ObfuscatePassword(string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        for (var i = 0; i < bytes.Length; i++)
        {
            var value = bytes[i];
            bytes[i] = (byte)(((value >> 4) | (value << 4)) ^ 0xA5);
        }

        return bytes;
    }

    private static void WriteField(
        Span<byte> login,
        int offsetPosition,
        ref int dataPosition,
        string value,
        bool obfuscate)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
          login[offsetPosition..],
          checked((ushort)dataPosition));
        BinaryPrimitives.WriteUInt16LittleEndian(
          login[(offsetPosition + 2)..],
          checked((ushort)value.Length));
        var bytes = obfuscate ? ObfuscatePassword(value) : Encoding.Unicode.GetBytes(value);
        bytes.CopyTo(login[dataPosition..]);
        dataPosition += bytes.Length;
    }

    private static void WriteEmptyField(Span<byte> login, int offsetPosition, int dataPosition)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
          login[offsetPosition..],
          checked((ushort)dataPosition));
    }

    private static void WriteExtensionPointer(
        Span<byte> login,
        int offsetPosition,
        ref int dataPosition,
        out int pointerPosition)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
          login[offsetPosition..],
          checked((ushort)dataPosition));
        BinaryPrimitives.WriteUInt16LittleEndian(login[(offsetPosition + 2)..], 4);
        pointerPosition = dataPosition;
        dataPosition += 4;
    }

    private static void ValidateField(string value, string name)
    {
        if (value.Length > MaximumFieldCharacters)
        {
            throw new ArgumentOutOfRangeException(
              name,
              $"LOGIN7 fields are limited to {MaximumFieldCharacters} characters.");
        }
    }
}

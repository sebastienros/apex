using Apex.SqlClient;

namespace Apex.MySqlClient.Internal;

/// <summary>
/// Bridges MySQL column definitions and the common <see cref="SqlColumn"/> contract. The MySQL
/// flags and scale are packed into <see cref="SqlColumn.TypeModifier"/> and the collation into
/// <see cref="SqlColumn.TypeSize"/> so the shared record can carry the driver specific details.
/// </summary>
internal static class MySqlColumnCodec
{
    internal static int PackModifier(MySqlColumnFlags flags, byte decimals) =>
      (ushort)flags | (decimals << 16);

    internal static MySqlColumnFlags GetFlags(int modifier) =>
      (MySqlColumnFlags)unchecked((ushort)modifier);

    internal static byte GetDecimals(int modifier) => (byte)((modifier >> 16) & 0xFF);

    internal static SqlColumn ToColumn(MySqlColumnMetadata metadata, bool binary) =>
      new(
        metadata.Name,
        (byte)metadata.Type,
        unchecked((short)metadata.CharacterSet),
        PackModifier(metadata.Flags, metadata.Decimals),
        binary ? SqlDataFormat.Binary : SqlDataFormat.Text);

    /// <summary>Reads a protocol 4.1 column definition packet.</summary>
    internal static MySqlColumnMetadata Read(ReadOnlySpan<byte> payload)
    {
        MySqlPayloadReader reader = new(payload);
        _ = reader.ReadLengthEncodedString();
        var schema = reader.ReadLengthEncodedString();
        var table = reader.ReadLengthEncodedString();
        var originalTable = reader.ReadLengthEncodedString();
        var name = reader.ReadLengthEncodedString();
        var originalName = reader.ReadLengthEncodedString();
        var fixedLength = reader.ReadRequiredLengthEncodedInteger();
        if (fixedLength < 12)
        {
            throw new InvalidDataException("MySQL column definition is truncated.");
        }

        int characterSet = reader.ReadUInt16();
        var columnLength = reader.ReadUInt32();
        MySqlType type = (MySqlType)reader.ReadByte();
        MySqlColumnFlags flags = (MySqlColumnFlags)reader.ReadUInt16();
        var decimals = reader.ReadByte();
        reader.Skip(MySqlPayloadReader.ToLength(fixedLength - 10));
        return new MySqlColumnMetadata(
          name,
          originalName,
          table,
          originalTable,
          schema,
          type,
          flags,
          characterSet,
          columnLength,
          decimals);
    }
}

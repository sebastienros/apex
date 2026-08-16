using System.Buffers.Binary;
using System.Text;

namespace Apex.MySqlClient.Internal;

/// <summary>Reads the little-endian primitives of a MySQL packet payload.</summary>
internal ref struct MySqlPayloadReader
{
    private static readonly Encoding s_utf8 = new UTF8Encoding(false, true);
    private readonly ReadOnlySpan<byte> _payload;
    private int _position;

    internal MySqlPayloadReader(ReadOnlySpan<byte> payload)
    {
        _payload = payload;
    }

    internal readonly int Remaining => _payload.Length - _position;

    internal readonly int Position => _position;

    internal byte ReadByte()
    {
        Ensure(sizeof(byte));
        return _payload[_position++];
    }

    internal readonly byte PeekByte()
    {
        Ensure(sizeof(byte));
        return _payload[_position];
    }

    internal ushort ReadUInt16()
    {
        Ensure(sizeof(ushort));
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_payload[_position..]);
        _position += sizeof(ushort);
        return value;
    }

    internal uint ReadUInt24()
    {
        Ensure(3);
        var value = (uint)(_payload[_position] |
          (_payload[_position + 1] << 8) |
          (_payload[_position + 2] << 16));
        _position += 3;
        return value;
    }

    internal uint ReadUInt32()
    {
        Ensure(sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_payload[_position..]);
        _position += sizeof(uint);
        return value;
    }

    internal ulong ReadUInt64()
    {
        Ensure(sizeof(ulong));
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_payload[_position..]);
        _position += sizeof(ulong);
        return value;
    }

    internal void Skip(int length)
    {
        Ensure(length);
        _position += length;
    }

    /// <summary>Reads a length encoded integer, returning <see langword="null"/> for the NULL marker.</summary>
    internal ulong? ReadLengthEncodedInteger()
    {
        var first = ReadByte();
        return first switch
        {
            < 0xFB => first,
            MySqlProtocol.NullHeader => null,
            0xFC => ReadUInt16(),
            0xFD => ReadUInt24(),
            0xFE => ReadUInt64(),
            _ => throw new InvalidDataException(
              $"Invalid MySQL length encoded integer prefix 0x{first:X2}."),
        };
    }

    internal ulong ReadRequiredLengthEncodedInteger() =>
      ReadLengthEncodedInteger() ??
      throw new InvalidDataException("Unexpected NULL length encoded integer.");

    /// <summary>Reads a length encoded byte string, returning an empty span for the NULL marker.</summary>
    internal ReadOnlySpan<byte> ReadLengthEncodedSpan(out bool isNull)
    {
        var length = ReadLengthEncodedInteger();
        if (length is null)
        {
            isNull = true;
            return default;
        }

        isNull = false;
        return ReadSpan(ToLength(length.Value));
    }

    internal string ReadLengthEncodedString()
    {
        var value = ReadLengthEncodedSpan(out var isNull);
        return isNull ? string.Empty : s_utf8.GetString(value);
    }

    internal string ReadNullTerminatedString()
    {
        var remaining = _payload[_position..];
        var length = remaining.IndexOf((byte)0);
        if (length < 0)
        {
            throw new InvalidDataException("MySQL string is not null terminated.");
        }

        var value = s_utf8.GetString(remaining[..length]);
        _position += length + 1;
        return value;
    }

    internal string ReadRemainingString()
    {
        var value = _payload[_position..];
        _position = _payload.Length;
        return value.IsEmpty ? string.Empty : s_utf8.GetString(value);
    }

    internal ReadOnlySpan<byte> ReadRemainingSpan()
    {
        var value = _payload[_position..];
        _position = _payload.Length;
        return value;
    }

    internal ReadOnlySpan<byte> ReadSpan(int length)
    {
        Ensure(length);
        var value = _payload.Slice(_position, length);
        _position += length;
        return value;
    }

    internal static int ToLength(ulong value) =>
      value <= int.MaxValue
        ? (int)value
        : throw new InvalidDataException($"MySQL value length {value} exceeds the supported size.");

    private readonly void Ensure(int length)
    {
        if (length < 0 || _position > _payload.Length - length)
        {
            throw new InvalidDataException("MySQL packet payload is truncated.");
        }
    }
}

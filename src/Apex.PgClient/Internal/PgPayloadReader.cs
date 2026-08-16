using System.Buffers.Binary;
using System.Text;

namespace Apex.PgClient.Internal;

internal ref struct PgPayloadReader
{
    private static readonly Encoding s_utf8 = new UTF8Encoding(false, true);
    private readonly ReadOnlySpan<byte> _payload;
    private int _position;

    public PgPayloadReader(ReadOnlySpan<byte> payload)
    {
        _payload = payload;
    }

    public int Remaining => _payload.Length - _position;

    public byte ReadByte()
    {
        Ensure(sizeof(byte));
        return _payload[_position++];
    }

    public short ReadInt16()
    {
        Ensure(sizeof(short));
        var value = BinaryPrimitives.ReadInt16BigEndian(_payload[_position..]);
        _position += sizeof(short);
        return value;
    }

    public int ReadInt32()
    {
        Ensure(sizeof(int));
        var value = BinaryPrimitives.ReadInt32BigEndian(_payload[_position..]);
        _position += sizeof(int);
        return value;
    }

    public string ReadCString()
    {
        var remaining = _payload[_position..];
        var length = remaining.IndexOf((byte)0);
        if (length < 0)
        {
            throw new InvalidDataException("PostgreSQL string is not null terminated.");
        }

        var value = s_utf8.GetString(remaining[..length]);
        _position += length + 1;
        return value;
    }

    public string ReadString(int length)
    {
        Ensure(length);
        var value = s_utf8.GetString(_payload.Slice(_position, length));
        _position += length;
        return value;
    }

    public byte[] ReadBytes(int length)
    {
        return ReadSpan(length).ToArray();
    }

    public ReadOnlySpan<byte> ReadSpan(int length)
    {
        Ensure(length);
        var value = _payload.Slice(_position, length);
        _position += length;
        return value;
    }

    private void Ensure(int length)
    {
        if (length < 0 || _position > _payload.Length - length)
        {
            throw new InvalidDataException("PostgreSQL message payload is truncated.");
        }
    }
}

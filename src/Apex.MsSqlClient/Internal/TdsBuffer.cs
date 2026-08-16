using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Apex.MsSqlClient.Internal;

internal static class TdsBufferWriterExtensions
{
    internal static void WriteByte(this IBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    internal static void WriteInt16LittleEndian(this IBufferWriter<byte> writer, short value)
    {
        var destination = writer.GetSpan(sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(destination, value);
        writer.Advance(sizeof(short));
    }

    internal static void WriteUInt16LittleEndian(this IBufferWriter<byte> writer, ushort value)
    {
        var destination = writer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(destination, value);
        writer.Advance(sizeof(ushort));
    }

    internal static void WriteUInt16BigEndian(this IBufferWriter<byte> writer, ushort value)
    {
        var destination = writer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16BigEndian(destination, value);
        writer.Advance(sizeof(ushort));
    }

    internal static void WriteInt32LittleEndian(this IBufferWriter<byte> writer, int value)
    {
        var destination = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(destination, value);
        writer.Advance(sizeof(int));
    }

    internal static void WriteUInt32LittleEndian(this IBufferWriter<byte> writer, uint value)
    {
        var destination = writer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        writer.Advance(sizeof(uint));
    }

    internal static void WriteInt64LittleEndian(this IBufferWriter<byte> writer, long value)
    {
        var destination = writer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(destination, value);
        writer.Advance(sizeof(long));
    }

    internal static void WriteUInt64LittleEndian(this IBufferWriter<byte> writer, ulong value)
    {
        var destination = writer.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(destination, value);
        writer.Advance(sizeof(ulong));
    }

    internal static void WriteUInt24LittleEndian(this IBufferWriter<byte> writer, int value)
    {
        if ((uint)value > 0x00FF_FFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        var destination = writer.GetSpan(3);
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
        writer.Advance(3);
    }

    internal static void WriteUInt40LittleEndian(this IBufferWriter<byte> writer, long value)
    {
        if ((ulong)value > 0xFF_FFFF_FFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        var destination = writer.GetSpan(5);
        for (var i = 0; i < 5; i++)
        {
            destination[i] = (byte)(value >> (8 * i));
        }

        writer.Advance(5);
    }

    internal static void WriteUtf16(this IBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.Unicode.GetByteCount(value);
        var destination = writer.GetSpan(byteCount);
        var written = Encoding.Unicode.GetBytes(value, destination);
        writer.Advance(written);
    }

    internal static void WriteBVarChar(this IBufferWriter<byte> writer, string value)
    {
        if (value.Length > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        writer.WriteByte((byte)value.Length);
        writer.WriteUtf16(value);
    }
}

internal ref struct TdsPayloadReader
{
    private readonly ReadOnlySpan<byte> _payload;
    private int _position;

    internal TdsPayloadReader(ReadOnlySpan<byte> payload)
    {
        _payload = payload;
    }

    internal int Position
    {
        readonly get => _position;
        set
        {
            if ((uint)value > (uint)_payload.Length)
            {
                throw new InvalidDataException("TDS payload position is outside the message.");
            }

            _position = value;
        }
    }

    internal readonly int Remaining => _payload.Length - _position;

    internal byte ReadByte()
    {
        Ensure(1);
        return _payload[_position++];
    }

    internal short ReadInt16LittleEndian()
    {
        var value = ReadSpan(sizeof(short));
        return BinaryPrimitives.ReadInt16LittleEndian(value);
    }

    internal ushort ReadUInt16LittleEndian()
    {
        var value = ReadSpan(sizeof(ushort));
        return BinaryPrimitives.ReadUInt16LittleEndian(value);
    }

    internal ushort ReadUInt16BigEndian()
    {
        var value = ReadSpan(sizeof(ushort));
        return BinaryPrimitives.ReadUInt16BigEndian(value);
    }

    internal int ReadInt32LittleEndian()
    {
        var value = ReadSpan(sizeof(int));
        return BinaryPrimitives.ReadInt32LittleEndian(value);
    }

    internal uint ReadUInt32LittleEndian()
    {
        var value = ReadSpan(sizeof(uint));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    internal long ReadInt64LittleEndian()
    {
        var value = ReadSpan(sizeof(long));
        return BinaryPrimitives.ReadInt64LittleEndian(value);
    }

    internal ulong ReadUInt64LittleEndian()
    {
        var value = ReadSpan(sizeof(ulong));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }

    internal int ReadUInt24LittleEndian()
    {
        var value = ReadSpan(3);
        return value[0] | value[1] << 8 | value[2] << 16;
    }

    internal long ReadUInt40LittleEndian()
    {
        var value = ReadSpan(5);
        long result = 0;
        for (var i = 0; i < value.Length; i++)
        {
            result |= (long)value[i] << (8 * i);
        }

        return result;
    }

    internal ReadOnlySpan<byte> ReadSpan(int length)
    {
        Ensure(length);
        var value = _payload.Slice(_position, length);
        _position += length;
        return value;
    }

    internal void Skip(int length) => _ = ReadSpan(length);

    internal string ReadBVarChar()
    {
        int characterCount = ReadByte();
        return ReadUnicode(characterCount);
    }

    internal string ReadUsVarChar()
    {
        int characterCount = ReadUInt16LittleEndian();
        return ReadUnicode(characterCount);
    }

    internal string ReadUnicode(int characterCount)
    {
        var byteCount = checked(characterCount * 2);
        return Encoding.Unicode.GetString(ReadSpan(byteCount));
    }

    private readonly void Ensure(int length)
    {
        if (length < 0 || _position > _payload.Length - length)
        {
            throw new InvalidDataException("TDS payload is truncated.");
        }
    }
}

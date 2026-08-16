using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Apex.MySqlClient.Internal;

/// <summary>Builds a MySQL packet payload into a pooled, growable buffer.</summary>
internal sealed class MySqlPayloadWriter
{
    private const int RetainedBufferLimit = 1024 * 1024;
    private static readonly Encoding s_utf8 = new UTF8Encoding(false, true);
    private byte[] _buffer;
    private int _length;

    internal MySqlPayloadWriter(int capacity = 256)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(capacity);
    }

    internal int Length => _length;

    internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    internal void Reset()
    {
        _length = 0;
        if (_buffer.Length <= RetainedBufferLimit)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = ArrayPool<byte>.Shared.Rent(256);
    }

    internal void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_length++] = value;
    }

    internal void WriteUInt16(ushort value)
    {
        EnsureCapacity(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length), value);
        _length += sizeof(ushort);
    }

    internal void WriteUInt32(uint value)
    {
        EnsureCapacity(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += sizeof(uint);
    }

    internal void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

    internal void WriteUInt64(ulong value)
    {
        EnsureCapacity(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_length), value);
        _length += sizeof(ulong);
    }

    internal void WriteInt64(long value) => WriteUInt64(unchecked((ulong)value));

    internal void WriteSingle(float value) =>
      WriteUInt32(unchecked((uint)BitConverter.SingleToInt32Bits(value)));

    internal void WriteDouble(double value) =>
      WriteUInt64(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));

    internal void WriteZero(int count)
    {
        EnsureCapacity(count);
        _buffer.AsSpan(_length, count).Clear();
        _length += count;
    }

    internal void WriteBytes(ReadOnlySpan<byte> value)
    {
        EnsureCapacity(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    internal void WriteLengthEncodedInteger(ulong value)
    {
        if (value < 0xFB)
        {
            WriteByte((byte)value);
        }
        else if (value <= ushort.MaxValue)
        {
            WriteByte(0xFC);
            WriteUInt16((ushort)value);
        }
        else if (value <= 0xFFFFFF)
        {
            WriteByte(0xFD);
            EnsureCapacity(3);
            _buffer[_length] = (byte)value;
            _buffer[_length + 1] = (byte)(value >> 8);
            _buffer[_length + 2] = (byte)(value >> 16);
            _length += 3;
        }
        else
        {
            WriteByte(0xFE);
            WriteUInt64(value);
        }
    }

    internal void WriteLengthEncodedBytes(ReadOnlySpan<byte> value)
    {
        WriteLengthEncodedInteger((ulong)value.Length);
        WriteBytes(value);
    }

    internal void WriteLengthEncodedString(string value)
    {
        var byteCount = s_utf8.GetByteCount(value);
        WriteLengthEncodedInteger((ulong)byteCount);
        EnsureCapacity(byteCount);
        _length += s_utf8.GetBytes(value, _buffer.AsSpan(_length));
    }

    internal void WriteUtf8(string value)
    {
        var byteCount = s_utf8.GetByteCount(value);
        EnsureCapacity(byteCount);
        _length += s_utf8.GetBytes(value, _buffer.AsSpan(_length));
    }

    internal void WriteNullTerminatedString(string value)
    {
        WriteUtf8(value);
        WriteByte(0);
    }

    internal void Release()
    {
        var buffer = _buffer;
        _buffer = [];
        _length = 0;
        if (buffer.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void EnsureCapacity(int additional)
    {
        var required = checked(_length + additional);
        if (required <= _buffer.Length)
        {
            return;
        }

        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(required, _buffer.Length * 2));
        _buffer.AsSpan(0, _length).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }
}

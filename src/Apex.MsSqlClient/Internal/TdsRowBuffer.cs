using System.Buffers;
using System.Buffers.Binary;

namespace Apex.MsSqlClient.Internal;

internal sealed class TdsRowBuffer : IBufferWriter<byte>
{
    private byte[] _buffer;
    private int _length;

    internal TdsRowBuffer(int capacity = 256)
    {
        _buffer = new byte[capacity];
    }

    internal int WrittenCount => _length;

    internal int Capacity => _buffer.Length;

    internal ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _length);

    internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    internal void Clear() => _length = 0;

    internal void WriteUInt16LittleEndian(ushort value)
    {
        EnsureCapacity(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length), value);
        _length += sizeof(ushort);
    }

    internal void WriteInt32LittleEndian(int value)
    {
        EnsureCapacity(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += sizeof(int);
    }

    /// <summary>Appends a null field: a length of -1 and no value.</summary>
    internal void WriteNull() => WriteInt32LittleEndian(-1);

    /// <summary>Appends a field: its 4-byte length, then the value.</summary>
    internal void WriteValue(ReadOnlySpan<byte> value)
    {
        EnsureCapacity(sizeof(int) + value.Length);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length), value.Length);
        value.CopyTo(_buffer.AsSpan(_length + sizeof(int)));
        _length += sizeof(int) + value.Length;
    }

    /// <summary>Appends raw bytes.</summary>
    internal void Write(ReadOnlySpan<byte> value)
    {
        EnsureCapacity(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    internal void PatchInt32LittleEndian(int offset, int value)
    {
        if ((uint)offset > (uint)(_length - sizeof(int)))
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(offset), value);
    }

    public void Advance(int count)
    {
        if (count < 0 || count > _buffer.Length - _length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _length += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_length);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_length);
    }

    private void EnsureCapacity(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

        var required = checked(_length + Math.Max(sizeHint, 1));
        if (required <= _buffer.Length)
        {
            return;
        }

        Array.Resize(ref _buffer, Math.Max(required, checked(_buffer.Length * 2)));
    }
}

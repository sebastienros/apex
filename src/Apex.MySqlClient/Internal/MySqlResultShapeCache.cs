using System.Buffers.Binary;
using System.IO.Hashing;
using Apex.SqlClient.Internal;

namespace Apex.MySqlClient.Internal;

/// <summary>
/// Remembers the column definitions of recent result sets on one connection, keyed by their raw
/// packet bytes. A repeated query shape then reuses the parsed metadata, the
/// <see cref="Apex.SqlClient.SqlColumn"/> array and the row decoder instead of decoding six
/// strings and allocating two records per column for every result.
/// </summary>
/// <remarks>
/// Identical definition bytes always produce identical metadata, so a hit can never change what a
/// result reports. Decoders are immutable after construction, which is what makes sharing them
/// between result sets (including buffered results read on other threads) safe. The cache is
/// owned by a single connection and is only used by its receive path, so it needs no locking.
/// </remarks>
internal sealed class MySqlResultShapeCache
{
    private const int SlotCount = 16;
    private const int MaximumCachedKeyLength = 16 * 1024;
    private const int RetainedScratchLimit = 64 * 1024;
    private readonly Shape?[] _shapes = new Shape?[SlotCount];
    private readonly Utf8StringCache _strings;
    private readonly MySqlZeroDateBehavior _zeroDates;
    private byte[] _scratch = new byte[1024];
    private int _length;

    internal MySqlResultShapeCache(Utf8StringCache strings, MySqlZeroDateBehavior zeroDates)
    {
        _strings = strings;
        _zeroDates = zeroDates;
    }

    /// <summary>Starts collecting the definition packets of a new result set.</summary>
    internal void Begin() => _length = 0;

    /// <summary>Appends one column definition packet payload.</summary>
    internal void Append(ReadOnlySpan<byte> payload)
    {
        var required = checked(_length + sizeof(int) + payload.Length);
        if (required > _scratch.Length)
        {
            Array.Resize(ref _scratch, Math.Max(required, checked(_scratch.Length * 2)));
        }

        BinaryPrimitives.WriteInt32LittleEndian(_scratch.AsSpan(_length), payload.Length);
        payload.CopyTo(_scratch.AsSpan(_length + sizeof(int)));
        _length = required;
    }

    /// <summary>Returns the decoder for the collected definitions, parsing them only on a miss.</summary>
    internal MySqlRowDecoder Complete(int columnCount, bool binary)
    {
        var key = _scratch.AsSpan(0, _length);
        var hash = XxHash3.HashToUInt64(key);
        var slot = (int)(hash & (SlotCount - 1));
        var shape = _shapes[slot];
        if (shape is null || shape.Hash != hash || !key.SequenceEqual(shape.Key))
        {
            // Only a shape that will be cached needs its own copy of the key.
            var cacheable = key.Length <= MaximumCachedKeyLength;
            shape = new Shape(hash, cacheable ? key.ToArray() : [], Parse(key, columnCount));
            if (cacheable)
            {
                _shapes[slot] = shape;
            }
        }

        if (_scratch.Length > RetainedScratchLimit)
        {
            _scratch = new byte[1024];
        }

        _length = 0;
        return shape.GetDecoder(binary, _strings, _zeroDates);
    }

    private static MySqlColumnMetadata[] Parse(ReadOnlySpan<byte> key, int columnCount)
    {
        var metadata = new MySqlColumnMetadata[columnCount];
        var position = 0;
        for (var i = 0; i < metadata.Length; i++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(key[position..]);
            position += sizeof(int);
            metadata[i] = MySqlColumnCodec.Read(key.Slice(position, length));
            position += length;
        }

        return metadata;
    }

    private sealed class Shape
    {
        private MySqlRowDecoder? _text;
        private MySqlRowDecoder? _binary;

        internal Shape(ulong hash, byte[] key, MySqlColumnMetadata[] metadata)
        {
            Hash = hash;
            Key = key;
            Metadata = metadata;
        }

        internal ulong Hash { get; }

        internal byte[] Key { get; }

        internal MySqlColumnMetadata[] Metadata { get; }

        internal MySqlRowDecoder GetDecoder(
            bool binary,
            Utf8StringCache strings,
            MySqlZeroDateBehavior zeroDates)
        {
            ref var decoder = ref binary ? ref _binary : ref _text;
            if (decoder is null)
            {
                decoder = new MySqlRowDecoder(strings, zeroDates);
                decoder.SetColumns(Metadata, binary);
            }

            return decoder;
        }
    }
}

using System.IO.Hashing;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MsSqlClient.Internal;

/// <summary>The parsed column metadata of one result set, shared by results of the same shape.</summary>
internal sealed class TdsResultShape
{
    internal static readonly TdsResultShape Empty = new([]);

    internal TdsResultShape(TdsColumn[] columns)
    {
        Columns = columns;
        var sqlColumns = new SqlColumn[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            sqlColumns[i] = columns[i].Column;
        }

        SqlColumns = sqlColumns;
        Ordinals = SqlColumnOrdinalMapCache.GetOrAdd(sqlColumns);
    }

    internal TdsColumn[] Columns { get; }

    internal SqlColumn[] SqlColumns { get; }

    internal SqlColumnOrdinalMap Ordinals { get; }
}

/// <summary>
/// Remembers the column metadata of recent result sets on one connection, keyed by the raw bytes
/// of their COLMETADATA token. Repeated executions of a statement then reuse the parsed columns,
/// their names, the <see cref="SqlColumn"/> array and the ordinal map instead of decoding them for
/// every result.
/// </summary>
/// <remarks>
/// Identical metadata bytes always parse to identical columns, so a hit can never change what a
/// result reports. Shapes are immutable, which is what makes sharing them between result sets
/// (including buffered results read on other threads) safe. Results of the same shape share one
/// <see cref="TdsResultShape"/>, so result boundaries must never be detected by comparing column
/// lists; readers count results instead. The cache is owned by one connection and used only by
/// its receive path, which runs one command at a time, so it needs no locking.
/// </remarks>
internal sealed class TdsResultShapeCache
{
    private const int SlotCount = 16;
    internal const int MaximumCachedKeyLength = 16 * 1024;
    private const int RetainedScratchLimit = 64 * 1024;
    private readonly Shape?[] _shapes = new Shape?[SlotCount];
    private byte[] _scratch = [];

    /// <summary>
    /// Returns the shape for a complete COLMETADATA body (the bytes after the token type), parsing
    /// it only on a miss.
    /// </summary>
    internal TdsResultShape GetOrAdd(ReadOnlySpan<byte> metadata)
    {
        var hash = XxHash3.HashToUInt64(metadata);
        var slot = (int)(hash & (SlotCount - 1));
        var cached = _shapes[slot];
        if (cached is not null && cached.Hash == hash && metadata.SequenceEqual(cached.Key))
        {
            return cached.Result;
        }

        var result = Parse(metadata);
        // Only a shape that will be cached needs its own copy of the key.
        if (metadata.Length <= MaximumCachedKeyLength)
        {
            _shapes[slot] = new Shape(hash, metadata.ToArray(), result);
        }

        return result;
    }

    /// <summary>Parses COLMETADATA without caching it.</summary>
    internal static TdsResultShape Parse(ReadOnlySpan<byte> metadata)
    {
        TdsPayloadReader reader = new(metadata);
        var columns = TdsTypeCodec.ReadColumns(ref reader);
        if (reader.Position != metadata.Length)
        {
            throw new InvalidDataException(
              "SQL Server COLMETADATA did not end where its column list ended.");
        }

        return columns.Length == 0 ? TdsResultShape.Empty : new TdsResultShape(columns);
    }

    /// <summary>
    /// Returns a reusable buffer of at least <paramref name="length"/> bytes that keeps its first
    /// <paramref name="preserve"/> bytes, for collecting a token that spans packets.
    /// </summary>
    internal byte[] GetScratch(int length, int preserve)
    {
        if (_scratch.Length < length)
        {
            var grown = new byte[Math.Max(length, Math.Max(1024, _scratch.Length * 2))];
            _scratch.AsSpan(0, preserve).CopyTo(grown);
            _scratch = grown;
        }

        return _scratch;
    }

    /// <summary>Drops a scratch buffer that grew past the retained limit.</summary>
    internal void TrimScratch()
    {
        if (_scratch.Length > RetainedScratchLimit)
        {
            _scratch = [];
        }
    }

    private sealed class Shape(ulong hash, byte[] key, TdsResultShape result)
    {
        internal ulong Hash { get; } = hash;

        internal byte[] Key { get; } = key;

        internal TdsResultShape Result { get; } = result;
    }
}

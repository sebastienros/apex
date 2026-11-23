using System.Buffers;

namespace Apex.SqlClient.Internal;

internal sealed class SqlRowPage
{
    internal SqlRowPage(byte[] data, ISqlRowDecoder decoder)
    {
        Data = data;
        Decoder = decoder;
    }

    internal byte[] Data { get; }

    internal ISqlRowDecoder Decoder { get; }
}

internal sealed class SqlRowPageBuilder
{
    private readonly ISqlRowDecoder _decoder;
    private RowRange[] _rows;
    private byte[] _buffer;
    private int _count;
    private int _length;

    internal SqlRowPageBuilder(
        ISqlRowDecoder decoder,
        int rowCapacity = 16,
        int byteCapacity = 1024)
    {
        _decoder = decoder;
        _rows = new RowRange[rowCapacity];
        _buffer = new byte[byteCapacity];
    }

    internal int Count => _count;

    internal int ByteLength => _length;

    internal void Add(ReadOnlySpan<byte> row)
    {
        EnsureCapacity(row.Length);
        row.CopyTo(_buffer.AsSpan(_length));
        if (_count == _rows.Length)
        {
            Array.Resize(ref _rows, checked(_rows.Length * 2));
        }

        _rows[_count++] = new RowRange(_length, row.Length);
        _length += row.Length;
    }

    internal SqlRow[] Build(IReadOnlyList<SqlColumn> columns)
    {
        var batch = BuildBatch(columns);
        if (batch.Count == 0)
        {
            return [];
        }

        SqlRow[] rows = new SqlRow[batch.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = batch.CreateRow(i);
        }

        return rows;
    }

    internal SqlRowPageBatch BuildBatch(IReadOnlyList<SqlColumn> columns) =>
      new(
        new SqlRowPage(_buffer, _decoder),
        columns,
        _rows,
        _count);

    private void EnsureCapacity(int additional)
    {
        var required = checked(_length + additional);
        if (required <= _buffer.Length)
        {
            return;
        }

        var capacity = Math.Max(required, checked(_buffer.Length * 2));
        Array.Resize(ref _buffer, capacity);
    }

    internal readonly record struct RowRange(int Offset, int Length);
}

// Buffers a whole result. Rows are accumulated in pooled scratch buffers; each completed
// page is copied once into an exact-size array that its rows alias for their lifetime, so
// a small result allocates only what it keeps. Build fills the final row array directly.
internal sealed class SqlRowPageCollectionBuilder
{
    private const int MaximumPageRows = 256;
    private const int MaximumPageBytes = 64 * 1024;

    // A row this large shares a page with at most smaller rows, so it gets its own exact
    // array instead of being copied through scratch and then copied again.
    private const int DedicatedPageBytes = MaximumPageBytes / 2;
    private const int InitialScratchBytes = 1024;
    private const int InitialRowCapacity = 16;
    private readonly ISqlRowDecoder _decoder;
    private byte[]? _scratch;
    private SqlRowPageBuilder.RowRange[]? _ranges;
    private SqlRowPage? _firstPage;
    private int _firstPageEnd;
    private List<(SqlRowPage Page, int EndRow)>? _additionalPages;
    private SqlRow[]? _built;
    private int _count;
    private int _pageStart;
    private int _pageLength;

    internal SqlRowPageCollectionBuilder(ISqlRowDecoder decoder)
    {
        _decoder = decoder;
    }

    internal void Add(ReadOnlySpan<byte> row)
    {
        if (_built is not null)
        {
            throw new InvalidOperationException("The row collection has already been built.");
        }

        if (row.Length >= DedicatedPageBytes)
        {
            FlushPage();
            EnsureRanges()[_count++] = new SqlRowPageBuilder.RowRange(0, row.Length);
            AddPage(new SqlRowPage(row.ToArray(), _decoder));
            _pageStart = _count;
            return;
        }

        var pageCount = _count - _pageStart;
        if (pageCount > 0 &&
            (pageCount == MaximumPageRows || row.Length > MaximumPageBytes - _pageLength))
        {
            FlushPage();
        }

        var scratch = EnsureScratch(row.Length);
        row.CopyTo(scratch.AsSpan(_pageLength));
        var ranges = EnsureRanges();
        ranges[_count++] = new SqlRowPageBuilder.RowRange(_pageLength, row.Length);
        _pageLength += row.Length;
    }

    internal SqlRow[] Build(IReadOnlyList<SqlColumn> columns)
    {
        if (_built is not null)
        {
            return _built;
        }

        FlushPage();
        if (_count == 0)
        {
            ReturnScratch();
            return _built = [];
        }

        var ordinals = SqlColumnOrdinalMapCache.GetOrAdd(columns);
        var ranges = _ranges!;
        var rows = new SqlRow[_count];
        var index = FillPage(rows, columns, ordinals, ranges, _firstPage!, 0, _firstPageEnd);
        if (_additionalPages is not null)
        {
            foreach (var (page, endRow) in _additionalPages)
            {
                index = FillPage(rows, columns, ordinals, ranges, page, index, endRow);
            }
        }

        ReturnScratch();
        _additionalPages = null;
        _firstPage = null;
        return _built = rows;
    }

    private static int FillPage(
        SqlRow[] rows,
        IReadOnlyList<SqlColumn> columns,
        SqlColumnOrdinalMap ordinals,
        SqlRowPageBuilder.RowRange[] ranges,
        SqlRowPage page,
        int start,
        int end)
    {
        for (var i = start; i < end; i++)
        {
            var range = ranges[i];
            rows[i] = new SqlRow(columns, ordinals, page, range.Offset, range.Length);
        }

        return end;
    }

    private void FlushPage()
    {
        if (_count == _pageStart)
        {
            return;
        }

        AddPage(new SqlRowPage(_scratch.AsSpan(0, _pageLength).ToArray(), _decoder));
        _pageStart = _count;
        _pageLength = 0;
    }

    // Registers a completed page that ends at the current row count.
    private void AddPage(SqlRowPage page)
    {
        if (_firstPage is null)
        {
            _firstPage = page;
            _firstPageEnd = _count;
        }
        else
        {
            (_additionalPages ??= []).Add((page, _count));
        }
    }

    private byte[] EnsureScratch(int additional)
    {
        var required = checked(_pageLength + additional);
        var scratch = _scratch;
        if (scratch is not null && required <= scratch.Length)
        {
            return scratch;
        }

        var grown = ArrayPool<byte>.Shared.Rent(
          Math.Max(required, scratch is null ? InitialScratchBytes : scratch.Length * 2));
        if (scratch is not null)
        {
            scratch.AsSpan(0, _pageLength).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(scratch);
        }

        return _scratch = grown;
    }

    private SqlRowPageBuilder.RowRange[] EnsureRanges()
    {
        var ranges = _ranges;
        if (ranges is not null && _count < ranges.Length)
        {
            return ranges;
        }

        var grown = ArrayPool<SqlRowPageBuilder.RowRange>.Shared.Rent(
          ranges is null ? InitialRowCapacity : checked(ranges.Length * 2));
        if (ranges is not null)
        {
            ranges.AsSpan(0, _count).CopyTo(grown);
            ArrayPool<SqlRowPageBuilder.RowRange>.Shared.Return(ranges);
        }

        return _ranges = grown;
    }

    private void ReturnScratch()
    {
        if (_scratch is { } scratch)
        {
            _scratch = null;
            ArrayPool<byte>.Shared.Return(scratch);
        }

        if (_ranges is { } ranges)
        {
            _ranges = null;
            ArrayPool<SqlRowPageBuilder.RowRange>.Shared.Return(ranges);
        }
    }
}

internal sealed class SqlRowPageBatch
{
    private readonly SqlRowPage _page;
    private readonly IReadOnlyList<SqlColumn> _columns;
    private readonly SqlColumnOrdinalMap _ordinals;
    private readonly SqlRowPageBuilder.RowRange[] _rows;

    internal SqlRowPageBatch(
        SqlRowPage page,
        IReadOnlyList<SqlColumn> columns,
        SqlRowPageBuilder.RowRange[] rows,
        int count)
    {
        _page = page;
        _columns = columns;
        _ordinals = SqlColumnOrdinalMapCache.GetOrAdd(columns);
        _rows = rows;
        Count = count;
    }

    internal int Count { get; }

    internal SqlRow CreateRow(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var range = _rows[index];
        return new SqlRow(
          _columns,
                    _ordinals,
          _page,
          range.Offset,
          range.Length);
    }
}

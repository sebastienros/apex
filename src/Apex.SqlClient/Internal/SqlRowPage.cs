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

internal sealed class SqlRowPageCollectionBuilder
{
    private const int MaximumPageRows = 256;
    private const int MaximumPageBytes = 64 * 1024;
    private readonly ISqlRowDecoder _decoder;
    private SqlRowPageBuilder? _firstPage;
    private List<SqlRowPageBuilder>? _additionalPages;
    private SqlRowPageBuilder? _current;
    private int _count;

    internal SqlRowPageCollectionBuilder(ISqlRowDecoder decoder)
    {
        _decoder = decoder;
    }

    internal void Add(ReadOnlySpan<byte> row)
    {
        var current = _current ??= CreatePage();
        if (current.Count > 0 &&
            (current.Count == MaximumPageRows ||
             row.Length > MaximumPageBytes - current.ByteLength))
        {
            Flush();
            current = _current = CreatePage();
        }

        current.Add(row);
        _count++;
    }

    internal SqlRow[] Build(IReadOnlyList<SqlColumn> columns)
    {
        Flush();
        if (_firstPage is null)
        {
            return [];
        }

        if (_additionalPages is null)
        {
            return _firstPage.Build(columns);
        }

        SqlRow[] rows = new SqlRow[_count];
        var offset = 0;
        var firstRows = _firstPage.Build(columns);
        firstRows.CopyTo(rows, offset);
        offset += firstRows.Length;
        foreach (var page in _additionalPages)
        {
            var pageRows = page.Build(columns);
            pageRows.CopyTo(rows, offset);
            offset += pageRows.Length;
        }

        return rows;
    }

    private void Flush()
    {
        var current = _current;
        if (current is null || current.Count == 0)
        {
            return;
        }

        if (_firstPage is null)
        {
            _firstPage = current;
        }
        else
        {
            (_additionalPages ??= []).Add(current);
        }

        _current = null;
    }

    private SqlRowPageBuilder CreatePage() =>
      new(
        _decoder,
        rowCapacity: 16,
        byteCapacity: 1024);
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

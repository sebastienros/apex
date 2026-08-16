using Apex.SqlClient;

namespace Apex.MsSqlClient;

internal sealed class MsSqlCursor : ISqlCursor
{
    private readonly SqlRowSet _rows;
    private readonly int _defaultFetchSize;
    private int _position;
    private bool _disposed;

    internal MsSqlCursor(SqlRowSet rows, int defaultFetchSize)
    {
        _rows = rows;
        _defaultFetchSize = defaultFetchSize;
    }

    public bool HasMore => !_disposed && _position < _rows.Count;

    public IReadOnlyList<SqlColumn> Columns => _rows.Columns;

    public ValueTask<SqlRowSet> ReadAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var requested = count == 0 ? _defaultFetchSize : count;
        var available = Math.Min(requested, _rows.Count - _position);
        SqlRow[] page = new SqlRow[available];
        for (var i = 0; i < available; i++)
        {
            page[i] = _rows[_position + i];
        }

        _position += available;
        return ValueTask.FromResult(
          new SqlRowSet(
            _rows.Columns,
            page,
            _position == _rows.Count ? _rows.AffectedRows : 0,
            _position == _rows.Count ? _rows.CommandTag : string.Empty));
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

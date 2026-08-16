using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MySqlClient;

/// <summary>A progressively read result set.</summary>
/// <remarks>
/// <para>
/// The cursor pages over the streaming reader rather than over a MySQL server side cursor.
/// COM_STMT_FETCH is deliberately avoided: a statement whose first execution opens a cursor
/// stops returning rows for later plain executions, which would silently corrupt a reused
/// prepared statement. Reading through the streaming reader keeps the wire under flow control,
/// so only the rows a caller asks for are materialized at a time.
/// </para>
/// <para>
/// Because the result set stays open on the wire, the connection cannot run another command
/// until the cursor is disposed.
/// </para>
/// </remarks>
internal sealed class MySqlCursor : ISqlCursor
{
    private readonly MySqlConnection.MySqlRowReader _reader;
    private readonly int _defaultFetchSize;
    private bool _pending;
    private bool _exhausted;
    private bool _disposed;

    private MySqlCursor(
        MySqlConnection.MySqlRowReader reader,
        int defaultFetchSize,
        bool pending)
    {
        _reader = reader;
        _defaultFetchSize = defaultFetchSize;
        _pending = pending;
        _exhausted = !pending;
        Columns = reader.Columns;
    }

    public bool HasMore => !_disposed && (_pending || !_exhausted);

    public IReadOnlyList<SqlColumn> Columns { get; private set; }

    internal static async ValueTask<MySqlCursor> CreateAsync(
        MySqlConnection.MySqlRowReader reader,
        int defaultFetchSize,
        CancellationToken cancellationToken)
    {
        try
        {
            var pending = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new MySqlCursor(reader, defaultFetchSize, pending);
        }
        catch
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SqlRowSet> ReadAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var limit = count == 0 ? _defaultFetchSize : count;
        if (_exhausted && !_pending)
        {
            return new SqlRowSet(Columns, [], 0, string.Empty);
        }

        if (!_pending)
        {
            _pending = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!_pending)
            {
                _exhausted = true;
                return new SqlRowSet(Columns, [], 0, string.Empty);
            }
        }

        var decoder = _reader.Decoder;
        Columns = _reader.Columns;
        SqlRowPageBuilder page = new(
          decoder,
          rowCapacity: Math.Min(limit, 256),
          byteCapacity: Math.Max(256, Math.Min(limit, 256) * 16));
        if (_pending)
        {
            _reader.CopyCurrentTo(page);
            _pending = false;
        }

        while (page.Count < limit)
        {
            var hasRow = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!hasRow)
            {
                _exhausted = true;
                break;
            }

            if (!ReferenceEquals(decoder, _reader.Decoder))
            {
                _pending = true;
                break;
            }

            _reader.CopyCurrentTo(page);
        }

        if (page.Count < limit && !_pending)
        {
            _exhausted = true;
        }

        return new SqlRowSet(Columns, page.Build(Columns), 0, string.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _reader.DisposeAsync().ConfigureAwait(false);
    }
}

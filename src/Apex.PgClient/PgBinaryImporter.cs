using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using Apex.SqlClient.Internal;

namespace Apex.PgClient;

public sealed class PgBinaryImporter : IAsyncDisposable
{
    private static ReadOnlySpan<byte> Header =>
        [80, 71, 67, 79, 80, 89, 10, 255, 13, 10, 0];

    private readonly PgConnection _connection;
    private readonly int _columnCount;
    private readonly Activity? _activity;
    private readonly long _started;
    private ArrayBufferWriter<byte>? _row;
    private int _fieldCount;
    private bool _completed;
    private bool _disposed;
    private int _diagnosticsRecorded;

    internal PgBinaryImporter(
        PgConnection connection,
        int columnCount,
        Activity? activity,
        long started)
    {
        _connection = connection;
        _columnCount = columnCount;
        _activity = activity;
        _started = started;
    }

    internal async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        var payload = new byte[Header.Length + 8];
        Header.CopyTo(payload);
        await _connection.WriteCopyDataAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StartRowAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await CompleteCurrentRowAsync(cancellationToken).ConfigureAwait(false);
        _row = new ArrayBufferWriter<byte>();
        WriteInt16(_row, checked((short)_columnCount));
        _fieldCount = 0;
    }

    public ValueTask WriteAsync(
        PgParameter value,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var row = _row ??
            throw new InvalidOperationException("StartRowAsync must be called before writing values.");
        if (_fieldCount >= _columnCount)
        {
            throw new InvalidOperationException("The current COPY row already contains all columns.");
        }

        if (value.Value.IsNull)
        {
            WriteInt32(row, -1);
        }
        else
        {
            var format = Internal.PgParameterEncoder.ResolveFormat(
                value,
                _connection.TypeRegistry);
            if (format != PgParameterFormat.Binary)
            {
                throw new NotSupportedException(
                    $"Binary COPY requires a binary codec for PostgreSQL type {value.Type.Name}.");
            }

            var payload = Internal.PgParameterEncoder.Encode(
                value,
                format,
                _connection.TypeRegistry);
            WriteInt32(row, payload.Length);
            row.Write(payload);
        }

        _fieldCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteNullAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var row = _row ??
            throw new InvalidOperationException("StartRowAsync must be called before writing values.");
        if (_fieldCount >= _columnCount)
        {
            throw new InvalidOperationException("The current COPY row already contains all columns.");
        }

        WriteInt32(row, -1);
        _fieldCount++;
        return ValueTask.CompletedTask;
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Exception? error = null;
        try
        {
            await CompleteCurrentRowAsync(cancellationToken).ConfigureAwait(false);
            Span<byte> trailer = stackalloc byte[sizeof(short)];
            BinaryPrimitives.WriteInt16BigEndian(trailer, -1);
            await _connection.WriteCopyDataAsync(trailer.ToArray(), cancellationToken).ConfigureAwait(false);
            await _connection.CompleteCopyAsync(cancellationToken).ConfigureAwait(false);
            _completed = true;
        }
        catch (Exception exception)
        {
            error = exception;
            _activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw;
        }
        finally
        {
            RecordDiagnostics(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_completed)
        {
            Exception? error = new OperationCanceledException(
                "Binary COPY was disposed before completion.");
            try
            {
                await _connection.AbortCopyAsync(error.Message).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                error = exception;
                throw;
            }
            finally
            {
                _activity?.SetStatus(ActivityStatusCode.Error, error.Message);
                RecordDiagnostics(error);
            }
        }
    }

    private async ValueTask CompleteCurrentRowAsync(CancellationToken cancellationToken)
    {
        if (_row is null)
        {
            return;
        }

        if (_fieldCount != _columnCount)
        {
            throw new InvalidOperationException(
                $"The current COPY row has {_fieldCount} values; {_columnCount} are required.");
        }

        await _connection.WriteCopyDataAsync(_row.WrittenMemory, cancellationToken)
            .ConfigureAwait(false);
        _row = null;
        _fieldCount = 0;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed || _completed, this);

    private void RecordDiagnostics(Exception? error)
    {
        if (Interlocked.Exchange(ref _diagnosticsRecorded, 1) != 0)
        {
            return;
        }

        _activity?.Dispose();
        SqlClientDiagnostics.RecordQuery(
            Stopwatch.GetElapsedTime(_started),
            "postgresql",
            "COPY",
            error);
    }

    private static void WriteInt16(IBufferWriter<byte> writer, short value)
    {
        var span = writer.GetSpan(sizeof(short));
        BinaryPrimitives.WriteInt16BigEndian(span, value);
        writer.Advance(sizeof(short));
    }

    private static void WriteInt32(IBufferWriter<byte> writer, int value)
    {
        var span = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(span, value);
        writer.Advance(sizeof(int));
    }
}

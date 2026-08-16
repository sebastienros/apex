using System.Buffers;
using System.Runtime.ExceptionServices;
using Apex.MySqlClient.Internal;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MySqlClient;

public sealed partial class MySqlConnection
{
    /// <summary>
    /// Reads every result set produced by one command. MySQL chains additional result sets with
    /// the <see cref="MySqlServerStatus.MoreResultsExist"/> flag, which maps onto
    /// <see cref="SqlRowSet.Next"/>.
    /// </summary>
    private async ValueTask<SqlRowSet> ReadResultsAsync(
        bool binary,
        CancellationToken cancellationToken)
    {
        List<ResultData> results = [];
        while (true)
        {
            results.Add(await ReadResultSetAsync(binary, cancellationToken).ConfigureAwait(false));
            if ((_status & MySqlServerStatus.MoreResultsExist) == 0)
            {
                break;
            }
        }

        return BuildChain(results);
    }

    private async ValueTask<ResultData> ReadResultSetAsync(
        bool binary,
        CancellationToken cancellationToken)
    {
        int columnCount;
        using (var packet =
          await _reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var header = ReadResultHeader(packet.Span);
            if (header.IsLocalInfile)
            {
                var fileName = s_utf8.GetString(packet.Span[1..]);
                await HandleLocalInfileAsync(fileName, packet.Sequence, cancellationToken)
                  .ConfigureAwait(false);
                return new ResultData(
                  [],
                  [],
                  _lastCommandInfo.AffectedRows,
                  _lastCommandInfo.Info);
            }

            if (header.IsCompletion)
            {
                _lastColumns = Array.Empty<MySqlColumnMetadata>();
                return new ResultData([], [], header.AffectedRows, header.Info);
            }

            columnCount = header.ColumnCount;
        }

        var decoder = await ReadColumnDefinitionsAsync(
          columnCount,
          binary,
          cancellationToken).ConfigureAwait(false);
        SqlRowPageCollectionBuilder builder = new(decoder);
        while (true)
        {
            using var packet = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (TryCompleteResultSet(packet.Span))
            {
                break;
            }

            decoder.ValidateRow(packet.Span);
            builder.Add(packet.Span);
        }

        return new ResultData(decoder.Columns, builder.Build(decoder.Columns), 0, string.Empty);
    }

    private async ValueTask<TState> ReadCollectedResultsAsync<TState>(
        TState state,
        Action<TState, SqlRow> collector,
        CancellationToken cancellationToken)
    {
        var collect = true;
        ExceptionDispatchInfo? collectorError = null;
        while (true)
        {
            collectorError = await ReadCollectedResultSetAsync(
                state,
                collector,
                collect,
                collectorError,
                cancellationToken).ConfigureAwait(false);
            if ((_status & MySqlServerStatus.MoreResultsExist) == 0)
            {
                collectorError?.Throw();
                return state;
            }

            collect = false;
        }
    }

    private async ValueTask<ExceptionDispatchInfo?> ReadCollectedResultSetAsync<TState>(
        TState state,
        Action<TState, SqlRow> collector,
        bool collect,
        ExceptionDispatchInfo? collectorError,
        CancellationToken cancellationToken)
    {
        int columnCount;
        using (var packet = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var header = ReadResultHeader(packet.Span);
            if (header.IsLocalInfile)
            {
                var fileName = s_utf8.GetString(packet.Span[1..]);
                await HandleLocalInfileAsync(fileName, packet.Sequence, cancellationToken)
                    .ConfigureAwait(false);
                return collectorError;
            }

            if (header.IsCompletion)
            {
                _lastColumns = Array.Empty<MySqlColumnMetadata>();
                return collectorError;
            }

            columnCount = header.ColumnCount;
        }

        var decoder = await ReadColumnDefinitionsAsync(
            columnCount,
            binary: true,
            cancellationToken).ConfigureAwait(false);
        var columns = decoder.Columns;
        var ordinals = SqlColumnOrdinalMapCache.GetOrAdd(columns);
        while (true)
        {
            using var packet = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (TryCompleteResultSet(packet.Span))
            {
                return collectorError;
            }

            decoder.ValidateRow(packet.Span);
            if (collect && collectorError is null)
            {
                try
                {
                    collector(
                        state,
                        new SqlRow(columns, ordinals, decoder, packet.Memory));
                }
                catch (Exception exception)
                {
                    collectorError = ExceptionDispatchInfo.Capture(exception);
                }
            }
        }
    }

    private async ValueTask<MySqlRowDecoder> ReadColumnDefinitionsAsync(
        int columnCount,
        bool binary,
        CancellationToken cancellationToken)
    {
        var metadata = await ReadColumnMetadataAsync(columnCount, cancellationToken)
          .ConfigureAwait(false);
        MySqlRowDecoder decoder = new(_strings, _options.ZeroDateBehavior);
        decoder.SetColumns(metadata, binary);
        _lastColumns = metadata;
        return decoder;
    }

    private async ValueTask<MySqlColumnMetadata[]> ReadColumnMetadataAsync(
        int columnCount,
        CancellationToken cancellationToken)
    {
        var metadata = columnCount == 0
          ? []
          : new MySqlColumnMetadata[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            using var packet = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            metadata[i] = ReadColumnDefinition(packet.Span);
        }

        if (columnCount > 0 && !DeprecateEof)
        {
            using var packet = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            EnsureEndOfMetadata(packet.Span);
        }

        return metadata;
    }

    private async ValueTask<MySqlStatement> ReadPrepareResponseAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        uint statementId;
        int columnCount;
        int parameterCount;
        using (var packet =
          await _reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            (statementId, columnCount, parameterCount) = ReadPrepareOk(packet.Span);
        }

        if (parameterCount > 0)
        {
            _ = await ReadColumnMetadataAsync(parameterCount, cancellationToken).ConfigureAwait(false);
        }

        var columns = columnCount > 0
          ? await ReadColumnMetadataAsync(columnCount, cancellationToken).ConfigureAwait(false)
          : [];
        return new MySqlStatement(statementId, sql, parameterCount, columns);
    }

    private async ValueTask HandleLocalInfileAsync(
        string fileName,
        byte sequence,
        CancellationToken cancellationToken)
    {
        Exception? fileError = null;
        if (_options.AllowLoadLocalInfile)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                FileStream file = new(
                  fileName,
                  new FileStreamOptions
                  {
                      Mode = FileMode.Open,
                      Access = FileAccess.Read,
                      Share = FileShare.Read,
                      BufferSize = buffer.Length,
                      Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                  });
                await using var _ = file.ConfigureAwait(false);
                while (true)
                {
                    var read = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    _writer.WritePacket(++sequence, buffer.AsSpan(0, read));
                }
            }
            catch (Exception exception) when (
              exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                ArgumentException)
            {
                fileError = exception;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        _writer.WritePacket(++sequence, ReadOnlySpan<byte>.Empty);
        await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        using (var packet = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            HandleCompletionPacket(packet.Span);
        }

        if (!_options.AllowLoadLocalInfile)
        {
            throw new NotSupportedException(
              "The MySQL server requested LOCAL INFILE, but AllowLoadLocalInfile is disabled.");
        }

        if (fileError is not null)
        {
            ExceptionDispatchInfo.Capture(fileError).Throw();
        }
    }

    private ResultHeader ReadResultHeader(ReadOnlySpan<byte> payload)
    {
        if (MySqlPackets.IsError(payload))
        {
            throw ReadCommandError(payload);
        }

        if (payload.Length > 0 && payload[0] == MySqlProtocol.OkHeader)
        {
            var completion = MySqlPackets.ReadOk(payload, _capabilities);
            _status = completion.Status;
            _lastCommandInfo = completion.ToCommandInfo();
            return ResultHeader.Completion(completion.AffectedRows, completion.Info);
        }

        if (payload.Length > 0 && payload[0] == MySqlProtocol.LocalInfileHeader)
        {
            return ResultHeader.LocalInfile();
        }

        MySqlPayloadReader reader = new(payload);
        var count = reader.ReadRequiredLengthEncodedInteger();
        if (count == 0 || count > 4096)
        {
            throw new InvalidDataException($"MySQL reported an invalid column count of {count}.");
        }

        return ResultHeader.Columns((int)count);
    }

    private bool TryCompleteResultSet(ReadOnlySpan<byte> payload)
    {
        if (MySqlPackets.IsError(payload))
        {
            throw ReadCommandError(payload);
        }

        if (!MySqlPackets.IsEof(payload, DeprecateEof))
        {
            return false;
        }

        var completion = DeprecateEof
          ? MySqlPackets.ReadOk(payload, _capabilities)
          : MySqlPackets.ReadEof(payload, _capabilities);
        _status = completion.Status;
        _lastCommandInfo = completion.ToCommandInfo();
        return true;
    }

    private MySqlColumnMetadata ReadColumnDefinition(ReadOnlySpan<byte> payload)
    {
        if (MySqlPackets.IsError(payload))
        {
            throw ReadCommandError(payload);
        }

        return MySqlColumnCodec.Read(payload);
    }

    private void EnsureEndOfMetadata(ReadOnlySpan<byte> payload)
    {
        if (MySqlPackets.IsError(payload))
        {
            throw ReadCommandError(payload);
        }

        if (payload.Length == 0 || payload[0] != MySqlProtocol.EofHeader)
        {
            throw new InvalidDataException("MySQL did not terminate the column definitions.");
        }

        var completion = MySqlPackets.ReadEof(payload, _capabilities);
        _status = completion.Status;
    }

    private (uint StatementId, int ColumnCount, int ParameterCount) ReadPrepareOk(
        ReadOnlySpan<byte> payload)
    {
        if (MySqlPackets.IsError(payload))
        {
            throw ReadCommandError(payload);
        }

        MySqlPayloadReader reader = new(payload);
        if (reader.ReadByte() != MySqlProtocol.OkHeader)
        {
            throw new InvalidDataException("MySQL did not acknowledge the prepared statement.");
        }

        var statementId = reader.ReadUInt32();
        int columnCount = reader.ReadUInt16();
        int parameterCount = reader.ReadUInt16();
        reader.Skip(1);
        if (reader.Remaining >= 2)
        {
            _ = reader.ReadUInt16();
        }

        return (statementId, columnCount, parameterCount);
    }

    private MySqlException ReadCommandError(ReadOnlySpan<byte> payload)
    {
        _status &= ~MySqlServerStatus.MoreResultsExist;
        return MySqlPackets.ReadError(payload);
    }

    private static SqlRowSet BuildChain(List<ResultData> results)
    {
        if (results.Count == 0)
        {
            return SqlRowSet.Empty;
        }

        SqlRowSet? next = null;
        for (var i = results.Count - 1; i >= 0; i--)
        {
            var result = results[i];
            next = new SqlRowSet(
              result.Columns,
              result.Rows,
              result.AffectedRows,
              result.CommandTag,
              next);
        }

        return next!;
    }

    private readonly record struct ResultData(
        SqlColumn[] Columns,
        SqlRow[] Rows,
        long AffectedRows,
        string CommandTag);

    private readonly struct ResultHeader
    {
        private ResultHeader(
            bool isCompletion,
            bool isLocalInfile,
            int columnCount,
            long affectedRows,
            string info)
        {
            IsCompletion = isCompletion;
            IsLocalInfile = isLocalInfile;
            ColumnCount = columnCount;
            AffectedRows = affectedRows;
            Info = info;
        }

        internal bool IsCompletion { get; }

        internal bool IsLocalInfile { get; }

        internal int ColumnCount { get; }

        internal long AffectedRows { get; }

        internal string Info { get; }

        internal static ResultHeader Completion(long affectedRows, string info) =>
          new(true, false, 0, affectedRows, info);

        internal static ResultHeader LocalInfile() => new(false, true, 0, 0, string.Empty);

        internal static ResultHeader Columns(int count) => new(false, false, count, 0, string.Empty);
    }
}

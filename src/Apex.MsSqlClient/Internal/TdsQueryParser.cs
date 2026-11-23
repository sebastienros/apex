using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MsSqlClient.Internal;

internal readonly record struct TdsQueryResponse(
    SqlRowSet Rows,
    MsSqlException? Error,
    bool IsFinal,
    bool AttentionAcknowledged);

/// <summary>
/// Collects a buffered response (<c>QueryAsync</c>/<c>ExecuteAsync</c>) into
/// <see cref="SqlRowSet"/> results. It parses each message straight from the connection's
/// receive buffer: rows that are already received are decoded in a tight synchronous loop, and
/// anything that crosses a packet or receive boundary goes through the incremental reader.
/// </summary>
internal sealed class TdsQueryParser
{
    private readonly MsSqlRowDecoder _decoder;
    private readonly TdsResultShapeCache? _shapes;
    private readonly List<ResultBuilder> _results = [];
    private readonly TdsRowBuffer _row = new();
    private List<MsSqlInfo>? _errors;
    private ResultBuilder? _current;
    private TdsColumn[] _columns = [];
    private bool _final;
    private bool _attention;

    internal TdsQueryParser(MsSqlRowDecoder decoder, TdsResultShapeCache? shapes = null)
    {
        _decoder = decoder;
        _shapes = shapes;
    }

    /// <summary>Forgets the previous response so the parser can be reused for the next one.</summary>
    internal void Reset()
    {
        _results.Clear();
        _errors = null;
        _current = null;
        _columns = [];
        _final = false;
        _attention = false;
    }

    /// <summary>
    /// Parses the rest of the current message. Results accumulate across the messages of one
    /// response; <see cref="TdsQueryResponse.Rows"/> is built once the final DONE arrives.
    /// </summary>
    internal async ValueTask<TdsQueryResponse> ParseMessageAsync(
        TdsStreamingTokenReader tokens,
        Action<MsSqlInfo>? infoHandler = null,
        Action<TdsEnvironmentChangeInfo>? environmentHandler = null,
        Action<TdsReturnValue>? returnValueHandler = null)
    {
        while (tokens.HasRemaining)
        {
            if (_current is { } current)
            {
                while (tokens.TryReadBufferedRow(_columns, _row))
                {
                    current.AddRow(_row.WrittenSpan);
                }

                if (!tokens.HasRemaining)
                {
                    break;
                }
            }

            var token = await tokens.ReadTokenTypeAsync().ConfigureAwait(false);
            switch (token)
            {
                case TdsTokenType.ColumnMetadata:
                    var shape = await tokens.ReadColumnMetadataAsync(_shapes).ConfigureAwait(false);
                    _columns = shape.Columns;
                    _current = new ResultBuilder(_decoder, shape);
                    break;
                case TdsTokenType.Row:
                case TdsTokenType.NbcRow:
                    if (_current is null)
                    {
                        throw new InvalidDataException(
                          "SQL Server sent a ROW token before COLMETADATA.");
                    }

                    await tokens.ReadRowAsync(
                      _columns,
                      nullCompressed: token == TdsTokenType.NbcRow,
                      _row).ConfigureAwait(false);
                    _current.AddRow(_row.WrittenSpan);
                    break;
                case TdsTokenType.Done:
                case TdsTokenType.DoneProc:
                case TdsTokenType.DoneInProc:
                    HandleDone(token, await tokens.ReadDoneAsync().ConfigureAwait(false));
                    break;
                case TdsTokenType.Info:
                    var info = await tokens.ReadMessageAsync().ConfigureAwait(false);
                    infoHandler?.Invoke(info);
                    break;
                case TdsTokenType.Error:
                    (_errors ??= []).Add(await tokens.ReadMessageAsync().ConfigureAwait(false));
                    break;
                case TdsTokenType.EnvironmentChange:
                    var change = await tokens.ReadEnvironmentChangeAsync().ConfigureAwait(false);
                    environmentHandler?.Invoke(change);
                    break;
                case TdsTokenType.ReturnStatus:
                    await tokens.SkipReturnStatusAsync().ConfigureAwait(false);
                    break;
                case TdsTokenType.ReturnValue:
                    var returnValue = await tokens.ReadReturnValueAsync().ConfigureAwait(false);
                    returnValueHandler?.Invoke(returnValue);
                    break;
                case TdsTokenType.Order:
                case TdsTokenType.TableName:
                case TdsTokenType.ColumnInfo:
                case TdsTokenType.Sspi:
                case TdsTokenType.LoginAck:
                    await tokens.SkipUShortLengthTokenAsync().ConfigureAwait(false);
                    break;
                case TdsTokenType.SessionState:
                case TdsTokenType.FedAuthInfo:
                    await tokens.SkipUIntLengthTokenAsync().ConfigureAwait(false);
                    break;
                case TdsTokenType.FeatureExtAck:
                    await tokens.SkipFeatureExtAckAsync().ConfigureAwait(false);
                    break;
                default:
                    throw new NotSupportedException(
                      $"SQL Server response token 0x{token:X2} is not supported.");
            }
        }

        return new TdsQueryResponse(
          _final ? BuildResultChain(_results) : SqlRowSet.Empty,
          BuildError(_errors),
          _final,
          _attention);
    }

    private void HandleDone(byte token, TdsDoneToken done)
    {
        _attention |= (done.Status & TdsDoneStatus.Attention) != 0;
        var hasCount = (done.Status & TdsDoneStatus.Count) != 0;
        if (_current is not null)
        {
            _current.Complete(
              hasCount ? done.RowCount : 0,
              TokenName(token));
            _results.Add(_current);
            _current = null;
            _columns = [];
        }
        else if (hasCount || (_results.Count == 0 && (done.Status & TdsDoneStatus.More) == 0))
        {
            ResultBuilder result = new(_decoder, TdsResultShape.Empty);
            result.Complete(hasCount ? done.RowCount : 0, TokenName(token));
            _results.Add(result);
        }

        if ((done.Status & TdsDoneStatus.More) == 0)
        {
            _final = true;
        }
    }

    private static string TokenName(byte token) =>
      token switch
      {
          TdsTokenType.DoneProc => "DONEPROC",
          TdsTokenType.DoneInProc => "DONEINPROC",
          _ => "DONE",
      };

    private static MsSqlException? BuildError(List<MsSqlInfo>? errors)
    {
        if (errors is null)
        {
            return null;
        }

        var first = errors[0];
        return new MsSqlException(
          first.Number,
          first.State,
          first.Severity,
          first.Message,
          first.ServerName,
          first.ProcedureName,
          first.LineNumber,
          errors);
    }

    private static SqlRowSet BuildResultChain(List<ResultBuilder> builders)
    {
        if (builders.Count == 0)
        {
            return SqlRowSet.Empty;
        }

        SqlRowSet? next = null;
        for (var i = builders.Count - 1; i >= 0; i--)
        {
            next = builders[i].Build(next);
        }

        return next!;
    }

    private sealed class ResultBuilder
    {
        private readonly SqlRowPageCollectionBuilder _rows;
        private readonly TdsResultShape _shape;
        private long _affectedRows;
        private string _commandTag = string.Empty;

        internal ResultBuilder(MsSqlRowDecoder decoder, TdsResultShape shape)
        {
            _rows = new SqlRowPageCollectionBuilder(decoder);
            _shape = shape;
        }

        internal void AddRow(ReadOnlySpan<byte> row) => _rows.Add(row);

        internal void Complete(long affectedRows, string commandTag)
        {
            _affectedRows = affectedRows;
            _commandTag = commandTag;
        }

        internal SqlRowSet Build(SqlRowSet? next) =>
          new(
            _shape.SqlColumns,
            _rows.Build(_shape.SqlColumns, _shape.Ordinals),
            _affectedRows,
            _commandTag,
            next);
    }
}

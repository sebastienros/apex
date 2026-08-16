using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MsSqlClient.Internal;

internal readonly record struct TdsQueryResponse(
    SqlRowSet Rows,
    MsSqlException? Error,
    bool IsFinal,
    bool AttentionAcknowledged);

internal sealed class TdsQueryParser
{
    private readonly MsSqlRowDecoder _decoder;
    private readonly List<ResultBuilder> _results = [];
    private readonly List<MsSqlInfo> _errors = [];
    private readonly TdsRowBuffer _row = new();
    private ResultBuilder? _current;
    private IReadOnlyList<TdsColumn> _columns = Array.Empty<TdsColumn>();
    private bool _final;
    private bool _attention;

    internal TdsQueryParser(MsSqlRowDecoder decoder)
    {
        _decoder = decoder;
    }

    internal TdsQueryResponse Parse(
        ReadOnlyMemory<byte> payload,
        Action<MsSqlInfo>? infoHandler = null,
        Action<TdsEnvironmentChangeInfo>? environmentHandler = null,
        Action<TdsReturnValue>? returnValueHandler = null)
    {
        TdsTokenReader reader = new(payload);
        while (reader.HasRemaining)
        {
            var token = reader.ReadTokenType();
            switch (token)
            {
                case TdsTokenType.ColumnMetadata:
                    _columns = reader.ReadColumns();
                    _current = new ResultBuilder(_decoder, _columns);
                    break;
                case TdsTokenType.Row:
                case TdsTokenType.NbcRow:
                    if (_current is null)
                    {
                        throw new InvalidDataException(
                          "SQL Server sent a ROW token before COLMETADATA.");
                    }

                    reader.ReadRow(
                      _columns,
                      nullCompressed: token == TdsTokenType.NbcRow,
                      _row);
                    _current.AddRow(_row.WrittenSpan);
                    break;
                case TdsTokenType.Done:
                case TdsTokenType.DoneProc:
                case TdsTokenType.DoneInProc:
                    HandleDone(token, reader.ReadDone());
                    break;
                case TdsTokenType.Info:
                    var info = reader.ReadMessage();
                    infoHandler?.Invoke(info);
                    break;
                case TdsTokenType.Error:
                    _errors.Add(reader.ReadMessage());
                    break;
                case TdsTokenType.EnvironmentChange:
                    var change = reader.ReadEnvironmentChange();
                    environmentHandler?.Invoke(change);
                    break;
                case TdsTokenType.ReturnStatus:
                    reader.SkipReturnStatus();
                    break;
                case TdsTokenType.ReturnValue:
                    var returnValue = reader.ReadReturnValue();
                    returnValueHandler?.Invoke(returnValue);
                    break;
                case TdsTokenType.Order:
                case TdsTokenType.TableName:
                case TdsTokenType.ColumnInfo:
                case TdsTokenType.Sspi:
                    reader.SkipUShortLengthToken();
                    break;
                case TdsTokenType.SessionState:
                case TdsTokenType.FedAuthInfo:
                    reader.SkipUIntLengthToken();
                    break;
                case TdsTokenType.FeatureExtAck:
                    reader.SkipFeatureExtAck();
                    break;
                case TdsTokenType.LoginAck:
                    _ = reader.ReadLoginAck();
                    break;
                default:
                    throw new NotSupportedException(
                      $"SQL Server response token 0x{token:X2} is not supported.");
            }
        }

        return new TdsQueryResponse(
          BuildResultChain(_results),
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
            _columns = Array.Empty<TdsColumn>();
        }
        else if (hasCount || (_results.Count == 0 && (done.Status & TdsDoneStatus.More) == 0))
        {
            ResultBuilder result = new(_decoder, Array.Empty<TdsColumn>());
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

    private static MsSqlException? BuildError(IReadOnlyList<MsSqlInfo> errors)
    {
        if (errors.Count == 0)
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

    private static SqlRowSet BuildResultChain(IReadOnlyList<ResultBuilder> builders)
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
        private readonly IReadOnlyList<SqlColumn> _columns;
        private long _affectedRows;
        private string _commandTag = string.Empty;

        internal ResultBuilder(
            MsSqlRowDecoder decoder,
            IReadOnlyList<TdsColumn> columns)
        {
            _rows = new SqlRowPageCollectionBuilder(decoder);
            _columns = columns.Select(static column => column.Column).ToArray();
        }

        internal void AddRow(ReadOnlySpan<byte> row) => _rows.Add(row);

        internal void Complete(long affectedRows, string commandTag)
        {
            _affectedRows = affectedRows;
            _commandTag = commandTag;
        }

        internal SqlRowSet Build(SqlRowSet? next) =>
          new(
            _columns,
            _rows.Build(_columns),
            _affectedRows,
            _commandTag,
            next);
    }
}

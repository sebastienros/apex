using System.Collections;
using Apex.SqlClient;

namespace Apex.PgClient;

public sealed class PgBatch : IReadOnlyList<PgBatchCommand>
{
    private readonly List<PgBatchCommand> _commands = [];

    public int Count => _commands.Count;

    public PgBatchCommand this[int index] => _commands[index];

    public PgBatchCommand Add(string sql, PgParameters parameters = default)
    {
        PgBatchCommand command = new(sql, parameters);
        _commands.Add(command);
        return command;
    }

    public IEnumerator<PgBatchCommand> GetEnumerator() => _commands.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class PgBatchCommand
{
    public PgBatchCommand(string sql, PgParameters parameters = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        Sql = sql;
        Parameters = parameters;
    }

    public string Sql { get; }

    public PgParameters Parameters { get; }
}

public sealed class PgBatchReader : IReadOnlyList<SqlRowSet>
{
    private readonly SqlRowSet[] _results;
    private int _position;

    internal PgBatchReader(SqlRowSet first)
    {
        List<SqlRowSet> results = [];
        for (SqlRowSet? result = first;
             result is not null && !ReferenceEquals(result, SqlRowSet.Empty);
             result = result.Next)
        {
            results.Add(result);
        }

        _results = results.ToArray();
        _position = _results.Length == 0 ? -1 : 0;
    }

    public int Count => _results.Length;

    public SqlRowSet this[int index] => _results[index];

    public SqlRowSet Current =>
        _position >= 0 && _position < _results.Length
          ? _results[_position]
          : throw new InvalidOperationException("The batch has no current result.");

    public ValueTask<bool> NextResultAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_position < 0 || _position + 1 >= _results.Length)
        {
            _position = _results.Length;
            return ValueTask.FromResult(false);
        }

        _position++;
        return ValueTask.FromResult(true);
    }

    public IEnumerator<SqlRowSet> GetEnumerator() =>
        ((IEnumerable<SqlRowSet>)_results).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _results.GetEnumerator();
}

public sealed class PgBatchException : Exception
{
    internal PgBatchException(int commandIndex, PgException serverError)
        : base($"PostgreSQL batch command {commandIndex} failed: {serverError.Message}", serverError)
    {
        CommandIndex = commandIndex;
        ServerError = serverError;
    }

    public int CommandIndex { get; }

    public PgException ServerError { get; }
}

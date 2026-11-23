using Apex.SqlClient;

namespace Apex.PgClient;

public sealed class PgTransaction : ISqlTransaction
{
    private readonly PgConnection _connection;

    internal PgTransaction(PgConnection connection)
    {
        _connection = connection;
    }

    public bool IsCompleted { get; private set; }

    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        try
        {
            await _connection.ExecuteTransactionControlAsync("COMMIT", cancellationToken).ConfigureAwait(false);
            IsCompleted = true;
        }
        catch (PgException) when (_connection.IsReadyForPool)
        {
            IsCompleted = true;
            throw;
        }
    }

    // A transaction that hit an error answers COMMIT with a ROLLBACK tag and no error. That is fine
    // for an explicit commit, but an ambient transaction must learn that PostgreSQL rolled back.
    internal async ValueTask CommitAmbientAsync()
    {
        if (_connection.IsTransactionFailed)
        {
            await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw new System.Transactions.TransactionException(
                "The PostgreSQL transaction failed before COMMIT and was rolled back.");
        }

        await CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (IsCompleted)
        {
            return;
        }

        await _connection.ExecuteTransactionControlAsync("ROLLBACK", cancellationToken).ConfigureAwait(false);
        IsCompleted = true;
    }

    public ValueTask CreateSavepointAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        return _connection.ExecuteTransactionControlAsync(
            "SAVEPOINT " + QuoteIdentifier(name),
            cancellationToken);
    }

    public ValueTask RollbackToSavepointAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        return _connection.ExecuteTransactionControlAsync(
            "ROLLBACK TO SAVEPOINT " + QuoteIdentifier(name),
            cancellationToken);
    }

    public ValueTask ReleaseSavepointAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        return _connection.ExecuteTransactionControlAsync(
            "RELEASE SAVEPOINT " + QuoteIdentifier(name),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsCompleted)
        {
            await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void ThrowIfCompleted()
    {
        if (IsCompleted)
        {
            throw new InvalidOperationException("The transaction has already completed.");
        }
    }

    private static string QuoteIdentifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (System.Text.Encoding.UTF8.GetByteCount(name) > 63)
        {
            throw new ArgumentException(
                "PostgreSQL identifiers cannot exceed 63 UTF-8 bytes.",
                nameof(name));
        }

        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

using Apex.SqlClient;

namespace Apex.MsSqlClient;

internal sealed class MsSqlTransaction : ISqlTransaction
{
    private readonly MsSqlConnection _connection;

    internal MsSqlTransaction(MsSqlConnection connection)
    {
        _connection = connection;
    }

    public bool IsCompleted { get; private set; }

    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        if (!_connection.HasActiveTransaction)
        {
            IsCompleted = true;
            throw new InvalidOperationException(
              "The transaction has already been rolled back by SQL Server.");
        }

        await _connection.ExecuteTransactionControlAsync(
          "COMMIT TRANSACTION",
          cancellationToken).ConfigureAwait(false);
        IsCompleted = true;
    }

    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (IsCompleted)
        {
            return;
        }

        // Nothing to roll back when the server already ended the transaction, and nothing can be
        // sent on a broken connection (the server rolls back when the session closes). Sending
        // ROLLBACK anyway fails with error 3903 and masks the error that ended the transaction.
        if (!_connection.HasActiveTransaction || !_connection.IsUsable)
        {
            IsCompleted = true;
            return;
        }

        await _connection.ExecuteTransactionControlAsync(
          "ROLLBACK TRANSACTION",
          cancellationToken).ConfigureAwait(false);
        IsCompleted = true;
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
}

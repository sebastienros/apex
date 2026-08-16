using Apex.SqlClient;

namespace Apex.MySqlClient;

/// <summary>
/// A MySQL transaction. Cancellation is only honoured before the commit or rollback reaches the
/// server because abandoning it would leave the session in an unknown state.
/// </summary>
internal sealed class MySqlTransaction : ISqlTransaction
{
    private readonly MySqlConnection _connection;

    internal MySqlTransaction(MySqlConnection connection)
    {
        _connection = connection;
    }

    public bool IsCompleted { get; private set; }

    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        await _connection.ExecuteTransactionControlAsync("COMMIT", cancellationToken)
          .ConfigureAwait(false);
        IsCompleted = true;
    }

    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (IsCompleted)
        {
            return;
        }

        await _connection.ExecuteTransactionControlAsync("ROLLBACK", cancellationToken)
          .ConfigureAwait(false);
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

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

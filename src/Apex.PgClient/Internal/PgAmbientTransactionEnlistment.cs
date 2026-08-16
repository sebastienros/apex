using System.Transactions;

namespace Apex.PgClient.Internal;

internal sealed class PgAmbientTransactionEnlistment(
    PgTransaction transaction) : IEnlistmentNotification
{
    public void Prepare(PreparingEnlistment preparingEnlistment)
    {
        ArgumentNullException.ThrowIfNull(preparingEnlistment);
        preparingEnlistment.Prepared();
    }

    public void Commit(Enlistment enlistment)
    {
        ArgumentNullException.ThrowIfNull(enlistment);
        try
        {
            transaction.CommitAsync(CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            enlistment.Done();
        }
    }

    public void Rollback(Enlistment enlistment)
    {
        ArgumentNullException.ThrowIfNull(enlistment);
        try
        {
            transaction.RollbackAsync(CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            enlistment.Done();
        }
    }

    public void InDoubt(Enlistment enlistment)
    {
        ArgumentNullException.ThrowIfNull(enlistment);
        try
        {
            transaction.RollbackAsync(CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            enlistment.Done();
        }
    }
}

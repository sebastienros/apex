using System.Transactions;

namespace Apex.PgClient.Internal;

/// <summary>
/// Volatile enlistment of a PostgreSQL transaction in an ambient <see cref="Transaction"/>.
/// PostgreSQL's COMMIT can still fail (deferred constraints, serialization failures), so it must
/// run while the outcome can still be changed: in <see cref="SinglePhaseCommit"/> when this is
/// the only enlistment, otherwise in <see cref="Prepare"/>.
/// </summary>
internal sealed class PgAmbientTransactionEnlistment(
    PgTransaction transaction) : ISinglePhaseNotification
{
    private bool _committedInPrepare;

    public void SinglePhaseCommit(SinglePhaseEnlistment singlePhaseEnlistment)
    {
        ArgumentNullException.ThrowIfNull(singlePhaseEnlistment);
        var error = TryCommit(out var inDoubt);
        if (error is null)
        {
            singlePhaseEnlistment.Committed();
        }
        else if (inDoubt)
        {
            singlePhaseEnlistment.InDoubt(error);
        }
        else
        {
            singlePhaseEnlistment.Aborted(error);
        }
    }

    // Other resources share the transaction and PostgreSQL takes no part in two-phase commit
    // here, so COMMIT runs now: a failure aborts the whole transaction before anyone commits.
    // The limit is the reverse case: if a resource prepared after this one votes to abort,
    // PostgreSQL has already committed and cannot roll back.
    public void Prepare(PreparingEnlistment preparingEnlistment)
    {
        ArgumentNullException.ThrowIfNull(preparingEnlistment);
        var error = TryCommit(out _);
        if (error is null)
        {
            _committedInPrepare = true;
            preparingEnlistment.Prepared();
        }
        else
        {
            preparingEnlistment.ForceRollback(error);
        }
    }

    public void Commit(Enlistment enlistment)
    {
        ArgumentNullException.ThrowIfNull(enlistment);
        enlistment.Done();
    }

    public void Rollback(Enlistment enlistment)
    {
        ArgumentNullException.ThrowIfNull(enlistment);
        try
        {
            if (!_committedInPrepare)
            {
                transaction.RollbackAsync(CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
            }
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
            if (!_committedInPrepare)
            {
                transaction.RollbackAsync(CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            enlistment.Done();
        }
    }

    private Exception? TryCommit(out bool inDoubt)
    {
        inDoubt = false;
        try
        {
            transaction.CommitAmbientAsync().AsTask().GetAwaiter().GetResult();
            return null;
        }
        catch (Exception exception)
        {
            // The transaction is completed only when the server answered and the session is idle
            // again, which means it rolled back. Otherwise (connection lost) nobody knows.
            inDoubt = !transaction.IsCompleted;
            return exception;
        }
    }
}

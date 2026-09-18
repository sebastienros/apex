using Apex.SqlClient.Internal;

namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class SqlResultReaderStateTests
{
    [TestMethod]
    public async Task InitializationConsumesExactlyOneRowNotification()
    {
        var state = new SqlResultReaderState();
        var initialization = state.Initialize();
        state.Start();

        Assert.IsTrue(state.PublishRow());
        Assert.IsTrue(await initialization);
        Assert.IsFalse(state.PublishRow());
        Assert.IsTrue(state.End(stopped: false));
        Assert.IsTrue(state.Ended);
        Assert.IsFalse(state.Active);
    }

    [TestMethod]
    public async Task EmptyResultsAndTransitionsPreserveTheirBoundaries()
    {
        var state = new SqlResultReaderState();
        var initialization = state.Initialize();
        state.Start();
        Assert.IsTrue(state.End(stopped: false));
        Assert.IsFalse(await initialization);

        var next = state.NextResult();
        Assert.IsFalse(next.IsCompleted);
        state.Start();
        Assert.IsTrue(state.End(stopped: false));
        Assert.IsTrue(await next);

        var completion = state.NextResult();
        state.Complete(null);
        Assert.IsFalse(await completion);
    }

    [TestMethod]
    public async Task CompletionPropagatesErrorsToPendingConsumers()
    {
        var state = new SqlResultReaderState();
        var initialization = state.Initialize();
        var next = state.NextResult();
        var error = new SqlClientException("wire failure");

        state.Complete(error);

        Assert.AreSame(error, await Assert.ThrowsExactlyAsync<SqlClientException>(() => initialization));
        Assert.AreSame(error, await Assert.ThrowsExactlyAsync<SqlClientException>(() => next));
        Assert.IsFalse(state.End(stopped: true));
    }

    [TestMethod]
    public void CountsDoNotWrapOrBecomeKnownAgainAfterOverflow()
    {
        var state = new SqlResultReaderState();
        Assert.AreEqual(-1, state.RecordsAffected);
        state.AddRecordsAffected(0);
        Assert.AreEqual(0, state.RecordsAffected);
        state.AddRecordsAffected(7);
        Assert.AreEqual(7, state.RecordsAffected);
        state.AddRecordsAffected(long.MaxValue);
        state.AddRecordsAffected(1);
        Assert.AreEqual(-1, state.RecordsAffected);
    }

    [TestMethod]
    public void PublishingOrdinaryRowsDoesNotAllocate()
    {
        var state = new SqlResultReaderState();
        state.Start();
        for (var i = 0; i < 100; i++) state.PublishRow();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) state.PublishRow();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(0L, allocated);
    }
}

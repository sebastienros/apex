using System.Collections.Concurrent;
using Apex.SqlClient.Internal;

namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class CoreReviewRegressionTests
{
    [TestMethod]
    public async Task SchedulerFlushBatchDoesNotReceiveBeforeFlushWhenFollowedByNonFlushOrCanceled()
    {
        ConcurrentQueue<string> events = [];
        var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using BoundedOrderedCommandScheduler scheduler = new(
            4,
            8,
            flushBatchAsync: _ =>
            {
                events.Enqueue("flush");
                flushStarted.TrySetResult();
                return ValueTask.CompletedTask;
            });

        var releasePump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pumpHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.ExecuteAsync(
            async ct =>
            {
                pumpHeld.SetResult();
                await releasePump.Task.WaitAsync(ct);
            },
            _ => ValueTask.FromResult(0),
            barrier: true);
        await pumpHeld.Task;

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var first = scheduler.ExecuteAsync(
            _ =>
            {
                events.Enqueue("send-1");
                return ValueTask.CompletedTask;
            },
            _ =>
            {
                events.Enqueue("receive-1");
                return ValueTask.FromResult(1);
            },
            flushBatch: true);

        var second = scheduler.ExecuteAsync(
            _ =>
            {
                events.Enqueue("send-2");
                return ValueTask.CompletedTask;
            },
            _ =>
            {
                events.Enqueue("receive-2");
                return ValueTask.FromResult(2);
            },
            cancellationToken: canceled.Token,
            flushBatch: false);

        var third = scheduler.ExecuteAsync(
            _ =>
            {
                events.Enqueue("send-3");
                return ValueTask.CompletedTask;
            },
            _ =>
            {
                events.Enqueue("receive-3");
                return ValueTask.FromResult(3);
            },
            flushBatch: false);

        releasePump.SetResult();
        await blocker;

        Assert.AreEqual(1, await first);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => _ = await second);
        Assert.AreEqual(3, await third);

        var list = events.ToArray();
        var flushIndex = Array.IndexOf(list, "flush");
        var receive1Index = Array.IndexOf(list, "receive-1");
        Assert.IsTrue(flushIndex >= 0 && receive1Index > flushIndex,
            $"Expected flush before receive-1, got: {string.Join(", ", list)}");
    }

    [TestMethod]
    public async Task SchedulerFlushBatchFollowedByFatalSendErrorDoesNotDeadlockOrReceiveUnflushed()
    {
        var receive1Called = 0;
        var flushCalled = 0;
        var scheduler = new BoundedOrderedCommandScheduler(
            4,
            8,
            flushBatchAsync: _ =>
            {
                Interlocked.Increment(ref flushCalled);
                return ValueTask.CompletedTask;
            },
            isFatal: static ex => ex is IOException);

        var releasePump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pumpHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.ExecuteAsync(
            async ct =>
            {
                pumpHeld.SetResult();
                await releasePump.Task.WaitAsync(ct);
            },
            _ => ValueTask.FromResult(0),
            barrier: true);
        await pumpHeld.Task;

        var first = scheduler.ExecuteAsync(
            _ => ValueTask.CompletedTask,
            _ =>
            {
                Interlocked.Increment(ref receive1Called);
                return ValueTask.FromResult(1);
            },
            flushBatch: true);

        var second = scheduler.ExecuteAsync(
            _ => ValueTask.FromException(new IOException("write failed")),
            _ => ValueTask.FromResult(2),
            flushBatch: true);

        var third = scheduler.ExecuteAsync(
            _ => ValueTask.CompletedTask,
            _ => ValueTask.FromResult(3),
            flushBatch: true);

        releasePump.SetResult();
        await blocker;

        await Assert.ThrowsExactlyAsync<IOException>(async () => _ = await first).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<IOException>(async () => _ = await second).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<IOException>(async () => _ = await third).WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, Volatile.Read(ref flushCalled));
        Assert.AreEqual(0, Volatile.Read(ref receive1Called));
    }

    [TestMethod]
    public async Task SchedulerSynchronousFlushFailureDoesNotReceiveUnflushedCommands()
    {
        var receiveCount = 0;
        var scheduler = new BoundedOrderedCommandScheduler(
            4,
            8,
            flushBatchAsync: _ => throw new IOException("sync flush failed"),
            isFatal: static ex => ex is IOException);

        var releasePump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pumpHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.ExecuteAsync(
            async ct =>
            {
                pumpHeld.SetResult();
                await releasePump.Task.WaitAsync(ct);
            },
            _ => ValueTask.FromResult(0),
            barrier: true);
        await pumpHeld.Task;

        var first = scheduler.ExecuteAsync(
            _ => ValueTask.CompletedTask,
            _ =>
            {
                Interlocked.Increment(ref receiveCount);
                return ValueTask.FromResult(1);
            },
            flushBatch: true);

        var second = scheduler.ExecuteAsync(
            _ => ValueTask.CompletedTask,
            _ =>
            {
                Interlocked.Increment(ref receiveCount);
                return ValueTask.FromResult(2);
            },
            flushBatch: true);

        releasePump.SetResult();
        await blocker;

        await Assert.ThrowsExactlyAsync<IOException>(async () => _ = await first).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<IOException>(async () => _ = await second).WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, Volatile.Read(ref receiveCount));
    }

    [TestMethod]
    public async Task PoolEvictionDisposeErrorDoesNotFailNewAcquisition()
    {
        var created = 0;
        var firstReusable = true;
        await using var pool = new SqlConnectionPool<FakePoolConnection>(
            new SqlPoolOptions { MaximumSize = 1 },
            _ =>
            {
                var id = Interlocked.Increment(ref created);
                return ValueTask.FromResult(new FakePoolConnection(id, throwOnDispose: id == 1));
            },
            conn => conn.Id != 1 || firstReusable);

        await using (var first = await pool.GetConnectionAsync())
        {
            Assert.IsNotNull(first);
        }

        // First connection is now idle, mark it unusable so RentAsync evicts and disposes it.
        firstReusable = false;

        await using var second = await pool.GetConnectionAsync();
        Assert.IsNotNull(second);
        Assert.AreEqual(2, created);
    }

    [TestMethod]
    public async Task PoolReturnReleasesSlotEvenWhenReusePredicateThrows()
    {
        var throwInPredicate = false;
        var created = 0;
        await using var pool = new SqlConnectionPool<FakePoolConnection>(
            new SqlPoolOptions { MaximumSize = 1 },
            _ => ValueTask.FromResult(new FakePoolConnection(Interlocked.Increment(ref created), false)),
            _ =>
            {
                if (throwInPredicate)
                {
                    throw new InvalidOperationException("predicate failed");
                }

                return true;
            });

        var lease = await pool.GetConnectionAsync();
        throwInPredicate = true;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await lease.DisposeAsync());

        // Slot must still have been released.
        throwInPredicate = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var next = await pool.GetConnectionAsync(cts.Token);
        Assert.IsNotNull(next);
    }

    [TestMethod]
    public void RowPageCollectionBuilderReleaseIsIdempotentBeforeAndAfterBuild()
    {
        var builder1 = new SqlRowPageCollectionBuilder(new TestRowDecoder());
        builder1.Add([1, 2, 3, 4]);
        builder1.Release();
        builder1.Release();

        var builder2 = new SqlRowPageCollectionBuilder(new TestRowDecoder());
        builder2.Add([1, 2, 3, 4]);
        var rows = builder2.Build([new SqlColumn("c", 1, 4, -1, SqlDataFormat.Binary)]);
        Assert.HasCount(1, rows);
        builder2.Release();
    }

    private sealed class FakePoolConnection(int id, bool throwOnDispose) : ISqlConnection
    {
        public int Id { get; } = id;
        public bool IsSecure => false;
        public DatabaseMetadata DatabaseMetadata => new("test", "1.0", 1, 0);
        public ValueTask<SqlRowSet> QueryAsync(string sql, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SqlRowSet> QueryAsync(string sql, SqlParameters parameters, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SqlCommandResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SqlCommandResult> ExecuteAsync(string sql, SqlParameters parameters, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<SqlCommandResult>> ExecuteBatchAsync(string sql, IReadOnlyList<SqlParameters> batch, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<SqlRow> StreamAsync(string sql, int pageSize = 128, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<SqlRow> StreamAsync(string sql, SqlParameters parameters, int pageSize = 128, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ISqlRowReader> ExecuteReaderAsync(string sql, SqlParameters parameters = default, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ISqlPreparedStatement> PrepareAsync(string sql, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ISqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() =>
            throwOnDispose
                ? ValueTask.FromException(new IOException("dispose failed"))
                : ValueTask.CompletedTask;
    }
}

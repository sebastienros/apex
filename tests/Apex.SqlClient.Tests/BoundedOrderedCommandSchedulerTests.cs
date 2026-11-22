using System.Collections.Concurrent;
using Apex.SqlClient.Internal;

namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class BoundedOrderedCommandSchedulerTests
{
    [TestMethod]
    public void RejectsNonpositiveLimits()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BoundedOrderedCommandScheduler(0, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BoundedOrderedCommandScheduler(1, 0));
    }

    [TestMethod]
    public async Task PipelinesOnlyTheConfiguredNumberInSubmissionOrder()
    {
        await using BoundedOrderedCommandScheduler scheduler = new(3, 4);
        ConcurrentQueue<string> events = [];
        (var blocker, var releasePump) = await HoldPumpAsync(scheduler);

        var first = Execute(scheduler, 1, events: events);
        var second = Execute(scheduler, 2, events: events);
        var third = Execute(scheduler, 3, events: events);
        var fourth = Execute(scheduler, 4, events: events);

        releasePump.SetResult();
        await blocker;

        CollectionAssert.AreEqual(
          new[] { 1, 2, 3, 4 },
          await Task.WhenAll(first.AsTask(), second.AsTask(), third.AsTask(), fourth.AsTask()));
        AssertEventOrder(events, "send-", "send-1", "send-2", "send-3", "send-4");
        AssertEventOrder(
          events,
          "receive-",
          "receive-1",
          "receive-2",
          "receive-3",
          "receive-4");
        Assert.IsLessThan(
          EventIndex(events, "send-4"),
          EventIndex(events, "receive-3"));
    }

    [TestMethod]
    public async Task FlushesOnceAfterEachAdmittedGroup()
    {
        ConcurrentQueue<string> events = [];
        await using BoundedOrderedCommandScheduler scheduler = new(
          3,
          4,
          flushBatchAsync: _ =>
          {
              events.Enqueue("flush");
              return ValueTask.CompletedTask;
          });
        (var blocker, var releasePump) = await HoldPumpAsync(scheduler);

        var first = Execute(scheduler, 1, events: events, flushBatch: true);
        var second = Execute(scheduler, 2, events: events, flushBatch: true);
        var third = Execute(scheduler, 3, events: events, flushBatch: true);
        var fourth = Execute(scheduler, 4, events: events, flushBatch: true);

        releasePump.SetResult();
        await blocker;
        await Task.WhenAll(first.AsTask(), second.AsTask(), third.AsTask(), fourth.AsTask());

        CollectionAssert.AreEqual(
          new[]
          {
            "send-1",
            "send-2",
            "send-3",
            "flush",
            "receive-1",
            "receive-2",
            "receive-3",
            "send-4",
            "flush",
            "receive-4",
          },
          events.ToArray());
    }

    [TestMethod]
    public async Task ReceivesAndCompletesResultsInSubmissionOrder()
    {
        await using BoundedOrderedCommandScheduler scheduler = new(3, 3);
        List<int> receiveOrder = [];
        var firstReceiveStarted = NewGate();
        var releaseFirstReceive = NewGate();
        var releaseSecondReceive = NewGate();
        var releaseThirdReceive = NewGate();
        (var blocker, var releasePump) = await HoldPumpAsync(scheduler);

        var first = scheduler.ExecuteAsync(
          _ => ValueTask.CompletedTask,
          async cancellationToken =>
          {
              receiveOrder.Add(1);
              firstReceiveStarted.SetResult();
              await releaseFirstReceive.Task.WaitAsync(cancellationToken);
              return 10;
          });
        var second = scheduler.ExecuteAsync(
          _ => ValueTask.CompletedTask,
          async cancellationToken =>
          {
              receiveOrder.Add(2);
              await releaseSecondReceive.Task.WaitAsync(cancellationToken);
              return 20;
          });
        var third = scheduler.ExecuteAsync(
          _ => ValueTask.CompletedTask,
          async cancellationToken =>
          {
              receiveOrder.Add(3);
              await releaseThirdReceive.Task.WaitAsync(cancellationToken);
              return 30;
          });

        releaseSecondReceive.SetResult();
        releaseThirdReceive.SetResult();
        releasePump.SetResult();
        await blocker;
        await firstReceiveStarted.Task;
        Assert.AreEqual(1, receiveOrder.Count);
        Assert.IsFalse(second.IsCompleted);
        Assert.IsFalse(third.IsCompleted);

        releaseFirstReceive.SetResult();

        CollectionAssert.AreEqual(
          new[] { 10, 20, 30 },
          await Task.WhenAll(first.AsTask(), second.AsTask(), third.AsTask()));
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, receiveOrder);
    }

    [TestMethod]
    public async Task ReceivesEarlierResponseWhileLaterCommandIsSending()
    {
        await using BoundedOrderedCommandScheduler scheduler = new(2, 2);
        var firstReceiveStarted = NewGate();
        var releaseSecondSend = NewGate();
        (var blocker, var releasePump) = await HoldPumpAsync(scheduler);

        var first = scheduler.ExecuteAsync(
          static _ => ValueTask.CompletedTask,
          _ =>
          {
              firstReceiveStarted.SetResult();
              return ValueTask.FromResult(1);
          });
        var second = scheduler.ExecuteAsync(
          async cancellationToken =>
          {
              await firstReceiveStarted.Task.WaitAsync(cancellationToken);
              await releaseSecondSend.Task.WaitAsync(cancellationToken);
          },
          static _ => ValueTask.FromResult(2));

        releasePump.SetResult();
        await blocker;
        await firstReceiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseSecondSend.SetResult();

        CollectionAssert.AreEqual(
          new[] { 1, 2 },
          await Task.WhenAll(first.AsTask(), second.AsTask())
            .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task ReceivesWhileSharedFlushIsInProgress()
    {
        var flushStarted = NewGate();
        var releaseFlush = NewGate();
        var receiveStarted = NewGate();
        await using BoundedOrderedCommandScheduler scheduler = new(
          2,
          2,
          flushBatchAsync: async cancellationToken =>
          {
              flushStarted.SetResult();
              await releaseFlush.Task.WaitAsync(cancellationToken);
          });
        (var blocker, var releasePump) = await HoldPumpAsync(scheduler);

        var first = scheduler.ExecuteAsync(
          static _ => ValueTask.CompletedTask,
          _ =>
          {
              receiveStarted.SetResult();
              return ValueTask.FromResult(1);
          },
          flushBatch: true);
        var second = Execute(scheduler, 2, flushBatch: true);

        releasePump.SetResult();
        await blocker;
        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await receiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseFlush.SetResult();

        CollectionAssert.AreEqual(
          new[] { 1, 2 },
          await Task.WhenAll(first.AsTask(), second.AsTask())
            .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task BarrierExecutesAloneBetweenBatches()
    {
        await using BoundedOrderedCommandScheduler scheduler = new(3, 5);
        ConcurrentQueue<string> events = [];
        (var blocker, var releasePump) = await HoldPumpAsync(scheduler);

        var first = Execute(scheduler, 1, events: events);
        var second = Execute(scheduler, 2, events: events);
        var barrier = Execute(scheduler, 3, events: events, barrier: true);
        var fourth = Execute(scheduler, 4, events: events);
        var fifth = Execute(scheduler, 5, events: events);

        releasePump.SetResult();
        await blocker;

        await Task.WhenAll(
          first.AsTask(),
          second.AsTask(),
          barrier.AsTask(),
          fourth.AsTask(),
          fifth.AsTask());
        AssertEventOrder(
          events,
          "send-",
          "send-1",
          "send-2",
          "send-3",
          "send-4",
          "send-5");
        AssertEventOrder(
          events,
          "receive-",
          "receive-1",
          "receive-2",
          "receive-3",
          "receive-4",
          "receive-5");
        Assert.IsLessThan(
          EventIndex(events, "send-3"),
          EventIndex(events, "receive-2"));
        Assert.IsLessThan(
          EventIndex(events, "send-4"),
          EventIndex(events, "receive-3"));
    }

    [TestMethod]
    public async Task CancellationBeforeSendSkipsTheCommand()
    {
        await using BoundedOrderedCommandScheduler scheduler = new(1, 3);
        ConcurrentQueue<string> events = [];
        using CancellationTokenSource cancellation = new();
        (var blocker, var releasePump) = await HoldPumpAsync(scheduler);

        var first = Execute(scheduler, 1, events: events);
        var canceled = Execute(
          scheduler,
          2,
          events: events,
          cancellationToken: cancellation.Token);
        var third = Execute(scheduler, 3, events: events);

        cancellation.Cancel();
        releasePump.SetResult();
        await blocker;

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
          async () =>
          {
              _ = await canceled;
          });
        CollectionAssert.AreEqual(
          new[] { 1, 3 },
          await Task.WhenAll(first.AsTask(), third.AsTask()));
        CollectionAssert.AreEqual(
          new[] { "send-1", "receive-1", "send-3", "receive-3" },
          events.ToArray());
    }

    [TestMethod]
    public async Task FatalErrorFailsQueuedCommandsAndStopsTheScheduler()
    {
        BoundedOrderedCommandScheduler scheduler = new(1, 3, exception => exception is FatalTestException);
        FatalTestException fatal = new();
        (var blocker, var releasePump) = await HoldPumpAsync(scheduler);

        var first = scheduler.ExecuteAsync(
          _ => ValueTask.CompletedTask,
          _ => ValueTask.FromException<int>(fatal));
        var second = Execute(scheduler, 2);
        var third = Execute(scheduler, 3);

        releasePump.SetResult();
        await blocker;

        Assert.AreSame(fatal, await AssertValueTaskThrowsExactlyAsync<FatalTestException, int>(first));
        Assert.AreSame(fatal, await AssertValueTaskThrowsExactlyAsync<FatalTestException, int>(second));
        Assert.AreSame(fatal, await AssertValueTaskThrowsExactlyAsync<FatalTestException, int>(third));
        Assert.AreSame(
          fatal,
          await AssertValueTaskThrowsExactlyAsync<FatalTestException, int>(
            scheduler.ExecuteAsync(_ => ValueTask.CompletedTask, _ => ValueTask.FromResult(4))));

        await scheduler.DisposeAsync();
    }

    [TestMethod]
    public async Task ExplicitFaultStopsFutureCommands()
    {
        BoundedOrderedCommandScheduler scheduler = new(1, 1);
        FatalTestException fatal = new();

        scheduler.Fault(fatal);

        Assert.IsTrue(scheduler.IsStopped);
        Assert.AreSame(
          fatal,
          await AssertValueTaskThrowsExactlyAsync<FatalTestException, int>(
            scheduler.ExecuteAsync(
              static _ => ValueTask.CompletedTask,
              static _ => ValueTask.FromResult(1))));
        await scheduler.DisposeAsync();
    }

    [TestMethod]
    public async Task NonfatalErrorAllowsLaterCommandsToComplete()
    {
        await using BoundedOrderedCommandScheduler scheduler =
          new(2, 2, exception => exception is FatalTestException);
        ConcurrentQueue<string> events = [];
        InvalidOperationException nonfatal = new();
        (var blocker, var releasePump) = await HoldPumpAsync(scheduler);

        var first = scheduler.ExecuteAsync(
          _ =>
          {
              events.Enqueue("send-1");
              return ValueTask.CompletedTask;
          },
          _ =>
          {
              events.Enqueue("receive-1");
              return ValueTask.FromException<int>(nonfatal);
          });
        var second = Execute(scheduler, 2, events: events);

        releasePump.SetResult();
        await blocker;
        Assert.AreSame(
          nonfatal,
          await AssertValueTaskThrowsExactlyAsync<InvalidOperationException, int>(first));
        Assert.AreEqual(2, await second);
        AssertEventOrder(events, "send-", "send-1", "send-2");
        AssertEventOrder(events, "receive-", "receive-1", "receive-2");
    }

    [TestMethod]
    public async Task DisposalFailsInFlightAndQueuedCommandsDeterministically()
    {
        BoundedOrderedCommandScheduler scheduler = new(1, 2);
        var receiveStarted = NewGate();

        var first = scheduler.ExecuteAsync(
          _ => ValueTask.CompletedTask,
          async cancellationToken =>
          {
              receiveStarted.SetResult();
              await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
              return 1;
          });
        var second = Execute(scheduler, 2);

        await receiveStarted.Task;
        await scheduler.DisposeAsync();

        await AssertValueTaskThrowsExactlyAsync<ObjectDisposedException, int>(first);
        await AssertValueTaskThrowsExactlyAsync<ObjectDisposedException, int>(second);
        await AssertValueTaskThrowsExactlyAsync<ObjectDisposedException, int>(
          scheduler.ExecuteAsync(_ => ValueTask.CompletedTask, _ => ValueTask.FromResult(3)));
        await scheduler.DisposeAsync();
    }

    [TestMethod]
    public async Task ReusesCommandsAcrossSynchronousAsyncCanceledAndFaultedCompletions()
    {
        await using BoundedOrderedCommandScheduler scheduler = new(1, 1);

        for (var i = 0; i < 500; i++)
        {
            var command = i % 2 == 0
              ? scheduler.ExecuteAsync(
                static _ => ValueTask.CompletedTask,
                _ => ValueTask.FromResult(i))
              : scheduler.ExecuteAsync(
                static async _ => await Task.Yield(),
                async _ =>
                {
                    await Task.Yield();
                    return i;
                });

            Assert.AreEqual(i, await command);
        }

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await AssertValueTaskThrowsExactlyAsync<OperationCanceledException, int>(
          Execute(scheduler, 500, cancellationToken: cancellation.Token));

        InvalidOperationException failure = new();
        Assert.AreSame(
          failure,
          await AssertValueTaskThrowsExactlyAsync<InvalidOperationException, int>(
            scheduler.ExecuteAsync(
              static _ => ValueTask.CompletedTask,
              _ => ValueTask.FromException<int>(failure))));

        Assert.AreEqual(501, await Execute(scheduler, 501));
    }

    [TestMethod]
    public async Task ValueTaskCanOnlyBeConsumedOnceAndReuseRemainsValid()
    {
        await using BoundedOrderedCommandScheduler scheduler = new(1, 1);
        var first = Execute(scheduler, 1);

        Assert.AreEqual(1, await first);
        await AssertValueTaskThrowsExactlyAsync<InvalidOperationException, int>(first);

        Assert.AreEqual(2, await Execute(scheduler, 2));
    }

    [TestMethod]
    public async Task CompletionContinuationsRunAsynchronously()
    {
        await using BoundedOrderedCommandScheduler scheduler = new(2, 2);
        using ManualResetEventSlim releaseContinuation = new();
        var continuationStarted = NewGate();
        var first = Execute(scheduler, 1);
        var second = Execute(scheduler, 2);

        first.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(
          () =>
          {
              continuationStarted.SetResult();
              releaseContinuation.Wait();
          });

        Assert.AreEqual(2, await second.AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        await continuationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        releaseContinuation.Set();
        Assert.AreEqual(1, first.GetAwaiter().GetResult());
    }

    private static ValueTask<int> Execute(
        BoundedOrderedCommandScheduler scheduler,
        int value,
        Func<CancellationToken, ValueTask>? send = null,
        ConcurrentQueue<string>? events = null,
        bool barrier = false,
        CancellationToken cancellationToken = default,
        bool flushBatch = false) =>
      scheduler.ExecuteAsync(
        send
        ?? (_ =>
        {
            events?.Enqueue($"send-{value}");
            return ValueTask.CompletedTask;
        }),
        _ =>
        {
            events?.Enqueue($"receive-{value}");
            return ValueTask.FromResult(value);
        },
        barrier,
        cancellationToken,
        flushBatch);

    private static TaskCompletionSource NewGate() =>
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertEventOrder(
        ConcurrentQueue<string> events,
        string prefix,
        params string[] expected) =>
      CollectionAssert.AreEqual(
        expected,
        events.Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).ToArray());

    private static int EventIndex(ConcurrentQueue<string> events, string expected) =>
      Array.IndexOf(events.ToArray(), expected);

    private static async Task<(ValueTask<int> Command, TaskCompletionSource Release)> HoldPumpAsync(
        BoundedOrderedCommandScheduler scheduler)
    {
        var started = NewGate();
        var release = NewGate();
        var command = scheduler.ExecuteAsync(
          async cancellationToken =>
          {
              started.SetResult();
              await release.Task.WaitAsync(cancellationToken);
          },
          _ => ValueTask.FromResult(0),
          barrier: true);
        await started.Task;
        return (command, release);
    }

    private static async Task<TException> AssertValueTaskThrowsExactlyAsync<TException, TResult>(
        ValueTask<TResult> valueTask)
      where TException : Exception =>
      await Assert.ThrowsExactlyAsync<TException>(
        async () =>
        {
            _ = await valueTask;
        });

    private sealed class FatalTestException : Exception;
}

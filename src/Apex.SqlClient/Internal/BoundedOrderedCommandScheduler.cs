using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Threading.Tasks.Sources;

namespace Apex.SqlClient.Internal;

/// <summary>
/// Runs commands on one connection in submission order, sending up to an in-flight limit ahead of
/// their responses.
/// </summary>
/// <remarks>
/// <para>
/// Shutdown is bounded. A connection closes with <see cref="CloseAsync"/>, which queues the
/// protocol's goodbye after the work already queued and waits for it at most
/// <see cref="DefaultCloseTimeout"/>. Past that deadline the scheduler is disposed.
/// <see cref="DisposeAsync"/> cancels the send in progress, and when the pump is still busy after
/// <see cref="DefaultAbortGracePeriod"/> (a receive, or a send that ignores its token, for example
/// because the peer stopped reading), it aborts the transport through the callback the connection
/// supplied, which makes the pending I/O fail. Once the pump unwinds, the commands that have not
/// run (the goodbye among them) fail with <see cref="ObjectDisposedException"/>. A connection close
/// therefore takes at most about the close timeout plus the abort grace period.
/// </para>
/// <para>
/// Disposal still waits for the pump to unwind before it returns, and does not abandon it: the
/// connections release pooled buffers right after the scheduler is disposed, so a delegate must
/// never run past that point. Without an abort callback, a pump stuck in I/O that ignores its token
/// still keeps disposal waiting.
/// </para>
/// </remarks>
internal sealed class BoundedOrderedCommandScheduler : IAsyncDisposable
{
    // How long CloseAsync waits for the commands queued before it and for the goodbye.
    internal static readonly TimeSpan DefaultCloseTimeout = TimeSpan.FromSeconds(5);

    // How long DisposeAsync lets the pump unwind on its own after shutdown, before it aborts the
    // transport. A healthy pump unwinds in microseconds; this only expires when it is stuck in I/O.
    internal static readonly TimeSpan DefaultAbortGracePeriod = TimeSpan.FromSeconds(1);

    private static readonly Func<CancellationToken, ValueTask<bool>> s_goodbyeReceived =
      static _ => ValueTask.FromResult(true);

    private readonly Channel<ICommand> _commands;
    private readonly int _inFlightLimit;
    private readonly Func<Exception, bool> _isFatal;
    private readonly Func<CancellationToken, ValueTask>? _flushBatchAsync;
    private readonly CancellationTokenSource _shutdown = new();

    // Batch entries are reused by the pump, which is their only user: an entry goes back here once
    // its batch (or its cancel skip) is finished.
    private readonly Stack<BatchEntry> _freeEntries = new();

    // In a multi-command batch, the receiver handles entry i once i + 1 entries are ready (the
    // count semantics of the semaphore this replaces). Reused across batches: the count is reset
    // when a batch starts, after the previous batch's sender and receiver have both finished, and a
    // stale signal left in the event only makes the receiver re-check the count.
    private readonly AsyncAutoResetEvent _ready = new();
    private int _readyCount;

    // The tokens a cancellable command's send and receive run with, each cancelled by the
    // command's own token or by shutdown. Sends run one at a time on the pump, and so do receives,
    // so each side reuses one source instead of linking a new one per command.
    private readonly ReusableLinkedSource _sendCancellation;
    private readonly ReusableLinkedSource _receiveCancellation;
    private readonly Action? _abortTransport;
    private readonly TimeSpan _closeTimeout;
    private readonly TimeSpan _abortGracePeriod;
    private readonly Task _pump;
    private Exception? _terminalError;
    private int _disposed;
    private int _shutdownDisposed;

    public BoundedOrderedCommandScheduler(
        int inFlightLimit,
        int queueCapacity,
        Func<Exception, bool>? isFatal = null,
        Func<CancellationToken, ValueTask>? flushBatchAsync = null,
        Action? abortTransport = null,
        TimeSpan? closeTimeout = null,
        TimeSpan? abortGracePeriod = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inFlightLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);

        _abortTransport = abortTransport;
        _closeTimeout = closeTimeout ?? DefaultCloseTimeout;
        _abortGracePeriod = abortGracePeriod ?? DefaultAbortGracePeriod;
        _inFlightLimit = inFlightLimit;
        _isFatal = isFatal ?? (_ => false);
        _flushBatchAsync = flushBatchAsync;
        _commands = Channel.CreateBounded<ICommand>(
          new BoundedChannelOptions(queueCapacity)
          {
              AllowSynchronousContinuations = false,
              FullMode = BoundedChannelFullMode.Wait,
              SingleReader = true,
              SingleWriter = false,
          });
        _sendCancellation = new ReusableLinkedSource(_shutdown);
        _receiveCancellation = new ReusableLinkedSource(_shutdown);
        _shutdown.Token.UnsafeRegister(
          static state =>
          {
              var scheduler = (BoundedOrderedCommandScheduler)state!;
              scheduler._sendCancellation.Cancel();
              scheduler._receiveCancellation.Cancel();
          },
          this);
        _pump = PumpAsync();
    }

    public bool IsStopped =>
      Volatile.Read(ref _disposed) != 0 ||
      Volatile.Read(ref _terminalError) is not null;

    public void Fault(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Stop(exception);
    }

    public ValueTask<T> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask> sendAsync,
        Func<CancellationToken, ValueTask<T>> receiveAsync,
        bool barrier = false,
        CancellationToken cancellationToken = default,
        bool flushBatch = false)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);
        ArgumentNullException.ThrowIfNull(receiveAsync);
        return ExecuteAsync(
          new DelegatePair<T>(sendAsync, receiveAsync),
          DelegatePair<T>.Send,
          DelegatePair<T>.Receive,
          barrier,
          cancellationToken,
          flushBatch);
    }

    /// <summary>
    /// Like the delegate-only overload, but the delegates receive <paramref name="state"/>, which
    /// the pooled command stores by value. With static lambdas a call site allocates no closure.
    /// </summary>
    public ValueTask<T> ExecuteAsync<TState, T>(
        TState state,
        Func<TState, CancellationToken, ValueTask> sendAsync,
        Func<TState, CancellationToken, ValueTask<T>> receiveAsync,
        bool barrier = false,
        CancellationToken cancellationToken = default,
        bool flushBatch = false)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);
        ArgumentNullException.ThrowIfNull(receiveAsync);

        if (Volatile.Read(ref _disposed) != 0)
        {
            return ValueTask.FromException<T>(
              new ObjectDisposedException(nameof(BoundedOrderedCommandScheduler)));
        }

        var terminalError = Volatile.Read(ref _terminalError);
        if (terminalError is not null)
        {
            return ValueTask.FromException<T>(terminalError);
        }

        var command = Command<TState, T>.Rent(
          this,
          state,
          sendAsync,
          receiveAsync,
          barrier,
          cancellationToken,
          flushBatch);
        var completion = command.Completion;
        command.Enqueue(_commands.Writer);
        return completion;
    }

    /// <summary>
    /// Sends the protocol's goodbye as a barrier after the commands already queued, waiting at
    /// most the close timeout. Past the deadline the scheduler is disposed, and this returns once
    /// the goodbye has completed or failed; an error caused by the deadline is not reported.
    /// The caller still calls <see cref="DisposeAsync"/> afterwards.
    /// </summary>
    public async ValueTask CloseAsync(Func<CancellationToken, ValueTask> sendGoodbye)
    {
        ArgumentNullException.ThrowIfNull(sendGoodbye);
        using CancellationTokenSource deadline = new(_closeTimeout);
        var registration = deadline.Token.UnsafeRegister(
          static state => ((BoundedOrderedCommandScheduler)state!).DisposeAfterCloseTimeout(),
          this);
        try
        {
            await ExecuteAsync(sendGoodbye, s_goodbyeReceived, barrier: true).ConfigureAwait(false);
        }
        catch (Exception) when (deadline.IsCancellationRequested)
        {
            // The deadline disposed the scheduler, which failed the goodbye.
        }
        finally
        {
            registration.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // Cancel before completing the writer: the pump waits for commands without a token,
            // so the writer completion is what wakes it, and it must then see the shutdown.
            Interlocked.CompareExchange(
              ref _terminalError,
              new ObjectDisposedException(nameof(BoundedOrderedCommandScheduler)),
              null);
            try
            {
                _shutdown.Cancel();
            }
            finally
            {
                // Even if a cancellation callback throws, the pump must still be woken.
                _commands.Writer.TryComplete();
            }
        }

        if (_abortTransport is not null && !_pump.IsCompleted)
        {
            try
            {
                await _pump.WaitAsync(_abortGracePeriod).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The pump is stuck in I/O that ignores shutdown. Aborting the transport makes
                // that I/O fail, so the pump unwinds and fails what is left.
                _abortTransport();
            }
        }

        await _pump.ConfigureAwait(false);
        if (Interlocked.Exchange(ref _shutdownDisposed, 1) == 0)
        {
            _shutdown.Dispose();
        }
    }

    private void DisposeAfterCloseTimeout()
    {
        // Runs on the timer thread. CloseAsync's caller disposes again afterwards, and that
        // DisposeAsync awaits the same pump, so this task needs no observer of its own.
        _ = DisposeAsync().AsTask();
    }

    private async Task PumpAsync()
    {
        BatchEntry? deferred = null;
        List<BatchEntry> batch = [];

        try
        {
            while (await TryGetNextAsync(deferred).ConfigureAwait(false) is { } next)
            {
                deferred = null;
                next.OnDequeued();
                if (next.Command.CancellationToken.IsCancellationRequested)
                {
                    next.Command.Cancel(next.Generation);
                    next.ReleasePump();
                    ReturnEntry(next);
                    continue;
                }

                batch.Add(next);
                if (!next.Command.IsBarrier)
                {
                    while (batch.Count < _inFlightLimit && _commands.Reader.TryRead(out var candidate))
                    {
                        var entry = RentEntry(candidate);
                        if (candidate.IsBarrier)
                        {
                            // A deferred barrier is still queued from its caller's point of view,
                            // so it keeps its early-cancellation registration until it runs.
                            deferred = entry;
                            break;
                        }

                        entry.OnDequeued();
                        batch.Add(entry);
                    }
                }

                if (!await ProcessBatchAsync(batch).ConfigureAwait(false))
                {
                    return;
                }

                foreach (var entry in batch)
                {
                    ReturnEntry(entry);
                }

                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Stop(exception);
        }
        finally
        {
            var terminalError = GetTerminalError();
            if (deferred is not null)
            {
                deferred.Command.Fail(deferred.Generation, terminalError);
                deferred.ReleasePump();
            }

            FailBatch(batch, terminalError);
            while (_commands.Reader.TryRead(out var command))
            {
                var entry = RentEntry(command);
                command.Fail(entry.Generation, terminalError);
                entry.ReleasePump();
                ReturnEntry(entry);
            }
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<BatchEntry?> TryGetNextAsync(BatchEntry? deferred)
    {
        // Disposal leaves the commands still queued (and a deferred barrier) for the drain in
        // PumpAsync's finally, which fails them without running them.
        if (_shutdown.IsCancellationRequested)
        {
            return null;
        }

        if (deferred is not null)
        {
            return deferred;
        }

        // No token: a cancellable one makes the channel allocate a waiter each time the pump goes
        // idle. Disposal completes the writer, which ends this wait.
        while (await _commands.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (_shutdown.IsCancellationRequested)
            {
                return null;
            }

            if (_commands.Reader.TryRead(out var command))
            {
                return RentEntry(command);
            }
        }

        return null;
    }

    private BatchEntry RentEntry(ICommand command)
    {
        if (!_freeEntries.TryPop(out var entry))
        {
            entry = new BatchEntry();
        }

        entry.Initialize(command);
        return entry;
    }

    private void ReturnEntry(BatchEntry entry)
    {
        entry.Clear();
        _freeEntries.Push(entry);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> ProcessBatchAsync(List<BatchEntry> batch)
    {
        if (batch.Count == 1)
        {
            var sent = await SendBatchAsync(batch, pipelined: false).ConfigureAwait(false);
            return sent &&
              await ReceiveBatchAsync(batch, pipelined: false).ConfigureAwait(false);
        }

        Volatile.Write(ref _readyCount, 0);
        var receive = ReceiveBatchAsync(batch, pipelined: true);
        var batchSent = await SendBatchAsync(batch, pipelined: true).ConfigureAwait(false);
        var received = await receive.ConfigureAwait(false);
        return batchSent && received;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> SendBatchAsync(
        List<BatchEntry> batch,
        bool pipelined)
    {
        var flushBatch = false;
        for (var index = 0; index < batch.Count; index++)
        {
            var entry = batch[index];
            if (entry.Command.CancellationToken.IsCancellationRequested)
            {
                entry.CanceledBeforeSend = true;
                SignalReady(entry, pipelined);
                continue;
            }

            try
            {
                await SendAsync(entry.Command).ConfigureAwait(false);
                entry.WasSent = true;
                flushBatch |= entry.Command.FlushBatch;
                if (!entry.Command.FlushBatch)
                {
                    SignalReady(entry, pipelined);
                }
            }
            catch (Exception exception)
            {
                entry.SendError = exception;
                SignalReady(entry, pipelined);
                if (IsFatal(exception))
                {
                    Stop(exception);
                    SignalRemaining(batch, index + 1, pipelined);
                    return false;
                }
            }
        }

        if (flushBatch && _flushBatchAsync is not null)
        {
            try
            {
                var flush = _flushBatchAsync(_shutdown.Token);
                SignalRemaining(batch, 0, pipelined);
                await flush.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Stop(exception);
                SignalRemaining(batch, 0, pipelined);
                return false;
            }
        }
        else
        {
            SignalRemaining(batch, 0, pipelined);
        }

        return true;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> ReceiveBatchAsync(
        List<BatchEntry> batch,
        bool pipelined)
    {
        for (var index = 0; index < batch.Count; index++)
        {
            var entry = batch[index];
            if (pipelined)
            {
                // The sender signals every entry on all of its paths, so this wait needs no token.
                while (Volatile.Read(ref _readyCount) <= index)
                {
                    await _ready.WaitAsync().ConfigureAwait(false);
                }
            }

            if (_shutdown.IsCancellationRequested)
            {
                // Disposal fails the rest of the batch (FailBatch) instead of reading responses
                // that no caller will get.
                return false;
            }

            if (entry.CanceledBeforeSend)
            {
                entry.Command.Cancel(entry.Generation);
                entry.ReleasePump();
                continue;
            }

            if (entry.SendError is { } sendError)
            {
                CompleteFromDelegateError(entry, sendError);
                entry.ReleasePump();
                continue;
            }

            if (!entry.WasSent)
            {
                // A fatal send error stopped the batch before this command; FailBatch completes
                // and releases it once the batch unwinds.
                continue;
            }

            try
            {
                await ReceiveAsync(entry).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (IsFatal(exception))
                {
                    // Left incomplete and held: FailBatch fails it with the terminal error.
                    Stop(exception);
                    return false;
                }

                CompleteFromDelegateError(entry, exception);
            }

            entry.ReleasePump();
        }

        return true;
    }

    private void SignalReady(BatchEntry entry, bool pipelined)
    {
        if (pipelined && !entry.IsReady)
        {
            entry.IsReady = true;
            Interlocked.Increment(ref _readyCount);
            _ready.Set();
        }
    }

    private void SignalRemaining(
        List<BatchEntry> batch,
        int startIndex,
        bool pipelined)
    {
        for (var index = startIndex; index < batch.Count; index++)
        {
            SignalReady(batch[index], pipelined);
        }
    }

    private ValueTask SendAsync(ICommand command)
    {
        var cancellationToken = command.CancellationToken;
        return cancellationToken.CanBeCanceled
          ? SendCancellableAsync(command, cancellationToken)
          : command.SendAsync(_shutdown.Token);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask SendCancellableAsync(ICommand command, CancellationToken cancellationToken)
    {
        var source = _sendCancellation.Link(cancellationToken, out var registration);
        try
        {
            await command.SendAsync(source.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendCancellation.Unlink(source, registration);
        }
    }

    // Receives get the caller's token too: some receives are meant to be cancelled, for example
    // PostgreSQL's wait for a notification, which its subscriber cancels to send LISTEN.
    private ValueTask ReceiveAsync(BatchEntry entry)
    {
        var cancellationToken = entry.Command.CancellationToken;
        return cancellationToken.CanBeCanceled
          ? ReceiveCancellableAsync(entry, cancellationToken)
          : entry.Command.ReceiveAsync(entry.Generation, _shutdown.Token);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask ReceiveCancellableAsync(BatchEntry entry, CancellationToken cancellationToken)
    {
        var source = _receiveCancellation.Link(cancellationToken, out var registration);
        try
        {
            await entry.Command.ReceiveAsync(entry.Generation, source.Token).ConfigureAwait(false);
        }
        finally
        {
            _receiveCancellation.Unlink(source, registration);
        }
    }

    private bool IsFatal(Exception exception) => _isFatal(exception);

    private void CompleteFromDelegateError(BatchEntry entry, Exception exception)
    {
        var terminalError = Volatile.Read(ref _terminalError);
        if (_shutdown.IsCancellationRequested && terminalError is not null)
        {
            entry.Command.Fail(entry.Generation, terminalError);
        }
        else if (exception is OperationCanceledException)
        {
            entry.Command.Cancel(entry.Generation);
        }
        else
        {
            entry.Command.Fail(entry.Generation, exception);
        }
    }

    private void Stop(Exception exception)
    {
        Interlocked.CompareExchange(ref _terminalError, exception, null);
        _commands.Writer.TryComplete();
    }

    private Exception GetTerminalError() =>
      Volatile.Read(ref _terminalError)
      ?? new InvalidOperationException("The command scheduler stopped unexpectedly.");

    private static void FailBatch(List<BatchEntry> batch, Exception exception)
    {
        foreach (var entry in batch)
        {
            entry.Command.Fail(entry.Generation, exception);
            entry.ReleasePump();
        }
    }

    private interface ICommand
    {
        bool IsBarrier { get; }

        bool FlushBatch { get; }

        int Generation { get; }

        CancellationToken CancellationToken { get; }

        // True when the pump holds an ownership of this rental that it must give back with
        // ReleasePump. Only read when the pump takes the command from the channel, while the
        // rental is guaranteed to be live.
        bool HoldsPump { get; }

        // Called by the pump when it takes the command to run it: from then on, cancellation is
        // observed by the pump rather than completing the command early.
        void OnDequeued();

        void ReleasePump();

        ValueTask SendAsync(CancellationToken cancellationToken);

        ValueTask ReceiveAsync(int generation, CancellationToken cancellationToken);

        void Cancel(int generation);

        void Fail(int generation, Exception exception);
    }

    // The state of the delegate-only ExecuteAsync overload.
    private readonly struct DelegatePair<T>(
        Func<CancellationToken, ValueTask> sendAsync,
        Func<CancellationToken, ValueTask<T>> receiveAsync)
    {
        public static readonly Func<DelegatePair<T>, CancellationToken, ValueTask> Send =
          static (pair, cancellationToken) => pair._sendAsync(cancellationToken);

        public static readonly Func<DelegatePair<T>, CancellationToken, ValueTask<T>> Receive =
          static (pair, cancellationToken) => pair._receiveAsync(cancellationToken);

        private readonly Func<CancellationToken, ValueTask> _sendAsync = sendAsync;
        private readonly Func<CancellationToken, ValueTask<T>> _receiveAsync = receiveAsync;
    }

    // Pooled per (TState, T): the state is stored by value, so a value-type state never boxes.
    private sealed class Command<TState, T> : ICommand, IValueTaskSource<T>
    {
        private static readonly ConcurrentQueue<Command<TState, T>> s_pool = new();
        private const int MaximumPoolSize = 256;
        private static int s_poolCount;

        // A rental is owned by its caller until the completion is consumed and, when the channel
        // write went asynchronous, by the writer continuation until it has observed the write.
        // The channel publishes the command to the pump before that continuation runs, so the
        // caller can consume the completion first. Whichever owner releases last returns the
        // command to the pool, so a stale continuation never reads a later rental's state.
        //
        // A command whose token can be cancelled is also owned by the pump from the moment it is
        // written to the channel until the pump is done with it. Such a command completes early
        // when it is cancelled while queued, so its caller can consume it and release while the
        // object is still in the channel; the pump's ownership keeps it from being recycled and
        // rented again under the stale channel entry. A command whose token cannot be cancelled
        // is only ever completed by the pump once it is queued, so it needs no such ownership.
        private const int CallerOwner = 1;
        private const int EnqueueOwner = 2;
        private const int PumpOwner = 4;
        private static readonly Action<object?> s_cancelQueued =
          static state => ((Command<TState, T>)state!).CancelQueued();
        private readonly Action _continueEnqueue;
        private ManualResetValueTaskSourceCore<T> _completion;
        private TState _state = default!;
        private Func<TState, CancellationToken, ValueTask>? _sendAsync;
        private Func<TState, CancellationToken, ValueTask<T>>? _receiveAsync;
        private BoundedOrderedCommandScheduler? _scheduler;
        private CancellationToken _cancellationToken;
        private ValueTask _pendingWrite;
        private CancellationTokenRegistration _queuedCancellation;
        private long _completionState;
        private int _generation;
        private int _owners;
        private bool _isBarrier;
        private bool _flushBatch;
        private bool _holdsPump;
        private int _consumed;

        private Command()
        {
            _continueEnqueue = ContinueEnqueue;
        }

        public ValueTask<T> Completion => new(this, _completion.Version);

        public bool IsBarrier => _isBarrier;

        public bool FlushBatch => _flushBatch;

        public int Generation => Volatile.Read(ref _generation);

        public CancellationToken CancellationToken => _cancellationToken;

        public bool HoldsPump => _holdsPump;

        public void OnDequeued()
        {
            // Dispose waits for a callback that is already running, so once this returns no
            // early cancellation can complete the command behind the pump's back.
            _queuedCancellation.Dispose();
            _queuedCancellation = default;
        }

        public void ReleasePump()
        {
            OnDequeued();
            Release(PumpOwner);
        }

        public ValueTask SendAsync(CancellationToken delegateCancellationToken) =>
          _sendAsync!(_state, delegateCancellationToken);

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
        public async ValueTask ReceiveAsync(
            int generation,
            CancellationToken delegateCancellationToken)
        {
            var result = await _receiveAsync!(_state, delegateCancellationToken).ConfigureAwait(false);
            if (TryBeginCompletion(generation))
            {
                _completion.SetResult(result);
            }
        }

        public void Cancel(int generation)
        {
            if (TryBeginCompletion(generation))
            {
                _completion.SetException(new OperationCanceledException(_cancellationToken));
            }
        }

        public void Fail(int generation, Exception exception)
        {
            if (TryBeginCompletion(generation))
            {
                _completion.SetException(exception);
            }
        }

        public T GetResult(short token)
        {
            var status = _completion.GetStatus(token);
            if (status == ValueTaskSourceStatus.Pending)
            {
                throw new InvalidOperationException("The command has not completed.");
            }

            if (Interlocked.Exchange(ref _consumed, 1) != 0)
            {
                throw new InvalidOperationException("A command ValueTask may only be consumed once.");
            }

            try
            {
                return _completion.GetResult(token);
            }
            finally
            {
                // Resetting here invalidates the token, so a second await fails even while the
                // writer continuation still owns the command. That continuation can only try to
                // complete the generation that has already completed, which is a no-op.
                _completion.Reset();
                Release(CallerOwner);
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags) =>
          _completion.OnCompleted(continuation, state, token, flags);

        public static Command<TState, T> Rent(
            BoundedOrderedCommandScheduler scheduler,
            TState state,
            Func<TState, CancellationToken, ValueTask> sendAsync,
            Func<TState, CancellationToken, ValueTask<T>> receiveAsync,
            bool isBarrier,
            CancellationToken cancellationToken,
            bool flushBatch)
        {
            if (!s_pool.TryDequeue(out var command))
            {
                command = new Command<TState, T>();
            }
            else
            {
                Interlocked.Decrement(ref s_poolCount);
            }

            command.Initialize(
                scheduler,
                state,
                sendAsync,
                receiveAsync,
                isBarrier,
                cancellationToken,
                flushBatch);
            return command;
        }

        public void Enqueue(ChannelWriter<ICommand> writer)
        {
            var generation = Generation;
            if (_holdsPump)
            {
                // Registered before the write publishes the command, so the pump always sees the
                // registration it must dispose. If the token is already cancelled the callback
                // runs inline and the write below fails fast with the same cancellation.
                _queuedCancellation = _cancellationToken.UnsafeRegister(s_cancelQueued, this);
            }

            ValueTask write;
            try
            {
                write = writer.WriteAsync(this, _cancellationToken);
            }
            catch (Exception exception)
            {
                Fail(generation, exception);
                ReleaseUnqueuedPumpOwnership();
                return;
            }

            if (write.IsCompleted)
            {
                CompleteEnqueue(generation, write);
                return;
            }

            // The caller cannot consume the completion before Enqueue returns, so taking the
            // second ownership here, before the continuation is registered, is never too late.
            _pendingWrite = write;
            Interlocked.Or(ref _owners, EnqueueOwner);
            write.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_continueEnqueue);
        }

        private void Initialize(
            BoundedOrderedCommandScheduler scheduler,
            TState state,
            Func<TState, CancellationToken, ValueTask> sendAsync,
            Func<TState, CancellationToken, ValueTask<T>> receiveAsync,
            bool isBarrier,
            CancellationToken cancellationToken,
            bool flushBatch)
        {
            _completion.RunContinuationsAsynchronously = true;
            _scheduler = scheduler;
            _state = state;
            _sendAsync = sendAsync;
            _receiveAsync = receiveAsync;
            _cancellationToken = cancellationToken;
            _isBarrier = isBarrier;
            _flushBatch = flushBatch;
            _holdsPump = cancellationToken.CanBeCanceled;
            Volatile.Write(ref _consumed, 0);
            Volatile.Write(ref _owners, _holdsPump ? CallerOwner | PumpOwner : CallerOwner);
            var generation = unchecked(_generation + 1);
            Volatile.Write(ref _generation, generation);
            Volatile.Write(ref _completionState, GetIncompleteState(generation));
        }

        private void ContinueEnqueue()
        {
            // The enqueue ownership keeps this rental from being reset or rented again, so the
            // pending write and the generation still belong to the write that registered us.
            var write = _pendingWrite;
            _pendingWrite = default;
            CompleteEnqueue(Generation, write);
            Release(EnqueueOwner);
        }

        private void Release(int owner)
        {
            // Every ownership is taken before the caller holds the ValueTask (the pump's in
            // Initialize, the writer continuation's in Enqueue), so once an owner has released it
            // can never come back. Seeing only our own bit means we are the last owner, which keeps
            // the synchronous fast path free of an extra interlocked write.
            if (Volatile.Read(ref _owners) != owner &&
                Interlocked.And(ref _owners, ~owner) != owner)
            {
                return;
            }

            _state = default!;
            _sendAsync = null;
            _receiveAsync = null;
            _scheduler = null;
            _cancellationToken = default;
            _pendingWrite = default;
            _queuedCancellation = default;
            _isBarrier = false;
            _flushBatch = false;
            _holdsPump = false;
            if (Interlocked.Increment(ref s_poolCount) <= MaximumPoolSize)
            {
                s_pool.Enqueue(this);
            }
            else
            {
                Interlocked.Decrement(ref s_poolCount);
            }
        }

        private void CompleteEnqueue(int generation, ValueTask write)
        {
            try
            {
                write.GetAwaiter().GetResult();
                return;
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                Cancel(generation);
            }
            catch (ChannelClosedException)
            {
                Fail(generation, _scheduler!.GetTerminalError());
            }
            catch (Exception exception)
            {
                Fail(generation, exception);
            }

            // The write failed, so the command never reached the pump: give its ownership back
            // on its behalf. This runs while the caller or the writer continuation still owns
            // the rental, so the fields are still this rental's.
            ReleaseUnqueuedPumpOwnership();
        }

        private void ReleaseUnqueuedPumpOwnership()
        {
            if (_holdsPump)
            {
                ReleasePump();
            }
        }

        private void CancelQueued()
        {
            // The registration is disposed before the pump releases its ownership, and the pump
            // releases before the rental can be recycled, so this generation is still ours.
            Cancel(Generation);
        }

        private bool TryBeginCompletion(int generation)
        {
            var incomplete = GetIncompleteState(generation);
            return Interlocked.CompareExchange(
              ref _completionState,
              incomplete | 1,
              incomplete) == incomplete;
        }

        private static long GetIncompleteState(int generation) => (long)(uint)generation << 1;
    }

    // A CancellationTokenSource linked to one command's token at a time and to shutdown. Only one
    // operation uses it at a time (the pump's sends, or its receives), so it is reset and reused,
    // and replaced only after it was actually cancelled. The token is valid for the duration of
    // that one operation: a delegate must not keep a registration on it after it returns.
    private sealed class ReusableLinkedSource(CancellationTokenSource shutdown)
    {
        private static readonly Action<object?> s_cancel =
          static state => ((CancellationTokenSource)state!).Cancel();
        private CancellationTokenSource _source = new();

        public CancellationTokenSource Link(
            CancellationToken cancellationToken,
            out CancellationTokenRegistration registration)
        {
            var source = Volatile.Read(ref _source);
            registration = cancellationToken.UnsafeRegister(s_cancel, source);
            return source;
        }

        public void Unlink(CancellationTokenSource source, CancellationTokenRegistration registration)
        {
            // Waits for a cancellation callback that is already running, so the source is quiet
            // before it is reset or replaced.
            registration.Dispose();
            if (!source.TryReset())
            {
                CancellationTokenSource next = new();
                Volatile.Write(ref _source, next);

                // Pairs with Cancel: either shutdown sees the new source, or this sees shutdown.
                if (shutdown.IsCancellationRequested)
                {
                    next.Cancel();
                }
            }
        }

        public void Cancel() => Volatile.Read(ref _source).Cancel();
    }

    private sealed class BatchEntry
    {
        private bool _holdsPump;

        public ICommand Command { get; private set; } = null!;

        // Both are captured when the pump takes the command, while the rental is still live. A
        // command the pump does not hold can be recycled as soon as it completes, so they must
        // never be re-read from the command after that.
        public int Generation { get; private set; }

        public bool CanceledBeforeSend { get; set; }

        public bool WasSent { get; set; }

        public Exception? SendError { get; set; }

        public bool IsReady { get; set; }

        public void Initialize(ICommand command)
        {
            Command = command;
            Generation = command.Generation;
            _holdsPump = command.HoldsPump;
        }

        public void Clear()
        {
            Command = null!;
            Generation = 0;
            _holdsPump = false;
            CanceledBeforeSend = false;
            WasSent = false;
            SendError = null;
            IsReady = false;
        }

        public void OnDequeued()
        {
            if (_holdsPump)
            {
                Command.OnDequeued();
            }
        }

        // Gives the pump's ownership back exactly once. Callers complete the command first: the
        // release may return the rental to the pool.
        public void ReleasePump()
        {
            if (_holdsPump)
            {
                _holdsPump = false;
                Command.ReleasePump();
            }
        }
    }
}

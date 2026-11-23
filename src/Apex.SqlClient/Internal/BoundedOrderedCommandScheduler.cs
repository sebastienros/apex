using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Threading.Tasks.Sources;

namespace Apex.SqlClient.Internal;

internal sealed class BoundedOrderedCommandScheduler : IAsyncDisposable
{
    private readonly Channel<ICommand> _commands;
    private readonly int _inFlightLimit;
    private readonly Func<Exception, bool> _isFatal;
    private readonly Func<CancellationToken, ValueTask>? _flushBatchAsync;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;
    private Exception? _terminalError;
    private int _disposed;
    private int _shutdownDisposed;

    public BoundedOrderedCommandScheduler(
        int inFlightLimit,
        int queueCapacity,
        Func<Exception, bool>? isFatal = null,
        Func<CancellationToken, ValueTask>? flushBatchAsync = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inFlightLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);

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

        Command<T> command = Command<T>.Rent(
          this,
          sendAsync,
          receiveAsync,
          barrier,
          cancellationToken,
          flushBatch);
        var completion = command.Completion;
        command.Enqueue(_commands.Writer);
        return completion;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Stop(new ObjectDisposedException(nameof(BoundedOrderedCommandScheduler)));
            _shutdown.Cancel();
        }

        await _pump.ConfigureAwait(false);
        if (Interlocked.Exchange(ref _shutdownDisposed, 1) == 0)
        {
            _shutdown.Dispose();
        }
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
                    continue;
                }

                batch.Add(next);
                if (!next.Command.IsBarrier)
                {
                    while (batch.Count < _inFlightLimit && _commands.Reader.TryRead(out var candidate))
                    {
                        var entry = new BatchEntry(candidate);
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
                var entry = new BatchEntry(command);
                command.Fail(entry.Generation, terminalError);
                entry.ReleasePump();
            }
        }
    }

    private async ValueTask<BatchEntry?> TryGetNextAsync(BatchEntry? deferred)
    {
        if (deferred is not null)
        {
            return deferred;
        }

        while (await _commands.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
        {
            if (_commands.Reader.TryRead(out var command))
            {
                return new BatchEntry(command);
            }
        }

        return null;
    }

    private async ValueTask<bool> ProcessBatchAsync(List<BatchEntry> batch)
    {
        if (batch.Count == 1)
        {
            var sent = await SendBatchAsync(batch, null).ConfigureAwait(false);
            return sent &&
              await ReceiveBatchAsync(batch, null).ConfigureAwait(false);
        }

        using SemaphoreSlim ready = new(0, batch.Count);
        var receive = ReceiveBatchAsync(batch, ready);
        var batchSent = await SendBatchAsync(batch, ready).ConfigureAwait(false);
        var received = await receive.ConfigureAwait(false);
        return batchSent && received;
    }

    private async ValueTask<bool> SendBatchAsync(
        List<BatchEntry> batch,
        SemaphoreSlim? ready)
    {
        var flushBatch = false;
        for (var index = 0; index < batch.Count; index++)
        {
            var entry = batch[index];
            if (entry.Command.CancellationToken.IsCancellationRequested)
            {
                entry.CanceledBeforeSend = true;
                SignalReady(entry, ready);
                continue;
            }

            try
            {
                using var cancellation = CreateDelegateCancellation(entry.Command);
                await entry.Command.SendAsync(cancellation.Token).ConfigureAwait(false);
                entry.WasSent = true;
                flushBatch |= entry.Command.FlushBatch;
                if (!entry.Command.FlushBatch)
                {
                    SignalReady(entry, ready);
                }
            }
            catch (Exception exception)
            {
                entry.SendError = exception;
                SignalReady(entry, ready);
                if (IsFatal(exception))
                {
                    Stop(exception);
                    SignalRemaining(batch, index + 1, ready);
                    return false;
                }
            }
        }

        if (flushBatch && _flushBatchAsync is not null)
        {
            try
            {
                var flush = _flushBatchAsync(_shutdown.Token);
                SignalRemaining(batch, 0, ready);
                await flush.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Stop(exception);
                SignalRemaining(batch, 0, ready);
                return false;
            }
        }
        else
        {
            SignalRemaining(batch, 0, ready);
        }

        return true;
    }

    private async ValueTask<bool> ReceiveBatchAsync(
        List<BatchEntry> batch,
        SemaphoreSlim? ready)
    {
        foreach (var entry in batch)
        {
            if (ready is not null)
            {
                await ready.WaitAsync(_shutdown.Token).ConfigureAwait(false);
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
                using var cancellation = CreateDelegateCancellation(entry.Command);
                await entry.Command.ReceiveAsync(entry.Generation, cancellation.Token).ConfigureAwait(false);
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

    private static void SignalReady(BatchEntry entry, SemaphoreSlim? ready)
    {
        if (ready is not null && !entry.IsReady)
        {
            entry.IsReady = true;
            ready.Release();
        }
    }

    private static void SignalRemaining(
        List<BatchEntry> batch,
        int startIndex,
        SemaphoreSlim? ready)
    {
        for (var index = startIndex; index < batch.Count; index++)
        {
            SignalReady(batch[index], ready);
        }
    }

    private DelegateCancellation CreateDelegateCancellation(ICommand command)
    {
        if (!command.CancellationToken.CanBeCanceled)
        {
            return new DelegateCancellation(_shutdown.Token, null);
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(
            command.CancellationToken,
            _shutdown.Token);
        return new DelegateCancellation(source.Token, source);
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

    private sealed class Command<T> : ICommand, IValueTaskSource<T>
    {
        private static readonly ConcurrentQueue<Command<T>> s_pool = new();
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
          static state => ((Command<T>)state!).CancelQueued();
        private readonly Action _continueEnqueue;
        private ManualResetValueTaskSourceCore<T> _completion;
        private Func<CancellationToken, ValueTask>? _sendAsync;
        private Func<CancellationToken, ValueTask<T>>? _receiveAsync;
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
          _sendAsync!(delegateCancellationToken);

        public async ValueTask ReceiveAsync(
            int generation,
            CancellationToken delegateCancellationToken)
        {
            var result = await _receiveAsync!(delegateCancellationToken).ConfigureAwait(false);
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

        public static Command<T> Rent(
            BoundedOrderedCommandScheduler scheduler,
            Func<CancellationToken, ValueTask> sendAsync,
            Func<CancellationToken, ValueTask<T>> receiveAsync,
            bool isBarrier,
            CancellationToken cancellationToken,
            bool flushBatch)
        {
            if (!s_pool.TryDequeue(out var command))
            {
                command = new Command<T>();
            }
            else
            {
                Interlocked.Decrement(ref s_poolCount);
            }

            command.Initialize(
                scheduler,
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
            Func<CancellationToken, ValueTask> sendAsync,
            Func<CancellationToken, ValueTask<T>> receiveAsync,
            bool isBarrier,
            CancellationToken cancellationToken,
            bool flushBatch)
        {
            _completion.RunContinuationsAsynchronously = true;
            _scheduler = scheduler;
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
            // An ownership is only taken before the caller holds the ValueTask, so once the other
            // owner has released it can never come back. Seeing only our own bit means we are the
            // last owner, which keeps the synchronous fast path free of an extra interlocked write.
            if (Volatile.Read(ref _owners) != owner &&
                Interlocked.And(ref _owners, ~owner) != owner)
            {
                return;
            }

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

    private sealed class BatchEntry(ICommand command)
    {
        public ICommand Command { get; } = command;

        // Both are captured when the pump takes the command, while the rental is still live. A
        // command the pump does not hold can be recycled as soon as it completes, so they must
        // never be re-read from the command after that.
        public int Generation { get; } = command.Generation;

        private bool _holdsPump = command.HoldsPump;

        public bool CanceledBeforeSend { get; set; }

        public bool WasSent { get; set; }

        public Exception? SendError { get; set; }

        public bool IsReady { get; set; }

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

    private readonly struct DelegateCancellation(
        CancellationToken token,
        CancellationTokenSource? source) : IDisposable
    {
        public CancellationToken Token { get; } = token;

        public void Dispose() => source?.Dispose();
    }
}

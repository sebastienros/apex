namespace Apex.SqlClient.Internal;

// Only allocated for result-preserving readers. Transitions run under the reader's
// existing gate; asynchronous continuations never run inside that gate.
internal class SqlResultReaderState
{
    private TaskCompletionSource<bool>? _initialization;
    private TaskCompletionSource<bool>? _nextResult;
    private bool _nextAwaitingStart;
    private long _recordsAffected = -1;

    internal AsyncAutoResetEvent Advance { get; } = new();
    internal bool Ended { get; private set; }
    internal bool Active { get; private set; }

    internal int RecordsAffected
    {
        get
        {
            var count = Volatile.Read(ref _recordsAffected);
            return count is >= 0 and <= int.MaxValue ? (int)count : -1;
        }
    }

    internal Task<bool> Initialize()
    {
        _initialization ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _initialization.Task;
    }

    internal Task<bool> NextResult()
    {
        if (_nextResult is not null)
        {
            throw new InvalidOperationException("Concurrent result transitions are not supported.");
        }

        _nextResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _nextAwaitingStart = true;
        return _nextResult.Task;
    }

    internal void Start()
    {
        Ended = false;
        Active = true;
        if (_nextResult is not null)
        {
            _nextAwaitingStart = false;
        }
    }

    internal bool PublishRow()
    {
        var initialization = _initialization;
        _initialization = null;
        var nextResult = TakeStartedResult();
        initialization?.TrySetResult(true);
        nextResult?.TrySetResult(true);
        return initialization is not null || nextResult is not null;
    }

    internal bool End(bool stopped)
    {
        Ended = true;
        Active = false;
        var initialization = _initialization;
        _initialization = null;
        var nextResult = TakeStartedResult();
        initialization?.TrySetResult(false);
        nextResult?.TrySetResult(true);
        return _nextResult is null && !stopped;
    }

    internal void Complete(Exception? error)
    {
        var initialization = _initialization;
        var nextResult = _nextResult;
        _initialization = null;
        _nextResult = null;
        if (error is null)
        {
            initialization?.TrySetResult(false);
            nextResult?.TrySetResult(false);
        }
        else
        {
            initialization?.TrySetException(error);
            nextResult?.TrySetException(error);
        }
    }

    // The protocol pump is the sole writer; consumers only read the published count.
    internal void AddRecordsAffected(long count)
    {
        if (count < 0) return;
        var current = _recordsAffected;
        var updated = current < 0 ? count
            : count > long.MaxValue - current ? long.MaxValue
            : current + count;
        Volatile.Write(ref _recordsAffected, updated);
    }

    private TaskCompletionSource<bool>? TakeStartedResult()
    {
        if (_nextAwaitingStart) return null;
        var result = _nextResult;
        _nextResult = null;
        return result;
    }
}

using System.Threading.Tasks.Sources;

namespace Apex.SqlClient.Internal;

internal sealed class AsyncAutoResetEvent : IValueTaskSource
{
    private readonly object _gate = new();
    private ManualResetValueTaskSourceCore<bool> _completion;
    private bool _signaled;
    private bool _waiting;

    internal AsyncAutoResetEvent(bool signaled = false)
    {
        _signaled = signaled;
        _completion.RunContinuationsAsynchronously = true;
    }

    internal ValueTask WaitAsync()
    {
        lock (_gate)
        {
            if (_signaled)
            {
                _signaled = false;
                return ValueTask.CompletedTask;
            }

            if (_waiting)
            {
                throw new InvalidOperationException("Only one waiter is supported.");
            }

            _waiting = true;
            _completion.Reset();
            return new ValueTask(this, _completion.Version);
        }
    }

    internal void Set() => Set(runContinuationInline: false);

    /// <summary>
    /// Releases the waiter. With <paramref name="runContinuationInline"/> a parked waiter resumes on
    /// the calling thread before this returns, instead of on the thread pool. Readers use it to
    /// let the consumer drive a parked protocol pump through data that is already received; the
    /// pump returns to the caller as soon as it parks again or awaits the network.
    /// </summary>
    internal void Set(bool runContinuationInline)
    {
        bool complete;
        lock (_gate)
        {
            complete = _waiting;
            if (complete)
            {
                _waiting = false;
                // Only the Set that releases this wait completes it, and SignalCompletion reads the
                // flag before it can run the continuation that may wait (and be Set) again.
                _completion.RunContinuationsAsynchronously = !runContinuationInline;
            }
            else
            {
                _signaled = true;
            }
        }

        if (complete)
        {
            _completion.SetResult(true);
        }
    }

    public void GetResult(short token) => _completion.GetResult(token);

    public ValueTaskSourceStatus GetStatus(short token) =>
      _completion.GetStatus(token);

    public void OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags) =>
      _completion.OnCompleted(continuation, state, token, flags);
}

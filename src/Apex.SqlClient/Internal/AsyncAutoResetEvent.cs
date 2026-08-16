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

    internal void Set()
    {
        bool complete;
        lock (_gate)
        {
            complete = _waiting;
            if (complete)
            {
                _waiting = false;
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

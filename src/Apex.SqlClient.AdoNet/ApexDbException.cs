using System.Data.Common;

namespace Apex.SqlClient;

/// <summary>
/// An ADO.NET error wrapping the original native Apex exception in <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class ApexDbException : DbException
{
    internal ApexDbException(
        SqlClientException exception,
        string? sqlState = null,
        bool isTransient = false,
        int? errorCode = null)
        : base(exception.Message, exception)
    {
        SqlState = sqlState;
        IsTransient = isTransient;
        HResult = errorCode ?? exception.HResult;
    }

    internal ApexDbException(string message, Exception innerException, bool isTransient)
        : base(message, innerException)
    {
        IsTransient = isTransient;
        HResult = innerException.HResult;
    }

    /// <summary>Gets the SQLSTATE supplied by the provider, or null when unavailable.</summary>
    public override string? SqlState { get; }

    /// <summary>Gets whether the native provider classifies the error as transient.</summary>
    public override bool IsTransient { get; }
}

/// <summary>
/// The cancellation of one command operation: the caller's token, <see cref="System.Data.Common.DbCommand.Cancel"/>,
/// and the command timeout, which is re-armed for each execute, read and next-result call.
/// </summary>
internal sealed class ApexOperationCancellation : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly CancellationToken _token;
    private readonly CancellationToken _callerToken;
    private readonly int _timeoutSeconds;
    private volatile bool _canceled;

    // CancelAfter accepts at most uint.MaxValue - 1 milliseconds (about 49.7 days); longer timeouts,
    // such as int.MaxValue used as "effectively infinite", never fire.
    private const int MaximumTimeoutSeconds = (int)((uint.MaxValue - 1) / 1000);

    internal ApexOperationCancellation(CancellationToken callerToken, int timeoutSeconds)
    {
        _source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        // Captured once: CancellationTokenSource.Token throws after disposal, which an exception filter would
        // swallow, silently skipping the translation.
        _token = _source.Token;
        _callerToken = callerToken;
        _timeoutSeconds = timeoutSeconds > MaximumTimeoutSeconds ? 0 : timeoutSeconds;
    }

    internal CancellationTokenSource Source => _source;
    internal CancellationToken Token => _token;

    internal void Arm()
    {
        if (_timeoutSeconds <= 0) return;
        try
        {
            _source.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void Disarm()
    {
        if (_timeoutSeconds <= 0) return;
        try
        {
            _source.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Cancels on behalf of DbCommand.Cancel; the operation then fails with cancellation, not timeout.</summary>
    internal void Cancel()
    {
        _canceled = true;
        ApexCancellation.TryCancel(_source);
    }

    /// <summary>
    /// Returns a timeout error for a failure caused by the command timeout: the source fired while neither
    /// the caller's token, the per-call token nor DbCommand.Cancel requested cancellation. A native error
    /// raised by the server-side cancel the timeout triggered counts as the timeout too.
    /// </summary>
    internal ApexDbException? TranslateTimeout(Exception exception, CancellationToken callToken = default)
    {
        if (_timeoutSeconds <= 0 ||
            exception is not (OperationCanceledException or SqlClientException) ||
            _canceled ||
            !_token.IsCancellationRequested ||
            _callerToken.IsCancellationRequested ||
            // The operation's own token is passed to initialization; only a separate call token counts.
            (callToken != _token && callToken.IsCancellationRequested))
        {
            return null;
        }

        var message = $"The command timed out after {_timeoutSeconds} seconds.";
        return new ApexDbException(message, new TimeoutException(message, exception), isTransient: true);
    }

    public void Dispose() => _source.Dispose();
}

internal static class ApexExceptionBoundary
{
    internal static T Invoke<T>(Func<T> operation, Func<SqlClientException, DbException> translate)
    {
        try
        {
            return operation();
        }
        catch (SqlClientException exception)
        {
            throw translate(exception);
        }
    }

    internal static async Task RunAsync(Func<Task> operation, Func<SqlClientException, DbException> translate)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (SqlClientException exception)
        {
            throw translate(exception);
        }
    }

    internal static async Task<T> RunAsync<T>(Func<Task<T>> operation, Func<SqlClientException, DbException> translate)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (SqlClientException exception)
        {
            throw translate(exception);
        }
    }

    internal static async ValueTask RunValueAsync(Func<ValueTask> operation, Func<SqlClientException, DbException> translate)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (SqlClientException exception)
        {
            throw translate(exception);
        }
    }

    internal static async ValueTask<T> RunValueAsync<T>(Func<ValueTask<T>> operation, Func<SqlClientException, DbException> translate)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (SqlClientException exception)
        {
            throw translate(exception);
        }
    }
}

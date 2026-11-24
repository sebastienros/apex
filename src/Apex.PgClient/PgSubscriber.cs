using System.Threading.Channels;

namespace Apex.PgClient;

public sealed class PgSubscriber : IPgSubscriber
{
    private readonly PgConnectOptions _options;
    private readonly Func<int, TimeSpan?> _reconnectPolicy;
    private readonly Channel<PgNotification> _notifications =
      Channel.CreateUnbounded<PgNotification>(
        new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly HashSet<string> _channels = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    // Kept apart from the source, which DisposeAsync disposes, so Notifications stays readable.
    private readonly CancellationToken _disposeToken;
    private readonly object _waitGate = new();
    private CancellationTokenSource? _waitCancellation;
    private TaskCompletionSource? _resumeWait;
    private int _pauseDepth;
    private PgConnection _connection;
    private readonly Task _listenLoop;
    private Exception? _listenError;
    private int _disposed;

    private PgSubscriber(
        PgConnectOptions options,
        PgConnection connection,
        Func<int, TimeSpan?> reconnectPolicy)
    {
        _options = options;
        _disposeToken = _disposeCancellation.Token;
        _connection = connection;
        _connection.Notification += OnNotification;
        _reconnectPolicy = reconnectPolicy;
        _listenLoop = ListenAsync();
    }

    public IAsyncEnumerable<PgNotification> Notifications =>
      _notifications.Reader.ReadAllAsync(_disposeToken);

    public int ProcessId => _connection.ProcessId;

    public IReadOnlyCollection<string> Channels
    {
        get
        {
            lock (_channels)
            {
                return _channels.ToArray();
            }
        }
    }

    public static async ValueTask<PgSubscriber> ConnectAsync(
        PgConnectOptions options,
        Func<int, TimeSpan?>? reconnectPolicy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connection = await PgClient.ConnectAsync(options, cancellationToken)
          .ConfigureAwait(false);
        return new PgSubscriber(
          options,
          connection,
          reconnectPolicy ?? (_ => null));
    }

    public async ValueTask SubscribeAsync(
        string channel,
        CancellationToken cancellationToken = default)
    {
        ValidateChannel(channel);
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            BeginPause();
            try
            {
                await _connection.ExecuteAsync(
                  "LISTEN " + QuoteIdentifier(channel),
                  cancellationToken).ConfigureAwait(false);
                lock (_channels)
                {
                    _channels.Add(channel);
                }
            }
            finally
            {
                EndPause();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask UnsubscribeAsync(
        string channel,
        CancellationToken cancellationToken = default)
    {
        ValidateChannel(channel);
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            BeginPause();
            try
            {
                await _connection.ExecuteAsync(
                  "UNLISTEN " + QuoteIdentifier(channel),
                  cancellationToken).ConfigureAwait(false);
                lock (_channels)
                {
                    _channels.Remove(channel);
                }
            }
            finally
            {
                EndPause();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _disposeCancellation.CancelAsync().ConfigureAwait(false);
        InterruptWait();
        // ListenAsync never faults: it reports its failure through the Notifications channel.
        await _listenLoop.ConfigureAwait(false);

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _connection.Notification -= OnNotification;
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
            _notifications.Writer.TryComplete();
            _disposeCancellation.Dispose();
            lock (_waitGate)
            {
                _waitCancellation?.Dispose();
                _waitCancellation = null;
            }
        }
    }

    private async Task ListenAsync()
    {
        Exception? error = null;
        try
        {
            await ListenCoreAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Anything that ends the loop (a non-fatal server error, a throwing reconnect policy,
            // a failed reconnect) must reach the Notifications reader instead of leaving it waiting.
            error = exception;
            Volatile.Write(ref _listenError, exception);
        }
        finally
        {
            _notifications.Writer.TryComplete(error);
        }
    }

    private async Task ListenCoreAsync()
    {
        var retry = 0;
        while (!_disposeCancellation.IsCancellationRequested)
        {
            var pause = GetPauseTask();
            if (pause is not null)
            {
                await pause.WaitAsync(_disposeCancellation.Token).ConfigureAwait(false);
                continue;
            }

            var wait = CreateWaitCancellation();
            if (wait is null)
            {
                continue;
            }

            var connection = _connection;
            try
            {
                _ = await connection.WaitForNotificationAsync(wait.Token).ConfigureAwait(false);
                retry = 0;
            }
            catch (OperationCanceledException) when (
              wait.IsCancellationRequested ||
              _disposeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (PgConnection.IsFatalConnectionError(exception))
            {
                var delay = _reconnectPolicy(retry++);
                if (delay is null)
                {
                    throw;
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay.Value, _disposeCancellation.Token).ConfigureAwait(false);
                }

                try
                {
                    await ReconnectAsync(_disposeCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception reconnectError) when (PgConnection.IsFatalConnectionError(reconnectError))
                {
                }
            }
            finally
            {
                ClearWaitCancellation(wait);
            }
        }
    }

    private async ValueTask ReconnectAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _connection.Notification -= OnNotification;
            await _connection.DisposeAsync().ConfigureAwait(false);
            PgConnection? replacement = null;
            try
            {
                replacement =
                  await PgClient.ConnectAsync(_options, cancellationToken).ConfigureAwait(false);
                replacement.Notification += OnNotification;
                string[] channels;
                lock (_channels)
                {
                    channels = _channels.ToArray();
                }

                foreach (var channel in channels)
                {
                    await replacement.ExecuteAsync(
                      "LISTEN " + QuoteIdentifier(channel),
                      cancellationToken).ConfigureAwait(false);
                }

                _connection = replacement;
            }
            catch
            {
                if (replacement is not null)
                {
                    replacement.Notification -= OnNotification;
                    await replacement.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private Task? GetPauseTask()
    {
        lock (_waitGate)
        {
            return _resumeWait?.Task;
        }
    }

    private void BeginPause()
    {
        lock (_waitGate)
        {
            if (_pauseDepth++ == 0)
            {
                _resumeWait = new TaskCompletionSource(
                  TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _waitCancellation?.Cancel();
        }
    }

    private void EndPause()
    {
        TaskCompletionSource? resume = null;
        lock (_waitGate)
        {
            if (--_pauseDepth == 0)
            {
                resume = _resumeWait;
                _resumeWait = null;
            }
        }

        resume?.TrySetResult();
    }

    private CancellationTokenSource? CreateWaitCancellation()
    {
        lock (_waitGate)
        {
            if (_pauseDepth > 0)
            {
                return null;
            }

            _waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
              _disposeCancellation.Token);
            return _waitCancellation;
        }
    }

    private void ClearWaitCancellation(CancellationTokenSource cancellation)
    {
        lock (_waitGate)
        {
            if (ReferenceEquals(_waitCancellation, cancellation))
            {
                _waitCancellation = null;
            }
        }

        cancellation.Dispose();
    }

    private void InterruptWait()
    {
        lock (_waitGate)
        {
            _waitCancellation?.Cancel();
        }
    }

    private void ThrowIfDisposed() =>
      ObjectDisposedException.ThrowIf(_disposed != 0, this);

    // Once the listen loop has failed nothing would deliver notifications for a new channel.
    private void ThrowIfStopped()
    {
        if (Volatile.Read(ref _listenError) is { } error)
        {
            throw new InvalidOperationException(
                "The subscriber stopped listening after an error; create a new subscriber.",
                error);
        }
    }

        private static void ValidateChannel(string channel)
        {
                ArgumentException.ThrowIfNullOrWhiteSpace(channel);
                if (System.Text.Encoding.UTF8.GetByteCount(channel) > 63)
                {
                        throw new ArgumentException(
                            "PostgreSQL channel names cannot exceed 63 UTF-8 bytes.",
                            nameof(channel));
                }
        }

    private static string QuoteIdentifier(string identifier) =>
      "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private void OnNotification(PgNotification notification) =>
      _notifications.Writer.TryWrite(notification);
}

namespace Apex.PgClient;

public static class PgClient
{
    public static async ValueTask<PgConnection> ConnectAsync(
        PgConnectOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ReconnectAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ReconnectInterval, TimeSpan.Zero);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await PgConnection.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
              attempt < options.ReconnectAttempts &&
              IsTransientConnectError(exception) &&
              !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(options.ReconnectInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static ValueTask<PgConnection> ConnectAsync(
        string connectionString,
        CancellationToken cancellationToken = default) =>
      ConnectAsync(PgConnectOptions.Parse(connectionString), cancellationToken);

    public static ValueTask<PgSubscriber> SubscribeAsync(
        PgConnectOptions options,
        Func<int, TimeSpan?>? reconnectPolicy = null,
        CancellationToken cancellationToken = default) =>
      PgSubscriber.ConnectAsync(options, reconnectPolicy, cancellationToken);

        private static bool IsTransientConnectError(Exception exception) =>
            exception is IOException or System.Net.Sockets.SocketException or
                PgException { SqlState: "57P03" or "08001" or "08006" };
}

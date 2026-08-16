namespace Apex.MySqlClient;

/// <summary>Entry points for connecting to MySQL and MariaDB.</summary>
public static class MySqlClient
{
    /// <summary>Opens a connection with the supplied settings.</summary>
    /// <param name="options">How to reach and authenticate against the server.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>An authenticated connection.</returns>
    public static async ValueTask<MySqlConnection> ConnectAsync(
        MySqlConnectOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ReconnectAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ReconnectInterval, TimeSpan.Zero);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await MySqlConnection.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Opens a connection described by a connection string.</summary>
    /// <param name="connectionString">
    /// A <c>mysql://</c> or <c>mariadb://</c> URI, or a semicolon separated keyword string.
    /// </param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>An authenticated connection.</returns>
    public static ValueTask<MySqlConnection> ConnectAsync(
        string connectionString,
        CancellationToken cancellationToken = default) =>
      ConnectAsync(MySqlConnectOptions.Parse(connectionString), cancellationToken);

        private static bool IsTransientConnectError(Exception exception) =>
            exception is IOException or EndOfStreamException or
                System.Net.Sockets.SocketException or MySqlException { IsFatal: true };
}

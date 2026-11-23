namespace Apex.SqlClient;

/// <summary>Configures a bounded pool of reusable SQL connections.</summary>
/// <remarks>
/// The pool validates these values when it is created and throws
/// <see cref="ArgumentOutOfRangeException"/> for any value outside its documented range.
/// Finite timer durations are limited to 4,294,967,294 milliseconds (about 49.7 days),
/// the longest that <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> and
/// <see cref="TimeProvider.CreateTimer"/> accept.
/// </remarks>
public sealed record SqlPoolOptions
{
    /// <summary>The maximum number of physical connections. Must be greater than zero.</summary>
    public int MaximumSize { get; init; } = 4;

    /// <summary>
    /// The maximum number of callers waiting for a connection, or <c>-1</c> for no limit.
    /// Zero rejects every caller that cannot be served immediately.
    /// </summary>
    public int MaximumWaitQueueSize { get; init; } = -1;

    /// <summary>
    /// How long a caller waits for a free connection. Must be greater than zero and at most
    /// about 49.7 days, or <see cref="Timeout.InfiniteTimeSpan"/> to wait
    /// until a connection is free or the caller cancels.
    /// </summary>
    public TimeSpan AcquisitionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long an idle connection is kept before it is closed. Must be greater than zero, or
    /// <see cref="Timeout.InfiniteTimeSpan"/> to keep idle connections indefinitely.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// How long a connection is used, counted from when it was opened, before it is closed
    /// on its next return or cleanup. Must be greater than zero, or
    /// <see cref="Timeout.InfiniteTimeSpan"/> for no limit.
    /// </summary>
    public TimeSpan MaximumLifetime { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// How often idle connections are checked against <see cref="IdleTimeout"/> and
    /// <see cref="MaximumLifetime"/>. Must be greater than zero and at most
    /// about 49.7 days; <see cref="TimeSpan.Zero"/> or
    /// <see cref="Timeout.InfiniteTimeSpan"/> disables periodic cleanup, and expired
    /// connections are then closed only when they are next acquired or returned.
    /// </summary>
    public TimeSpan CleanerPeriod { get; init; } = TimeSpan.FromSeconds(1);
}

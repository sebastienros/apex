namespace Apex.SqlClient;

public sealed record SqlPoolOptions
{
    public int MaximumSize { get; init; } = 4;

    public int MaximumWaitQueueSize { get; init; } = -1;

    public TimeSpan AcquisitionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan IdleTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    public TimeSpan MaximumLifetime { get; init; } = Timeout.InfiniteTimeSpan;

    public TimeSpan CleanerPeriod { get; init; } = TimeSpan.FromSeconds(1);
}

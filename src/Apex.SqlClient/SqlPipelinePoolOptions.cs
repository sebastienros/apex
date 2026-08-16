namespace Apex.SqlClient;

public sealed record SqlPipelinePoolOptions
{
    public int ConnectionCount { get; init; } = Environment.ProcessorCount;
}

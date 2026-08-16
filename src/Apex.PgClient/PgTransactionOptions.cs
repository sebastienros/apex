namespace Apex.PgClient;

public enum PgIsolationLevel
{
    ReadCommitted,
    RepeatableRead,
    Serializable,
}

public sealed record PgTransactionOptions
{
    public PgIsolationLevel IsolationLevel { get; init; } = PgIsolationLevel.ReadCommitted;

    public bool ReadOnly { get; init; }

    public bool Deferrable { get; init; }
}

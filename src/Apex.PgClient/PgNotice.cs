namespace Apex.PgClient;

public sealed record PgNotice(
    string Message,
    string? Severity,
    string? SqlState,
    string? Detail,
    string? Hint);

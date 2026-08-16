namespace Apex.PgClient;

public sealed record PgNotification(int ProcessId, string Channel, string Payload);

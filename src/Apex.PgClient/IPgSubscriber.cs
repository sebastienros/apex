namespace Apex.PgClient;

/// <summary>Manages LISTEN/UNLISTEN subscriptions over a dedicated PostgreSQL connection.</summary>
public interface IPgSubscriber : IAsyncDisposable
{
    IAsyncEnumerable<PgNotification> Notifications { get; }

    IReadOnlyCollection<string> Channels { get; }

    ValueTask SubscribeAsync(string channel, CancellationToken cancellationToken = default);

    ValueTask UnsubscribeAsync(string channel, CancellationToken cancellationToken = default);
}

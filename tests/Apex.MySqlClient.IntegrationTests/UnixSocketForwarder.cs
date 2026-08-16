using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Apex.MySqlClient.IntegrationTests;

internal sealed class UnixSocketForwarder : IAsyncDisposable
{
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly Socket _listener = new(
      AddressFamily.Unix,
      SocketType.Stream,
      ProtocolType.Unspecified);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentBag<Socket> _connections = [];
    private readonly ConcurrentBag<Task> _relays = [];
    private readonly Task _acceptLoop;

    internal UnixSocketForwarder(string targetHost, int targetPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetPort);
        _targetHost = targetHost;
        _targetPort = targetPort;
        SocketPath = "/tmp/apex-mysql-" + Guid.NewGuid().ToString("N") + ".sock";
        _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        _listener.Listen();
        _acceptLoop = AcceptLoopAsync();
    }

    internal string SocketPath { get; }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Dispose();
        foreach (var connection in _connections)
        {
            connection.Dispose();
        }

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }

        await Task.WhenAll(_relays.ToArray()).ConfigureAwait(false);
        _stopping.Dispose();
        File.Delete(SocketPath);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket frontend;
            try
            {
                frontend = await _listener.AcceptAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_stopping.IsCancellationRequested)
            {
                return;
            }

            _connections.Add(frontend);
            _relays.Add(RelayAsync(frontend, _stopping.Token));
        }
    }

    private async Task RelayAsync(Socket frontend, CancellationToken cancellationToken)
    {
        using Socket backend = new(
          AddressFamily.InterNetworkV6,
          SocketType.Stream,
          ProtocolType.Tcp)
        {
            DualMode = true,
            NoDelay = true,
        };
        _connections.Add(backend);
        try
        {
            await backend.ConnectAsync(_targetHost, _targetPort, cancellationToken)
              .ConfigureAwait(false);
            await using NetworkStream frontendStream = new(frontend, ownsSocket: true);
            await using NetworkStream backendStream = new(backend, ownsSocket: false);
            var upstream = frontendStream.CopyToAsync(backendStream, cancellationToken);
            var downstream = backendStream.CopyToAsync(frontendStream, cancellationToken);
            await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
        }
        catch (Exception exception) when (
          cancellationToken.IsCancellationRequested ||
          exception is IOException or SocketException or ObjectDisposedException)
        {
        }
        finally
        {
            frontend.Dispose();
        }
    }
}
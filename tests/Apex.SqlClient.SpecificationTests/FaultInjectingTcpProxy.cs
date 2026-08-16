using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Apex.SqlClient.SpecificationTests;

public sealed class FaultInjectingTcpProxy : IAsyncDisposable
{
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentBag<TcpClient> _connections = [];
    private readonly ConcurrentBag<Task> _relays = [];
    private readonly Task _acceptLoop;
    private int _connectionsToDrop;
    private int _acceptedConnections;
    private int _rejectNewConnections;

    public FaultInjectingTcpProxy(
        string targetHost,
        int targetPort,
        int connectionsToDrop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetPort);
        ArgumentOutOfRangeException.ThrowIfNegative(connectionsToDrop);
        _targetHost = targetHost;
        _targetPort = targetPort;
        _connectionsToDrop = connectionsToDrop;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    public int AcceptedConnections => Volatile.Read(ref _acceptedConnections);

    public void CloseActiveConnections()
    {
        foreach (var connection in _connections)
        {
            connection.Dispose();
        }
    }

    public void RejectNewConnections() =>
      Volatile.Write(ref _rejectNewConnections, 1);

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
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
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient frontend;
            try
            {
                frontend = await _listener.AcceptTcpClientAsync(_stopping.Token)
                  .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException) when (_stopping.IsCancellationRequested)
            {
                return;
            }

            Interlocked.Increment(ref _acceptedConnections);
            if (Volatile.Read(ref _rejectNewConnections) != 0 ||
                Interlocked.Decrement(ref _connectionsToDrop) >= 0)
            {
                frontend.Dispose();
                continue;
            }

            _connections.Add(frontend);
            var relay = RelayAsync(frontend, _stopping.Token);
            _relays.Add(relay);
        }
    }

    private async Task RelayAsync(TcpClient frontend, CancellationToken cancellationToken)
    {
        using var backend = new TcpClient();
        _connections.Add(backend);
        try
        {
            await backend.ConnectAsync(_targetHost, _targetPort, cancellationToken)
              .ConfigureAwait(false);
            await using var frontendStream = frontend.GetStream();
            await using var backendStream = backend.GetStream();
            var upstream = frontendStream.CopyToAsync(backendStream, cancellationToken);
            var downstream = backendStream.CopyToAsync(frontendStream, cancellationToken);
            await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
        }
        catch (Exception exception) when (
          cancellationToken.IsCancellationRequested ||
          exception is IOException or SocketException)
        {
        }
        finally
        {
            frontend.Dispose();
        }
    }
}
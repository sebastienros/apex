using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Apex.MySqlClient.Internal;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MySqlClient;

/// <summary>A direct connection to a MySQL or MariaDB server.</summary>
public sealed partial class MySqlConnection : ISqlConnection
{
    private static readonly Encoding s_utf8 = new UTF8Encoding(false, true);
    private readonly MySqlConnectOptions _options;
    private readonly Socket _socket;
    private readonly Stream _stream;
    private readonly MySqlPacketReader _reader;
    private readonly MySqlPacketWriter _writer;
    private readonly MySqlPayloadWriter _payload = new();
    private readonly Utf8StringCache _strings;
    private readonly BoundedOrderedCommandScheduler _scheduler;
    private readonly object _statementCacheGate = new();
    private readonly LruCache<string, MySqlStatement>? _statementCache;
    private readonly MySqlCapabilities _capabilities;
    private readonly SqlAuthenticationMethod _authenticationMethod;
    private MySqlServerStatus _status = MySqlServerStatus.AutoCommit;
    private MySqlCommandInfo _lastCommandInfo = MySqlCommandInfo.Empty;
    private IReadOnlyList<MySqlColumnMetadata> _lastColumns = Array.Empty<MySqlColumnMetadata>();
    private DatabaseMetadata _databaseMetadata = new("MySQL", "unknown", 0, 0);
    private MySqlServerVersion _serverVersion = new("unknown", 0, 0, 0, false);
    private uint _connectionId;
    private int _broken;
    private bool _disposed;

    private MySqlConnection(
        MySqlConnectOptions options,
        Socket socket,
        Stream stream,
        bool secure,
        MySqlCapabilities capabilities,
        SqlAuthenticationMethod authenticationMethod)
    {
        _options = options;
        _socket = socket;
        _stream = stream;
        IsSecure = secure;
        _capabilities = capabilities;
        _authenticationMethod = authenticationMethod;
        _reader = new MySqlPacketReader(stream);
        _writer = new MySqlPacketWriter(stream);
        _strings = new Utf8StringCache(
          options.StringCacheCapacity,
          options.StringCacheMaximumByteLength);
        _scheduler = new BoundedOrderedCommandScheduler(
          options.PipeliningLimit,
          (int)Math.Max(16, Math.Min(4096, (long)options.PipeliningLimit * 4)),
                    IsFatalConnectionError,
          FlushSchedulerBatchAsync);
        _statementCache = options.CachePreparedStatements && options.PreparedStatementCacheSize > 0
          ? new LruCache<string, MySqlStatement>(options.PreparedStatementCacheSize, StringComparer.Ordinal)
          : null;
    }

    /// <summary>Gets a value indicating whether the transport is protected by TLS.</summary>
    public bool IsSecure { get; }

    /// <summary>Gets the product name and version reported during the handshake.</summary>
    public DatabaseMetadata DatabaseMetadata => _databaseMetadata;

    /// <summary>Gets the parsed server version, including the MariaDB micro version.</summary>
    public MySqlServerVersion ServerVersion => _serverVersion;

    /// <summary>Gets the server side identifier of this session, used by <c>KILL QUERY</c>.</summary>
    public uint ConnectionId => _connectionId;

    private async ValueTask FlushSchedulerBatchAsync(CancellationToken cancellationToken)
    {
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the session status flags reported by the most recent command.</summary>
    public MySqlServerStatus ServerStatus => _status;

    /// <summary>Gets a value indicating whether a transaction is open on the session.</summary>
    public bool InTransaction => (_status & MySqlServerStatus.InTransaction) != 0;

    /// <summary>
    /// Gets the affected rows, generated identifier, warning count and informational message of
    /// the most recently completed command.
    /// </summary>
    public MySqlCommandInfo LastCommandInfo => _lastCommandInfo;

    /// <summary>Gets the MySQL metadata of the columns of the most recent result set.</summary>
    public IReadOnlyList<MySqlColumnMetadata> LastColumns => _lastColumns;

    internal bool IsUsable =>
      !_disposed && !_scheduler.IsStopped && Volatile.Read(ref _broken) == 0 && _socket.Connected;

    internal bool IsReadyForPool =>
      IsUsable &&
      !InTransaction &&
      (_status & MySqlServerStatus.AutoCommit) != 0;

    internal bool DeprecateEof => (_capabilities & MySqlCapabilities.DeprecateEof) != 0;

    /// <inheritdoc />
    public async ValueTask<SqlRowSet> QueryAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var result = await ExecuteQueryCoreAsync(
          sql,
          default,
          cancellationToken).ConfigureAwait(false);
        return result.Rows;
    }

    /// <inheritdoc />
    public async ValueTask<SqlRowSet> QueryAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var result = await ExecuteQueryCoreAsync(
          sql,
          parameters,
          cancellationToken).ConfigureAwait(false);
        return result.Rows;
    }

    /// <inheritdoc />
    public async ValueTask<SqlCommandResult> ExecuteAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var result = await ExecuteQueryCoreAsync(
          sql,
          default,
          cancellationToken).ConfigureAwait(false);
        return result.ToCommandResult();
    }

    /// <inheritdoc />
    public async ValueTask<SqlCommandResult> ExecuteAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var result = await ExecuteQueryCoreAsync(
          sql,
          parameters,
          cancellationToken).ConfigureAwait(false);
        return result.ToCommandResult();
    }

    /// <inheritdoc />
    /// <remarks>
    /// MySQL delivers a result set as a stream of row packets, so rows are handed to the caller
    /// as they arrive and the reader applies backpressure to the connection. The fetch size is
    /// validated but does not change how much the server sends ahead.
    /// </remarks>
    public async IAsyncEnumerable<SqlRow> StreamAsync(
        string sql,
        SqlParameters parameters = default,
        int fetchSize = 50,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fetchSize);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (parameters.Count == 0)
        {
            await foreach (var row in StreamTextRowsAsync(
                             sql,
                             fetchSize,
                             cancellationToken).ConfigureAwait(false))
            {
                yield return row;
            }

            yield break;
        }

        var statement = await GetOrPrepareViaSchedulerAsync(sql, cancellationToken)
          .ConfigureAwait(false);
        await foreach (var row in StreamPreparedRowsAsync(
                         statement,
                         parameters,
                         ownsStatement: !statement.IsCached,
                         fetchSize,
                         cancellationToken).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    /// <inheritdoc />
    public async ValueTask<ISqlPreparedStatement> PrepareAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var statement = await PrepareCoreAsync(sql, cancellationToken).ConfigureAwait(false);
        return new MySqlPreparedStatement(this, statement);
    }

    /// <inheritdoc />
    public ValueTask<ISqlRowReader> ExecuteReaderAsync(
        string sql,
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return parameters.Count == 0
          ? ValueTask.FromResult<ISqlRowReader>(CreateTextReader(sql, cancellationToken))
          : ExecutePreparedReaderCoreAsync(sql, parameters, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<ISqlTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (InTransaction)
        {
            throw new InvalidOperationException("A transaction is already active.");
        }

        await ExecuteTransactionControlAsync("START TRANSACTION", cancellationToken)
          .ConfigureAwait(false);
        return new MySqlTransaction(this);
    }

    /// <summary>Sends COM_PING to verify that the session is alive.</summary>
    /// <param name="cancellationToken">Cancels the command before it is submitted.</param>
    public async ValueTask PingAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _scheduler.ExecuteAsync(
          async token =>
          {
              token.ThrowIfCancellationRequested();
              _writer.WriteCommand(MySqlCommand.Ping);
              await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
          },
          async _ =>
          {
              using var packet = await _reader.ReadAsync(CancellationToken.None)
            .ConfigureAwait(false);
              HandleCompletionPacket(packet.Span);
              return true;
          },
          barrier: true,
          cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends COM_RESET_CONNECTION, clearing session state and prepared statements.</summary>
    /// <param name="cancellationToken">Cancels the command before it is submitted.</param>
    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _scheduler.ExecuteAsync(
          async token =>
          {
              token.ThrowIfCancellationRequested();
              _writer.WriteCommand(MySqlCommand.ResetConnection);
              await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
          },
          async _ =>
          {
              using var packet = await _reader.ReadAsync(CancellationToken.None)
            .ConfigureAwait(false);
              HandleCompletionPacket(packet.Span);
              return true;
          },
          barrier: true,
          cancellationToken).ConfigureAwait(false);
        ClearStatementCache();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (Volatile.Read(ref _broken) == 0)
            {
                await _scheduler.ExecuteAsync(
                  async token =>
                  {
                      token.ThrowIfCancellationRequested();
                      _writer.WriteCommand(MySqlCommand.Quit);
                      await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                  },
                  static _ => ValueTask.FromResult(true),
                  barrier: true).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (!_socket.Connected || IsFatalConnectionError(exception))
        {
        }
        finally
        {
            await _scheduler.DisposeAsync().ConfigureAwait(false);
            await _reader.CompleteAsync().ConfigureAwait(false);
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _socket.Dispose();
                _payload.Release();
                _strings.Disable();
            }
        }
    }

    internal static bool IsFatalConnectionError(Exception exception) =>
      exception is IOException or
        SocketException or
        InvalidDataException or
        EndOfStreamException or
        AuthenticationException or
        ObjectDisposedException or
        MySqlConnectionAbortedException ||
      exception is MySqlException { IsFatal: true };

    /// <summary>Marks the connection unusable so it is discarded instead of returned to a pool.</summary>
    internal void Invalidate(Exception reason)
    {
        if (Interlocked.Exchange(ref _broken, 1) != 0)
        {
            return;
        }

        _scheduler.Fault(reason);
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _socket.Dispose();
    }

    private static Socket CreateSocket(MySqlConnectOptions options)
    {
        if (IsUnixSocket(options))
        {
            return new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        }

        return new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            DualMode = true,
        };
    }

    private static bool IsUnixSocket(MySqlConnectOptions options) =>
      options.Host.Length > 0 && options.Host[0] == '/';

    private static async ValueTask<Stream> UpgradeToTlsAsync(
        Stream stream,
        MySqlConnectOptions options,
        CancellationToken cancellationToken)
    {
        var validation =
          options.CertificateValidationCallback ??
          options.SslMode switch
          {
              MySqlSslMode.Preferred or MySqlSslMode.Required => static (_, _, _, _) => true,
              MySqlSslMode.VerifyCa => VerifyCertificateAuthority,
              _ => null,
          };

        SslClientAuthenticationOptions authenticationOptions = new()
        {
            TargetHost = IsUnixSocket(options) ? "localhost" : options.Host,
            EnabledSslProtocols = SslProtocols.None,
            ClientCertificates = options.ClientCertificates.Count == 0
              ? null
              : new X509CertificateCollection(options.ClientCertificates.ToArray()),
            CertificateRevocationCheckMode = options.CertificateRevocationCheckMode,
            RemoteCertificateValidationCallback = validation,
        };

        if (options.UseExperimentalLowLevelTls)
        {
#if NET11_0_OR_GREATER
            return await LowLevelTlsStream.AuthenticateAsClientAsync(
                stream,
                authenticationOptions,
                cancellationToken).ConfigureAwait(false);
#else
            throw new PlatformNotSupportedException(
              "Experimental low-level TLS requires .NET 11 or later.");
#endif
        }

        SslStream ssl = new(stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(
              authenticationOptions,
              cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return ssl;
    }

    private static bool VerifyCertificateAuthority(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors) =>
      certificate is not null &&
      chain is not null &&
      (errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) == SslPolicyErrors.None;

    private static string GetOperation(string sql)
    {
        var text = sql.AsSpan().TrimStart();
        var separator = text.IndexOfAny(" \t\r\n");
        return (separator < 0 ? text : text[..separator]).ToString().ToUpperInvariant();
    }

    private static void ValidateOptions(MySqlConnectOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Username);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Port);
        if (options.Port > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Port must be at most 65535.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PipeliningLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PreparedStatementCacheSize);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PreparedStatementCacheSqlLengthLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(options.StringCacheCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(options.StringCacheMaximumByteLength);
        if (options.ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Connect timeout must be positive.");
        }
        if (options.StringCacheCapacity > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
              nameof(options),
              "String cache capacity must be at most 1,048,576.");
        }

        if (options.StringCacheMaximumByteLength > 4096)
        {
            throw new ArgumentOutOfRangeException(
              nameof(options),
              "Cached strings must be at most 4,096 UTF-8 bytes.");
        }

        if (options.Collation == 0)
        {
            throw new ArgumentException("A collation identifier is required.", nameof(options));
        }

        if (options.AuthenticationPlugin == MySqlAuthenticationPlugin.ClearPassword &&
            !options.AllowCleartextPassword)
        {
            throw new ArgumentException(
              "mysql_clear_password requires AllowCleartextPassword to be enabled.",
              nameof(options));
        }

        if (options.AllowCleartextPassword && options.SslMode is MySqlSslMode.Disabled)
        {
            throw new ArgumentException(
              "Cleartext passwords require TLS.",
              nameof(options));
        }
    }
}

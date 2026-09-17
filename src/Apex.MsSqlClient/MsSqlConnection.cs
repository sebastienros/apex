using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Apex.MsSqlClient.Internal;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.MsSqlClient;

public sealed class MsSqlConnection : ISqlConnection, IApexAdoReaderConnection
{
    private readonly MsSqlConnectOptions _options;
    private readonly TdsFedAuthLogin? _fedAuth;
    private readonly Socket _socket;
    private readonly Stream _stream;
    private readonly TdsPacketReader _reader;
    private readonly TdsPacketWriter _writer;
    private readonly MsSqlRowDecoder _rowDecoder;
    private readonly BoundedOrderedCommandScheduler _scheduler;
    private readonly object _attentionGate = new();
    private AttentionState? _activeAttention;
    private DatabaseMetadata _databaseMetadata =
      new("Microsoft SQL Server", "unknown", 0, 0);
    private string _database;
    private long _transactionDescriptor;
    private bool _broken;
    private bool _disposed;

    private MsSqlConnection(
        MsSqlConnectOptions options,
        Socket socket,
        Stream stream,
        bool secure,
        Version? preLoginVersion,
        TdsFedAuthLogin? fedAuth)
    {
        _options = options;
        _fedAuth = fedAuth;
        _socket = socket;
        _stream = stream;
        _reader = new TdsPacketReader(stream);
        _writer = new TdsPacketWriter(stream, options.PacketSize);
        _rowDecoder = new MsSqlRowDecoder(
          options.StringCacheCapacity,
          options.StringCacheMaximumByteLength);
        _scheduler = new BoundedOrderedCommandScheduler(
          inFlightLimit: 1,
          queueCapacity: 64,
          IsFatalConnectionError);
        _database = options.Database;
        IsSecure = secure;
        if (preLoginVersion is not null)
        {
            _databaseMetadata = new DatabaseMetadata(
              "Microsoft SQL Server",
              preLoginVersion.ToString(),
              preLoginVersion.Major,
              preLoginVersion.Minor);
        }
    }

    public event Action<MsSqlInfo>? InfoMessage;

    public bool IsSecure { get; }

    public DatabaseMetadata DatabaseMetadata => _databaseMetadata;

    internal bool IsUsable =>
      !_disposed && !_broken && !_scheduler.IsStopped && _socket.Connected;

    internal bool IsReadyForPool => IsUsable && _transactionDescriptor == 0;

    internal BoundedOrderedCommandScheduler Scheduler => _scheduler;

    internal TdsPacketReader Reader => _reader;

    internal MsSqlRowDecoder RowDecoder => _rowDecoder;

    internal static async ValueTask<MsSqlConnection> ConnectAsync(
        MsSqlConnectOptions options,
        CancellationToken cancellationToken)
    {
        ValidateOptions(options);
        using CancellationTokenSource timeout =
          CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.ConnectTimeout > TimeSpan.Zero)
        {
            timeout.CancelAfter(options.ConnectTimeout);
        }

        var current = options;
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            (var resolved, var accessToken) =
              await ResolveCredentialAsync(current, timeout.Token).ConfigureAwait(false);
            var connection = await ConnectPhysicalAsync(resolved, accessToken, timeout.Token)
              .ConfigureAwait(false);
            try
            {
                var routing =
                  await connection.InitializeAsync(timeout.Token).ConfigureAwait(false);
                if (routing is null)
                {
                    return connection;
                }

                if (redirect == 3)
                {
                    throw new InvalidDataException(
                      "SQL Server exceeded the maximum of three routing redirects.");
                }

                current = current with
                {
                    Host = routing.Value.Host,
                    Port = routing.Value.Port,
                };
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }

        throw new InvalidOperationException("Unreachable SQL Server routing state.");
    }

    public ValueTask<SqlRowSet> QueryAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return ExecuteQueryCoreAsync(sql, default, cancellationToken);
    }

    public ValueTask<SqlRowSet> QueryAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return ExecuteQueryCoreAsync(sql, parameters, cancellationToken);
    }

    public async ValueTask<SqlCommandResult> ExecuteAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync(sql, cancellationToken).ConfigureAwait(false);
        return new SqlCommandResult(result.AffectedRows, result.CommandTag);
    }

    public async ValueTask<SqlCommandResult> ExecuteAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken = default)
    {
        var result =
          await QueryAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        return new SqlCommandResult(result.AffectedRows, result.CommandTag);
    }

    public async IAsyncEnumerable<SqlRow> StreamAsync(
        string sql,
        SqlParameters parameters = default,
        int fetchSize = 50,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fetchSize);
        await foreach (var row in StreamRowsAsync(
                         new MsSqlRowReader(this, sql, parameters, cancellationToken),
                         fetchSize,
                         cancellationToken).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    internal async IAsyncEnumerable<SqlRow> StreamRowsAsync(
        MsSqlRowReader reader,
        int fetchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pageCapacity = Math.Min(fetchSize, 256);
        await using (reader.ConfigureAwait(false))
        {
            SqlRowPageBuilder? page = null;
            IReadOnlyList<SqlColumn>? pageColumns = null;
            var pageGeneration = 0;
            while (true)
            {
                if (page?.Count == pageCapacity)
                {
                    var fullBatch = page.BuildBatch(pageColumns!);
                    page = null;
                    for (var i = 0; i < fullBatch.Count; i++)
                    {
                        yield return fullBatch.CreateRow(i);
                    }

                    continue;
                }

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (page is not null)
                    {
                        var finalBatch = page.BuildBatch(pageColumns!);
                        for (var i = 0; i < finalBatch.Count; i++)
                        {
                            yield return finalBatch.CreateRow(i);
                        }
                    }

                    yield break;
                }

                var currentGeneration = reader.ResultSetGeneration;
                if (page is not null && currentGeneration != pageGeneration)
                {
                    var completedBatch = page.BuildBatch(pageColumns!);
                    page = null;
                    for (var i = 0; i < completedBatch.Count; i++)
                    {
                        yield return completedBatch.CreateRow(i);
                    }
                }

                if (page is null)
                {
                    page = new SqlRowPageBuilder(
                      _rowDecoder,
                      rowCapacity: pageCapacity,
                      byteCapacity: Math.Max(256, pageCapacity * 16));
                    pageColumns = reader.Columns;
                    pageGeneration = currentGeneration;
                }

                reader.CopyCurrentTo(page);
            }
        }
    }

    public ValueTask<ISqlPreparedStatement> PrepareAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ISqlPreparedStatement>(
          new MsSqlPreparedStatement(this, sql));
    }

    public ValueTask<ISqlRowReader> ExecuteReaderAsync(
        string sql,
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return ValueTask.FromResult<ISqlRowReader>(
          new MsSqlRowReader(this, sql, parameters, cancellationToken));
    }

    internal ValueTask<ISqlRowReader> ExecuteAdoReaderAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return ValueTask.FromResult<ISqlRowReader>(
          new MsSqlRowReader(
            this,
            sql,
            parameters,
            cancellationToken,
            adoResultBoundaries: true));
    }

    ValueTask<ISqlRowReader> IApexAdoReaderConnection.ExecuteAdoReaderAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken) =>
        ExecuteAdoReaderAsync(sql, parameters, cancellationToken);

    public async ValueTask<ISqlTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Read(ref _transactionDescriptor) != 0)
        {
            throw new InvalidOperationException("A transaction is already active.");
        }

        await ExecuteTransactionControlAsync(
          "BEGIN TRANSACTION",
          cancellationToken).ConfigureAwait(false);
        if (Interlocked.Read(ref _transactionDescriptor) == 0)
        {
            MarkBroken();
            throw new InvalidDataException(
              "SQL Server did not return a transaction descriptor after BEGIN TRANSACTION.");
        }

        return new MsSqlTransaction(this);
    }

    public async ValueTask CancelRequestAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AttentionState attention;
        lock (_attentionGate)
        {
            attention = _activeAttention ??
              throw new InvalidOperationException("There is no active SQL Server command to cancel.");
            attention.Cancel(cancellationToken);
        }

        await attention.GetSendTask().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await _scheduler.ExecuteAsync(
              static _ => ValueTask.CompletedTask,
              static _ => ValueTask.FromResult(true),
              barrier: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFatalConnectionError(exception))
        {
        }
        finally
        {
            await _scheduler.DisposeAsync().ConfigureAwait(false);
            _rowDecoder.DisableCache();
            _writer.Dispose();
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _socket.Dispose();
            }
        }
    }

    internal ValueTask WriteRequestAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> payload;
        byte messageType;
        if (parameters.Count == 0)
        {
            payload = TdsRequestWriter.BuildSqlBatch(
              sql,
              Interlocked.Read(ref _transactionDescriptor));
            messageType = TdsMessageType.SqlBatch;
        }
        else
        {
            payload = TdsRequestWriter.BuildExecuteSql(
              sql,
              parameters,
              Interlocked.Read(ref _transactionDescriptor));
            messageType = TdsMessageType.Rpc;
        }

        return _writer.WriteMessageAsync(messageType, payload, cancellationToken);
    }

    internal async ValueTask<bool> WritePreparedRequestAsync(
        MsSqlPreparedStatement statement,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        var payload = statement.BuildRequest(
          parameters,
          Interlocked.Read(ref _transactionDescriptor),
          out var preparesHandle);
        await _writer.WriteMessageAsync(
          TdsMessageType.Rpc,
          payload,
          cancellationToken).ConfigureAwait(false);
        return preparesHandle;
    }

    internal async ValueTask<SqlRowSet> ExecutePreparedAsync(
        MsSqlPreparedStatement statement,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var operation = statement.Operation;
        using var activity = SqlClientDiagnostics.StartQuery(
          "sqlserver",
          _database,
          _options.Host,
          _options.Port,
          operation);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Exception? error = null;
        var preparesHandle = false;
        try
        {
            return await _scheduler.ExecuteAsync(
              async token =>
              {
                  token.ThrowIfCancellationRequested();
                  preparesHandle = await WritePreparedRequestAsync(
              statement,
              parameters,
              CancellationToken.None).ConfigureAwait(false);
              },
              _ => ReceiveQueryAsync(
                cancellationToken,
                preparesHandle ? statement : null),
              barrier: true,
              cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = exception;
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, exception.Message);
            throw;
        }
        finally
        {
            SqlClientDiagnostics.RecordQuery(
              System.Diagnostics.Stopwatch.GetElapsedTime(started),
              "sqlserver",
              operation,
              error);
        }
    }

    internal async ValueTask ClosePreparedAsync(MsSqlPreparedStatement statement)
    {
        if (_disposed)
        {
            statement.MarkUnprepared(statement.GetHandleForClose());
            return;
        }

        var sent = false;
        var handle = 0;
        await _scheduler.ExecuteAsync(
          async token =>
          {
              token.ThrowIfCancellationRequested();
              handle = statement.GetHandleForClose();
              if (handle > 0)
              {
                  var payload = TdsRequestWriter.BuildUnprepare(
                handle,
                Interlocked.Read(ref _transactionDescriptor));
                  await _writer.WriteMessageAsync(
                TdsMessageType.Rpc,
                payload,
                CancellationToken.None).ConfigureAwait(false);
                  sent = true;
              }
          },
          async receiveToken =>
          {
              receiveToken.ThrowIfCancellationRequested();
              if (sent)
              {
                  await ReceiveQueryAsync(CancellationToken.None).ConfigureAwait(false);
                  statement.MarkUnprepared(handle);
              }

              return true;
          },
          barrier: true).ConfigureAwait(false);
    }

    internal async ValueTask ExecuteTransactionControlAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        _ = await ExecuteQueryCoreAsync(sql, default, cancellationToken).ConfigureAwait(false);
    }

    internal void HandleEnvironmentChange(TdsEnvironmentChangeInfo change)
    {
        if (change.Database is not null)
        {
            _database = change.Database;
        }

        if (change.PacketSize is int packetSize)
        {
            _writer.PacketSize = packetSize;
        }

        if (change.TransactionDescriptor is long transactionDescriptor)
        {
            Interlocked.Exchange(ref _transactionDescriptor, transactionDescriptor);
        }
    }

    internal void InvokeInfo(MsSqlInfo info) => InvokeSafely(InfoMessage, info);

    internal static MsSqlException CreateException(IReadOnlyList<MsSqlInfo> errors)
    {
        var first = errors[0];
        return new MsSqlException(
          first.Number,
          first.State,
          first.Severity,
          first.Message,
          first.ServerName,
          first.ProcedureName,
          first.LineNumber,
          errors);
    }

    internal static void ProcessAncillaryToken(
        byte token,
        TdsTokenReader reader,
        MsSqlConnection connection,
        List<MsSqlInfo> errors)
    {
        switch (token)
        {
            case TdsTokenType.Info:
                connection.InvokeInfo(reader.ReadMessage());
                break;
            case TdsTokenType.Error:
                errors.Add(reader.ReadMessage());
                break;
            case TdsTokenType.EnvironmentChange:
                connection.HandleEnvironmentChange(reader.ReadEnvironmentChange());
                break;
            case TdsTokenType.ReturnStatus:
                reader.SkipReturnStatus();
                break;
            case TdsTokenType.ReturnValue:
                reader.SkipReturnValue();
                break;
            case TdsTokenType.Order:
            case TdsTokenType.TableName:
            case TdsTokenType.ColumnInfo:
            case TdsTokenType.Sspi:
                reader.SkipUShortLengthToken();
                break;
            case TdsTokenType.SessionState:
            case TdsTokenType.FedAuthInfo:
                reader.SkipUIntLengthToken();
                break;
            case TdsTokenType.FeatureExtAck:
                reader.SkipFeatureExtAck();
                break;
            case TdsTokenType.LoginAck:
                _ = reader.ReadLoginAck();
                break;
            default:
                throw new NotSupportedException(
                  $"SQL Server response token 0x{token:X2} is not supported.");
        }
    }

    internal static async ValueTask ProcessAncillaryTokenAsync(
        byte token,
        TdsStreamingTokenReader reader,
        MsSqlConnection connection,
        List<MsSqlInfo> errors)
    {
        switch (token)
        {
            case TdsTokenType.Info:
                connection.InvokeInfo(await reader.ReadMessageAsync().ConfigureAwait(false));
                break;
            case TdsTokenType.Error:
                errors.Add(await reader.ReadMessageAsync().ConfigureAwait(false));
                break;
            case TdsTokenType.EnvironmentChange:
                connection.HandleEnvironmentChange(
                  await reader.ReadEnvironmentChangeAsync().ConfigureAwait(false));
                break;
            case TdsTokenType.ReturnStatus:
                await reader.SkipReturnStatusAsync().ConfigureAwait(false);
                break;
            case TdsTokenType.ReturnValue:
                await reader.SkipReturnValueAsync().ConfigureAwait(false);
                break;
            case TdsTokenType.Order:
            case TdsTokenType.TableName:
            case TdsTokenType.ColumnInfo:
            case TdsTokenType.Sspi:
                await reader.SkipUShortLengthTokenAsync().ConfigureAwait(false);
                break;
            case TdsTokenType.SessionState:
            case TdsTokenType.FedAuthInfo:
                await reader.SkipUIntLengthTokenAsync().ConfigureAwait(false);
                break;
            case TdsTokenType.FeatureExtAck:
                await reader.SkipFeatureExtAckAsync().ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException(
                  $"SQL Server response token 0x{token:X2} is not supported.");
        }
    }

    internal AttentionState BeginAttention(CancellationToken cancellationToken)
    {
        AttentionState attention = new(this, cancellationToken, _options.ConnectTimeout);
        lock (_attentionGate)
        {
            if (_activeAttention is not null)
            {
                attention.Dispose();
                throw new InvalidOperationException(
                  "A SQL Server command is already active on this connection.");
            }

            _activeAttention = attention;
        }

        return attention;
    }

    internal void EndAttention(AttentionState attention)
    {
        lock (_attentionGate)
        {
            if (ReferenceEquals(_activeAttention, attention))
            {
                _activeAttention = null;
            }
        }
    }

    internal void MarkBroken()
    {
        _broken = true;
        _socket.Dispose();
    }

    private static async ValueTask<MsSqlConnection> ConnectPhysicalAsync(
        MsSqlConnectOptions options,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        Socket socket = new(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
        {
            DualMode = true,
            NoDelay = true,
        };
        try
        {
            await socket.ConnectAsync(options.Host, options.Port, cancellationToken)
              .ConfigureAwait(false);
            Stream stream = new NetworkStream(socket, ownsSocket: false);
            var secure = false;
            if (options.EncryptionMode == MsSqlEncryptionMode.Strict)
            {
                stream = await UpgradeToTlsAsync(
                  stream,
                  options,
                  encapsulateHandshake: false,
                  cancellationToken).ConfigureAwait(false);
                secure = true;
            }

            var preLogin = await ExchangePreLoginAsync(
              stream,
              options.EncryptionMode == MsSqlEncryptionMode.Disable
                ? TdsEncryptionLevel.NotSupported
                : TdsEncryptionLevel.On,
              options.PacketSize,
              requestFederatedAuthentication: accessToken is not null,
              cancellationToken).ConfigureAwait(false);

            if (options.EncryptionMode != MsSqlEncryptionMode.Strict)
            {
                var serverRequiresTls =
                  preLogin.EncryptionLevel is TdsEncryptionLevel.On or TdsEncryptionLevel.Required;
                if (options.EncryptionMode == MsSqlEncryptionMode.Disable && serverRequiresTls)
                {
                    throw new AuthenticationException(
                      "SQL Server requires encryption, but encryption was explicitly disabled.");
                }

                if (serverRequiresTls)
                {
                    stream = await UpgradeToTlsAsync(
                      stream,
                      options,
                      encapsulateHandshake: true,
                      cancellationToken).ConfigureAwait(false);
                    secure = true;
                }
                else if (preLogin.EncryptionLevel == TdsEncryptionLevel.Off &&
                         options.EncryptionMode != MsSqlEncryptionMode.Disable)
                {
                    throw new AuthenticationException(
                      "SQL Server negotiated login-only TLS, which this driver rejects to avoid " +
                      "an unsafe TLS-to-plaintext downgrade. Require full-session encryption.");
                }
                else if (options.EncryptionMode == MsSqlEncryptionMode.Require)
                {
                    throw new AuthenticationException(
                      "SQL Server does not support the required full-session encryption.");
                }
            }

            return new MsSqlConnection(
              options,
              socket,
              stream,
              secure,
              preLogin.ServerVersion,
              CreateFedAuthLogin(accessToken, secure, preLogin));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async ValueTask<(MsSqlConnectOptions Options, string? AccessToken)>
      ResolveCredentialAsync(
        MsSqlConnectOptions options,
        CancellationToken cancellationToken)
    {
        if (options.AuthenticationProvider is null)
        {
            return (options, null);
        }

        var credential = await options.AuthenticationProvider(cancellationToken)
          .ConfigureAwait(false) ??
          throw new InvalidOperationException(
            "The SQL Server authentication provider returned no credential.");
        var bearer = credential.Method == SqlAuthenticationMethod.BearerToken;
        var resolved = options with
        {
            Username = credential.Username ?? options.Username,
            Password = bearer ? string.Empty : credential.Secret,
        };
        if (!bearer)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(resolved.Username);
            return (resolved, null);
        }

        if (resolved.EncryptionMode is not (MsSqlEncryptionMode.Require or
            MsSqlEncryptionMode.Strict))
        {
            throw new AuthenticationException(
              "SQL Server bearer token authentication requires full-session encryption; " +
              "use Encrypt=true or Encrypt=strict.");
        }

        if (resolved.TrustServerCertificate)
        {
            throw new AuthenticationException(
              "SQL Server bearer token authentication requires server certificate validation; " +
              "TrustServerCertificate must be false.");
        }

        return (resolved, credential.Secret);
    }

    private static TdsFedAuthLogin? CreateFedAuthLogin(
        string? accessToken,
        bool secure,
        TdsPreLoginResponse preLogin)
    {
        if (accessToken is null)
        {
            return null;
        }

        if (!secure)
        {
            throw new AuthenticationException(
              "SQL Server bearer token authentication requires an encrypted connection.");
        }

        return new TdsFedAuthLogin(
          accessToken,
          preLogin.FederatedAuthenticationRequired,
          preLogin.Nonce);
    }

    private static async ValueTask<TdsPreLoginResponse> ExchangePreLoginAsync(
        Stream stream,
        TdsEncryptionLevel requestedLevel,
        int packetSize,
        bool requestFederatedAuthentication,
        CancellationToken cancellationToken)
    {
        using TdsPacketWriter writer = new(stream, packetSize);
        TdsPacketReader reader = new(stream);
        await writer.WriteMessageAsync(
          TdsMessageType.PreLogin,
          TdsPreLogin.Encode(requestedLevel, requestFederatedAuthentication),
          cancellationToken).ConfigureAwait(false);
        var response =
          await reader.ReadMessageAsync(cancellationToken).ConfigureAwait(false);
        if (response.Type != TdsMessageType.TabularResult)
        {
            throw new InvalidDataException(
              $"Expected SQL Server PRELOGIN response, received TDS type 0x{response.Type:X2}.");
        }

        return TdsPreLogin.Parse(response.Payload.Span);
    }

    private static async ValueTask<Stream> UpgradeToTlsAsync(
        Stream stream,
        MsSqlConnectOptions options,
        bool encapsulateHandshake,
        CancellationToken cancellationToken)
    {
        var handshakeStream = encapsulateHandshake
          ? new TdsTlsHandshakeStream(stream, options.PacketSize)
          : null;
        var transport = handshakeStream ?? stream;
        var validation =
          options.CertificateValidationCallback ??
          (options.TrustServerCertificate ? static (_, _, _, _) => true : null);
        var certificates = options.ClientCertificates.Count == 0
          ? null
          : new X509CertificateCollection(options.ClientCertificates.ToArray());
        SslClientAuthenticationOptions authentication = new()
        {
            TargetHost = options.TlsHostName ?? options.Host,
            EnabledSslProtocols = SslProtocols.None,
            CertificateRevocationCheckMode = options.CertificateRevocationCheckMode,
            ClientCertificates = certificates,
            RemoteCertificateValidationCallback = validation,
        };
        if (options.EncryptionMode == MsSqlEncryptionMode.Strict)
        {
            authentication.ApplicationProtocols = [new SslApplicationProtocol("tds/8.0")];
        }

        if (options.UseExperimentalLowLevelTls)
        {
#if NET11_0_OR_GREATER
            LowLevelTlsStream tls = await LowLevelTlsStream.AuthenticateAsClientAsync(
                transport,
                authentication,
                cancellationToken).ConfigureAwait(false);
            ValidateStrictTlsAlpn(tls.NegotiatedApplicationProtocol, options);
            handshakeStream?.SwitchToRaw();
            return tls;
#else
            throw new PlatformNotSupportedException(
              "Experimental low-level TLS requires .NET 11 or later.");
#endif
        }

        SslStream ssl = new(transport, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(authentication, cancellationToken)
          .ConfigureAwait(false);
        ValidateStrictTlsAlpn(ssl.NegotiatedApplicationProtocol, options);

        handshakeStream?.SwitchToRaw();
        return ssl;
    }

    private static void ValidateStrictTlsAlpn(
        SslApplicationProtocol negotiatedProtocol,
        MsSqlConnectOptions options)
    {
        if (options.EncryptionMode == MsSqlEncryptionMode.Strict &&
            negotiatedProtocol != new SslApplicationProtocol("tds/8.0"))
        {
            throw new AuthenticationException(
              "SQL Server strict encryption did not negotiate the 'tds/8.0' ALPN protocol.");
        }
    }

    private async ValueTask<MsSqlRoutingInfo?> InitializeAsync(
        CancellationToken cancellationToken)
    {
        await _writer.WriteMessageAsync(
          TdsMessageType.Login7,
          TdsLogin7.Encode(_options, _fedAuth),
          cancellationToken).ConfigureAwait(false);
        List<MsSqlInfo> errors = [];
        var loginAcknowledged = false;
        var fedAuthAcknowledged = false;
        var fedAuthTokenSent = false;
        MsSqlRoutingInfo? routing = null;
        var final = false;
        while (!final)
        {
            var message =
              await _reader.ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            if (message.Type != TdsMessageType.TabularResult)
            {
                throw new InvalidDataException(
                  $"Expected SQL Server LOGIN response, received TDS type 0x{message.Type:X2}.");
            }

            TdsTokenReader tokens = new(message.Payload);
            var fedAuthInfoRequested = false;
            var tokenCount = 0;
            while (tokens.HasRemaining)
            {
                var token = tokens.ReadTokenType();
                tokenCount++;
                switch (token)
                {
                    case TdsTokenType.LoginAck:
                        var login = tokens.ReadLoginAck();
                        loginAcknowledged = true;
                        _databaseMetadata = new DatabaseMetadata(
                          "Microsoft SQL Server",
                          login.ProductVersion.ToString(),
                          login.ProductVersion.Major,
                          login.ProductVersion.Minor);
                        break;
                    case TdsTokenType.EnvironmentChange:
                        var change = tokens.ReadEnvironmentChange();
                        HandleEnvironmentChange(change);
                        routing ??= change.Routing;
                        break;
                    case TdsTokenType.Info:
                        InvokeInfo(tokens.ReadMessage());
                        break;
                    case TdsTokenType.Error:
                        errors.Add(tokens.ReadMessage());
                        break;
                    case TdsTokenType.FedAuthInfo:
                        _ = tokens.ReadFedAuthInfo();
                        fedAuthInfoRequested = true;
                        break;
                    case TdsTokenType.FeatureExtAck:
                        var acknowledgement = tokens.ReadFeatureExtAck();
                        if (acknowledgement.FedAuthAcknowledged)
                        {
                            ValidateFedAuthAcknowledgement(acknowledgement);
                            fedAuthAcknowledged = true;
                        }

                        break;
                    case TdsTokenType.Done:
                    case TdsTokenType.DoneProc:
                    case TdsTokenType.DoneInProc:
                        var done = tokens.ReadDone();
                        final |= (done.Status & TdsDoneStatus.More) == 0;
                        break;
                    case TdsTokenType.Sspi:
                        tokens.SkipUShortLengthToken();
                        throw new AuthenticationException(
                          "SQL Server requested integrated authentication; only SQL authentication is supported.");
                    default:
                        throw new NotSupportedException(
                          $"SQL Server login token 0x{token:X2} is not supported.");
                }
            }

            if (!fedAuthInfoRequested)
            {
                continue;
            }

            if (tokenCount != 1)
            {
                throw new InvalidDataException(
                  "SQL Server sent FEDAUTHINFO with other tokens in the same message.");
            }

            if (_fedAuth is not { } fedAuth)
            {
                throw new AuthenticationException(
                  "SQL Server requested a federated authentication token, but the connection " +
                  "does not use bearer token authentication.");
            }

            if (fedAuthTokenSent)
            {
                throw new InvalidDataException(
                  "SQL Server requested a federated authentication token more than once.");
            }

            fedAuthTokenSent = true;
            await _writer.WriteMessageAsync(
              TdsMessageType.FedAuthToken,
              TdsFedAuth.EncodeTokenMessage(fedAuth),
              cancellationToken).ConfigureAwait(false);
        }

        if (errors.Count > 0)
        {
            throw CreateException(errors);
        }

        if (!loginAcknowledged)
        {
            throw new AuthenticationException(
              "SQL Server completed LOGIN7 without a LOGINACK token.");
        }

        if (_fedAuth is not null && routing is null && !fedAuthAcknowledged)
        {
            throw new AuthenticationException(
              "SQL Server did not acknowledge the FEDAUTH feature extension.");
        }

        return routing;
    }

    private void ValidateFedAuthAcknowledgement(TdsFeatureExtAck acknowledgement)
    {
        if (_fedAuth is null)
        {
            throw new InvalidDataException(
              "SQL Server acknowledged the FEDAUTH feature that was never requested.");
        }

        if (acknowledgement.FedAuthDataLength != 0)
        {
            throw new InvalidDataException(
              "SQL Server acknowledged the FEDAUTH feature with unexpected data.");
        }
    }

    private async ValueTask<SqlRowSet> ExecuteQueryCoreAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var operation = GetOperation(sql);
        using var activity = SqlClientDiagnostics.StartQuery(
          "sqlserver",
          _database,
          _options.Host,
          _options.Port,
          operation);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Exception? error = null;
        try
        {
            return await _scheduler.ExecuteAsync(
              async token =>
              {
                  token.ThrowIfCancellationRequested();
                  await WriteRequestAsync(sql, parameters, CancellationToken.None)
              .ConfigureAwait(false);
              },
              _ => ReceiveQueryAsync(cancellationToken),
              barrier: true,
              cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = exception;
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, exception.Message);
            throw;
        }
        finally
        {
            SqlClientDiagnostics.RecordQuery(
              System.Diagnostics.Stopwatch.GetElapsedTime(started),
              "sqlserver",
              operation,
              error);
        }
    }

    private async ValueTask<SqlRowSet> ReceiveQueryAsync(
        CancellationToken cancellationToken,
        MsSqlPreparedStatement? preparingStatement = null)
    {
        using var attention = BeginAttention(cancellationToken);
        TdsQueryParser parser = new(_rowDecoder);
        Action<TdsReturnValue>? returnValueHandler = preparingStatement is null
          ? null
          : preparingStatement.CaptureReturnValue;
        try
        {
            while (true)
            {
                var message = await _reader.ReadMessageAsync(
                  attention.ReadCancellationToken)
                  .ConfigureAwait(false);
                if (message.Type != TdsMessageType.TabularResult)
                {
                    throw new InvalidDataException(
                      $"Expected SQL Server result, received TDS type 0x{message.Type:X2}.");
                }

                var response = parser.Parse(
                  message.Payload,
                  InvokeInfo,
                  HandleEnvironmentChange,
                  returnValueHandler);
                if (response.AttentionAcknowledged &&
                    attention.IsCancellationRequested)
                {
                    attention.Acknowledge();
                    await attention.GetSendTask().ConfigureAwait(false);
                    throw new OperationCanceledException(attention.CancellationToken);
                }

                if (response.IsFinal && attention.TryCompleteCommand())
                {
                    if (response.Error is not null)
                    {
                        throw response.Error;
                    }

                    preparingStatement?.EnsureHandleInitialized();
                    cancellationToken.ThrowIfCancellationRequested();
                    return response.Rows;
                }

                if (attention.IsCancellationRequested || !response.IsFinal)
                {
                    continue;
                }
            }
        }
        catch
        {
            if (attention.IsCancellationRequested &&
                !attention.IsAcknowledged)
            {
                MarkBroken();
            }

            throw;
        }
        finally
        {
            EndAttention(attention);
        }
    }

    private async Task SendAttentionSafeAsync()
    {
        try
        {
            await _writer.WriteAttentionAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            MarkBroken();
            throw;
        }
    }

    private static void InvokeSafely<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(value);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                  "Apex SQL client event handler failed: {0}",
                  exception);
            }
        }
    }

    private static string GetOperation(string sql)
    {
        var text = sql.AsSpan().TrimStart();
        var separator = text.IndexOfAny(" \t\r\n");
        return (separator < 0 ? text : text[..separator]).ToString().ToUpperInvariant();
    }

    private static bool IsFatalConnectionError(Exception exception) =>
      exception is IOException or
        SocketException or
        InvalidDataException or
        AuthenticationException or
        ObjectDisposedException or
        MsSqlException { Severity: >= 20 };

    internal static void ValidateOptions(MsSqlConnectOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApplicationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientInterfaceName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Port);
        if (options.Port > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Port must be at most 65535.");
        }

        if (options.PacketSize is < 512 or > 32767)
        {
            throw new ArgumentOutOfRangeException(
              nameof(options),
              "SQL Server packet size must be between 512 and 32767 bytes.");
        }

        if (options.ConnectTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
              nameof(options),
              "Connect timeout cannot be negative.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.PreparedStatementCacheSize);
        ArgumentOutOfRangeException.ThrowIfNegative(
          options.PreparedStatementCacheSqlLengthLimit);
        if (options.CachePreparedStatements)
        {
            throw new ArgumentException(
              "Automatic prepared statement caching is not supported by Apex.MsSqlClient; " +
              "use PrepareAsync for an explicit server-side prepared handle.",
              nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.StringCacheCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(options.StringCacheMaximumByteLength);
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
              "Cached strings must be at most 4,096 bytes.");
        }

        ArgumentNullException.ThrowIfNull(options.ClientCertificates);
    }

    internal sealed class AttentionState : IDisposable
    {
        private readonly MsSqlConnection _connection;
        private readonly object _gate = new();
        private readonly CancellationTokenRegistration _registration;
        private readonly CancellationTokenSource _drainCancellation = new();
        private readonly TimeSpan _drainTimeout;
        private Task? _sendTask;
        private CancellationToken _cancellationToken;
        private bool _acknowledged;
        private bool _cancellationRequested;
        private bool _commandCompleted;

        internal AttentionState(
            MsSqlConnection connection,
            CancellationToken cancellationToken,
            TimeSpan drainTimeout)
        {
            _connection = connection;
            _cancellationToken = cancellationToken;
            _drainTimeout = drainTimeout;
            _registration = cancellationToken.CanBeCanceled
              ? cancellationToken.Register(
                static state => ((AttentionState)state!).CancelFromRegistration(),
                this)
              : default;
        }

        internal bool IsCancellationRequested
        {
            get
            {
                lock (_gate)
                {
                    return _cancellationRequested;
                }
            }
        }

        internal CancellationToken CancellationToken
        {
            get
            {
                lock (_gate)
                {
                    return _cancellationToken;
                }
            }
        }

        internal CancellationToken ReadCancellationToken =>
          _drainCancellation.Token;

        internal bool IsAcknowledged
        {
            get
            {
                lock (_gate)
                {
                    return _acknowledged;
                }
            }
        }

        internal void Cancel(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_cancellationRequested || _commandCompleted)
                {
                    return;
                }

                _cancellationRequested = true;
                _cancellationToken = cancellationToken;
                _sendTask = _connection.SendAttentionSafeAsync();
                if (_drainTimeout > TimeSpan.Zero)
                {
                    _drainCancellation.CancelAfter(_drainTimeout);
                }
            }
        }

        internal bool TryCompleteCommand()
        {
            lock (_gate)
            {
                if (_cancellationRequested)
                {
                    return false;
                }

                _commandCompleted = true;
                return true;
            }
        }

        internal void Acknowledge()
        {
            lock (_gate)
            {
                _acknowledged = true;
            }
        }

        internal Task GetSendTask()
        {
            lock (_gate)
            {
                return _sendTask ?? Task.CompletedTask;
            }
        }

        public void Dispose()
        {
            _registration.Dispose();
            _drainCancellation.Dispose();
        }

        private void CancelFromRegistration() => Cancel(_cancellationToken);
    }
}

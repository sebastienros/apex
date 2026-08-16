using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks.Sources;
using System.Transactions;
using Apex.PgClient.Internal;
using Apex.SqlClient;
using Apex.SqlClient.Internal;

namespace Apex.PgClient;

public sealed class PgConnection : ISqlConnection
{
    private readonly PgConnectOptions _options;
    private readonly Socket _socket;
    private readonly Stream _stream;
    private readonly PgWireReader _reader;
    private readonly PgWireWriter _writer;
    private readonly PgRowDecoder _rowDecoder;
    private readonly PgTypeRegistry _typeRegistry;
    private readonly byte[]? _channelBindingData;
    private readonly SqlAuthenticationMethod _authenticationMethod;
    private readonly BoundedOrderedCommandScheduler _scheduler;
    private readonly object _statementCacheGate = new();
    private readonly LruCache<string, string>? _statementCache;
    private bool _disposed;
    private int _processId;
    private int _secretKey;
    private int _statementSequence;
    private int _copyActive;
    private int _portalSequence;
    private byte _transactionStatus = (byte)'I';
    private DatabaseMetadata _databaseMetadata =
        new("PostgreSQL", "unknown", 0, 0);

    private PgConnection(
        PgConnectOptions options,
        Socket socket,
        Stream stream,
        bool secure,
        byte[]? channelBindingData,
        SqlAuthenticationMethod authenticationMethod)
    {
        _options = options;
        _socket = socket;
        _stream = stream;
        IsSecure = secure;
        _channelBindingData = channelBindingData;
        _authenticationMethod = authenticationMethod;
        _reader = new PgWireReader(stream);
        _typeRegistry = options.TypeRegistry ?? new PgTypeRegistry();
        _writer = new PgWireWriter(stream, _typeRegistry);
        _rowDecoder = new PgRowDecoder(
          options.StringCacheCapacity,
          options.StringCacheMaximumByteLength,
          options.Utf8BytesCacheCapacity,
          options.Utf8BytesCacheMaximumByteLength,
          _typeRegistry);
        _scheduler = new BoundedOrderedCommandScheduler(
          options.PipeliningLimit,
          (int)Math.Max(16, Math.Min(4096, (long)options.PipeliningLimit * 4)),
                    IsFatalConnectionError,
                    _writer.FlushAsync);
        _statementCache = options.CachePreparedStatements && options.PreparedStatementCacheSize > 0
          ? new LruCache<string, string>(
            options.PreparedStatementCacheSize,
            StringComparer.Ordinal)
          : null;
    }

    public event Action<PgNotice>? Notice;

    public event Action<PgNotification>? Notification;

    public bool IsSecure { get; }

    public DatabaseMetadata DatabaseMetadata => _databaseMetadata;

    public int ProcessId => _processId;

    public int SecretKey => _secretKey;

    public PgTypeRegistry TypeRegistry => _typeRegistry;

    internal bool IsUsable => !_disposed && !_scheduler.IsStopped && _socket.Connected;

    internal bool IsReadyForPool =>
        IsUsable && _transactionStatus == (byte)'I' && Volatile.Read(ref _copyActive) == 0;

    internal static async ValueTask<PgConnection> ConnectAsync(
        PgConnectOptions options,
        CancellationToken cancellationToken)
    {
        ValidateOptions(options);
        using CancellationTokenSource timeout =
          CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConnectTimeout);
        var authenticationMethod = SqlAuthenticationMethod.Password;
        if (options.AuthenticationProvider is not null)
        {
            var credential = await options.AuthenticationProvider(timeout.Token).ConfigureAwait(false);
            options = options with
            {
                Username = credential.Username ?? options.Username,
                Password = credential.Secret,
            };
            authenticationMethod = credential.Method;
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Username);
        }

        if (authenticationMethod == SqlAuthenticationMethod.BearerToken &&
            options.SslMode is not (PgSslMode.VerifyCa or PgSslMode.VerifyFull))
        {
            throw new AuthenticationException(
                "PostgreSQL bearer token authentication requires verified TLS.");
        }

        var socket = CreateSocket(options);
        try
        {
            Stream stream = await PgProxyConnector.ConnectAsync(socket, options, timeout.Token)
              .ConfigureAwait(false);
            var secure = false;
            byte[]? channelBindingData = null;
            if (options.SslNegotiation == PgSslNegotiation.Direct)
            {
                if (options.SslMode == PgSslMode.Disable)
                {
                    throw new InvalidOperationException("Direct SSL negotiation requires SSL to be enabled.");
                }

                stream = await UpgradeToTlsAsync(stream, options, timeout.Token).ConfigureAwait(false);
                secure = true;
                channelBindingData = GetChannelBindingData((SslStream)stream);
            }
            else if (options.SslMode is PgSslMode.Prefer or PgSslMode.Require or
                     PgSslMode.VerifyCa or PgSslMode.VerifyFull)
            {
                var response = await RequestSslAsync(stream, timeout.Token).ConfigureAwait(false);
                if (response == (byte)'S')
                {
                    stream = await UpgradeToTlsAsync(stream, options, timeout.Token).ConfigureAwait(false);
                    secure = true;
                    channelBindingData = GetChannelBindingData((SslStream)stream);
                }
                else if (response != (byte)'N')
                {
                    throw new InvalidDataException($"Unexpected PostgreSQL SSL response 0x{response:X2}.");
                }
                else if (options.SslMode is not PgSslMode.Prefer)
                {
                    throw new AuthenticationException("The PostgreSQL server does not support SSL.");
                }
            }

            PgConnection connection = new(
                options,
                socket,
                stream,
                secure,
                channelBindingData,
                authenticationMethod);
            try
            {
                await connection.InitializeAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (PgException exception) when (
              options.SslMode == PgSslMode.Allow &&
              IsSslRequired(exception))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return await ConnectAsync(
                  options with { SslMode = PgSslMode.Require },
                  cancellationToken).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async ValueTask<SqlRowSet> QueryAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return await ExecuteQueryCoreAsync(sql, default, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SqlRowSet> QueryAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return await ExecuteQueryCoreAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SqlRowSet> QueryTypedAsync(
        string sql,
        PgParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return await ExecuteTypedQueryCoreAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
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
        var result = await QueryAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        return new SqlCommandResult(result.AffectedRows, result.CommandTag);
    }

    public async ValueTask<SqlCommandResult> ExecuteTypedAsync(
        string sql,
        PgParameters parameters,
        CancellationToken cancellationToken = default)
    {
        var result = await QueryTypedAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        return new SqlCommandResult(result.AffectedRows, result.CommandTag);
    }

    public async ValueTask<PgBatchReader> ExecuteBatchAsync(
        PgBatch batch,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(batch);
        ThrowIfCopyActive();
        if (batch.Count == 0)
        {
            throw new ArgumentException("A PostgreSQL batch must contain at least one command.", nameof(batch));
        }

        using var activity = SqlClientDiagnostics.StartQuery(
            "postgresql",
            _options.Database,
            _options.Host,
            _options.Port,
            "BATCH");
        activity?.SetTag("db.operation.batch.size", batch.Count);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Exception? error = null;
        try
        {
            return await _scheduler.ExecuteAsync(
                async token =>
                {
                    token.ThrowIfCancellationRequested();
                    ThrowIfCopyActive();
                    await _writer.WriteBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
                },
                async _ => new PgBatchReader(
                    await ReadBatchResultsAsync(cancellationToken).ConfigureAwait(false)),
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
                "postgresql",
                "BATCH",
                error);
        }
    }

    public async ValueTask<PgBinaryImporter> BeginBinaryImportAsync(
        string copySql,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(copySql);
        if (Volatile.Read(ref _copyActive) != 0)
        {
            throw new InvalidOperationException("A COPY operation is already active.");
        }

        var activity = SqlClientDiagnostics.StartQuery(
            "postgresql",
            _options.Database,
            _options.Host,
            _options.Port,
            "COPY");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var ownsCopy = false;
        try
        {
            var columnCount = await _scheduler.ExecuteAsync(
                async token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (Interlocked.CompareExchange(ref _copyActive, 1, 0) != 0)
                    {
                        throw new InvalidOperationException("A COPY operation is already active.");
                    }

                    ownsCopy = true;
                    await _writer.WriteQueryAsync(copySql, CancellationToken.None).ConfigureAwait(false);
                },
                _ => ReadCopyInResponseAsync(cancellationToken),
                barrier: true,
                cancellationToken).ConfigureAwait(false);
            PgBinaryImporter importer = new(this, columnCount, activity, started);
            await importer.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return importer;
        }
        catch (Exception exception)
        {
            if (ownsCopy)
            {
                _socket.Dispose();
                Volatile.Write(ref _copyActive, 0);
            }
            activity?.SetStatus(
                System.Diagnostics.ActivityStatusCode.Error,
                exception.Message);
            activity?.Dispose();
            SqlClientDiagnostics.RecordQuery(
                System.Diagnostics.Stopwatch.GetElapsedTime(started),
                "postgresql",
                "COPY",
                exception);
            throw;
        }
    }

    public async ValueTask ReloadTypesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync(
            """
            SELECT t.oid::int8 AS oid,
                   n.nspname || '.' || t.typname AS qualified_name
            FROM pg_catalog.pg_type AS t
            JOIN pg_catalog.pg_namespace AS n ON n.oid = t.typnamespace
            """,
            cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            _typeRegistry.RegisterType(
                new PgType(checked((uint)row.Get<long>("oid")), row.Get<string>("qualified_name")));
        }
    }

    public async IAsyncEnumerable<SqlRow> StreamAsync(
        string sql,
        SqlParameters parameters = default,
        int fetchSize = 50,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfCopyActive();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fetchSize);

        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        await foreach (var row in StreamRowsAsync(
                         sql,
                         statementName: null,
                         parameters,
                         fetchSize,
                         cancellationToken).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    public async ValueTask<ISqlPreparedStatement> PrepareAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfCopyActive();
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        if (_options.UseLayer7Proxy && _transactionStatus != (byte)'T')
        {
            throw new InvalidOperationException(
              "Explicit prepared statements require an active transaction with a layer-7 proxy.");
        }

        var name = "A" + Interlocked.Increment(ref _statementSequence)
          .ToString("x", CultureInfo.InvariantCulture);
        var operation = GetOperation(sql);
        return await _scheduler.ExecuteAsync(
          async token =>
          {
              token.ThrowIfCancellationRequested();
              ThrowIfCopyActive();
              await _writer.WritePrepareAsync(name, sql, CancellationToken.None).ConfigureAwait(false);
          },
          async _ =>
          {
              var columns = await ReadPreparedAsync(CancellationToken.None).ConfigureAwait(false);
              return (ISqlPreparedStatement)new PgPreparedStatement(
                  this,
                  name,
                  sql,
                  operation,
                  columns);
          },
          barrier: true,
          cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ISqlRowReader> ExecuteReaderAsync(
        string sql,
        SqlParameters parameters = default,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfCopyActive();
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        return ValueTask.FromResult<ISqlRowReader>(
          new PgRowReader(
            this,
            sql,
            statementName: null,
            parameters,
            cancellationToken));
    }

    public async ValueTask<ISqlTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        return await BeginTransactionCoreAsync(
            "BEGIN",
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PgTransaction> BeginPgTransactionAsync(
        PgTransactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfCopyActive();
        options ??= new PgTransactionOptions();
        if (options.Deferrable &&
            (!options.ReadOnly || options.IsolationLevel != PgIsolationLevel.Serializable))
        {
            throw new ArgumentException(
                "Deferrable transactions must be serializable and read-only.",
                nameof(options));
        }

        return await BeginTransactionCoreAsync(
            BuildBeginTransactionSql(options),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PgTransaction> BeginTransactionCoreAsync(
        string beginSql,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfCopyActive();
        return await _scheduler.ExecuteAsync(
          async token =>
          {
              token.ThrowIfCancellationRequested();
              ThrowIfCopyActive();
              if (_transactionStatus != (byte)'I')
              {
                  throw new InvalidOperationException("A transaction is already active.");
              }

              await _writer.WriteQueryAsync(beginSql, CancellationToken.None).ConfigureAwait(false);
          },
          async _ =>
          {
              await ReadQueryResultsAsync(CancellationToken.None).ConfigureAwait(false);
              return new PgTransaction(this);
          },
          barrier: true,
          cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask EnlistTransactionAsync(
        Transaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.TransactionInformation.Status != TransactionStatus.Active)
        {
           throw new TransactionException("Only an active ambient transaction can be enlisted.");
        }

        var pgTransaction = await BeginPgTransactionAsync(
           new PgTransactionOptions(),
           cancellationToken).ConfigureAwait(false);
        try
        {
           transaction.EnlistVolatile(
               new PgAmbientTransactionEnlistment(pgTransaction),
               EnlistmentOptions.None);
        }
        catch
        {
           await pgTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
           throw;
        }
    }

    private static string BuildBeginTransactionSql(PgTransactionOptions options)
    {
        var isolation = options.IsolationLevel switch
        {
           PgIsolationLevel.ReadCommitted => "READ COMMITTED",
           PgIsolationLevel.RepeatableRead => "REPEATABLE READ",
           PgIsolationLevel.Serializable => "SERIALIZABLE",
           _ => throw new ArgumentOutOfRangeException(
               nameof(options),
               options.IsolationLevel,
               "Unsupported PostgreSQL isolation level."),
        };
        return "BEGIN ISOLATION LEVEL " + isolation +
              (options.ReadOnly ? " READ ONLY" : " READ WRITE") +
              (options.Deferrable ? " DEFERRABLE" : " NOT DEFERRABLE");
    }

    public async ValueTask CancelRequestAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using CancellationTokenSource timeout =
          CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ConnectTimeout);
        var cancelSocket = CreateSocket(_options);
        try
        {
            var stream = await PgProxyConnector
              .ConnectAsync(cancelSocket, _options, timeout.Token)
              .ConfigureAwait(false);
            await using var _ = stream.ConfigureAwait(false);
            var message = GC.AllocateUninitializedArray<byte>(16);
            BinaryPrimitives.WriteInt32BigEndian(message, 16);
            BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4), 80877102);
            BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(8), _processId);
            BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(12), _secretKey);
            await stream.WriteAsync(message, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            cancelSocket.Dispose();
        }
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
            if (Volatile.Read(ref _copyActive) == 0)
            {
                await _scheduler.ExecuteAsync(
                async token =>
                {
                    token.ThrowIfCancellationRequested();
                    await _writer.WriteTerminateAsync(CancellationToken.None).ConfigureAwait(false);
                },
                static _ => ValueTask.FromResult(true),
                barrier: true).ConfigureAwait(false);
            }
            else
            {
                _socket.Dispose();
            }
        }
        catch (Exception exception) when (
          !_socket.Connected ||
          IsFatalConnectionError(exception))
        {
        }
        finally
        {
            await _scheduler.DisposeAsync().ConfigureAwait(false);
            await _reader.CompleteAsync().ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);
            _socket.Dispose();
            _rowDecoder.DisableCache();
        }
    }

    private async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        await _writer.WriteStartupAsync(_options, cancellationToken).ConfigureAwait(false);
        PgScramClient? scram = null;
        var scramServerFinalVerified = false;

        while (true)
        {
            using var message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (message.Type)
            {
                case (byte)'R':
                    PgPayloadReader authentication = new(message.Payload.Span);
                    var authenticationType = authentication.ReadInt32();
                    switch (authenticationType)
                    {
                        case 0:
                            if (_options.ChannelBinding == PgChannelBinding.Require &&
                                !scramServerFinalVerified)
                            {
                                throw new AuthenticationException(
                                  "PostgreSQL channel binding was required but authentication did not verify SCRAM-SHA-256-PLUS.");
                            }

                            break;
                        case 3:
                            RejectNonBindingAuthentication();
                            await _writer.WritePasswordAsync(_options.Password, cancellationToken)
                                .ConfigureAwait(false);
                            break;
                        case 5:
                            RejectBearerPasswordHashing();
                            RejectNonBindingAuthentication();
                            if (authentication.Remaining != 4)
                            {
                                throw new InvalidDataException("The PostgreSQL MD5 salt is invalid.");
                            }

                            var md5 = PgWireWriter.Md5Password(
                                _options.Password,
                                _options.Username,
                                authentication.ReadSpan(4));
                            await _writer.WritePasswordAsync(md5, cancellationToken).ConfigureAwait(false);
                            break;
                        case 10:
                            RejectBearerPasswordHashing();
                            var mechanism = SelectSaslMechanism(ref authentication);
                            scram = new PgScramClient(
                              _options.Username,
                              _options.Password,
                              mechanism == "SCRAM-SHA-256-PLUS" ? _channelBindingData : null,
                              advertiseChannelBinding:
                                mechanism != "SCRAM-SHA-256-PLUS" &&
                                _options.ChannelBinding == PgChannelBinding.Prefer &&
                                IsSecure);
                            await _writer.WriteSaslInitialAsync(
                                mechanism,
                                scram.ClientFirstMessage,
                                cancellationToken).ConfigureAwait(false);
                            break;
                        case 11:
                            if (scram is null)
                            {
                                throw new InvalidDataException("Unexpected PostgreSQL SASL continuation.");
                            }

                            var clientFinal = scram.HandleServerFirst(
                                authentication.ReadString(authentication.Remaining));
                            await _writer.WriteSaslResponseAsync(clientFinal, cancellationToken)
                                .ConfigureAwait(false);
                            break;
                        case 12:
                            if (scram is null)
                            {
                                throw new InvalidDataException("Unexpected PostgreSQL SASL completion.");
                            }

                            scram.HandleServerFinal(authentication.ReadString(authentication.Remaining));
                            scramServerFinalVerified = true;
                            break;
                        default:
                            throw new NotSupportedException(
                                $"PostgreSQL authentication type {authenticationType} is not supported.");
                    }

                    break;
                case (byte)'S':
                    HandleParameterStatus(message.Payload.Span);
                    break;
                case (byte)'K':
                    PgPayloadReader keyData = new(message.Payload.Span);
                    _processId = keyData.ReadInt32();
                    _secretKey = keyData.ReadInt32();
                    break;
                case (byte)'N':
                    HandleNotice(message.Payload.Span);
                    break;
                case (byte)'E':
                    throw ParseError(message.Payload.Span);
                case (byte)'Z':
                    UpdateTransactionStatus(message.Payload.Span);
                    return;
                default:
                    throw new InvalidDataException(
                        $"Unexpected PostgreSQL startup message '{(char)message.Type}'.");
            }
        }
    }

    private void RejectBearerPasswordHashing()
    {
        if (_authenticationMethod == SqlAuthenticationMethod.BearerToken)
        {
            throw new AuthenticationException(
                "PostgreSQL bearer tokens require cleartext password authentication over TLS.");
        }
    }

    private void RejectNonBindingAuthentication()
    {
        if (_options.ChannelBinding == PgChannelBinding.Require)
        {
            throw new AuthenticationException(
              "PostgreSQL channel binding requires SCRAM-SHA-256-PLUS authentication.");
        }
    }

    private async ValueTask<SqlRowSet> ExecuteQueryCoreAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfCopyActive();
        var operation = GetOperation(sql);
        using var activity = SqlClientDiagnostics.StartQuery(
          "postgresql",
          _options.Database,
          _options.Host,
          _options.Port,
          operation);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Exception? error = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValueTask<SqlRowSet> execution;
            var cachedExecution = false;
            if (parameters.Count == 0)
            {
                execution = _scheduler.ExecuteAsync(
                  async token =>
                  {
                      token.ThrowIfCancellationRequested();
                      ThrowIfCopyActive();
                      await _writer.WriteQueryAsync(sql, CancellationToken.None).ConfigureAwait(false);
                  },
                  _ => ReceiveQueryAsync(cancellationToken),
                  barrier: cancellationToken.CanBeCanceled,
                  cancellationToken: cancellationToken);
            }

            else if (_statementCache is not null &&
                     sql.Length <= _options.PreparedStatementCacheSqlLengthLimit)
            {
                cachedExecution = true;
                execution = _scheduler.ExecuteAsync(
                  _ =>
                  {
                      ThrowIfCopyActive();
                      return ValueTask.CompletedTask;
                  },
                  _ => PrepareCacheAndExecuteAsync(sql, parameters, cancellationToken),
                  barrier: true,
                  cancellationToken);
            }
            else
            {
                execution = _scheduler.ExecuteAsync(
                  async token =>
                  {
                      token.ThrowIfCancellationRequested();
                      ThrowIfCopyActive();
                      await _writer.WriteExtendedQueryAsync(
                sql,
                parameters,
                CancellationToken.None).ConfigureAwait(false);
                  },
                  _ => ReceiveQueryAsync(cancellationToken),
                  barrier: cancellationToken.CanBeCanceled,
                  cancellationToken: cancellationToken);
            }

            try
            {
                return await execution.ConfigureAwait(false);
            }
            catch (PgException exception) when (
              cachedExecution &&
              exception.SqlState is "26000" or "0A000")
            {
                lock (_statementCacheGate)
                {
                    _statementCache!.Remove(sql, out _);
                }

                return await _scheduler.ExecuteAsync(
                  _ =>
                  {
                      ThrowIfCopyActive();
                      return ValueTask.CompletedTask;
                  },
                  _ => PrepareCacheAndExecuteAsync(sql, parameters, cancellationToken),
                  barrier: true,
                  cancellationToken).ConfigureAwait(false);
            }
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
              "postgresql",
              operation,
              error);
        }
    }

    private async ValueTask<SqlRowSet> ExecuteTypedQueryCoreAsync(
        string sql,
        PgParameters parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfCopyActive();
        var operation = GetOperation(sql);
        using var activity = SqlClientDiagnostics.StartQuery(
            "postgresql",
            _options.Database,
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
                    ThrowIfCopyActive();
                    await _writer.WriteExtendedQueryAsync(
                        sql,
                        parameters,
                        CancellationToken.None).ConfigureAwait(false);
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
                "postgresql",
                operation,
                error);
        }
    }

    private async ValueTask<SqlRowSet> PrepareCacheAndExecuteAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfCopyActive();
        string? existing;
        lock (_statementCacheGate)
        {
            _statementCache!.TryGet(sql, out existing);
        }

        var name = existing ??
          "A" + Interlocked.Increment(ref _statementSequence)
            .ToString("x", CultureInfo.InvariantCulture);
        if (existing is null)
        {
            await _writer.WritePrepareAsync(name, sql, CancellationToken.None).ConfigureAwait(false);
            await ReadReadyAsync((byte)'1', CancellationToken.None).ConfigureAwait(false);
            string? evicted;
            lock (_statementCacheGate)
            {
                _statementCache!.Add(sql, name, out evicted);
            }

            if (evicted is not null)
            {
                await _writer.WriteCloseStatementAsync(evicted, CancellationToken.None).ConfigureAwait(false);
                await ReadReadyAsync((byte)'3', CancellationToken.None).ConfigureAwait(false);
            }
        }

        await _writer.WritePreparedQueryAsync(name, parameters, CancellationToken.None).ConfigureAwait(false);
        return await ReceiveQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SqlRowSet> ReceiveQueryAsync(CancellationToken cancellationToken)
    {
        return await ReceiveQueryAsync(cancellationToken, null).ConfigureAwait(false);
    }

    private async ValueTask<SqlRowSet> ReceiveQueryAsync(
        CancellationToken cancellationToken,
        IReadOnlyList<SqlColumn>? columns)
    {
        Task? cancellationRequest = null;
        using var registration = cancellationToken.Register(
          () => cancellationRequest = TryCancelRequestAsync());
        try
        {
            var result = columns is null
              ? await ReadQueryResultsAsync(CancellationToken.None).ConfigureAwait(false)
              : await ReadPreparedQueryResultsAsync(
                  columns,
                  CancellationToken.None).ConfigureAwait(false);
            if (cancellationRequest is not null)
            {
                await cancellationRequest.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (PgException) when (cancellationToken.IsCancellationRequested)
        {
            if (cancellationRequest is not null)
            {
                await cancellationRequest.ConfigureAwait(false);
            }

            throw new OperationCanceledException(cancellationToken);
        }
    }

    internal async ValueTask<SqlRowSet> ExecutePreparedAsync(
        string name,
        string operation,
        IReadOnlyList<SqlColumn> columns,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var activity = SqlClientDiagnostics.StartQuery(
            "postgresql",
            _options.Database,
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
                    ThrowIfCopyActive();
                    await _writer.WritePreparedQueryAsync(
                                    name,
                                    parameters,
                                    CancellationToken.None,
                                    describePortal: false,
                                    flush: false).ConfigureAwait(false);
                },
                _ => ReceiveQueryAsync(cancellationToken, columns),
                barrier: cancellationToken.CanBeCanceled,
                cancellationToken: cancellationToken,
                flushBatch: true).ConfigureAwait(false);
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
                "postgresql",
                operation,
                error);
        }
    }

    internal async ValueTask<TState> ExecutePreparedCollectAsync<TState>(
        string name,
        string operation,
        IReadOnlyList<SqlColumn> columns,
        TState state,
        Action<TState, SqlRow> collector,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var activity = SqlClientDiagnostics.StartQuery(
            "postgresql",
            _options.Database,
            _options.Host,
            _options.Port,
            operation);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Exception? error = null;
        var execution = PreparedCollectionExecution<TState>.Rent(
            this,
            name,
            columns,
            state,
            collector,
            parameters,
            cancellationToken);
        try
        {
            return await _scheduler.ExecuteAsync(
                execution.SendAsync,
                execution.ReceiveAsync,
                barrier: cancellationToken.CanBeCanceled,
                cancellationToken,
                flushBatch: true).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = exception;
            activity?.SetStatus(
                System.Diagnostics.ActivityStatusCode.Error,
                exception.Message);
            throw;
        }
        finally
        {
            execution.Return();
            SqlClientDiagnostics.RecordQuery(
                System.Diagnostics.Stopwatch.GetElapsedTime(started),
                "postgresql",
                operation,
                error);
        }
    }

    internal async ValueTask ExecuteTransactionControlAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _scheduler.ExecuteAsync(
          async token =>
          {
              token.ThrowIfCancellationRequested();
              ThrowIfCopyActive();
              await _writer.WriteQueryAsync(sql, CancellationToken.None).ConfigureAwait(false);
          },
          async _ =>
          {
              await ReadQueryResultsAsync(CancellationToken.None).ConfigureAwait(false);
              return true;
          },
          barrier: true,
          cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask WriteCopyDataAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _copyActive) == 0)
        {
            throw new InvalidOperationException("No COPY operation is active.");
        }

        return _writer.WriteCopyDataAsync(payload, cancellationToken);
    }

    internal async ValueTask CompleteCopyAsync(CancellationToken cancellationToken)
    {
        var completed = false;
        try
        {
            await _writer.WriteCopyDoneAsync(cancellationToken).ConfigureAwait(false);
            await ReadCopyCompletionAsync(
                CancellationToken.None,
                throwServerError: true).ConfigureAwait(false);
            completed = true;
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
        finally
        {
            if (completed || !_socket.Connected)
            {
                Volatile.Write(ref _copyActive, 0);
            }
        }
    }

    internal async ValueTask AbortCopyAsync(string message)
    {
        if (Volatile.Read(ref _copyActive) == 0)
        {
            return;
        }

        try
        {
            await _writer.WriteCopyFailAsync(message, CancellationToken.None).ConfigureAwait(false);
            await ReadCopyCompletionAsync(
                CancellationToken.None,
                throwServerError: false).ConfigureAwait(false);
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
        finally
        {
            Volatile.Write(ref _copyActive, 0);
        }
    }

    internal async ValueTask ClosePreparedAsync(string name)
    {
        if (_disposed)
        {
            return;
        }

        await _scheduler.ExecuteAsync(
          async token =>
          {
              token.ThrowIfCancellationRequested();
              ThrowIfCopyActive();
              await _writer.WriteCloseStatementAsync(name, CancellationToken.None).ConfigureAwait(false);
          },
          async _ =>
          {
              await ReadReadyAsync((byte)'3', CancellationToken.None).ConfigureAwait(false);
              return true;
          },
          barrier: true).ConfigureAwait(false);
    }

    internal async ValueTask<ISqlCursor> CreateCursorAsync(
        string statementName,
        SqlParameters parameters,
        int fetchSize,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_transactionStatus != (byte)'T')
        {
            throw new InvalidOperationException(
              "PostgreSQL cursors require an active transaction.");
        }

        var portalName = "P" + Interlocked.Increment(ref _portalSequence)
          .ToString("x", CultureInfo.InvariantCulture);
        var initialPage = await ReadPortalAsync(
          portalName,
          statementName,
          parameters,
          Array.Empty<SqlColumn>(),
          bound: false,
          fetchSize,
          cancellationToken).ConfigureAwait(false);
        return new PgCursor(
          this,
          statementName,
          portalName,
          parameters,
          fetchSize,
          initialPage);
    }

    internal async ValueTask<PortalPage> ReadPortalAsync(
        string portalName,
        string statementName,
        SqlParameters parameters,
        IReadOnlyList<SqlColumn> columns,
        bool bound,
        int fetchSize,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _scheduler.ExecuteAsync(
          async token =>
          {
              token.ThrowIfCancellationRequested();
              ThrowIfCopyActive();
              if (bound)
              {
                  await _writer.WriteExecutePortalAsync(
                portalName,
                fetchSize,
                CancellationToken.None).ConfigureAwait(false);
              }
              else
              {
                  await _writer.WriteOpenPortalAsync(
                portalName,
                statementName,
                parameters,
                fetchSize,
                CancellationToken.None).ConfigureAwait(false);
              }
          },
          _ => ReadPortalPageAsync(columns, CancellationToken.None),
          barrier: true,
          cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask ClosePortalAsync(string portalName)
    {
        if (_disposed)
        {
            return;
        }

        await _scheduler.ExecuteAsync(
          async token =>
          {
              token.ThrowIfCancellationRequested();
              ThrowIfCopyActive();
              await _writer.WriteClosePortalAsync(
            portalName,
            CancellationToken.None).ConfigureAwait(false);
          },
          async _ =>
          {
              await ReadReadyAsync((byte)'3', CancellationToken.None).ConfigureAwait(false);
              return true;
          },
          barrier: true).ConfigureAwait(false);
    }

    internal async ValueTask<PgNotification> WaitForNotificationAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _scheduler.ExecuteAsync(
          static _ => ValueTask.CompletedTask,
          ReadNotificationAsync,
          barrier: true,
          cancellationToken).ConfigureAwait(false);
    }

    internal async IAsyncEnumerable<SqlRow> StreamPreparedRowsAsync(
        string statementName,
        SqlParameters parameters,
        int fetchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var row in StreamRowsAsync(
                         sql: null,
                         statementName,
                         parameters,
                         fetchSize,
                         cancellationToken).ConfigureAwait(false))
        {
            yield return row;
        }
    }

    internal ValueTask<ISqlRowReader> ExecutePreparedReaderAsync(
        string statementName,
        SqlParameters parameters,
        CancellationToken cancellationToken) =>
      ValueTask.FromResult<ISqlRowReader>(
        new PgRowReader(
          this,
          sql: null,
          statementName,
          parameters,
          cancellationToken));

    private async IAsyncEnumerable<SqlRow> StreamRowsAsync(
        string? sql,
        string? statementName,
        SqlParameters parameters,
        int capacity,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pageCapacity = Math.Min(capacity, 256);
        PgRowReader reader = new(
          this,
          sql,
          statementName,
          parameters,
          cancellationToken);
        await using var _ = reader.ConfigureAwait(false);
        while (true)
        {
            SqlRowPageBuilder page = new(
              _rowDecoder,
              rowCapacity: pageCapacity,
              byteCapacity: Math.Max(256, pageCapacity * 16));
            while (page.Count < pageCapacity &&
                   await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                reader.CopyCurrentTo(page);
            }

            if (page.Count == 0)
            {
                yield break;
            }

            var batch = page.BuildBatch(reader.Columns);
            for (var i = 0; i < batch.Count; i++)
            {
                yield return batch.CreateRow(i);
            }
        }
    }

    private async Task TryCancelRequestAsync()
    {
        try
        {
            await CancelRequestAsync(CancellationToken.None).ConfigureAwait(false);
        }

        catch (Exception)
        {
            _socket.Dispose();
        }
    }

    private async ValueTask<PgNotification> ReadNotificationAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (message.Type)
            {
                case (byte)'A':
                    return HandleNotification(message.Payload.Span);
                case (byte)'N':
                    HandleNotice(message.Payload.Span);
                    break;
                case (byte)'S':
                    HandleParameterStatus(message.Payload.Span);
                    break;
                case (byte)'E':
                    throw ParseError(message.Payload.Span);
                default:
                    throw new InvalidDataException(
                      $"Unexpected idle PostgreSQL message '{(char)message.Type}'.");
            }
        }
    }

    private async ValueTask<SqlRowSet> ReadQueryResultsAsync(
        CancellationToken cancellationToken,
        IReadOnlyList<SqlColumn>? initialColumns = null)
    {
        List<ResultBuilder> results = [];
        ResultBuilder current = new(_rowDecoder);
        if (initialColumns is not null)
        {
            current.SetColumns(initialColumns);
        }
        PgException? error = null;

        while (true)
        {
            using var message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (message.Type)
            {
                case (byte)'T':
                    current.SetColumns(ParseColumns(message.Payload.Span));
                    break;
                case (byte)'D':
                    current.AddRow(message.Payload.Span);
                    break;
                case (byte)'C':
                    current.Complete(ParseCommandTag(message.Payload.Span));
                    results.Add(current);
                    current = new ResultBuilder(_rowDecoder);
                    break;
                case (byte)'I':
                    current.Complete(string.Empty);
                    results.Add(current);
                    current = new ResultBuilder(_rowDecoder);
                    break;
                case (byte)'E':
                    error = ParseError(message.Payload.Span);
                    break;
                case (byte)'N':
                    HandleNotice(message.Payload.Span);
                    break;
                case (byte)'S':
                    HandleParameterStatus(message.Payload.Span);
                    break;
                case (byte)'A':
                    HandleNotification(message.Payload.Span);
                    break;
                case (byte)'1':
                case (byte)'2':
                case (byte)'3':
                case (byte)'n':
                case (byte)'t':
                    break;
                case (byte)'Z':
                    UpdateTransactionStatus(message.Payload.Span);
                    if (error is not null)
                    {
                        throw error;
                    }

                    return BuildResultChain(results);
                default:
                    throw new InvalidDataException(
                      $"Unexpected PostgreSQL query message '{(char)message.Type}'.");
            }
        }
    }

    private async ValueTask<SqlRowSet> ReadPreparedQueryResultsAsync(
        IReadOnlyList<SqlColumn> columns,
        CancellationToken cancellationToken)
    {
        SqlRowPageCollectionBuilder rows = new(_rowDecoder);
        var commandTag = string.Empty;
        var completed = false;
        PgException? error = null;

        while (true)
        {
            using var message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (message.Type)
            {
                case (byte)'D':
                    ValidateRow(message.Payload.Span, columns);
                    rows.Add(message.Payload.Span);
                    break;
                case (byte)'C':
                    commandTag = ParseCommandTag(message.Payload.Span);
                    completed = true;
                    break;
                case (byte)'E':
                    error = ParseError(message.Payload.Span);
                    break;
                case (byte)'N':
                    HandleNotice(message.Payload.Span);
                    break;
                case (byte)'S':
                    HandleParameterStatus(message.Payload.Span);
                    break;
                case (byte)'A':
                    HandleNotification(message.Payload.Span);
                    break;
                case (byte)'2':
                case (byte)'n':
                case (byte)'t':
                    break;
                case (byte)'Z':
                    UpdateTransactionStatus(message.Payload.Span);
                    if (error is not null)
                    {
                        throw error;
                    }

                    if (!completed)
                    {
                        throw new InvalidDataException(
                            "PostgreSQL prepared query did not complete.");
                    }

                    return new SqlRowSet(
                      columns,
                      rows.Build(columns),
                      ParseAffectedRows(commandTag),
                      commandTag);
                default:
                    throw new InvalidDataException(
                      $"Unexpected PostgreSQL prepared-query message '{(char)message.Type}'.");
            }
        }
    }

    private ValueTask<TState> ReceivePreparedCollectionAsync<TState>(
        IReadOnlyList<SqlColumn> columns,
        TState state,
        Action<TState, SqlRow> collector,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return ReceivePreparedCollectionCoreAsync(
                columns,
                state,
                collector,
                cancellation: null,
                cancellationToken);
        }

        return ReceivePreparedCollectionCancelableAsync(
            columns,
            state,
            collector,
            cancellationToken);
    }

    private async ValueTask<TState> ReceivePreparedCollectionCancelableAsync<TState>(
        IReadOnlyList<SqlColumn> columns,
        TState state,
        Action<TState, SqlRow> collector,
        CancellationToken cancellationToken)
    {
        using var cancellation = new PgCancellation(this, cancellationToken);
        try
        {
            return await ReceivePreparedCollectionCoreAsync(
                columns,
                state,
                collector,
                cancellation,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PgException) when (cancellationToken.IsCancellationRequested)
        {
            if (cancellation.Request is { } request)
            {
                await request.ConfigureAwait(false);
            }

            throw new OperationCanceledException(cancellationToken);
        }
    }

    private async ValueTask<TState> ReceivePreparedCollectionCoreAsync<TState>(
        IReadOnlyList<SqlColumn> columns,
        TState state,
        Action<TState, SqlRow> collector,
        PgCancellation? cancellation,
        CancellationToken cancellationToken)
    {
        var ordinals = SqlColumnOrdinalMapCache.GetOrAdd(columns);
        var completed = false;
        PgException? error = null;

        while (true)
        {
            using var message =
                await _reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            switch (message.Type)
            {
                case (byte)'D':
                    ValidateRow(message.Payload.Span, columns);
                    collector(
                        state,
                        new SqlRow(
                            columns,
                            ordinals,
                            _rowDecoder,
                            message.Payload));
                    break;
                case (byte)'C':
                    completed = true;
                    break;
                case (byte)'E':
                    error = ParseError(message.Payload.Span);
                    break;
                case (byte)'N':
                    HandleNotice(message.Payload.Span);
                    break;
                case (byte)'S':
                    HandleParameterStatus(message.Payload.Span);
                    break;
                case (byte)'A':
                    HandleNotification(message.Payload.Span);
                    break;
                case (byte)'2':
                case (byte)'n':
                case (byte)'t':
                    break;
                case (byte)'Z':
                    UpdateTransactionStatus(message.Payload.Span);
                    if (error is not null)
                    {
                        throw error;
                    }

                    if (!completed)
                    {
                        throw new InvalidDataException(
                            "PostgreSQL prepared query did not complete.");
                    }

                    if (cancellation?.Request is { } request)
                    {
                        await request.ConfigureAwait(false);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    return state;
                default:
                    throw new InvalidDataException(
                        $"Unexpected PostgreSQL prepared-query message '{(char)message.Type}'.");
            }
        }
    }

    private async ValueTask<SqlRowSet> ReadBatchResultsAsync(
        CancellationToken cancellationToken)
    {
        List<ResultBuilder> results = [];
        ResultBuilder current = new(_rowDecoder);
        PgException? error = null;
        var errorIndex = -1;
        Task? cancellationRequest = null;
        using var registration = cancellationToken.Register(
            () => cancellationRequest = TryCancelRequestAsync());
        while (true)
        {
            using var message = await _reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            switch (message.Type)
            {
                case (byte)'T':
                    current.SetColumns(ParseColumns(message.Payload.Span));
                    break;
                case (byte)'D':
                    current.AddRow(message.Payload.Span);
                    break;
                case (byte)'C':
                    current.Complete(ParseCommandTag(message.Payload.Span));
                    results.Add(current);
                    current = new ResultBuilder(_rowDecoder);
                    break;
                case (byte)'I':
                    current.Complete(string.Empty);
                    results.Add(current);
                    current = new ResultBuilder(_rowDecoder);
                    break;
                case (byte)'E':
                    error ??= ParseError(message.Payload.Span);
                    errorIndex = results.Count;
                    break;
                case (byte)'N':
                    HandleNotice(message.Payload.Span);
                    break;
                case (byte)'S':
                    HandleParameterStatus(message.Payload.Span);
                    break;
                case (byte)'A':
                    HandleNotification(message.Payload.Span);
                    break;
                case (byte)'1':
                case (byte)'2':
                case (byte)'3':
                case (byte)'n':
                case (byte)'t':
                    break;
                case (byte)'Z':
                    UpdateTransactionStatus(message.Payload.Span);
                    registration.Dispose();
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancellationRequest ??= TryCancelRequestAsync();
                        await cancellationRequest.ConfigureAwait(false);
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (error is not null)
                    {
                        throw new PgBatchException(errorIndex, error);
                    }

                    return BuildResultChain(results);
                default:
                    throw new InvalidDataException(
                        $"Unexpected PostgreSQL batch message '{(char)message.Type}'.");
            }
        }
    }

    private async ValueTask<int> ReadCopyInResponseAsync(
        CancellationToken cancellationToken)
    {
        PgException? error = null;
        while (true)
        {
            using var message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (message.Type)
            {
                case (byte)'G':
                    PgPayloadReader response = new(message.Payload.Span);
                    var overallFormat = response.ReadByte();
                    var columnCount = response.ReadInt16();
                    if (overallFormat != 1 || columnCount < 0)
                    {
                        throw new InvalidDataException(
                            "The server did not start a binary COPY import.");
                    }

                    for (var i = 0; i < columnCount; i++)
                    {
                        if (response.ReadInt16() != 1)
                        {
                            throw new InvalidDataException(
                                "The server returned a non-binary COPY column.");
                        }
                    }

                    return columnCount;
                case (byte)'E':
                    error = ParseError(message.Payload.Span);
                    break;
                case (byte)'N':
                    HandleNotice(message.Payload.Span);
                    break;
                case (byte)'S':
                    HandleParameterStatus(message.Payload.Span);
                    break;
                case (byte)'A':
                    HandleNotification(message.Payload.Span);
                    break;
                case (byte)'Z':
                    UpdateTransactionStatus(message.Payload.Span);
                    throw error is not null
                      ? error
                      : new InvalidDataException(
                        "PostgreSQL rejected COPY without an error response.");
                default:
                    throw new InvalidDataException(
                        $"Unexpected PostgreSQL COPY response '{(char)message.Type}'.");
            }
        }
    }

    private async ValueTask ReadCopyCompletionAsync(
        CancellationToken cancellationToken,
        bool throwServerError)
    {
        PgException? error = null;
        while (true)
        {
            using var message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (message.Type)
            {
                case (byte)'C':
                    break;
                case (byte)'E':
                    error = ParseError(message.Payload.Span);
                    break;
                case (byte)'N':
                    HandleNotice(message.Payload.Span);
                    break;
                case (byte)'S':
                    HandleParameterStatus(message.Payload.Span);
                    break;
                case (byte)'A':
                    HandleNotification(message.Payload.Span);
                    break;
                case (byte)'Z':
                    UpdateTransactionStatus(message.Payload.Span);
                    if (throwServerError && error is not null)
                    {
                        throw error;
                    }

                    return;
                default:
                    throw new InvalidDataException(
                        $"Unexpected PostgreSQL COPY completion '{(char)message.Type}'.");
            }
        }
    }

    private void ThrowIfCopyActive()
    {
        if (Volatile.Read(ref _copyActive) != 0)
        {
            throw new InvalidOperationException(
                "The connection cannot execute commands while a COPY operation is active.");
        }
    }

    private async ValueTask<PortalPage> ReadPortalPageAsync(
        IReadOnlyList<SqlColumn> existingColumns,
        CancellationToken cancellationToken)
    {
        var columns = existingColumns;
        SqlRowPageCollectionBuilder rows = new(_rowDecoder);
        var commandTag = string.Empty;
        var hasMore = false;
        var completed = false;
        PgException? error = null;
        while (true)
        {
            using var message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (message.Type)
            {
                case (byte)'T':
                    columns = ParseColumns(message.Payload.Span);
                    break;
                case (byte)'D':
                    ValidateRow(message.Payload.Span, columns);
                    rows.Add(message.Payload.Span);
                    break;
                case (byte)'C':
                    commandTag = ParseCommandTag(message.Payload.Span);
                    completed = true;
                    break;
                case (byte)'s':
                    hasMore = true;
                    completed = true;
                    break;
                case (byte)'2':
                case (byte)'n':
                    break;
                case (byte)'E':
                    error = ParseError(message.Payload.Span);
                    break;
                case (byte)'N':
                    HandleNotice(message.Payload.Span);
                    break;
                case (byte)'S':
                    HandleParameterStatus(message.Payload.Span);
                    break;
                case (byte)'A':
                    HandleNotification(message.Payload.Span);
                    break;
                case (byte)'Z':
                    UpdateTransactionStatus(message.Payload.Span);
                    if (error is not null)
                    {
                        throw error;
                    }

                    if (!completed)
                    {
                        throw new InvalidDataException("PostgreSQL portal execution did not complete.");
                    }

                    return new PortalPage(
                      new SqlRowSet(
                        columns,
                        rows.Build(columns),
                        ParseAffectedRows(commandTag),
                        commandTag),
                      hasMore);
                default:
                    throw new InvalidDataException(
                      $"Unexpected PostgreSQL portal message '{(char)message.Type}'.");
            }
        }
    }

    private async ValueTask ReadReadyAsync(
        byte expectedCompletion,
        CancellationToken cancellationToken)
    {
        var completed = false;
        PgException? error = null;
        while (true)
        {
            using var message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (message.Type)
            {
                case var type when type == expectedCompletion:
                    completed = true;
                    break;
                case (byte)'E':
                    error = ParseError(message.Payload.Span);
                    break;
                case (byte)'N':
                    HandleNotice(message.Payload.Span);
                    break;
                case (byte)'S':
                    HandleParameterStatus(message.Payload.Span);
                    break;
                case (byte)'A':
                    HandleNotification(message.Payload.Span);
                    break;
                case (byte)'Z':
                    UpdateTransactionStatus(message.Payload.Span);
                    if (error is not null)
                    {
                        throw error;
                    }

                    if (!completed)
                    {
                        throw new InvalidDataException(
                            $"PostgreSQL did not send completion '{(char)expectedCompletion}'.");
                    }

                    return;
                default:
                    throw new InvalidDataException(
                        $"Unexpected PostgreSQL control message '{(char)message.Type}'.");
            }
        }
    }

    private async ValueTask<IReadOnlyList<SqlColumn>> ReadPreparedAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SqlColumn>? columns = null;
        var parsed = false;
        var described = false;
        PgException? error = null;
        while (true)
        {
            using var message = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (message.Type)
            {
                case (byte)'1':
                    parsed = true;
                    break;
                case (byte)'t':
                    break;
                case (byte)'T':
                    columns = ParseColumns(message.Payload.Span, SqlDataFormat.Binary);
                    described = true;
                    break;
                case (byte)'n':
                    columns = Array.Empty<SqlColumn>();
                    described = true;
                    break;
                case (byte)'E':
                    error = ParseError(message.Payload.Span);
                    break;
                case (byte)'N':
                    HandleNotice(message.Payload.Span);
                    break;
                case (byte)'S':
                    HandleParameterStatus(message.Payload.Span);
                    break;
                case (byte)'A':
                    HandleNotification(message.Payload.Span);
                    break;
                case (byte)'Z':
                    UpdateTransactionStatus(message.Payload.Span);
                    if (error is not null)
                    {
                        throw error;
                    }

                    if (!parsed || !described)
                    {
                        throw new InvalidDataException(
                          "PostgreSQL did not describe the prepared statement.");
                    }

                    return columns!;
                default:
                    throw new InvalidDataException(
                      $"Unexpected PostgreSQL prepare message '{(char)message.Type}'.");
            }
        }
    }

    private static IReadOnlyList<SqlColumn> ParseColumns(
        ReadOnlySpan<byte> payload,
        SqlDataFormat? resultFormat = null)
    {
        PgPayloadReader reader = new(payload);
        int count = reader.ReadInt16();
        SqlColumn[] columns = new SqlColumn[count];
        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadCString();
            _ = reader.ReadInt32();
            _ = reader.ReadInt16();
            var typeId = unchecked((uint)reader.ReadInt32());
            var typeSize = reader.ReadInt16();
            var typeModifier = reader.ReadInt32();
            SqlDataFormat format = resultFormat ?? (SqlDataFormat)reader.ReadInt16();
            if (resultFormat is not null)
            {
                _ = reader.ReadInt16();
            }
            columns[i] = new SqlColumn(name, typeId, typeSize, typeModifier, format);
        }

        return columns;
    }

    private static void ValidateRow(
        ReadOnlySpan<byte> payload,
        IReadOnlyList<SqlColumn> columns)
    {
        var count = PgRowDecoder.GetFieldCount(payload);
        if (count != columns.Count)
        {
            throw new InvalidDataException(
                $"PostgreSQL row has {count} values but {columns.Count} columns were described.");
        }
    }

    private static string ParseCommandTag(ReadOnlySpan<byte> payload)
    {
        PgPayloadReader reader = new(payload);
        return reader.ReadCString();
    }

    private static SqlRowSet BuildResultChain(IReadOnlyList<ResultBuilder> builders)
    {
        if (builders.Count == 0)
        {
            return SqlRowSet.Empty;
        }

        SqlRowSet? next = null;
        for (var i = builders.Count - 1; i >= 0; i--)
        {
            next = builders[i].Build(next);
        }

        return next!;
    }

    private string SelectSaslMechanism(ref PgPayloadReader reader)
    {
        var supportsScram = false;
        var supportsScramPlus = false;
        while (reader.Remaining > 0)
        {
            var mechanism = reader.ReadCString();
            if (mechanism.Length == 0)
            {
                break;
            }

            supportsScram |= mechanism == "SCRAM-SHA-256";
            supportsScramPlus |= mechanism == "SCRAM-SHA-256-PLUS";
        }

        if (_options.ChannelBinding != PgChannelBinding.Disable &&
              IsSecure &&
              _channelBindingData is not null &&
              supportsScramPlus)
        {
            return "SCRAM-SHA-256-PLUS";
        }

        if (_options.ChannelBinding == PgChannelBinding.Require)
        {
            throw new AuthenticationException(
              "PostgreSQL channel binding is required but SCRAM-SHA-256-PLUS is unavailable.");
        }

        return supportsScram
            ? "SCRAM-SHA-256"
            : throw new NotSupportedException("The server does not offer SCRAM-SHA-256.");
    }

    private void HandleParameterStatus(ReadOnlySpan<byte> payload)
    {
        PgPayloadReader reader = new(payload);
        var name = reader.ReadCString();
        var value = reader.ReadCString();
        if (name == "server_version")
        {
            var numeric = value.Split(' ', '-', StringSplitOptions.RemoveEmptyEntries)[0];
            var parts = numeric.Split('.');
            var major = int.TryParse(parts.ElementAtOrDefault(0), out var parsedMajor) ? parsedMajor : 0;
            var minor = int.TryParse(parts.ElementAtOrDefault(1), out var parsedMinor) ? parsedMinor : 0;
            _databaseMetadata = new DatabaseMetadata("PostgreSQL", value, major, minor);
        }
    }

    private void HandleNotice(ReadOnlySpan<byte> payload)
    {
        var fields = ParseErrorFields(payload);
        PgNotice notice = new(
            Get(fields, 'M') ?? "PostgreSQL notice",
            Get(fields, 'V') ?? Get(fields, 'S'),
            Get(fields, 'C'),
            Get(fields, 'D'),
            Get(fields, 'H'));
        InvokeSafely(Notice, notice);
    }

    private PgNotification HandleNotification(ReadOnlySpan<byte> payload)
    {
        PgPayloadReader reader = new(payload);
        PgNotification notification = new(
            reader.ReadInt32(),
            reader.ReadCString(),
            reader.ReadCString());
        InvokeSafely(Notification, notification);
        return notification;
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

    private static PgException ParseError(ReadOnlySpan<byte> payload) =>
        new(ParseErrorFields(payload));

    private static IReadOnlyDictionary<char, string> ParseErrorFields(ReadOnlySpan<byte> payload)
    {
        PgPayloadReader reader = new(payload);
        Dictionary<char, string> fields = [];
        while (reader.Remaining > 0)
        {
            var type = (char)reader.ReadByte();
            if (type == '\0')
            {
                break;
            }

            fields[type] = reader.ReadCString();
        }

        return fields;
    }

    private static string? Get(IReadOnlyDictionary<char, string> fields, char key) =>
        fields.TryGetValue(key, out var value) ? value : null;

    private void UpdateTransactionStatus(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 1 ||
            payload[0] is not ((byte)'I' or (byte)'T' or (byte)'E'))
        {
            throw new InvalidDataException("The PostgreSQL transaction status is invalid.");
        }

        _transactionStatus = payload[0];
    }

    private static string GetOperation(string sql)
    {
        var text = sql.AsSpan().TrimStart();
        var separator = text.IndexOfAny(" \t\r\n");
        return (separator < 0 ? text : text[..separator]).ToString().ToUpperInvariant();
    }

    internal static bool IsFatalConnectionError(Exception exception) =>
      exception is IOException or
        SocketException or
        InvalidDataException or
        AuthenticationException or
        ObjectDisposedException or
        PgException { SqlState: "57P01" or "57P02" or "57P03" or "08006" };

    private static long ParseAffectedRows(string commandTag)
    {
        var tag = commandTag.AsSpan();
        var lastSpace = tag.LastIndexOf(' ');
        return lastSpace >= 0 &&
               long.TryParse(tag[(lastSpace + 1)..], out var affected)
          ? affected
          : 0;
    }

    private static Socket CreateSocket(PgConnectOptions options)
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

    private static bool IsUnixSocket(PgConnectOptions options) =>
        Path.IsPathRooted(options.Host);

    private static async ValueTask<byte> RequestSslAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var request = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(request, 8);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(4), 80877103);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        var response = new byte[1];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        return response[0];
    }

    private static async ValueTask<Stream> UpgradeToTlsAsync(
        Stream stream,
        PgConnectOptions options,
        CancellationToken cancellationToken)
    {
        var validation =
            options.CertificateValidationCallback ??
            options.SslMode switch
            {
                PgSslMode.Require or PgSslMode.Prefer => static (_, _, _, _) => true,
                PgSslMode.VerifyCa => VerifyCertificateAuthority,
                _ => null,
            };

        SslStream ssl = new(stream, leaveInnerStreamOpen: false, validation);
        var clientCertificates = options.ClientCertificates.Count == 0
            ? null
            : new X509CertificateCollection(options.ClientCertificates.ToArray());
        SslClientAuthenticationOptions authenticationOptions = new()
        {
            TargetHost = options.Host,
            EnabledSslProtocols = SslProtocols.None,
            ClientCertificates = clientCertificates,
            CertificateRevocationCheckMode = options.CertificateRevocationCheckMode,
        };
        if (options.SslNegotiation == PgSslNegotiation.Direct)
        {
            authenticationOptions.ApplicationProtocols =
              [new SslApplicationProtocol("postgresql")];
        }

        await ssl.AuthenticateAsClientAsync(
            authenticationOptions,
            cancellationToken).ConfigureAwait(false);
        if (options.SslNegotiation == PgSslNegotiation.Direct &&
              ssl.NegotiatedApplicationProtocol != new SslApplicationProtocol("postgresql"))
        {
            throw new AuthenticationException(
              "PostgreSQL direct TLS did not negotiate the required 'postgresql' ALPN protocol.");
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

    private static byte[] GetChannelBindingData(SslStream stream)
    {
        var certificate = stream.RemoteCertificate ??
          throw new AuthenticationException(
            "The TLS server certificate is unavailable for PostgreSQL channel binding.");
        var certificate2 =
          X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var signatureAlgorithm = certificate2.SignatureAlgorithm.Value ?? string.Empty;
        return signatureAlgorithm switch
        {
            "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3" =>
              SHA384.HashData(certificate2.RawData),
            "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4" =>
              SHA512.HashData(certificate2.RawData),
            _ => SHA256.HashData(certificate2.RawData),
        };
    }

    private static bool IsSslRequired(PgException exception) =>
      exception.SqlState == "28000" &&
      (exception.Message.Contains("SSL off", StringComparison.OrdinalIgnoreCase) ||
       exception.Message.Contains("no encryption", StringComparison.OrdinalIgnoreCase));

    private static void ValidateOptions(PgConnectOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Database);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Port);
        if (options.Port > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Port must be at most 65535.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PipeliningLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PreparedStatementCacheSize);
        ArgumentOutOfRangeException.ThrowIfNegative(options.StringCacheCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(options.StringCacheMaximumByteLength);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Utf8BytesCacheCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Utf8BytesCacheMaximumByteLength);
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

        if (options.Utf8BytesCacheCapacity > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(
              nameof(options),
              "UTF-8 byte cache capacity must be at most 1,048,576.");
        }

        if (options.Utf8BytesCacheMaximumByteLength > 4096)
        {
            throw new ArgumentOutOfRangeException(
              nameof(options),
              "Cached UTF-8 byte values must be at most 4,096 bytes.");
        }

        if (options.Proxy is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Proxy.Host);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Proxy.Port);
            if (IsUnixSocket(options))
            {
                throw new ArgumentException(
                  "Proxy transport cannot target a Unix domain socket.",
                  nameof(options));
            }
        }

        if (options.UseLayer7Proxy && options.CachePreparedStatements)
        {
            throw new ArgumentException(
              "Prepared statement caching must be disabled with a layer-7 proxy.",
              nameof(options));
        }
        if (options.ChannelBinding == PgChannelBinding.Require &&
            options.SslMode == PgSslMode.Disable)
        {
            throw new ArgumentException(
              "PostgreSQL channel binding requires SSL.",
              nameof(options));
        }

        if (options.SslNegotiation == PgSslNegotiation.Direct &&
            options.SslMode is PgSslMode.Disable or PgSslMode.Allow or PgSslMode.Prefer)
        {
            throw new ArgumentException(
              "PostgreSQL direct TLS requires Require, VerifyCa, or VerifyFull SSL mode.",
              nameof(options));
        }
    }

    private sealed class PreparedCollectionExecution<TState>
    {
        private const int MaximumPoolSize = 4096;
        private static readonly ConcurrentQueue<PreparedCollectionExecution<TState>>
            s_pool = new();
        private static int s_poolCount;
        private readonly Func<CancellationToken, ValueTask> _sendAsync;
        private readonly Func<CancellationToken, ValueTask<TState>> _receiveAsync;
        private PgConnection? _connection;
        private string? _name;
        private IReadOnlyList<SqlColumn>? _columns;
        private TState _state = default!;
        private Action<TState, SqlRow>? _collector;
        private SqlParameters _parameters;
        private CancellationToken _cancellationToken;

        private PreparedCollectionExecution()
        {
            _sendAsync = SendAsyncCore;
            _receiveAsync = ReceiveAsyncCore;
        }

        public Func<CancellationToken, ValueTask> SendAsync => _sendAsync;

        public Func<CancellationToken, ValueTask<TState>> ReceiveAsync => _receiveAsync;

        public static PreparedCollectionExecution<TState> Rent(
            PgConnection connection,
            string name,
            IReadOnlyList<SqlColumn> columns,
            TState state,
            Action<TState, SqlRow> collector,
            SqlParameters parameters,
            CancellationToken cancellationToken)
        {
            if (!s_pool.TryDequeue(out var execution))
            {
                execution = new PreparedCollectionExecution<TState>();
            }
            else
            {
                Interlocked.Decrement(ref s_poolCount);
            }

            execution._connection = connection;
            execution._name = name;
            execution._columns = columns;
            execution._state = state;
            execution._collector = collector;
            execution._parameters = parameters;
            execution._cancellationToken = cancellationToken;
            return execution;
        }

        public void Return()
        {
            _connection = null;
            _name = null;
            _columns = null;
            _state = default!;
            _collector = null;
            _parameters = default;
            _cancellationToken = default;
            if (Interlocked.Increment(ref s_poolCount) <= MaximumPoolSize)
            {
                s_pool.Enqueue(this);
            }
            else
            {
                Interlocked.Decrement(ref s_poolCount);
            }
        }

        private ValueTask SendAsyncCore(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connection = _connection!;
            connection.ThrowIfCopyActive();
            return connection._writer.WritePreparedQueryAsync(
                _name!,
                _parameters,
                CancellationToken.None,
                describePortal: false,
                flush: false);
        }

        private ValueTask<TState> ReceiveAsyncCore(CancellationToken _) =>
            _connection!.ReceivePreparedCollectionAsync(
                _columns!,
                _state,
                _collector!,
                _cancellationToken);
    }

    private sealed class PgCancellation : IDisposable
    {
        private readonly PgConnection _connection;
        private readonly CancellationTokenRegistration _registration;
        private Task? _request;

        public PgCancellation(
            PgConnection connection,
            CancellationToken cancellationToken)
        {
            _connection = connection;
            _registration = cancellationToken.Register(
                static state => ((PgCancellation)state!).Cancel(),
                this);
        }

        public Task? Request => Volatile.Read(ref _request);

        public void Dispose() => _registration.Dispose();

        private void Cancel() =>
            Volatile.Write(ref _request, _connection.TryCancelRequestAsync());
    }

    private sealed class ResultBuilder
    {
        private readonly SqlRowPageCollectionBuilder _rows;
        private string _commandTag = string.Empty;

        internal ResultBuilder(PgRowDecoder decoder)
        {
            _rows = new SqlRowPageCollectionBuilder(decoder);
        }

        public IReadOnlyList<SqlColumn> Columns { get; private set; } = Array.Empty<SqlColumn>();

        public void SetColumns(IReadOnlyList<SqlColumn> columns) => Columns = columns;

        public void AddRow(ReadOnlySpan<byte> row)
        {
            ValidateRow(row, Columns);
            _rows.Add(row);
        }

        public void Complete(string commandTag) => _commandTag = commandTag;

        public SqlRowSet Build(SqlRowSet? next)
        {
            var affectedRows = PgConnection.ParseAffectedRows(_commandTag);
            return new SqlRowSet(
              Columns,
              _rows.Build(Columns),
              affectedRows,
              _commandTag,
              next);
        }
    }

    private sealed class PgRowReader : ISqlRowReader, IValueTaskSource<bool>
    {
        private readonly PgConnection _connection;
        private readonly AsyncAutoResetEvent _advance = new();
        private readonly object _gate = new();
        private readonly Action _cancelAction;
        private readonly CancellationTokenRegistration _operationCancellation;
        private readonly CancellationToken _operationCancellationToken;
        private readonly Task<bool> _operation;
        private ManualResetValueTaskSourceCore<bool> _readCompletion;
        private CancellationTokenRegistration _readCancellation;
        private CancellationToken _readCancellationToken;
        private CancellationToken _cancellationToken;
        private PgWireMessage _current;
        private IReadOnlyList<SqlColumn> _columns = Array.Empty<SqlColumn>();
        private Exception? _error;
        private Task? _cancelRequest;
        private bool _hasCurrent;
        private bool _currentDelivered;
        private bool _completed;
        private bool _stopped;
        private bool _canceled;
        private bool _sent;
        private bool _readPending;
        private int _disposed;

        internal PgRowReader(
            PgConnection connection,
            string? sql,
            string? statementName,
            SqlParameters parameters,
            CancellationToken cancellationToken)
        {
            _connection = connection;
            _operationCancellationToken = cancellationToken;
            _cancelAction = Cancel;
            _readCompletion.RunContinuationsAsynchronously = true;
            _operationCancellation = cancellationToken.CanBeCanceled
              ? cancellationToken.Register(_cancelAction)
              : default;
            _operation = connection._scheduler.ExecuteAsync(
              async token =>
              {
                  token.ThrowIfCancellationRequested();
                  connection.ThrowIfCopyActive();
                  if (sql is not null)
                  {
                      await connection._writer.WriteExtendedQueryAsync(
                  sql,
                  parameters,
                  CancellationToken.None).ConfigureAwait(false);
                  }
                  else
                  {
                      await connection._writer.WritePreparedQueryAsync(
                  statementName!,
                  parameters,
                  CancellationToken.None).ConfigureAwait(false);
                  }

                  Task? cancelRequest = null;
                  lock (_gate)
                  {
                      _sent = true;
                      if (_canceled && _cancelRequest is null)
                      {
                          cancelRequest = _cancelRequest =
                      _connection.TryCancelRequestAsync();
                      }
                  }

                  _ = cancelRequest;
              },
              _ => PumpAsync(),
              barrier: true,
              cancellationToken).AsTask();
            _ = ObserveOperationAsync();
        }

        public IReadOnlyList<SqlColumn> Columns => _columns;

        public int FieldCount => _columns.Count;

        public ValueTask<bool> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            bool advance;
            lock (_gate)
            {
                ThrowIfError();
                if (_canceled)
                {
                    if (_hasCurrent)
                    {
                        _advance.Set();
                    }

                    return ValueTask.FromException<bool>(
                      new OperationCanceledException(_cancellationToken));
                }

                if (_completed)
                {
                    return ValueTask.FromResult(false);
                }

                if (_hasCurrent && !_currentDelivered)
                {
                    _currentDelivered = true;
                    return ValueTask.FromResult(true);
                }

                if (_readPending)
                {
                    throw new InvalidOperationException("Concurrent row reads are not supported.");
                }

                advance = _hasCurrent;
                _readPending = true;
                _readCompletion.Reset();
                _readCancellationToken = cancellationToken;
                _readCancellation = cancellationToken.CanBeCanceled
                  ? cancellationToken.Register(_cancelAction)
                  : default;
            }

            if (advance)
            {
                _advance.Set();
            }

            return new ValueTask<bool>(this, _readCompletion.Version);
        }

        public bool GetResult(short token)
        {
            CancellationTokenRegistration registration;
            CancellationToken cancellationToken;
            bool result;
            try
            {
                result = _readCompletion.GetResult(token);
            }
            finally
            {
                lock (_gate)
                {
                    registration = _readCancellation;
                    cancellationToken = _readCancellationToken;
                    _readCancellation = default;
                    _readCancellationToken = default;
                    _readPending = false;
                }

                registration.Dispose();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        public ValueTaskSourceStatus GetStatus(short token) =>
          _readCompletion.GetStatus(token);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags) =>
          _readCompletion.OnCompleted(continuation, state, token, flags);

        public bool IsNull(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.IsNull(_current.Payload, ordinal);
        }

        [SuppressMessage(
            "Usage",
            "CA2201:Do not raise reserved exception types",
            Justification = "Matches the IDataRecord.GetOrdinal contract.")]
        public int GetOrdinal(string name)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            for (var i = 0; i < _columns.Count; i++)
            {
                if (string.Equals(_columns[i].Name, name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            throw new IndexOutOfRangeException($"Column '{name}' does not exist.");
        }

        public T Get<T>(int ordinal)
        {
            EnsureCurrent();
            return SqlRowDecoder.Decode<T>(
              _connection._rowDecoder,
              _current.Payload,
              ordinal,
              _columns[ordinal],
              copyReadOnlyMemory: true);
        }

        public TElement[]? GetArray<TElement>(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.DecodeArray<TElement>(
                _current.Payload,
                ordinal,
                _columns[ordinal]);
        }

        public bool GetBoolean(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.DecodeBoolean(
              _current.Payload,
              ordinal,
              _columns[ordinal]);
        }

        public short GetInt16(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.DecodeInt16(
              _current.Payload,
              ordinal,
              _columns[ordinal]);
        }

        public int GetInt32(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.DecodeInt32(
              _current.Payload,
              ordinal,
              _columns[ordinal]);
        }

        public long GetInt64(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.DecodeInt64(
              _current.Payload,
              ordinal,
              _columns[ordinal]);
        }

        public float GetFloat(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.DecodeFloat(
              _current.Payload,
              ordinal,
              _columns[ordinal]);
        }

        public double GetDouble(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.DecodeDouble(
              _current.Payload,
              ordinal,
              _columns[ordinal]);
        }

        public decimal GetDecimal(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.DecodeDecimal(
              _current.Payload,
              ordinal,
              _columns[ordinal]);
        }

        public string GetString(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.DecodeString(
              _current.Payload,
              ordinal,
              _columns[ordinal])!;
        }

        public Guid GetGuid(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.DecodeGuid(
              _current.Payload,
              ordinal,
              _columns[ordinal]);
        }

        public DateOnly GetDateOnly(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.DecodeDateOnly(
              _current.Payload,
              ordinal,
              _columns[ordinal]);
        }

        public TimeOnly GetTimeOnly(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.DecodeTimeOnly(
              _current.Payload,
              ordinal,
              _columns[ordinal]);
        }

        public DateTime GetDateTime(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.DecodeDateTime(
              _current.Payload,
              ordinal,
              _columns[ordinal]);
        }

        public DateTimeOffset GetDateTimeOffset(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.DecodeDateTimeOffset(
              _current.Payload,
              ordinal,
              _columns[ordinal]);
        }

        public byte[] GetBytes(int ordinal)
        {
            EnsureCurrent();
            return _connection._rowDecoder.DecodeBytes(
              _current.Payload,
              ordinal,
              _columns[ordinal])!;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (_gate)
            {
                _stopped = true;
            }

            _advance.Set();
            try
            {
                await _operation.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _operationCancellation.Dispose();
                DisposeCurrent();
            }
        }

        internal void CopyCurrentTo(SqlRowPageBuilder page)
        {
            EnsureCurrent();
            page.Add(_current.Payload.Span);
        }

        private async ValueTask<bool> PumpAsync()
        {
            PgException? serverError = null;
            try
            {
                while (true)
                {
                    var message =
                      await _connection._reader.ReadAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    var retained = false;
                    try
                    {
                        switch (message.Type)
                        {
                            case (byte)'T':
                                _columns = ParseColumns(message.Payload.Span);
                                break;
                            case (byte)'D':
                                ValidateRow(
                                  message.Payload.Span,
                                  _columns);
                                lock (_gate)
                                {
                                    if (!_stopped)
                                    {
                                        _current = message;
                                        _hasCurrent = true;
                                        _currentDelivered = false;
                                        retained = true;
                                    }
                                }

                                if (retained)
                                {
                                    SignalRead(result: true, error: null);
                                    await _advance.WaitAsync().ConfigureAwait(false);
                                    DisposeCurrent();
                                }

                                break;
                            case (byte)'E':
                                serverError = ParseError(message.Payload.Span);
                                break;
                            case (byte)'N':
                                _connection.HandleNotice(message.Payload.Span);
                                break;
                            case (byte)'S':
                                _connection.HandleParameterStatus(message.Payload.Span);
                                break;
                            case (byte)'A':
                                _connection.HandleNotification(message.Payload.Span);
                                break;
                            case (byte)'1':
                            case (byte)'2':
                            case (byte)'3':
                            case (byte)'C':
                            case (byte)'I':
                            case (byte)'n':
                            case (byte)'t':
                                break;
                            case (byte)'Z':
                                _connection.UpdateTransactionStatus(message.Payload.Span);
                                Task? cancelRequest;
                                lock (_gate)
                                {
                                    cancelRequest = _cancelRequest;
                                }

                                if (cancelRequest is not null)
                                {
                                    await cancelRequest.ConfigureAwait(false);
                                }

                                bool canceled;
                                lock (_gate)
                                {
                                    canceled = _canceled;
                                }

                                if (canceled)
                                {
                                    throw new OperationCanceledException(_cancellationToken);
                                }

                                if (serverError is not null)
                                {
                                    throw serverError;
                                }

                                Complete(error: null);
                                return true;
                            default:
                                throw new InvalidDataException(
                                  $"Unexpected PostgreSQL reader message '{(char)message.Type}'.");
                        }
                    }
                    finally
                    {
                        if (!retained)
                        {
                            message.Dispose();
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Complete(exception);
                throw;
            }
        }

        private async Task ObserveOperationAsync()
        {
            try
            {
                await _operation.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Complete(exception);
            }
        }

        private void Cancel()
        {
            bool advance;
            lock (_gate)
            {
                if (_completed || _canceled)
                {
                    return;
                }

                _canceled = true;
                _stopped = true;
                _cancellationToken = _readCancellationToken.IsCancellationRequested
                  ? _readCancellationToken
                  : _operationCancellationToken;
                advance = !_hasCurrent || !_currentDelivered;
                if (_sent)
                {
                    _cancelRequest = _connection.TryCancelRequestAsync();
                }
            }

            if (advance)
            {
                _advance.Set();
            }
        }

        private void Complete(Exception? error)
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _error = error;
                _completed = true;
            }

            SignalRead(result: false, error);
        }

        private void DisposeCurrent()
        {
            lock (_gate)
            {
                if (!_hasCurrent)
                {
                    return;
                }

                _current.Dispose();
                _current = default;
                _hasCurrent = false;
                _currentDelivered = false;
            }
        }

        private void EnsureCurrent()
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            lock (_gate)
            {
                ThrowIfError();
                if (!_hasCurrent)
                {
                    throw new InvalidOperationException("ReadAsync must return true first.");
                }
            }
        }

        private void ThrowIfError()
        {
            if (_error is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                  .Capture(_error)
                  .Throw();
            }
        }

        private void SignalRead(bool result, Exception? error)
        {
            bool signal;
            lock (_gate)
            {
                signal = _readPending;
                if (signal && result)
                {
                    _currentDelivered = true;
                }
            }

            if (!signal)
            {
                return;
            }

            if (error is not null)
            {
                _readCompletion.SetException(error);
            }
            else
            {
                _readCompletion.SetResult(result);
            }
        }
    }

    internal readonly record struct PortalPage(SqlRowSet Rows, bool HasMore);
}

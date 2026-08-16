using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Apex.MySqlClient.Internal;
using Apex.SqlClient;

namespace Apex.MySqlClient;

public sealed partial class MySqlConnection
{
    internal static async ValueTask<MySqlConnection> ConnectAsync(
        MySqlConnectOptions options,
        CancellationToken cancellationToken)
    {
        ValidateOptions(options);
        var socket = CreateSocket(options);
        Stream? stream = null;
        try
        {
            using CancellationTokenSource timeout =
              CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.ConnectTimeout);
            if (IsUnixSocket(options))
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.Host), timeout.Token)
                  .ConfigureAwait(false);
            }
            else
            {
                await socket.ConnectAsync(options.Host, options.Port, timeout.Token).ConfigureAwait(false);
            }

            stream = new NetworkStream(socket, ownsSocket: false);
            MySqlHandshake handshake;
            using (var packet =
              await MySqlPacketReader.ReadFromStreamAsync(stream, timeout.Token).ConfigureAwait(false))
            {
                if (MySqlPackets.IsError(packet.Span))
                {
                    throw MySqlPackets.ReadError(packet.Span);
                }

                handshake = MySqlHandshake.Parse(packet.Span, packet.Sequence);
            }

            var version = ParseServerVersion(handshake.ServerVersion);
            var unixSocket = IsUnixSocket(options);
            if (unixSocket &&
                options.SslMode is MySqlSslMode.Required or
                  MySqlSslMode.VerifyCa or
                  MySqlSslMode.VerifyIdentity)
            {
                throw new InvalidOperationException(
                  "MySQL TLS cannot be negotiated over a Unix domain socket.");
            }

            var serverSupportsTls =
              !unixSocket &&
              (handshake.Capabilities & MySqlCapabilities.Ssl) != 0;
            var upgrade = options.SslMode switch
            {
                MySqlSslMode.Disabled => false,
                MySqlSslMode.Preferred => serverSupportsTls,
                _ => true,
            };
            if (upgrade && !serverSupportsTls && options.SslMode != MySqlSslMode.Preferred)
            {
                throw new AuthenticationException("The MySQL server does not support TLS.");
            }

            var capabilities = ComputeCapabilities(options, handshake, upgrade);
            var sequence = handshake.Sequence;
            if (upgrade)
            {
                sequence++;
                await WriteSslRequestAsync(stream, options, capabilities, sequence, timeout.Token)
                  .ConfigureAwait(false);
                try
                {
                    stream = await UpgradeToTlsAsync(stream, options, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                  options.SslMode == MySqlSslMode.Preferred &&
                  !timeout.IsCancellationRequested &&
                  exception is AuthenticationException or IOException)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    stream = null;
                    socket.Dispose();
                    return await ConnectAsync(
                      options with { SslMode = MySqlSslMode.Disabled },
                      cancellationToken).ConfigureAwait(false);
                }
            }

            MySqlConnection connection = new(options, socket, stream, upgrade, capabilities);
            stream = null;
            try
            {
                connection._connectionId = handshake.ConnectionId;
                connection.SetServerVersion(version);
                await connection.AuthenticateAsync(handshake, (byte)(sequence + 1), timeout.Token)
                  .ConfigureAwait(false);
                await connection.ApplySessionVariablesAsync(timeout.Token).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            socket.Dispose();
            throw;
        }
    }

    internal static MySqlServerVersion ParseServerVersion(string serverVersion)
    {
        var isMariaDb = serverVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
        var normalized = isMariaDb
          ? serverVersion.Replace("5.5.5-", string.Empty, StringComparison.Ordinal)
          : serverVersion;
        var end = normalized.AsSpan().IndexOfAny('-', ' ');
        var token = end < 0 ? normalized.AsSpan() : normalized.AsSpan(0, end);
        var major = 0;
        var minor = 0;
        var micro = 0;
        Span<Range> parts = stackalloc Range[4];
        var count = token.Split(parts, '.');
        if (count > 0)
        {
            _ = int.TryParse(token[parts[0]], out major);
        }

        if (count > 1)
        {
            _ = int.TryParse(token[parts[1]], out minor);
        }

        if (count > 2)
        {
            _ = int.TryParse(token[parts[2]], out micro);
        }

        return new MySqlServerVersion(serverVersion, major, minor, micro, isMariaDb);
    }

    private static MySqlCapabilities ComputeCapabilities(
        MySqlConnectOptions options,
        MySqlHandshake handshake,
        bool upgradeToTls)
    {
        var client =
          MySqlCapabilities.LongPassword |
          MySqlCapabilities.LongFlag |
          MySqlCapabilities.Protocol41 |
          MySqlCapabilities.Transactions |
          MySqlCapabilities.SecureConnection |
          MySqlCapabilities.PluginAuth |
          MySqlCapabilities.PluginAuthLengthEncodedClientData |
          MySqlCapabilities.MultiResults |
          MySqlCapabilities.PreparedStatementMultiResults |
          MySqlCapabilities.DeprecateEof;
        if (options.Database.Length > 0)
        {
            client |= MySqlCapabilities.ConnectWithDatabase;
        }

        if (options.ConnectionAttributes.Count > 0)
        {
            client |= MySqlCapabilities.ConnectAttributes;
        }

        if (options.AllowMultiStatements)
        {
            client |= MySqlCapabilities.MultiStatements;
        }

        if (options.AllowLoadLocalInfile)
        {
            client |= MySqlCapabilities.LocalFiles;
        }

        if (!options.UseAffectedRows)
        {
            client |= MySqlCapabilities.FoundRows;
        }

        var negotiated = client & handshake.Capabilities;
        if ((negotiated & MySqlCapabilities.Protocol41) == 0)
        {
            throw new NotSupportedException(
              "The server does not support the 4.1 protocol, which this driver requires.");
        }

        if (upgradeToTls)
        {
            negotiated |= MySqlCapabilities.Ssl;
        }

        return negotiated;
    }

    private static async ValueTask WriteSslRequestAsync(
        Stream stream,
        MySqlConnectOptions options,
        MySqlCapabilities capabilities,
        byte sequence,
        CancellationToken cancellationToken)
    {
        var request = new byte[MySqlProtocol.PacketHeaderLength + 32];
        MySqlPacketReader.WriteHeader(request, 32, sequence);
        var payload = request.AsSpan(MySqlProtocol.PacketHeaderLength);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)capabilities);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
          payload[4..],
          MySqlProtocol.MaximumFramePayloadLength);
        payload[8] = options.Collation;
        payload[9..].Clear();
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void SetServerVersion(MySqlServerVersion version)
    {
        _serverVersion = version;
        _databaseMetadata = new DatabaseMetadata(
          version.ProductName,
          version.FullVersion,
          version.Major,
          version.Minor);
    }

    private async ValueTask AuthenticateAsync(
        MySqlHandshake handshake,
        byte sequence,
        CancellationToken cancellationToken)
    {
        var password = MySqlAuthentication.GetPasswordBytes(_options.Password);
        try
        {
            AuthenticationState state = new(
              SelectAuthenticationPlugin(handshake.AuthenticationPlugin),
              handshake.Nonce);
            state = CreateInitialResponse(state, password);
            WriteHandshakeResponse(sequence, state.Plugin, state.Response ?? []);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            while (true)
            {
                using var packet = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                state = ProcessAuthenticationPacket(packet.Span, packet.Sequence, password, state);
                if (state.Completed)
                {
                    return;
                }

                if (state.Response is not null)
                {
                    WritePacket(state.Sequence, state.Response);
                    await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
        }
    }

    private AuthenticationState ProcessAuthenticationPacket(
        ReadOnlySpan<byte> payload,
        byte sequence,
        byte[] password,
        AuthenticationState state)
    {
        if (MySqlPackets.IsError(payload))
        {
            throw ReadCommandError(payload);
        }

        var header = payload.Length == 0 ? (byte)0 : payload[0];
        var next = (byte)(sequence + 1);
        if (header == MySqlProtocol.OkHeader)
        {
            var completion = MySqlPackets.ReadOk(payload, _capabilities);
            _status = completion.Status;
            _lastCommandInfo = completion.ToCommandInfo();
            return state.Complete();
        }

        if (header == MySqlProtocol.EofHeader)
        {
            MySqlPayloadReader reader = new(payload);
            reader.Skip(1);
            var switched = reader.Remaining > 0
              ? reader.ReadNullTerminatedString()
              : MySqlProtocol.NativePasswordPlugin;
            var switchedNonce = reader.ReadRemainingSpan();
            if (switchedNonce.Length > MySqlProtocol.NonceLength)
            {
                switchedNonce = switchedNonce[..MySqlProtocol.NonceLength];
            }

            AuthenticationState switchedState =
              new(ValidatePlugin(switched), switchedNonce.ToArray());
            return CreateInitialResponse(switchedState with { Sequence = next }, password);
        }

        if (header == MySqlProtocol.AuthMoreDataHeader)
        {
            return CreateContinuationResponse(payload, password, state with { Sequence = next });
        }

        throw new InvalidDataException(
          $"Unexpected MySQL authentication packet header 0x{header:X2}.");
    }

    private AuthenticationState CreateInitialResponse(AuthenticationState state, byte[] password)
    {
        switch (state.Plugin)
        {
            case MySqlProtocol.NativePasswordPlugin:
                return state with
                {
                    Response = MySqlAuthentication.ScrambleNativePassword(password, state.Nonce),
                };
            case MySqlProtocol.CachingSha2PasswordPlugin:
                return state with
                {
                    Response = MySqlAuthentication.ScrambleCachingSha2Password(password, state.Nonce),
                };
            case MySqlProtocol.ClearPasswordPlugin:
                if (!IsSecure)
                {
                    throw new AuthenticationException("mysql_clear_password requires TLS.");
                }

                return state with
                {
                    Response = MySqlAuthentication.GetNullTerminatedPassword(password),
                };
            case MySqlProtocol.Sha256PasswordPlugin:
                if (password.Length == 0)
                {
                    return state with { Response = [] };
                }

                if (IsSecure)
                {
                    return state with
                    {
                        Response = MySqlAuthentication.GetNullTerminatedPassword(password),
                    };
                }

                if (_options.ServerRsaPublicKey is { Length: > 0 } key)
                {
                    return state with
                    {
                        Response = MySqlAuthentication.EncryptPassword(password, state.Nonce, key),
                    };
                }

                if (!_options.AllowPublicKeyRetrieval)
                {
                    throw new AuthenticationException(
                      "sha256_password requires TLS, a configured server RSA public key, or " +
                      "AllowPublicKeyRetrieval.");
                }

                return state with
                {
                    AwaitingPublicKey = true,
                    Response = [MySqlProtocol.Sha256PublicKeyRequest],
                };
            default:
                throw new NotSupportedException(
                  $"MySQL authentication plugin '{state.Plugin}' is not supported.");
        }
    }

    private AuthenticationState CreateContinuationResponse(
        ReadOnlySpan<byte> payload,
        byte[] password,
        AuthenticationState state)
    {
        if (state.AwaitingPublicKey)
        {
            var publicKey = s_utf8.GetString(payload[1..]).TrimEnd('\0');
            return state with
            {
                AwaitingPublicKey = false,
                Response = MySqlAuthentication.EncryptPassword(password, state.Nonce, publicKey),
            };
        }

        var flag = payload.Length > 1 ? payload[1] : (byte)0;
        if (flag == MySqlProtocol.AuthFastSuccess)
        {
            return state with { Response = null };
        }

        if (flag != MySqlProtocol.AuthFullAuthentication)
        {
            throw new InvalidDataException(
              $"Unexpected MySQL authentication continuation flag 0x{flag:X2}.");
        }

        if (IsSecure)
        {
            return state with
            {
                Response = MySqlAuthentication.GetNullTerminatedPassword(password),
            };
        }

        if (_options.ServerRsaPublicKey is { Length: > 0 } key)
        {
            return state with
            {
                Response = MySqlAuthentication.EncryptPassword(password, state.Nonce, key),
            };
        }

        if (!_options.AllowPublicKeyRetrieval)
        {
            throw new AuthenticationException(
              "SHA-2 full authentication requires TLS, a configured server RSA public key, or " +
              "AllowPublicKeyRetrieval.");
        }

        return state with
        {
            AwaitingPublicKey = true,
            Response = [MySqlProtocol.AuthPublicKeyRequest],
        };
    }

    private sealed record AuthenticationState(string Plugin, byte[] Nonce)
    {
        internal byte[]? Response { get; init; }

        internal bool AwaitingPublicKey { get; init; }

        internal byte Sequence { get; init; }

        internal bool Completed { get; init; }

        internal AuthenticationState Complete() => this with { Completed = true, Response = null };
    }

    private string SelectAuthenticationPlugin(string serverPlugin) =>
      _options.AuthenticationPlugin switch
      {
          MySqlAuthenticationPlugin.NativePassword => MySqlProtocol.NativePasswordPlugin,
          MySqlAuthenticationPlugin.CachingSha2Password => MySqlProtocol.CachingSha2PasswordPlugin,
          MySqlAuthenticationPlugin.Sha256Password => MySqlProtocol.Sha256PasswordPlugin,
          MySqlAuthenticationPlugin.ClearPassword => ValidateCleartext(),
          _ => ValidatePlugin(serverPlugin),
      };

    private string ValidatePlugin(string plugin) =>
      plugin switch
      {
          MySqlProtocol.NativePasswordPlugin or
          MySqlProtocol.CachingSha2PasswordPlugin or
          MySqlProtocol.Sha256PasswordPlugin => plugin,
          MySqlProtocol.ClearPasswordPlugin => ValidateCleartext(),
          _ => throw new NotSupportedException(
          $"MySQL authentication plugin '{plugin}' is not supported."),
      };

    private string ValidateCleartext() =>
      _options.AllowCleartextPassword && IsSecure
        ? MySqlProtocol.ClearPasswordPlugin
        : throw new AuthenticationException(
          "mysql_clear_password requires TLS and AllowCleartextPassword.");

    private void WriteHandshakeResponse(byte sequence, string plugin, byte[] authenticationResponse)
    {
        _payload.Reset();
        _payload.WriteUInt32((uint)_capabilities);
        _payload.WriteUInt32(MySqlProtocol.MaximumFramePayloadLength);
        _payload.WriteByte(_options.Collation);
        _payload.WriteZero(23);
        _payload.WriteNullTerminatedString(_options.Username);
        if ((_capabilities & MySqlCapabilities.PluginAuthLengthEncodedClientData) != 0)
        {
            _payload.WriteLengthEncodedBytes(authenticationResponse);
        }
        else if ((_capabilities & MySqlCapabilities.SecureConnection) != 0)
        {
            if (authenticationResponse.Length > byte.MaxValue)
            {
                throw new InvalidOperationException(
                  "The authentication response is too large for the negotiated capabilities.");
            }

            _payload.WriteByte((byte)authenticationResponse.Length);
            _payload.WriteBytes(authenticationResponse);
        }
        else
        {
            _payload.WriteByte(0);
        }

        if ((_capabilities & MySqlCapabilities.ConnectWithDatabase) != 0)
        {
            _payload.WriteNullTerminatedString(_options.Database);
        }

        if ((_capabilities & MySqlCapabilities.PluginAuth) != 0)
        {
            _payload.WriteNullTerminatedString(plugin);
        }

        if ((_capabilities & MySqlCapabilities.ConnectAttributes) != 0)
        {
            WriteConnectionAttributes();
        }

        _writer.WritePacket(sequence, _payload.WrittenSpan);
    }

    private void WriteConnectionAttributes()
    {
        var length = 0;
        foreach ((var key, var value) in _options.ConnectionAttributes)
        {
            length = checked(length + GetLengthEncodedSize(key) + GetLengthEncodedSize(value));
        }

        _payload.WriteLengthEncodedInteger((ulong)length);
        foreach ((var key, var value) in _options.ConnectionAttributes)
        {
            _payload.WriteLengthEncodedString(key);
            _payload.WriteLengthEncodedString(value);
        }
    }

    private static int GetLengthEncodedSize(string value)
    {
        var byteCount = s_utf8.GetByteCount(value);
        var prefix = byteCount switch
        {
            < 0xFB => 1,
            <= ushort.MaxValue => 3,
            <= 0xFFFFFF => 4,
            _ => 9,
        };
        return checked(prefix + byteCount);
    }

    private void WritePacket(byte sequence, ReadOnlySpan<byte> payload) =>
      _writer.WritePacket(sequence, payload);

    private async ValueTask ApplySessionVariablesAsync(CancellationToken cancellationToken)
    {
        if (_options.SessionVariables.Count == 0)
        {
            return;
        }

        StringBuilder builder = new("SET ");
        var first = true;
        foreach ((var name, var value) in _options.SessionVariables)
        {
            if (name.Length == 0 ||
                name.Any(static character =>
                  !char.IsAsciiLetterOrDigit(character) && character != '_'))
            {
                throw new ArgumentException(
                  $"MySQL session variable name '{name}' is invalid.");
            }

            if (!first)
            {
                builder.Append(", ");
            }

            first = false;
            builder.Append("SESSION ").Append(name).Append('=').Append(value);
        }

        _writer.WriteTextCommand(MySqlCommand.Query, builder.ToString());
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        using var packet = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        HandleCompletionPacket(packet.Span);
    }

    private void HandleCompletionPacket(ReadOnlySpan<byte> payload)
    {
        if (MySqlPackets.IsError(payload))
        {
            throw ReadCommandError(payload);
        }

        var completion = payload.Length > 0 && payload[0] == MySqlProtocol.EofHeader
          ? MySqlPackets.ReadEof(payload, _capabilities)
          : MySqlPackets.ReadOk(payload, _capabilities);
        _status = completion.Status;
        _lastCommandInfo = completion.ToCommandInfo();
    }
}

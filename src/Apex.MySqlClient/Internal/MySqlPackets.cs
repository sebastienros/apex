namespace Apex.MySqlClient.Internal;

/// <summary>A statement prepared on one physical connection.</summary>
internal sealed class MySqlStatement
{
    internal MySqlStatement(
        uint id,
        string sql,
        int parameterCount,
        MySqlColumnMetadata[] columns)
    {
        Id = id;
        Sql = sql;
        Operation = GetOperation(sql);
        ParameterCount = parameterCount;
        Columns = columns;
    }

    internal uint Id { get; }

    internal string Sql { get; }

    internal string Operation { get; }

    internal int ParameterCount { get; }

    internal MySqlColumnMetadata[] Columns { get; }

    /// <summary>Gets or sets a value indicating whether the statement cache owns the statement.</summary>
    internal bool IsCached { get; set; }

    private static string GetOperation(string sql)
    {
        var text = sql.AsSpan().TrimStart();
        var separator = text.IndexOfAny(" \t\r\n");
        return (separator < 0 ? text : text[..separator]).ToString().ToUpperInvariant();
    }
}

/// <summary>The initial handshake sent by the server.</summary>
internal readonly struct MySqlHandshake
{
    internal MySqlHandshake(
        string serverVersion,
        uint connectionId,
        byte[] nonce,
        MySqlCapabilities capabilities,
        string authenticationPlugin,
        byte sequence)
    {
        ServerVersion = serverVersion;
        ConnectionId = connectionId;
        Nonce = nonce;
        Capabilities = capabilities;
        AuthenticationPlugin = authenticationPlugin;
        Sequence = sequence;
    }

    internal string ServerVersion { get; }

    internal uint ConnectionId { get; }

    internal byte[] Nonce { get; }

    internal MySqlCapabilities Capabilities { get; }

    internal string AuthenticationPlugin { get; }

    internal byte Sequence { get; }

    internal static MySqlHandshake Parse(ReadOnlySpan<byte> payload, byte sequence)
    {
        MySqlPayloadReader reader = new(payload);
        var protocolVersion = reader.ReadByte();
        if (protocolVersion != 10)
        {
            throw new NotSupportedException(
              $"MySQL protocol version {protocolVersion} is not supported.");
        }

        var serverVersion = reader.ReadNullTerminatedString();
        var connectionId = reader.ReadUInt32();
        var nonce = new byte[MySqlProtocol.NonceLength];
        reader.ReadSpan(8).CopyTo(nonce);
        reader.Skip(1);
        uint capabilities = reader.ReadUInt16();
        if (reader.Remaining > 0)
        {
            reader.Skip(1);
            _ = reader.ReadUInt16();
            capabilities |= (uint)reader.ReadUInt16() << 16;
            var pluginAuth = (capabilities & (uint)MySqlCapabilities.PluginAuth) != 0;
            int authPluginDataLength = reader.ReadByte();
            if (!pluginAuth)
            {
                authPluginDataLength = 0;
            }

            reader.Skip(10);
            var remaining = Math.Max(
              MySqlProtocol.NonceLength - 8,
              authPluginDataLength - 9);
            var part2 = reader.ReadSpan(Math.Min(remaining, reader.Remaining));
            part2[..Math.Min(part2.Length, MySqlProtocol.NonceLength - 8)].CopyTo(nonce.AsSpan(8));
            if (reader.Remaining > 0)
            {
                reader.Skip(1);
            }

            var plugin = pluginAuth && reader.Remaining > 0
              ? reader.ReadNullTerminatedString()
              : MySqlProtocol.NativePasswordPlugin;
            return new MySqlHandshake(
              serverVersion,
              connectionId,
              nonce,
              (MySqlCapabilities)capabilities,
              plugin,
              sequence);
        }

        return new MySqlHandshake(
          serverVersion,
          connectionId,
          nonce,
          (MySqlCapabilities)capabilities,
          MySqlProtocol.NativePasswordPlugin,
          sequence);
    }
}

/// <summary>The outcome carried by an OK or EOF packet.</summary>
internal readonly struct MySqlCompletion
{
    internal MySqlCompletion(
        long affectedRows,
        ulong lastInsertId,
        MySqlServerStatus status,
        int warnings,
        string info)
    {
        AffectedRows = affectedRows;
        LastInsertId = lastInsertId;
        Status = status;
        Warnings = warnings;
        Info = info;
    }

    internal long AffectedRows { get; }

    internal ulong LastInsertId { get; }

    internal MySqlServerStatus Status { get; }

    internal int Warnings { get; }

    internal string Info { get; }

    internal MySqlCommandInfo ToCommandInfo() =>
      new(AffectedRows, LastInsertId, Status, Warnings, Info);
}

/// <summary>Parses the control packets shared by every MySQL command.</summary>
internal static class MySqlPackets
{
    internal static bool IsOk(ReadOnlySpan<byte> payload) =>
      payload.Length > 0 && payload[0] == MySqlProtocol.OkHeader && payload.Length >= 7;

    internal static bool IsError(ReadOnlySpan<byte> payload) =>
      payload.Length > 0 && payload[0] == MySqlProtocol.ErrorHeader;

    internal static bool IsEof(ReadOnlySpan<byte> payload, bool deprecateEof) =>
      payload.Length > 0 &&
      payload[0] == MySqlProtocol.EofHeader &&
      (deprecateEof
        ? payload.Length < MySqlProtocol.MaximumFramePayloadLength
        : payload.Length < 9);

    internal static MySqlCompletion ReadOk(ReadOnlySpan<byte> payload, MySqlCapabilities capabilities)
    {
        MySqlPayloadReader reader = new(payload);
        reader.Skip(1);
        var affectedRows = ToAffectedRows(reader.ReadRequiredLengthEncodedInteger());
        var lastInsertId = reader.ReadRequiredLengthEncodedInteger();
        var status = MySqlServerStatus.None;
        var warnings = 0;
        if ((capabilities & MySqlCapabilities.Protocol41) != 0)
        {
            status = (MySqlServerStatus)reader.ReadUInt16();
            warnings = reader.ReadUInt16();
        }
        else if ((capabilities & MySqlCapabilities.Transactions) != 0)
        {
            status = (MySqlServerStatus)reader.ReadUInt16();
        }

        var info = reader.Remaining > 0 ? reader.ReadRemainingString() : string.Empty;
        return new MySqlCompletion(affectedRows, lastInsertId, status, warnings, info);
    }

    internal static MySqlCompletion ReadEof(
        ReadOnlySpan<byte> payload,
        MySqlCapabilities capabilities)
    {
        MySqlPayloadReader reader = new(payload);
        reader.Skip(1);
        var warnings = 0;
        var status = MySqlServerStatus.None;
        if ((capabilities & MySqlCapabilities.Protocol41) != 0 && reader.Remaining >= 4)
        {
            warnings = reader.ReadUInt16();
            status = (MySqlServerStatus)reader.ReadUInt16();
        }

        return new MySqlCompletion(0, 0, status, warnings, string.Empty);
    }

    internal static MySqlException ReadError(ReadOnlySpan<byte> payload)
    {
        MySqlPayloadReader reader = new(payload);
        reader.Skip(1);
        int errorNumber = reader.ReadUInt16();
        string? sqlState = null;
        if (reader.Remaining >= 6 && reader.PeekByte() == (byte)'#')
        {
            reader.Skip(1);
            var state = reader.ReadSpan(5);
            Span<char> characters = stackalloc char[5];
            for (var i = 0; i < state.Length; i++)
            {
                characters[i] = (char)state[i];
            }

            sqlState = new string(characters);
        }

        var message = reader.ReadRemainingString();
        return new MySqlException(
          errorNumber,
          sqlState,
          message.Length == 0 ? $"MySQL error {errorNumber}." : message);
    }

    private static long ToAffectedRows(ulong value) =>
      value <= long.MaxValue ? (long)value : throw new InvalidDataException(
        $"MySQL reported {value} affected rows, which exceeds the supported range.");
}

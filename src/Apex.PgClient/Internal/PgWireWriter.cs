using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Apex.SqlClient;

namespace Apex.PgClient.Internal;

internal sealed class PgWireWriter
{
    private static readonly Encoding s_utf8 = new UTF8Encoding(false, true);
    private readonly Stream _stream;
    private readonly ArrayBufferWriter<byte> _writer = new(16 * 1024);
    private readonly PgTypeRegistry _typeRegistry;

    public PgWireWriter(Stream stream, PgTypeRegistry typeRegistry)
    {
        _stream = stream;
        _typeRegistry = typeRegistry;
    }

    public ValueTask WriteStartupAsync(
        PgConnectOptions options,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> payload = new();
        payload.WriteInt32(196608);
        payload.WriteCString("user");
        payload.WriteCString(options.Username);
        payload.WriteCString("database");
        payload.WriteCString(options.Database);
        foreach ((var key, var value) in options.Properties)
        {
            payload.WriteCString(key);
            payload.WriteCString(value);
        }

        payload.WriteByte(0);
        return WriteUntypedAsync(payload.WrittenMemory, cancellationToken);
    }

    public ValueTask WritePasswordAsync(
        string password,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> payload = new();
        payload.WriteCString(password);
        return WriteTypedAsync((byte)'p', payload.WrittenMemory, cancellationToken);
    }

    public ValueTask WriteSaslInitialAsync(
        string mechanism,
        string message,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> payload = new();
        payload.WriteCString(mechanism);
        var byteCount = s_utf8.GetByteCount(message);
        payload.WriteInt32(byteCount);
        payload.WriteUtf8(message);
        return WriteTypedAsync((byte)'p', payload.WrittenMemory, cancellationToken);
    }

    public ValueTask WriteSaslResponseAsync(
        string message,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> payload = new();
        payload.WriteUtf8(message);
        return WriteTypedAsync((byte)'p', payload.WrittenMemory, cancellationToken);
    }

    public async ValueTask WriteQueryAsync(
        string sql,
        CancellationToken cancellationToken,
        bool flush = true)
    {
        PgBufferWriterExtensions.ThrowIfContainsNull(sql);
        var byteCount = s_utf8.GetByteCount(sql);
        var messageLength = checked(sizeof(int) + byteCount + 1);
        var totalLength = checked(1 + messageLength);
        var message = _writer.GetSpan(totalLength);
        message[0] = (byte)'Q';
        BinaryPrimitives.WriteInt32BigEndian(
          message[1..],
          messageLength);
        var written = s_utf8.GetBytes(sql, message[5..]);
        message[5 + written] = 0;
        _writer.Advance(totalLength);
        if (flush)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask WriteExtendedQueryAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken,
        bool flush = true)
    {
        if (parameters.Count == 0)
        {
            WriteParseUnnamed(sql);
            WriteBindDescribeExecuteNoParameters(
              portalName: string.Empty,
              statementName: string.Empty,
              fetchSize: 0);
            if (flush)
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var mark = _writer.WrittenCount;
        try
        {
            WriteParseUnnamed(sql);
            WriteBindDescribeExecute(string.Empty, string.Empty, parameters, 0);
        }
        catch
        {
            RollbackTo(mark);
            throw;
        }

        if (flush)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask WriteExtendedQueryAsync(
        string sql,
        PgParameters parameters,
        CancellationToken cancellationToken)
    {
        var mark = _writer.WrittenCount;
        try
        {
            WriteTypedParse(sql, parameters);
            WriteBindDescribeExecute(string.Empty, string.Empty, parameters, 0);
        }
        catch
        {
            RollbackTo(mark);
            throw;
        }

        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteBatchAsync(
        PgBatch batch,
        CancellationToken cancellationToken)
    {
        var mark = _writer.WrittenCount;
        try
        {
            for (var i = 0; i < batch.Count; i++)
            {
                var command = batch[i];
                WriteTypedParse(command.Sql, command.Parameters);
                WriteBindDescribeExecute(
                    string.Empty,
                    string.Empty,
                    command.Parameters,
                    fetchSize: 0,
                    sync: false);
            }
        }
        catch
        {
            RollbackTo(mark);
            throw;
        }

        WriteTyped((byte)'S', ReadOnlySpan<byte>.Empty);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WritePrepareAsync(
        string name,
        string sql,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> parse = new();
        parse.WriteCString(name);
        parse.WriteCString(sql);
        parse.WriteInt16(0);
        WriteTyped((byte)'P', parse.WrittenSpan);
        WriteDescribeStatement(name);
        WriteTyped((byte)'S', ReadOnlySpan<byte>.Empty);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WritePreparedQueryAsync(
        string name,
        SqlParameters parameters,
        CancellationToken cancellationToken,
        bool describePortal = true,
        bool flush = true)
    {
        if (parameters.Count == 0)
        {
            WriteBindDescribeExecuteNoParameters(
                string.Empty,
                name,
                fetchSize: 0,
                describePortal);
        }
        else
        {
            WriteBindDescribeExecute(
                string.Empty,
                name,
                parameters,
                fetchSize: 0,
                describePortal);
        }

        if (flush)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Drops messages appended after mark when building a command fails (for example a
    // parameter that cannot be encoded), so no half-written command reaches the server with the
    // next flush. Earlier unflushed pipelined messages before mark are kept: ResetWrittenCount
    // leaves the buffer contents in place, so re-advancing restores exactly that prefix.
    private void RollbackTo(int mark)
    {
        if (_writer.WrittenCount == mark)
        {
            return;
        }

        _writer.ResetWrittenCount();
        _writer.Advance(mark);
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (_writer.WrittenCount == 0)
        {
            return;
        }

        await _stream.WriteAsync(
            _writer.WrittenMemory,
            cancellationToken).ConfigureAwait(false);
        // Clear() would zero every written byte on each flush; the bytes are overwritten anyway.
        _writer.ResetWrittenCount();
    }

    public async ValueTask WriteCopyDataAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        WriteTyped((byte)'d', payload.Span);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteCopyDoneAsync(CancellationToken cancellationToken)
    {
        WriteTyped((byte)'c', ReadOnlySpan<byte>.Empty);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteCopyFailAsync(
        string message,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> payload = new();
        payload.WriteCString(message);
        WriteTyped((byte)'f', payload.WrittenSpan);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteOpenPortalAsync(
        string portalName,
        string statementName,
        SqlParameters parameters,
        int fetchSize,
        CancellationToken cancellationToken)
    {
        WriteBindDescribeExecute(portalName, statementName, parameters, fetchSize);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteExecutePortalAsync(
        string portalName,
        int fetchSize,
        CancellationToken cancellationToken)
    {
        WriteExecute(portalName, fetchSize);
        WriteTyped((byte)'S', ReadOnlySpan<byte>.Empty);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteClosePortalAsync(
        string portalName,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> close = new();
        close.WriteByte((byte)'P');
        close.WriteCString(portalName);
        WriteTyped((byte)'C', close.WrittenSpan);
        WriteTyped((byte)'S', ReadOnlySpan<byte>.Empty);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteCloseStatementAsync(
        string name,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> close = new();
        close.WriteByte((byte)'S');
        close.WriteCString(name);
        WriteTyped((byte)'C', close.WrittenSpan);
        WriteTyped((byte)'S', ReadOnlySpan<byte>.Empty);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask WriteTerminateAsync(CancellationToken cancellationToken) =>
        WriteTypedAsync((byte)'X', ReadOnlyMemory<byte>.Empty, cancellationToken);

    public static string Md5Password(string password, string username, ReadOnlySpan<byte> salt)
    {
        var firstInput = s_utf8.GetBytes(password + username);
        Span<byte> firstHash = stackalloc byte[MD5.HashSizeInBytes];
        MD5.HashData(firstInput, firstHash);

        var firstHex = Convert.ToHexStringLower(firstHash);
        var secondPrefix = s_utf8.GetBytes(firstHex);
        var secondInput = GC.AllocateUninitializedArray<byte>(secondPrefix.Length + salt.Length);
        secondPrefix.CopyTo(secondInput, 0);
        salt.CopyTo(secondInput.AsSpan(secondPrefix.Length));

        Span<byte> secondHash = stackalloc byte[MD5.HashSizeInBytes];
        MD5.HashData(secondInput, secondHash);
        return "md5" + Convert.ToHexStringLower(secondHash);
    }

    private async ValueTask WriteUntypedAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var length = _writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(length, checked(payload.Length + sizeof(int)));
        _writer.Advance(sizeof(int));
        _writer.Write(payload.Span);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteTypedAsync(
        byte type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        WriteTyped(type, payload.Span);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void WriteTyped(byte type, ReadOnlySpan<byte> payload)
    {
        var header = _writer.GetSpan(5);
        header[0] = type;
        BinaryPrimitives.WriteInt32BigEndian(header[1..], checked(payload.Length + sizeof(int)));
        _writer.Advance(5);
        _writer.Write(payload);
    }

    private void WriteBindDescribeExecute(
        string portalName,
        string statementName,
        SqlParameters parameters,
        int fetchSize,
        bool describePortal = true)
    {
        // Bind is written straight into the connection buffer; a parameter that fails to format
        // must not leave half a message behind for the next flush.
        var mark = _writer.WrittenCount;
        try
        {
            var bind = BeginMessage((byte)'B');
            _writer.WriteCString(portalName);
            _writer.WriteCString(statementName);
            _writer.WriteInt16(0);
            _writer.WriteInt16(checked((short)parameters.Count));
            for (var i = 0; i < parameters.Count; i++)
            {
                var value = parameters[i];
                if (value.IsNull)
                {
                    _writer.WriteInt32(-1);
                    continue;
                }

                WriteTextParameter(value);
            }

            _writer.WriteInt16(1);
            _writer.WriteInt16(1);
            EndMessage(bind);

            if (describePortal)
            {
                WriteDescribePortal(portalName);
            }

            WriteExecute(portalName, fetchSize);
            WriteTyped((byte)'S', ReadOnlySpan<byte>.Empty);
        }
        catch
        {
            RollbackTo(mark);
            throw;
        }
    }

    // Writes a length-prefixed text parameter. Scalars format straight into the buffer with the
    // same invariant formatting PgTextCodec.FormatParameter uses; other kinds go through it.
    private void WriteTextParameter(SqlValue value)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Boolean:
                WriteParameterBytes(value.Get<bool>() ? "true"u8 : "false"u8);
                return;
            case SqlValueKind.Int16 when WriteFormatted(value.Get<short>(), 6):
                return;
            case SqlValueKind.Int32 when WriteFormatted(value.Get<int>(), 11):
                return;
            case SqlValueKind.Int64 when WriteFormatted(value.Get<long>(), 20):
                return;
            case SqlValueKind.Single when WriteFormatted(value.Get<float>(), 32):
                return;
            case SqlValueKind.Double when WriteFormatted(value.Get<double>(), 32):
                return;
            case SqlValueKind.Decimal when WriteFormatted(value.Get<decimal>(), 64):
                return;
            case SqlValueKind.Guid when WriteFormatted(value.Get<Guid>(), 36, "D"):
                return;
            case SqlValueKind.String:
                WriteParameterText(value.GetRequired<string>());
                return;
            default:
                WriteParameterText(PgTextCodec.FormatParameter(value));
                return;
        }
    }

    private bool WriteFormatted<T>(T value, int maximumLength, ReadOnlySpan<char> format = default)
        where T : IUtf8SpanFormattable
    {
        var destination = _writer.GetSpan(sizeof(int) + maximumLength);
        if (!value.TryFormat(destination[sizeof(int)..], out var written, format, CultureInfo.InvariantCulture))
        {
            return false;
        }

        BinaryPrimitives.WriteInt32BigEndian(destination, written);
        _writer.Advance(sizeof(int) + written);
        return true;
    }

    private void WriteParameterBytes(ReadOnlySpan<byte> value)
    {
        _writer.WriteInt32(value.Length);
        _writer.Write(value);
    }

    private void WriteParameterText(string text)
    {
        // Short strings encode in one pass into a worst-case reservation; long ones are measured
        // first so the reservation does not triple their size.
        var byteCount = text.Length <= 1024
          ? s_utf8.GetMaxByteCount(text.Length)
          : s_utf8.GetByteCount(text);
        var destination = _writer.GetSpan(checked(sizeof(int) + byteCount));
        var written = s_utf8.GetBytes(text, destination[sizeof(int)..]);
        BinaryPrimitives.WriteInt32BigEndian(destination, written);
        _writer.Advance(checked(sizeof(int) + written));
    }

    private void WriteBindDescribeExecute(
        string portalName,
        string statementName,
        PgParameters parameters,
        int fetchSize,
        bool describePortal = true,
        bool sync = true)
    {
        var mark = _writer.WrittenCount;
        try
        {
            var count = parameters.Count;
            Span<PgParameterFormat> formats = count <= 64
              ? stackalloc PgParameterFormat[count]
              : new PgParameterFormat[count];
            var bind = BeginMessage((byte)'B');
            _writer.WriteCString(portalName);
            _writer.WriteCString(statementName);
            _writer.WriteInt16(checked((short)count));
            for (var i = 0; i < count; i++)
            {
                var format = PgParameterEncoder.ResolveFormat(parameters[i], _typeRegistry);
                formats[i] = format;
                _writer.WriteInt16(format == PgParameterFormat.Binary ? (short)1 : (short)0);
            }

            _writer.WriteInt16(checked((short)count));
            for (var i = 0; i < count; i++)
            {
                var parameter = parameters[i];
                if (parameter.Value.IsNull)
                {
                    _writer.WriteInt32(-1);
                    continue;
                }

                WriteParameterBytes(PgParameterEncoder.Encode(parameter, formats[i], _typeRegistry));
            }

            _writer.WriteInt16(1);
            _writer.WriteInt16(1);
            EndMessage(bind);

            if (describePortal)
            {
                WriteDescribePortal(portalName);
            }

            WriteExecute(portalName, fetchSize);
            if (sync)
            {
                WriteTyped((byte)'S', ReadOnlySpan<byte>.Empty);
            }
        }
        catch
        {
            RollbackTo(mark);
            throw;
        }
    }

    private void WriteTypedParse(string sql, PgParameters parameters)
    {
        var parse = BeginMessage((byte)'P');
        _writer.WriteByte(0);
        _writer.WriteCString(sql);
        _writer.WriteInt16(checked((short)parameters.Count));
        for (var i = 0; i < parameters.Count; i++)
        {
            _writer.WriteInt32(unchecked((int)parameters[i].Type.Oid));
        }

        EndMessage(parse);
    }

    // Reserves the type byte and length of a message written directly into the buffer and
    // returns its offset; EndMessage backfills the length once the body is complete.
    private int BeginMessage(byte type)
    {
        var start = _writer.WrittenCount;
        var header = _writer.GetSpan(5);
        header[0] = type;
        _writer.Advance(5);
        return start;
    }

    private void EndMessage(int start)
    {
        var length = checked(_writer.WrittenCount - start - 1);
        // GetSpan may have moved the buffer since BeginMessage, so look it up again.
        var written = MemoryMarshal.AsMemory(_writer.WrittenMemory).Span;
        BinaryPrimitives.WriteInt32BigEndian(written.Slice(start + 1, sizeof(int)), length);
    }

    private void WriteDescribePortal(string portalName)
    {
        var describe = BeginMessage((byte)'D');
        _writer.WriteByte((byte)'P');
        _writer.WriteCString(portalName);
        EndMessage(describe);
    }

    private void WriteDescribeStatement(string statementName)
    {
        var describe = BeginMessage((byte)'D');
        _writer.WriteByte((byte)'S');
        _writer.WriteCString(statementName);
        EndMessage(describe);
    }

    private void WriteParseUnnamed(string sql)
    {
        PgBufferWriterExtensions.ThrowIfContainsNull(sql);
        var sqlLength = s_utf8.GetByteCount(sql);
        var payloadLength = checked(1 + sqlLength + 1 + sizeof(short));
        var totalLength = checked(5 + payloadLength);
        var messageLength = checked(payloadLength + sizeof(int));
        var message = _writer.GetSpan(totalLength);
        message[0] = (byte)'P';
        BinaryPrimitives.WriteInt32BigEndian(
          message[1..],
          messageLength);
        var position = 5;
        message[position++] = 0;
        position += s_utf8.GetBytes(sql, message[position..]);
        message[position++] = 0;
        BinaryPrimitives.WriteInt16BigEndian(message[position..], 0);
        _writer.Advance(totalLength);
    }

    private void WriteBindDescribeExecuteNoParameters(
        string portalName,
        string statementName,
        int fetchSize,
        bool describePortal = true)
    {
        var bind = BeginMessage((byte)'B');
        _writer.WriteCString(portalName);
        _writer.WriteCString(statementName);
        _writer.WriteInt16(0);
        _writer.WriteInt16(0);
        _writer.WriteInt16(1);
        _writer.WriteInt16(1);
        EndMessage(bind);

        if (describePortal)
        {
            WriteDescribePortal(portalName);
        }

        WriteExecute(portalName, fetchSize);
        WriteTyped((byte)'S', ReadOnlySpan<byte>.Empty);
    }

    private void WriteExecute(string portalName, int fetchSize)
    {
        var execute = BeginMessage((byte)'E');
        _writer.WriteCString(portalName);
        _writer.WriteInt32(fetchSize);
        EndMessage(execute);
    }
}

internal static class PgBufferWriterExtensions
{
    private static readonly Encoding s_utf8 = new UTF8Encoding(false, true);

    public static void WriteByte(this IBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    public static void WriteInt16(this IBufferWriter<byte> writer, short value)
    {
        var destination = writer.GetSpan(sizeof(short));
        BinaryPrimitives.WriteInt16BigEndian(destination, value);
        writer.Advance(sizeof(short));
    }

    public static void WriteInt32(this IBufferWriter<byte> writer, int value)
    {
        var destination = writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(destination, value);
        writer.Advance(sizeof(int));
    }

    public static void WriteCString(this IBufferWriter<byte> writer, string value)
    {
        ThrowIfContainsNull(value);
        writer.WriteUtf8(value);
        writer.WriteByte(0);
    }

    internal static void ThrowIfContainsNull(string value)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("PostgreSQL protocol strings cannot contain NUL characters.", nameof(value));
        }
    }

    public static void WriteUtf8(this IBufferWriter<byte> writer, string value)
    {
        var byteCount = s_utf8.GetByteCount(value);
        var destination = writer.GetSpan(byteCount);
        var written = s_utf8.GetBytes(value, destination);
        writer.Advance(written);
    }
}

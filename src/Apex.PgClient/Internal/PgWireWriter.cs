using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Apex.SqlClient;

namespace Apex.PgClient.Internal;

internal sealed class PgWireWriter
{
    private static readonly Encoding s_utf8 = new UTF8Encoding(false, true);
    private readonly PipeWriter _writer;
    private readonly PgTypeRegistry _typeRegistry;

    public PgWireWriter(PipeWriter writer, PgTypeRegistry typeRegistry)
    {
        _writer = writer;
        _typeRegistry = typeRegistry;
    }

    public ValueTask<FlushResult> WriteStartupAsync(
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

    public ValueTask<FlushResult> WritePasswordAsync(
        string password,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> payload = new();
        payload.WriteCString(password);
        return WriteTypedAsync((byte)'p', payload.WrittenMemory, cancellationToken);
    }

    public ValueTask<FlushResult> WriteSaslInitialAsync(
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

    public ValueTask<FlushResult> WriteSaslResponseAsync(
        string message,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> payload = new();
        payload.WriteUtf8(message);
        return WriteTypedAsync((byte)'p', payload.WrittenMemory, cancellationToken);
    }

    public async ValueTask<FlushResult> WriteQueryAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        var byteCount = s_utf8.GetByteCount(sql);
        var totalLength = 1 + sizeof(int) + byteCount + 1;
        var message = _writer.GetSpan(totalLength);
        message[0] = (byte)'Q';
        BinaryPrimitives.WriteInt32BigEndian(
          message[1..],
          sizeof(int) + byteCount + 1);
        var written = s_utf8.GetBytes(sql, message[5..]);
        message[5 + written] = 0;
        _writer.Advance(totalLength);
        return await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteExtendedQueryAsync(
        string sql,
        SqlParameters parameters,
        CancellationToken cancellationToken)
    {
        if (parameters.Count == 0)
        {
            WriteParseUnnamed(sql);
            WriteBindDescribeExecuteNoParameters(
              portalName: string.Empty,
              statementName: string.Empty,
              fetchSize: 0);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        ArrayBufferWriter<byte> parse = new();
        parse.WriteByte(0);
        parse.WriteCString(sql);
        parse.WriteInt16(0);
        WriteTyped((byte)'P', parse.WrittenSpan);

        WriteBindDescribeExecute(string.Empty, string.Empty, parameters, 0);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteExtendedQueryAsync(
        string sql,
        PgParameters parameters,
        CancellationToken cancellationToken)
    {
        WriteTypedParse(sql, parameters);
        WriteBindDescribeExecute(string.Empty, string.Empty, parameters, 0);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteBatchAsync(
        PgBatch batch,
        CancellationToken cancellationToken)
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

        WriteTyped((byte)'S', ReadOnlySpan<byte>.Empty);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
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
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WritePreparedQueryAsync(
        string name,
        SqlParameters parameters,
        CancellationToken cancellationToken,
        bool describePortal = true,
        bool flush = true)
    {
        WriteBindDescribeExecute(
          string.Empty,
          name,
          parameters,
          0,
          describePortal);
        if (flush)
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteCopyDataAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        WriteTyped((byte)'d', payload.Span);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteCopyDoneAsync(CancellationToken cancellationToken)
    {
        WriteTyped((byte)'c', ReadOnlySpan<byte>.Empty);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteCopyFailAsync(
        string message,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> payload = new();
        payload.WriteCString(message);
        WriteTyped((byte)'f', payload.WrittenSpan);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteOpenPortalAsync(
        string portalName,
        string statementName,
        SqlParameters parameters,
        int fetchSize,
        CancellationToken cancellationToken)
    {
        WriteBindDescribeExecute(portalName, statementName, parameters, fetchSize);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteExecutePortalAsync(
        string portalName,
        int fetchSize,
        CancellationToken cancellationToken)
    {
        WriteExecute(portalName, fetchSize);
        WriteTyped((byte)'S', ReadOnlySpan<byte>.Empty);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
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
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
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
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<FlushResult> WriteTerminateAsync(CancellationToken cancellationToken) =>
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

    private async ValueTask<FlushResult> WriteUntypedAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var length = _writer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(length, checked(payload.Length + sizeof(int)));
        _writer.Advance(sizeof(int));
        _writer.Write(payload.Span);
        return await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<FlushResult> WriteTypedAsync(
        byte type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        WriteTyped(type, payload.Span);
        return await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
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
        ArrayBufferWriter<byte> bind = new();
        bind.WriteCString(portalName);
        bind.WriteCString(statementName);
        bind.WriteInt16(0);
        bind.WriteInt16(checked((short)parameters.Count));
        for (var i = 0; i < parameters.Count; i++)
        {
            var value = parameters[i];
            if (value.IsNull)
            {
                bind.WriteInt32(-1);
                continue;
            }

            var text = PgTextCodec.FormatParameter(value);
            var byteCount = s_utf8.GetByteCount(text);
            bind.WriteInt32(byteCount);
            bind.WriteUtf8(text);
        }

        bind.WriteInt16(1);
        bind.WriteInt16(1);
        WriteTyped((byte)'B', bind.WrittenSpan);

        if (describePortal)
        {
            ArrayBufferWriter<byte> describe = new();
            describe.WriteByte((byte)'P');
            describe.WriteCString(portalName);
            WriteTyped((byte)'D', describe.WrittenSpan);
        }
        WriteExecute(portalName, fetchSize);
        WriteTyped((byte)'S', ReadOnlySpan<byte>.Empty);
    }

    private void WriteBindDescribeExecute(
        string portalName,
        string statementName,
        PgParameters parameters,
        int fetchSize,
        bool describePortal = true,
        bool sync = true)
    {
        ArrayBufferWriter<byte> bind = new();
        bind.WriteCString(portalName);
        bind.WriteCString(statementName);
        bind.WriteInt16(checked((short)parameters.Count));
        var payloads = new byte[]?[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            var format = PgParameterEncoder.ResolveFormat(parameter, _typeRegistry);
            bind.WriteInt16(format == PgParameterFormat.Binary ? (short)1 : (short)0);
            if (!parameter.Value.IsNull)
            {
                payloads[i] = PgParameterEncoder.Encode(parameter, format, _typeRegistry);
            }
        }

        bind.WriteInt16(checked((short)parameters.Count));
        for (var i = 0; i < payloads.Length; i++)
        {
            var payload = payloads[i];
            if (payload is null)
            {
                bind.WriteInt32(-1);
                continue;
            }

            bind.WriteInt32(payload.Length);
            bind.Write(payload);
        }

        bind.WriteInt16(1);
        bind.WriteInt16(1);
        WriteTyped((byte)'B', bind.WrittenSpan);

        if (describePortal)
        {
            ArrayBufferWriter<byte> describe = new();
            describe.WriteByte((byte)'P');
            describe.WriteCString(portalName);
            WriteTyped((byte)'D', describe.WrittenSpan);
        }

        WriteExecute(portalName, fetchSize);
        if (sync)
        {
            WriteTyped((byte)'S', ReadOnlySpan<byte>.Empty);
        }
    }

    private void WriteTypedParse(string sql, PgParameters parameters)
    {
        ArrayBufferWriter<byte> parse = new();
        parse.WriteByte(0);
        parse.WriteCString(sql);
        parse.WriteInt16(checked((short)parameters.Count));
        for (var i = 0; i < parameters.Count; i++)
        {
            parse.WriteInt32(unchecked((int)parameters[i].Type.Oid));
        }

        WriteTyped((byte)'P', parse.WrittenSpan);
    }

    private void WriteDescribeStatement(string statementName)
    {
        var byteCount = s_utf8.GetByteCount(statementName);
        Span<byte> describe = stackalloc byte[1 + byteCount + 1];
        describe[0] = (byte)'S';
        int written = s_utf8.GetBytes(statementName, describe[1..]);
        describe[1 + written] = 0;
        WriteTyped((byte)'D', describe);
    }

    private void WriteParseUnnamed(string sql)
    {
        var sqlLength = s_utf8.GetByteCount(sql);
        var payloadLength = 1 + sqlLength + 1 + sizeof(short);
        var message = _writer.GetSpan(5 + payloadLength);
        message[0] = (byte)'P';
        BinaryPrimitives.WriteInt32BigEndian(
          message[1..],
          payloadLength + sizeof(int));
        var position = 5;
        message[position++] = 0;
        position += s_utf8.GetBytes(sql, message[position..]);
        message[position++] = 0;
        BinaryPrimitives.WriteInt16BigEndian(message[position..], 0);
        _writer.Advance(5 + payloadLength);
    }

    private void WriteBindDescribeExecuteNoParameters(
        string portalName,
        string statementName,
        int fetchSize)
    {
        var portalLength = s_utf8.GetByteCount(portalName);
        var statementLength = s_utf8.GetByteCount(statementName);
        var bindLength =
          portalLength + 1 +
          statementLength + 1 +
          sizeof(short) +
          sizeof(short) +
          sizeof(short) +
          sizeof(short);
        Span<byte> bind = stackalloc byte[bindLength];
        var position = 0;
        position += s_utf8.GetBytes(portalName, bind[position..]);
        bind[position++] = 0;
        position += s_utf8.GetBytes(statementName, bind[position..]);
        bind[position++] = 0;
        BinaryPrimitives.WriteInt16BigEndian(bind[position..], 0);
        position += sizeof(short);
        BinaryPrimitives.WriteInt16BigEndian(bind[position..], 0);
        position += sizeof(short);
        BinaryPrimitives.WriteInt16BigEndian(bind[position..], 1);
        position += sizeof(short);
        BinaryPrimitives.WriteInt16BigEndian(bind[position..], 1);
        WriteTyped((byte)'B', bind);

        Span<byte> describe = stackalloc byte[1 + portalLength + 1];
        describe[0] = (byte)'P';
        position = 1;
        position += s_utf8.GetBytes(portalName, describe[position..]);
        describe[position] = 0;
        WriteTyped((byte)'D', describe);

        Span<byte> execute = stackalloc byte[portalLength + 1 + sizeof(int)];
        position = s_utf8.GetBytes(portalName, execute);
        execute[position++] = 0;
        BinaryPrimitives.WriteInt32BigEndian(execute[position..], fetchSize);
        WriteTyped((byte)'E', execute);
        WriteTyped((byte)'S', ReadOnlySpan<byte>.Empty);
    }

    private void WriteExecute(string portalName, int fetchSize)
    {
        ArrayBufferWriter<byte> execute = new();
        execute.WriteCString(portalName);
        execute.WriteInt32(fetchSize);
        WriteTyped((byte)'E', execute.WrittenSpan);
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
        writer.WriteUtf8(value);
        writer.WriteByte(0);
    }

    public static void WriteUtf8(this IBufferWriter<byte> writer, string value)
    {
        var byteCount = s_utf8.GetByteCount(value);
        var destination = writer.GetSpan(byteCount);
        var written = s_utf8.GetBytes(value, destination);
        writer.Advance(written);
    }
}

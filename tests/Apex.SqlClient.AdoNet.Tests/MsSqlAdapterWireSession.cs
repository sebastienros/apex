using System.Buffers;
using Apex.MsSqlClient.Internal;

namespace Apex.SqlClient.AdoNet.Tests;

internal sealed class MsSqlAdapterWireSession(Stream stream, CancellationToken cancellationToken)
    : AdapterWireSession(stream, cancellationToken)
{
    internal override async Task LoginAsync(bool fail)
    {
        var reader = new TdsPacketReader(Stream);
        Assert.AreEqual(TdsMessageType.PreLogin, (await reader.ReadMessageAsync(CancellationToken)).Type);
        using var writer = new TdsPacketWriter(Stream, 4096);
        await writer.WriteMessageAsync(TdsMessageType.TabularResult,
            TdsPreLogin.Encode(TdsEncryptionLevel.NotSupported), CancellationToken);
        Assert.AreEqual(TdsMessageType.Login7, (await reader.ReadMessageAsync(CancellationToken)).Type);
        if (fail)
        {
            await WriteErrorAsync();
            return;
        }

        ArrayBufferWriter<byte> body = new();
        body.WriteByte(1);
        body.Write("\x04\x00\x00\x74"u8);
        body.WriteBVarChar("SQL Server");
        body.WriteByte(16);
        body.WriteByte(0);
        body.WriteUInt16BigEndian(1000);
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.LoginAck);
        response.WriteUInt16LittleEndian((ushort)body.WrittenCount);
        response.Write(body.WrittenSpan);
        WriteDone(response, 0);
        await WriteAsync(response);
    }

    internal override async Task ExpectQueryAsync()
    {
        var request = await new TdsPacketReader(Stream).ReadMessageAsync(CancellationToken);
        Assert.AreEqual(TdsMessageType.SqlBatch, request.Type);
    }

    internal override Task WriteResultsAsync(WireResult[] results)
    {
        ArrayBufferWriter<byte> response = new();
        for (var index = 0; index < results.Length; index++)
        {
            response.WriteByte(TdsTokenType.ColumnMetadata);
            response.WriteUInt16LittleEndian(1);
            response.WriteUInt32LittleEndian(0);
            response.WriteUInt16LittleEndian(0);
            response.WriteByte(TdsDataType.Int4);
            response.WriteBVarChar(results[index].Name);
            foreach (var value in results[index].Rows)
            {
                response.WriteByte(TdsTokenType.Row);
                response.WriteInt32LittleEndian(value);
            }
            WriteDone(response, index + 1 < results.Length ? TdsDoneStatus.More : 0);
        }
        return WriteAsync(response);
    }

    internal override Task WriteErrorAsync()
    {
        ArrayBufferWriter<byte> body = new();
        body.WriteInt32LittleEndian(2627);
        body.WriteByte(1);
        body.WriteByte(14);
        body.WriteUInt16LittleEndian(10);
        body.WriteUtf16("wire error");
        body.WriteBVarChar("server");
        body.WriteBVarChar("procedure");
        body.WriteInt32LittleEndian(42);
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.Error);
        response.WriteUInt16LittleEndian((ushort)body.WrittenCount);
        response.Write(body.WrittenSpan);
        WriteDone(response, TdsDoneStatus.Error);
        return WriteAsync(response);
    }

    internal override async Task ExpectCloseAsync()
    {
        var last = new byte[1];
        Assert.AreEqual(0, await Stream.ReadAsync(last, CancellationToken));
    }

    private async Task WriteAsync(ArrayBufferWriter<byte> response)
    {
        using var writer = new TdsPacketWriter(Stream, 4096);
        await writer.WriteMessageAsync(TdsMessageType.TabularResult, response.WrittenMemory, CancellationToken);
    }

    private static void WriteDone(ArrayBufferWriter<byte> response, TdsDoneStatus status)
    {
        response.WriteByte(TdsTokenType.Done);
        response.WriteUInt16LittleEndian((ushort)status);
        response.WriteUInt16LittleEndian(0);
        response.WriteInt64LittleEndian(0);
    }
}

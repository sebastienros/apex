using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Apex.MsSqlClient;
using Apex.MsSqlClient.Internal;

namespace Apex.SqlClient.AdoNet.Tests;

[TestClass]
public sealed class MsSqlAdoNamedParametersTests
{
    [TestMethod]
    public async Task MsSqlDbCommandAndBatchPreserveParameterNamesOnWire()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        List<byte[]> rpcPayloads = [];

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            var wire = new MsSqlAdapterWireSession(stream, timeout.Token);
            await wire.LoginAsync(fail: false);

            var reader = new TdsPacketReader(stream);

            // 1. Unprepared command with named parameters (@id and bare name)
            var req1 = await reader.ReadMessageAsync(timeout.Token);
            Assert.AreEqual(TdsMessageType.Rpc, req1.Type);
            rpcPayloads.Add(req1.Payload.ToArray());
            await wire.WriteResultsAsync([new("v", [1])]);

            // 2. Prepared command first exec (sp_prepexec)
            var req2 = await reader.ReadMessageAsync(timeout.Token);
            Assert.AreEqual(TdsMessageType.Rpc, req2.Type);
            rpcPayloads.Add(req2.Payload.ToArray());
            await WritePreparedHandleResultAsync(stream, handle: 42, value: 2, timeout.Token);

            // 3. Prepared command second exec (sp_execute)
            var req3 = await reader.ReadMessageAsync(timeout.Token);
            Assert.AreEqual(TdsMessageType.Rpc, req3.Type);
            rpcPayloads.Add(req3.Payload.ToArray());
            await wire.WriteResultsAsync([new("v", [3])]);

            // 4. Prepared unprepare on dispose
            var req4 = await reader.ReadMessageAsync(timeout.Token);
            Assert.AreEqual(TdsMessageType.Rpc, req4.Type);
            await WriteDoneOnlyAsync(stream, timeout.Token);

            // 5. Batch command with named parameters
            var req5 = await reader.ReadMessageAsync(timeout.Token);
            Assert.AreEqual(TdsMessageType.Rpc, req5.Type);
            rpcPayloads.Add(req5.Payload.ToArray());
            await wire.WriteResultsAsync([new("v", [4])]);

            await wire.ExpectCloseAsync();
        });

        await using (var connection = new MsSqlDbConnection(
            $"Server=127.0.0.1,{port};User ID=sa;Database=test;Encrypt=Disable"))
        {
            await connection.OpenAsync(timeout.Token);

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT @id, @name";
                cmd.Parameters.Add(new MsSqlDbParameter { ParameterName = "@id", Value = 10 });
                cmd.Parameters.Add(new MsSqlDbParameter { ParameterName = "name", Value = "Alice" });
                Assert.AreEqual(1, await cmd.ExecuteScalarAsync(timeout.Token));
            }

            await using (var prepared = connection.CreateCommand())
            {
                prepared.CommandText = "SELECT @userId";
                var p = new MsSqlDbParameter { ParameterName = "userId", Value = 100 };
                prepared.Parameters.Add(p);
                await prepared.PrepareAsync(timeout.Token);
                Assert.AreEqual(2, await prepared.ExecuteScalarAsync(timeout.Token));
                p.Value = 200;
                Assert.AreEqual(3, await prepared.ExecuteScalarAsync(timeout.Token));
            }

            await using (var batch = connection.CreateBatch())
            {
                var bc = batch.CreateBatchCommand();
                bc.CommandText = "SELECT @tag";
                bc.Parameters.Add(new MsSqlDbParameter { ParameterName = "@tag", Value = "batch" });
                batch.BatchCommands.Add(bc);
                Assert.AreEqual(4, await batch.ExecuteScalarAsync(timeout.Token));
            }
        }

        await server;

        Assert.AreEqual(4, rpcPayloads.Count);
        Assert.IsTrue(ContainsUtf16(rpcPayloads[0], "@id int,@name nvarchar(4000)"));
        Assert.IsTrue(ContainsUtf16(rpcPayloads[0], "@id"));
        Assert.IsTrue(ContainsUtf16(rpcPayloads[0], "@name"));

        Assert.IsTrue(ContainsUtf16(rpcPayloads[1], "@userId int"));
        Assert.IsTrue(ContainsUtf16(rpcPayloads[2], "@userId"));

        Assert.IsTrue(ContainsUtf16(rpcPayloads[3], "@tag nvarchar(4000)"));
    }

    [TestMethod]
    public async Task MsSqlDbCommandRejectsInvalidParameterName()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            var wire = new MsSqlAdapterWireSession(stream, timeout.Token);
            await wire.LoginAsync(fail: false);
            await wire.ExpectCloseAsync();
        });

        await using (var connection = new MsSqlDbConnection(
            $"Server=127.0.0.1,{port};User ID=sa;Database=test;Encrypt=Disable"))
        {
            await connection.OpenAsync(timeout.Token);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT @bad";
            cmd.Parameters.Add(new MsSqlDbParameter { ParameterName = "@a,b", Value = 1 });
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => cmd.ExecuteScalarAsync(timeout.Token));
        }

        await server;
    }

    private static bool ContainsUtf16(byte[] payload, string expected) =>
        payload.AsSpan().IndexOf(Encoding.Unicode.GetBytes(expected)) >= 0;

    private static async Task WritePreparedHandleResultAsync(
        Stream stream,
        int handle,
        int value,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(1);
        response.WriteUInt32LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteByte(TdsDataType.Int4);
        response.WriteBVarChar("v");
        response.WriteByte(TdsTokenType.Row);
        response.WriteInt32LittleEndian(value);
        WriteDone(response, TdsDoneStatus.More);

        response.WriteByte(TdsTokenType.ReturnValue);
        response.WriteUInt16LittleEndian(0);
        response.WriteBVarChar(string.Empty);
        response.WriteByte(1); // output
        response.WriteUInt32LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
        response.WriteByte(TdsDataType.IntN);
        response.WriteByte(4);
        response.WriteByte(4);
        response.WriteInt32LittleEndian(handle);
        WriteDone(response, 0);

        using var writer = new TdsPacketWriter(stream, 4096);
        await writer.WriteMessageAsync(TdsMessageType.TabularResult, response.WrittenMemory, cancellationToken);
    }

    private static async Task WriteDoneOnlyAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> response = new();
        WriteDone(response, 0);
        using var writer = new TdsPacketWriter(stream, 4096);
        await writer.WriteMessageAsync(TdsMessageType.TabularResult, response.WrittenMemory, cancellationToken);
    }

    private static void WriteDone(ArrayBufferWriter<byte> response, TdsDoneStatus status)
    {
        response.WriteByte(TdsTokenType.Done);
        response.WriteUInt16LittleEndian((ushort)status);
        response.WriteUInt16LittleEndian(0);
        response.WriteInt64LittleEndian(0);
    }
}

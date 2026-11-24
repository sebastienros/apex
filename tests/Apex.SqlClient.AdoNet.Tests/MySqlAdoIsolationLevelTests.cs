using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Apex.MySqlClient;
using Apex.MySqlClient.Internal;

namespace Apex.SqlClient.AdoNet.Tests;

[TestClass]
public sealed class MySqlAdoIsolationLevelTests
{
    [TestMethod]
    public async Task BeginTransactionAsyncWithReadCommittedSetsMySqlTransactionIsolationLevel()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var executedSql = new List<string>();

        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var session = new MySqlAdapterWireSession(stream, CancellationToken.None);
            await session.LoginAsync(fail: false);

            while (true)
            {
                var header = new byte[4];
                var read = await stream.ReadAsync(header);
                if (read == 0) break;
                var total = read;
                while (total < 4)
                {
                    var n = await stream.ReadAsync(header.AsMemory(total));
                    if (n == 0) return;
                    total += n;
                }

                var len = header[0] | (header[1] << 8) | (header[2] << 16);
                var seq = (byte)(header[3] + 1);
                var payload = new byte[len];
                if (len > 0) await stream.ReadExactlyAsync(payload);

                if (payload[0] == (byte)MySqlCommand.Quit) break;
                if (payload[0] == (byte)MySqlCommand.Query)
                {
                    var sql = Encoding.UTF8.GetString(payload, 1, payload.Length - 1);
                    executedSql.Add(sql);
                    var status = sql.StartsWith("START", StringComparison.Ordinal)
                        ? (ushort)(MySqlServerStatus.AutoCommit | MySqlServerStatus.InTransaction)
                        : (ushort)MySqlServerStatus.AutoCommit;
                    byte[] ok = [0x00, 0x00, 0x00, (byte)(status & 0xFF), (byte)(status >> 8), 0x00, 0x00];
                    var frame = new byte[4 + ok.Length];
                    MySqlPacketReader.WriteHeader(frame, ok.Length, seq);
                    ok.CopyTo(frame, 4);
                    await stream.WriteAsync(frame);
                    await stream.FlushAsync();
                }
            }
        });

        await using var connection = new MySqlDbConnection(
            $"Server=127.0.0.1;Port={port};Database=app;User ID=root;SslMode=None");
        await connection.OpenAsync();
        await using (var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            Assert.AreEqual(IsolationLevel.ReadCommitted, tx.IsolationLevel);
            await tx.CommitAsync();
        }

        await connection.CloseAsync();
        await serverTask;

        CollectionAssert.AreEqual(
            new[]
            {
                "SET TRANSACTION ISOLATION LEVEL READ COMMITTED",
                "START TRANSACTION",
                "COMMIT",
            },
            executedSql);
    }
}

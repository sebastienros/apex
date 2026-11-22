using Apex.MySqlClient;
using Apex.MySqlClient.Internal;

namespace Apex.SqlClient.AdoNet.Tests;

internal sealed class MySqlAdapterWireSession(Stream stream, CancellationToken cancellationToken)
    : AdapterWireSession(stream, cancellationToken)
{
    private byte _sequence;

    internal override async Task LoginAsync(bool fail)
    {
        await WriteAsync(Handshake());
        _ = await ReadAsync();
        if (fail) await WriteErrorAsync();
        else await WriteAsync([0, 0, 0, (byte)MySqlServerStatus.AutoCommit, 0, 0, 0]);
    }

    internal override async Task ExpectQueryAsync()
    {
        var payload = await ReadAsync();
        Assert.AreEqual((byte)MySqlCommand.Query, payload[0]);
    }

    internal override async Task WriteResultsAsync(WireResult[] results)
    {
        for (var index = 0; index < results.Length; index++)
        {
            await WriteAsync([1]);
            await WriteAsync(Column(results[index].Name));
            foreach (var value in results[index].Rows)
            {
                var text = System.Text.Encoding.UTF8.GetBytes(
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await WriteAsync([(byte)text.Length, .. text]);
            }
            var status = MySqlServerStatus.AutoCommit;
            if (index + 1 < results.Length) status |= MySqlServerStatus.MoreResultsExist;
            await WriteAsync([MySqlProtocol.EofHeader, 0, 0, (byte)status, 0, 0, 0]);
        }
    }

    internal override Task WriteErrorAsync() =>
        WriteAsync([0xff, 0x26, 0x04, (byte)'#', .. "23000wire error"u8]);

    internal override async Task ExpectCloseAsync()
    {
        var payload = await ReadAsync();
        Assert.AreEqual((byte)MySqlCommand.Quit, payload[0]);
    }

    private static byte[] Handshake()
    {
        const MySqlCapabilities capabilities =
            MySqlCapabilities.LongPassword | MySqlCapabilities.LongFlag | MySqlCapabilities.Protocol41 |
            MySqlCapabilities.Transactions | MySqlCapabilities.SecureConnection |
            MySqlCapabilities.PluginAuth | MySqlCapabilities.PluginAuthLengthEncodedClientData |
            MySqlCapabilities.MultiResults | MySqlCapabilities.PreparedStatementMultiResults |
            MySqlCapabilities.DeprecateEof | MySqlCapabilities.ConnectWithDatabase | MySqlCapabilities.FoundRows;
        MySqlPayloadWriter writer = new();
        try
        {
            writer.WriteByte(10);
            writer.WriteNullTerminatedString("8.4.2");
            writer.WriteUInt32(42);
            writer.WriteBytes("12345678"u8);
            writer.WriteByte(0);
            writer.WriteUInt16((ushort)((uint)capabilities & 0xffff));
            writer.WriteByte(MySqlProtocol.Utf8Mb4Collation);
            writer.WriteUInt16((ushort)MySqlServerStatus.AutoCommit);
            writer.WriteUInt16((ushort)((uint)capabilities >> 16));
            writer.WriteByte(21);
            writer.WriteZero(10);
            writer.WriteBytes("901234567890"u8);
            writer.WriteByte(0);
            writer.WriteNullTerminatedString(MySqlProtocol.NativePasswordPlugin);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Release();
        }
    }

    private static byte[] Column(string name)
    {
        MySqlPayloadWriter writer = new();
        try
        {
            writer.WriteLengthEncodedString("def");
            writer.WriteLengthEncodedString(string.Empty);
            writer.WriteLengthEncodedString(string.Empty);
            writer.WriteLengthEncodedString(string.Empty);
            writer.WriteLengthEncodedString(name);
            writer.WriteLengthEncodedString(name);
            writer.WriteLengthEncodedInteger(12);
            writer.WriteUInt16(MySqlProtocol.Utf8Mb4Collation);
            writer.WriteUInt32(11);
            writer.WriteByte((byte)MySqlType.Long);
            writer.WriteUInt16(0);
            writer.WriteByte(0);
            writer.WriteUInt16(0);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Release();
        }
    }

    private async Task WriteAsync(byte[] payload)
    {
        var frame = new byte[payload.Length + MySqlProtocol.PacketHeaderLength];
        MySqlPacketReader.WriteHeader(frame, payload.Length, _sequence++);
        payload.CopyTo(frame, MySqlProtocol.PacketHeaderLength);
        await Stream.WriteAsync(frame, CancellationToken);
    }

    private async Task<byte[]> ReadAsync()
    {
        var header = new byte[MySqlProtocol.PacketHeaderLength];
        await Stream.ReadExactlyAsync(header, CancellationToken);
        _sequence = (byte)(header[3] + 1);
        var payload = new byte[header[0] | header[1] << 8 | header[2] << 16];
        await Stream.ReadExactlyAsync(payload, CancellationToken);
        return payload;
    }
}

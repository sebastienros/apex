using System.Buffers.Binary;
using System.Text;

namespace Apex.SqlClient.AdoNet.Tests;

internal sealed class PgAdapterWireSession(Stream stream, CancellationToken cancellationToken)
    : AdapterWireSession(stream, cancellationToken)
{
    internal override async Task LoginAsync(bool fail)
    {
        var header = new byte[4];
        await Stream.ReadExactlyAsync(header, CancellationToken);
        var startup = new byte[BinaryPrimitives.ReadInt32BigEndian(header) - 4];
        await Stream.ReadExactlyAsync(startup, CancellationToken);
        Assert.AreEqual(196608, BinaryPrimitives.ReadInt32BigEndian(startup));
        if (fail)
        {
            await WriteErrorMessageAsync();
            return;
        }
        await WriteAsync('R', Int32(0));
        await WriteAsync('S', [.. CString("server_version"), .. CString("16.4")]);
        await WriteAsync('K', [.. Int32(123), .. Int32(456)]);
        await WriteAsync('Z', [(byte)'I']);
    }

    internal override async Task ExpectQueryAsync()
    {
        foreach (var expected in "PBDES")
        {
            Assert.AreEqual((byte)expected, await ReadAsync());
        }
    }

    internal override async Task WriteResultsAsync(WireResult[] results)
    {
        Assert.HasCount(1, results);
        await WriteAsync('1', []);
        await WriteAsync('2', []);
        var result = results[0];
        await WriteAsync('T',
            [.. Int16(1), .. CString(result.Name), .. Int32(0), .. Int16(0),
                .. Int32(23), .. Int16(4), .. Int32(-1), .. Int16(0)]);
        foreach (var value in result.Rows)
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await WriteAsync('D', [.. Int16(1), .. Int32(bytes.Length), .. bytes]);
        }
        await WriteAsync('C', CString($"SELECT {result.Rows.Length}"));
        await WriteAsync('Z', [(byte)'I']);
    }

    internal override async Task WriteErrorAsync()
    {
        await WriteErrorMessageAsync();
        await WriteAsync('Z', [(byte)'I']);
    }

    private Task WriteErrorMessageAsync() =>
        WriteAsync('E', [(byte)'S', .. CString("ERROR"), (byte)'C', .. CString("40001"),
            (byte)'M', .. CString("wire error"), 0]);

    internal override async Task ExpectCloseAsync() => Assert.AreEqual((byte)'X', await ReadAsync());

    private async Task<byte> ReadAsync()
    {
        var header = new byte[5];
        await Stream.ReadExactlyAsync(header, CancellationToken);
        var payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4];
        await Stream.ReadExactlyAsync(payload, CancellationToken);
        return header[0];
    }

    private async Task WriteAsync(char type, byte[] payload)
    {
        var frame = new byte[payload.Length + 5];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1), payload.Length + 4);
        payload.CopyTo(frame, 5);
        await Stream.WriteAsync(frame, CancellationToken);
    }

    private static byte[] CString(string value) => [.. Encoding.UTF8.GetBytes(value), 0];
    private static byte[] Int16(short value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        return bytes;
    }
    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}

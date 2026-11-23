using System.Buffers;
using System.Text;
using Apex.MsSqlClient.Internal;
using Apex.SqlClient;
using BenchmarkDotNet.Attributes;

namespace Apex.DriverBenchmarks;

/// <summary>
/// Parses pre-framed SQL Server responses from memory, so client-side TDS decoding is measured
/// without the server round trip. The responses mirror <see cref="MsSqlBenchmarks"/>'s wide rows:
/// int, nvarchar(32), float, datetime2(7) and a null int, in 4096-byte packets.
/// </summary>
[MemoryDiagnoser]
public class MsSqlParseBenchmarks
{
    private const int PacketSize = 4096;
    private const int SmallResultRepeats = 100;
    private readonly MsSqlRowDecoder _decoder = new();
    private readonly TdsResultShapeCache _shapes = new();
    private TdsQueryParser _parser = null!;
    private TdsPacketReader _wideBuffered = null!;
    private TdsPacketReader _wideStreaming = null!;
    private TdsPacketReader _small = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _parser = new TdsQueryParser(_decoder, _shapes);
        // Each reader lives for the whole run, as a connection's does, so buffers it retains
        // between messages are reused rather than regrown on every call.
        var wide = await FrameAsync(BuildResponse(10_000));
        _wideBuffered = TdsPacketReader.CreateBuffered(new LoopingStream(wide));
        _wideStreaming = TdsPacketReader.CreateBuffered(new LoopingStream(wide));
        _small = TdsPacketReader.CreateBuffered(
          new LoopingStream(await FrameAsync(BuildResponse(1))));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _wideBuffered.Release();
        _wideStreaming.Release();
        _small.Release();
    }

    /// <summary>The buffered <c>QueryAsync</c> path over a 10,000-row response.</summary>
    [Benchmark]
    public async Task<int> Buffered10kRows()
    {
        var rows = await ParseBufferedAsync(_wideBuffered);
        return rows.Count;
    }

    /// <summary>The streaming reader's token loop over a 10,000-row response.</summary>
    [Benchmark]
    public async Task<long> Streaming10kRows()
    {
        return await ParseStreamingAsync(_wideStreaming);
    }

    /// <summary>
    /// The buffered path over 100 back-to-back one-row responses of the same shape, as repeated
    /// executions of one statement produce. Per-result metadata costs dominate here.
    /// </summary>
    [Benchmark(OperationsPerInvoke = SmallResultRepeats)]
    public async Task<int> BufferedRepeatedSmallResult()
    {
        var total = 0;
        for (var i = 0; i < SmallResultRepeats; i++)
        {
            total += (await ParseBufferedAsync(_small)).Count;
        }

        return total;
    }

    // What MsSqlConnection.ReceiveQueryAsync does, without the attention handling.
    private async ValueTask<SqlRowSet> ParseBufferedAsync(TdsPacketReader reader)
    {
        _parser.Reset();
        TdsStreamingTokenReader tokens = new(reader, default);
        while (true)
        {
            _ = await reader.BeginMessageAsync(default);
            var response = await _parser.ParseMessageAsync(tokens);
            if (response.IsFinal)
            {
                return response.Rows;
            }
        }
    }

    private async ValueTask<long> ParseStreamingAsync(TdsPacketReader packets)
    {
        _ = await packets.BeginMessageAsync(default);
        TdsStreamingTokenReader tokens = new(packets, default);
        TdsRowBuffer row = new();
        IReadOnlyList<TdsColumn> columns = [];
        long count = 0;
        while (tokens.HasRemaining)
        {
            var token = await tokens.ReadTokenTypeAsync();
            switch (token)
            {
                case TdsTokenType.ColumnMetadata:
                    columns = (await tokens.ReadColumnMetadataAsync(_shapes)).Columns;
                    break;
                case TdsTokenType.Row:
                case TdsTokenType.NbcRow:
                    await tokens.ReadRowAsync(columns, token == TdsTokenType.NbcRow, row);
                    count += row.WrittenCount;
                    break;
                case TdsTokenType.Done:
                    _ = await tokens.ReadDoneAsync();
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected token 0x{token:X2}.");
            }
        }

        return count;
    }

    private static byte[] BuildResponse(int rows)
    {
        ArrayBufferWriter<byte> response = new();
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(5);
        WriteColumn(response, "id", static writer =>
        {
            writer.WriteByte(TdsDataType.IntN);
            writer.WriteByte(4);
        });
        WriteColumn(response, "name", static writer =>
        {
            writer.WriteByte(TdsDataType.NVarChar);
            writer.WriteUInt16LittleEndian(64);
            writer.WriteUInt32LittleEndian(0x00D00409);
            writer.WriteByte(0);
        });
        WriteColumn(response, "amount", static writer =>
        {
            writer.WriteByte(TdsDataType.FloatN);
            writer.WriteByte(8);
        });
        WriteColumn(response, "created", static writer =>
        {
            writer.WriteByte(TdsDataType.DateTime2);
            writer.WriteByte(7);
        });
        WriteColumn(response, "missing", static writer =>
        {
            writer.WriteByte(TdsDataType.IntN);
            writer.WriteByte(4);
        });

        for (var i = 1; i <= rows; i++)
        {
            response.WriteByte(TdsTokenType.Row);
            response.WriteByte(4);
            response.WriteInt32LittleEndian(i);
            var name = Encoding.Unicode.GetBytes("name-" + i);
            response.WriteUInt16LittleEndian((ushort)name.Length);
            response.Write(name);
            response.WriteByte(8);
            response.WriteInt64LittleEndian(BitConverter.DoubleToInt64Bits(i / 3.0));
            response.WriteByte(8);
            response.Write(new byte[] { 0, 0, 0, 0, 0, 0x3B, 0x4A, 0x0B });
            response.WriteByte(0);
        }

        response.WriteByte(TdsTokenType.Done);
        response.WriteUInt16LittleEndian((ushort)TdsDoneStatus.Count);
        response.WriteUInt16LittleEndian(0xC1);
        response.WriteInt64LittleEndian(rows);
        return response.WrittenSpan.ToArray();
    }

    private static void WriteColumn(
        ArrayBufferWriter<byte> writer,
        string name,
        Action<ArrayBufferWriter<byte>> typeInfo)
    {
        writer.WriteUInt32LittleEndian(0);
        writer.WriteUInt16LittleEndian(0x09);
        typeInfo(writer);
        writer.WriteBVarChar(name);
    }

    private static async Task<byte[]> FrameAsync(byte[] payload)
    {
        using MemoryStream framed = new();
        await TdsPacketWriter.WriteMessageCoreAsync(
          framed,
          TdsMessageType.TabularResult,
          payload,
          PacketSize,
          default);
        return framed.ToArray();
    }

    /// <summary>Serves the same bytes forever, one response after another.</summary>
    private sealed class LoopingStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
          Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position = (_position + count) % data.Length;
            return count;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
          new(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
          throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
          throw new NotSupportedException();
    }
}

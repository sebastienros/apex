using System.Buffers;
using Apex.MsSqlClient.Internal;

namespace Apex.MsSqlClient.Tests;

[TestClass]
public sealed class TdsBufferedReaderTests
{
    private const int UnicodeCollation = 0x00D00409;

    public static IEnumerable<object[]> FramingMatrix()
    {
        foreach (var packetSize in new[] { 9, 10, 11, 16, 512, 4096 })
        {
            foreach (var readChunk in new[] { 1, 7, int.MaxValue })
            {
                foreach (var receiveBuffer in new[] { 512, TdsPacketReader.DefaultReceiveBufferSize })
                {
                    yield return [packetSize, readChunk, receiveBuffer];
                }
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(FramingMatrix))]
    public async Task StreamingReaderMatchesWholeMessageParserAtEveryBoundary(
        int packetSize,
        int readChunk,
        int receiveBuffer)
    {
        var payload = BuildResponse();
        var expected = ParseWholeMessage(payload);
        using ChunkedStream stream = new(await FrameAsync(packetSize, payload), readChunk);
        var reader = TdsPacketReader.CreateBuffered(stream, receiveBuffer);

        var actual = await ParseStreamingAsync(reader);

        CollectionAssert.AreEqual(expected, actual);
        reader.Release();
    }

    [TestMethod]
    [DynamicData(nameof(FramingMatrix))]
    public async Task BufferedMessageReadsReturnTheFullPayload(
        int packetSize,
        int readChunk,
        int receiveBuffer)
    {
        var first = BuildResponse();
        var second = BuildDoneOnly();
        using MemoryStream framed = new();
        await TdsPacketWriter.WriteMessageCoreAsync(
          framed,
          TdsMessageType.TabularResult,
          first,
          packetSize,
          default);
        await TdsPacketWriter.WriteMessageCoreAsync(
          framed,
          TdsMessageType.TabularResult,
          second,
          packetSize,
          default);
        using ChunkedStream stream = new(framed.ToArray(), readChunk);
        var reader = TdsPacketReader.CreateBuffered(stream, receiveBuffer);

        var firstMessage = await reader.ReadMessageAsync(default);
        CollectionAssert.AreEqual(first, firstMessage.Payload.ToArray());
        var secondMessage = await reader.ReadMessageAsync(default);
        CollectionAssert.AreEqual(second, secondMessage.Payload.ToArray());
        Assert.AreEqual(TdsMessageType.TabularResult, secondMessage.Type);
        reader.Release();
    }

    [TestMethod]
    public async Task CoalescedResponsesAreServedFromOneReceive()
    {
        var first = BuildDoneOnly();
        var second = BuildDoneOnly(rowCount: 2);
        using MemoryStream framed = new();
        await TdsPacketWriter.WriteMessageCoreAsync(
          framed,
          TdsMessageType.TabularResult,
          first,
          4096,
          default);
        await TdsPacketWriter.WriteMessageCoreAsync(
          framed,
          TdsMessageType.TabularResult,
          second,
          4096,
          default);
        using ChunkedStream stream = new(framed.ToArray(), int.MaxValue);
        var reader = TdsPacketReader.CreateBuffered(stream);

        CollectionAssert.AreEqual(first, (await reader.ReadMessageAsync(default)).Payload.ToArray());
        Assert.AreEqual(1, stream.ReadCount);
        CollectionAssert.AreEqual(second, (await reader.ReadMessageAsync(default)).Payload.ToArray());
        Assert.AreEqual(1, stream.ReadCount);
        reader.Release();
    }

    [TestMethod]
    public async Task ExactReaderNeverReadsPastTheMessage()
    {
        var payload = BuildResponse();
        var message = await FrameAsync(512, payload);
        byte[] trailing = [0x16, 0x03, 0x03, 0x00, 0x10];
        using MemoryStream stream = new([.. message, .. trailing]);

        var read = await new TdsPacketReader(stream).ReadMessageAsync(default);

        CollectionAssert.AreEqual(payload, read.Payload.ToArray());
        Assert.AreEqual(message.Length, stream.Position);
    }

    [TestMethod]
    public async Task ReleasedReaderRejectsFurtherReceives()
    {
        using MemoryStream stream = new(await FrameAsync(512, BuildDoneOnly()));
        var reader = TdsPacketReader.CreateBuffered(stream);
        reader.Release();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
          () => reader.BeginMessageAsync(default).AsTask());
    }

    [TestMethod]
    public async Task ReleaseDuringPendingReceiveFailsThatReceive()
    {
        var framed = await FrameAsync(512, BuildDoneOnly());
        using GatedStream stream = new(framed);
        var reader = TdsPacketReader.CreateBuffered(stream);

        var pending = reader.BeginMessageAsync(default).AsTask();
        await stream.ReadStarted;
        reader.Release();
        stream.Open();

        // The receive completed into a buffer that went back to the pool, so the caller must not
        // see its bytes.
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => pending);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
          () => reader.BeginMessageAsync(default).AsTask());
    }

    [TestMethod]
    public async Task TruncatedRowInFinalPacketIsRejected()
    {
        ArrayBufferWriter<byte> response = new();
        WriteColumnMetadata(response);
        response.WriteByte(TdsTokenType.Row);
        response.WriteByte(sizeof(int));
        response.WriteInt32LittleEndian(1);
        response.WriteUInt16LittleEndian(10);
        response.Write("ab"u8);
        using MemoryStream stream = new(await FrameAsync(4096, response.WrittenSpan.ToArray()));
        var reader = TdsPacketReader.CreateBuffered(stream);
        _ = await reader.BeginMessageAsync(default);
        TdsStreamingTokenReader tokens = new(reader, default);
        Assert.AreEqual(TdsTokenType.ColumnMetadata, await tokens.ReadTokenTypeAsync());
        var columns = await tokens.ReadColumnsAsync();
        Assert.AreEqual(TdsTokenType.Row, await tokens.ReadTokenTypeAsync());

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
          () => tokens.ReadRowAsync(columns, nullCompressed: false, new TdsRowBuffer()).AsTask());
        reader.Release();
    }

    private static List<string> ParseWholeMessage(byte[] payload)
    {
        List<string> events = [];
        TdsTokenReader reader = new(payload);
        IReadOnlyList<TdsColumn> columns = [];
        TdsRowBuffer row = new(8);
        while (reader.HasRemaining)
        {
            var token = reader.ReadTokenType();
            switch (token)
            {
                case TdsTokenType.ColumnMetadata:
                    columns = reader.ReadColumns();
                    events.Add(DescribeColumns(columns));
                    break;
                case TdsTokenType.Row:
                case TdsTokenType.NbcRow:
                    reader.ReadRow(columns, token == TdsTokenType.NbcRow, row);
                    events.Add("row:" + Convert.ToHexString(row.WrittenSpan));
                    break;
                case TdsTokenType.Info:
                    events.Add("info:" + reader.ReadMessage());
                    break;
                case TdsTokenType.EnvironmentChange:
                    events.Add("env:" + reader.ReadEnvironmentChange());
                    break;
                case TdsTokenType.Done:
                    events.Add("done:" + reader.ReadDone());
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected token 0x{token:X2}.");
            }
        }

        return events;
    }

    private static async Task<List<string>> ParseStreamingAsync(TdsPacketReader packets)
    {
        List<string> events = [];
        Assert.AreEqual(TdsMessageType.TabularResult, await packets.BeginMessageAsync(default));
        TdsStreamingTokenReader reader = new(packets, default);
        IReadOnlyList<TdsColumn> columns = [];
        TdsRowBuffer row = new(8);
        while (reader.HasRemaining)
        {
            var token = await reader.ReadTokenTypeAsync();
            switch (token)
            {
                case TdsTokenType.ColumnMetadata:
                    columns = await reader.ReadColumnsAsync();
                    events.Add(DescribeColumns(columns));
                    break;
                case TdsTokenType.Row:
                case TdsTokenType.NbcRow:
                    await reader.ReadRowAsync(columns, token == TdsTokenType.NbcRow, row);
                    events.Add("row:" + Convert.ToHexString(row.WrittenSpan));
                    break;
                case TdsTokenType.Info:
                    events.Add("info:" + await reader.ReadMessageAsync());
                    break;
                case TdsTokenType.EnvironmentChange:
                    events.Add("env:" + await reader.ReadEnvironmentChangeAsync());
                    break;
                case TdsTokenType.Done:
                    events.Add("done:" + await reader.ReadDoneAsync());
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected token 0x{token:X2}.");
            }
        }

        return events;
    }

    private static string DescribeColumns(IReadOnlyList<TdsColumn> columns) =>
      "columns:" + string.Join(
        ",",
        columns.Select(static column => $"{column.Column.Name}/{column.TypeInfo}"));

    private static byte[] BuildResponse()
    {
        ArrayBufferWriter<byte> response = new();
        TdsTokenParserTests.WriteEnvironmentChange(
          response,
          TdsEnvironmentChange.Database,
          "apex",
          "master");
        WriteColumnMetadata(response);
        var large = string.Concat(Enumerable.Repeat("0123456789abcdef", 200));
        for (var i = 0; i < 40; i++)
        {
            var nullCompressed = i % 3 == 0;
            response.WriteByte(nullCompressed ? TdsTokenType.NbcRow : TdsTokenType.Row);
            if (nullCompressed)
            {
                // Columns 1 (name) and 4 (flag) are null in every NBCROW.
                response.WriteByte(0b0001_0010);
            }

            response.WriteByte(sizeof(int));
            response.WriteInt32LittleEndian(i);
            if (!nullCompressed)
            {
                var name = $"name-{i}";
                response.WriteUInt16LittleEndian(checked((ushort)(name.Length * 2)));
                response.WriteUtf16(name);
            }

            WritePlp(response, (i % 4) switch
            {
                0 => null,
                1 => string.Empty,
                2 => $"body-{i}",
                _ => large,
            }, unknownLength: i % 5 == 0);
            if (i % 7 == 0)
            {
                response.WriteUInt16LittleEndian(ushort.MaxValue);
            }
            else
            {
                response.WriteUInt16LittleEndian(3);
                response.Write<byte>([(byte)i, 0xAB, 0xCD]);
            }

            if (!nullCompressed)
            {
                if (i % 2 == 0)
                {
                    response.WriteByte(0);
                }
                else
                {
                    response.WriteByte(1);
                    response.WriteByte(1);
                }
            }

            response.WriteInt32LittleEndian(-i);
            if (i == 20)
            {
                WriteInfo(response, "halfway");
            }
        }

        WriteDone(response, rowCount: 40);
        return response.WrittenSpan.ToArray();
    }

    private static byte[] BuildDoneOnly(long rowCount = 1)
    {
        ArrayBufferWriter<byte> response = new();
        WriteDone(response, rowCount);
        return response.WrittenSpan.ToArray();
    }

    private static void WriteColumnMetadata(ArrayBufferWriter<byte> response)
    {
        response.WriteByte(TdsTokenType.ColumnMetadata);
        response.WriteUInt16LittleEndian(6);
        WriteColumnHeader(response);
        response.WriteByte(TdsDataType.IntN);
        response.WriteByte(sizeof(int));
        response.WriteBVarChar("id");
        WriteColumnHeader(response);
        response.WriteByte(TdsDataType.NVarChar);
        response.WriteUInt16LittleEndian(128);
        response.WriteUInt32LittleEndian(UnicodeCollation);
        response.WriteByte(0);
        response.WriteBVarChar("name");
        WriteColumnHeader(response);
        response.WriteByte(TdsDataType.NVarChar);
        response.WriteUInt16LittleEndian(ushort.MaxValue);
        response.WriteUInt32LittleEndian(UnicodeCollation);
        response.WriteByte(0);
        response.WriteBVarChar("body");
        WriteColumnHeader(response);
        response.WriteByte(TdsDataType.BigVarBinary);
        response.WriteUInt16LittleEndian(16);
        response.WriteBVarChar("bin");
        WriteColumnHeader(response);
        response.WriteByte(TdsDataType.BitN);
        response.WriteByte(1);
        response.WriteBVarChar("flag");
        WriteColumnHeader(response);
        response.WriteByte(TdsDataType.Int4);
        response.WriteBVarChar("fixed");
    }

    private static void WriteColumnHeader(ArrayBufferWriter<byte> response)
    {
        response.WriteUInt32LittleEndian(0);
        response.WriteUInt16LittleEndian(0);
    }

    private static void WritePlp(ArrayBufferWriter<byte> response, string? value, bool unknownLength)
    {
        if (value is null)
        {
            response.WriteUInt64LittleEndian(ulong.MaxValue);
            return;
        }

        var bytes = System.Text.Encoding.Unicode.GetBytes(value);
        response.WriteUInt64LittleEndian(unknownLength ? 0xFFFF_FFFF_FFFF_FFFE : (ulong)bytes.Length);
        for (var offset = 0; offset < bytes.Length; offset += 1000)
        {
            var count = Math.Min(1000, bytes.Length - offset);
            response.WriteUInt32LittleEndian((uint)count);
            response.Write(bytes.AsSpan(offset, count));
        }

        response.WriteUInt32LittleEndian(0);
    }

    private static void WriteInfo(ArrayBufferWriter<byte> response, string message)
    {
        ArrayBufferWriter<byte> body = new();
        body.WriteInt32LittleEndian(50000);
        body.WriteByte(1);
        body.WriteByte(10);
        body.WriteUInt16LittleEndian(checked((ushort)message.Length));
        body.WriteUtf16(message);
        body.WriteBVarChar("server");
        body.WriteBVarChar(string.Empty);
        body.WriteInt32LittleEndian(3);
        response.WriteByte(TdsTokenType.Info);
        response.WriteUInt16LittleEndian(checked((ushort)body.WrittenCount));
        response.Write(body.WrittenSpan);
    }

    private static void WriteDone(ArrayBufferWriter<byte> response, long rowCount)
    {
        response.WriteByte(TdsTokenType.Done);
        response.WriteUInt16LittleEndian((ushort)TdsDoneStatus.Count);
        response.WriteUInt16LittleEndian(0xC1);
        response.WriteInt64LittleEndian(rowCount);
    }

    private static async Task<byte[]> FrameAsync(int packetSize, byte[] payload)
    {
        using MemoryStream framed = new();
        await TdsPacketWriter.WriteMessageCoreAsync(
          framed,
          TdsMessageType.TabularResult,
          payload,
          packetSize,
          default);
        return framed.ToArray();
    }

    /// <summary>Holds the first read until <see cref="Open"/> is called.</summary>
    private sealed class GatedStream(byte[] data) : Stream
    {
        private readonly TaskCompletionSource _readStarted =
          new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _open =
          new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        internal Task ReadStarted => _readStarted.Task;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        internal void Open() => _open.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            await _open.Task.ConfigureAwait(false);
            var count = Math.Min(buffer.Length, data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
          throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
          throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
          throw new NotSupportedException();
    }

    /// <summary>Returns at most <c>chunk</c> bytes per read, like a socket delivering segments.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : Stream
    {
        private int _position;

        internal int ReadCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
          Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ReadCount++;
            var count = Math.Min(Math.Min(buffer.Length, chunk), data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
          new(Read(buffer.Span));

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
          Task.FromResult(Read(buffer.AsSpan(offset, count)));

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

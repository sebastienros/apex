using System.Buffers.Binary;
using Apex.PgClient.Internal;
using Apex.SqlClient;

namespace Apex.PgClient.Tests;

[TestClass]
public sealed class PgReviewRegressionTests
{
    [TestMethod]
    public void ArrayParserKeepsEqualsSignsInsideElements()
    {
        CollectionAssert.AreEqual(new[] { "a=b", "c" }, PgArrayParser.Parse("{a=b,c}"));
        CollectionAssert.AreEqual(new[] { "x", "y=z" }, PgArrayParser.Parse("[1:2]={x,y=z}"));
    }

    [TestMethod]
    public void UtcDateTimeParametersCarryAnExplicitZone()
    {
        var utc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

        Assert.AreEqual("2026-01-02 03:04:05.0000000Z", PgTextCodec.FormatParameter(utc));
        Assert.AreEqual("2026-01-02 03:04:05.0000000", PgTextCodec.FormatParameter(unspecified));
        Assert.AreEqual(
            "{\"2026-01-02 03:04:05.0000000Z\"}",
            PgTextCodec.FormatParameter(SqlValue.From(new[] { utc })));
    }

    [TestMethod]
    [DataRow("57P05", "FATAL", true)] // idle_session_timeout
    [DataRow("08003", "FATAL", true)] // connection_does_not_exist
    [DataRow("25P03", "FATAL", true)] // idle_in_transaction_session_timeout (severity only)
    [DataRow("XX000", "PANIC", true)]
    [DataRow("23505", "ERROR", false)]
    [DataRow("08P01", "ERROR", false)] // protocol violation reported as ERROR keeps the session
    public void ClassifiesServerFatalErrorsAsConnectionFatal(string sqlState, string severity, bool fatal)
    {
        PgException error = new(new Dictionary<char, string>
        {
            ['C'] = sqlState,
            ['V'] = severity,
            ['M'] = "test",
        });

        Assert.AreEqual(fatal, PgConnection.IsFatalConnectionError(error));
    }

    [TestMethod]
    public async Task FailedCommandEncodingLeavesNoPartialMessagesBuffered()
    {
        using MemoryStream stream = new();
        PgWireWriter writer = new(stream, new PgTypeRegistry());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await writer.WriteExtendedQueryAsync(
                "SELECT $1",
                SqlParameters.Create(SqlValue.From(new UnformattableValue())),
                CancellationToken.None));
        Assert.AreEqual(0, stream.Length);

        await writer.WriteExtendedQueryAsync("SELECT 1", SqlParameters.Empty, CancellationToken.None);
        var sent = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.AreEqual('P', sent[0], "The next command must start with its own Parse.");
        Assert.DoesNotContain("SELECT $1", sent);
        Assert.Contains("SELECT 1", sent);
    }

    [TestMethod]
    public async Task FailedCommandEncodingKeepsEarlierUnflushedPipelinedCommands()
    {
        using MemoryStream stream = new();
        PgWireWriter writer = new(stream, new PgTypeRegistry());

        // A pipelined prepared execute buffered with flush: false must survive the rollback.
        await writer.WritePreparedQueryAsync(
            "S1",
            SqlParameters.Create(42),
            CancellationToken.None,
            flush: false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await writer.WriteExtendedQueryAsync(
                "SELECT $1",
                SqlParameters.Create(SqlValue.From(new UnformattableValue())),
                CancellationToken.None));
        await writer.FlushAsync(CancellationToken.None);

        var sent = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.AreEqual('B', sent[0], "The buffered Bind for S1 must be sent first.");
        Assert.Contains("S1", sent);
        Assert.Contains("42", sent);
        Assert.DoesNotContain("SELECT $1", sent);
    }

    [TestMethod]
    public async Task FailedPreparedBindLeavesEarlierUnflushedCommandsIntact()
    {
        using MemoryStream stream = new();
        PgWireWriter writer = new(stream, new PgTypeRegistry());

        // Bind is written straight into the connection buffer; a parameter that fails to format
        // part-way must roll back to the end of the earlier buffered command.
        await writer.WritePreparedQueryAsync(
            "S1",
            SqlParameters.Create(42),
            CancellationToken.None,
            flush: false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await writer.WritePreparedQueryAsync(
                "S2",
                SqlParameters.Create(7, "text", SqlValue.From(new UnformattableValue())),
                CancellationToken.None,
                flush: false));
        await writer.FlushAsync(CancellationToken.None);

        var frames = ReadFrames(stream.ToArray());
        CollectionAssert.AreEqual(
            new[] { 'B', 'D', 'E', 'S' },
            frames.Select(static frame => (char)frame.Type).ToArray());
        Assert.Contains("S1", System.Text.Encoding.UTF8.GetString(frames[0].Payload));
    }

    [TestMethod]
    public async Task BindParametersMatchTextCodecFormatting()
    {
        SqlValue[] values =
        [
            (short)-12, 42, int.MinValue, long.MaxValue, 1.5f, float.NaN, 3.141592653589793,
            double.NegativeInfinity, 1e-300, 12.50m, -79228162514264337593543950335m, true, false,
            "héllo", string.Empty, Guid.Parse("12345678-1234-5678-9012-123456789abc"),
            new DateOnly(2026, 11, 22), SqlValue.Null, new byte[] { 1, 2, 255 },
        ];
        using MemoryStream stream = new();
        PgWireWriter writer = new(stream, new PgTypeRegistry());
        await writer.WritePreparedQueryAsync("S1", SqlParameters.Create(values), CancellationToken.None);

        var frames = ReadFrames(stream.ToArray());
        Assert.AreEqual('B', (char)frames[0].Type);
        var bind = frames[0].Payload;
        var position = Array.IndexOf(bind, (byte)0) + 1; // portal
        position = Array.IndexOf(bind, (byte)0, position) + 1; // statement
        Assert.AreEqual(0, BinaryPrimitives.ReadInt16BigEndian(bind.AsSpan(position)));
        position += sizeof(short);
        Assert.AreEqual(values.Length, BinaryPrimitives.ReadInt16BigEndian(bind.AsSpan(position)));
        position += sizeof(short);
        foreach (var value in values)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bind.AsSpan(position));
            position += sizeof(int);
            if (value.IsNull)
            {
                Assert.AreEqual(-1, length);
                continue;
            }

            Assert.AreEqual(
                PgTextCodec.FormatParameter(value),
                System.Text.Encoding.UTF8.GetString(bind, position, length),
                value.Kind.ToString());
            position += length;
        }

        Assert.AreEqual(1, BinaryPrimitives.ReadInt16BigEndian(bind.AsSpan(position)));
        Assert.AreEqual(1, BinaryPrimitives.ReadInt16BigEndian(bind.AsSpan(position + 2)));
        Assert.AreEqual(position + 4, bind.Length);
    }

    private static List<(byte Type, byte[] Payload)> ReadFrames(byte[] data)
    {
        List<(byte Type, byte[] Payload)> frames = [];
        var position = 0;
        while (position < data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(position + 1));
            frames.Add((data[position], data[(position + 5)..(position + 1 + length)]));
            position += 1 + length;
        }

        return frames;
    }

    private sealed class UnformattableValue
    {
        public override string ToString() => throw new InvalidOperationException("cannot format");
    }
}

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

    private sealed class UnformattableValue
    {
        public override string ToString() => throw new InvalidOperationException("cannot format");
    }
}

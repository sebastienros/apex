using Apex.MySqlClient.Internal;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlPacketsTests
{
    [TestMethod]
    public void IsOkRequiresHeaderByteAndMinimumLength()
    {
        Assert.IsTrue(MySqlPackets.IsOk([0x00, 0, 0, 0, 0, 0, 0]));
        Assert.IsFalse(MySqlPackets.IsOk([0x00, 0, 0, 0, 0, 0]));
        Assert.IsFalse(MySqlPackets.IsOk([]));
        Assert.IsFalse(MySqlPackets.IsOk([0xFF, 0, 0, 0, 0, 0, 0]));
    }

    [TestMethod]
    public void IsErrorChecksHeaderByteOnly()
    {
        Assert.IsTrue(MySqlPackets.IsError([0xFF]));
        Assert.IsFalse(MySqlPackets.IsError([0x00]));
        Assert.IsFalse(MySqlPackets.IsError([]));
    }

    [TestMethod]
    public void IsEofUsesDeprecateEofLengthRule()
    {
        // Without DeprecateEof, EOF packets are always shorter than 9 bytes.
        Assert.IsTrue(MySqlPackets.IsEof([0xFE, 0, 0, 0, 0], deprecateEof: false));
        Assert.IsFalse(MySqlPackets.IsEof([0xFE, 0, 0, 0, 0, 0, 0, 0, 0], deprecateEof: false));

        // With DeprecateEof, the header byte 0xFE is reused for a long OK packet and only the
        // maximum frame boundary disambiguates it from column data starting with 0xFE.
        var longOkLike = new byte[MySqlProtocol.MaximumFramePayloadLength];
        longOkLike[0] = 0xFE;
        Assert.IsFalse(MySqlPackets.IsEof(longOkLike, deprecateEof: true));
        Assert.IsTrue(MySqlPackets.IsEof([0xFE, 0, 0, 0, 0, 0, 0, 0, 0, 0], deprecateEof: true));
    }

    [TestMethod]
    public void ReadsOkPacketWithProtocol41Fields()
    {
        // header, affected rows=5, last insert id=42, status=AutoCommit, warnings=2, info="done"
        byte[] payload =
        [
          0x00,
      5,
      42,
      0x02, 0x00,
      2, 0,
      (byte)'d', (byte)'o', (byte)'n', (byte)'e',
    ];

        var completion = MySqlPackets.ReadOk(payload, MySqlCapabilities.Protocol41);

        Assert.AreEqual(5, completion.AffectedRows);
        Assert.AreEqual(42ul, completion.LastInsertId);
        Assert.AreEqual(MySqlServerStatus.AutoCommit, completion.Status);
        Assert.AreEqual(2, completion.Warnings);
        Assert.AreEqual("done", completion.Info);
    }

    [TestMethod]
    public void ReadsOkPacketWithOnlyTransactionsCapability()
    {
        byte[] payload = [0x00, 0, 0, 0x01, 0x00];

        var completion = MySqlPackets.ReadOk(payload, MySqlCapabilities.Transactions);

        Assert.AreEqual(MySqlServerStatus.InTransaction, completion.Status);
        Assert.AreEqual(0, completion.Warnings);
    }

    [TestMethod]
    public void ReadsOkPacketWithoutStatusCapabilities()
    {
        byte[] payload = [0x00, 3, 0];

        var completion = MySqlPackets.ReadOk(payload, MySqlCapabilities.None);

        Assert.AreEqual(3, completion.AffectedRows);
        Assert.AreEqual(MySqlServerStatus.None, completion.Status);
        Assert.AreEqual(string.Empty, completion.Info);
    }

    [TestMethod]
    public void ReadOkRejectsAffectedRowsAboveInt64MaxValue()
    {
        byte[] payload = [0x00, 0xFE, 0, 0, 0, 0, 0, 0, 0, 0x80, 0];

        Assert.ThrowsExactly<InvalidDataException>(
          () => MySqlPackets.ReadOk(payload, MySqlCapabilities.None));
    }

    [TestMethod]
    public void ReadsEofPacketWithProtocol41Fields()
    {
        byte[] payload = [0xFE, 1, 0, 0x02, 0x00];

        var completion = MySqlPackets.ReadEof(payload, MySqlCapabilities.Protocol41);

        Assert.AreEqual(1, completion.Warnings);
        Assert.AreEqual(MySqlServerStatus.AutoCommit, completion.Status);
    }

    [TestMethod]
    public void ReadsEofPacketWithoutProtocol41Capability()
    {
        byte[] payload = [0xFE];

        var completion = MySqlPackets.ReadEof(payload, MySqlCapabilities.None);

        Assert.AreEqual(0, completion.Warnings);
        Assert.AreEqual(MySqlServerStatus.None, completion.Status);
    }

    [TestMethod]
    public void ReadsErrorPacketWithSqlState()
    {
        byte[] payload =
        [
          0xFF,
      0x19, 0x04, // 1049
      (byte)'#',
      (byte)'4', (byte)'2', (byte)'0', (byte)'0', (byte)'0',
      (byte)'U', (byte)'n', (byte)'k', (byte)'n', (byte)'o', (byte)'w', (byte)'n', (byte)' ',
      (byte)'d', (byte)'b',
    ];

        var exception = MySqlPackets.ReadError(payload);

        Assert.AreEqual(1049, exception.ErrorNumber);
        Assert.AreEqual("42000", exception.SqlState);
        Assert.AreEqual("Unknown db", exception.Message);
    }

    [TestMethod]
    public void ReadsErrorPacketWithoutSqlState()
    {
        byte[] payload = [0xFF, 0x01, 0x00];

        var exception = MySqlPackets.ReadError(payload);

        Assert.AreEqual(1, exception.ErrorNumber);
        Assert.IsNull(exception.SqlState);
        Assert.AreEqual("MySQL error 1.", exception.Message);
    }
}

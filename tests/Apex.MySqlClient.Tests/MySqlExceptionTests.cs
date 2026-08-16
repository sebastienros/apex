using Apex.MySqlClient.Internal;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlExceptionTests
{
    [TestMethod]
    [DataRow(1053)]
    [DataRow(1077)]
    [DataRow(1078)]
    [DataRow(1079)]
    [DataRow(1080)]
    [DataRow(1152)]
    [DataRow(1153)]
    [DataRow(1159)]
    [DataRow(1160)]
    [DataRow(1161)]
    [DataRow(1184)]
    [DataRow(1927)]
    [DataRow(2006)]
    [DataRow(2013)]
    [DataRow(4031)]
    public void RecognizesFatalErrorNumbers(int errorNumber)
    {
        var exception = CreateError(errorNumber);

        Assert.IsTrue(exception.IsFatal);
    }

    [TestMethod]
    [DataRow(1062)]
    [DataRow(1146)]
    [DataRow(1)]
    [DataRow(0)]
    public void DoesNotClassifyOrdinaryErrorsAsFatal(int errorNumber)
    {
        var exception = CreateError(errorNumber);

        Assert.IsFalse(exception.IsFatal);
    }

    [TestMethod]
    [DataRow(1317)]
    [DataRow(3024)]
    public void RecognizesInterruptedErrorNumbers(int errorNumber)
    {
        var exception = CreateError(errorNumber);

        Assert.IsTrue(exception.IsInterrupted);
    }

    [TestMethod]
    public void DoesNotClassifyOrdinaryErrorsAsInterrupted()
    {
        var exception = CreateError(1062);

        Assert.IsFalse(exception.IsInterrupted);
    }

    [TestMethod]
    public void ParsesErrorNumberAndSqlStateFromPacket()
    {
        var exception = MySqlPackets.ReadError(
          [
            0xFF,
        0x16, 0x04, // 1046
        (byte)'#',
        (byte)'3', (byte)'D', (byte)'0', (byte)'0', (byte)'0',
        (byte)'N', (byte)'o', (byte)' ', (byte)'d', (byte)'a', (byte)'t', (byte)'a', (byte)'b',
        (byte)'a', (byte)'s', (byte)'e',
      ]);

        Assert.AreEqual(1046, exception.ErrorNumber);
        Assert.AreEqual("3D000", exception.SqlState);
        Assert.AreEqual("No database", exception.Message);
    }

    [TestMethod]
    public void UnsupportedTypeExceptionReportsHexFormattedType()
    {
        MySqlUnsupportedTypeException exception = new(MySqlType.Geometry);

        Assert.AreEqual(MySqlType.Geometry, exception.Type);
        StringAssert.Contains(exception.Message, "0xFF");
    }

    private static MySqlException CreateError(int errorNumber)
    {
        byte[] payload =
        [
          0xFF,
      (byte)(errorNumber & 0xFF),
      (byte)((errorNumber >> 8) & 0xFF),
    ];
        return MySqlPackets.ReadError(payload);
    }
}

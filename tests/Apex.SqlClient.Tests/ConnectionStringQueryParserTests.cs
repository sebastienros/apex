using Apex.SqlClient.Internal;

namespace Apex.SqlClient.Tests;

[TestClass]
public sealed class ConnectionStringQueryParserTests
{
    [TestMethod]
    public void ParsesFormEncodedQuery()
    {
        var values =
          ConnectionStringQueryParser.Parse(
            "?user=app+user&password=s%40cret&flag&empty=");

        Assert.AreEqual("app user", values["USER"]);
        Assert.AreEqual("s@cret", values["password"]);
        Assert.AreEqual(string.Empty, values["flag"]);
        Assert.AreEqual(string.Empty, values["empty"]);
    }

    [TestMethod]
    public void LastDuplicateValueWins()
    {
        var values =
          ConnectionStringQueryParser.Parse(
            "?host=first&&HOST=second");

        Assert.HasCount(1, values);
        Assert.AreEqual("second", values["host"]);
    }
}

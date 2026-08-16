using System.Globalization;
using System.Numerics;
using Apex.SqlClient;

namespace Apex.MySqlClient.Tests;

[TestClass]
public sealed class MySqlDecimalTests
{
    [TestMethod]
    public void PreservesArbitraryPrecisionAndScale()
    {
        const string text =
          "12345678901234567890123456789012345.123456789012345678901234567890";

        MySqlDecimal value = MySqlDecimal.Parse(text);

        Assert.AreEqual(30, value.Scale);
        Assert.AreEqual(
          BigInteger.Parse(
            "12345678901234567890123456789012345123456789012345678901234567890",
            CultureInfo.InvariantCulture),
          value.UnscaledValue);
        Assert.AreEqual(text, value.ToString());
    }

    [TestMethod]
    public void ConvertsRepresentableValuesToDecimalAndSqlValue()
    {
        MySqlDecimal value = MySqlDecimal.Parse("-12345.6789");
        SqlValue parameter = value;

        Assert.AreEqual(-12345.6789m, value.ToDecimal());
        Assert.AreEqual(value, parameter.Get<MySqlDecimal>());
    }

    [TestMethod]
    public void RejectsInvalidRepresentations()
    {
        Assert.ThrowsExactly<FormatException>(() => MySqlDecimal.Parse("1.2.3"));
        Assert.ThrowsExactly<ArgumentException>(() => MySqlDecimal.Parse(string.Empty));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
          () => MySqlDecimal.Create(BigInteger.One, -1));
    }
}

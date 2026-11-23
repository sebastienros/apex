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

    [TestMethod]
    [DataRow("0")]
    [DataRow("-0.00")]
    [DataRow("+12.5")]
    [DataRow("18446744073709551615")]
    [DataRow("18446744073709551616")]
    [DataRow("-1844674407370955161.5")]
    [DataRow("98765432100.0000000100")]
    [DataRow("340282366920938463463374607431768211455")]
    [DataRow("340282366920938463463374607431768211456")]
    [DataRow("-3402823669209384634633746074317682114.56")]
    [DataRow("12345678901234567890123456789012345678901234567890.123456789")]
    public void Utf8ParseMatchesBigIntegerAcrossAccumulatorWidths(string text)
    {
        var value = MySqlDecimal.Parse(System.Text.Encoding.UTF8.GetBytes(text));
        var unsigned = text.TrimStart('+', '-');
        var separator = unsigned.IndexOf('.', StringComparison.Ordinal);
        var expectedScale = separator < 0 ? 0 : unsigned.Length - separator - 1;
        var expected = BigInteger.Parse(unsigned.Replace(".", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture);
        if (text.StartsWith('-'))
        {
            expected = -expected;
        }

        Assert.AreEqual(expectedScale, value.Scale);
        Assert.AreEqual(expected, value.UnscaledValue);
        Assert.AreEqual(MySqlDecimal.Parse(text), value);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("-")]
    [DataRow(".")]
    [DataRow("1.2.3")]
    [DataRow("12a")]
    [DataRow(" 1")]
    public void Utf8ParseRejectsInvalidText(string text) =>
      Assert.ThrowsExactly<FormatException>(
        () => MySqlDecimal.Parse(System.Text.Encoding.UTF8.GetBytes(text)));
}

using System.Globalization;
using System.Numerics;
using Apex.SqlClient;

namespace Apex.MySqlClient;

/// <summary>An arbitrary-precision fixed-point MySQL DECIMAL value.</summary>
public readonly record struct MySqlDecimal
{
    private MySqlDecimal(BigInteger unscaledValue, int scale)
    {
        UnscaledValue = unscaledValue;
        Scale = scale;
    }

    /// <summary>Gets the signed integer formed by removing the decimal separator.</summary>
    public BigInteger UnscaledValue { get; }

    /// <summary>Gets the number of fractional decimal digits.</summary>
    public int Scale { get; }

    /// <summary>Creates a fixed-point value from its unscaled coefficient and scale.</summary>
    public static MySqlDecimal Create(BigInteger unscaledValue, int scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(scale);
        return new MySqlDecimal(unscaledValue, scale);
    }

    /// <summary>Parses an invariant MySQL DECIMAL representation.</summary>
    public static MySqlDecimal Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var text = value.AsSpan();
        var negative = text[0] == '-';
        if (negative || text[0] == '+')
        {
            text = text[1..];
        }

        if (text.IsEmpty)
        {
            throw new FormatException($"Invalid MySQL DECIMAL value '{value}'.");
        }

        var coefficient = BigInteger.Zero;
        var scale = 0;
        var separator = false;
        var digits = 0;
        foreach (var character in text)
        {
            if (character == '.' && !separator)
            {
                separator = true;
                continue;
            }

            if (character is < '0' or > '9')
            {
                throw new FormatException($"Invalid MySQL DECIMAL value '{value}'.");
            }

            coefficient = (coefficient * 10) + (character - '0');
            digits++;
            if (separator)
            {
                scale++;
            }
        }

        if (digits == 0)
        {
            throw new FormatException($"Invalid MySQL DECIMAL value '{value}'.");
        }

        return new MySqlDecimal(negative ? -coefficient : coefficient, scale);
    }

    /// <summary>Converts the value to <see cref="decimal"/>, failing when it is out of range.</summary>
    public decimal ToDecimal() =>
      decimal.Parse(ToString(), NumberStyles.Number, CultureInfo.InvariantCulture);

    /// <summary>Converts the value to a common SQL parameter.</summary>
    public static implicit operator SqlValue(MySqlDecimal value) => SqlValue.From(value);

    /// <inheritdoc />
    public override string ToString()
    {
        var negative = UnscaledValue.Sign < 0;
        var digits = BigInteger.Abs(UnscaledValue).ToString(CultureInfo.InvariantCulture);
        if (Scale != 0)
        {
            if (digits.Length <= Scale)
            {
                digits = new string('0', Scale - digits.Length + 1) + digits;
            }

            digits = digits.Insert(digits.Length - Scale, ".");
        }

        return negative ? "-" + digits : digits;
    }
}

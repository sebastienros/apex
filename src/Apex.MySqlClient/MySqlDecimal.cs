using System.Globalization;
using System.Numerics;
using System.Text;
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
        return TryParse(value.AsSpan(), out var result)
          ? result
          : throw new FormatException($"Invalid MySQL DECIMAL value '{value}'.");
    }

    /// <summary>Parses the UTF-8 text a MySQL server sends for DECIMAL values.</summary>
    internal static MySqlDecimal Parse(ReadOnlySpan<byte> value) =>
      TryParse(value, out var result)
        ? result
        : throw new FormatException(
          $"Invalid MySQL DECIMAL value '{Encoding.UTF8.GetString(value)}'.");

    /// <summary>
    /// Accumulates the coefficient in <see cref="ulong"/>, then <see cref="UInt128"/>, and only
    /// falls back to per-digit <see cref="BigInteger"/> arithmetic beyond 38 significant digits.
    /// </summary>
    private static bool TryParse<TChar>(ReadOnlySpan<TChar> text, out MySqlDecimal result)
      where TChar : unmanaged, IBinaryInteger<TChar>
    {
        result = default;
        if (text.IsEmpty)
        {
            return false;
        }

        var sign = uint.CreateTruncating(text[0]);
        var negative = sign == '-';
        if (negative || sign == '+')
        {
            text = text[1..];
        }

        // Below these thresholds, value * 10 + 9 cannot overflow the accumulator.
        const ulong SmallLimit = (ulong.MaxValue - 9) / 10;
        var mediumLimit = (UInt128.MaxValue - 9) / 10;
        ulong small = 0;
        UInt128 medium = 0;
        BigInteger large = default;
        var width = 0;
        var scale = 0;
        var separator = false;
        var digits = 0;
        foreach (var character in text)
        {
            var code = uint.CreateTruncating(character);
            if (code == '.' && !separator)
            {
                separator = true;
                continue;
            }

            var digit = code - '0';
            if (digit > 9)
            {
                return false;
            }

            digits++;
            if (separator)
            {
                scale++;
            }

            switch (width)
            {
                case 0 when small <= SmallLimit:
                    small = (small * 10) + digit;
                    break;
                case 0:
                    medium = ((UInt128)small * 10) + digit;
                    width = 1;
                    break;
                case 1 when medium <= mediumLimit:
                    medium = (medium * 10) + digit;
                    break;
                case 1:
                    large = ((BigInteger)medium * 10) + digit;
                    width = 2;
                    break;
                default:
                    large = (large * 10) + digit;
                    break;
            }
        }

        if (digits == 0)
        {
            return false;
        }

        BigInteger coefficient = width switch
        {
            0 => small,
            1 => medium,
            _ => large,
        };
        result = new MySqlDecimal(negative ? -coefficient : coefficient, scale);
        return true;
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

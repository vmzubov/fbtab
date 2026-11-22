using System.Globalization;
using System.Numerics;
using FirebirdSql.Data.Types;

namespace Fbtab;

/// <summary>Turns a value read from Firebird into text that does not depend on the machine's locale.</summary>
public static class ValueFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <param name="value">Value from the reader; DBNull and null give null.</param>
    /// <param name="typeName">Firebird type name as the driver reports it (CHAR, DATE, NUMERIC...).</param>
    /// <param name="scale">Digits after the point for NUMERIC / DECIMAL, otherwise null.</param>
    public static string? Format(object? value, string typeName, int? scale)
    {
        switch (value)
        {
            case null:
            case DBNull:
                return null;
            case string s:
                return typeName == "CHAR" ? s.TrimEnd(' ') : s;
            case int or long or short or sbyte or byte:
                return ((IFormattable)value).ToString(null, Inv);
            case BigInteger big:
                return big.ToString(Inv);
            case decimal d:
                return scale is > 0 ? d.ToString("F" + scale.Value.ToString(Inv), Inv) : d.ToString(Inv);
            case double d:
                return FormatDouble(d, d.ToString("R", Inv));
            case float f:
                return FormatDouble(f, f.ToString("R", Inv));
            case bool b:
                return b ? "true" : "false";
            case DateTime dt:
                return typeName == "DATE"
                    ? dt.ToString("yyyy-MM-dd", Inv)
                    : dt.ToString("yyyy-MM-dd HH:mm:ss.ffff", Inv);
            case TimeSpan t:
                return FormatTime(t);
            case FbDecFloat df:
                return FormatDecFloat(df);
            case Guid g:
                return "0x" + Convert.ToHexString(g.ToByteArray(bigEndian: true));
            case byte[] bytes:
                return "0x" + Convert.ToHexString(bytes);
            default:
                return Convert.ToString(value, Inv);
        }
    }

    private static string FormatDouble(double value, string text)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : "-Infinity";
        }

        return text;
    }

    private static string FormatTime(TimeSpan t) => new DateTime(2000, 1, 1).Add(t).ToString("HH:mm:ss.ffff", Inv);

    internal static string FormatDecFloat(FbDecFloat value)
    {
        if (value.Equals(FbDecFloat.PositiveInfinity))
        {
            return "Infinity";
        }

        if (value.Equals(FbDecFloat.NegativeInfinity))
        {
            return "-Infinity";
        }

        if (value.Equals(FbDecFloat.PositiveNaN) || value.Equals(FbDecFloat.NegativeNaN)
            || value.Equals(FbDecFloat.PositiveSignalingNaN) || value.Equals(FbDecFloat.NegativeSignalingNaN))
        {
            return "NaN";
        }

        return FormatScaled(value.Coefficient, value.Exponent);
    }

    /// <summary>coefficient * 10^exponent as a plain number; very large or small exponents stay in E notation.</summary>
    internal static string FormatScaled(BigInteger coefficient, int exponent)
    {
        if (exponent is > 40 or < -60)
        {
            return coefficient.ToString(Inv) + "E" + exponent.ToString(Inv);
        }

        var negative = coefficient.Sign < 0;
        var digits = BigInteger.Abs(coefficient).ToString(Inv);
        if (exponent >= 0)
        {
            digits = coefficient.IsZero ? "0" : digits + new string('0', exponent);
        }
        else
        {
            var scale = -exponent;
            if (digits.Length <= scale)
            {
                digits = new string('0', scale - digits.Length + 1) + digits;
            }

            digits = digits[..^scale] + "." + digits[^scale..];
        }

        return negative ? "-" + digits : digits;
    }
}

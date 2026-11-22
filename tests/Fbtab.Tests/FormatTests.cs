using System.Numerics;

namespace Fbtab.Tests;

public class FormatTests
{
    private static string Row(params string?[] fields)
    {
        var text = new StringWriter();
        var tsv = new TsvWriter(text);
        foreach (var field in fields)
        {
            tsv.Field(field);
        }

        tsv.EndRow();
        return text.ToString();
    }

    [Fact]
    public void EscapesSoThatOneLineIsOneRow()
    {
        Assert.Equal("a\\tb\tc\\nd\\re\t\\\\N\t\t\\N\n", Row("a\tb", "c\nd\re", "\\N", "", null));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData("tab\there")]
    [InlineData("line\r\nbreak")]
    [InlineData("back\\slash \\t \\N")]
    [InlineData("\\")]
    [InlineData("кириллица и 日本語")]
    public void UnescapeReversesEscape(string value)
    {
        var line = Row(value, null).TrimEnd('\n');
        var fields = line.Split('\t');
        Assert.Equal(2, fields.Length);
        Assert.Equal(value, TsvWriter.Unescape(fields[0]));
        Assert.Null(TsvWriter.Unescape(fields[1]));
    }

    [Fact]
    public void NullAndEmptyAndBackslashNDiffer()
    {
        Assert.Equal("\\N\t\t\\\\N\n", Row(null, "", "\\N"));
    }

    [Fact]
    public void FormatsValuesWithoutLocale()
    {
        Assert.Null(ValueFormatter.Format(DBNull.Value, "INTEGER", null));
        Assert.Equal("-5", ValueFormatter.Format(-5, "INTEGER", null));
        Assert.Equal("9223372036854775807", ValueFormatter.Format(long.MaxValue, "BIGINT", null));
        Assert.Equal("170141183460469231731687303715884105727", ValueFormatter.Format(BigInteger.Parse("170141183460469231731687303715884105727"), "INT128", null));
        Assert.Equal("12.50", ValueFormatter.Format(12.5m, "NUMERIC", 2));
        Assert.Equal("-0.050", ValueFormatter.Format(-0.05m, "NUMERIC", 3));
        Assert.Equal("7", ValueFormatter.Format(7m, "NUMERIC", 0));
        Assert.Equal("0.1", ValueFormatter.Format(0.1d, "DOUBLE PRECISION", null));
        Assert.Equal("1E+300", ValueFormatter.Format(1e300, "DOUBLE PRECISION", null));
        Assert.Equal("1.5", ValueFormatter.Format(1.5f, "FLOAT", null));
        Assert.Equal("NaN", ValueFormatter.Format(double.NaN, "DOUBLE PRECISION", null));
        Assert.Equal("-Infinity", ValueFormatter.Format(double.NegativeInfinity, "DOUBLE PRECISION", null));
        Assert.Equal("true", ValueFormatter.Format(true, "BOOLEAN", null));
        Assert.Equal("2026-11-22", ValueFormatter.Format(new DateTime(2026, 11, 22), "DATE", null));
        Assert.Equal("2026-11-22 09:10:00.1234", ValueFormatter.Format(new DateTime(2026, 11, 22, 9, 10, 0).AddTicks(1234000), "TIMESTAMP", null));
        Assert.Equal("09:10:00.1234", ValueFormatter.Format(new TimeSpan(9, 10, 0).Add(TimeSpan.FromTicks(1234000)), "TIME", null));
        Assert.Equal("ab", ValueFormatter.Format("ab   ", "CHAR", null));
        Assert.Equal("ab   ", ValueFormatter.Format("ab   ", "VARCHAR", null));
        Assert.Equal("0x0001FF", ValueFormatter.Format(new byte[] { 0, 1, 255 }, "BLOB", null));
        Assert.Equal("0x", ValueFormatter.Format(Array.Empty<byte>(), "BLOB", null));
    }

    [Theory]
    [InlineData("15", -1, "1.5")]
    [InlineData("-1", -3, "-0.001")]
    [InlineData("0", 0, "0")]
    [InlineData("0", 5, "0")]
    [InlineData("12", 3, "12000")]
    [InlineData("123456", -2, "1234.56")]
    [InlineData("5", 100, "5E100")]
    [InlineData("5", -100, "5E-100")]
    public void FormatsDecFloatAsPlainNumber(string coefficient, int exponent, string expected) =>
        Assert.Equal(expected, ValueFormatter.FormatScaled(BigInteger.Parse(coefficient), exponent));
}

namespace Fbtab;

/// <summary>
/// Writes tab-separated text where one line is always one row: inside a value backslash, tab,
/// line feed and carriage return are written as \\ \t \n \r, NULL is \N and an empty string is
/// empty. This is the text format of PostgreSQL COPY.
/// </summary>
public sealed class TsvWriter(TextWriter writer)
{
    public const string Null = "\\N";

    private static readonly System.Buffers.SearchValues<char> Special = System.Buffers.SearchValues.Create("\\\t\n\r");

    private bool _firstInRow = true;

    public void Field(string? value)
    {
        if (!_firstInRow)
        {
            writer.Write('\t');
        }

        _firstInRow = false;
        if (value is null)
        {
            writer.Write(Null);
            return;
        }

        var span = value.AsSpan();
        while (true)
        {
            var i = span.IndexOfAny(Special);
            if (i < 0)
            {
                writer.Write(span);
                return;
            }

            writer.Write(span[..i]);
            writer.Write(span[i] switch { '\\' => "\\\\", '\t' => "\\t", '\n' => "\\n", _ => "\\r" });
            span = span[(i + 1)..];
        }
    }

    public void EndRow()
    {
        writer.Write('\n');
        _firstInRow = true;
    }

    /// <summary>Reverses the escaping of one field; null for \N. For tests and for readers written in .NET.</summary>
    public static string? Unescape(string field)
    {
        if (field == Null)
        {
            return null;
        }

        if (!field.Contains('\\'))
        {
            return field;
        }

        var sb = new System.Text.StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            var c = field[i];
            if (c == '\\' && i + 1 < field.Length)
            {
                i++;
                sb.Append(field[i] switch { 't' => '\t', 'n' => '\n', 'r' => '\r', _ => field[i] });
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace Fbtab;

/// <summary>Transaction isolation of one sql file. Both are read only.</summary>
public enum Isolation
{
    ReadCommitted,
    Snapshot,
}

/// <summary>What fbtab does with a statement of the script.</summary>
public enum StatementKind
{
    /// <summary>Returns rows: SELECT, WITH, EXECUTE BLOCK, EXECUTE PROCEDURE.</summary>
    Query,

    /// <summary>An isql display setting (SET LIST, SET COUNT...): ignored, so scripts written for isql run as they are.</summary>
    IsqlSetting,

    /// <summary>Anything else: refused before anything is sent to the server.</summary>
    Forbidden,
}

/// <param name="Text">Statement without its terminator.</param>
/// <param name="Line">1-based line where it starts.</param>
public sealed record SqlStatement(string Text, int Line, StatementKind Kind);

/// <param name="Isolation">From a <c>-- isolation: snapshot</c> header, if the file has one.</param>
public sealed record SqlScript(IReadOnlyList<SqlStatement> Statements, Isolation? Isolation);

/// <summary>
/// Splits an isql-style script into statements. Understands SET TERM, line and block comments,
/// string literals (including q'{...}') and quoted identifiers, so a terminator inside any of them
/// does not end a statement.
/// </summary>
public static partial class SqlScriptParser
{
    public static SqlScript Parse(string script)
    {
        if (script.Length > 0 && script[0] == '﻿')
        {
            script = script[1..];
        }

        var result = new List<SqlStatement>();
        var terminator = ";";
        var current = new StringBuilder();
        var line = 1;
        var startLine = 1;
        var i = 0;

        void Flush()
        {
            var text = current.ToString().Trim();
            current.Clear();

            // Comments before the statement are dropped, so the line number and error messages point at the statement itself.
            var skip = LeadingCommentsLength(text);
            var statementLine = startLine;
            for (var k = 0; k < skip; k++)
            {
                if (text[k] == '\n')
                {
                    statementLine++;
                }
            }

            text = text[skip..];
            if (text.Length == 0)
            {
                return;
            }

            var words = FirstWords(text, 2);
            var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 2 && words[0] == "SET" && words[1] == "TERM" && tokens.Length == 3)
            {
                terminator = tokens[2];
                return;
            }

            result.Add(new SqlStatement(text, statementLine, Classify(text)));
        }

        while (i < script.Length)
        {
            var c = script[i];

            if (IsBlank(current))
            {
                if (char.IsWhiteSpace(c))
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    i++;
                    continue;
                }

                current.Clear();
                startLine = line;
            }

            int end;
            if (c == '-' && Peek(script, i + 1) == '-')
            {
                end = script.IndexOf('\n', i);
                end = end < 0 ? script.Length : end;
            }
            else if (c == '/' && Peek(script, i + 1) == '*')
            {
                end = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? script.Length : end + 2;
            }
            else if (c is 'q' or 'Q' && Peek(script, i + 1) == '\'' && !IsIdentifierChar(Peek(script, i - 1)))
            {
                end = SkipAlternativeQuote(script, i);
            }
            else if (c is '\'' or '"')
            {
                end = SkipQuoted(script, i, c);
            }
            else if (string.CompareOrdinal(script, i, terminator, 0, terminator.Length) == 0)
            {
                Flush();
                i += terminator.Length;
                continue;
            }
            else
            {
                end = i + 1;
            }

            for (var k = i; k < end; k++)
            {
                if (script[k] == '\n')
                {
                    line++;
                }
            }

            current.Append(script, i, end - i);
            i = end;
        }

        Flush();
        return new SqlScript(result, ReadIsolationHeader(script));
    }

    /// <summary>Decides by the first words; comments before them are skipped.</summary>
    public static StatementKind Classify(string statement)
    {
        var w = FirstWords(statement, 2);
        if (w.Length == 0)
        {
            return StatementKind.Forbidden;
        }

        switch (w[0])
        {
            case "SELECT":
            case "WITH":
                return StatementKind.Query;
            case "EXECUTE":
                return w.Length > 1 && w[1] is "BLOCK" or "PROCEDURE" ? StatementKind.Query : StatementKind.Forbidden;
            case "SET":
                return w.Length > 1 && IsqlSettings.Contains(w[1]) ? StatementKind.IsqlSetting : StatementKind.Forbidden;
            default:
                return StatementKind.Forbidden;
        }
    }

    // isql display settings. SET TRANSACTION, SET GENERATOR, SET STATISTICS and the like are not here on purpose.
    private static readonly HashSet<string> IsqlSettings = new(StringComparer.Ordinal)
    {
        "LIST", "COUNT", "HEADING", "STATS", "STAT", "PLAN", "PLANONLY", "EXPLAIN", "WIDTH", "BAIL", "ECHO",
        "NAMES", "SQLDA_DISPLAY", "BLOB", "BLOBDISPLAY", "TIME", "WNG", "WARNINGS", "ROWCOUNT", "MAXROWS",
        "PER_TABLE_STATS", "PER_TAB", "KEEP_TRAN_PARAMS", "KEEP_TRAN", "EXEC_PATH_DISPLAY", "LOCAL_TIMEOUT",
    };

    private static int LeadingCommentsLength(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (text[i] == '-' && Peek(text, i + 1) == '-')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
            }
            else if (text[i] == '/' && Peek(text, i + 1) == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else
            {
                break;
            }
        }

        return i;
    }

    /// <summary>Up to <paramref name="count"/> leading words in upper case, comments skipped.</summary>
    internal static string[] FirstWords(string text, int count)
    {
        var words = new List<string>();
        var i = 0;
        while (i < text.Length && words.Count < count)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && Peek(text, i + 1) == '-')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
            }
            else if (c == '/' && Peek(text, i + 1) == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else if (IsIdentifierChar(c))
            {
                var start = i;
                while (i < text.Length && IsIdentifierChar(text[i]))
                {
                    i++;
                }

                words.Add(text[start..i].ToUpperInvariant());
            }
            else
            {
                // A statement that starts with anything but a word, e.g. "(select ...)": keep what was found.
                if (words.Count == 0 && c == '(')
                {
                    i++;
                    continue;
                }

                break;
            }
        }

        return words.ToArray();
    }

    private static Isolation? ReadIsolationHeader(string script)
    {
        foreach (var raw in script.Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0)
            {
                continue;
            }

            if (!l.StartsWith("--", StringComparison.Ordinal))
            {
                break;
            }

            var m = IsolationHeader().Match(l);
            if (m.Success)
            {
                return m.Groups[1].Value.StartsWith("s", StringComparison.OrdinalIgnoreCase)
                    ? Isolation.Snapshot
                    : Isolation.ReadCommitted;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^--\s*isolation\s*:\s*(snapshot|read[- _]?committed)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsolationHeader();

    private static int SkipQuoted(string s, int start, char quote)
    {
        var i = start + 1;
        while (i < s.Length)
        {
            if (s[i] == quote)
            {
                if (Peek(s, i + 1) == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return s.Length;
    }

    // q'{text}' : the delimiter after q' closes with its pair ( ) { } [ ] < > or with itself.
    private static int SkipAlternativeQuote(string s, int start)
    {
        if (start + 2 >= s.Length)
        {
            return s.Length;
        }

        var open = s[start + 2];
        var close = open switch { '(' => ')', '{' => '}', '[' => ']', '<' => '>', _ => open };
        var end = s.IndexOf($"{close}'", start + 3, StringComparison.Ordinal);
        return end < 0 ? s.Length : end + 2;
    }

    private static char Peek(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    private static bool IsBlank(StringBuilder sb)
    {
        for (var i = 0; i < sb.Length; i++)
        {
            if (!char.IsWhiteSpace(sb[i]))
            {
                return false;
            }
        }

        return true;
    }
}

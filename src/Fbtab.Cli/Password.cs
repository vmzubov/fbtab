using System.Text;

namespace Fbtab.Cli;

/// <summary>Database password: from the environment or typed in. Never from the command line, never stored.</summary>
internal static class Password
{
    public static string Read(string user)
    {
        foreach (var name in new[] { "FBTAB_PASSWORD", "ISC_PASSWORD" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        if (Console.IsInputRedirected)
        {
            throw new UsageException("No password: set FBTAB_PASSWORD or ISC_PASSWORD, or run in a console to type it in.");
        }

        Console.Error.Write($"Password for {user}: ");
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return sb.ToString();
    }
}

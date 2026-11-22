using System.Runtime.InteropServices;
using FirebirdSql.Data.FirebirdClient;

namespace Fbtab;

/// <summary>
/// Opens a connection. Through fbclient the driver passes the user name but not the password, so
/// fbclient would log in without one; it also reads ISC_PASSWORD, which is set here only while
/// the connection is being opened and restored afterwards.
/// </summary>
public static class Connector
{
    private const string PasswordVariable = "ISC_PASSWORD";
    private static readonly Lock Gate = new();

    /// <summary>
    /// Makes the single-byte code pages (WIN1251 and the like) known to .NET. Needed only when the
    /// connection character set is one of them; with UTF8 the server does the conversion.
    /// </summary>
    public static void RegisterCodePages() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    public static FbConnection Open(ConnectionSettings settings)
    {
        RegisterCodePages();
        var connection = new FbConnection(settings.BuildConnectionString());
        try
        {
            if (!settings.Native)
            {
                connection.Open();
            }
            else
            {
                lock (Gate)
                {
                    var before = Environment.GetEnvironmentVariable(PasswordVariable);
                    SetVariable(settings.Password);
                    try
                    {
                        connection.Open();
                    }
                    finally
                    {
                        SetVariable(before);
                    }
                }
            }

            AskForNativeTypes(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // Firebird 4+. A server can be set (DataTypeCompatibility) to hand the newer types to clients as older
    // ones: BOOLEAN as CHAR(5), INT128 as BIGINT, DECFLOAT as DOUBLE PRECISION, values with a time zone
    // without it. That is a default for clients that do not know the types; this one does, and the output
    // must not depend on a server setting. Older servers have neither the statement nor the setting.
    private static void AskForNativeTypes(FbConnection connection)
    {
        SetBinds(connection, ["boolean to native", "int128 to native", "decfloat to native", "time zone to native"]);
    }

    // Every statement costs several round trips to the server, which is seconds per file over a distant
    // link, so the binds go as one block. Should a server refuse the block, they go one by one, and a server
    // that does not know the statement (Firebird 3) is left as it is after the first refusal.
    internal static void SetBinds(FbConnection connection, IReadOnlyList<string> binds)
    {
        try
        {
            var block = "execute block as begin " + string.Concat(binds.Select(b => $"set bind of {b}; ")) + "end";
            using var command = new FbCommand(block, connection);
            command.ExecuteNonQuery();
            return;
        }
        catch (FbException)
        {
        }

        foreach (var bind in binds)
        {
            try
            {
                using var command = new FbCommand($"set bind of {bind}", connection);
                command.ExecuteNonQuery();
            }
            catch (FbException)
            {
                return;
            }
        }
    }

    // .NET keeps its own copy of the environment on Unix, so fbclient would not see a change made
    // through Environment there.
    private static void SetVariable(string? value)
    {
        Environment.SetEnvironmentVariable(PasswordVariable, value);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (string.IsNullOrEmpty(value))
        {
            _ = unsetenv(PasswordVariable);
        }
        else
        {
            _ = setenv(PasswordVariable, value, 1);
        }
    }

    [DllImport("libc", CharSet = CharSet.Ansi)]
    private static extern int setenv(string name, string value, int overwrite);

    [DllImport("libc", CharSet = CharSet.Ansi)]
    private static extern int unsetenv(string name);
}

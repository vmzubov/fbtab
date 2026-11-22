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

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
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

using FirebirdSql.Data.FirebirdClient;

namespace Fbtab;

/// <summary>Server and database taken apart from a string as isql takes it.</summary>
/// <param name="Host">Null for a local path or alias.</param>
public sealed record DatabaseAddress(string? Host, int Port, string Database)
{
    public const int DefaultPort = 3050;

    /// <summary>
    /// Understands <c>host:db</c>, <c>host/port:db</c>, <c>[host]/port:db</c>, <c>inet://host:port/db</c>
    /// and a local path or alias. <c>C:\dir\file.fdb</c> is a local path, not a host named C.
    /// </summary>
    public static DatabaseAddress Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            throw new FormatException("Database is empty.");
        }

        foreach (var scheme in new[] { "inet://", "inet4://", "inet6://" })
        {
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                var rest = text[scheme.Length..];
                var slash = rest.IndexOf('/');
                if (slash <= 0 || slash == rest.Length - 1)
                {
                    throw new FormatException($"Cannot read the database string '{text}'.");
                }

                var (host, port) = SplitHostPort(rest[..slash], ':');
                return new DatabaseAddress(host, port, rest[(slash + 1)..]);
            }
        }

        if (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/')
        {
            return new DatabaseAddress(null, DefaultPort, text);
        }

        var searchFrom = text[0] == '[' ? Math.Max(text.IndexOf(']'), 0) : 0;
        var colon = text.IndexOf(':', searchFrom);
        if (colon <= 0 || colon == text.Length - 1)
        {
            return new DatabaseAddress(null, DefaultPort, text);
        }

        var (h, p) = SplitHostPort(text[..colon], '/');
        return new DatabaseAddress(h, p, text[(colon + 1)..]);
    }

    private static (string Host, int Port) SplitHostPort(string text, char separator)
    {
        var port = DefaultPort;
        var searchFrom = text.Length > 0 && text[0] == '[' ? Math.Max(text.IndexOf(']'), 0) : 0;
        var at = text.IndexOf(separator, searchFrom);
        if (at >= 0)
        {
            if (!int.TryParse(text[(at + 1)..], out port) || port is <= 0 or > 65535)
            {
                throw new FormatException($"Port in '{text}' is not a number from 1 to 65535.");
            }

            text = text[..at];
        }

        text = text.Trim('[', ']');
        if (text.Length == 0)
        {
            throw new FormatException("Host is empty.");
        }

        return (text, port);
    }
}

/// <param name="ClientLibrary">Path or name of fbclient; null means the driver's own protocol, without fbclient.</param>
public sealed record ConnectionSettings(
    DatabaseAddress Address,
    string User,
    string Password,
    string Charset = "UTF8",
    string? Role = null,
    string? ClientLibrary = null)
{
    public bool Native => ClientLibrary is not null;

    public string BuildConnectionString()
    {
        var builder = new FbConnectionStringBuilder
        {
            UserID = User,
            Password = Password,
            Charset = Charset,
            Dialect = 3,
            Pooling = false,
        };
        if (!string.IsNullOrWhiteSpace(Role))
        {
            builder.Role = Role;
        }

        if (Native)
        {
            // fbclient takes the server inside the database string. The driver splits every usual form
            // ("host/port:path", "inet://...") back into host and port and hands fbclient a bare path,
            // which then attaches locally. A bracketed host is left alone, and fbclient 3+ accepts it.
            builder.ServerType = FbServerType.Embedded;
            builder.ClientLibrary = ClientLibrary;
            builder.Database = Address.Host is null
                ? Address.Database
                : $"[{Address.Host}]/{Address.Port}:{Address.Database}";
        }
        else
        {
            builder.DataSource = Address.Host ?? "localhost";
            builder.Port = Address.Port;
            builder.Database = Address.Database;
        }

        return builder.ToString();
    }
}

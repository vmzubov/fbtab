namespace Fbtab.Cli;

internal sealed class UsageException(string message) : Exception(message);

/// <summary>Command line of fbtab.</summary>
internal sealed record Options(
    string Database,
    string User,
    string? Role,
    string Charset,
    string? FbClient,
    bool Managed,
    bool Compress,
    bool AllowDml,
    Isolation Isolation,
    string OutDir,
    IReadOnlyList<string> Files)
{
    public const string Help = """
        fbtab - runs SELECT statements from sql files against Firebird and writes tsv.

        Usage:
          fbtab --db <database> --user <user> [options] <file.sql> [<file.sql> ...]

        Options:
          --db <database>        host:alias, host/port:path, inet://host:port/path or a local path
          --user <user>          user name (default: ISC_USER)
          --role <role>          role
          --charset <name>       connection character set (default: UTF8)
          --fbclient <path>      fbclient library to connect through (default: FBTAB_FBCLIENT,
                                 then the one next to fbtab, then the one the system finds)
          --managed              connect without fbclient (Srp and Legacy_Auth only)
          --compress             compress the connection: worth it over a slow link. With --managed
                                 it is enough; through fbclient it is the client's own setting,
                                 "WireCompression = true" in the firebird.conf next to fbclient
          --allow-dml            also run INSERT, UPDATE, DELETE and MERGE. The transaction stays read only,
                                 so the server lets them change global temporary tables and nothing else
          --isolation <mode>     read-committed (default) or snapshot; a file can ask for its own
                                 with a first-lines comment "-- isolation: snapshot"
          --out <dir>            where to write (default: current directory)
          --version, --help

        The password comes from FBTAB_PASSWORD or ISC_PASSWORD, or is typed in. It is never taken
        from the command line and never stored.

        Each file runs on its own connection in a read-only transaction. Results: <file>.tsv and
        <file>.meta.json; the second and later queries of a file give <file>_2.tsv and so on.

        Exit codes: 0 ok, 1 a file failed, 2 no connection or wrong credentials, 3 usage, 4 cancelled.
        """;

    private static readonly HashSet<string> WithValue = ["--db", "--user", "--role", "--charset", "--fbclient", "--isolation", "--out"];

    public static Options Parse(IReadOnlyList<string> args, Func<string, string?> env)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<string>();
        var managed = false;
        var compress = false;
        var allowDml = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--managed")
            {
                managed = true;
            }
            else if (arg == "--compress")
            {
                compress = true;
            }
            else if (arg == "--allow-dml")
            {
                allowDml = true;
            }
            else if (WithValue.Contains(arg))
            {
                if (i + 1 >= args.Count)
                {
                    throw new UsageException($"{arg} needs a value.");
                }

                values[arg] = args[++i];
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException(arg is "--password" or "--pass"
                    ? "The password is not taken from the command line: set FBTAB_PASSWORD or ISC_PASSWORD, or type it in."
                    : $"Unknown option {arg}.");
            }
            else
            {
                files.Add(arg);
            }
        }

        if (!values.TryGetValue("--db", out var db))
        {
            throw new UsageException("--db is required.");
        }

        var user = values.GetValueOrDefault("--user") ?? env("ISC_USER");
        if (string.IsNullOrEmpty(user))
        {
            throw new UsageException("--user is required.");
        }

        if (files.Count == 0)
        {
            throw new UsageException("No sql file given.");
        }

        var fbclient = values.GetValueOrDefault("--fbclient");
        if (managed && fbclient is not null)
        {
            throw new UsageException("--managed and --fbclient cannot be used together.");
        }

        var isolation = Isolation.ReadCommitted;
        if (values.TryGetValue("--isolation", out var mode))
        {
            isolation = mode.ToLowerInvariant() switch
            {
                "read-committed" or "rc" => Isolation.ReadCommitted,
                "snapshot" => Isolation.Snapshot,
                _ => throw new UsageException("--isolation is read-committed or snapshot."),
            };
        }

        return new Options(
            db,
            user,
            values.GetValueOrDefault("--role"),
            values.GetValueOrDefault("--charset") ?? "UTF8",
            fbclient,
            managed,
            compress,
            allowDml,
            isolation,
            values.GetValueOrDefault("--out") ?? ".",
            files);
    }

    /// <summary>The library to connect through; null in managed mode.</summary>
    public string? ResolveClientLibrary(Func<string, string?> env, string baseDirectory)
    {
        if (Managed)
        {
            return null;
        }

        var given = FbClient ?? env("FBTAB_FBCLIENT");
        if (!string.IsNullOrEmpty(given))
        {
            if (!File.Exists(given))
            {
                throw new UsageException($"fbclient not found: {given}");
            }

            return Path.GetFullPath(given);
        }

        string[] names = OperatingSystem.IsWindows()
            ? ["fbclient.dll"]
            : OperatingSystem.IsMacOS() ? ["libfbclient.dylib"] : ["libfbclient.so.2", "libfbclient.so"];
        foreach (var name in names)
        {
            var beside = Path.Combine(baseDirectory, name);
            if (File.Exists(beside))
            {
                return beside;
            }
        }

        return names[0];
    }
}

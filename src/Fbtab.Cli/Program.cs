using System.Globalization;
using System.Runtime.InteropServices;
using Fbtab;
using Fbtab.Cli;

return App.Run(args);

namespace Fbtab.Cli
{
    internal static class App
    {
        public const int ExitOk = 0;
        public const int ExitFailed = 1;
        public const int ExitNoConnection = 2;
        public const int ExitUsage = 3;
        public const int ExitCancelled = 4;

        public static int Run(string[] args)
        {
            if (args.Length == 0 || args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
            {
                Console.Out.WriteLine(Options.Help);
                return args.Length == 0 ? ExitUsage : ExitOk;
            }

            if (args.Contains("--version"))
            {
                Console.Out.WriteLine("fbtab " + AppVersion.Value);
                return ExitOk;
            }

            using var cancel = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                e.Cancel = true;
                cancel.Cancel();
            };
            Console.CancelKeyPress += onCancel;
            try
            {
                var options = Options.Parse(args, Environment.GetEnvironmentVariable);
                foreach (var file in options.Files)
                {
                    if (!File.Exists(file))
                    {
                        throw new UsageException($"File not found: {file}");
                    }
                }

                DatabaseAddress address;
                try
                {
                    address = DatabaseAddress.Parse(options.Database);
                }
                catch (FormatException e)
                {
                    throw new UsageException(e.Message);
                }

                var library = options.ResolveClientLibrary(Environment.GetEnvironmentVariable, AppContext.BaseDirectory);
                if (library is not null)
                {
                    // Checked here and not left to the driver: it would fail again while the process exits.
                    if (!NativeLibrary.TryLoad(library, out _))
                    {
                        throw new UsageException(
                            $"Cannot load fbclient ({library}). Give the path to a 64-bit one with --fbclient, or use --managed to connect without it.");
                    }
                }

                var settings = new ConnectionSettings(
                    address, options.User, Password.Read(options.User), options.Charset, options.Role, library, options.Compress);
                return Export(options, settings, cancel.Token);
            }
            catch (UsageException e)
            {
                Console.Error.WriteLine("fbtab: " + e.Message);
                Console.Error.WriteLine("Try fbtab --help.");
                return ExitUsage;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        private static int Export(Options options, ConnectionSettings settings, CancellationToken cancel)
        {
            var live = !Console.IsErrorRedirected;
            var lastReport = DateTime.UtcNow;
            var exporter = new Exporter(settings, options.OutDir, options.Isolation)
            {
                Progress = (name, rows) =>
                {
                    // In a console the line is redrawn every second; into a file or a pipe one line a minute is enough.
                    if (live)
                    {
                        Console.Error.Write($"\r{name}: {rows.ToString("N0", CultureInfo.InvariantCulture)} rows   ");
                    }
                    else if ((DateTime.UtcNow - lastReport).TotalSeconds >= 60)
                    {
                        lastReport = DateTime.UtcNow;
                        Console.Error.WriteLine($"{name}: {rows} rows");
                    }
                },
            };

            exporter.Warning = text =>
            {
                if (live)
                {
                    Console.Error.Write("\r" + new string(' ', 60) + "\r");
                }

                Console.Error.WriteLine("fbtab: " + text);
            };

            var exit = ExitOk;
            foreach (var file in options.Files)
            {
                var outcome = exporter.ExportFile(file, cancel);
                if (live)
                {
                    Console.Error.Write("\r" + new string(' ', 60) + "\r");
                }

                var seconds = outcome.Seconds.ToString("0.0", CultureInfo.InvariantCulture);
                switch (outcome.Status)
                {
                    case ExportStatus.Ok:
                        Console.Out.WriteLine($"OK {outcome.Name} rows={outcome.Rows} sec={seconds}");
                        break;
                    case ExportStatus.Cancelled:
                        Console.Out.WriteLine($"CANCELLED {outcome.Name} rows={outcome.Rows} sec={seconds}");
                        return ExitCancelled;
                    case ExportStatus.ConnectionFailed:
                        Console.Out.WriteLine($"ERROR {outcome.Name} {outcome.SqlState ?? "-"} {outcome.Message}");
                        return ExitNoConnection;
                    default:
                        Console.Out.WriteLine($"ERROR {outcome.Name} {outcome.SqlState ?? "-"} {outcome.Message}");
                        exit = ExitFailed;
                        break;
                }
            }

            return exit;
        }
    }
}

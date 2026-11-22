using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FirebirdSql.Data.FirebirdClient;

namespace Fbtab;

public enum ExportStatus
{
    Ok,

    /// <summary>The script or one of its statements failed; other files can still run.</summary>
    Failed,

    /// <summary>No connection or wrong credentials: nothing else will work either.</summary>
    ConnectionFailed,

    Cancelled,
}

/// <summary>What happened to one sql file.</summary>
/// <param name="Name">File name without extension.</param>
/// <param name="Rows">Rows written, all result sets of the file together.</param>
/// <param name="Files">Finished tsv files, full paths.</param>
public sealed record ExportOutcome(
    string Name,
    ExportStatus Status,
    long Rows,
    double Seconds,
    IReadOnlyList<string> Files,
    string? SqlState = null,
    string? Message = null);

public sealed record ColumnInfo(string Name, string Type, int? Length, int? Scale, bool? Nullable);

/// <summary>Runs sql files, each on its own connection in its own read-only transaction, and writes tsv.</summary>
/// <param name="allowDml">Run INSERT, UPDATE, DELETE and MERGE too. The transaction stays read only, so the server
/// lets them change global temporary tables only: a list loaded into one can then be joined in a query.</param>
public sealed class Exporter(
    ConnectionSettings settings,
    string outDir,
    Isolation defaultIsolation = Isolation.ReadCommitted,
    bool allowDml = false)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Rows asked from the server at once by the driver's own protocol (its default is 200). Over a slow link
    // every request costs a round trip; the server still cuts a batch to what fits its buffer.
    private const int FetchRows = 2000;

    private bool _compressionWarned;

    /// <summary>Called from the reading thread about once a second: file name and rows written so far.</summary>
    public Action<string, long>? Progress { get; set; }

    /// <summary>Something the user should know that is not an error.</summary>
    public Action<string>? Warning { get; set; }

    public ExportOutcome ExportFile(string sqlPath, CancellationToken cancel = default)
    {
        var name = Path.GetFileNameWithoutExtension(sqlPath);
        var watch = Stopwatch.StartNew();
        var files = new List<string>();
        long rows = 0;

        ExportOutcome Result(ExportStatus status, string? sqlState = null, string? message = null) =>
            new(name, status, rows, watch.Elapsed.TotalSeconds, files, sqlState, OneLine(message));

        SqlScript script;
        try
        {
            script = SqlScriptParser.Parse(File.ReadAllText(sqlPath, Encoding.UTF8));
        }
        catch (IOException e)
        {
            return Result(ExportStatus.Failed, message: e.Message);
        }

        var forbidden = script.Statements.FirstOrDefault(s => s.Kind == StatementKind.Forbidden || (s.Kind == StatementKind.Dml && !allowDml));
        if (forbidden is not null)
        {
            return Result(ExportStatus.Failed, message: forbidden.Kind == StatementKind.Dml
                ? $"line {forbidden.Line}: INSERT, UPDATE, DELETE and MERGE run only with --allow-dml, found: {Head(forbidden.Text)}"
                : $"line {forbidden.Line}: only SELECT, WITH, EXECUTE BLOCK and EXECUTE PROCEDURE are allowed"
                  + (allowDml ? " (and INSERT, UPDATE, DELETE, MERGE)" : "") + $", found: {Head(forbidden.Text)}");
        }

        // Statements run in their order; queries are described before anything runs, DML is not.
        var statements = script.Statements.Where(s => s.Kind is StatementKind.Query or StatementKind.Dml).ToList();
        if (statements.Count == 0)
        {
            return Result(ExportStatus.Failed, message: "no query in the file");
        }

        Directory.CreateDirectory(outDir);
        var isolation = script.Isolation ?? defaultIsolation;

        FbConnection connection;
        try
        {
            connection = Connector.Open(settings);
        }
        catch (FbException e)
        {
            return Result(ExportStatus.ConnectionFailed, e.SQLSTATE, e.Message);
        }
        catch (Exception e) when (!IsFatal(e))
        {
            return Result(ExportStatus.ConnectionFailed, message: Unexpected(e));
        }

        try
        {
            FbCommand? running = null;
            using var registration = cancel.Register(() =>
            {
                try
                {
                    running?.Cancel();
                }
                catch (Exception)
                {
                    // The statement may have just finished; the loop sees the token anyway.
                }
            });

            try
            {
                // Every query is prepared before the first one runs, so a mistake in the last query of a
                // file shows up at once and not after an hour of the first. DML is not: a list loaded into a
                // temporary table can be thousands of inserts, and each prepare is a round trip to the server.
                var described = new List<IReadOnlyList<ColumnInfo>?>();
                using (var describing = connection.BeginTransaction(TransactionOptions(isolation)))
                {
                    foreach (var query in statements)
                    {
                        if (query.Kind == StatementKind.Dml)
                        {
                            described.Add(null);
                            continue;
                        }

                        try
                        {
                            described.Add(Describe(connection, describing, query.Text));
                        }
                        catch (FbException e)
                        {
                            return Result(
                                IsConnectionLost(e) ? ExportStatus.ConnectionFailed : ExportStatus.Failed,
                                e.SQLSTATE,
                                $"line {query.Line}: {e.Message}");
                        }
                    }

                    describing.Rollback();
                }

                WideTypesAsText(connection);
                using var transaction = connection.BeginTransaction(TransactionOptions(isolation));
                var server = ReadServerInfo(connection, transaction);
                if (settings.Compress && server.Compressed == false && !_compressionWarned)
                {
                    _compressionWarned = true;
                    Warning?.Invoke(settings.Native
                        ? "the connection is not compressed: through fbclient compression is switched on by the line \"WireCompression = true\" in the firebird.conf next to fbclient"
                        : "the connection is not compressed: the server did not agree to it");
                }

                // Files are numbered by the statements that return rows: <name>.tsv, <name>_2.tsv, ...
                var outputs = 0;
                for (var i = 0; i < statements.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var statement = statements[i];
                    using var command = new FbCommand(statement.Text, connection, transaction) { FetchSize = FetchRows };
                    running = command;
                    try
                    {
                        if (described[i] is not { Count: > 0 } columns)
                        {
                            command.ExecuteNonQuery();
                            continue;
                        }

                        outputs++;
                        var baseName = outputs == 1 ? name : $"{name}_{outputs}";
                        var written = ExportQuery(command, statement, columns, baseName, isolation, server, cancel, r => Progress?.Invoke(name, rows + r));
                        if (written is { } w)
                        {
                            rows += w.Rows;
                            files.Add(w.Path);
                        }
                    }
                    catch (FbException e) when (!cancel.IsCancellationRequested)
                    {
                        return Result(
                            IsConnectionLost(e) ? ExportStatus.ConnectionFailed : ExportStatus.Failed,
                            e.SQLSTATE,
                            $"line {statement.Line}: {e.Message}");
                    }
                    finally
                    {
                        running = null;
                    }
                }

                transaction.Rollback();
                return Result(ExportStatus.Ok);
            }
            catch (Exception e) when (cancel.IsCancellationRequested && !IsFatal(e))
            {
                return Result(ExportStatus.Cancelled, message: "cancelled");
            }
            catch (FbException e)
            {
                return Result(IsConnectionLost(e) ? ExportStatus.ConnectionFailed : ExportStatus.Failed, e.SQLSTATE, e.Message);
            }
            catch (ValueException e)
            {
                // The file stays as .part; the run goes on.
                return Result(ExportStatus.Failed, message: e.Message);
            }
            catch (Exception e) when (!IsFatal(e))
            {
                // Anything else the driver or the disk throws, a damaged answer from the server included: it
                // fails this file, not the run.
                return Result(ExportStatus.Failed, message: Unexpected(e));
            }
        }
        finally
        {
            try
            {
                connection.Dispose();
            }
            catch (Exception e) when (!IsFatal(e))
            {
                // A connection left in a bad state may fail to close; the outcome is already decided.
            }
        }
    }

    private static bool IsFatal(Exception e) => e is OutOfMemoryException or AccessViolationException;

    private static string Unexpected(Exception e) => $"{e.GetType().Name}: {e.Message}";

    private sealed class ValueException(string message, Exception inner) : Exception(message, inner);

    private (string Path, long Rows)? ExportQuery(
        FbCommand command,
        SqlStatement statement,
        IReadOnlyList<ColumnInfo> columns,
        string baseName,
        Isolation isolation,
        ServerInfo server,
        CancellationToken cancel,
        Action<long> progress)
    {
        var tsvPath = Path.Combine(outDir, baseName + ".tsv");
        var partPath = tsvPath + ".part";
        var metaPath = Path.Combine(outDir, baseName + ".meta.json");
        File.Delete(tsvPath);
        File.Delete(metaPath);

        var started = DateTime.UtcNow;
        long rows = 0;
        using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
        {
            if (reader.FieldCount == 0)
            {
                return null;
            }

            if (reader.FieldCount != columns.Count)
            {
                throw new InvalidOperationException("The query returned other columns than it was described with.");
            }

            var types = columns.Select(c => c.Type).ToArray();
            var scales = columns.Select(c => c.Scale).ToArray();

            // Through fbclient the driver reads a value of a zero-length type ('' as a literal is CHAR(0)) as NULL.
            // Where the column cannot be NULL it is the empty string; a nullable CHAR(0) expression stays ambiguous.
            var alwaysEmpty = columns.Select(c => c is { Type: "CHAR" or "VARCHAR", Length: 0, Nullable: false }).ToArray();

            using var stream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            using var text = new StreamWriter(stream, Utf8NoBom, 1 << 16);
            var tsv = new TsvWriter(text);
            foreach (var column in columns)
            {
                tsv.Field(column.Name);
            }

            tsv.EndRow();

            var nextReport = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            while (reader.Read())
            {
                var i = 0;
                try
                {
                    for (; i < types.Length; i++)
                    {
                        var value = reader.GetValue(i);
                        tsv.Field(value is DBNull && alwaysEmpty[i] ? "" : ValueFormatter.Format(value, types[i], scales[i]));
                    }
                }
                catch (Exception e) when (e is OverflowException or ArgumentException or InvalidCastException or FormatException or InvalidOperationException)
                {
                    // The driver could not turn a value into a .NET one.
                    throw new ValueException($"cannot read a value of column {columns[i].Name}: {e.Message}", e);
                }

                tsv.EndRow();
                rows++;
                if ((rows & 0xFF) == 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (Stopwatch.GetTimestamp() >= nextReport)
                    {
                        progress(rows);
                        nextReport = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                    }
                }
            }
        }

        cancel.ThrowIfCancellationRequested();
        File.Move(partPath, tsvPath, overwrite: true);
        WriteMeta(metaPath, baseName, statement, columns, rows, started, DateTime.UtcNow, isolation, server);
        return (tsvPath, rows);
    }

    // The columns as the server describes them, without running the query.
    private static IReadOnlyList<ColumnInfo> Describe(FbConnection connection, FbTransaction transaction, string sql)
    {
        using var command = new FbCommand(sql, connection, transaction);
        using var reader = command.ExecuteReader(CommandBehavior.SchemaOnly);
        DataTable? schema = null;
        try
        {
            schema = reader.GetSchemaTable();
        }
        catch (FbException)
        {
            // Names and types are enough to write the file; length, scale and nullability are extras.
        }

        var columns = new List<ColumnInfo>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var type = reader.GetDataTypeName(i);
            if (type == "BLOB SUB_TYPE 1")
            {
                type = "BLOB SUB_TYPE TEXT";
            }

            int? length = null;
            int? scale = null;
            bool? nullable = null;
            if (schema is not null && i < schema.Rows.Count)
            {
                var row = schema.Rows[i];
                if (type is "CHAR" or "VARCHAR" && row["ColumnSize"] is int size)
                {
                    length = size;
                }

                if (type is "NUMERIC" or "DECIMAL" && row["NumericScale"] is not DBNull)
                {
                    scale = Convert.ToInt32(row["NumericScale"]);
                }

                if (row["AllowDBNull"] is bool allow)
                {
                    nullable = allow;
                }
            }

            columns.Add(new ColumnInfo(reader.GetName(i), type, length, scale, nullable));
        }

        return columns;
    }

    // Firebird 4+, after the columns have been described with their real types. The server then sends as
    // text the values the driver cannot read in full: TIME / TIMESTAMP WITH TIME ZONE ("2026-11-22
    // 10:00:00.1234 Europe/Budapest" or "... +03:00"; the driver fails on zones given as an offset) and
    // INT128 with the NUMERIC / DECIMAL of more than 18 digits built on it (the driver reads those into a
    // .NET decimal, which holds 28 digits of the 38). The text is exact and keeps the scale and the zone.
    // Older servers have neither the statement nor the types.
    private static void WideTypesAsText(FbConnection connection)
    {
        Connector.SetBinds(connection, ["time zone to varchar", "int128 to varchar", "numeric(38) to varchar"]);
    }

    private sealed record ServerInfo(string? Time, string? EngineVersion, string? User, bool? Compressed);

    private const string ServerInfoSql =
        "select localtimestamp, rdb$get_context('SYSTEM', 'ENGINE_VERSION'), current_user";

    // WIRE_COMPRESSED is there from Firebird 3.0.4; an older server refuses the name, and is asked without it.
    private static ServerInfo ReadServerInfo(FbConnection connection, FbTransaction transaction)
    {
        try
        {
            return ReadServerInfo(connection, transaction, ServerInfoSql + ", rdb$get_context('SYSTEM', 'WIRE_COMPRESSED') from rdb$database");
        }
        catch (FbException)
        {
            return ReadServerInfo(connection, transaction, ServerInfoSql + " from rdb$database");
        }
    }

    private static ServerInfo ReadServerInfo(FbConnection connection, FbTransaction transaction, string sql)
    {
        using var command = new FbCommand(sql, connection, transaction);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new ServerInfo(null, null, null, null);
        }

        bool? compressed = reader.FieldCount > 3 && reader.GetValue(3) is string text
            ? text.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase)
            : null;
        return new ServerInfo(
            ValueFormatter.Format(reader.GetValue(0), "TIMESTAMP", null),
            ValueFormatter.Format(reader.GetValue(1), "VARCHAR", null),
            ValueFormatter.Format(reader.GetValue(2), "VARCHAR", null),
            compressed);
    }

    private static void WriteMeta(
        string path,
        string name,
        SqlStatement statement,
        IReadOnlyList<ColumnInfo> columns,
        long rows,
        DateTime started,
        DateTime finished,
        Isolation isolation,
        ServerInfo server)
    {
        using var stream = File.Create(path);
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            json.WriteStartObject();
            json.WriteString("fbtab", AppVersion.Value);
            json.WriteString("file", name + ".tsv");
            json.WriteStartObject("format");
            json.WriteString("encoding", "UTF-8");
            json.WriteString("separator", "\t");
            json.WriteString("null", TsvWriter.Null);
            json.WriteString("escapes", "\\\\ \\t \\n \\r");
            json.WriteBoolean("header", true);
            json.WriteEndObject();
            json.WriteNumber("rows", rows);
            json.WriteString("started", started.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            json.WriteString("finished", finished.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            json.WriteStartObject("server");
            json.WriteString("time", server.Time);
            json.WriteString("engine", server.EngineVersion);
            json.WriteString("user", server.User);
            if (server.Compressed is { } compressed)
            {
                json.WriteBoolean("compressed", compressed);
            }

            json.WriteEndObject();
            json.WriteString("isolation", isolation == Isolation.Snapshot ? "snapshot" : "read-committed");
            json.WriteNumber("line", statement.Line);
            json.WriteString("sql", statement.Text);
            json.WriteStartArray("columns");
            foreach (var column in columns)
            {
                json.WriteStartObject();
                json.WriteString("name", column.Name);
                json.WriteString("type", column.Type);
                if (column.Length is { } length)
                {
                    json.WriteNumber("length", length);
                }

                if (column.Scale is { } scale)
                {
                    json.WriteNumber("scale", scale);
                }

                if (column.Nullable is { } nullable)
                {
                    json.WriteBoolean("nullable", nullable);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
    }

    private static FbTransactionOptions TransactionOptions(Isolation isolation) => new()
    {
        TransactionBehavior = isolation == Isolation.Snapshot
            ? FbTransactionBehavior.Read | FbTransactionBehavior.Concurrency | FbTransactionBehavior.NoWait
            : FbTransactionBehavior.Read | FbTransactionBehavior.ReadCommitted | FbTransactionBehavior.RecVersion | FbTransactionBehavior.NoWait,
    };

    /// <summary>SQLSTATE class 08: the connection itself is gone, as opposed to a failed statement.</summary>
    private static bool IsConnectionLost(FbException e) =>
        e.SQLSTATE is { Length: 5 } state && state.StartsWith("08", StringComparison.Ordinal);

    private static string Head(string text)
    {
        var words = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return words.Length <= 60 ? words : words[..60] + "...";
    }

    private static string? OneLine(string? message) =>
        message is null ? null : string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

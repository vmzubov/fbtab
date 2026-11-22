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
public sealed class Exporter(ConnectionSettings settings, string outDir, Isolation defaultIsolation = Isolation.ReadCommitted)
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Called from the reading thread about once a second: file name and rows written so far.</summary>
    public Action<string, long>? Progress { get; set; }

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

        var forbidden = script.Statements.FirstOrDefault(s => s.Kind == StatementKind.Forbidden);
        if (forbidden is not null)
        {
            return Result(ExportStatus.Failed, message:
                $"line {forbidden.Line}: only SELECT, WITH, EXECUTE BLOCK and EXECUTE PROCEDURE are allowed, found: {Head(forbidden.Text)}");
        }

        var queries = script.Statements.Where(s => s.Kind == StatementKind.Query).ToList();
        if (queries.Count == 0)
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

        using (connection)
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
                // file shows up at once and not after an hour of the first.
                var nativeTypes = new List<string[]>();
                using (var describing = connection.BeginTransaction(TransactionOptions(isolation)))
                {
                    foreach (var query in queries)
                    {
                        try
                        {
                            nativeTypes.Add(Describe(connection, describing, query.Text));
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

                TimeZonesAsText(connection);
                using var transaction = connection.BeginTransaction(TransactionOptions(isolation));
                var server = ReadServerInfo(connection, transaction);
                for (var i = 0; i < queries.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var baseName = i == 0 ? name : $"{name}_{i + 1}";
                    using var command = new FbCommand(queries[i].Text, connection, transaction);
                    running = command;
                    try
                    {
                        var written = ExportQuery(command, queries[i], nativeTypes[i], baseName, isolation, server, cancel, r => Progress?.Invoke(name, rows + r));
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
                            $"line {queries[i].Line}: {e.Message}");
                    }
                    finally
                    {
                        running = null;
                    }
                }

                transaction.Rollback();
                return Result(ExportStatus.Ok);
            }
            catch (Exception e) when (cancel.IsCancellationRequested && e is FbException or OperationCanceledException)
            {
                return Result(ExportStatus.Cancelled, message: "cancelled");
            }
            catch (FbException e)
            {
                return Result(IsConnectionLost(e) ? ExportStatus.ConnectionFailed : ExportStatus.Failed, e.SQLSTATE, e.Message);
            }
        }
    }

    private (string Path, long Rows)? ExportQuery(
        FbCommand command,
        SqlStatement statement,
        string[] nativeTypes,
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
        IReadOnlyList<ColumnInfo> columns;
        using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
        {
            if (reader.FieldCount == 0)
            {
                return null;
            }

            columns = ReadColumns(reader, nativeTypes);
            var types = columns.Select(c => c.Type).ToArray();
            var scales = columns.Select(c => c.Scale).ToArray();

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
                for (var i = 0; i < types.Length; i++)
                {
                    tsv.Field(ValueFormatter.Format(reader.GetValue(i), types[i], scales[i]));
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

    private static IReadOnlyList<ColumnInfo> ReadColumns(FbDataReader reader, string[] nativeTypes)
    {
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
            // Values WITH TIME ZONE arrive as text (see TimeZonesAsText); the real type is the one described before that.
            var zoned = i < nativeTypes.Length && nativeTypes[i].Contains("TIME ZONE", StringComparison.Ordinal);
            var type = zoned ? nativeTypes[i] : reader.GetDataTypeName(i);
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

    private static string[] Describe(FbConnection connection, FbTransaction transaction, string sql)
    {
        using var command = new FbCommand(sql, connection, transaction);
        using var reader = command.ExecuteReader(CommandBehavior.SchemaOnly);
        var types = new string[reader.FieldCount];
        for (var i = 0; i < types.Length; i++)
        {
            types[i] = reader.GetDataTypeName(i);
        }

        return types;
    }

    // Firebird 4+. The server then sends TIME / TIMESTAMP WITH TIME ZONE as text, "2026-11-22 10:00:00.1234
    // Europe/Budapest" or "... +03:00": the zone stays as it is stored, no time zone database is needed here,
    // and the driver's own reading of these types, which fails on zones given as an offset, is not used.
    // Older servers have neither the statement nor the types.
    private static void TimeZonesAsText(FbConnection connection)
    {
        try
        {
            using var command = new FbCommand("set bind of time zone to varchar", connection);
            command.ExecuteNonQuery();
        }
        catch (FbException)
        {
        }
    }

    private sealed record ServerInfo(string? Time, string? EngineVersion, string? User);

    private static ServerInfo ReadServerInfo(FbConnection connection, FbTransaction transaction)
    {
        using var command = new FbCommand(
            "select localtimestamp, rdb$get_context('SYSTEM', 'ENGINE_VERSION'), current_user from rdb$database",
            connection,
            transaction);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new ServerInfo(null, null, null);
        }

        return new ServerInfo(
            ValueFormatter.Format(reader.GetValue(0), "TIMESTAMP", null),
            ValueFormatter.Format(reader.GetValue(1), "VARCHAR", null),
            ValueFormatter.Format(reader.GetValue(2), "VARCHAR", null));
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

using System.Text;
using System.Text.Json;
using FirebirdSql.Data.FirebirdClient;

namespace Fbtab.Tests;

/// <summary>
/// Needs a Firebird 4+ server: FBTAB_TEST_DB (host/port:path of a database the tests may create and
/// fill), FBTAB_TEST_USER, FBTAB_TEST_PASSWORD, and FBTAB_TEST_FBCLIENT for the tests through fbclient.
/// Without FBTAB_TEST_DB the tests are skipped.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute(bool native = false)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FBTAB_TEST_DB")))
        {
            Skip = "FBTAB_TEST_DB is not set";
        }
        else if (native && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FBTAB_TEST_FBCLIENT")))
        {
            Skip = "FBTAB_TEST_FBCLIENT is not set";
        }
    }
}

public sealed class TestDatabase
{
    public const int ManyRows = 20_000;

    public static readonly string LongText = string.Concat(Enumerable.Repeat("строка с табом\tи переводом\nстроки\\ ", 20_000));

    public TestDatabase()
    {
        var db = Environment.GetEnvironmentVariable("FBTAB_TEST_DB");
        if (string.IsNullOrEmpty(db))
        {
            return;
        }

        Managed = new ConnectionSettings(
            DatabaseAddress.Parse(db),
            Environment.GetEnvironmentVariable("FBTAB_TEST_USER") ?? "SYSDBA",
            Environment.GetEnvironmentVariable("FBTAB_TEST_PASSWORD") ?? "masterkey");
        var library = Environment.GetEnvironmentVariable("FBTAB_TEST_FBCLIENT");
        Native = string.IsNullOrEmpty(library) ? null : Managed with { ClientLibrary = library };

        // WIN1251 on purpose: the connection is UTF8, so texts must arrive converted.
        Connector.RegisterCodePages();
        var create = new FbConnectionStringBuilder(Managed.BuildConnectionString()) { Charset = "WIN1251" };
        FbConnection.CreateDatabase(create.ToString(), pageSize: 8192, forcedWrites: false, overwrite: true);
        using var connection = Connector.Open(Managed);
        Run(connection, """
            create table T (
              ID integer not null primary key,
              SI smallint, BI bigint, I128 int128,
              N numeric(18, 2), N4 numeric(9, 4), DP double precision, FL float, DF decfloat(34),
              D date, TM time, TS timestamp, TSTZ timestamp with time zone, TMTZ time with time zone,
              BO boolean,
              C char(10), VC varchar(100), TXT blob sub_type text,
              BIN blob sub_type binary, OCT varchar(8) character set octets, UUID char(16) character set octets)
            """);
        Run(connection, "create table MANY (ID integer not null primary key, NAME varchar(50))");
        Run(connection, "create table GUARD (ID integer)");
        Run(connection, """
            insert into T values (1, -32768, 9223372036854775807, 170141183460469231731687303715884105727,
              12.5, -0.0001, 0.1, 1.5, 1.5,
              '2026-11-22', '09:10:00.1234', '2026-11-22 09:10:00.1234',
              '2026-11-22 10:00:00.1234 Europe/Budapest', '10:00:00 +03:00',
              true,
              'ab', 'Привет, мир', 'первая' || ascii_char(9) || 'вторая' || ascii_char(13) || ascii_char(10) || 'третья\',
              x'0001FF', x'DEADBEEF', x'000102030405060708090A0B0C0D0E0F')
            """);
        Run(connection, "insert into T (ID, C, VC, TXT, BO) values (2, '', '', '', false)");
        Run(connection, "insert into T (ID) values (3)");
        Run(connection, "insert into T (ID, VC) values (4, '\\N')");
        using (var command = new FbCommand("insert into T (ID, TXT) values (5, @t)", connection))
        {
            command.Parameters.AddWithValue("t", LongText);
            command.ExecuteNonQuery();
        }

        Run(connection, $"""
            execute block as
              declare i integer = 0;
            begin
              while (i < {ManyRows}) do
              begin
                insert into MANY values (:i, 'имя ' || :i);
                i = i + 1;
              end
            end
            """);
    }

    public ConnectionSettings? Managed { get; }

    public ConnectionSettings? Native { get; }

    private static void Run(FbConnection connection, string sql)
    {
        using var command = new FbCommand(sql, connection);
        command.ExecuteNonQuery();
    }
}

public sealed class IntegrationTests : IClassFixture<TestDatabase>, IDisposable
{
    private readonly TestDatabase _db;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fbtab-tests-" + Guid.NewGuid().ToString("N"));

    public IntegrationTests(TestDatabase db)
    {
        _db = db;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Sql(string name, string text)
    {
        var path = Path.Combine(_dir, name + ".sql");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static List<string?[]> ReadTsv(string path) =>
        File.ReadAllText(path, Encoding.UTF8).TrimEnd('\n').Split('\n')
            .Select(line => line.Split('\t').Select(TsvWriter.Unescape).ToArray())
            .ToList();

    private ExportOutcome Export(ConnectionSettings settings, string outName, string sqlPath, CancellationToken cancel = default) =>
        new Exporter(settings, Path.Combine(_dir, outName)).ExportFile(sqlPath, cancel);

    private const string AllTypes = "select * from T order by ID;";

    private void CheckAllTypes(ConnectionSettings settings, string outName)
    {
        var outcome = Export(settings, outName, Sql("types", AllTypes));
        Assert.Equal(ExportStatus.Ok, outcome.Status);
        Assert.Equal(5, outcome.Rows);

        var path = Path.Combine(_dir, outName, "types.tsv");
        Assert.Equal([path], outcome.Files);
        Assert.False(File.Exists(path + ".part"));

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(6, bytes.Count(b => b == (byte)'\n'));

        var rows = ReadTsv(path);
        Assert.Equal(
            ["ID", "SI", "BI", "I128", "N", "N4", "DP", "FL", "DF", "D", "TM", "TS", "TSTZ", "TMTZ", "BO", "C", "VC", "TXT", "BIN", "OCT", "UUID"],
            rows[0]);
        Assert.Equal(
            [
                "1", "-32768", "9223372036854775807", "170141183460469231731687303715884105727",
                "12.50", "-0.0001", "0.1", "1.5", "1.5",
                "2026-11-22", "09:10:00.1234", "2026-11-22 09:10:00.1234",
                "2026-11-22 10:00:00.1234 Europe/Budapest", "10:00:00.0000 +03:00",
                "true",
                "ab", "Привет, мир", "первая\tвторая\r\nтретья\\",
                "0x0001FF", "0xDEADBEEF", "0x000102030405060708090A0B0C0D0E0F",
            ],
            rows[1]);

        // Row 2: empty strings are empty, not NULL; an empty CHAR(10) is ten spaces in Firebird and is trimmed.
        Assert.Equal("", rows[2][15]);
        Assert.Equal("", rows[2][16]);
        Assert.Equal("", rows[2][17]);
        Assert.Equal("false", rows[2][14]);
        Assert.Null(rows[2][1]);

        // Row 3: everything but the key is NULL.
        Assert.All(rows[3].Skip(1), Assert.Null);

        // Row 4: the two characters \N as a value are not NULL.
        Assert.Equal("\\N", rows[4][16]);

        // Row 5: a long text with tabs and line breaks stays one line and comes back whole.
        Assert.Equal(TestDatabase.LongText, rows[5][17]);

        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, outName, "types.meta.json")));
        var root = meta.RootElement;
        Assert.Equal(5, root.GetProperty("rows").GetInt64());
        Assert.Equal("read-committed", root.GetProperty("isolation").GetString());
        var columns = root.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
        Assert.Equal("NUMERIC", columns["N"].GetProperty("type").GetString());
        Assert.Equal(2, columns["N"].GetProperty("scale").GetInt32());
        Assert.Equal("TIMESTAMP WITH TIME ZONE", columns["TSTZ"].GetProperty("type").GetString());
        Assert.Equal("TIME WITH TIME ZONE", columns["TMTZ"].GetProperty("type").GetString());
        Assert.Equal("BLOB SUB_TYPE TEXT", columns["TXT"].GetProperty("type").GetString());
        Assert.Equal("DATE", columns["D"].GetProperty("type").GetString());
        Assert.False(columns["ID"].GetProperty("nullable").GetBoolean());
        Assert.True(columns["VC"].GetProperty("nullable").GetBoolean());
    }

    [IntegrationFact]
    public void AllTypesManaged() => CheckAllTypes(_db.Managed!, "managed");

    [IntegrationFact(native: true)]
    public void AllTypesThroughFbclient() => CheckAllTypes(_db.Native!, "native");

    [IntegrationFact(native: true)]
    public void BothModesWriteTheSameBytes()
    {
        var sql = Sql("same", AllTypes + "\nselect * from MANY order by ID;");
        Assert.Equal(ExportStatus.Ok, Export(_db.Managed!, "m", sql).Status);
        Assert.Equal(ExportStatus.Ok, Export(_db.Native!, "n", sql).Status);
        foreach (var name in new[] { "same.tsv", "same_2.tsv" })
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, "m", name)), File.ReadAllBytes(Path.Combine(_dir, "n", name)));
        }
    }

    [IntegrationFact(native: true)]
    public void FbclientGetsThePasswordWithoutIscPasswordSet()
    {
        Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ISC_PASSWORD")));
        Assert.Equal(ExportStatus.Ok, Export(_db.Native!, "pw", Sql("pw", "select 1 from rdb$database;")).Status);
        Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ISC_PASSWORD")));
    }

    [IntegrationFact]
    public void SeveralQueriesGiveNumberedFiles()
    {
        var sql = Sql("multi", """
            set list on;
            select count(*) CNT from MANY;
            set term ^ ;
            execute block returns (I integer) as
            begin
              I = 1; suspend;
              I = 2; suspend;
            end^
            set term ; ^
            select ID, NAME from MANY where ID < 3 order by ID;
            """);
        var outcome = Export(_db.Managed!, "multi", sql);
        Assert.Equal(ExportStatus.Ok, outcome.Status);
        Assert.Equal(1 + 2 + 3, outcome.Rows);
        Assert.Equal(["multi.tsv", "multi_2.tsv", "multi_3.tsv"], outcome.Files.Select(Path.GetFileName));
        Assert.Equal(TestDatabase.ManyRows.ToString(), ReadTsv(outcome.Files[0])[1][0]);
        Assert.Equal(["I", "1", "2"], ReadTsv(outcome.Files[1]).Select(r => r[0]));
        Assert.Equal("имя 2", ReadTsv(outcome.Files[2])[3][1]);
    }

    [IntegrationFact]
    public void ManyRowsAreAllWritten()
    {
        var outcome = Export(_db.Managed!, "many", Sql("many", "select ID, NAME from MANY order by ID;"));
        Assert.Equal(TestDatabase.ManyRows, outcome.Rows);
        var rows = ReadTsv(outcome.Files[0]);
        Assert.Equal(TestDatabase.ManyRows + 1, rows.Count);
        Assert.Equal($"имя {TestDatabase.ManyRows - 1}", rows[^1][1]);
    }

    [IntegrationFact]
    public void TransactionIsReadOnly()
    {
        var sql = Sql("write", """
            set term ^ ;
            execute block returns (I integer) as
            begin
              insert into GUARD values (1);
              I = 1; suspend;
            end^
            """);
        var outcome = Export(_db.Managed!, "write", sql);
        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Contains("read-only", outcome.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "write", "write.tsv")));

        var check = Export(_db.Managed!, "write", Sql("guard", "select count(*) from GUARD;"));
        Assert.Equal("0", ReadTsv(check.Files[0])[1][0]);
    }

    [IntegrationFact]
    public void ForbiddenStatementStopsTheFileBeforeConnecting()
    {
        var wrong = _db.Managed! with { Password = "wrong password" };
        var outcome = Export(wrong, "forbidden", Sql("forbidden", "select 1 from rdb$database;\ndelete from GUARD;"));
        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.StartsWith("line 2:", outcome.Message);
    }

    [IntegrationFact]
    public void MistakeInLaterQueryIsFoundBeforeTheFirstRuns()
    {
        var outcome = Export(_db.Managed!, "late", Sql("late", "select ID from MANY;\n\nselect NO_SUCH_COLUMN from MANY;"));
        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.Equal("42000", outcome.SqlState);
        Assert.StartsWith("line 3:", outcome.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "late")));
    }

    [IntegrationFact]
    public void RuntimeErrorLeavesPartFileAndNoTsv()
    {
        var outcome = Export(_db.Managed!, "runtime", Sql("runtime", "select ID, 1 / (ID - 10000) X from MANY order by ID;"));
        Assert.Equal(ExportStatus.Failed, outcome.Status);
        Assert.StartsWith("22", outcome.SqlState);
        Assert.False(File.Exists(Path.Combine(_dir, "runtime", "runtime.tsv")));
        Assert.True(File.Exists(Path.Combine(_dir, "runtime", "runtime.tsv.part")));
    }

    [IntegrationFact]
    public void WrongPasswordIsConnectionFailure()
    {
        var wrong = _db.Managed! with { Password = "wrong password" };
        var outcome = Export(wrong, "wrong", Sql("wrong", "select 1 from rdb$database;"));
        Assert.Equal(ExportStatus.ConnectionFailed, outcome.Status);
        Assert.Equal("28000", outcome.SqlState);
    }

    [IntegrationFact(native: true)]
    public void WrongPasswordThroughFbclientIsConnectionFailure()
    {
        var wrong = _db.Native! with { Password = "wrong password" };
        Assert.Equal(ExportStatus.ConnectionFailed, Export(wrong, "wrongn", Sql("wrongn", "select 1 from rdb$database;")).Status);
    }

    [IntegrationFact]
    public void IsolationHeaderGivesSnapshot()
    {
        var outcome = Export(_db.Managed!, "snap", Sql("snap", "-- isolation: snapshot\nselect rdb$get_context('SYSTEM', 'ISOLATION_LEVEL') L from rdb$database;"));
        Assert.Equal(ExportStatus.Ok, outcome.Status);
        Assert.Equal("SNAPSHOT", ReadTsv(outcome.Files[0])[1][0]);
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "snap", "snap.meta.json")));
        Assert.Equal("snapshot", meta.RootElement.GetProperty("isolation").GetString());
    }

    [IntegrationFact]
    public void DefaultIsReadCommittedReadOnly()
    {
        var outcome = Export(_db.Managed!, "rc", Sql("rc", "select rdb$get_context('SYSTEM', 'ISOLATION_LEVEL') L, rdb$get_context('SYSTEM', 'READ_ONLY') RO from rdb$database;"));
        Assert.Equal(["READ COMMITTED", "TRUE"], ReadTsv(outcome.Files[0])[1]);
    }

    private const string Slow = "select count(*) from MANY a, MANY b, MANY c;";

    private void CheckCancel(ConnectionSettings settings, string outName)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var outcome = Export(settings, outName, Sql(outName, Slow), cancel.Token);
        Assert.Equal(ExportStatus.Cancelled, outcome.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"cancel took {watch.Elapsed}");
        Assert.False(File.Exists(Path.Combine(_dir, outName, outName + ".tsv")));
    }

    [IntegrationFact]
    public void CancelStopsRunningQuery() => CheckCancel(_db.Managed!, "cancel");

    [IntegrationFact(native: true)]
    public void CancelStopsRunningQueryThroughFbclient() => CheckCancel(_db.Native!, "canceln");
}

namespace Fbtab.Tests;

public class SqlScriptTests
{
    [Fact]
    public void SplitsOnSemicolonOutsideStringsAndComments()
    {
        var script = SqlScriptParser.Parse("""
            -- first; not a terminator
            select 'a;b', "x;y" from t; /* c; d */
            select q'{it's; here}' from t;
            select 'it''s; ok' from t
            """);
        Assert.Equal(3, script.Statements.Count);
        Assert.All(script.Statements, s => Assert.Equal(StatementKind.Query, s.Kind));
        Assert.EndsWith("from t", script.Statements[0].Text);
        Assert.Equal(2, script.Statements[0].Line);
        Assert.Equal(3, script.Statements[1].Line);
        Assert.StartsWith("select q'", script.Statements[1].Text);
        Assert.Contains("it''s; ok", script.Statements[2].Text);
    }

    [Fact]
    public void SetTermSwitchesTerminator()
    {
        var script = SqlScriptParser.Parse("""
            select 1 from rdb$database;
            set term ^ ;
            execute block returns (i integer) as
            begin
              i = 1; suspend;
            end^
            set term ; ^
            select 2 from rdb$database;
            """);
        Assert.Equal(3, script.Statements.Count);
        Assert.StartsWith("execute block", script.Statements[1].Text);
        Assert.EndsWith("end", script.Statements[1].Text);
        Assert.Equal(3, script.Statements[1].Line);
        Assert.Equal("select 2 from rdb$database", script.Statements[2].Text);
    }

    [Fact]
    public void IsqlSettingsAreSkippedNotRefused()
    {
        var script = SqlScriptParser.Parse("set list on;\nSET COUNT ON;\nset heading off;\nselect 1 from rdb$database;");
        Assert.Equal(
            [StatementKind.IsqlSetting, StatementKind.IsqlSetting, StatementKind.IsqlSetting, StatementKind.Query],
            script.Statements.Select(s => s.Kind));
    }

    [Theory]
    [InlineData("select 1 from rdb$database", StatementKind.Query)]
    [InlineData("  /* c */ -- d\n WITH t as (select 1 x from rdb$database) select * from t", StatementKind.Query)]
    [InlineData("(select 1 from rdb$database) union all (select 2 from rdb$database)", StatementKind.Query)]
    [InlineData("execute procedure p(1)", StatementKind.Query)]
    [InlineData("Execute Block as begin end", StatementKind.Query)]
    [InlineData("execute statement 'x'", StatementKind.Forbidden)]
    [InlineData("update t set a = 1", StatementKind.Dml)]
    [InlineData("update or insert into t values (1) matching (a)", StatementKind.Dml)]
    [InlineData("insert into t values (1)", StatementKind.Dml)]
    [InlineData(" -- c\n Delete from t", StatementKind.Dml)]
    [InlineData("merge into t using s on 1=1 when matched then delete", StatementKind.Dml)]
    [InlineData("create table t (a int)", StatementKind.Forbidden)]
    [InlineData("alter table t add b int", StatementKind.Forbidden)]
    [InlineData("drop table t", StatementKind.Forbidden)]
    [InlineData("commit", StatementKind.Forbidden)]
    [InlineData("rollback", StatementKind.Forbidden)]
    [InlineData("set transaction read write", StatementKind.Forbidden)]
    [InlineData("set generator g to 1", StatementKind.Forbidden)]
    [InlineData("set statistics index i", StatementKind.Forbidden)]
    [InlineData("grant select on t to u", StatementKind.Forbidden)]
    [InlineData("-- select\nupdate t set a = 1", StatementKind.Dml)]
    [InlineData("connect 'x'", StatementKind.Forbidden)]
    public void Classifies(string sql, StatementKind expected) => Assert.Equal(expected, SqlScriptParser.Classify(sql));

    [Theory]
    [InlineData("-- report\n-- isolation: snapshot\nselect 1 from rdb$database;", Isolation.Snapshot)]
    [InlineData("--Isolation:  Read-Committed\nselect 1 from rdb$database;", Isolation.ReadCommitted)]
    [InlineData("select 1 from rdb$database;\n-- isolation: snapshot", null)]
    [InlineData("-- expected: ~5 min\nselect 1 from rdb$database;", null)]
    public void ReadsIsolationHeader(string sql, Isolation? expected) =>
        Assert.Equal(expected, SqlScriptParser.Parse(sql).Isolation);

    [Fact]
    public void BomAndTrailingCommentsDoNotMakeStatements()
    {
        var script = SqlScriptParser.Parse("\uFEFFselect 1 from rdb$database;\n-- the end\n/* really */\n");
        Assert.Single(script.Statements);
    }

    [Fact]
    public void LineAndTextPointAtTheStatementNotAtCommentsBeforeIt()
    {
        var script = SqlScriptParser.Parse("-- what it does\n-- expected: 1 s\n\n/* more */ update T set A = 1;\n");
        var statement = Assert.Single(script.Statements);
        Assert.Equal(4, statement.Line);
        Assert.Equal("update T set A = 1", statement.Text);
        Assert.Equal(StatementKind.Dml, statement.Kind);
    }

    [Fact]
    public void LastStatementMayHaveNoTerminator()
    {
        var script = SqlScriptParser.Parse("select 1 from rdb$database;\nselect 2 from rdb$database");
        Assert.Equal(2, script.Statements.Count);
    }
}

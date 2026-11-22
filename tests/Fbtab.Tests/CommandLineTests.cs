using Fbtab.Cli;

namespace Fbtab.Tests;

public class CommandLineTests
{
    private static string? NoEnv(string name) => null;

    [Theory]
    [InlineData("host:alias", "host", 3050, "alias")]
    [InlineData("host/3055:/data/base.fdb", "host", 3055, "/data/base.fdb")]
    [InlineData("srv.example.org:C:\\data\\base.fdb", "srv.example.org", 3050, "C:\\data\\base.fdb")]
    [InlineData("[::1]/3051:alias", "::1", 3051, "alias")]
    [InlineData("inet://host:3055/alias", "host", 3055, "alias")]
    [InlineData("inet://host//data/base.fdb", "host", 3050, "/data/base.fdb")]
    [InlineData("C:\\data\\base.fdb", null, 3050, "C:\\data\\base.fdb")]
    [InlineData("D:/data/base.fdb", null, 3050, "D:/data/base.fdb")]
    [InlineData("/data/base.fdb", null, 3050, "/data/base.fdb")]
    [InlineData("alias", null, 3050, "alias")]
    public void ParsesDatabaseString(string text, string? host, int port, string database) =>
        Assert.Equal(new DatabaseAddress(host, port, database), DatabaseAddress.Parse(text));

    [Theory]
    [InlineData("host/abc:alias")]
    [InlineData("host/0:alias")]
    [InlineData("")]
    public void RejectsBadDatabaseString(string text) => Assert.Throws<FormatException>(() => DatabaseAddress.Parse(text));

    [Fact]
    public void NativeConnectionStringKeepsServerInBrackets()
    {
        var settings = new ConnectionSettings(DatabaseAddress.Parse("host/3055:alias"), "U", "p", ClientLibrary: "fbclient.dll");
        var text = settings.BuildConnectionString();
        Assert.Contains("[host]/3055:alias", text);
        Assert.Contains("fbclient.dll", text);
    }

    [Fact]
    public void ParsesOptions()
    {
        var o = Options.Parse(["--db", "h:a", "--user", "U", "--isolation", "snapshot", "--out", "res", "a.sql", "b.sql"], NoEnv);
        Assert.Equal("h:a", o.Database);
        Assert.Equal("U", o.User);
        Assert.Equal("UTF8", o.Charset);
        Assert.Equal(Isolation.Snapshot, o.Isolation);
        Assert.Equal("res", o.OutDir);
        Assert.Equal(["a.sql", "b.sql"], o.Files);
        Assert.False(o.Managed);
        Assert.False(o.Compress);
        Assert.False(o.AllowDml);
    }

    [Fact]
    public void ParsesAllowDml() =>
        Assert.True(Options.Parse(["--db", "h:a", "--user", "U", "--allow-dml", "a.sql"], NoEnv).AllowDml);

    [Fact]
    public void CompressGoesIntoTheManagedConnectionString()
    {
        var o = Options.Parse(["--db", "h:a", "--user", "U", "--managed", "--compress", "a.sql"], NoEnv);
        Assert.True(o.Compress);
        var settings = new ConnectionSettings(DatabaseAddress.Parse(o.Database), o.User, "p", Compress: o.Compress);
        Assert.Contains("compression=True", settings.BuildConnectionString(), StringComparison.OrdinalIgnoreCase);
    }

    // The driver sizes the socket buffers by the packet size; its default of 8192 caps the speed over a
    // distant link.
    [Fact]
    public void ManagedConnectionAsksForTheLargestPacket()
    {
        var managed = new ConnectionSettings(DatabaseAddress.Parse("h:a"), "U", "p");
        Assert.Contains("packet size=32767", managed.BuildConnectionString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserComesFromIscUser()
    {
        var o = Options.Parse(["--db", "h:a", "a.sql"], name => name == "ISC_USER" ? "ENVUSER" : null);
        Assert.Equal("ENVUSER", o.User);
    }

    [Theory]
    [InlineData("--user", "U", "a.sql")]
    [InlineData("--db", "h:a", "a.sql")]
    [InlineData("--db", "h:a", "--user", "U")]
    [InlineData("--db", "h:a", "--user", "U", "--password", "x", "a.sql")]
    [InlineData("--db", "h:a", "--user", "U", "--isolation", "dirty", "a.sql")]
    [InlineData("--db", "h:a", "--user", "U", "--managed", "--fbclient", "x", "a.sql")]
    [InlineData("--db", "h:a", "--user", "U", "a.sql", "--out")]
    public void RejectsBadOptions(params string[] args) => Assert.Throws<UsageException>(() => Options.Parse(args, NoEnv));

    [Fact]
    public void ManagedModeHasNoClientLibrary()
    {
        var o = Options.Parse(["--db", "h:a", "--user", "U", "--managed", "a.sql"], NoEnv);
        Assert.Null(o.ResolveClientLibrary(NoEnv, AppContext.BaseDirectory));
    }

    [Fact]
    public void MissingClientLibraryIsUsageError()
    {
        var o = Options.Parse(["--db", "h:a", "--user", "U", "--fbclient", "/no/such/fbclient.so", "a.sql"], NoEnv);
        Assert.Throws<UsageException>(() => o.ResolveClientLibrary(NoEnv, AppContext.BaseDirectory));
    }
}

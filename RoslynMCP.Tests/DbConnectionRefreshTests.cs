using RoslynMCP.Config;
using RoslynMCP.Services.Database;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The registry's in-place refresh (<see cref="DbConnectionRegistry.ApplyResolved"/>) and the
/// file-driven refresh body of <see cref="DbConnectionWatcher"/> — what makes an edited
/// connection string reach the db_* tools without a host restart.
/// </summary>
public class DbConnectionRefreshTests : IDisposable
{
    private readonly string _root;

    public DbConnectionRefreshTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rsense-dbrefresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static SqliteDbProvider Sqlite(string alias, string file = "app.db") =>
        new(alias, $"Data Source={file}");

    // ---- DbConnectionRegistry.ApplyResolved ----

    [Fact]
    public void ApplyResolved_ReplacesAutoEntriesAndKeepsRuntimeOnes()
    {
        var reg = new DbConnectionRegistry([]);
        reg.ApplyResolved([], [Sqlite("Auto_Old")]);
        Assert.True(reg.TryAdd(Sqlite("runtime")));

        var changes = reg.ApplyResolved([], [Sqlite("Auto_New")]);

        Assert.Equal(["added 'Auto_New' (sqlite)", "removed 'Auto_Old'"], changes);
        Assert.Null(reg.Get("Auto_Old"));
        Assert.NotNull(reg.Get("Auto_New"));
        Assert.NotNull(reg.Get("runtime"));
    }

    [Fact]
    public void ApplyResolved_UpdatesEntryWhoseConnectionStringChanged()
    {
        var reg = new DbConnectionRegistry([]);
        reg.ApplyResolved([], [Sqlite("App_Main", "old.db")]);
        var before = reg.Get("App_Main");

        var changes = reg.ApplyResolved([], [Sqlite("App_Main", "new.db")]);

        Assert.Equal(["updated 'App_Main'"], changes);
        Assert.NotSame(before, reg.Get("App_Main"));
    }

    [Fact]
    public void ApplyResolved_KeepsInstanceAndReportsNothingWhenUnchanged()
    {
        var reg = new DbConnectionRegistry([]);
        reg.ApplyResolved([], [Sqlite("App_Main")]);
        var before = reg.Get("App_Main");

        // Discovery produces fresh instances every walk; an identical result must be a no-op.
        var changes = reg.ApplyResolved([], [Sqlite("App_Main")]);

        Assert.Empty(changes);
        Assert.Same(before, reg.Get("App_Main"));
    }

    [Fact]
    public void ApplyResolved_ExplicitWinsOverAutoAndRuntimeOverBoth()
    {
        var reg = new DbConnectionRegistry([]);
        reg.AddOrReplace(Sqlite("both", "runtime.db"));

        reg.ApplyResolved(
            [Sqlite("both", "explicit.db"), Sqlite("shared", "explicit.db")],
            [Sqlite("both", "auto.db"), Sqlite("shared", "auto.db"), Sqlite("only_auto")]);

        Assert.Equal(3, reg.All.Count);
        // Runtime entry survives untouched; explicit shadows auto for "shared".
        var both = Assert.IsType<SqliteDbProvider>(reg.Get("both"));
        Assert.Contains("runtime.db", both.ConnectionString);
        var shared = Assert.IsType<SqliteDbProvider>(reg.Get("shared"));
        Assert.Contains("explicit.db", shared.ConnectionString);
    }

    [Fact]
    public void ApplyResolved_DoesNotResurrectARemovedConnection()
    {
        var reg = new DbConnectionRegistry([]);
        reg.ApplyResolved([], [Sqlite("App_Main")]);
        Assert.True(reg.Remove("App_Main"));

        reg.ApplyResolved([], [Sqlite("App_Main")]);

        Assert.Null(reg.Get("App_Main"));

        // Re-adding it at runtime clears the tombstone.
        Assert.True(reg.TryAdd(Sqlite("App_Main")));
        Assert.NotNull(reg.Get("App_Main"));
    }

    // ---- DbConnectionWatcher (debounced body, no FileSystemWatcher timing) ----

    private string MakeProject(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{name}.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return dir;
    }

    private static EffectiveSettings DefaultSettings() =>
        EffectiveSettings.Resolve([], null, out _);

    [Fact]
    public void Refresh_PicksUpAnEditedConnectionString()
    {
        var dir = MakeProject("Shop");
        var config = Path.Combine(dir, "appsettings.json");
        File.WriteAllText(config, """{ "ConnectionStrings": { "Main": "Data Source=old.db" } }""");

        var reg = new DbConnectionRegistry([]);
        var settings = DefaultSettings();
        Assert.True(settings.ShouldRunAutoDiscovery()); // the mode this feature exists for

        DbConnectionWatcher.Resolve(reg, settings, _root, out _);
        var before = Assert.IsType<SqliteDbProvider>(reg.Get("Shop_Main"));
        Assert.Contains("old.db", before.ConnectionString);

        File.WriteAllText(config, """{ "ConnectionStrings": { "Main": "Data Source=new.db" } }""");
        var changes = DbConnectionWatcher.Resolve(reg, settings, _root, out _);

        Assert.Equal(["updated 'Shop_Main'"], changes);
        var after = Assert.IsType<SqliteDbProvider>(reg.Get("Shop_Main"));
        Assert.Contains("new.db", after.ConnectionString);
    }

    [Fact]
    public void Refresh_RuntimeAddedConnectionSurvivesAndRemovedFileGoesAway()
    {
        var dir = MakeProject("Shop");
        var config = Path.Combine(dir, "appsettings.json");
        File.WriteAllText(config, """{ "ConnectionStrings": { "Main": "Data Source=a.db" } }""");

        var reg = new DbConnectionRegistry([]);
        var settings = DefaultSettings();
        DbConnectionWatcher.Resolve(reg, settings, _root, out _);
        reg.AddOrReplace(Sqlite("scratch"));

        File.Delete(config);
        DbConnectionWatcher.Resolve(reg, settings, _root, out _);

        Assert.Null(reg.Get("Shop_Main"));
        Assert.NotNull(reg.Get("scratch"));
    }

    [Fact]
    public void ConfigFilesInsideBuildOutputAreIgnored()
    {
        // A build copies appsettings.json into bin/ — the watcher must not treat that as an
        // edit, and discovery already skips those directories entirely.
        Assert.True(AutoConnectionStringDiscovery.IsUnderIgnoredDirectory(
            _root, Path.Combine(_root, "Shop", "bin", "Debug", "appsettings.json")));
        Assert.True(AutoConnectionStringDiscovery.IsUnderIgnoredDirectory(
            _root, Path.Combine(_root, "Shop", "obj", "web.config")));
        Assert.True(AutoConnectionStringDiscovery.IsUnderIgnoredDirectory(
            _root, Path.Combine(_root, ".vs", "appsettings.json")));
        Assert.False(AutoConnectionStringDiscovery.IsUnderIgnoredDirectory(
            _root, Path.Combine(_root, "Shop", "appsettings.json")));
    }
}

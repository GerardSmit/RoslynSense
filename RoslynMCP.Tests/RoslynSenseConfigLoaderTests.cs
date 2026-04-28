using RoslynMCP.Config;
using RoslynMCP.Services.Database;
using Xunit;

namespace RoslynMCP.Tests;

public class RoslynSenseConfigLoaderTests : IDisposable
{
    private readonly string _root;

    public RoslynSenseConfigLoaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rsense-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string MakeDir(params string[] segments)
    {
        var dir = Path.Combine(new[] { _root }.Concat(segments).ToArray());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteCfg(string dir, string content) =>
        File.WriteAllText(Path.Combine(dir, RoslynSenseConfigLoader.FileName), content);

    [Fact]
    public void Load_ReturnsNull_WhenNoFileExists()
    {
        var (cfg, path, err) = RoslynSenseConfigLoader.Load(_root);
        Assert.Null(cfg);
        Assert.Null(path);
        Assert.Null(err);
    }

    [Fact]
    public void Load_FindsFileInCwd()
    {
        WriteCfg(_root, "{}");
        var (cfg, path, err) = RoslynSenseConfigLoader.Load(_root);
        Assert.NotNull(cfg);
        Assert.NotNull(path);
        Assert.Null(err);
    }

    [Fact]
    public void Load_WalksUpToParent()
    {
        var child = MakeDir("a", "b", "c");
        WriteCfg(_root, """{"tableFormat":"toon"}""");

        var (cfg, path, err) = RoslynSenseConfigLoader.Load(child);
        Assert.NotNull(cfg);
        Assert.Equal("toon", cfg!.TableFormat);
        Assert.Equal(Path.Combine(_root, "roslynsense.json"), path);
        Assert.Null(err);
    }

    [Fact]
    public void Load_StopsAtFirstFound()
    {
        var child = MakeDir("a", "b");
        WriteCfg(_root, """{"tableFormat":"parent"}""");
        WriteCfg(child, """{"tableFormat":"child"}""");

        var (cfg, _, _) = RoslynSenseConfigLoader.Load(child);
        Assert.Equal("child", cfg!.TableFormat);
    }

    [Fact]
    public void Load_ReturnsError_OnMalformedJson()
    {
        WriteCfg(_root, "{ this is not json");
        var (cfg, path, err) = RoslynSenseConfigLoader.Load(_root);
        Assert.Null(cfg);
        Assert.NotNull(path);
        Assert.NotNull(err);
        Assert.Contains("Invalid JSON", err);
    }

    [Fact]
    public void Load_IgnoresUnknownProperties()
    {
        WriteCfg(_root, """{"futureField":42,"tools":{"webForms":false}}""");
        var (cfg, _, err) = RoslynSenseConfigLoader.Load(_root);
        Assert.Null(err);
        Assert.NotNull(cfg);
        Assert.False(cfg!.Tools.WebForms);
    }

    [Fact]
    public void Load_AllowsTrailingCommas()
    {
        WriteCfg(_root, """{"tools":{"webForms":false,},}""");
        var (cfg, _, err) = RoslynSenseConfigLoader.Load(_root);
        Assert.Null(err);
        Assert.False(cfg!.Tools.WebForms);
    }

    [Fact]
    public void Load_AllowsLineComments()
    {
        WriteCfg(_root, """
        {
            // disable webforms
            "tools": { "webForms": false }
        }
        """);
        var (cfg, _, err) = RoslynSenseConfigLoader.Load(_root);
        Assert.Null(err);
        Assert.False(cfg!.Tools.WebForms);
    }

    [Fact]
    public void Load_DefaultsToolsToTrue()
    {
        WriteCfg(_root, "{}");
        var (cfg, _, _) = RoslynSenseConfigLoader.Load(_root);
        Assert.True(cfg!.Tools.WebForms);
        Assert.True(cfg.Tools.Razor);
        Assert.True(cfg.Tools.Debugger);
        Assert.True(cfg.Tools.Profiling);
        Assert.True(cfg.Tools.Database);
        Assert.Null(cfg.Database.AutoDiscovery);
    }

    [Fact]
    public void Load_ConnectionShorthand_String()
    {
        WriteCfg(_root, """
        {
            "database": {
                "connections": {
                    "myapp": "psql:Host=localhost;Database=myapp;Username=u;Password=p"
                }
            }
        }
        """);
        var (cfg, _, err) = RoslynSenseConfigLoader.Load(_root);
        Assert.Null(err);
        var entry = cfg!.Database.Connections["myapp"];
        Assert.Equal("psql", entry.Provider);
        Assert.Contains("Host=localhost", entry.ConnectionString);
    }

    [Fact]
    public void Load_ConnectionLonghand_Object()
    {
        WriteCfg(_root, """
        {
            "database": {
                "connections": {
                    "reports": {
                        "provider": "sqlserver",
                        "connectionString": "Server=.;Database=Reports;Integrated Security=true"
                    }
                }
            }
        }
        """);
        var (cfg, _, err) = RoslynSenseConfigLoader.Load(_root);
        Assert.Null(err);
        var entry = cfg!.Database.Connections["reports"];
        Assert.Equal("mssql", entry.Provider);
        Assert.Contains("Database=Reports", entry.ConnectionString);
    }

    [Fact]
    public void Load_MixedConnections()
    {
        WriteCfg(_root, """
        {
            "database": {
                "connections": {
                    "a": "sqlite:Data Source=app.db",
                    "b": { "provider": "psql", "connectionString": "Host=h;Database=d;Username=u;Password=p" }
                }
            }
        }
        """);
        var (cfg, _, err) = RoslynSenseConfigLoader.Load(_root);
        Assert.Null(err);
        Assert.Equal("sqlite", cfg!.Database.Connections["a"].Provider);
        Assert.Equal("psql", cfg.Database.Connections["b"].Provider);
    }

    [Fact]
    public void Load_ConnectionWithUnknownProvider_ReturnsError()
    {
        WriteCfg(_root, """
        {
            "database": {
                "connections": { "x": "mongo:foo=bar" }
            }
        }
        """);
        var (cfg, _, err) = RoslynSenseConfigLoader.Load(_root);
        Assert.Null(cfg);
        Assert.NotNull(err);
    }

    // ---------------- EffectiveSettings ----------------

    [Fact]
    public void EffectiveSettings_ToolDefaultsTrue_WhenNoConfigOrCli()
    {
        var settings = EffectiveSettings.Resolve(Array.Empty<string>(), null, out var w);
        Assert.True(settings.WebForms);
        Assert.True(settings.Razor);
        Assert.True(settings.Debugger);
        Assert.True(settings.Profiling);
        Assert.True(settings.Database);
        Assert.Null(settings.AutoDiscoverDb);
        Assert.Null(settings.TableFormat);
        Assert.Empty(settings.ExplicitDbProviders);
        Assert.Empty(w);
    }

    [Fact]
    public void EffectiveSettings_CliFlagOverridesConfigTrue()
    {
        var cfg = new RoslynSenseConfig { Tools = new ToolsConfig { Debugger = true } };
        var settings = EffectiveSettings.Resolve(new[] { "--no-debugger" }, cfg, out _);
        Assert.False(settings.Debugger);
    }

    [Fact]
    public void EffectiveSettings_ConfigFalseHonoredWhenNoCli()
    {
        var cfg = new RoslynSenseConfig { Tools = new ToolsConfig { Profiling = false } };
        var settings = EffectiveSettings.Resolve(Array.Empty<string>(), cfg, out _);
        Assert.False(settings.Profiling);
    }

    [Fact]
    public void EffectiveSettings_AutoDiscoverNull_NoExplicit_RunsAuto()
    {
        var settings = EffectiveSettings.Resolve(Array.Empty<string>(), null, out _);
        Assert.True(settings.ShouldRunAutoDiscovery());
    }

    [Fact]
    public void EffectiveSettings_AutoDiscoverNull_WithExplicit_SkipsAuto()
    {
        var cfg = new RoslynSenseConfig
        {
            Database = new DatabaseConfig
            {
                Connections = { ["a"] = new ConnectionEntry("sqlite", "Data Source=a.db") }
            }
        };
        var settings = EffectiveSettings.Resolve(Array.Empty<string>(), cfg, out _);
        Assert.False(settings.ShouldRunAutoDiscovery());
        Assert.Single(settings.ExplicitDbProviders);
    }

    [Fact]
    public void EffectiveSettings_AutoDiscoverTrue_RunsAutoEvenWithExplicit()
    {
        var cfg = new RoslynSenseConfig
        {
            Database = new DatabaseConfig
            {
                AutoDiscovery = true,
                Connections = { ["a"] = new ConnectionEntry("sqlite", "Data Source=a.db") }
            }
        };
        var settings = EffectiveSettings.Resolve(Array.Empty<string>(), cfg, out _);
        Assert.True(settings.ShouldRunAutoDiscovery());
    }

    [Fact]
    public void EffectiveSettings_CliNoAutoDbOverridesConfigTrue()
    {
        var cfg = new RoslynSenseConfig
        {
            Database = new DatabaseConfig { AutoDiscovery = true }
        };
        var settings = EffectiveSettings.Resolve(new[] { "--no-auto-db" }, cfg, out _);
        Assert.False(settings.AutoDiscoverDb);
        Assert.False(settings.ShouldRunAutoDiscovery());
    }

    [Fact]
    public void EffectiveSettings_DatabaseDisabled_NoAutoDiscovery()
    {
        var settings = EffectiveSettings.Resolve(new[] { "--no-db" }, null, out _);
        Assert.False(settings.Database);
        Assert.False(settings.ShouldRunAutoDiscovery());
    }

    [Fact]
    public void EffectiveSettings_CliToonOverridesConfigTableFormat()
    {
        var cfg = new RoslynSenseConfig { TableFormat = "markdown" };
        var settings = EffectiveSettings.Resolve(new[] { "--toon" }, cfg, out _);
        Assert.Equal("toon", settings.TableFormat);
    }

    [Fact]
    public void EffectiveSettings_ConfigTableFormatPreserved()
    {
        var cfg = new RoslynSenseConfig { TableFormat = "toon" };
        var settings = EffectiveSettings.Resolve(Array.Empty<string>(), cfg, out _);
        Assert.Equal("toon", settings.TableFormat);
    }

    [Fact]
    public void EffectiveSettings_CliAliasBeatsConfigAlias()
    {
        var cfg = new RoslynSenseConfig
        {
            Database = new DatabaseConfig
            {
                Connections = { ["myapp"] = new ConnectionEntry("psql", "Host=h;Database=d;Username=u;Password=p") }
            }
        };
        var args = new[] { "--db", "myapp=mssql:Server=.;Database=Override;Integrated Security=true" };
        var settings = EffectiveSettings.Resolve(args, cfg, out _);
        var provider = Assert.Single(settings.ExplicitDbProviders);
        Assert.Equal("myapp", provider.Alias);
        Assert.Equal("mssql", provider.ProviderName);
    }

    [Fact]
    public void EffectiveSettings_ConfigAliasUsed_WhenNoCliConflict()
    {
        var cfg = new RoslynSenseConfig
        {
            Database = new DatabaseConfig
            {
                Connections = { ["only"] = new ConnectionEntry("sqlite", "Data Source=x.db") }
            }
        };
        var settings = EffectiveSettings.Resolve(Array.Empty<string>(), cfg, out _);
        var provider = Assert.Single(settings.ExplicitDbProviders);
        Assert.Equal("only", provider.Alias);
        Assert.Equal("sqlite", provider.ProviderName);
    }

    [Fact]
    public void EffectiveSettings_MalformedConfigConnection_BecomesWarning()
    {
        var cfg = new RoslynSenseConfig
        {
            Database = new DatabaseConfig
            {
                Connections = { ["bad"] = new ConnectionEntry("sqlite", "") }
            }
        };
        var settings = EffectiveSettings.Resolve(Array.Empty<string>(), cfg, out var warnings);
        Assert.Empty(settings.ExplicitDbProviders);
        Assert.Single(warnings);
        Assert.Contains("bad", warnings[0]);
    }
}

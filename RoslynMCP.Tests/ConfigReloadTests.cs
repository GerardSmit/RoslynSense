using Microsoft.Extensions.DependencyInjection;
using RoslynMCP.Config;
using RoslynMCP.Daemon;
using RoslynMCP.Languages;
using RoslynMCP.Services;
using RoslynMCP.Services.Database;
using RoslynMCP.Services.Designers;
using RoslynMCP.Services.Run;
using Xunit;

namespace RoslynMCP.Tests;

public class SettingsDiffTests
{
    private static EffectiveSettings Resolve(string json) =>
        EffectiveSettings.Resolve(
            [],
            System.Text.Json.JsonSerializer.Deserialize<RoslynSenseConfig>(json, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            }),
            out _);

    [Fact]
    public void Identical_settings_produce_no_changes()
    {
        var a = Resolve("""{"tools":{"webforms":true}}""");
        var b = Resolve("""{"tools":{"webforms":true}}""");

        Assert.Empty(SettingsDiff.Describe(a, b));
    }

    [Fact]
    public void A_toggled_feature_is_named_with_its_direction()
    {
        var a = Resolve("{}");
        var b = Resolve("""{"tools":{"webforms":false,"razor":false}}""");

        var changes = SettingsDiff.Describe(a, b);

        Assert.Contains("webforms: on → off", changes);
        Assert.Contains("razor: on → off", changes);
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void Added_and_removed_connections_are_listed_by_alias()
    {
        var a = Resolve("""{"database":{"connections":{"old":{"provider":"sqlserver","connectionString":"Server=a"}}}}""");
        var b = Resolve("""{"database":{"connections":{"fresh":{"provider":"sqlserver","connectionString":"Server=b"}}}}""");

        var changes = SettingsDiff.Describe(a, b);

        Assert.Contains("database connections: +fresh, -old", changes);
    }

    [Fact]
    public void Numeric_and_format_settings_are_reported_with_values()
    {
        var a = Resolve("{}");
        var b = Resolve("""{"maxWorkspaces":8,"hostIdleMinutes":5,"tableFormat":"toon"}""");

        var changes = SettingsDiff.Describe(a, b);

        Assert.Contains("maxWorkspaces: 4 → 8", changes);
        Assert.Contains("hostIdleMinutes: 30 → 5", changes);
        Assert.Contains("tableFormat: markdown → toon", changes);
    }

    [Fact]
    public void Preload_null_and_empty_differ()
    {
        // Null means auto-discover, [] means disabled — the resolved behavior differs.
        var auto = Resolve("{}");
        var disabled = Resolve("""{"preload":[]}""");

        Assert.Contains("preload paths changed", SettingsDiff.Describe(auto, disabled));
        Assert.Empty(SettingsDiff.Describe(auto, Resolve("{}")));
    }
}

// Building the container publishes LanguageRegistry.Current — process-wide state.
[Collection(SharedState.Name)]
public class ToolHostServicesRebuildTests
{
    private static EffectiveSettings Settings(RoslynSenseConfig? config = null) =>
        EffectiveSettings.Resolve([], config, out _);

    [Fact]
    public void Rebuild_carries_the_stateful_stores_and_replaces_the_settings_shaped_services()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rsense-rebuild-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var first = ToolHostServices.Build(Settings(), new MarkdownFormatter(), dir);
            var carriedStore = first.GetRequiredService<BackgroundTaskStore>();
            var carriedApps = first.GetRequiredService<AppSessionStore>();
            var carriedSessions = first.GetRequiredService<SolutionSessionService>();
            var firstRegistry = first.GetRequiredService<LanguageRegistry>();

            var newSettings = Settings(new RoslynSenseConfig { Tools = new ToolsConfig { WebForms = false } });
            var second = ToolHostServices.Build(newSettings, new MarkdownFormatter(), dir, carryFrom: first);

            // State the user can see survives the swap.
            Assert.Same(carriedStore, second.GetRequiredService<BackgroundTaskStore>());
            Assert.Same(carriedApps, second.GetRequiredService<AppSessionStore>());
            Assert.Same(carriedSessions, second.GetRequiredService<SolutionSessionService>());

            // The settings-shaped surface is rebuilt: the registry is new, reflects the new
            // toggles, and has published itself as the process-wide current registry.
            var secondRegistry = second.GetRequiredService<LanguageRegistry>();
            Assert.NotSame(firstRegistry, secondRegistry);
            Assert.Contains(firstRegistry.Packs, p => p.GetType().Name == "WebFormsLanguage");
            Assert.DoesNotContain(secondRegistry.Packs, p => p.GetType().Name == "WebFormsLanguage");
            Assert.Same(secondRegistry, LanguageRegistry.Current);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}

public class ConfigWatcherReloadTests : IDisposable
{
    private readonly string _root;

    public ConfigWatcherReloadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rsense-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string ConfigPath => Path.Combine(_root, RoslynSenseConfigLoader.FileName);

    private static EffectiveSettings Current(string dir)
    {
        var (config, _, _) = RoslynSenseConfigLoader.Load(dir);
        return EffectiveSettings.Resolve([], config, out _);
    }

    [Fact]
    public void An_edited_config_is_applied_with_its_changes_named()
    {
        File.WriteAllText(ConfigPath, "{}");
        var reloads = new List<ConfigReload>();
        using var watcher = ConfigWatcher.Start(_root, [], Current(_root), reloads.Add);
        Assert.NotNull(watcher);

        File.WriteAllText(ConfigPath, """{"tools":{"webforms":false}}""");
        watcher!.Reload();

        var reload = Assert.Single(reloads);
        Assert.False(reload.Settings.WebForms);
        Assert.Contains("webforms: on → off", reload.Changes);
    }

    [Fact]
    public void A_touch_that_changes_nothing_is_ignored()
    {
        File.WriteAllText(ConfigPath, """{"tools":{"webforms":false}}""");
        var reloads = new List<ConfigReload>();
        using var watcher = ConfigWatcher.Start(_root, [], Current(_root), reloads.Add);

        watcher!.Reload(); // no write in between

        Assert.Empty(reloads);
    }

    [Fact]
    public void Broken_json_keeps_the_current_settings()
    {
        File.WriteAllText(ConfigPath, "{}");
        var reloads = new List<ConfigReload>();
        using var watcher = ConfigWatcher.Start(_root, [], Current(_root), reloads.Add);

        File.WriteAllText(ConfigPath, """{"tools":{""");
        watcher!.Reload();

        Assert.Empty(reloads);

        // And once the file parses again, the reload happens against the settings that were
        // actually in effect, not against the broken intermediate.
        File.WriteAllText(ConfigPath, """{"tools":{"webforms":false}}""");
        watcher.Reload();
        var reload = Assert.Single(reloads);
        Assert.Contains("webforms: on → off", reload.Changes);
    }

    [Fact]
    public void A_deleted_config_reverts_to_defaults()
    {
        File.WriteAllText(ConfigPath, """{"tools":{"webforms":false}}""");
        var reloads = new List<ConfigReload>();
        using var watcher = ConfigWatcher.Start(_root, [], Current(_root), reloads.Add);

        File.Delete(ConfigPath);
        watcher!.Reload();

        var reload = Assert.Single(reloads);
        Assert.True(reload.Settings.WebForms);
        Assert.Contains("webforms: off → on", reload.Changes);
        Assert.Null(reload.ConfigPath);
    }

    [Fact]
    public void A_config_created_where_none_was_is_picked_up()
    {
        var reloads = new List<ConfigReload>();
        using var watcher = ConfigWatcher.Start(_root, [], Current(_root), reloads.Add);

        File.WriteAllText(ConfigPath, """{"maxWorkspaces":9}""");
        watcher!.Reload();

        var reload = Assert.Single(reloads);
        Assert.Equal(9, reload.Settings.MaxWorkspaces);
        Assert.Contains("maxWorkspaces: 4 → 9", reload.Changes);
    }

    [Fact]
    public void A_change_the_diff_cannot_name_still_reloads()
    {
        File.WriteAllText(ConfigPath, "{}");
        var reloads = new List<ConfigReload>();
        using var watcher = ConfigWatcher.Start(_root, [], Current(_root), reloads.Add);

        // resources lookup details are below the diff's granularity
        File.WriteAllText(ConfigPath, """{"resources":{"missingKeyDiagnostic":true}}""");
        watcher!.Reload();

        var reload = Assert.Single(reloads);
        Assert.Contains("configuration details changed", reload.Changes);
    }
}

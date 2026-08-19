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

/// <remarks>
/// In the serialized collection because the home directory the global and personal layers live
/// under is an environment variable, and it is pointed at an empty directory here so that whatever
/// the machine running the suite has in <c>~/.roslynsense</c> cannot change what these assert.
/// </remarks>
[Collection(SharedState.Name)]
public class ConfigWatcherReloadTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousHome;

    public ConfigWatcherReloadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rsense-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _previousHome = Environment.GetEnvironmentVariable(ConfigPaths.HomeOverrideVariable);
        Environment.SetEnvironmentVariable(
            ConfigPaths.HomeOverrideVariable,
            Directory.CreateDirectory(Path.Combine(_root, ".home")).FullName);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ConfigPaths.HomeOverrideVariable, _previousHome);
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

    /// <summary>
    /// The personal sibling is a layer like any other, so editing it is a configuration change.
    /// Before layers existed the watcher only ever looked at one file name.
    /// </summary>
    [Fact]
    public void An_edited_local_override_reloads_too()
    {
        File.WriteAllText(ConfigPath, """{"tools":{"webforms":true}}""");
        var reloads = new List<ConfigReload>();
        using var watcher = ConfigWatcher.Start(_root, [], Current(_root), reloads.Add);

        File.WriteAllText(
            Path.Combine(_root, RoslynSenseConfigLoader.LocalFileName),
            """{"tools":{"webforms":false}}""");
        watcher!.Reload();

        var reload = Assert.Single(reloads);
        Assert.False(reload.Settings.WebForms);
        Assert.Contains("webforms: on → off", reload.Changes);
    }

    /// <summary>
    /// Deleting the nearer file falls back to the parent's value rather than to the default —
    /// which is the difference between merging the chain and stopping at the first file found.
    /// </summary>
    [Fact]
    public void A_deleted_nearer_config_falls_back_to_the_parent()
    {
        string nested = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        File.WriteAllText(ConfigPath, """{"maxWorkspaces":9}""");
        string nestedConfig = Path.Combine(nested, RoslynSenseConfigLoader.FileName);
        File.WriteAllText(nestedConfig, """{"maxWorkspaces":7}""");

        var reloads = new List<ConfigReload>();
        using var watcher = ConfigWatcher.Start(nested, [], Current(nested), reloads.Add);
        Assert.Equal(7, Current(nested).MaxWorkspaces);

        File.Delete(nestedConfig);
        watcher!.Reload();

        var reload = Assert.Single(reloads);
        Assert.Equal(9, reload.Settings.MaxWorkspaces);
        Assert.Equal(ConfigPath, reload.ConfigPath);
    }

    /// <summary>
    /// A file the watcher never used to look at: the global layer is outside the working
    /// directory entirely, and an edit to it changes what every solution resolves to.
    /// </summary>
    [Fact]
    public void An_edited_global_config_reloads()
    {
        var reloads = new List<ConfigReload>();
        using var watcher = ConfigWatcher.Start(_root, [], Current(_root), reloads.Add);

        File.WriteAllText(ConfigPaths.GlobalConfigFile!, """{"tableFormat":"toon"}""");
        watcher!.Reload();

        var reload = Assert.Single(reloads);
        Assert.Equal("toon", reload.Settings.TableFormat);
    }

    /// <summary>
    /// The file-system events, not just the reload body: everything above calls
    /// <c>Reload</c> by hand, which would still pass if nothing were watching at all.
    /// </summary>
    /// <remarks>
    /// The personal layer is the case that was actually broken. Its directory —
    /// <c>&lt;home&gt;/projects/&lt;mangled-path&gt;/</c> — does not exist until the first personal
    /// setting is saved, a watcher cannot be opened on a directory that is not there, and the save
    /// that creates it is exactly the one nobody was listening for. One recursive watcher over the
    /// home directory is what covers it.
    /// </remarks>
    [Fact]
    public async Task A_personal_config_saved_for_the_first_time_reloads_on_its_own()
    {
        var reloaded = new TaskCompletionSource<ConfigReload>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = ConfigWatcher.Start(
            _root, [], Current(_root), reload => reloaded.TrySetResult(reload));
        Assert.NotNull(watcher);

        string personal = ConfigPaths.PersonalConfigFile(_root)!;
        Assert.False(Directory.Exists(Path.GetDirectoryName(personal)));

        Directory.CreateDirectory(Path.GetDirectoryName(personal)!);
        File.WriteAllText(personal, """{"maxWorkspaces":9}""");

        Assert.Equal(9, (await WithinTimeout(reloaded.Task)).Settings.MaxWorkspaces);
    }

    [Fact]
    public async Task An_edited_global_config_reloads_on_its_own()
    {
        var reloaded = new TaskCompletionSource<ConfigReload>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = ConfigWatcher.Start(
            _root, [], Current(_root), reload => reloaded.TrySetResult(reload));
        Assert.NotNull(watcher);

        File.WriteAllText(ConfigPaths.GlobalConfigFile!, """{"tableFormat":"toon"}""");

        Assert.Equal("toon", (await WithinTimeout(reloaded.Task)).Settings.TableFormat);
    }

    /// <summary>
    /// A home directory that is not there yet is created rather than skipped — it is the one
    /// directory this owns, and not creating it means no live reload for anyone who has not
    /// already saved a global setting.
    /// </summary>
    [Fact]
    public void A_missing_home_directory_is_created_so_that_it_can_be_watched()
    {
        string home = Path.Combine(_root, "not-yet");
        Environment.SetEnvironmentVariable(ConfigPaths.HomeOverrideVariable, home);

        using var watcher = ConfigWatcher.Start(_root, [], Current(_root), _ => { });

        Assert.NotNull(watcher);
        Assert.True(Directory.Exists(home));
    }

    /// <summary>Fails the test rather than hanging the run when no event ever arrives.</summary>
    private static async Task<ConfigReload> WithinTimeout(Task<ConfigReload> reload)
    {
        var finished = await Task.WhenAny(reload, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(finished == reload, "No configuration reload arrived; nothing was watching.");
        return await reload;
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

using RoslynMCP.Config;
using RoslynMCP.Services;

namespace RoslynMCP.Services.Database;

/// <summary>
/// Watches the working tree's connection-string sources (<c>web.config</c>, <c>app.config</c>,
/// <c>appsettings*.json</c>) and re-resolves the <see cref="DbConnectionRegistry"/> when one
/// changes. Without this, the registry is a snapshot of the files at host start, and an edited
/// connection string keeps pointing the db_* tools at the old database until the host restarts.
/// </summary>
/// <remarks>
/// One recursive watcher, filtered to the file names discovery reads, with events from skipped
/// directories dropped — a build copying <c>appsettings.json</c> into <c>bin/</c> must not
/// trigger anything. Every event re-runs the full discovery walk rather than trusting the
/// event's path, so create, delete and rename all resolve to whatever the tree now says.
/// Settings arrive by reference and are swapped on a configuration reload; the registry keeps
/// runtime-added connections across every refresh (see <see cref="DbConnectionRegistry.ApplyResolved"/>).
/// </remarks>
public sealed class DbConnectionWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(750);

    private readonly string _workingDir;
    private readonly DbConnectionRegistry _registry;
    private readonly Debouncer _debounce = new("DbConfig");
    private FileSystemWatcher? _fsw;
    private volatile EffectiveSettings _settings;

    private DbConnectionWatcher(string workingDir, EffectiveSettings settings, DbConnectionRegistry registry)
    {
        _workingDir = workingDir;
        _settings = settings;
        _registry = registry;
    }

    /// <summary>
    /// Starts watching, or returns null when the directory cannot be watched — a host whose
    /// connections stay start-time stale is degraded, not broken.
    /// </summary>
    public static DbConnectionWatcher? Start(
        string workingDir, EffectiveSettings settings, DbConnectionRegistry registry)
    {
        var watcher = new DbConnectionWatcher(workingDir, settings, registry);
        if (!watcher.Watch())
        {
            watcher.Dispose();
            return null;
        }
        return watcher;
    }

    private bool Watch()
    {
        try
        {
            var fsw = new FileSystemWatcher(_workingDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            fsw.Filters.Add("appsettings*.json");
            fsw.Filters.Add("web*.config");
            fsw.Filters.Add("app*.config");
            fsw.Changed += (_, e) => OnEvent(e.FullPath);
            fsw.Created += (_, e) => OnEvent(e.FullPath);
            fsw.Deleted += (_, e) => OnEvent(e.FullPath);
            fsw.Renamed += (_, e) => { OnEvent(e.OldFullPath); OnEvent(e.FullPath); };
            fsw.EnableRaisingEvents = true;
            _fsw = fsw;
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            if (Directory.Exists(_workingDir))
                Console.Error.WriteLine($"[DbConfig] Cannot watch '{_workingDir}' for connection strings: {ex.Message}");
            return false;
        }
    }

    private void OnEvent(string fullPath)
    {
        if (!AutoConnectionStringDiscovery.IsConfigFile(Path.GetFileName(fullPath)))
            return;
        if (AutoConnectionStringDiscovery.IsUnderIgnoredDirectory(_workingDir, fullPath))
            return;

        _debounce.Restart(Debounce, _ =>
        {
            Refresh();
            return Task.CompletedTask;
        });
    }

    /// <summary>A configuration reload changed the settings future file events resolve under.
    /// The reload itself already re-resolved the registry; this only swaps the reference.</summary>
    public void UpdateSettings(EffectiveSettings settings) => _settings = settings;

    /// <summary>The debounced body — the unit under test.</summary>
    internal void Refresh()
    {
        var changes = Resolve(_registry, _settings, _workingDir, out _);
        if (changes.Count > 0)
            Console.Error.WriteLine($"[DbConfig] Connection strings changed: {string.Join("; ", changes)}.");
    }

    /// <summary>
    /// Resolves what the registry should hold under <paramref name="settings"/> — the explicit
    /// connections plus auto-discovery over <paramref name="workingDir"/> when enabled — and
    /// applies it. The one code path for startup, config reload, and file-change refresh.
    /// </summary>
    public static IReadOnlyList<string> Resolve(
        DbConnectionRegistry registry, EffectiveSettings settings, string workingDir,
        out IReadOnlyList<AutoConnectionStringDiscovery.DiscoveryWarning> warnings)
    {
        warnings = Array.Empty<AutoConnectionStringDiscovery.DiscoveryWarning>();

        var explicitProviders = settings.Database
            ? settings.ExplicitDbProviders
            : Array.Empty<IDbProvider>();
        var auto = settings.ShouldRunAutoDiscovery()
            ? AutoConnectionStringDiscovery.Discover(workingDir, out warnings)
            : Array.Empty<IDbProvider>();

        return registry.ApplyResolved(explicitProviders, auto);
    }

    public void Dispose()
    {
        _debounce.Cancel();
        _fsw?.Dispose();
        _fsw = null;
    }
}

using RoslynMCP.Config;

namespace RoslynMCP.Daemon;

/// <summary>What a configuration reload resolved to, and why it counts as a change.</summary>
internal sealed record ConfigReload(
    EffectiveSettings Settings,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Warnings,
    string? ConfigPath);

/// <summary>
/// Watches <c>roslynsense.json</c> for a running host and reports when the settings it resolves
/// to actually change. Without this, the file is read once at startup and every later edit is
/// silently ignored until the daemon happens to idle out — a wait the user cannot see and did
/// not choose.
/// </summary>
/// <remarks>
/// Two directories are watched: the one holding the config file the startup walk found, and the
/// working directory itself — so a config newly created closer to the solution than the one in
/// effect is noticed too. A config appearing in some other ancestor directory is not; that costs
/// a watcher per path segment and moves in practice are between "next to the solution" and
/// "nowhere".
///
/// Every event re-runs the same startup walk (<see cref="RoslynSenseConfigLoader.Load"/>) rather
/// than trusting the event's path, so create, delete and rename all resolve to whichever file
/// now governs. A file whose text did not change (editors touch without writing) is dropped, and
/// a file that no longer parses keeps the current settings — half a config must never win over a
/// whole one.
/// </remarks>
internal sealed class ConfigWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly string _workingDir;
    private readonly string[] _args;
    private readonly Action<ConfigReload> _apply;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _gate = new();
    private CancellationTokenSource? _debounce;
    private EffectiveSettings _current;
    private string? _lastText;

    private ConfigWatcher(string workingDir, string[] args, EffectiveSettings current, string? initialText, Action<ConfigReload> apply)
    {
        _workingDir = workingDir;
        _args = args;
        _current = current;
        _lastText = initialText;
        _apply = apply;
    }

    /// <summary>
    /// Starts watching, or returns null when no directory can be watched (network path gone,
    /// access denied) — a host without live reload is degraded, not broken.
    /// </summary>
    /// <param name="args">The CLI args <paramref name="current"/> was resolved under. A reload
    /// resolves with the same ones, so a flag like <c>--no-webforms</c> keeps winning over the
    /// file and never shows up as a phantom change.</param>
    public static ConfigWatcher? Start(
        string workingDir, string[] args, EffectiveSettings current, Action<ConfigReload> apply)
    {
        var (_, configPath, _) = RoslynSenseConfigLoader.Load(workingDir);
        string? initialText = configPath is not null ? TryRead(configPath) : null;

        var watcher = new ConfigWatcher(workingDir, args, current, initialText, apply);

        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { workingDir };
        if (configPath is not null && Path.GetDirectoryName(configPath) is { } configDir)
            dirs.Add(configDir);

        foreach (string dir in dirs)
        {
            try
            {
                var fsw = new FileSystemWatcher(dir, RoslynSenseConfigLoader.FileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                fsw.Changed += (_, _) => watcher.OnEvent();
                fsw.Created += (_, _) => watcher.OnEvent();
                fsw.Deleted += (_, _) => watcher.OnEvent();
                fsw.Renamed += (_, _) => watcher.OnEvent();
                fsw.EnableRaisingEvents = true;
                watcher._watchers.Add(fsw);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[Config] Cannot watch '{dir}' for {RoslynSenseConfigLoader.FileName}: {ex.Message}");
            }
        }

        if (watcher._watchers.Count == 0)
        {
            watcher.Dispose();
            return null;
        }

        return watcher;
    }

    private void OnEvent()
    {
        lock (_gate)
        {
            _debounce?.Cancel();
            var cts = _debounce = new CancellationTokenSource();
            _ = ReloadAfterDelayAsync(cts.Token);
        }
    }

    private async Task ReloadAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Debounce, ct);
            Reload();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Config] Reload failed: {ex.Message}");
        }
    }

    /// <summary>The debounced body — the unit under test.</summary>
    internal void Reload()
    {
        var (config, configPath, loadError) = RoslynSenseConfigLoader.Load(_workingDir);
        if (loadError is not null)
        {
            // A save mid-edit, or genuinely broken JSON. Either way the current settings stand:
            // reverting a running host to defaults because a file was briefly invalid would be
            // strictly worse than staying stale for one more save.
            Console.Error.WriteLine($"[Config] {RoslynSenseConfigLoader.FileName} ({configPath}): {loadError}; keeping current settings.");
            return;
        }

        string? text = configPath is not null ? TryRead(configPath) : null;

        ConfigReload reload;
        lock (_gate)
        {
            if (string.Equals(text, _lastText, StringComparison.Ordinal))
                return; // touched, not changed

            var settings = EffectiveSettings.Resolve(_args, config, out var warnings);
            var changes = SettingsDiff.Describe(_current, settings);
            if (changes.Count == 0)
                changes = ["configuration details changed"];

            _current = settings;
            _lastText = text;
            reload = new ConfigReload(settings, changes, warnings, configPath);
        }

        _apply(reload);
    }

    private static string? TryRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _debounce?.Cancel();
            _debounce = null;
        }
        foreach (var fsw in _watchers)
            fsw.Dispose();
        _watchers.Clear();
    }
}

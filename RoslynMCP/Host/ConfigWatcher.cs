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
/// Every directory a layer could occupy is watched: every ancestor of the working directory that
/// could hold a <c>roslynsense.json</c> or a <c>roslynsense.local.json</c>, one watcher each, plus
/// one recursive watcher over the home directory for the global and personal layers. That is one
/// watcher per path segment, which the old two-directory version avoided; it is affordable now
/// because it is also necessary, since a repository-root file two levels up now contributes to the
/// answer instead of being shadowed.
///
/// Every event re-runs the same startup walk (<see cref="RoslynSenseConfigLoader.LoadLayers"/>)
/// rather than trusting the event's path, so create, delete and rename all resolve to whatever now
/// governs. The comparison is against every layer's text at once: a file whose text did not change
/// (editors touch without writing) is dropped, and a file that no longer parses keeps the current
/// settings — half a config must never win over a whole one.
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
        var layered = RoslynSenseConfigLoader.LoadLayers(workingDir);

        var watcher = new ConfigWatcher(workingDir, args, current, Fingerprint(layered), apply);

        string home = ConfigPaths.HomeDirectory;

        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { workingDir };
        foreach (var layer in layered.Layers)
        {
            if (Path.GetDirectoryName(layer.FilePath) is not { Length: > 0 } layerDir)
                continue;

            // The two home layers are covered by one recursive watcher below, and have to be:
            // the personal layer's own directory usually does not exist yet, and a watcher
            // cannot be opened on a directory that is not there.
            if (!IsUnder(layerDir, home))
                dirs.Add(layerDir);
        }

        foreach (string dir in dirs)
            watcher.Watch(dir, recursive: false);

        watcher.WatchHome(home);

        if (watcher._watchers.Count == 0)
        {
            watcher.Dispose();
            return null;
        }

        return watcher;
    }

    /// <summary>
    /// Watches the home directory, creating it if it is not there.
    /// </summary>
    /// <remarks>
    /// Recursive, and the one place this class creates anything. Both home layers live under it —
    /// the global file directly, the personal one inside <c>projects/&lt;mangled-path&gt;/</c> —
    /// and neither directory need exist yet: the first personal setting a person ever saves
    /// creates its directory, and a watcher opened per layer directory would have missed exactly
    /// that save. One directory that this program owns anyway is a smaller price than a reload
    /// that works only for people who already had the folder.
    /// </remarks>
    private void WatchHome(string home)
    {
        if (home.Length == 0)
            return;

        try
        {
            Directory.CreateDirectory(home);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No home to watch, so no global or personal layer to reload from either.
            return;
        }

        Watch(home, recursive: true);
    }

    /// <summary>One watcher over one directory, or nothing if it cannot be opened.</summary>
    /// <remarks>
    /// Filtered by name pattern rather than one watcher per file name: the two names differ only
    /// by an infix, and a filter that matches both also matches a <c>roslynsense.local.json</c>
    /// created after the watcher was set up.
    /// </remarks>
    private void Watch(string dir, bool recursive)
    {
        try
        {
            var fsw = new FileSystemWatcher(dir, "roslynsense*.json")
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            fsw.Changed += (_, _) => OnEvent();
            fsw.Created += (_, _) => OnEvent();
            fsw.Deleted += (_, _) => OnEvent();
            fsw.Renamed += (_, _) => OnEvent();
            fsw.EnableRaisingEvents = true;
            _watchers.Add(fsw);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // A directory that is simply not there is the ordinary case, not a fault.
            if (Directory.Exists(dir))
                Console.Error.WriteLine($"[Config] Cannot watch '{dir}' for {RoslynSenseConfigLoader.FileName}: {ex.Message}");
        }
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or sits inside it.</summary>
    private static bool IsUnder(string path, string root)
    {
        if (root.Length == 0)
            return false;

        string normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        string normalizedPath = Path.TrimEndingDirectorySeparator(path);

        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
        var layered = RoslynSenseConfigLoader.LoadLayers(_workingDir);
        if (layered.LoadError is { } loadError)
        {
            // A save mid-edit, or genuinely broken JSON. Either way the current settings stand:
            // reverting a running host to defaults because a file was briefly invalid would be
            // strictly worse than staying stale for one more save.
            Console.Error.WriteLine($"[Config] {loadError}; keeping current settings.");
            return;
        }

        var config = layered.Config;
        string? configPath = layered.PrimaryPath;
        string? text = Fingerprint(layered);

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

    /// <summary>
    /// Every layer's path and text, in precedence order — what "the configuration" is, as one
    /// string to compare against the last one.
    /// </summary>
    /// <remarks>
    /// The paths are part of it, not only the contents: deleting the nearest file and leaving a
    /// parent one whose text happens to match would otherwise read as no change at all, when what
    /// actually changed is which file governs.
    /// </remarks>
    private static string? Fingerprint(LayeredConfig layered)
    {
        var present = layered.Present.ToList();
        if (present.Count == 0)
            return null;

        return string.Join(
            " | ",
            present.Select(layer => layer.FilePath + " = " + (TryRead(layer.FilePath) ?? string.Empty)));
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

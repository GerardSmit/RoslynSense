namespace RoslynMCP.Services;

/// <summary>
/// Manages shadow-copying of analyzer DLLs to a temporary directory so that the
/// original files remain unlocked and can be overwritten by MSBuild during builds.
/// <para>
/// Only non-NuGet analyzer DLLs (e.g., project-referenced analyzers whose output
/// lives in bin/obj directories) are shadow-copied. NuGet package analyzers are
/// loaded directly because the global packages cache is immutable.
/// </para>
/// <para>
/// Each MCP server instance gets its own subdirectory under
/// <c>%TEMP%/roslyn-mcp-shadow/</c>, protected by an exclusive file lock.
/// On startup, stale directories from crashed or shut-down instances are cleaned up
/// by attempting to acquire their lock files.
/// </para>
/// </summary>
internal sealed class ShadowCopyManager : IDisposable
{
    private static readonly string BaseDir = Path.Combine(Path.GetTempPath(), "roslyn-mcp-shadow");

    private readonly string _instanceDir;
    private readonly FileStream _lockStream;
    private readonly Lock _lock = new();

    /// <summary>Source directory → shadow subdirectory path.</summary>
    private readonly Dictionary<string, string> _shadowDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Source directory → <see cref="FileSystemWatcher"/> for DLL changes.</summary>
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Source directory → debounce timer for coalescing rapid FS events.</summary>
    private readonly Dictionary<string, Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _nugetPackagesDir;
    private int _generationCounter;
    private bool _disposed;

    /// <summary>
    /// Fired (after a debounce delay) when an analyzer DLL in a watched source
    /// directory is created or modified. The argument is the source directory path.
    /// </summary>
    public event Action<string>? AnalyzerDirectoryChanged;

    public ShadowCopyManager()
    {
        _nugetPackagesDir = GetNuGetPackagesDirectory();
        CleanupStaleInstances();

        _instanceDir = Path.Combine(BaseDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_instanceDir);

        _lockStream = new FileStream(
            Path.Combine(_instanceDir, ".lock"),
            FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        Console.Error.WriteLine($"[ShadowCopy] Initialized: {_instanceDir}");
    }

    /// <summary>
    /// Returns <c>true</c> when the analyzer at <paramref name="path"/> should be
    /// shadow-copied. Skipped for the NuGet global packages folder (immutable) and for
    /// paths already inside our own shadow root (avoids double-shadowing on re-loads).
    /// </summary>
    public bool NeedsShadowCopy(string path)
    {
        if (path.StartsWith(_nugetPackagesDir, StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.StartsWith(BaseDir, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>
    /// Returns the path to load from. For project-output analyzers this is a shadow
    /// copy path; for NuGet analyzers the original path is returned unchanged.
    /// The first call for a given source directory copies <b>all</b> DLLs, PDBs, and
    /// JSON metadata files from that directory to the shadow location.
    /// </summary>
    public string GetLoadPath(string originalPath)
    {
        if (!NeedsShadowCopy(originalPath))
            return originalPath;

        lock (_lock)
        {
            string sourceDir = Path.GetDirectoryName(Path.GetFullPath(originalPath))!;
            string shadowDir = EnsureShadowDirectory(sourceDir);
            return Path.Combine(shadowDir, Path.GetFileName(originalPath));
        }
    }

    /// <summary>
    /// Maps an analyzer DLL path — either an <b>original</b> source path or a
    /// <b>shadow-copy</b> path — to the source directory that the rebuild watcher fires
    /// <see cref="AnalyzerDirectoryChanged"/> on. Returns <c>null</c> when the path is not
    /// associated with any shadow-copied directory (e.g. NuGet packages, never watched).
    /// <para>
    /// Callers (e.g. <c>AnalyzerHost</c>) must key their rebuild-eviction maps on this
    /// value rather than on <c>Path.GetDirectoryName(path)</c>: after the workspace rebind,
    /// analyzer paths are already shadow paths, so their directory is a shadow temp dir —
    /// never the source dir the watcher reports, so the two would never match.
    /// </para>
    /// </summary>
    public string? TryGetSourceDirectory(string path)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? path;
        lock (_lock)
        {
            // Already an original source dir → it's a watcher key as-is.
            if (_shadowDirectories.ContainsKey(dir))
                return dir;

            // A shadow dir → reverse-map to the source dir the watcher fires on.
            foreach (var (source, shadow) in _shadowDirectories)
            {
                if (string.Equals(shadow, dir, StringComparison.OrdinalIgnoreCase))
                    return source;
            }
        }
        return null;
    }

    /// <summary>
    /// Invalidates (deletes) the shadow copy for <paramref name="sourceDir"/>.
    /// The next call to <see cref="GetLoadPath"/> will re-copy from the source.
    /// </summary>
    public void Invalidate(string sourceDir)
    {
        lock (_lock)
        {
            if (_shadowDirectories.TryGetValue(sourceDir, out var shadowDir))
            {
                _shadowDirectories.Remove(sourceDir);
                try { Directory.Delete(shadowDir, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }

    // ───────────────────────── Private helpers ─────────────────────────

    private string EnsureShadowDirectory(string sourceDir)
    {
        if (_shadowDirectories.TryGetValue(sourceDir, out var existing) && Directory.Exists(existing))
            return existing;

        // Use a unique generation suffix every time we shadow-copy a directory.
        // After a rebuild, the old shadow directory may still hold a locked DLL
        // (the previous collectible ALC's Unload() is asynchronous — file handles
        // release only after a future GC), so reusing the same shadow path would
        // fail with "file in use" when File.Copy tries to overwrite. Each generation
        // gets its own subdirectory; stale ones are cleaned up at process exit.
        int gen = Interlocked.Increment(ref _generationCounter);
        string shadowDir = Path.Combine(_instanceDir, $"{ComputeDirectoryHash(sourceDir)}_{gen:x}");
        Directory.CreateDirectory(shadowDir);

        // Copy all DLLs, PDBs, and JSON metadata (e.g. .deps.json, .runtimeconfig.json)
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".dll" or ".pdb" or ".json")
            {
                try
                {
                    File.Copy(file, Path.Combine(shadowDir, Path.GetFileName(file)), overwrite: true);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[ShadowCopy] Failed to copy '{Path.GetFileName(file)}': {ex.Message}");
                }
            }
        }

        _shadowDirectories[sourceDir] = shadowDir;
        EnsureWatcher(sourceDir);

        Console.Error.WriteLine($"[ShadowCopy] Copied '{sourceDir}' → '{shadowDir}'");
        return shadowDir;
    }

    private void EnsureWatcher(string directory)
    {
        if (_watchers.ContainsKey(directory) || _disposed)
            return;

        try
        {
            var watcher = new FileSystemWatcher(directory, "*.dll")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            watcher.Changed += (_, _) => DebouncedInvalidate(directory);
            watcher.Created += (_, _) => DebouncedInvalidate(directory);

            _watchers[directory] = watcher;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[ShadowCopy] Failed to create watcher for '{directory}': {ex.Message}");
        }
    }

    private void DebouncedInvalidate(string directory)
    {
        lock (_lock)
        {
            if (_disposed) return;

            if (_debounceTimers.TryGetValue(directory, out var existing))
                existing.Dispose();

            _debounceTimers[directory] = new Timer(_ =>
            {
                Invalidate(directory);
                Console.Error.WriteLine(
                    $"[ShadowCopy] Detected rebuild in '{directory}', invalidated shadow copy.");
                AnalyzerDirectoryChanged?.Invoke(directory);
            }, null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
    }

    // ───────────────────────── NuGet cache detection ─────────────────────────

    private static string GetNuGetPackagesDirectory()
    {
        string? envVar = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(envVar))
            return Path.GetFullPath(envVar);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages");
    }

    // ───────────────────────── Stale instance cleanup ─────────────────────────

    private static void CleanupStaleInstances()
    {
        if (!Directory.Exists(BaseDir))
            return;

        foreach (var dir in Directory.GetDirectories(BaseDir))
        {
            string lockPath = Path.Combine(dir, ".lock");
            if (!File.Exists(lockPath))
            {
                TryDeleteDirectory(dir);
                continue;
            }

            try
            {
                // If we can open the lock exclusively, the owning process is gone.
                using var fs = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                continue; // Lock held — another MCP server is still running
            }
            catch
            {
                continue;
            }

            TryDeleteDirectory(dir);
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            Console.Error.WriteLine($"[ShadowCopy] Cleaned up stale directory: {dir}");
        }
        catch { /* best effort */ }
    }

    // ───────────────────────── Hashing ─────────────────────────

    /// <summary>FNV-1a hash of the directory path, used as a subdirectory name.</summary>
    private static string ComputeDirectoryHash(string input)
    {
        uint hash = 2166136261;
        foreach (char c in input.ToLowerInvariant())
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash.ToString("x8");
    }

    // ───────────────────────── Dispose ─────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var timer in _debounceTimers.Values)
            timer.Dispose();
        _debounceTimers.Clear();

        foreach (var watcher in _watchers.Values)
            watcher.Dispose();
        _watchers.Clear();

        _lockStream.Dispose();

        try { Directory.Delete(_instanceDir, recursive: true); }
        catch { /* best effort */ }
    }
}

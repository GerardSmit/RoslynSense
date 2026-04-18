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
    /// shadow-copied (i.e.&nbsp;it is <b>not</b> inside the NuGet global packages folder).
    /// </summary>
    public bool NeedsShadowCopy(string path)
    {
        return !path.StartsWith(_nugetPackagesDir, StringComparison.OrdinalIgnoreCase);
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

        string shadowDir = Path.Combine(_instanceDir, ComputeDirectoryHash(sourceDir));
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

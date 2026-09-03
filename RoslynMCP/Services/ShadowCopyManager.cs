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

    /// <summary>Source directory → content hash of its DLLs when it was last accepted. What
    /// decides whether a write was a real rebuild; see <see cref="OnQuiet"/>.</summary>
    private readonly Dictionary<string, string> _fingerprints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a watched directory must stop changing before it is judged. A build
    /// writes its outputs in bursts, and each burst used to be its own eviction.</summary>
    private static readonly TimeSpan RebuildQuiet = TimeSpan.FromMilliseconds(1000);

    private readonly string _nugetPackagesDir;

    /// <summary>Directory trees a build never writes to, so analyzers in them can be loaded in
    /// place. See <see cref="NeedsShadowCopy"/>.</summary>
    private readonly string[] _immutableRoots;

    private int _generationCounter;
    private bool _disposed;

    /// <summary>
    /// Fired (after a debounce delay) when an analyzer DLL in a watched source
    /// directory is created or modified. The argument is the source directory path.
    /// </summary>
    public event Action<string>? AnalyzerDirectoryChanged;

    public ShadowCopyManager() : this(cleanupStaleInstances: true)
    {
    }

    /// <summary>
    /// <paramref name="cleanupStaleInstances"/> is for tests that need a second manager in this
    /// process. The cleanup reclaims shadow directories left by crashed instances, which it
    /// identifies by being able to take their lock — a reasonable thing to do once at startup, and
    /// a destructive one for a manager created alongside the live one, whose copies it can delete
    /// out from under whatever is loading analyzers at the time.
    /// </summary>
    internal ShadowCopyManager(bool cleanupStaleInstances)
    {
        _nugetPackagesDir = GetNuGetPackagesDirectory();
        _immutableRoots = BuildImmutableRoots(_nugetPackagesDir);
        if (cleanupStaleInstances)
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
    /// shadow-copied. Skipped for anything that a build cannot overwrite: the NuGet global
    /// packages folder, the .NET installation, and our own shadow root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shadow copying exists for exactly one reason — an analyzer or source generator that this
    /// process has loaded is locked on disk, and MSBuild then cannot overwrite it, so
    /// <c>dotnet build</c> fails on a project that generates its own analyzers. That reason applies
    /// to build output and to nothing else, and every directory a build never writes to is a
    /// directory whose analyzers can be loaded in place.
    /// </para>
    /// <para>
    /// The .NET installation is the omission that mattered. Every SDK-style project references the
    /// SDK's own analyzers under <c>&lt;dotnet&gt;/sdk/&lt;version&gt;/Sdks/Microsoft.NET.Sdk/analyzers</c>
    /// and the targeting pack's under <c>&lt;dotnet&gt;/packs/…/analyzers</c>, so every server
    /// instance copied those directories out of an installed, read-only tree and then armed a
    /// <see cref="FileSystemWatcher"/> over <c>C:\Program Files\dotnet</c> waiting for a rebuild
    /// that cannot happen.
    /// </para>
    /// <para>
    /// Measured, the copy itself is only about 15 ms — the directories are small and the OS cache is
    /// warm — so this is not a latency fix and should not be sold as one. What it removes is the
    /// standing cost: two recursive watchers per server instance pointed at the .NET installation,
    /// the temp-directory copies they keep alive, and the assembly-load-context pin that made the
    /// SDK's own analyzers unshareable between instances.
    /// </para>
    /// </remarks>
    public bool NeedsShadowCopy(string path) => !IsUnderImmutableRoot(path);

    private bool IsUnderImmutableRoot(string path)
    {
        foreach (string root in _immutableRoots)
        {
            if (IsUnder(path, root))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is inside <paramref name="root"/>, comparing whole path
    /// segments so that a sibling directory whose name merely starts the same way — a
    /// <c>packages.backup</c> beside <c>packages</c> — is not swallowed by it.
    /// </summary>
    private static bool IsUnder(string path, string root)
    {
        if (root.Length == 0)
            return false;

        string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return path.Length > normalizedRoot.Length
            && path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && (path[normalizedRoot.Length] == Path.DirectorySeparatorChar
                || path[normalizedRoot.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// The root of the .NET installation this process is running on, or <c>null</c> when it cannot
    /// be identified with confidence.
    /// </summary>
    /// <remarks>
    /// Derived from the runtime directory — <c>&lt;dotnet&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;</c>,
    /// so three levels up — and then confirmed by looking for the <c>sdk</c> or <c>packs</c>
    /// directory that the analyzers in question actually live under. A guess that cannot be
    /// confirmed returns <c>null</c> and everything keeps being shadow-copied, because copying a
    /// directory that did not need it wastes a second and failing to copy one that did breaks the
    /// user's build.
    /// </remarks>
    private static string? TryFindDotnetRoot()
    {
        try
        {
            var directory = new DirectoryInfo(
                System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());

            for (int up = 0; up < 3 && directory is not null; up++)
                directory = directory.Parent;

            if (directory is null)
                return null;

            bool looksLikeDotnetRoot =
                Directory.Exists(Path.Combine(directory.FullName, "sdk"))
                || Directory.Exists(Path.Combine(directory.FullName, "packs"));

            return looksLikeDotnetRoot ? directory.FullName : null;
        }
        catch
        {
            return null;
        }
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

        // Copy all DLLs, PDBs, and JSON metadata (e.g. .deps.json, .runtimeconfig.json).
        // A source directory that does not exist yet — a generator that has never been built has
        // no bin at all — is not an error: the shadow dir stays empty, no fingerprint baseline is
        // recorded, and the pending watcher armed below invalidates the first time a build
        // creates the directory.
        bool copiedEverything = true;
        if (Directory.Exists(sourceDir))
        {
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
                        copiedEverything = false;
                        Console.Error.WriteLine(
                            $"[ShadowCopy] Failed to copy '{Path.GetFileName(file)}': {ex.Message}");
                    }
                }
            }
        }
        else
        {
            copiedEverything = false;
        }

        _shadowDirectories[sourceDir] = shadowDir;

        // Recorded before the watcher is armed: without a baseline the first write of any kind
        // counts as a rebuild, which is the case this is meant to stop.
        //
        // Only when every file actually copied. A DLL locked mid-build is skipped above, and
        // stamping the source's fingerprint anyway claims a shadow copy we do not have — the next
        // quiet period then matches, reports "rewritten with identical content", and never
        // invalidates, so the analyzer the user just rebuilt never takes effect all session.
        if (copiedEverything && TryFingerprint(sourceDir) is { } fingerprint)
        {
            _fingerprints[sourceDir] = fingerprint;
        }
        else
        {
            // Dropped, not merely left unwritten. A rebuild records the source's new fingerprint
            // before re-copying, so an incomplete copy would leave a stamp that matches what is on
            // disk — and the next rebuild of unchanged sources produces those same bytes, is judged
            // "identical content", and never invalidates. The half-copied analyzer would then stay
            // broken for the rest of the session.
            _fingerprints.Remove(sourceDir);
        }

        EnsureWatcher(sourceDir);

        Console.Error.WriteLine($"[ShadowCopy] Copied '{sourceDir}' → '{shadowDir}'");
        return shadowDir;
    }

    private void EnsureWatcher(string directory)
    {
        if (_watchers.ContainsKey(directory) || _disposed)
            return;

        if (!Directory.Exists(directory))
        {
            EnsurePendingWatcher(directory);
            return;
        }

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

    /// <summary>
    /// Arms a stand-in watcher for a source directory that does not exist yet.
    /// <see cref="FileSystemWatcher"/> cannot watch a nonexistent path, and the never-built
    /// generator is exactly the case where a watcher matters most: the first build has to be
    /// noticed or the workspace stays wrong for the whole session. The nearest existing ancestor
    /// is watched recursively until the directory appears; then <see cref="OnPendingSourceEvent"/>
    /// swaps in the real per-directory watcher and treats the appearance itself as a rebuild.
    /// </summary>
    private void EnsurePendingWatcher(string directory)
    {
        string? ancestor = Path.GetDirectoryName(directory);
        while (ancestor is not null && !Directory.Exists(ancestor))
            ancestor = Path.GetDirectoryName(ancestor);

        if (ancestor is null)
        {
            Console.Error.WriteLine(
                $"[ShadowCopy] No existing ancestor to watch for missing directory '{directory}'.");
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(ancestor)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };

            watcher.Created += (_, _) => OnPendingSourceEvent(directory);
            watcher.Changed += (_, _) => OnPendingSourceEvent(directory);
            watcher.Renamed += (_, _) => OnPendingSourceEvent(directory);

            // Keyed under the missing directory, not the ancestor: the ContainsKey guard in
            // EnsureWatcher and the swap in OnPendingSourceEvent both address it by that key.
            _watchers[directory] = watcher;

            Console.Error.WriteLine(
                $"[ShadowCopy] '{directory}' does not exist yet; watching '{ancestor}' for it to appear.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[ShadowCopy] Failed to create pending watcher for '{directory}' via '{ancestor}': {ex.Message}");
        }
    }

    /// <summary>
    /// An event under a pending directory's watched ancestor. Once the directory actually exists,
    /// the stand-in is swapped for the real watcher and the appearance is debounced into the
    /// ordinary rebuild pipeline — with no fingerprint baseline recorded for a never-copied
    /// directory, <see cref="OnQuiet"/> always judges the first build a real change.
    /// </summary>
    private void OnPendingSourceEvent(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        lock (_lock)
        {
            if (_disposed)
                return;

            if (_watchers.TryGetValue(directory, out var standIn)
                && !string.Equals(
                    Path.TrimEndingDirectorySeparator(standIn.Path),
                    Path.TrimEndingDirectorySeparator(directory),
                    StringComparison.OrdinalIgnoreCase))
            {
                standIn.EnableRaisingEvents = false;
                standIn.Dispose();
                _watchers.Remove(directory);
                EnsureWatcher(directory);
            }
        }

        DebouncedInvalidate(directory);
    }

    private void DebouncedInvalidate(string directory)
    {
        lock (_lock)
        {
            if (_disposed) return;

            if (_debounceTimers.TryGetValue(directory, out var existing))
                existing.Dispose();

            _debounceTimers[directory] = new Timer(
                _ => OnQuiet(directory), null, RebuildQuiet, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// The directory stopped changing. Invalidate only if what it holds is actually different.
    /// </summary>
    /// <remarks>
    /// The watcher fires on writes, and a build writes an analyzer's DLL whether or not the
    /// analyzer changed — so building anything evicted every workspace that had pinned it, which
    /// meant a full MSBuild reload of the solution after every build. The compiler is deterministic
    /// for unchanged input, so comparing content tells a real rebuild from a rewrite of the same
    /// bytes, and only a real one is worth throwing a workspace away for.
    /// </remarks>
    private void OnQuiet(string directory)
    {
        string? fingerprint = TryFingerprint(directory);

        lock (_lock)
        {
            if (_disposed)
                return;

            if (fingerprint is not null
                && _fingerprints.TryGetValue(directory, out var previous)
                && previous == fingerprint)
            {
                Console.Error.WriteLine(
                    $"[ShadowCopy] '{directory}' was rewritten with identical content; keeping the workspace.");
                return;
            }

            if (fingerprint is not null)
                _fingerprints[directory] = fingerprint;
        }

        Invalidate(directory);
        Console.Error.WriteLine(
            $"[ShadowCopy] Detected rebuild in '{directory}', invalidated shadow copy.");
        AnalyzerDirectoryChanged?.Invoke(directory);
    }

    /// <summary>
    /// A content hash over the directory's DLLs, or null if it could not be read — mid-build the
    /// files are locked, and an unreadable directory must not be mistaken for an unchanged one.
    /// </summary>
    private static string? TryFingerprint(string directory)
    {
        try
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);

            foreach (string file in Directory.EnumerateFiles(directory, "*.dll")
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(file)));

                using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch
        {
            return null;
        }
    }

    // ───────────────────────── NuGet cache detection ─────────────────────────

    private static string[] BuildImmutableRoots(string nugetPackagesDir)
    {
        var roots = new List<string> { nugetPackagesDir, BaseDir };

        if (TryFindDotnetRoot() is { } dotnetRoot)
        {
            roots.Add(dotnetRoot);
            Console.Error.WriteLine(
                $"[ShadowCopy] Loading analyzers in place from the .NET installation at '{dotnetRoot}'.");
        }

        return [.. roots];
    }

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

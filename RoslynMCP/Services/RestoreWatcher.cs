using System.Collections.Concurrent;
using System.Security.Cryptography;
using RoslynMCP.Services.Packages;

namespace RoslynMCP.Services;

/// <summary>
/// Watches the output of a restore — <c>obj/project.assets.json</c>, and the packages folder for a
/// <c>packages.config</c> project — and evicts the workspaces that were loaded before it landed.
/// </summary>
/// <remarks>
/// <para>
/// A project is evaluated once and cached, and the NuGet graph it was evaluated against is an input
/// to that evaluation that nothing else in this process watches. Every other input has a watcher:
/// the project file has its timestamp checked, source files come through the editor, analyzer
/// rebuilds come through <see cref="ShadowCopyService"/>. A restore has none, so a project loaded
/// before its packages arrived stayed unresolved until the process restarted — which is what
/// "everything is red until I restart the server" is, when it happens after a <c>dotnet restore</c>
/// or a build in a terminal.
/// </para>
/// <para>
/// The restore this tool runs itself does not need a watcher: it is awaited before the load, so the
/// first evaluation already sees the graph. What needs one is every restore this process did not
/// perform — a build in a terminal, a <c>dotnet restore</c>, a package added in another editor, a
/// branch switch that changes what is referenced, or the CI script somebody ran over the tree.
/// </para>
/// <para>
/// Keyed and deduplicated by directory, so the twenty projects of one solution that all restore into
/// the same packages folder share one handle, and re-loading a project after eviction reuses the
/// watcher rather than adding a second.
/// </para>
/// </remarks>
internal static class RestoreWatcher
{
    /// <summary>Watched directory → the watcher on it. One handle per directory, never two.</summary>
    private static readonly ConcurrentDictionary<string, FileSystemWatcher> s_watchers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Project file path → the restore fingerprint it was last known to be loaded with.</summary>
    private static readonly ConcurrentDictionary<string, string> s_fingerprints =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Project file path → the debounce currently pending for it.</summary>
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> s_pending =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Watched directory → the projects whose restore output lands in it.</summary>
    private static readonly ConcurrentDictionary<string, HashSet<string>> s_projectsByDirectory =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A ceiling on handles, because the number of projects is not bounded by anything this class
    /// controls: a solution with hundreds of projects would otherwise open hundreds of directory
    /// handles for a signal that matters most on the handful somebody is actually editing.
    /// </summary>
    private const int MaxWatchedDirectories = 256;

    /// <summary>
    /// How long to wait after the last change before acting. A restore writes several files in a
    /// burst and rewrites <c>project.assets.json</c> more than once for a multi-targeted project;
    /// evicting per event would throw a workspace away in the middle of the restore that is about
    /// to make it valid.
    /// </summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(750);

    private static bool s_warnedAboutCap;

    /// <summary>
    /// Whether to watch at all. On by default; <c>ROSLYNMCP_NO_RESTORE_WATCH=1</c> turns it off.
    /// </summary>
    /// <remarks>
    /// The escape hatch for the two environments where a directory watch is the wrong trade: a tree
    /// on a network share or a container bind mount, where the notification either does not arrive
    /// or arrives for every unrelated write, and a sandbox that denies the handle outright. Turning
    /// it off costs only this behaviour — a project then picks up an outside restore when it is next
    /// reloaded, which is where it stood before.
    /// </remarks>
    private static readonly bool EnabledByDefault =
        Environment.GetEnvironmentVariable("ROSLYNMCP_NO_RESTORE_WATCH") is not ("1" or "true" or "on");

    private static bool s_enabled = EnabledByDefault;

    /// <summary>
    /// Starts watching the restore output of every project in <paramref name="projectPaths"/>.
    /// Cheap and non-blocking: the callers are load paths holding the cache lock.
    /// </summary>
    public static void WatchAll(IReadOnlyCollection<string> projectPaths)
    {
        if (!s_enabled || projectPaths.Count == 0)
            return;

        // A copy, because the caller's collection is a live view of the cache it holds a lock on.
        var snapshot = projectPaths.ToArray();

        _ = Task.Run(() =>
        {
            foreach (string project in snapshot)
            {
                try
                {
                    Watch(project);
                }
                catch (Exception ex)
                {
                    // One project whose directory cannot be watched — a deleted obj/, a path length
                    // limit, an exhausted handle quota — must not cost the others their watcher.
                    Console.Error.WriteLine(
                        $"[RestoreWatcher] Could not watch '{Path.GetFileName(project)}': {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// Starts watching one project's restore output, and records the fingerprint it is currently
    /// loaded with so a later event can tell a real change from a no-op restore.
    /// </summary>
    private static void Watch(string projectPath)
    {
        string full = Path.GetFullPath(projectPath);
        string? projectDir = Path.GetDirectoryName(full);
        if (projectDir is null)
            return;

        s_fingerprints[full] = Fingerprint(full);

        // The assets file lives in obj/, which does not exist until the first restore. Watching the
        // project directory instead — for obj/ appearing — is what makes the never-restored project
        // work, and it is the case that matters most: that project is the one showing errors.
        string objDir = Path.Combine(projectDir, "obj");
        Register(Directory.Exists(objDir) ? objDir : projectDir, full);

        if (PackagesConfigService.Uses(full))
        {
            string packagesRoot = PackagesConfigService.PackagesRootFor(full);
            if (Directory.Exists(packagesRoot))
                Register(packagesRoot, full);
        }
    }

    /// <summary>
    /// Associates <paramref name="projectPath"/> with <paramref name="directory"/> and makes sure
    /// exactly one watcher exists on it.
    /// </summary>
    private static void Register(string directory, string projectPath)
    {
        var projects = s_projectsByDirectory.GetOrAdd(
            directory, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        lock (projects)
            projects.Add(projectPath);

        if (s_watchers.ContainsKey(directory))
            return;

        if (s_watchers.Count >= MaxWatchedDirectories)
        {
            if (!s_warnedAboutCap)
            {
                s_warnedAboutCap = true;
                Console.Error.WriteLine(
                    $"[RestoreWatcher] Watching {MaxWatchedDirectories} directories; further projects " +
                    "will not notice a restore that happens outside this process until they are reloaded.");
            }
            return;
        }

        var watcher = new FileSystemWatcher(directory)
        {
            // LastWrite and Size for the assets file being rewritten, FileName and DirectoryName for
            // it (or obj/, or a package folder) appearing for the first time.
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            IncludeSubdirectories = false,
        };

        watcher.Created += (_, e) => OnChanged(directory, e.Name);
        watcher.Changed += (_, e) => OnChanged(directory, e.Name);
        watcher.Renamed += (_, e) => OnChanged(directory, e.Name);

        // An errored watcher never resumes, whether it overflowed its buffer or its directory was
        // deleted under it, so the projects behind it would be silently back to the old behaviour.
        // Rebind replaces it either way.
        watcher.Error += (_, e) =>
        {
            Console.Error.WriteLine(
                $"[RestoreWatcher] Watcher on '{directory}' failed: {e.GetException().Message}");
            Rebind(directory);
        };

        // obj/ being deleted is the watcher watching its own directory disappear.
        watcher.Deleted += (_, e) =>
        {
            OnChanged(directory, e.Name);
            if (!Directory.Exists(directory))
                Rebind(directory);
        };

        if (!s_watchers.TryAdd(directory, watcher))
        {
            // Lost the race to another caller; theirs is already watching.
            watcher.Dispose();
            return;
        }

        watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Replaces a watcher that has stopped reporting — its directory was deleted, or its buffer
    /// overflowed — so the projects behind it keep their signal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="FileSystemWatcher"/> that raises <see cref="FileSystemWatcher.Error"/> is done:
    /// it does not resume, so anything left pointing at it is watching nothing. Both ways that
    /// happens are ordinary here — <c>dotnet clean</c> and branch switches delete <c>obj</c>, and a
    /// restore over a large tree can outrun the buffer.
    /// </para>
    /// <para>
    /// Two things go wrong if the dead watcher is simply left in place. Its handle holds the deleted
    /// directory in the pending-delete state Windows uses, which is what makes recreating a
    /// same-named <c>obj</c> fail with "access is denied" — precisely what the next restore does. And
    /// the slot it occupies counts against <see cref="MaxWatchedDirectories"/> forever, so a
    /// long-lived daemon that has seen a few hundred trees come and go would stop watching new ones.
    /// </para>
    /// </remarks>
    private static void Rebind(string directory)
    {
        if (s_watchers.TryRemove(directory, out var dead))
        {
            dead.EnableRaisingEvents = false;
            dead.Dispose();
        }

        if (!s_projectsByDirectory.TryRemove(directory, out var projects))
            return;

        string[] affected;
        lock (projects)
            affected = [.. projects];

        foreach (string project in affected)
        {
            // A project whose own file is gone is gone with it: forget it rather than reopen a handle
            // on a tree that no longer exists.
            if (!File.Exists(project))
            {
                s_fingerprints.TryRemove(project, out _);
                continue;
            }

            try
            {
                // Through Watch rather than Register, because which directory is the right one may
                // have changed: with obj/ deleted it is the project's own directory, and watching
                // that is what makes obj/ reappearing visible.
                Watch(project);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[RestoreWatcher] Could not re-watch '{Path.GetFileName(project)}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Filters an event down to the things a restore actually produces, then debounces the projects
    /// behind the directory it happened in.
    /// </summary>
    private static void OnChanged(string directory, string? name)
    {
        // Everything a restore writes into obj/ except the assets file — nuget.g.props,
        // nuget.g.targets, project.nuget.cache, dgspec.json — is rewritten by a no-op restore too,
        // and none of them changes what Roslyn resolves. The directory names are for the two
        // "nothing has ever been restored here" cases.
        bool interesting = name is null
            || name.EndsWith("project.assets.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || !Path.HasExtension(name);

        if (!interesting)
            return;

        if (!s_projectsByDirectory.TryGetValue(directory, out var projects))
            return;

        string[] affected;
        lock (projects)
            affected = [.. projects];

        foreach (string project in affected)
            Debounced(project);
    }

    /// <summary>
    /// Restarts the wait for <paramref name="projectPath"/>, and evicts once it expires and the
    /// fingerprint has genuinely moved.
    /// </summary>
    private static void Debounced(string projectPath)
    {
        var cts = new CancellationTokenSource();
        if (s_pending.TryRemove(projectPath, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        s_pending[projectPath] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Debounce, cts.Token);

                string now = Fingerprint(projectPath);
                if (s_fingerprints.TryGetValue(projectPath, out var before) && before == now)
                    return; // a no-op restore, or a file in obj/ that says nothing about references

                s_fingerprints[projectPath] = now;

                if (await WorkspaceService.EvictProjectIfLoadedAsync(projectPath))
                {
                    Console.Error.WriteLine(
                        $"[RestoreWatcher] Restore output changed for " +
                        $"'{Path.GetFileName(projectPath)}'; evicted so it reloads with the new graph.");
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by a later change in the same burst.
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[RestoreWatcher] Refresh for '{Path.GetFileName(projectPath)}' failed: {ex.Message}");
            }
            finally
            {
                if (s_pending.TryGetValue(projectPath, out var current) && current == cts)
                    s_pending.TryRemove(new KeyValuePair<string, CancellationTokenSource>(projectPath, cts));
                cts.Dispose();
            }
        });
    }

    /// <summary>
    /// What this project's references were resolved from, reduced to a string that changes when they
    /// would: the content of <c>project.assets.json</c>, and the set of restored package folders for
    /// a <c>packages.config</c> project.
    /// </summary>
    /// <remarks>
    /// Content-hashed rather than timestamped because a no-op restore rewrites the assets file with
    /// byte-identical content, and reacting to that would evict — and reload — every project in the
    /// solution every time anybody ran a build.
    /// </remarks>
    internal static string Fingerprint(string projectPath)
    {
        try
        {
            string? projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            if (projectDir is null)
                return "";

            string assets = Path.Combine(projectDir, "obj", "project.assets.json");
            string assetsHash = File.Exists(assets)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assets)))
                : "none";

            if (!PackagesConfigService.Uses(projectPath))
                return assetsHash;

            // Folder names rather than contents: packages.config restore either unpacked the package
            // or did not, and hashing a packages folder is minutes of IO.
            string root = PackagesConfigService.PackagesRootFor(projectPath);
            string packages = Directory.Exists(root)
                ? string.Join(';', Directory.EnumerateDirectories(root)
                    .Select(Path.GetFileName)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                : "none";

            return $"{assetsHash}|{packages}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mid-restore: the file is being written. Returning a sentinel rather than a hash keeps
            // this from being read as "the graph changed", and the next event will fingerprint it
            // again once the writer is done.
            return "unreadable";
        }
    }

    /// <summary>
    /// Stops watching everything and forgets which projects were being watched.
    /// </summary>
    /// <remarks>
    /// Called when the whole cache is dropped — closing a solution, or the reload command — because
    /// what is being watched follows what is loaded. Without it, switching between repositories over
    /// a long-lived daemon session accumulates handles on trees nobody is looking at any more, and
    /// eventually spends the whole handle budget on them. Reloading re-registers, so nothing is lost
    /// by dropping them: a project starts being watched again the moment it is loaded again.
    /// </remarks>
    public static void StopAll()
    {
        foreach (string directory in s_watchers.Keys.ToList())
        {
            if (s_watchers.TryRemove(directory, out var watcher))
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
        }

        s_projectsByDirectory.Clear();
        s_fingerprints.Clear();

        // Pending debounces are left to expire on their own: each re-reads the fingerprint and asks
        // the workspace service whether anything is still cached, and after a full eviction the
        // answer is no, so the worst they do is wake up and find nothing to do.
    }

    // ---- Test hooks (exposed via InternalsVisibleTo) ----

    /// <summary>
    /// Stops every watcher, cancels every pending debounce, and re-arms watching.
    /// </summary>
    /// <remarks>
    /// Re-arming is the point of doing it here rather than calling <see cref="StopAll"/>: the tests
    /// that cover this class exercise the watching itself, so a machine or CI job that has set
    /// <c>ROSLYNMCP_NO_RESTORE_WATCH</c> for its own reasons must not turn them into tests of
    /// nothing. Pending debounces are cancelled rather than left to expire, so one test's eviction
    /// cannot land in the middle of the next.
    /// </remarks>
    internal static void ResetForTests()
    {
        StopAll();

        foreach (string project in s_pending.Keys.ToList())
        {
            if (s_pending.TryRemove(project, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        s_warnedAboutCap = false;
        s_enabled = EnabledByDefault;
    }

    /// <summary>Watches for the lifetime of the returned scope, whatever the environment says.</summary>
    /// <remarks>
    /// The suite turns watching off for itself — an eviction landing asynchronously in a test that
    /// was not expecting one is cross-talk, and the tests run in parallel — so the tests that cover
    /// this class have to ask for it back, and give it up again on the way out.
    /// </remarks>
    internal static IDisposable ArmForTests()
    {
        bool previous = s_enabled;
        s_enabled = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => s_enabled = previous;
    }

    /// <summary>How many directories are being watched right now.</summary>
    internal static int WatchedDirectoryCount => s_watchers.Count;

    /// <summary>Which directories are being watched right now.</summary>
    internal static IReadOnlyCollection<string> WatchedDirectoriesForTests => [.. s_watchers.Keys];

    /// <summary>Installs the watchers synchronously, so a test does not have to wait for the pool.</summary>
    internal static void WatchForTests(string projectPath) => Watch(projectPath);
}

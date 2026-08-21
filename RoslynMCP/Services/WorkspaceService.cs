using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMCP.Services;

/// <summary>What a watched file did, as far as the workspace is concerned.</summary>
public enum FileChange
{
    Created,
    Changed,
    Deleted,
}

/// <summary>
/// Whether a watched-file change could be applied to the live workspace, and whether doing so
/// actually altered anything. The distinction matters: a change nothing was done about must not
/// tell the editor to re-pull the workspace, and one that cannot be applied in place has to fall
/// back to eviction rather than being silently dropped.
/// </summary>
public enum FileSyncResult
{
    /// <summary>Applied in place; the editor should refresh.</summary>
    Applied,

    /// <summary>Nothing needed doing — no reload, and nothing for the editor to re-pull.</summary>
    NothingToDo,

    /// <summary>Only MSBuild can answer; the caller should evict.</summary>
    CannotApply,
}

/// <summary>
/// Manages MSBuildWorkspace creation, project discovery, document lookup, and
/// workspace/project caching with configurable idle eviction.
/// </summary>
internal static class WorkspaceService
{
    /// <summary>
    /// Raised whenever the set of loaded projects changes — a workspace loaded, projects added to
    /// one, a workspace thrown away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The editor caches what it was told. When the project set moves underneath it, everything
    /// derived from the old set is wrong and the client has no way to find out: it did not ask for
    /// the change and nothing in LSP tells it. The one gesture that widens the workspace is
    /// find-references, which is why "go to reference works but the code lens beside it still says
    /// nothing" — the search pulled in the projects, and the lens was never asked again.
    /// </para>
    /// <para>
    /// A delegate rather than a call into <c>LspSessionRegistry</c>, matching
    /// <see cref="ProgressReporter.Factory"/> and <see cref="ServiceLog.Sink"/>: this service is
    /// used by hosts that have no LSP layer at all, and it must not depend on one.
    /// </para>
    /// <para>
    /// Raised from here rather than from the handlers that trigger loads. That was the original
    /// design and it is what produced the gaps — six handlers refresh, two sibling branches doing
    /// the same kind of work do not, and background loads never did. This is the only place that
    /// knows the set actually moved.
    /// </para>
    /// </remarks>
    public static Action? ProjectSetChanged;

    private static void NotifyProjectSetChanged()
    {
        // Before the subscriber, and unconditionally: the answers this drops were derived from the
        // set that just moved, and a subscriber that throws must not leave them behind.
        s_containingProject.Clear();

        try { ProjectSetChanged?.Invoke(); }
        catch { /* an editor that cannot be told is not a reason to fail the load */ }
    }

    /// <summary>
    /// Normalized file path → the project that compiles it, or <see langword="null"/> for "none
    /// does". Filled by <see cref="FindContainingProjectAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lookup it memoizes is a directory walk: <c>GetFiles("*.csproj")</c> at every level from
    /// the file up towards the drive root, and for each candidate found, opening that project and
    /// searching it for the file. It sits under <c>LspDocumentResolver.ResolveAsync</c>, which is
    /// the first line of hover, completion, signature help, code lens, semantic tokens, folding,
    /// inlay hints, formatting, rename and every navigation request — so the walk ran several times
    /// per keystroke, and for a file that belongs to no project it ran all the way to the root
    /// every time.
    /// </para>
    /// <para>
    /// Only the path→project mapping is kept, deliberately, and never the <see cref="Document"/>.
    /// A document pins the whole <see cref="Solution"/> snapshot it came from, so a memo over one
    /// would hand out yesterday's text after every keystroke; which project owns a file changes
    /// only when the project set or its file list does, which is exactly what
    /// <see cref="NotifyProjectSetChanged"/> announces.
    /// </para>
    /// <para>
    /// Cleared whole rather than evicted per entry: every entry costs the same to rebuild, so
    /// there is nothing for an LRU to protect, and the signal that invalidates one plausibly
    /// invalidates all of them.
    /// </para>
    /// </remarks>
    private static readonly ConcurrentDictionary<string, string> s_containingProject =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ceiling on <see cref="s_containingProject"/>. Reached only by a caller asking about paths
    /// that are not a solution's files — the entries are one short string each, and a solution has
    /// as many as it has documents.
    /// </summary>
    private const int MaxContainingProjectEntries = 20_000;

    /// <summary>
    /// How many times the directory walk actually ran. Exposed for tests, which assert on what was
    /// <em>not</em> walked — the resolved document is identical either way, so counting the work is
    /// the only way to pin the memo.
    /// </summary>
    internal static long ProjectSearches;

    /// <summary>Zeroes <see cref="ProjectSearches"/> and forgets every owner, for a test that
    /// needs a cold measurement.</summary>
    internal static void ResetContainingProjectMemo()
    {
        s_containingProject.Clear();
        Interlocked.Exchange(ref ProjectSearches, 0);
    }

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EvictionInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Hard ceiling on <see cref="MSBuildWorkspace.OpenProjectAsync"/>. Override via
    /// <c>ROSLYNMCP_OPEN_PROJECT_TIMEOUT_SECONDS</c> environment variable. Default 300s
    /// is long enough for a cold WebForms project (BuildHost-net472 spin-up plus full
    /// MSBuild evaluation) but short enough that a wedged BuildHost surfaces as an
    /// error rather than an indefinite hang.
    /// </summary>
    private static readonly TimeSpan OpenProjectTimeout = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("ROSLYNMCP_OPEN_PROJECT_TIMEOUT_SECONDS"), out var s) && s > 0
            ? s : 300);

    /// <summary>
    /// The cached workspaces. Concurrent so a reader that only wants to look at an entry does not
    /// have to take <see cref="s_cacheLock"/>, which a load holds while it caches its result — that
    /// made <see cref="TryGetMostRecentSolution"/> block whichever request thread called it for as
    /// long as the load took. The lock still guards mutations, because those maintain invariants
    /// across this dictionary and the two reverse indexes together.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedWorkspaceEntry> s_cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<(Workspace, Project)>> s_inflight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim s_cacheLock = new(1, 1);
    private static readonly Timer s_evictionTimer;

    /// <summary>
    /// Reverse index: analyzer / source-generator source directory → set of cached
    /// project paths whose workspace pinned an ALC for that directory. Used to evict
    /// affected workspaces when <see cref="ShadowCopyManager"/> reports a rebuild.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> s_dirToProjects =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reverse index: normalized project (.csproj) path — or a decompiled manifest path —
    /// → the <see cref="s_cache"/> keys of the workspaces that can serve it. One solution
    /// workspace serves all its member projects, so this maps every project in a loaded
    /// solution's transitive closure to that entry. This is what gives both solution-wide dedup
    /// and reuse-by-membership for loose projects.
    ///
    /// A set, not one key: the same project genuinely belongs to more than one entry. Two
    /// solutions can both include it, and Roslyn pulls a referenced project into whichever
    /// workspace asked for its consumer. While this was one-to-one, the last registration won and
    /// every other entry holding that project became unreachable — so invalidating it evicted one
    /// workspace and left the others compiling against the state it had before.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> s_projectToCacheKey =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maximum number of cached workspace entries. When the cache exceeds this after the
    /// idle sweep, the least-recently-used entries are evicted. Each entry now holds a whole
    /// solution, so a small cap suffices. Override via <c>ROSLYNMCP_MAX_WORKSPACES</c>.
    /// </summary>
    internal static int MaxCachedWorkspaces { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("ROSLYNMCP_MAX_WORKSPACES"), out var mw) && mw > 0
            ? mw : 4;

    /// <summary>
    /// Test seam, not a feature: counts how many times <see cref="EnsureProjectLoadedAsync"/> has
    /// actually pulled a new project into an already-cached workspace, for the life of the
    /// process. Nothing in production reads it. It exists so an out-of-process test driving the
    /// real <c>--lsp</c> server can assert that an incidental gesture — a code lens resolving as
    /// the editor scrolls, a hover, a completion — never silently expands the loaded workspace,
    /// which is exactly the mechanism a wall of phantom CS0012s on a large solution traced back to.
    /// Exposed over LSP by <c>roslynSense/diagnosticsCounters</c>.
    /// </summary>
    public static int IncrementalLoadCount;

    /// <summary>
    /// Reflection handle for <c>Workspace.SetCurrentSolution(Solution)</c> (protected,
    /// instance, returns Solution). Used to atomically swap a workspace's current
    /// solution to the analyzer-ref-rebound copy WITHOUT going through
    /// <see cref="Workspace.TryApplyChanges"/> — the latter would round-trip the new
    /// analyzer references back to the .csproj file on disk.
    /// </summary>
    private static readonly MethodInfo? s_setCurrentSolutionMethod = typeof(Workspace)
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .FirstOrDefault(m => m.Name == "SetCurrentSolution"
            && m.ReturnType == typeof(Solution)
            && m.GetParameters() is { Length: 1 } ps
            && ps[0].ParameterType == typeof(Solution));

    /// <summary>
    /// Indicates whether legacy .NET Framework projects (non-SDK-style .csproj) are supported.
    /// True when a Visual Studio install with the MSBuild component and the .NET Framework
    /// targeting packs are both present.
    /// </summary>
    /// <remarks>
    /// Answered on first ask, not at startup. Computing it means shelling out to <c>vswhere</c> —
    /// about 210 ms of subprocess — and it used to happen inside the static constructor, so every
    /// server start paid it, before MSBuild was even registered, to answer a question that only
    /// matters when somebody opens a non-SDK-style project. Worse, everything the constructor gates
    /// queued behind it: the background NuGet restore and the MEF warm-up both start after
    /// <c>BindSolution</c> touches this class, so a probe for Visual Studio delayed the first real
    /// project load on solutions that have no legacy project anywhere in them.
    /// </remarks>
    public static bool IsLegacyProjectSupported => s_legacyMsBuildDir.Value is not null;

    /// <summary>
    /// The Visual Studio MSBuild <c>Bin</c> directory legacy projects are loaded through, or
    /// <c>null</c> when this machine has none.
    /// </summary>
    /// <remarks>
    /// Exposed for <see cref="RestoreService"/>, which has to restore non-SDK projects with the same
    /// engine that evaluates them: the .NET SDK's MSBuild cannot resolve the
    /// <c>$(MSBuildExtensionsPath)</c> imports a legacy web project is built from, so
    /// <c>dotnet restore</c> answers with an evaluation failure rather than a restore.
    /// </remarks>
    public static string? LegacyMsBuildDirectory => s_legacyMsBuildDir.Value;

    /// <summary>
    /// The MSBuild bin directory a legacy project's BuildHost needs, or <c>null</c> when this
    /// machine cannot build one. Probed via <c>vswhere</c> because MSBuildLocator's VS Setup COM
    /// discovery often fails in the .NET 10 host even when VS is installed.
    /// </summary>
    private static readonly Lazy<string?> s_legacyMsBuildDir = new(() =>
    {
        // Targeting packs first: without them the VS probe cannot change the answer, and this is a
        // directory existence check against a subprocess launch.
        string refAssembliesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Reference Assemblies", "Microsoft", "Framework", ".NETFramework");

        string? directory = Directory.Exists(refAssembliesPath)
            ? FindLegacyCompatibleMsBuildDirViaVsWhere()
            : null;

        Console.Error.WriteLine(directory is not null
            ? $"[WorkspaceService] Legacy .NET Framework projects supported via BuildHost (MSBuild at '{directory}')."
            : "[WorkspaceService] Legacy .NET Framework projects NOT supported (no VS install with " +
              "the MSBuild component, or no .NET Framework targeting packs).");

        return directory;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Triggers the static initializer (MSBuildLocator registration). Call this from test
    /// code that creates a bare <see cref="MSBuildWorkspace"/> instead of going through
    /// <see cref="GetOrOpenProjectAsync"/>, so MSBuild is registered before workspace creation.
    /// </summary>
    public static void EnsureRegistered() { }

    /// <summary>
    /// The shared MEF composition, for the few places that need a workspace of their own rather
    /// than one from the cache. Lives on <see cref="HostComposition"/> so warming it does not run
    /// this class's static initializer — and with it the MSBuild registration the composition
    /// never needed.
    /// </summary>
    internal static Microsoft.CodeAnalysis.Host.HostServices HostServices =>
        HostComposition.HostServices;

    /// <inheritdoc cref="HostComposition.WarmInBackground"/>
    public static void WarmHostServicesInBackground() => HostComposition.WarmInBackground();

    private static Dictionary<string, string> CreateDefaultProperties() => new()
    {
        { "AlwaysUseNETSdkDefaults", "true" },
        { "DesignTimeBuild", "true" }
    };

    private static Dictionary<string, string> CreateLegacyProperties() => new()
    {
        { "DesignTimeBuild", "true" }
    };

    /// <summary>
    /// One-time static initializer that registers a Visual Studio MSBuild instance
    /// (if available), ensures the C# Roslyn assembly is loaded, and starts the idle
    /// eviction timer.
    /// </summary>
    static WorkspaceService()
    {
        RunwayTrace.Mark("cctor start");
        PatchBuildHostBindingRedirects();
        RunwayTrace.Mark("binding redirects patched");
        TryRegisterVisualStudioMSBuild();
        RunwayTrace.Mark("locator registered");
        // Warmed, not required: nothing in this initializer consumes the C# syntax factories,
        // and the callers gated on registration shouldn't wait out a Roslyn static init they
        // may never touch. Fired after the locator so this can't pull in an MSBuild type early.
        _ = Task.Run(() => RuntimeHelpers.RunClassConstructor(typeof(CSharpSyntaxTree).TypeHandle));
        s_evictionTimer = new Timer(EvictExpiredEntries, null, EvictionInterval, EvictionInterval);
        ShadowCopyService.Instance.AnalyzerDirectoryChanged += OnAnalyzerDirectoryChanged;
        DecompiledSourceService.CleanupOrphanedTempDirs();
        RunwayTrace.Mark("cctor end");
    }

    /// <summary>
    /// Roslyn's BuildHost-net472 subprocess loads MSBuild via MSBuildLocator, which on
    /// .NET Framework picks the highest installed Visual Studio version. With VS 2026
    /// (MSBuild 18) installed, the BuildHost picks v18 — and v18 references newer versions
    /// of System.Collections.Immutable, System.Memory, System.Threading.Tasks.Extensions,
    /// Microsoft.Bcl.AsyncInterfaces and System.Text.Json than the BuildHost ships.
    /// The original BuildHost.exe.config caps redirects (e.g. 0.0.0.0-9.0.0.0) so the
    /// CLR cannot satisfy v18's requested versions (e.g. 9.0.0.11), causing a
    /// TypeInitializationException for Microsoft.Build.Shared.XMakeElements.
    ///
    /// Fix: rewrite the redirect upper-bound to a very high value so any version
    /// MSBuild 18 (or future MSBuild) requests is satisfied by the BuildHost-shipped DLLs.
    /// This is idempotent — if already patched, nothing happens.
    /// </summary>
    private static void PatchBuildHostBindingRedirects()
    {
        try
        {
            var configPath = LocateBuildHostConfig();
            if (configPath is null || !File.Exists(configPath))
                return;

            var original = File.ReadAllText(configPath);

            // Look for any redirect with a non-99 upper bound; if all are already widened, skip.
            // Replacement: oldVersion="0.0.0.0-X.Y.Z" -> oldVersion="0.0.0.0-99.0.0.0"
            var pattern = new System.Text.RegularExpressions.Regex(
                "oldVersion=\"0\\.0\\.0\\.0-(?!99\\.0\\.0\\.0\")[0-9.]+\"");

            if (!pattern.IsMatch(original))
                return;

            var patched = pattern.Replace(original, "oldVersion=\"0.0.0.0-99.0.0.0\"");
            File.WriteAllText(configPath, patched);

            Console.Error.WriteLine(
                $"[WorkspaceService] Patched BuildHost binding redirects at '{configPath}' for MSBuild 18 compatibility.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[WorkspaceService] Failed to patch BuildHost binding redirects: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the absolute path to the BuildHost-net472 .exe.config, located alongside
    /// the executing assembly under <c>BuildHost-net472/</c>.
    /// </summary>
    private static string? LocateBuildHostConfig()
    {
        var asmDir = Path.GetDirectoryName(typeof(WorkspaceService).Assembly.Location);
        if (string.IsNullOrEmpty(asmDir))
            return null;

        return Path.Combine(asmDir, "BuildHost-net472",
            "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.exe.config");
    }

    /// <summary>
    /// Attempts to find and register a Visual Studio or Build Tools MSBuild instance.
    /// Falls back silently to the SDK-bundled MSBuild when none is found.
    /// </summary>
    private static void TryRegisterVisualStudioMSBuild()
    {
        try
        {
            if (!MSBuildLocator.CanRegister)
                return;

            var instances = MSBuildLocator.QueryVisualStudioInstances().ToList();

            // The parent process is .NET 10 and only ever loads SDK-style projects in-process
            // (legacy .NET Framework projects are loaded by the BuildHost-net472 subprocess,
            // which does its OWN MSBuildLocator discovery). So we MUST register the .NET SDK
            // MSBuild here — registering a VS MSBuild bin path in this process would hijack
            // assembly resolution (e.g. System.Text.Json) and break the SDK resolver.
            var dotnetSdkInstance = instances
                .Where(i => i.DiscoveryType == DiscoveryType.DotNetSdk)
                .OrderByDescending(i => i.Version)
                .FirstOrDefault();

            if (dotnetSdkInstance is not null)
            {
                MSBuildLocator.RegisterInstance(dotnetSdkInstance);
                Console.Error.WriteLine(
                    $"[WorkspaceService] Registered MSBuild from '{dotnetSdkInstance.Name}' " +
                    $"v{dotnetSdkInstance.Version} at '{dotnetSdkInstance.MSBuildPath}'.");
                return;
            }

            if (instances.Count == 0)
                return;

            // No DotNetSdk instance found — fall back to whatever is available so the workspace
            // at least works for legacy projects.
            var instance = instances.OrderByDescending(i => i.Version).First();
            MSBuildLocator.RegisterInstance(instance);
            Console.Error.WriteLine(
                $"[WorkspaceService] Registered MSBuild from '{instance.Name}' v{instance.Version} at '{instance.MSBuildPath}' (no .NET SDK MSBuild instance found).");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[WorkspaceService] MSBuild Locator failed, using SDK-bundled MSBuild: {ex.Message}");
        }
    }

    /// <summary>
    /// Uses vswhere to find the MSBuild bin directory of a VS installation with the MSBuild
    /// component (VS 2017 or later, including VS 2026 / MSBuild 18). Returns the directory
    /// path (containing MSBuild.dll), or null if not found.
    /// </summary>
    private static string? FindLegacyCompatibleMsBuildDirViaVsWhere()
    {
        var vswherePath = MsBuildLocator.EnsureVsWhere();

        if (vswherePath is null)
            return null;

        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = vswherePath,
                    // -products * includes BuildTools; no version filter — newest wins.
                    Arguments = "-products * -requires Microsoft.Component.MSBuild " +
                                "-find MSBuild\\**\\Bin\\MSBuild.exe -latest",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            foreach (var line in output.Split('\n'))
            {
                var exePath = line.Trim();
                if (File.Exists(exePath))
                    return Path.GetDirectoryName(exePath);
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Creates a configured MSBuildWorkspace.
    /// Workspace failure diagnostics are written to <paramref name="diagnosticWriter"/>
    /// (defaults to <see cref="Console.Error"/> when <c>null</c>).
    /// The caller is responsible for disposing the returned workspace.
    /// Prefer <see cref="GetOrOpenProjectAsync"/> for cached access.
    /// </summary>
    public static MSBuildWorkspace CreateWorkspace(
        TextWriter? diagnosticWriter = null, bool isLegacy = false,
        IReadOnlyDictionary<string, string>? extraProperties = null,
        bool lite = false)
    {
        var properties = isLegacy ? CreateLegacyProperties() : CreateDefaultProperties();
        if (extraProperties is not null)
            foreach (var (key, value) in extraProperties)
                properties[key] = value;

        // A lite workspace runs on the workspaces-only composition: enough to drive BuildHost
        // evaluations, ready long before the full feature composition, and only ever asked for
        // by callers that dispose the workspace without serving a semantic request from it.
        var workspace = MSBuildWorkspace.Create(properties,
            lite ? HostComposition.LiteHostServices : HostComposition.HostServices);

        workspace.RegisterWorkspaceFailedHandler(args =>
        {
            var writer = diagnosticWriter ?? Console.Error;
            writer.WriteLine($"Workspace warning: {args.Diagnostic.Message}");
        }, null);

        return workspace;
    }

    /// <summary>
    /// Returns a cached workspace and project for the given project path.
    /// If <paramref name="targetFilePath"/> is supplied and the file was modified after
    /// the cache was populated, an immutable project snapshot with refreshed document
    /// text is returned. The workspace's internal solution is not modified.
    /// </summary>
    public static async Task<(Workspace Workspace, Project Project)> GetOrOpenProjectAsync(
        string projectPath, string? targetFilePath = null, TextWriter? diagnosticWriter = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedPath = Path.GetFullPath(projectPath);
        TaskCompletionSource<(Workspace, Project)>? ourTcs = null;

        bool isDecompile = DecompiledSourceService.IsGeneratedProjectPath(normalizedPath);

        // Owning-solution resolution is disk I/O (dir walk + .sln parse), so it is deferred to
        // the first cache MISS and memoized — cache hits never pay for it. When the project
        // belongs to a multi-project solution we open the whole solution ONCE into a single
        // workspace (keyed by the .sln path) instead of one workspace per .csproj. `loadKey`
        // is both the s_inflight key (so sibling-project requests coalesce onto one load) and
        // the s_cache key.
        string? solutionPath = null;
        string? loadKey = null;
        bool ownerResolved = false;
        bool incrementalAttempted = false;

        while (true)
        {
            Task<(Workspace, Project)>? inflightTask = null;
            CachedWorkspaceEntry? incrementalEntry = null;

            await s_cacheLock.WaitAsync(cancellationToken);
            try
            {
                if (TryGetValidCachedEntryLocked(normalizedPath, out var cachedEntry))
                    return CreateProjectSnapshot(cachedEntry!, normalizedPath, targetFilePath);

                // Cache miss → resolve the owning solution once (brief I/O, miss-path only).
                if (!ownerResolved)
                {
                    if (!isDecompile)
                        solutionPath = TryFindOwnerSolutionKey(normalizedPath);
                    loadKey = solutionPath ?? normalizedPath;
                    ownerResolved = true;
                }

                if (!incrementalAttempted && solutionPath is not null
                    && s_cache.TryGetValue(solutionPath, out var slnEntry)
                    && slnEntry.Workspace is MSBuildWorkspace)
                {
                    // The owning-solution workspace is already cached but doesn't hold this
                    // project yet — add it incrementally (reusing loaded references) rather than
                    // opening a second workspace. Done outside the lock; we then loop back and the
                    // next cache check returns the snapshot.
                    incrementalEntry = slnEntry;
                }
                else if (s_inflight.TryGetValue(loadKey!, out inflightTask))
                {
                    // Another caller is loading this solution/project — wait for it outside the lock
                }
                else
                {
                    // We are the loader — register ourselves and break out to do the load
                    ourTcs = new TaskCompletionSource<(Workspace, Project)>(TaskCreationOptions.RunContinuationsAsynchronously);
                    s_inflight[loadKey!] = ourTcs.Task;
                    break;
                }
            }
            finally
            {
                s_cacheLock.Release();
            }

            if (incrementalEntry is not null)
            {
                incrementalAttempted = true;
                try
                {
                    await EnsureProjectLoadedAsync(incrementalEntry, normalizedPath, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[WorkspaceService] Incremental load of '{Path.GetFileName(normalizedPath)}' into " +
                        $"'{Path.GetFileName(solutionPath!)}' failed ({ex.Message}); falling back to a standalone workspace.");
                }

                // If the project still isn't resolvable (load failure, or it isn't actually a
                // member of that solution), stop targeting the solution workspace and fall back
                // to a standalone per-project load on the next iteration.
                bool nowLoaded;
                await s_cacheLock.WaitAsync(cancellationToken);
                try { nowLoaded = TryGetValidCachedEntryLocked(normalizedPath, out _); }
                finally { s_cacheLock.Release(); }

                if (!nowLoaded)
                {
                    solutionPath = null;
                    loadKey = normalizedPath;
                }
                continue;
            }

            // Wait for the in-flight load to complete, then loop back to check cache.
            // Use WaitAsync so the caller's cancellation token is respected — without
            // it, a waiter would be stuck until the (potentially minutes-long) load
            // finishes even after its own token is cancelled.
            try
            {
                await inflightTask!.WaitAsync(cancellationToken);
            }
            catch
            {
                // In-flight load failed or our token was cancelled;
                // loop back to try again (we may become the loader,
                // or the next WaitAsync on the semaphore will throw
                // OperationCanceledException and we'll exit cleanly).
            }
        }

        // At this point we are the designated loader with ourTcs registered in s_inflight.
        // The s_cacheLock is NOT held.

        Workspace workspace;
        Project openedProject;
        ShadowCopyAnalyzerAssemblyLoader? shadowLoader = null;
        bool notifyLoaded = false;
        HashSet<string>? shadowDirs = null;
        string? decompileTempDir = null;
        // The s_cache key == loadKey: the .sln path in solution-mode (so siblings share and
        // extend one workspace), or the .csproj path for a loose project or after an incremental
        // add failed and the loop fell back to a standalone load. loadKey is non-null here: we
        // only reach the loader after resolving it and breaking out.
        string cacheKey = loadKey!;

        await using var progress = await ProgressReporter.BeginAsync(
            $"Loading {Path.GetFileNameWithoutExtension(cacheKey)}", cancellationToken);

        try
        {
            if (isDecompile)
            {
                (workspace, openedProject, decompileTempDir) = await DecompiledSourceService.OpenProjectAsync(
                    normalizedPath,
                    cancellationToken);
            }
            else
            {
                var isLegacy = PathHelper.RequiresMsBuild(normalizedPath);
                if (isLegacy && !IsLegacyProjectSupported)
                    throw new NotSupportedException(
                        "Legacy .NET Framework projects require a Visual Studio install with the MSBuild " +
                        "component (VS 2017+ or Build Tools 2017+) and the .NET Framework targeting packs. " +
                        "Install 'Visual Studio Build Tools' and relaunch the MCP server.");
                var msbuildWorkspace = CreateWorkspace(diagnosticWriter, isLegacy);

                // Persistent index storage opens only when Solution.FilePath is set, and this
                // codebase never calls OpenSolutionAsync — projects are loaded individually — so
                // without this every index (SyntaxTreeIndex, SymbolTreeInfo, ...) is rebuilt each
                // daemon start. Must happen before the first project lands in the workspace, and
                // only for the bound solution's own workspace: SQLite holds the DB exclusively,
                // so a second workspace given the same path would silently degrade to NoOp.
                //
                // Opt-in while it earns trust: setting the FilePath switches every index read and
                // write in the process onto the SQLite storage service and its native library —
                // a crash or wedge there takes the whole load down, which is a daemon that never
                // answers and an editor that says the workspace is still loading.
                if (Environment.GetEnvironmentVariable("ROSLYN_SENSE_PERSISTENT_INDEX") == "1"
                    && BoundSolutionPath is { Length: > 0 } boundSolution
                    && string.Equals(cacheKey, Path.GetFullPath(boundSolution),
                        StringComparison.OrdinalIgnoreCase))
                {
                    msbuildWorkspace.OnSolutionAdded(SolutionInfo.Create(
                        SolutionId.CreateNewId(), VersionStamp.Create(), filePath: boundSolution));
                }

                var phases = new LoadPhaseTimings();
                try
                {
                    progress.Report("Restoring packages");
                    phases.Start();
                    await RestoreService.EnsureRestoredAsync(normalizedPath, cancellationToken);
                    // Same contract as the restore above: a subprocess the evaluation depends on,
                    // run before the load so a never-built source generator exists by the time the
                    // analyzer references resolve to it. No-op when every generator has output.
                    await GeneratorBuildService.EnsureGeneratorsBuiltAsync(
                        normalizedPath, cancellationToken, msg => progress.Report(msg));
                    phases.Mark(ref phases.RestoreMs);
                    progress.Report($"Opening {Path.GetFileName(normalizedPath)}");

                    // Hard ceiling on OpenProjectAsync so a wedged BuildHost-net472 subprocess
                    // (common with legacy WebForms + source generators) cannot freeze the
                    // entire MCP server. The token is also passed to OpenProjectAsync so a
                    // well-behaved load can short-circuit; WaitAsync is the belt-and-braces
                    // backstop for native hangs that ignore the token.
                    using var openCts = new CancellationTokenSource(OpenProjectTimeout);
                    using var openLinked = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, openCts.Token);

                    try
                    {
                        // Only the requested project (Roslyn additionally pulls in its transitive
                        // ProjectReferences). When the project belongs to a multi-project solution,
                        // cacheKey is the .sln path, so sibling and referencing projects requested
                        // later are added to THIS same workspace incrementally — sharing
                        // already-loaded references — instead of loading the whole solution up front.
                        //
                        // Deliberately still OpenProjectAsync, even though the shared BuildHost
                        // evaluates the same project about a second faster — measured, on this very
                        // path: 1,429 ms through a fresh host against 276 ms through a warm one.
                        //
                        // Routing the seed through the pool passes in isolation and fails under a
                        // parallel test run: seven tests covering rename, file operations, document
                        // formatting and completion break together, and only when other tests are
                        // loading projects at the same time. UpdateReferencesAfterAdd — the obvious
                        // candidate, and the thing OpenProjectAsync does that a bare OnProjectAdded
                        // does not — is not the difference; adding it changed nothing.
                        //
                        // Something about a first, workspace-creating load is not reproduced by
                        // adding project models to an empty workspace, and until that is understood
                        // the second is not worth a second. The batch path below does use the pool:
                        // it adds to a workspace this call already created, which is a different and
                        // demonstrably safe situation.
                        var solutionForMap = msbuildWorkspace.CurrentSolution;
                        var seedInfos = await SharedBuildHost.LoadAsync(
                            msbuildWorkspace, msbuildWorkspace.Properties, [normalizedPath],
                            () => ProjectMap.Create(solutionForMap), openLinked.Token);
                        await AddProjectsRewireAndHealAsync(msbuildWorkspace, seedInfos, openLinked.Token);
                        openedProject = msbuildWorkspace.CurrentSolution.Projects.First(p =>
                            p.FilePath is { Length: > 0 } fp
                            && string.Equals(Path.GetFullPath(fp), normalizedPath, StringComparison.OrdinalIgnoreCase));
                    }
                    catch (TimeoutException tex)
                    {
                        throw new TimeoutException(
                            $"Opening '{Path.GetFileName(normalizedPath)}' timed out after " +
                            $"{OpenProjectTimeout.TotalSeconds:F0}s. The MSBuild BuildHost subprocess may be wedged " +
                            "(legacy WebForms projects with source generators are a frequent cause). " +
                            "Disposing the workspace will kill the BuildHost; the next attempt should succeed.",
                            tex);
                    }

                    phases.Mark(ref phases.OpenMs);

                    var openedId = openedProject.Id;
                    (shadowLoader, shadowDirs) = ApplyPostOpenPipeline(
                        msbuildWorkspace, newProjects: null, existingLoader: null);
                    phases.Mark(ref phases.PipelineMs);
                    openedProject = msbuildWorkspace.CurrentSolution.GetProject(openedId)!;

                    Console.Error.WriteLine(
                        $"[WorkspaceService] Seed-loaded '{Path.GetFileName(normalizedPath)}' " +
                        $"({msbuildWorkspace.CurrentSolution.ProjectIds.Count} project(s) in closure). {phases}");

                    workspace = msbuildWorkspace;
                }
                catch
                {
                    shadowLoader?.Dispose();
                    msbuildWorkspace.Dispose();
                    throw;
                }
            }
        }
        catch (DllNotFoundException dllEx)
        {
            // clr.dll (or another native DLL) could not be loaded. This typically means
            // .NET Framework is not installed, or the VS Setup COM component is broken.
            await RemoveInflightAndSignal(loadKey!, ourTcs!, dllEx);
            if (decompileTempDir is not null) DecompiledSourceService.TryDeleteTempDir(decompileTempDir);
            throw new PlatformNotSupportedException(
                $"Opening '{Path.GetFileName(normalizedPath)}' requires a native DLL that could not be loaded " +
                $"({dllEx.Message}). For legacy .NET Framework projects, ensure .NET Framework 4.7.2 or later " +
                "is installed and Visual Studio Build Tools are present.", dllEx);
        }
        catch (Exception ex)
        {
            await RemoveInflightAndSignal(loadKey!, ourTcs!, ex);
            if (decompileTempDir is not null) DecompiledSourceService.TryDeleteTempDir(decompileTempDir);
            throw;
        }

        // Cache the result and signal waiters.
        // TCS is signaled AFTER releasing the lock to avoid holding the lock
        // while continuations run (even with RunContinuationsAsynchronously).
        (Workspace, Project) result;
        try
        {
            await s_cacheLock.WaitAsync(cancellationToken);
        }
        catch
        {
            workspace.Dispose();
            if (decompileTempDir is not null) DecompiledSourceService.TryDeleteTempDir(decompileTempDir);
            await RemoveInflightAndSignal(loadKey!, ourTcs!);
            throw;
        }

        try
        {
            s_inflight.Remove(loadKey!);

            if (TryGetValidCachedEntryLocked(normalizedPath, out var cachedEntry))
            {
                // A concurrent loader already cached a workspace that serves this project.
                shadowLoader?.Dispose();
                workspace.Dispose();
                if (decompileTempDir is not null) DecompiledSourceService.TryDeleteTempDir(decompileTempDir);
                result = CreateProjectSnapshot(cachedEntry!, normalizedPath, targetFilePath);
            }
            else
            {
                string[]? tempDirs = decompileTempDir is not null ? [decompileTempDir] : null;
                var newEntry = new CachedWorkspaceEntry(
                    cacheKey, workspace, openedProject.Id, shadowLoader, shadowDirs, tempDirs);
                s_cache[cacheKey] = newEntry;
                notifyLoaded = true;
                RegisterProjectMappingsLocked(cacheKey, normalizedPath, workspace);
                RegisterShadowDirsLocked(cacheKey, shadowDirs);
                Console.Error.WriteLine(
                    $"[WorkspaceService] Cached workspace for '{cacheKey}' ({newEntry.ProjectIds.Count} project(s)).");

                // Said out loud, through ServiceLog, and only when it is a re-load. A first load is
                // what opening a solution costs and nobody needs telling; a *second* one is the
                // thing that reads as "it reloaded everything again" with no visible cause, and the
                // cause was recorded when the unload happened rather than guessed at now.
                if (LastEvictionOf(cacheKey) is { } previous)
                {
                    ServiceLog.Warn(
                        $"Re-loaded '{Path.GetFileNameWithoutExtension(cacheKey)}' "
                        + $"({newEntry.ProjectIds.Count} project(s)) because "
                        + $"{Requested(normalizedPath, targetFilePath)} needed it. It was unloaded "
                        + $"{(DateTime.UtcNow - previous.When).TotalSeconds:F0}s ago: {previous.Reason}.",
                        key: $"reload:{cacheKey}");
                }

                result = CreateProjectSnapshot(newEntry, normalizedPath, targetFilePath);
            }
        }
        catch (Exception ex)
        {
            // CreateProjectSnapshot failed — signal waiters so they don't hang
            ourTcs!.TrySetException(ex);
            throw;
        }
        finally
        {
            s_cacheLock.Release();
        }

        ourTcs!.TrySetResult(result);

        // After the waiters are released and the lock is gone: the client re-pulls in response,
        // and re-pulling must not queue behind the load it is reacting to.
        if (notifyLoaded)
        {
            ReconcileOpenBuffersAfterLoad();
            NotifyProjectSetChanged();
        }

        return result;
    }

    /// <summary>
    /// The first frame outside this file, which is the feature that wanted the project.
    /// </summary>
    private static string LoadOrigin()
    {
        try
        {
            var trace = new System.Diagnostics.StackTrace(fNeedFileInfo: false);

            for (int i = 0; i < trace.FrameCount; i++)
            {
                if (trace.GetFrame(i)?.GetMethod() is not { DeclaringType: { } type } method)
                    continue;

                string name = type.FullName ?? type.Name;
                if (name.StartsWith("RoslynMCP", StringComparison.Ordinal)
                    && !name.StartsWith("RoslynMCP.Services.WorkspaceService", StringComparison.Ordinal))
                {
                    return $"{type.Name}.{method.Name}";
                }
            }
        }
        catch
        {
            // A diagnostic that throws is worse than one that says nothing.
        }

        return "an unknown caller";
    }

    /// <summary>
    /// Removes the in-flight entry for <paramref name="normalizedPath"/> under the
    /// cache lock and then signals the TCS so waiters can retry or propagate the error.
    /// When <paramref name="ex"/> is <c>null</c> the TCS is cancelled; otherwise it is faulted.
    /// </summary>
    private static async Task RemoveInflightAndSignal(
        string normalizedPath, TaskCompletionSource<(Workspace, Project)> tcs, Exception? ex = null)
    {
        await s_cacheLock.WaitAsync(CancellationToken.None);
        try { s_inflight.Remove(normalizedPath); }
        finally { s_cacheLock.Release(); }

        if (ex is OperationCanceledException oce)
            tcs.TrySetCanceled(oce.CancellationToken);
        else if (ex is not null)
            tcs.TrySetException(ex);
        else
            tcs.TrySetCanceled();
    }

    /// <summary>
    /// Adds <paramref name="normalizedProjectPath"/> to an already-cached solution workspace via an
    /// incremental <c>OpenProjectAsync</c> (Roslyn reuses any references already loaded), then runs
    /// the post-open pipeline over only the newly-added projects and updates the cache mappings.
    /// Serialized per entry by its <see cref="CachedWorkspaceEntry.LoadGate"/>; reads of the
    /// workspace stay safe meanwhile via immutable solution snapshots. No-op when the project is
    /// already loaded, nothing new was pulled in, or the entry isn't an MSBuild workspace.
    /// </summary>
    private static async Task EnsureProjectLoadedAsync(
        CachedWorkspaceEntry entry, string normalizedProjectPath, CancellationToken cancellationToken)
    {
        if (entry.Workspace is not MSBuildWorkspace ws)
            return;

        // Taken before the gate, where the caller's frames are still on the stack. A load is
        // minutes of MSBuild on a large solution, so which feature asked for one is the difference
        // between "the solution is big" and a feature quietly walking every project in it.
        string origin = LoadOrigin();

        var phases = new LoadPhaseTimings();
        phases.Start();

        // Before the gate, deliberately. Restore is a subprocess and, on a cold package cache, the
        // network; the gate exists only because MSBuildWorkspace cannot take two concurrent opens,
        // and holding it across a restore made every other project in the solution — and every
        // interactive request queued behind them — wait out somebody else's NuGet download.
        // RestoreService single-flights per solution, so N projects arriving here at once still
        // produce one restore rather than N.
        await RestoreService.EnsureRestoredAsync(normalizedProjectPath, cancellationToken);
        // Also before the gate, for the same reason: a never-built source generator this project
        // references is a build subprocess, and GeneratorBuildService single-flights it.
        await GeneratorBuildService.EnsureGeneratorsBuiltAsync(normalizedProjectPath, cancellationToken);
        phases.Mark(ref phases.RestoreMs);

        await entry.LoadGate.WaitAsync(cancellationToken);
        try
        {
            // Getting in says no other load is running; it does not say there is still a workspace
            // to load into. An eviction — an idle sweep, a .csproj touch, a branch switch — can have
            // disposed this entry while the restore above was running.
            if (entry.IsDisposed)
                return;

            if (entry.ProjectIds.ContainsKey(normalizedProjectPath))
                return; // a concurrent caller already added it

            var beforeIds = ws.CurrentSolution.ProjectIds.ToHashSet();

            using var openCts = new CancellationTokenSource(OpenProjectTimeout);
            using var openLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, openCts.Token);

            phases.Mark(ref phases.GateMs);
            await ws.OpenProjectAsync(normalizedProjectPath, cancellationToken: openLinked.Token)
                .WaitAsync(OpenProjectTimeout, cancellationToken);
            phases.Mark(ref phases.OpenMs);

            var newIds = ws.CurrentSolution.ProjectIds.Where(id => !beforeIds.Contains(id)).ToHashSet();
            if (newIds.Count == 0)
                return; // already present transitively; mappings unchanged

            var (loader, dirs) = ApplyPostOpenPipeline(ws, newIds, entry.ShadowLoader);
            phases.Mark(ref phases.PipelineMs);

            await s_cacheLock.WaitAsync(cancellationToken);
            try
            {
                entry.MergeShadow(loader, dirs);
                entry.RefreshProjectIds();
                RegisterProjectMappingsLocked(entry.CacheKey, normalizedProjectPath, ws);
                if (dirs is { Count: > 0 })
                    RegisterShadowDirsLocked(entry.CacheKey, dirs);
                Interlocked.Increment(ref IncrementalLoadCount);
                Console.Error.WriteLine(
                    $"[WorkspaceService] Incrementally loaded '{Path.GetFileName(normalizedProjectPath)}' into " +
                    $"'{Path.GetFileName(entry.CacheKey)}' (+{newIds.Count} project(s); {entry.ProjectIds.Count} loaded) " +
                    $"for {origin}. {phases}");
            }
            finally
            {
                s_cacheLock.Release();
            }
        }
        finally
        {
            entry.LoadGate.Release();
        }

        // The single-project add is reached whenever exactly one project is missing — which is
        // precisely "F12 into a project that is not loaded yet", the case the buffer bridge exists
        // for. Without these the newly added project held disk text for every open file, and the
        // editor was never told the project set had moved.
        ReconcileOpenBuffersAfterLoad();
        NotifyProjectSetChanged();
    }

    /// <summary>
    /// Adds <paramref name="infos"/> to <paramref name="workspace"/> and then lets it convert the
    /// references between them from file paths into real project references.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OnProjectAdded</c>, not a <c>SetCurrentSolution</c> swap: it is the mutation API a
    /// <see cref="Workspace"/> exposes for this, and it raises the change notifications the rest of
    /// Roslyn — and everything in this repository that watches for a project appearing — depends on.
    /// </para>
    /// <para>
    /// <c>UpdateReferencesAfterAdd</c> is the step whose absence broke this the first time it was
    /// tried, and it is easy to miss because nothing throws. MSBuild reports a
    /// <c>&lt;ProjectReference&gt;</c> whose target is not in the batch as a metadata reference to
    /// that project's output assembly; this is what turns those back into
    /// <see cref="ProjectReference"/>s once the target has been added. Skip it and the workspace
    /// reads perfectly — every symbol resolves, through the DLL — while a rename stops crossing
    /// project boundaries and cross-project navigation lands in metadata instead of source.
    /// </para>
    /// </remarks>
    private static void AddProjectsAndRewireReferences(
        MSBuildWorkspace workspace, ImmutableArray<ProjectInfo> infos)
    {
        foreach (var info in infos)
        {
            if (!workspace.CurrentSolution.ContainsProject(info.Id))
                workspace.OnProjectAdded(info);
        }

        workspace.UpdateReferencesAfterAdd();
    }

    /// <summary>
    /// <see cref="AddProjectsAndRewireReferences"/>, then heals the references evaluation
    /// dropped over unbuilt outputs — loading the dropped targets when the workspace does not
    /// hold them, which is the usual case: a dropped reference is precisely one the loader
    /// never chased.
    /// </summary>
    /// <remarks>
    /// Bounded rounds, not a fixpoint: each round can only discover targets through projects
    /// the previous round loaded, and reference chains of dropped-over-unbuilt-output projects
    /// run shallow. A chain deeper than the bound stays partially healed and says so in the
    /// load log, which beats an unbounded loop over a pathological project graph.
    /// </remarks>
    private static async Task AddProjectsRewireAndHealAsync(
        MSBuildWorkspace workspace,
        ImmutableArray<ProjectInfo> infos,
        CancellationToken cancellationToken)
    {
        AddProjectsAndRewireReferences(workspace, infos);

        for (int round = 0; round < 3; round++)
        {
            var missing = ProjectReferenceHealer.Heal(workspace);
            if (missing.Count == 0)
                return;

            ImmutableArray<ProjectInfo> extra;
            try
            {
                var solutionForMap = workspace.CurrentSolution;
                extra = await SharedBuildHost.LoadAsync(
                    workspace, workspace.Properties, missing,
                    () => ProjectMap.Create(solutionForMap), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[WorkspaceService] Could not load {missing.Count} project(s) referenced " +
                    $"through unbuilt outputs ({ex.Message}); their references stay dropped.");
                return;
            }

            if (extra.IsDefaultOrEmpty)
                return;

            Console.Error.WriteLine(
                $"[WorkspaceService] Loaded {extra.Length} project(s) reachable only through " +
                $"references evaluation had dropped: " +
                $"{string.Join(", ", missing.Select(Path.GetFileName).Take(4))}" +
                (missing.Count > 4 ? ", …" : "") + ".");

            AddProjectsAndRewireReferences(workspace, extra);
        }
    }

    /// <summary>
    /// Which of <paramref name="projectPaths"/> are not served by any live cached workspace yet.
    /// </summary>
    /// <remarks>
    /// Exists so an explicit open can skip its warm-up — including the restore probe and the seed
    /// load — when an editor session has already loaded everything. The daemon serves the editor
    /// and MCP clients from one cache, so "open the solution" arriving second is the common case,
    /// and re-walking a solution that is already being served is what made it look like the whole
    /// solution loaded twice.
    /// </remarks>
    internal static async Task<List<string>> ProjectsNotYetLoadedAsync(
        IEnumerable<string> projectPaths, CancellationToken cancellationToken = default)
    {
        var missing = new List<string>();

        await s_cacheLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var path in projectPaths)
            {
                string key = Path.GetFullPath(path);
                if (!(s_projectToCacheKey.TryGetValue(key, out var cks) && cks.Any(s_cache.ContainsKey)))
                    missing.Add(path);
            }
        }
        finally
        {
            s_cacheLock.Release();
        }

        return missing;
    }

    /// <summary>
    /// Loads several projects of one solution in a single Roslyn batch instead of one call each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a caller that already knows it wants N projects — every consumer of a shared contract,
    /// every project in an explicitly opened solution — this is the difference between paying
    /// Roslyn's fixed per-call cost once and paying it N times. Measured on a generated 34-project
    /// solution: nine projects one at a time is 14.3 s, the same nine in one batch is 3.7 s.
    /// See <see cref="PartialSolution"/> for why the batch has to be expressed as a solution file.
    /// </para>
    /// <para>
    /// Falls back to the per-project path — silently, and with the same end state — whenever the
    /// batch cannot apply: fewer than two projects actually missing, no solution-keyed entry to
    /// batch into, a non-MSBuild workspace, or the batch itself failing. A caller gets the projects
    /// loaded either way; only the cost differs.
    /// </para>
    /// <para>
    /// Projects already loaded are re-listed in the generated solution rather than left out. They
    /// have to be: the batch produces a whole new workspace, and one that omitted them would drop
    /// projects this entry has already promised to serve.
    /// </para>
    /// </remarks>
    public static async Task EnsureProjectsLoadedAsync(
        IReadOnlyCollection<string> projectPaths, CancellationToken cancellationToken = default)
    {
        var normalized = projectPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
            return;

        // Before anything else — the restore probe, the generator scan, the seed — start the
        // BuildHost processes this open is about to need. Spawns are pure overlap: nothing here
        // waits on them, and each prewarm lane's first evaluation then meets a live process
        // instead of paying the spawn inside its own wall. The scratch workspace exists this
        // early only because its properties are the pool key.
        Task? prewarm = null;
        MSBuildWorkspace? scratch = null;
        var evaluations = SharedBuildHost.NewEvaluationMap();
        if (normalized.Count > 1)
        {
            try
            {
                // Ignition takes the raw property map rather than the workspace's own: the two
                // hold the same pairs (MSBuildWorkspace.Create stores what it is given, and the
                // pool key sorts them), but the workspace blocks on the MEF composition for most
                // of a second — a wait the host spawns have no reason to sit behind. Fired first,
                // the handshakes run under the composition instead of after it, and each lane's
                // first evaluation meets a process that is already live.
                bool legacySeed = PathHelper.RequiresMsBuild(normalized[0]);
                SharedBuildHost.IgniteInBackground(
                    ImmutableDictionary.CreateRange(
                        legacySeed ? CreateLegacyProperties() : CreateDefaultProperties()),
                    normalized);
                RunwayTrace.Mark("ignition fired");
                scratch = CreateWorkspace(isLegacy: legacySeed, lite: true);
                RunwayTrace.Mark("scratch workspace created");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WorkspaceService] Host ignition failed to start: {ex.Message}");
            }
        }

        // Restore first, once, for everything: it is per solution and outside every gate, so doing
        // it here rather than inside the loop below means one subprocess for the whole batch.
        await RestoreService.EnsureRestoredAsync(normalized[0], cancellationToken);
        RunwayTrace.Mark("restore ensured");

        // Every project's MSBuild evaluation, started before the seed instead of after it. The
        // seed takes seconds and the batch's evaluation took tens of them, strictly in sequence;
        // this runs the expensive half of the batch concurrently with the seed (which takes a solo
        // BuildHost rather than queueing behind these shards), and the batch below then finds
        // every project already in the evaluation cache. Failure here costs nothing: the batch
        // evaluates whatever the cache cannot answer, exactly as it always did.
        //
        // Started before the generator scan below, not after: evaluation reads project XML and
        // targets, and what a generator project has or hasn't built changes nothing it records —
        // while the scan over a large solution costs most of a second that the prewarm lanes
        // would otherwise spend idle.
        if (scratch is not null)
        {
            try
            {
                // Everything except the seed itself: the seed's own load is about to evaluate
                // that one anyway, and evaluating it here too would only race the stores.
                prewarm = SharedBuildHost.PrewarmEvaluationsAsync(
                    scratch, scratch.Properties, normalized.Skip(1).ToList(), evaluations,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WorkspaceService] Evaluation prewarm failed to start: {ex.Message}");
            }
        }

        // Per batch member rather than only the first: the members can sit in disjoint corners of
        // the reference graph, each with its own generator. The scan is cached project XML, so the
        // repeats cost nothing when there is nothing to build. It stays ahead of the seed because
        // the seed's compilation is what consumes generator output.
        foreach (string batchProjectPath in normalized)
            await GeneratorBuildService.EnsureGeneratorsBuiltAsync(batchProjectPath, cancellationToken);

        try
        {
            // The first project both establishes the solution workspace (via the ordinary cached
            // path, including all its fallback behaviour) and tells us which entry the rest belong
            // in.
            await GetOrOpenProjectAsync(normalized[0], cancellationToken: cancellationToken);

            CachedWorkspaceEntry? entry = null;
            await s_cacheLock.WaitAsync(cancellationToken);
            try
            {
                if (s_projectToCacheKey.TryGetValue(normalized[0], out var keys)
                    && keys.FirstOrDefault(k => s_cache.ContainsKey(k)) is { } key
                    && s_cache.TryGetValue(key, out var found)
                    && found.Workspace is MSBuildWorkspace
                    && PathHelper.IsSolutionFile(found.CacheKey))
                {
                    entry = found;
                }
            }
            finally
            {
                s_cacheLock.Release();
            }

            if (entry is null)
            {
                // Loose projects, decompiled entries, or a solution whose workspace failed to
                // become the owner: nothing to batch into.
                foreach (string path in normalized.Skip(1))
                    await LoadOneIgnoringFailureAsync(path, cancellationToken);
                return;
            }

            // Deliberately not awaiting the prewarm first. The batch's shards queue on the same
            // host gates the prewarm holds, so each one starts converting the moment that shard's
            // evaluations drain — staggered, while the slowest shards are still working — instead
            // of the whole conversion waiting for the last one. The shared evaluation map is what
            // makes this safe: every prewarm result, including a reference outside the solution
            // list (the chase) and a failed evaluation the disk cache never records, reaches the
            // batch as an in-flight task rather than as a cache miss that would claim a host.
            if (!await TryBatchLoadAsync(entry, normalized, evaluations, cancellationToken))
            {
                foreach (string path in normalized)
                    await LoadOneIgnoringFailureAsync(path, cancellationToken);
            }
        }
        finally
        {
            if (prewarm is not null)
            {
                // Whether the batch consumed it or the seed threw halfway: let the prewarm drain
                // before the scratch workspace it borrows services from is disposed under it.
                try { await prewarm; } catch { }
            }

            scratch?.Dispose();
        }
    }

    private static async Task LoadOneIgnoringFailureAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await GetOrOpenProjectAsync(path, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One project that will not load must not end the batch: the answer is then as narrow
            // as it would have been without this call, which is a worse result and not a failure.
            Console.Error.WriteLine(
                $"[WorkspaceService] Batch member '{Path.GetFileName(path)}' failed to load: {ex.Message}");
        }
    }

    /// <summary>
    /// Evaluates <paramref name="wanted"/> in one throwaway workspace, then grafts the resulting
    /// projects into <paramref name="entry"/>'s live workspace. Returns <see langword="false"/>
    /// when the batch does not apply or did not succeed, leaving the entry exactly as it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The expensive half of a project load — the BuildHost subprocess and the MSBuild design-time
    /// evaluation — has nothing to do with which workspace the result lands in, so it is done in a
    /// transient workspace that exists only for the duration of this call and is disposed before it
    /// returns. What survives is the evaluated project model, grafted onto the live solution.
    /// </para>
    /// <para>
    /// Grafting rather than swapping the live workspace for the batch one, which is the obvious
    /// shortcut and is wrong. Callers hold <see cref="ISymbol"/>s, <see cref="Project"/>s and
    /// <see cref="Solution"/>s taken from this workspace across a load — cross-project find-usages
    /// resolves the symbol first and then asks for the projects to search it in — and a symbol from
    /// a replaced workspace belongs to no compilation in the new one, so
    /// <c>SymbolFinder.FindReferencesAsync</c> silently returns nothing. The invariant that a
    /// cached workspace only ever <em>gains</em> projects is relied on well outside this file, and
    /// grafting is what keeps it true.
    /// </para>
    /// </remarks>
    private static async Task<bool> TryBatchLoadAsync(
        CachedWorkspaceEntry entry,
        IReadOnlyList<string> wanted,
        ConcurrentDictionary<string, Lazy<Task<ImmutableArray<ProjectFileInfo>>>>? sharedEvaluations,
        CancellationToken cancellationToken)
    {
        if (entry.Workspace is not MSBuildWorkspace live)
            return false;

        // Read without the gate. This is a hint, not a decision: another loader may add one of
        // these while the evaluation below runs, and GraftProjects re-checks by file path and skips
        // whatever arrived meanwhile. The only thing being decided here is whether a batch is worth
        // starting at all.
        var missing = wanted
            .Where(p => !entry.ProjectIds.ContainsKey(p) && File.Exists(p))
            .ToList();

        // One project is not a batch: a single OpenProjectAsync costs the same one BuildHost and
        // skips the graft entirely, so the common incremental case stays on the path that has
        // always served it.
        if (missing.Count < 2)
            return false;

        var watch = Stopwatch.StartNew();

        // The seed load has always announced itself; this one never did, and this is the one that
        // runs while the user is already working — opening a file in a project that is not loaded
        // yet, or a search widening the solution. Several seconds of it looked exactly like the
        // editor having stopped responding, with nothing on screen to say otherwise. That is the
        // "nothing shows me that something is being loaded" in the report.
        await using var progress = await ProgressReporter.BeginAsync(
            missing.Count == 1
                ? $"Loading {Path.GetFileNameWithoutExtension(missing[0])}"
                : $"Loading {missing.Count} projects",
            cancellationToken);

        ImmutableArray<ProjectInfo> loaded;
        try
        {
            // Evaluated through the process-wide warm BuildHost, and outside the gate. Nothing here
            // touches the live workspace — Roslyn is being asked to build project models, and the
            // ProjectMap is what makes those models point at the projects the workspace already
            // holds instead of at duplicates of them.
            var solutionForMaps = live.CurrentSolution;
            loaded = await SharedBuildHost.LoadAsync(
                live,
                live.Properties,
                missing,
                () => ProjectMap.Create(solutionForMaps),
                cancellationToken,
                sharedEvaluations);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[WorkspaceService] Batch load of '{Path.GetFileName(entry.CacheKey)}' failed " +
                $"({ex.Message}); falling back to loading one project at a time.");
            return false;
        }

        long evaluateMs = watch.ElapsedMilliseconds;
        if (loaded.IsDefaultOrEmpty)
        {
            // Said out loud rather than returned quietly: the caller's fallback is to load the same
            // projects one at a time, which works but costs several seconds, and a batch that
            // produces nothing without explanation looks exactly like a batch that was never tried.
            Console.Error.WriteLine(
                $"[WorkspaceService] Batch load of {missing.Count} project(s) for " +
                $"'{Path.GetFileName(entry.CacheKey)}' produced no projects after " +
                $"{evaluateMs} ms; falling back to loading one at a time.");
            return false;
        }

        await entry.LoadGate.WaitAsync(cancellationToken);
        long gateMs;
        int added;
        try
        {
            // Evicted while the batch was being evaluated — which is minutes on a large solution.
            // Grafting project models into a torn-down workspace is not something to attempt; the
            // caller falls back and the projects load into whatever workspace exists by then.
            if (entry.IsDisposed)
                return false;

            gateMs = watch.ElapsedMilliseconds - evaluateMs;

            // A project the ProjectMap matched to one already in the solution comes back with that
            // project's own id; AddProjectsAndRewireReferences skips those, so this counts what
            // genuinely arrived.
            added = loaded.Count(i => !live.CurrentSolution.ContainsProject(i.Id));
            await AddProjectsRewireAndHealAsync(live, loaded, cancellationToken);

            if (added == 0)
            {
                Console.Error.WriteLine(
                    $"[WorkspaceService] Batch of {missing.Count} project(s) for " +
                    $"'{Path.GetFileName(entry.CacheKey)}' was overtaken by another load; " +
                    "nothing left to add.");
                return true;
            }

            // Over the newly added projects only, and while they are still unreachable by any other
            // caller: the analyzer rebind has to happen before anything asks them for a compilation.
            var (loader, dirs) = ApplyPostOpenPipeline(
                live, [.. loaded.Select(i => i.Id)], entry.ShadowLoader);

            await s_cacheLock.WaitAsync(cancellationToken);
            try
            {
                entry.MergeShadow(loader, dirs);
                entry.RefreshProjectIds();
                RegisterProjectMappingsLocked(entry.CacheKey, wanted[0], live);
                if (dirs is { Count: > 0 })
                    RegisterShadowDirsLocked(entry.CacheKey, dirs);
                Interlocked.Increment(ref IncrementalLoadCount);
            }
            finally
            {
                s_cacheLock.Release();
            }

            // The gutter beside the caret was computed against the smaller solution.
            ReconcileOpenBuffersAfterLoad();
            NotifyProjectSetChanged();
        }
        finally
        {
            entry.LoadGate.Release();
        }

        // Anything the batch did not produce is still unloaded, so the caller is told to fall back;
        // whatever it did land is already in the workspace and costs the retry a cache hit.
        var stillMissing = missing.Where(p => !entry.ProjectIds.ContainsKey(p)).ToList();

        Console.Error.WriteLine(
            $"[WorkspaceService] Batch-loaded {added} project(s) into " +
            $"'{Path.GetFileName(entry.CacheKey)}' through the shared BuildHost " +
            $"({entry.ProjectIds.Count} loaded) [evaluate={evaluateMs}ms gate={gateMs}ms " +
            $"apply={watch.ElapsedMilliseconds - evaluateMs - gateMs}ms]" +
            (stillMissing.Count > 0 ? $" — {stillMissing.Count} still missing." : "."));

        return stillMissing.Count == 0;
    }

    /// <summary>
    /// Walks up the directory tree from <paramref name="filePath"/> to find
    /// the first .csproj whose project contains that file.
    /// Uses the workspace cache so repeated lookups are fast.
    /// </summary>
    public static async Task<string?> FindContainingProjectAsync(
        string filePath, CancellationToken cancellationToken = default)
    {
        string? generatedProjectPath = DecompiledSourceService.TryGetGeneratedProjectPath(filePath);
        if (!string.IsNullOrEmpty(generatedProjectPath))
            return generatedProjectPath;

        return (await FindOwnerAsync(filePath, cancellationToken)).ProjectPath;
    }

    /// <summary>
    /// The document a file backs, found through the project that compiles it.
    /// </summary>
    /// <remarks>
    /// One project fetch, where the two callers this replaces made two: the search below already
    /// opens each candidate project and asks it for the file, and then the caller opened the
    /// winner again to get the same document out of it.
    /// </remarks>
    public static async Task<Document?> FindDocumentAsync(
        string filePath, CancellationToken cancellationToken = default)
    {
        string? decompiled = DecompiledSourceService.TryGetGeneratedProjectPath(filePath);
        if (!string.IsNullOrEmpty(decompiled))
            return await TryFindInProjectAsync(decompiled, PathHelper.NormalizePath(filePath), cancellationToken);

        return (await FindOwnerAsync(filePath, cancellationToken)).Document;
    }

    /// <summary>Both halves of the same answer, so neither caller pays for the other's.</summary>
    private static async Task<(string? ProjectPath, Document? Document)> FindOwnerAsync(
        string filePath, CancellationToken cancellationToken)
    {
        string path = PathHelper.NormalizePath(filePath);

        // Verified, never trusted. A hit still opens the project and asks it for the file, so a
        // stale entry costs one wasted lookup and can never produce a wrong answer — which is what
        // makes this memoizable at all, given the ways ownership can move that nothing announces.
        // What the entry saves is the walk: every ancestor directory enumerated for *.csproj, and
        // every candidate found along the way opened and searched.
        if (s_containingProject.TryGetValue(path, out string? remembered))
        {
            if (await TryFindInProjectAsync(remembered, path, cancellationToken) is { } hit)
                return (remembered, hit);

            s_containingProject.TryRemove(path, out _);
        }

        var (found, document) = await SearchForContainingProjectAsync(filePath, cancellationToken);

        // Only what was found. A miss is never recorded: a file created on disk belongs to no
        // project until the watcher syncs it in, an MCP-only session has no watcher at all, and a
        // remembered "nothing owns this" would outlive the creation and leave the file inert with
        // no diagnostics, no hover and no navigation for the rest of the session.
        if (found is null)
            return (null, null);

        if (s_containingProject.Count >= MaxContainingProjectEntries)
            s_containingProject.Clear();

        s_containingProject[path] = found;

        return (found, document);
    }

    /// <summary>
    /// The document, if this project turns out to hold it. <paramref name="filePath"/> goes in as
    /// the target so a file changed on disk since the project was cached is re-read.
    /// </summary>
    private static async Task<Document?> TryFindInProjectAsync(
        string projectPath, string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var (_, project) = await GetOrOpenProjectAsync(
                projectPath, targetFilePath: filePath, cancellationToken: cancellationToken);

            return FindDocumentInProject(project, filePath);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // A project that will not open holds nothing as far as this is concerned. The walk
            // reports the failure; repeating it here would only duplicate the log entry.
            return null;
        }
    }

    /// <summary>
    /// The walk: every ancestor directory, every <c>.csproj</c> in it, in name order. The document
    /// comes back with the project because finding it is how a candidate is accepted — returning
    /// only the path is what made every caller open the winner a second time.
    /// </summary>
    private static async Task<(string? ProjectPath, Document? Document)> SearchForContainingProjectAsync(
        string filePath, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ProjectSearches);

        DirectoryInfo? directory = new FileInfo(filePath).Directory;

        while (directory != null)
        {
            var projectFiles = directory.GetFiles("*.csproj")
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (projectFiles.Count > 0)
            {
                foreach (var projectFile in projectFiles)
                {
                    string projectPath = projectFile.FullName;
                    try
                    {
                        // The file goes in as the target so a candidate that does hold it hands
                        // back a document already reconciled against what is on disk — the refresh
                        // the second lookup used to be responsible for.
                        var (_, project) = await GetOrOpenProjectAsync(
                            projectPath, targetFilePath: filePath, diagnosticWriter: Console.Error,
                            cancellationToken: cancellationToken);

                        if (FindDocumentInProject(project, filePath) is { } document)
                            return (projectPath, document);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Visible to the user: a project that will not load makes every feature
                        // in it look broken, with no other clue as to why.
                        ServiceLog.Error(
                            $"Could not open '{Path.GetFileName(projectPath)}': {ex.Message}" +
                            (ex.InnerException is { } inner ? $" ({inner.Message})" : ""),
                            key: $"project-load:{projectPath}");
                    }
                }
            }

            directory = directory.Parent;
        }

        return (null, null);
    }

    /// <summary>
    /// Finds a document in a project by file path (case-insensitive comparison).
    /// </summary>
    public static Document? FindDocumentInProject(Project project, string filePath)
    {
        // The solution's path index first: this is on the resolve path of every LSP request, and a
        // linear scan of a large project's documents is a per-request cost for an answer Roslyn
        // already has in a dictionary.
        foreach (var id in project.Solution.GetDocumentIdsWithFilePath(filePath))
        {
            if (id.ProjectId == project.Id && project.GetDocument(id) is { } indexed)
                return indexed;
        }

        // The index keys on the raw FilePath the project was loaded with, so a caller holding a
        // differently-spelled but equivalent path — a different case, or a different separator —
        // misses it. That is what the scan has always tolerated, so it stays as the fallback.
        return project.Documents
            .FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds .csproj files that contain a <c>&lt;ProjectReference&gt;</c> to
    /// <paramref name="referencedProjectPath"/>. Scans the ancestor directories of
    /// the referenced project up to the repository root (detected by <c>.git</c> folder)
    /// or at most 5 levels up.
    /// </summary>
    public static List<string> FindReferencingProjects(string referencedProjectPath)
    {
        var normalizedTarget = Path.GetFullPath(referencedProjectPath);
        var targetFileName = Path.GetFileName(normalizedTarget);
        var results = new List<string>();

        // Walk up to repo root or 5 levels
        var searchRoot = new FileInfo(normalizedTarget).Directory;
        for (int i = 0; i < 5 && searchRoot?.Parent != null; i++)
        {
            searchRoot = searchRoot.Parent;
            if (Directory.Exists(Path.Combine(searchRoot.FullName, ".git")))
                break;
        }

        if (searchRoot is null)
            return results;

        foreach (var csprojFile in searchRoot.EnumerateFiles("*.csproj", SearchOption.AllDirectories))
        {
            if (string.Equals(csprojFile.FullName, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var content = File.ReadAllText(csprojFile.FullName);
                // Check if this project references the target by file name
                if (content.Contains(targetFileName, StringComparison.OrdinalIgnoreCase) &&
                    content.Contains("ProjectReference", StringComparison.Ordinal))
                {
                    // Verify by resolving the actual ProjectReference path
                    var dir = csprojFile.DirectoryName!;
                    foreach (var line in content.Split('\n'))
                    {
                        if (!line.Contains("ProjectReference", StringComparison.Ordinal))
                            continue;

                        var includeStart = line.IndexOf("Include=\"", StringComparison.Ordinal);
                        if (includeStart < 0) continue;
                        includeStart += 9;
                        var includeEnd = line.IndexOf('"', includeStart);
                        if (includeEnd < 0) continue;

                        var refPath = line[includeStart..includeEnd].Replace('\\', Path.DirectorySeparatorChar);
                        var resolvedPath = Path.GetFullPath(Path.Combine(dir, refPath));
                        if (string.Equals(resolvedPath, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(csprojFile.FullName);
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Ignore unreadable files
            }
        }

        return results;
    }

    /// <summary>
    /// The solution this process was started for, when it was started for one.
    /// </summary>
    /// <remarks>
    /// Set from <c>--host &lt;solution&gt;</c> or <c>--lsp --solution &lt;path&gt;</c>. It is known
    /// before any project is loaded, which matters for anything that needs the solution's
    /// <em>identity</em> rather than its contents — the Solution Explorer reads the .sln directly,
    /// and inferring the path from whatever workspace happened to be cached left it empty until
    /// someone opened a file, and wrong once someone opened decompiled source.
    /// </remarks>
    public static string? BoundSolutionPath { get; private set; }

    public static void BindSolution(string? solutionPath)
    {
        if (solutionPath is { Length: > 0 } path && PathHelper.IsSolutionFile(path) && File.Exists(path))
        {
            BoundSolutionPath = Path.GetFullPath(path);

            // Nothing is loaded here — these only start the fixed costs the first real request would
            // otherwise discover inside itself: the NuGet restore the solution needs if and only if
            // it has never been restored, the MEF composition every workspace runs on, and MSBuild's
            // own start-up inside the BuildHost. All three overlap the seconds the editor spends on
            // structural work, which needs none of them. See StartSolutionRestoreInBackground for
            // why this is not a preload: no project is opened and no solution is read.
            RestoreService.StartSolutionRestoreInBackground(BoundSolutionPath);
            WarmHostServicesInBackground();

            if (PathHelper.GetProjectsFromSolution(BoundSolutionPath).FirstOrDefault() is { } anyProject)
                SharedBuildHost.WarmInBackground(CreateDefaultProperties().ToImmutableDictionary(), anyProject);
        }
    }

    /// <summary>
    /// Returns the most-recently-used cached solution (with open-buffer overlays applied), or
    /// null when nothing is loaded yet. Used by solution-wide queries that aren't anchored to
    /// a file (LSP workspace/symbol).
    /// </summary>
    public static Solution? TryGetMostRecentSolution()
    {
        CachedWorkspaceEntry? entry = null;

        // Deliberately lock-free. This is called from workspace/symbol, search-everywhere, the
        // solution tree and the diagnostics sweep — all on request threads — and s_cacheLock is
        // held across the bookkeeping at the end of a project load, so taking it here stalled
        // those requests behind whatever was loading. The dictionary is concurrent.
        //
        // Synthetic entries are skipped. Opening a dependency's source caches an ad-hoc workspace
        // keyed by its manifest, and it is by definition the most recently used one — so the
        // Solution Explorer, which asks this for the solution to list, emptied itself every
        // time someone looked at a framework type.
        Solution? snapshot = null;
        foreach (var (key, e) in s_cache)
        {
            if (ExternalSource.ExternalSourceCache.IsExternalSourcePath(key))
                continue;
            if (entry is not null && e.LastAccessedUtc <= entry.LastAccessedUtc)
                continue;

            // Captured inside the loop, and kept only if it holds something. Disposing a Workspace
            // clears its CurrentSolution, so reading it after choosing a winner could hand back an
            // *empty* solution for an entry evicted in between — the Problems panel and the
            // Solution Explorer would go blank, which is the symptom this whole change exists to
            // remove. A Solution is immutable and outlives the entry that produced it, so a
            // captured non-empty one stays valid however the cache moves afterwards.
            var candidate = e.Workspace.CurrentSolution;
            if (candidate.ProjectIds.Count == 0)
                continue;

            entry = e;
            snapshot = candidate;
        }

        if (entry is null || snapshot is null)
            return null;

        var project = snapshot.GetProject(entry.PrimaryProjectId);
        return project is null ? snapshot : ApplyOpenDocumentOverlay(entry, project).Solution;
    }

    /// <summary>
    /// The loaded project a file belongs to, or null when nothing that could answer is open.
    /// </summary>
    /// <remarks>
    /// Reads the cache and never fills it, which is the whole point: this serves questions that are
    /// worth answering when the answer is already in memory and not worth an MSBuild evaluation when
    /// it is not — hovering a suppressed warning code in a <c>.csproj</c> to see what it means.
    /// <see cref="GetOrOpenProjectAsync"/> is for callers that need the project either way.
    ///
    /// A project file matches itself. Anything else — <c>Directory.Build.props</c>,
    /// <c>Directory.Packages.props</c>, a <c>.targets</c> — matches the nearest loaded project
    /// beneath it, because that is the project whose settings it is written to affect.
    /// </remarks>
    public static Project? TryGetLoadedProject(string filePath)
    {
        Project? best = null;

        // Nearest wins: a Directory.Build.props at the repository root would otherwise answer with
        // whichever project the cache happened to enumerate first.
        foreach (var project in LoadedProjectsUnder(filePath))
        {
            if (best?.FilePath is { } chosen && project.FilePath is { } candidate
                && chosen.Length > candidate.Length)
            {
                continue;
            }

            best = project;
        }

        return best;
    }

    /// <summary>
    /// Every loaded project a file governs: itself for a project file, everything beneath it for
    /// anything else.
    /// </summary>
    /// <remarks>
    /// The scope a <c>Directory.Build.props</c> actually has. A property written there applies to
    /// every project under that directory, so a question about what it does — how many warnings a
    /// suppression there is hiding, say — is a question about all of them and not about whichever
    /// one happens to be nearest.
    ///
    /// Cache-only, like <see cref="TryGetLoadedProject"/>: what is open answers, and what is not
    /// stays closed.
    /// </remarks>
    public static ImmutableArray<Project> LoadedProjectsUnder(string filePath)
    {
        string full = Path.GetFullPath(filePath);
        bool isProjectFile = full.EndsWith("proj", StringComparison.OrdinalIgnoreCase);
        string directory = Path.GetDirectoryName(full) is { Length: > 0 } d
            ? d + Path.DirectorySeparatorChar
            : full;

        var found = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);

        // Lock-free for the same reason as TryGetMostRecentSolution: this runs on request threads,
        // and s_cacheLock is held across the end of a project load.
        foreach (var (key, entry) in s_cache)
        {
            if (ExternalSource.ExternalSourceCache.IsExternalSourcePath(key))
                continue;

            var solution = entry.Workspace.CurrentSolution;

            foreach (var (path, id) in entry.ProjectIds)
            {
                bool matches = isProjectFile
                    ? string.Equals(path, full, StringComparison.OrdinalIgnoreCase)
                    : path.StartsWith(directory, StringComparison.OrdinalIgnoreCase);

                // Keyed by path: the same project is cached under every solution that holds it, and
                // counting it once per workspace would multiply every answer about it.
                if (matches && !found.ContainsKey(path) && solution.GetProject(id) is { } project)
                    found[path] = project;
            }
        }

        return [.. found.Values];
    }

    /// <summary>
    /// Evicts all cached workspace entries immediately.
    /// </summary>
    public static async Task EvictAllAsync(CancellationToken cancellationToken = default)
    {
        bool evictedAnything;

        await s_cacheLock.WaitAsync(cancellationToken);
        try
        {
            evictedAnything = s_cache.Count > 0;

            foreach (var entry in s_cache.Values)
            {
                foreach (var projectPath in entry.ProjectIds.Keys)
                    AnalyzerService.EvictAnalyzersForProject(projectPath);
                entry.Dispose();
            }
            s_cache.Clear();
            s_dirToProjects.Clear();
            s_projectToCacheKey.Clear();

            // Its answer depends on Directory.Build.props/.targets, whose edits do not move any
            // .csproj timestamp — and that timestamp is the whole cache key. A .props change comes
            // through here, so this is where the stale verdict has to go.
            s_plainGlob.Clear();

            // What is watched follows what is loaded: with nothing cached there is nothing for a
            // restore to invalidate, and holding the handles would leak a tree per solution switch.
            RestoreWatcher.StopAll();

            Console.Error.WriteLine("[WorkspaceService] All cached workspaces evicted.");
        }
        finally
        {
            s_cacheLock.Release();
        }

        // The second eviction funnel: this one disposes inline rather than going through
        // EvictEntryLocked, so it needs its own signal. It is reached by a solution rebind, an
        // analyzer rebuild and a watched .csproj changing — every one of which leaves the editor
        // holding answers computed against workspaces that are now gone.
        if (evictedAnything)
            NotifyProjectSetChanged();

        // Deliberately not tearing down the build hosts here. This method means "drop the cached
        // workspaces" — it is called on a solution rebind, on analyzer rebuilds, and by tests
        // between cases — and the hosts have nothing to do with any one workspace: they are warm
        // MSBuild processes that answer questions about project files. Disposing them here killed
        // hosts that other in-flight loads were mid-conversation with. They are released in
        // ShutdownAsync instead, at the one point where nothing can still be using them.
    }

    /// <summary>
    /// Releases the process-wide resources that outlive any workspace. Call once, on the way out.
    /// </summary>
    public static async Task ShutdownAsync()
    {
        await EvictAllAsync(CancellationToken.None);

        // Subprocesses, so nothing else reclaims them: left behind they are the orphaned MSBuild
        // hosts that accumulate on a machine until it is rebooted.
        await SharedBuildHost.DisposeAllAsync();
    }

    // ---- Test hooks (exposed via InternalsVisibleTo) ----

    internal static int CachedEntryCount
    {
        get { s_cacheLock.Wait(); try { return s_cache.Count; } finally { s_cacheLock.Release(); } }
    }

    /// <summary>True when <paramref name="projectPath"/> resolves to a live cached workspace.</summary>
    internal static bool IsProjectCachedForTests(string projectPath)
    {
        string key = Path.GetFullPath(projectPath);
        s_cacheLock.Wait();
        try
        {
            return s_projectToCacheKey.TryGetValue(key, out var cks) && cks.Any(s_cache.ContainsKey);
        }
        finally { s_cacheLock.Release(); }
    }

    /// <summary>Number of projects currently loaded in the workspace serving
    /// <paramref name="projectPath"/>, or 0 if it isn't cached. Lets tests assert that opening
    /// one project of a multi-project solution loads only it (+ forward refs), not the whole sln.</summary>
    internal static int LoadedProjectCountForTests(string projectPath)
    {
        string key = Path.GetFullPath(projectPath);
        s_cacheLock.Wait();
        try
        {
            return s_projectToCacheKey.TryGetValue(key, out var cks)
                && cks.Select(ck => s_cache.TryGetValue(ck, out var e) ? e : null).OfType<CachedWorkspaceEntry>().FirstOrDefault() is { } entry
                ? entry.ProjectIds.Count
                : 0;
        }
        finally { s_cacheLock.Release(); }
    }

    /// <summary>Evicts only the single entry serving <paramref name="projectPath"/> (no global sweep).</summary>
    internal static Task EvictProjectForTests(string projectPath) => EvictProjectAsync(projectPath);

    /// <summary>
    /// Evicts the cached workspace entry serving <paramref name="projectPath"/>, leaving other
    /// solutions loaded. Used when a change is known to be local to one project — a source file
    /// appearing or disappearing on disk — where a full sweep would needlessly discard
    /// everything else.
    /// </summary>
    public static async Task EvictProjectAsync(
        string projectPath, CancellationToken cancellationToken = default) =>
        await EvictProjectIfLoadedAsync(projectPath, cancellationToken);

    /// <summary>
    /// Evicts the cached workspace entries serving <paramref name="projectPath"/> and reports
    /// whether there were any.
    /// </summary>
    /// <remarks>
    /// The answer is what lets a caller that evicts speculatively — a restore that has just written
    /// a NuGet graph for a whole solution, over projects that may or may not be loaded — say how
    /// much it actually invalidated instead of claiming it refreshed twenty projects it never
    /// touched.
    /// </remarks>
    public static async Task<bool> EvictProjectIfLoadedAsync(
        string projectPath, CancellationToken cancellationToken = default)
    {
        bool evictedAny = false;
        string key = Path.GetFullPath(projectPath);
        await s_cacheLock.WaitAsync(cancellationToken);
        try
        {
            // Every workspace holding this project. Evicting one and leaving the rest is how a
            // project shared by two solutions ended up served from a snapshot nothing would ever
            // correct — its own .csproj write is recognised as ours, so no staleness check and no
            // watcher event would come back for it either.
            if (s_projectToCacheKey.TryGetValue(key, out var cks))
            {
                foreach (string ck in cks.ToList())
                {
                    if (s_cache.TryGetValue(ck, out var entry))
                    {
                        EvictEntryLocked(ck, entry, $"'{Path.GetFileName(key)}' was written");
                        evictedAny = true;
                    }
                }
            }
        }
        finally { s_cacheLock.Release(); }

        return evictedAny;
    }

    /// <summary>
    /// Re-applies every open buffer once a load has finished with the gate.
    /// </summary>
    /// <remarks>
    /// A reconcile that arrives while a project is loading waits a bounded time for that project's
    /// gate and then gives up — an unbounded wait deadlocks, and the per-request overlay still
    /// covers correctness. But nothing else ever came back for it, so the newly loaded projects
    /// held disk text for files the editor has open, and the fork this bridge exists to eliminate
    /// quietly took over again. Every load path has to close that window, not just the first one:
    /// the incremental adds are the F12-into-an-unloaded-project case the bridge is really for.
    ///
    /// Not awaited: the request that triggered the load is waiting on it, and re-applying N buffers
    /// across M workspaces has nothing to tell that request.
    /// </remarks>
    private static void ReconcileOpenBuffersAfterLoad() =>
        _ = Task.Run(async () =>
        {
            foreach (string open in OpenDocumentStore.OpenPaths())
            {
                try { await ReconcileOpenBufferAsync(open); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[WorkspaceService] Re-reconciling '{open}' after a load failed: {ex.Message}");
                }
            }
        });

    /// <summary>Serializes buffer reconciliation, so two keystrokes cannot land out of order.</summary>
    private static readonly SemaphoreSlim s_bufferGate = new(1, 1);

    /// <summary>How long a buffer sync waits for a workspace busy loading before giving up on it.
    /// Bounded so a load — or an eviction racing one — cannot wedge the bridge; see the wait
    /// itself for why an unbounded one is unrecoverable.</summary>
    private static readonly TimeSpan LoadGateWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Subscribes the live workspaces to open-buffer changes. Idempotent; called on session attach.
    /// </summary>
    public static void InstallOpenBufferBridge() =>
        OpenDocumentStore.OverlayableBufferChanged = path =>
        {
            // Task.Run, because the store raises this from inside didOpen/didChange — synchronous
            // JSON-RPC handlers — and the awaits below all complete synchronously when the gates
            // are free. Every keystroke was therefore mutating each cached workspace, and running
            // its WorkspaceChanged fan-out, before the notification handler returned.
            string key = NormalizeReconcileKey(path);
            var reconcile = Task.Run(() => ReconcileOpenBufferAsync(path));
            s_pendingReconciles[key] = reconcile;
            reconcile.ContinueWith(
                _ => ((ICollection<KeyValuePair<string, Task>>)s_pendingReconciles)
                    .Remove(new KeyValuePair<string, Task>(key, reconcile)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        };

    /// <summary>The reconcile each open file currently has in flight, if any. A request that
    /// resolves the file between the keystroke and the reconcile landing would fork an overlay
    /// off the stale base and build a frozen compilation there, only for the reconcile to move
    /// <c>CurrentSolution</c> right after — one whole duplicate build per keystroke. Rendezvous
    /// instead: see <see cref="AwaitPendingReconcileAsync"/>.</summary>
    private static readonly ConcurrentDictionary<string, Task> s_pendingReconciles =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bounded, for the case where the reconcile is stuck behind a workspace mid-load
    /// (<see cref="LoadGateWait"/>): the overlay fork still covers correctness, so a request
    /// never waits out somebody's MSBuild for what is only a caching optimization.</summary>
    private static readonly TimeSpan ReconcileRendezvousBound = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// Waits — briefly — for <paramref name="filePath"/>'s in-flight buffer reconcile, so the
    /// request that follows a keystroke binds against the reconciled solution instead of paying
    /// for a throwaway fork. Returns immediately when nothing is pending, which is every request
    /// outside the small window between didChange and the reconcile completing.
    /// </summary>
    internal static async Task AwaitPendingReconcileAsync(string filePath, CancellationToken ct)
    {
        if (!s_pendingReconciles.TryGetValue(NormalizeReconcileKey(filePath), out var pending)
            || pending.IsCompleted)
        {
            return;
        }

        await Task.WhenAny(pending, Task.Delay(ReconcileRendezvousBound, ct));
    }

    private static string NormalizeReconcileKey(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }

    /// <summary>
    /// Brings every loaded workspace's copy of <paramref name="filePath"/> in line with the editor:
    /// the buffer text while it is open, the text on disk once it is closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The open-buffer overlay (<see cref="ApplyOpenDocumentOverlay"/>) forks a solution per
    /// request, and a fork cannot be carried across a change to the solution it was forked from.
    /// So navigating into a project that was not loaded yet — F12 — added that project to the
    /// workspace, invalidated the fork, and forced every open buffer to be re-applied onto the new
    /// base, which gave each one a new version and told the editor that everything it held was
    /// stale. Putting the text in the workspace instead means a project add builds on top of it and
    /// the versions survive.
    /// </para>
    /// <para>
    /// Reconcile rather than apply: it reads the store inside the gate and writes whatever is
    /// current, so it converges on the newest buffer no matter what order the calls run in, and a
    /// reconcile that loses a race with a close restores the file from disk instead of resurrecting
    /// a buffer that is gone.
    /// </para>
    /// </remarks>
    internal static async Task ReconcileOpenBufferAsync(string filePath)
    {
        try
        {
            await s_bufferGate.WaitAsync();
            try
            {
                bool open = OpenDocumentStore.TryGet(filePath, out var text);

                foreach (var entry in s_cache.Values)
                {
                    if (entry.Workspace is not MSBuildWorkspace live)
                        continue;

                    var ids = live.CurrentSolution.GetDocumentIdsWithFilePath(filePath);
                    if (ids.IsEmpty)
                        continue;

                    // Per entry, so one workspace being evicted underneath this loop — which
                    // disposes its gate without waiting for holders — does not abandon the
                    // remaining workspaces. With two solutions open, an eviction in one used to
                    // silently skip the reconcile for the other, leaving it on stale text.
                    //
                    // Bounded, because an unbounded wait here is a permanent hang: a project load
                    // holds this gate for as long as MSBuild takes, and s_bufferGate — which this
                    // whole loop holds — is not released until the finally below runs. Waiting out
                    // somebody else's MSBuild would silently kill the buffer bridge for the rest of
                    // the session.
                    //
                    // Whatever is holding it is mid-load, and a load ends by rebuilding this
                    // anyway. The overlay fork still covers correctness until then.
                    if (!await entry.LoadGate.WaitAsync(LoadGateWait))
                        continue;

                    // Evicted while we were queued: there is no workspace left to reconcile into.
                    if (entry.IsDisposed)
                    {
                        entry.LoadGate.Release();
                        continue;
                    }

                    try
                    {
                        foreach (var id in live.CurrentSolution.GetDocumentIdsWithFilePath(filePath))
                        {
                            var document = live.CurrentSolution.GetDocument(id);
                            if (document is null)
                                continue;

                            if (open)
                            {
                                // Content, not reference. The buffer arrives as a fresh SourceText
                                // — didOpen builds one from the notification's text — so it is
                                // never the instance the workspace holds, and a reference test
                                // meant every open re-stamped the document, moved its project's
                                // dependent semantic version, and missed the analyzer cache for
                                // every file in it. That is the reported symptom exactly: open a
                                // file, watch every warning in the window vanish.
                                // GetTextAsync, not TryGetText: the latter answers false whenever
                                // the text has not been realized yet, and the guard would then be
                                // skipped and the change applied — so a cold solution re-stamped
                                // every document it touched, which is the cost this exists to
                                // avoid. Materializing is cheap for a file already loaded and
                                // needed anyway for one that is not.
                                if ((await document.GetTextAsync()).ContentEquals(text))
                                    continue;

                                Lsp.AnalyzerDiagnosticCache.Evict(id);
                                live.OnDocumentTextChanged(id, text, PreservationMode.PreserveIdentity);
                            }
                            else
                            {
                                // Closing is only a change if the buffer differed from disk. Almost
                                // always it did not — the file was saved, or never edited — and
                                // reverting unconditionally made closing a tab invalidate the whole
                                // project's analysis.
                                if (!File.Exists(filePath))
                                    continue;

                                var disk = TryReadDisk(filePath);
                                if (disk is not null && (await document.GetTextAsync()).ContentEquals(disk))
                                    continue;

                                Lsp.AnalyzerDiagnosticCache.Evict(id);
                                live.OnDocumentTextLoaderChanged(
                                    id, new FileTextLoader(filePath, defaultEncoding: null));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or ArgumentException)
                    {
                        // This workspace was torn down underneath us. The others still need the
                        // buffer, and a single outer catch would have skipped every one of them.
                        Console.Error.WriteLine(
                            $"[WorkspaceService] Reconciling '{filePath}' into a workspace failed: {ex.Message}");
                    }
                    finally
                    {
                        entry.LoadGate.Release();
                    }
                }
            }
            finally
            {
                s_bufferGate.Release();
            }
        }
        catch (Exception ex)
        {
            // The overlay still covers correctness; this is the optimization that keeps versions
            // stable, and failing it must not break editing.
            Console.Error.WriteLine(
                $"[WorkspaceService] Reconciling buffer '{filePath}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds or removes one source file in the live workspace, instead of throwing the workspace
    /// away so MSBuild can rediscover it. Returns false when that cannot be done safely, and the
    /// caller should fall back to eviction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cache entry serves a whole solution, so "evict just this project" discards every project
    /// in it — every compilation, every cached analyzer result — because one file appeared. A git
    /// checkout or a scaffold therefore cost a full reload. An SDK-style project globs its compile
    /// items, so a <c>.cs</c> under its directory is a compile item by construction and adding the
    /// document is exactly what re-evaluating MSBuild would have concluded.
    /// </para>
    /// <para>
    /// Legacy projects are refused: they list their compile items explicitly, so a file on disk is
    /// not necessarily part of the project and only MSBuild can say. Those keep the old behaviour.
    /// </para>
    /// </remarks>
    /// <param name="authoritative">
    /// True when the caller has itself just made this true of the project — written the
    /// <c>Compile</c> item, or the <c>Compile Remove</c> — rather than merely observing a file
    /// appear or vanish on disk. It settles the two questions this method otherwise has to guess
    /// at: whether a legacy project compiles a file that just appeared, and whether a file still
    /// present on disk is still part of the project.
    /// </param>
    public static async Task<FileSyncResult> TryApplyFileChangeAsync(
        string projectPath,
        string filePath,
        FileChange change,
        CancellationToken cancellationToken = default,
        bool authoritative = false)
    {
        // Nothing is refused up front on the strength of the project being legacy. Whether a
        // change can be applied in place is decided per project further down, where the document
        // set is visible: an edit or a delete concerns a document that is already there, and only
        // a file that just appeared raises the question of whether the project compiles it. Turning
        // the whole class away meant every save in a .NET Framework or WebForms solution evicted
        // the workspace — most of what "it reloads constantly" amounted to off SDK-style projects.
        string key = Path.GetFullPath(projectPath);
        string target = Path.GetFullPath(filePath);

        List<CachedWorkspaceEntry> entries;
        await s_cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (!s_projectToCacheKey.TryGetValue(key, out var cacheKeys)
                || cacheKeys.Select(k => s_cache.TryGetValue(k, out var e) ? e : null)
                    .OfType<CachedWorkspaceEntry>().ToList() is not { Count: > 0 } found)
            {
                // Nothing loaded for it, so there is nothing to bring up to date and nothing worth
                // evicting. Deliberately not "applied": telling the editor to re-pull everything
                // because a file changed in a project nobody has opened is the cost this avoids.
                return FileSyncResult.NothingToDo;
            }

            entries = found;
        }
        finally { s_cacheLock.Release(); }

        // Every workspace that holds this project, not one of them. Two solutions can both load
        // it, and Roslyn pulls a referenced project into whichever workspace asked for its
        // consumer — so applying to the first the index yields left the others missing the file
        // entirely, with no eviction (the call reported success) and no second watcher event (the
        // write was recognised as ours) to ever correct them.
        var results = new List<FileSyncResult>();

        foreach (var entry in entries)
        {
            if (entry.Workspace is not MSBuildWorkspace live)
            {
                results.Add(FileSyncResult.CannotApply);
                continue;
            }

            results.Add(await ApplyToWorkspaceAsync(entry, live, key, target, change, authoritative, cancellationToken));
        }

        // One workspace that cannot decide in place decides for all of them: the caller falls back
        // and MSBuild answers for every entry at once.
        if (results.Contains(FileSyncResult.CannotApply))
            return FileSyncResult.CannotApply;

        if (!results.Contains(FileSyncResult.Applied))
            return FileSyncResult.NothingToDo;

        if (OpenDocumentStore.IsOpen(target))
            await ReconcileOpenBufferAsync(target);

        NotifyProjectSetChanged();
        return FileSyncResult.Applied;
    }

    private static async Task<FileSyncResult> ApplyToWorkspaceAsync(
        CachedWorkspaceEntry entry,
        MSBuildWorkspace live,
        string key,
        string target,
        FileChange change,
        bool authoritative,
        CancellationToken cancellationToken)
    {
        try
        {
            // Every project sharing this file path, not one: a multi-targeted project is several
            // Projects with the same FilePath, and updating only the one the path index happens to
            // hold leaves the other frameworks with a document over a file that is gone.
            //
            // Bounded for the same reason the buffer bridge is: a load holding this gate holds it
            // for as long as MSBuild takes, so an unbounded wait parks the caller behind somebody
            // else's evaluation. It means "come back later", not "MSBuild must decide". Reporting
            // the timeout as undecidable made the caller evict — and evict the workspace that was
            // still loading, which faults the request that started the load and then reloads the
            // solution from scratch. Saving any file ten seconds into an F12 was enough. The buffer
            // bridge already treats this timeout as a skip; so does this now, for the same reason:
            // a load ends by rebuilding what it holds.
            if (!await entry.LoadGate.WaitAsync(LoadGateWait, cancellationToken))
                return FileSyncResult.NothingToDo;

            try
            {
                // Evicted while we were queued behind a load; nothing left to sync into.
                if (entry.IsDisposed)
                    return FileSyncResult.NothingToDo;

                // The project the caller named, plus every other project that already holds a
                // document for this exact path. A linked item puts the same file in a project that
                // no upward directory walk from it would ever reach, so updating only the named one
                // left every linking project answering from pre-edit text, with nothing to correct
                // it. Eviction used to cover this by accident, by discarding the whole solution.
                var byPath = live.CurrentSolution.GetDocumentIdsWithFilePath(target)
                    .Select(id => live.CurrentSolution.GetProject(id.ProjectId))
                    .OfType<Project>();

                var namedProjects = live.CurrentSolution.Projects
                    .Where(p => p.FilePath is { Length: > 0 } fp
                        && string.Equals(Path.GetFullPath(fp), key, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var namedIds = namedProjects.Select(p => p.Id).ToHashSet();

                var projects = namedProjects
                    .Concat(byPath)
                    .DistinctBy(p => p.Id)
                    .ToList();

                if (projects.Count == 0)
                    return FileSyncResult.CannotApply;

                bool applied = false;
                foreach (var project in projects)
                {
                    // A .cs beside a .vbproj belongs to neither the VB project nor this method.
                    if (!IsProjectLanguageFor(project, target))
                        continue;

                    // Authority reaches only the project the caller named. Excluding a file from
                    // project A writes a Compile Remove in A; a project B that links the same file
                    // still compiles it, and removing B's document too would unresolve every type
                    // in that file across B, with nothing to put it back.
                    bool named = authoritative && namedIds.Contains(project.Id);

                    var result = change switch
                    {
                        FileChange.Deleted => await RemoveDocumentsAsync(live, project.Id, target, named),
                        FileChange.Created => await TryAddDocumentAsync(live, project.Id, target, named),
                        _ => await ReloadDocumentTextAsync(live, project.Id, target, named),
                    };

                    // One project that cannot be decided in place decides the whole call: the
                    // caller must fall back so MSBuild answers for every project at once.
                    if (result == FileSyncResult.CannotApply)
                        return FileSyncResult.CannotApply;

                    applied |= result == FileSyncResult.Applied;
                }

                if (!applied)
                    return FileSyncResult.NothingToDo;
            }
            finally
            {
                entry.LoadGate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // The entry was evicted while we waited on its gate — a branch switch racing an
            // analyzer rebuild does this. It is already gone, so there is nothing left to apply.
            return FileSyncResult.NothingToDo;
        }

        return FileSyncResult.Applied;
    }

    /// <summary>Whether the file's extension is the language this project compiles.</summary>
    private static bool IsProjectLanguageFor(Project project, string filePath) =>
        Path.GetExtension(filePath.AsSpan()) switch
        {
            var ext when ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) =>
                project.Language == LanguageNames.CSharp,
            var ext when ext.Equals(".vb", StringComparison.OrdinalIgnoreCase) =>
                project.Language == LanguageNames.VisualBasic,
            _ => false,
        };

    /// <remarks>
    /// The index before the scan. This is called once per (file, project) pair of a watched-file
    /// batch — a branch switch or a build output landing produces thousands of those — and each
    /// call used to enumerate the project's whole document list, taking a
    /// <see cref="Path.GetFullPath(string)"/> per document, all of it inside the load gate. That is
    /// the pause the incremental-apply path exists to remove.
    ///
    /// The scan stays as the fallback: <c>GetDocumentIdsWithFilePath</c> keys on the raw
    /// <c>FilePath</c> a project was loaded with, and <paramref name="fullPath"/> has been
    /// normalized by the caller, so the two can legitimately disagree on spelling.
    /// </remarks>
    private static List<Document> DocumentsAt(Solution solution, ProjectId projectId, string fullPath)
    {
        if (solution.GetProject(projectId) is not { } project)
            return [];

        var indexed = new List<Document>();
        foreach (var id in solution.GetDocumentIdsWithFilePath(fullPath))
        {
            if (id.ProjectId == projectId && project.GetDocument(id) is { } document)
                indexed.Add(document);
        }

        if (indexed.Count > 0)
            return indexed;

        return project.Documents
            .Where(d => d.FilePath is { Length: > 0 } fp
                && string.Equals(Path.GetFullPath(fp), fullPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static async Task<FileSyncResult> RemoveDocumentsAsync(
        MSBuildWorkspace live, ProjectId projectId, string fullPath, bool authoritative)
    {
        // The file is still there. Many writers replace a file by unlinking and recreating it, or
        // by renaming a temporary over it, which produces a delete and a create for a file that
        // never actually went away. Dropping the document then would unresolve every type it
        // declares, solution-wide, with nothing to ever put it back — so this is a content change,
        // and the new bytes still have to be read rather than the event being discarded.
        //
        // Unless the caller is what removed it from the project: "exclude from project" leaves the
        // file exactly where it is, and is the one case where a file on disk really has stopped
        // being compiled.
        if (!authoritative && File.Exists(fullPath))
            return await ReloadDocumentTextAsync(live, projectId, fullPath);

        bool any = false;
        foreach (var document in DocumentsAt(live.CurrentSolution, projectId, fullPath))
        {
            Lsp.AnalyzerDiagnosticCache.Evict(document.Id);
            live.OnDocumentRemoved(document.Id);
            any = true;
        }
        return any ? FileSyncResult.Applied : FileSyncResult.NothingToDo;
    }

    /// <summary>Whether <paramref name="path"/> sits inside <paramref name="root"/>.</summary>
    private static bool IsUnderDirectory(string path, string root)
    {
        string normalized = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return path.Length >= normalized.Length
            && path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
            && (path.Length == normalized.Length
                || path[normalized.Length] == Path.DirectorySeparatorChar
                || path[normalized.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>The file's text, or null when it cannot be read right now.</summary>
    private static SourceText? TryReadDisk(string fullPath)
    {
        try
        {
            using var stream = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return SourceText.From(stream);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Re-reads a closed file from disk. This is the case an external edit produces — a checkout, a
    /// formatter, another agent — and it is a text change, not a document-set change, so no
    /// re-evaluation is needed and the projects stay loaded.
    /// </summary>
    private static async Task<FileSyncResult> ReloadDocumentTextAsync(
        MSBuildWorkspace live, ProjectId projectId, string fullPath, bool authoritative = false)
    {
        if (!File.Exists(fullPath))
            return FileSyncResult.NothingToDo;

        // An open buffer outranks disk: the editor's unsaved text is the truth, and the buffer
        // bridge already put it in the workspace. This is also what makes saving free — the text
        // reached the workspace on didChange, and the watcher event the save produces is a
        // restatement of what is already there.
        //
        // Checked after the document lookup below rather than before it, because a file with no
        // document yet has nothing for the buffer to outrank: a .designer.cs open in the editor
        // when it is generated for the first time would otherwise be declined here and never enter
        // the workspace at all.
        bool isOpen = OpenDocumentStore.IsOpen(fullPath);

        var documents = DocumentsAt(live.CurrentSolution, projectId, fullPath);

        if (isOpen && documents.Count > 0)
            return FileSyncResult.NothingToDo;

        // What the file says now, so a write that changed nothing costs nothing. A formatter that
        // reformats to the same text, a generator re-emitting identical output, and a checkout that
        // restores a file to the content it already had all land here — and re-applying the loader
        // would give the document a new version, move its project's dependent semantic version, and
        // invalidate the analyzer results and pull-diagnostics ids of every file in it.
        var disk = TryReadDisk(fullPath);

        // A watcher can report a file it was not previously watching as merely Changed — after a
        // recursive-watch reset, or for a file created inside a directory created in the same
        // batch. Nothing else would ever pick it up, so treat it as the arrival it is.
        // Carried through: a caller that owns this file — the designer generator regenerating a
        // partial for the first time — is telling us it is compiled, and dropping the flag here
        // sent it straight into the legacy-project refusal it was passed to override.
        if (documents.Count == 0)
            return await TryAddDocumentAsync(live, projectId, fullPath, authoritative);

        bool any = false;
        foreach (var document in documents)
        {
            // Materialized rather than probed: TryGetText answers false for a document whose
            // text was never realized, which on a cold solution is most of them — the guard would
            // be skipped and every file in a checkout re-stamped, unchanged bytes included.
            if (disk is not null && (await document.GetTextAsync()).ContentEquals(disk))
                continue;

            // The cached analyzer results describe the previous text, and the entry is keyed by
            // DocumentId — which an in-place text change keeps. Nothing else drops them, so
            // without this the sweep would serve pre-change diagnostics, at pre-change line
            // positions, for the rest of the session.
            Lsp.AnalyzerDiagnosticCache.Evict(document.Id);

            live.OnDocumentTextLoaderChanged(
                document.Id, new FileTextLoader(fullPath, defaultEncoding: null));
            any = true;
        }
        return any ? FileSyncResult.Applied : FileSyncResult.NothingToDo;
    }

    /// <summary>
    /// Adds a document for a newly created file, but only where the project's own contents show
    /// that files in that directory are compiled.
    /// </summary>
    /// <remarks>
    /// An SDK project globs its compile items, but the glob honours <c>Compile Remove</c>,
    /// <c>DefaultItemExcludes</c> and <c>EnableDefaultCompileItems=false</c>, so "it is under the
    /// project directory" does not mean "it is compiled" — inventing a document for an excluded file
    /// produces duplicate-definition errors against the code that legitimately owns those types. A
    /// sibling document in the same directory is evidence the glob reaches there; without one the
    /// caller falls back to letting MSBuild decide.
    /// </remarks>
    private static async Task<FileSyncResult> TryAddDocumentAsync(
        MSBuildWorkspace live, ProjectId projectId, string fullPath, bool authoritative = false)
    {
        // A create event for a file that is already gone again: adding it would hand the project a
        // document whose loader throws the first time anything reads it.
        if (!File.Exists(fullPath))
            return FileSyncResult.NothingToDo;

        var solution = live.CurrentSolution;

        // The project already lists it — a file restored after being deleted, or one the project
        // named before it existed. The document is there; what changed is that it is now readable.
        if (DocumentsAt(solution, projectId, fullPath).Count > 0)
            return await ReloadDocumentTextAsync(live, projectId, fullPath, authoritative);

        var project = solution.GetProject(projectId);
        if (project is null || Path.GetDirectoryName(fullPath) is not { Length: > 0 } directory)
            return FileSyncResult.CannotApply;

        // A legacy project compiles what it lists, and a file that just appeared is not listed —
        // so it is not compiled, and reloading would reach that same conclusion after several
        // seconds of MSBuild. Whoever adds it to the project has to write the .csproj to do so,
        // and that write is its own event, handled by the reload above.
        //
        // That reasoning only holds while we are guessing. A caller that has just written the
        // Compile item is telling us the file is compiled, and answering "nothing to do" left it
        // invisible: the caller reads that as success so no fallback runs, and its own .csproj
        // write is then suppressed as an echo of itself.
        if (!authoritative && PathHelper.RequiresMsBuild(project.FilePath ?? ""))
            return FileSyncResult.NothingToDo;

        if (!authoritative && !GlobReaches(project, directory))
            return FileSyncResult.CannotApply;

        live.OnDocumentAdded(DocumentInfo.Create(
            DocumentId.CreateNewId(projectId, Path.GetFileName(fullPath)),
            Path.GetFileName(fullPath),
            // Folders is what namespace-sync and the file-scoped-namespace fixes read to compute a
            // default namespace; an empty list makes them propose the wrong one.
            folders: FoldersFor(project, fullPath),
            loader: new FileTextLoader(fullPath, defaultEncoding: null),
            filePath: fullPath));

        return FileSyncResult.Applied;
    }

    /// <summary>
    /// Whether the project's compile glob reaches <paramref name="directory"/>.
    /// </summary>
    /// <remarks>
    /// A document already in that directory settles it outright. Without one — a brand-new folder,
    /// which is an ordinary thing to make — the project file decides: the SDK's default glob takes
    /// every <c>.cs</c> under the project unless the author turned defaults off or excluded
    /// something, and only then can the answer be anything but yes. Being unsure returns false and
    /// the caller lets MSBuild answer, because inventing a document for a file the compiler does
    /// not see produces duplicate-definition errors against whatever legitimately owns those types.
    /// </remarks>
    private static bool GlobReaches(Project project, string directory)
    {
        // Only inside the project's own tree. A linked item — <Compile Include="..\Shared\X.cs" /> —
        // puts a document in a directory the glob does not cover, so treating it as evidence would
        // add every new file dropped beside it, and those types then collide with the project that
        // legitimately owns them.
        string? projectDir = Path.GetDirectoryName(project.FilePath);
        if (projectDir is not { Length: > 0 }
            || !IsUnderDirectory(directory, Path.GetFullPath(projectDir)))
        {
            return false;
        }

        // The cached predicate first. It is a mtime-keyed lookup, while the sibling scan below
        // takes a GetFullPath per document in the project — and for an ordinary SDK project the
        // glob answers true, which makes the scan dead code. Pure reorder of an OR.
        if (project.FilePath is { Length: > 0 } projectPath && HasPlainDefaultCompileGlob(projectPath))
            return true;

        return project.Documents.Any(d =>
            d.FilePath is { Length: > 0 } fp
            && string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(fp)), directory, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Project file path → (stamp it was read at, whether its compile glob is unqualified).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Ticks, bool Plain)>
        s_plainGlob = new(StringComparer.OrdinalIgnoreCase);

    private static bool HasPlainDefaultCompileGlob(string projectPath)
    {
        try
        {
            long ticks = new FileInfo(projectPath).LastWriteTimeUtc.Ticks;
            if (s_plainGlob.TryGetValue(projectPath, out var cached) && cached.Ticks == ticks)
                return cached.Plain;

            // A Directory.Build.props above the project can set EnableDefaultCompileItems,
            // DefaultItemExcludes or repo-wide Compile Remove items, none of which are visible in
            // the project file — so those files are read too. Their mere existence is not enough
            // to refuse: nearly every real repository has one, and treating that as "unknowable"
            // meant the first file in any new folder fell back to reloading the whole solution.
            if (ImportsConstrainTheGlob(projectPath))
            {
                s_plainGlob[projectPath] = (ticks, false);
                return false;
            }

            var document = System.Xml.Linq.XDocument.Load(projectPath);
            bool plain = document.Root is { } root
                && (root.Attribute("Sdk") is not null
                    || root.Elements().Any(e => e.Name.LocalName == "Sdk"))
                && !root.Descendants().Any(e =>
                    (e.Name.LocalName == "EnableDefaultCompileItems"
                        && string.Equals(e.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase))
                    || e.Name.LocalName == "DefaultItemExcludes"
                    || (e.Name.LocalName == "Compile" && e.Attribute("Remove") is not null));

            s_plainGlob[projectPath] = (ticks, plain);
            return plain;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a <c>Directory.Build.props</c> at or above the project says anything that could
    /// narrow the default compile glob.
    /// </summary>
    /// <remarks>
    /// Only the three things that can: turning defaults off, excluding item patterns, or removing
    /// compile items. A props file that merely sets a version or a target framework — which is what
    /// most of them do — leaves the glob exactly as the SDK defines it, and refusing to reason
    /// about those projects would cost a full reload every time someone makes a folder.
    /// </remarks>
    private static bool ImportsConstrainTheGlob(string projectPath)
    {
        try
        {
            var pending = new HashSet<string>(
                ["Directory.Build.props", "Directory.Build.targets"], StringComparer.OrdinalIgnoreCase);

            for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(projectPath))!);
                 dir is not null;
                 dir = dir.Parent)
            {
                foreach (string name in (string[])["Directory.Build.props", "Directory.Build.targets"])
                {
                    if (!pending.Contains(name))
                        continue;

                    string path = Path.Combine(dir.FullName, name);
                    if (!File.Exists(path))
                        continue;

                    // MSBuild stops at the first one it finds rather than merging every ancestor,
                    // so this does too — walking to the filesystem root let a stray props file in a
                    // user profile quietly force every project on the machine into the slow path.
                    //
                    // Per file name, because MSBuild runs two independent searches: props imported
                    // at the top, targets at the bottom. Sharing one flag meant a Directory.Build
                    // .targets beside the project ended the search for a Directory.Build.props at
                    // the repo root, so its DefaultItemExcludes went unseen.
                    pending.Remove(name);

                    var root = System.Xml.Linq.XDocument.Load(path).Root;
                    if (root is not null && root.Descendants().Any(IsGlobConstraint))
                        return true;
                }

                if (pending.Count == 0)
                    return false;
            }
        }
        catch
        {
            // Unreadable is unknowable, and guessing wrong here invents documents.
            return true;
        }

        return false;
    }

    private static bool IsGlobConstraint(System.Xml.Linq.XElement e) =>
        (e.Name.LocalName == "EnableDefaultCompileItems"
            && string.Equals(e.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase))
        || e.Name.LocalName == "DefaultItemExcludes"
        || (e.Name.LocalName == "Compile" && e.Attribute("Remove") is not null);

    private static IReadOnlyList<string> FoldersFor(Project project, string fullPath)
    {
        if (Path.GetDirectoryName(project.FilePath) is not { Length: > 0 } projectDir)
            return [];

        string relative = Path.GetRelativePath(projectDir, Path.GetDirectoryName(fullPath)!);
        if (relative is "." || relative.StartsWith("..", StringComparison.Ordinal))
            return [];

        return relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
    }


    /// <summary>
    /// Returns an immutable project snapshot with refreshed text for
    /// <paramref name="filePath"/> when the file on disk really differs from what the workspace
    /// holds. The workspace's internal solution is unchanged.
    /// </summary>
    /// <remarks>
    /// The content check is what makes this affordable. The trigger is a modification timestamp,
    /// and <see cref="CachedWorkspaceEntry.CachedAtUtc"/> is set once and never advanced — so after
    /// any build or checkout touched a file, every subsequent request forked the solution again,
    /// forever. Each fork re-stamped the document, which moved its project's dependent semantic
    /// version, which invalidated the analyzer cache and the pull-diagnostics result id of every
    /// document in that project and every project depending on it. A timestamp that moved without
    /// the bytes moving — which is what a rebuild produces — must cost nothing.
    /// </remarks>
    private static Project RefreshDocumentIfStale(
        CachedWorkspaceEntry entry, Project project, string? filePath, DateTime cacheTime)
    {
        var solution = project.Solution;
        List<(DocumentId Id, SourceText Text)>? stale = null;
        var watcher = entry.DirtyWatcher;
        bool watching = watcher is not null && !watcher.TakeOverflow();

        if (watching)
        {
            // The watcher recorded every disk change since load, so refresh is exactly the dirty
            // set — no stats over files nobody touched, and edits in *other* projects of this
            // workspace are picked up too, which the old target-only refresh never did. Events
            // arrive with no mtime promise (a copy that preserves timestamps still raises one),
            // so the event itself is the trigger and content is the judge.
            foreach (var evt in watcher!.Snapshot())
            {
                bool retry = false;
                foreach (var documentId in solution.GetDocumentIdsWithFilePath(evt.Key))
                {
                    if (solution.GetDocument(documentId) is not { } document)
                        continue;

                    switch (TryReadChanged(document, evt.Key, cacheTime, requireNewerMtime: false, out var text))
                    {
                        case ReadOutcome.Changed:
                            (stale ??= []).Add((documentId, text!));
                            break;
                        case ReadOutcome.Unreadable:
                            // Mid-write. Leave the event marked so the next request retries.
                            retry = true;
                            break;
                    }
                }

                if (!retry)
                    watcher.Clear(evt);
            }
        }
        else
        {
            // No watcher (unwatchable root) or it overflowed: fall back to statting every
            // document of the requested project, throttled per workspace. A request with no
            // named file is asking about the project as a whole and always sweeps — there is
            // no precise fallback that could cover for it.
            bool sweepDue = filePath is null;
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref entry.LastStaleSweepTicks);
            if (now - last >= StaleSweepInterval
                && Interlocked.CompareExchange(ref entry.LastStaleSweepTicks, now, last) == last)
            {
                sweepDue = true;
            }

            if (sweepDue)
            {
                foreach (var document in project.Documents)
                {
                    if (document.FilePath is not { Length: > 0 } path)
                        continue;
                    if (TryReadChanged(document, path, cacheTime, requireNewerMtime: true, out var text)
                        == ReadOutcome.Changed)
                    {
                        (stale ??= []).Add((document.Id, text!));
                    }
                }
            }
        }

        // The named file is always checked precisely, watcher or not: a watcher event is
        // delivered asynchronously, and the write this request is about may have beaten it here.
        if (filePath is not null
            && (stale is null || !stale.Any(s => solution.GetDocument(s.Id)?.FilePath?.Equals(
                filePath, StringComparison.OrdinalIgnoreCase) == true))
            && FindDocumentInProject(project, filePath) is { } target
            && TryReadChanged(target, filePath, cacheTime, requireNewerMtime: true, out var targetText)
                == ReadOutcome.Changed)
        {
            (stale ??= []).Add((target.Id, targetText!));
        }

        if (stale is null)
            return project;

        var updatedSolution = MemoizedRefresh(entry, solution, stale);
        return updatedSolution.GetProject(project.Id) ?? project;
    }

    /// <summary>How long a workspace coasts between whole-project staleness sweeps when no
    /// watcher covers it. Long enough that the LSP request stream never pays the per-document
    /// stats twice for one burst, short enough that an edit-then-ask MCP exchange always lands
    /// after a fresh sweep.</summary>
    private const long StaleSweepInterval = 2_000;

    private enum ReadOutcome { Unchanged, Changed, Unreadable }

    /// <summary>Reads the file's disk text when it genuinely differs from what the workspace
    /// holds. <see cref="ReadOutcome.Unchanged"/> also covers a file an editor buffer owns
    /// (the overlay already applied it, and disk says nothing about unsaved edits) and one whose
    /// mtime gate says nothing moved; <see cref="ReadOutcome.Unreadable"/> is a file mid-write,
    /// where what the workspace holds is the better answer than no answer.</summary>
    private static ReadOutcome TryReadChanged(
        Document document, string filePath, DateTime cacheTime, bool requireNewerMtime,
        out SourceText? text)
    {
        text = null;

        if (OpenDocumentStore.IsOpen(filePath))
            return ReadOutcome.Unchanged;

        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists || (requireNewerMtime && fileInfo.LastWriteTimeUtc <= cacheTime))
            return ReadOutcome.Unchanged;

        SourceText disk;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            disk = SourceText.From(stream);
        }
        catch (IOException)
        {
            return ReadOutcome.Unreadable;
        }

        if (document.TryGetText(out var current) && current.ContentEquals(disk))
            return ReadOutcome.Unchanged;

        text = disk;
        return ReadOutcome.Changed;
    }

    /// <summary>
    /// The forked solution for one refreshed document, memoized per (base solution, document,
    /// disk content) so the same disk text refreshed again returns the <em>same</em>
    /// <see cref="Solution"/> instance rather than an equivalent one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fork was discarded with the request that made it, so every semantic question asked of
    /// the next one replayed the tree replace and re-bound the project: a document's semantic-model
    /// weak reference and the solution's frozen-partial memo both hang off the instance, and both
    /// died with it. Nothing about the answer changes here — only whether the work behind it is
    /// done once or once per request, forever, for a file that stopped changing.
    /// </para>
    /// <para>
    /// Keyed on the base solution by reference, as the buffer overlay is: any move of
    /// <c>CurrentSolution</c> (an incremental project add, a reconciled buffer) makes every fork
    /// taken from the old one an answer about a solution that no longer exists. The content
    /// checksum rather than the <see cref="SourceText"/> instance, because the text is read fresh
    /// from disk on each call and is never the same object twice.
    /// </para>
    /// <para>
    /// Bounded, and cleared wholesale when it overflows: each retained fork pins a compilation, and
    /// a session that walks a large project would otherwise accumulate one per file it touched.
    /// </para>
    /// </remarks>
    private static Solution MemoizedRefresh(
        CachedWorkspaceEntry entry, Solution baseSolution,
        IReadOnlyList<(DocumentId Id, SourceText Text)> staleDocuments)
    {
        lock (entry.RefreshLock)
        {
            if (!ReferenceEquals(entry.RefreshBase, baseSolution))
            {
                entry.RefreshBase = baseSolution;
                entry.RefreshResult = baseSolution;
                entry.RefreshedDocuments.Clear();
            }

            // One chain rather than a fork per document: each stale document's text is applied on
            // top of what is already there, so a request that names file A and one that names
            // file B converge on the same Solution instance — and its semantic models — instead
            // of building rival forks that each miss the other's refresh.
            // Overflow restarts the chain with just this batch, checked up front so a reset can
            // never throw away part of the batch it is applying. The chain holds one Solution
            // however many documents it carries, so the bound is about not letting a session that
            // walks the whole tree keep every replaced syntax tree alive forever.
            int newDocuments = staleDocuments.Count(d => !entry.RefreshedDocuments.ContainsKey(d.Id));
            if (entry.RefreshedDocuments.Count + newDocuments > MaxMemoizedRefreshes)
            {
                entry.RefreshResult = baseSolution;
                entry.RefreshedDocuments.Clear();
            }

            var result = entry.RefreshResult ?? baseSolution;
            foreach (var (documentId, text) in staleDocuments)
            {
                var checksum = text.GetChecksum();
                if (entry.RefreshedDocuments.TryGetValue(documentId, out var applied)
                    && applied.SequenceEqual(checksum))
                {
                    continue;
                }

                result = result.WithDocumentText(documentId, text);
                entry.RefreshedDocuments[documentId] = checksum;
            }

            entry.RefreshResult = result;
            return result;
        }
    }

    /// <summary>How many refreshed-from-disk forks one entry keeps alive at once.</summary>
    private const int MaxMemoizedRefreshes = 16;

    private static bool TryGetValidCachedEntryLocked(string normalizedProjectPath, out CachedWorkspaceEntry? entry)
    {
        entry = null;
        if (!s_projectToCacheKey.TryGetValue(normalizedProjectPath, out var cacheKeys)
            || cacheKeys.FirstOrDefault(k => s_cache.ContainsKey(k)) is not { } cacheKey)
        {
            return false;
        }

        if (!s_cache.TryGetValue(cacheKey, out entry))
        {
            // Dangling reverse-index entry (entry was evicted) — drop it.
            s_projectToCacheKey.Remove(normalizedProjectPath);
            entry = null;
            return false;
        }

        if (!IsEntryStale(cacheKey, normalizedProjectPath, entry))
            return true;

        Console.Error.WriteLine(
            $"[WorkspaceService] Project/solution file changed, evicting cache for '{cacheKey}'.");
        EvictEntryLocked(cacheKey, entry, "its project or solution file changed on disk");
        entry = null;
        return false;
    }

    /// <summary>Why each cache key was last evicted, and when.</summary>
    /// <remarks>
    /// A reload is only ever surprising because the unload that made it necessary happened
    /// somewhere else, minutes earlier, for a reason nobody recorded. Keeping the reason means the
    /// load can say what it is recovering from instead of announcing itself as if it were the
    /// first time. Bounded because a long-lived daemon opens and drops many solutions.
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Reason, DateTime When)>
        s_lastEviction = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxRememberedEvictions = 64;

    /// <summary>The reason <paramref name="cacheKey"/> was last unloaded, if it was.</summary>
    internal static (string Reason, DateTime When)? LastEvictionOf(string cacheKey) =>
        s_lastEviction.TryGetValue(cacheKey, out var last) ? last : null;

    /// <summary>What a load was for, in the terms the user would recognise: the file they were
    /// looking at when it happened, or the project itself when nothing narrower is known.</summary>
    private static string Requested(string projectPath, string? targetFilePath) =>
        targetFilePath is { Length: > 0 } target
            ? $"'{Path.GetFileName(target)}'"
            : $"'{Path.GetFileName(projectPath)}'";

    private static void RecordEviction(string cacheKey, string reason)
    {
        if (s_lastEviction.Count >= MaxRememberedEvictions)
            s_lastEviction.Clear();

        s_lastEviction[cacheKey] = (reason, DateTime.UtcNow);
    }

    private static void EvictEntryLocked(string cacheKey, CachedWorkspaceEntry entry, string reason)
    {
        RecordEviction(cacheKey, reason);

        s_cache.TryRemove(cacheKey, out _);

        // Only this entry's membership. A project that another loaded workspace also serves keeps
        // its mapping to that one — dropping the whole row made every other entry holding it
        // unreachable, so nothing could ever invalidate them again.
        foreach (var (project, keys) in s_projectToCacheKey.ToList())
        {
            keys.Remove(cacheKey);
            if (keys.Count == 0)
                s_projectToCacheKey.Remove(project);
        }

        UnregisterShadowDirsLocked(cacheKey, entry.ShadowDirs);
        entry.Dispose();

        // Analyzer host entries are keyed per project FilePath, so evict for every project
        // this workspace served (a solution entry served many).
        foreach (var projectPath in entry.ProjectIds.Keys)
            AnalyzerService.EvictAnalyzersForProject(projectPath);

        // The funnel every eviction goes through — the idle sweep, the LRU cap, EvictAllAsync, a
        // .csproj change, an analyzer rebuild. Everything the editor holds was derived from a
        // workspace that no longer exists, and it has no way to find that out.
        //
        // Raised under s_cacheLock, which is safe because the subscriber only arms a debounce and
        // returns; it does not call back into this service.
        NotifyProjectSetChanged();
    }

    /// <summary>
    /// Records that the workspace cached under <paramref name="cacheKey"/> can serve the
    /// requested project plus every project in its loaded solution's closure. This powers
    /// both solution-wide dedup and reuse-by-membership.
    /// </summary>
    private static void RegisterProjectMappingsLocked(
        string cacheKey, string requestedProjectPath, Workspace workspace)
    {
        var watched = new List<string> { requestedProjectPath };

        Register(requestedProjectPath, cacheKey);
        foreach (var project in workspace.CurrentSolution.Projects)
        {
            if (!string.IsNullOrEmpty(project.FilePath))
            {
                string full = Path.GetFullPath(project.FilePath!);
                Register(full, cacheKey);
                watched.Add(full);
            }
        }

        // Every load path funnels through here, which is what makes this the one place a project
        // becomes "loaded" and therefore the one place its NuGet graph starts needing to be watched.
        // The call does its work on a pool thread: this runs under the cache lock.
        RestoreWatcher.WatchAll(watched);
    }

    private static void RegisterShadowDirsLocked(string cacheKey, IReadOnlyCollection<string>? dirs)
    {
        if (dirs is null || dirs.Count == 0)
            return;

        foreach (var dir in dirs)
        {
            if (!s_dirToProjects.TryGetValue(dir, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                s_dirToProjects[dir] = set;
            }
            set.Add(cacheKey);
        }
    }

    private static void UnregisterShadowDirsLocked(string cacheKey, IReadOnlyCollection<string>? dirs)
    {
        if (dirs is null || dirs.Count == 0)
            return;

        foreach (var dir in dirs)
        {
            if (s_dirToProjects.TryGetValue(dir, out var set))
            {
                set.Remove(cacheKey);
                if (set.Count == 0)
                    s_dirToProjects.Remove(dir);
            }
        }
    }

    /// <summary>
    /// Returns the normalized owning-solution path for <paramref name="projectPath"/> (the key a
    /// shared workspace is cached under), or <c>null</c> for loose / single-project solutions.
    /// Used by preload to warm each solution once.
    /// </summary>
    internal static string? GetOwnerSolutionKey(string projectPath) =>
        TryFindOwnerSolutionKey(Path.GetFullPath(projectPath));

    /// <summary>
    /// Walks up from the project to its nearest solution file and, if that solution lists
    /// the project and contains more than one project, returns the normalized solution path
    /// to use as the shared cache key. Returns <c>null</c> for loose / single-project
    /// solutions, which fall back to per-project loading.
    /// </summary>
    /// <remarks>
    /// This used to also return whether the solution was legacy, computed by
    /// <c>PathHelper.RequiresMsBuild(sln)</c> — which opens and regex-scans <em>every</em>
    /// <c>.csproj</c> the solution lists. Neither caller ever read the flag. On a 34-project
    /// solution that was 34 file opens per cache miss, spent to produce a value that was
    /// immediately discarded, and spent while holding the process-wide cache lock that every
    /// interactive hover and completion also has to take.
    /// </remarks>
    private static string? TryFindOwnerSolutionKey(string normalizedProjectPath)
    {
        try
        {
            string? sln = PathHelper.FindNearestSolution(normalizedProjectPath);
            if (string.IsNullOrEmpty(sln))
                return null;

            var projects = PathHelper.GetProjectsFromSolution(sln);
            if (projects.Count <= 1)
                return null;  // single-project solution gains nothing from sharing

            bool contains = projects.Any(p =>
                string.Equals(Path.GetFullPath(p), normalizedProjectPath, StringComparison.OrdinalIgnoreCase));

            return contains ? Path.GetFullPath(sln) : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WorkspaceService] Solution discovery failed for '{normalizedProjectPath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fired by <see cref="ShadowCopyService"/> when a watched analyzer / source-generator
    /// directory is rebuilt. Evicts every cached workspace that pinned an ALC for that
    /// directory so the next <see cref="GetOrOpenProjectAsync"/> call re-binds with fresh
    /// shadow copies and a fresh ALC, picking up the new generator binaries.
    /// </summary>
    private static void OnAnalyzerDirectoryChanged(string sourceDir)
    {
        // Wait synchronously on a thread-pool callback — eviction here is best-effort
        // and shouldn't dead-lock the watcher thread for long.
        if (!s_cacheLock.Wait(0))
        {
            // If the lock is busy, schedule a retry once it's free.
            _ = Task.Run(async () =>
            {
                await s_cacheLock.WaitAsync();
                try { EvictForDirLocked(sourceDir); }
                finally { s_cacheLock.Release(); }
            });
            return;
        }

        try { EvictForDirLocked(sourceDir); }
        finally { s_cacheLock.Release(); }
    }

    private static void EvictForDirLocked(string sourceDir)
    {
        if (!s_dirToProjects.TryGetValue(sourceDir, out var cacheKeys))
            return;

        foreach (var cacheKey in cacheKeys.ToList())
        {
            if (s_cache.TryGetValue(cacheKey, out var entry))
            {
                Console.Error.WriteLine(
                    $"[WorkspaceService] Analyzer rebuild in '{sourceDir}', evicting workspace for '{cacheKey}'.");
                EvictEntryLocked(cacheKey, entry, $"analyzers were rebuilt in '{sourceDir}'");
            }
        }
    }

    /// <summary>
    /// An entry is stale when the requested project's <c>.csproj</c> OR the entry's own key
    /// file (the <c>.sln</c> for a solution entry, or the same <c>.csproj</c> otherwise) was
    /// modified after the entry was cached.
    /// </summary>
    private static void Register(string projectPath, string cacheKey)
    {
        if (!s_projectToCacheKey.TryGetValue(projectPath, out var keys))
            s_projectToCacheKey[projectPath] = keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        keys.Add(cacheKey);
    }

    private static bool IsEntryStale(string cacheKey, string normalizedProjectPath, CachedWorkspaceEntry entry)
    {
        return IsChangedBySomeoneElse(normalizedProjectPath, entry.CachedAtUtc)
            || IsChangedBySomeoneElse(cacheKey, entry.CachedAtUtc);
    }

    /// <summary>
    /// Whether the file is newer than the snapshot <em>and</em> that is not simply our own write.
    /// </summary>
    /// <remarks>
    /// <see cref="CachedWorkspaceEntry.CachedAtUtc"/> is stamped once and never advanced, so any
    /// write to a project or solution file makes its entry stale forever after — and every
    /// mutating operation this server performs ends by writing one. Adding a package, renaming a
    /// solution folder, excluding a file: each already invalidated exactly what it changed, and
    /// then the next request that happened to touch that project threw the whole workspace away
    /// again and reloaded it from MSBuild.
    ///
    /// Suppressed only for writes we can still recognise as ours, byte for byte. Anyone else's
    /// edit — another editor, a script, a checkout — is a real change and still evicts.
    /// </remarks>
    private static bool IsChangedBySomeoneElse(string path, DateTime cacheTime) =>
        IsFileNewerThan(path, cacheTime) && !SelfWriteTracker.WasWrittenByUs(path);

    private static bool IsFileNewerThan(string path, DateTime cacheTime)
    {
        var info = new FileInfo(path);
        return info.Exists && info.LastWriteTimeUtc > cacheTime;
    }

    private static (Workspace Workspace, Project Project) CreateProjectSnapshot(
        CachedWorkspaceEntry entry, string requestedProjectPath, string? targetFilePath)
    {
        entry.LastAccessedUtc = DateTime.UtcNow;
        var project = entry.GetProject(requestedProjectPath);

        project = ApplyOpenDocumentOverlay(entry, project);

        if (targetFilePath != null)
            project = RefreshDocumentIfStale(entry, project, targetFilePath, entry.CachedAtUtc);

        return (entry.Workspace, project);
    }

    /// <summary>
    /// Overlays every open editor buffer (<see cref="OpenDocumentStore"/>) onto the snapshot,
    /// so cross-file analysis (find usages, diagnostics, rename) sees unsaved edits in ALL
    /// open files, not just the requested one. The forked solution is memoized per store
    /// generation — rebuilding it on every request would re-fork N documents each call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="OpenDocumentStore.OverlayGeneration"/> rather than
    /// <see cref="OpenDocumentStore.Generation"/>: a rebuild that produces the same texts still
    /// produces a <em>new</em> <see cref="Solution"/>, and every compilation, semantic version and
    /// downstream cache keyed on one goes with it. Only a buffer this method could actually apply
    /// is allowed to cause that.
    /// </para>
    /// <para>
    /// When a rebuild is unavoidable it re-applies onto the <em>previous overlay</em> and touches
    /// only the buffers whose text object actually moved. Restarting from the base solution each
    /// time re-stamped every open document — <c>WithDocumentText</c> compares texts by reference,
    /// and the store's <see cref="SourceText"/> is never the one the base solution loaded from
    /// disk, so all of them came back with a fresh version. That is what made opening one file
    /// look like a full reload: every other open document's dependent semantic version moved with
    /// it, which invalidated its cached analyzer run and changed its pull-diagnostics resultId, so
    /// the editor dropped the squiggles it had and asked for all of them again.
    /// </para>
    /// </remarks>
    private static Project ApplyOpenDocumentOverlay(CachedWorkspaceEntry entry, Project project)
    {
        if (OpenDocumentStore.IsEmpty)
            return project;

        long generation = OpenDocumentStore.OverlayGeneration;
        var baseSolution = project.Solution;
        Solution? overlay;
        lock (entry.OverlayLock)
        {
            // The base-solution check catches incremental project adds, which change
            // CurrentSolution without bumping the store generation.
            if (entry.OverlayGeneration == generation && ReferenceEquals(entry.OverlayBase, baseSolution))
            {
                overlay = entry.OverlaySolution;
            }
            else
            {
                var open = OpenDocumentStore.SnapshotAll();

                // Reusing the previous overlay is only sound while it is a strict subset of what
                // is open now: a closed buffer has to revert to the text on disk, and the only way
                // back to that is the base solution.
                bool reusable =
                    entry.OverlaySolution is not null
                    && ReferenceEquals(entry.OverlayBase, baseSolution)
                    && entry.OverlayTexts.Count > 0
                    && StillCoversEveryOverlaidDocument(entry, open, baseSolution);

                var solution = reusable ? entry.OverlaySolution! : baseSolution;
                var applied = reusable
                    ? new Dictionary<DocumentId, SourceText>(entry.OverlayTexts)
                    : new Dictionary<DocumentId, SourceText>();

                bool any = reusable;
                foreach (var (path, text) in open)
                {
                    // Multi-targeting: the same file can back several DocumentIds.
                    foreach (var docId in solution.GetDocumentIdsWithFilePath(path))
                    {
                        // Reference equality, matching what WithDocumentText itself compares:
                        // an untouched buffer must not be re-applied, or it takes a new version
                        // stamp and drags every dependent project's semantic version with it.
                        if (applied.TryGetValue(docId, out var current) && ReferenceEquals(current, text))
                            continue;

                        // Already reconciled into the workspace itself, which is the normal case —
                        // the fork exists only for buffers whose project loaded after they opened.
                        //
                        // Content, not reference: a buffer that matches disk is deliberately never
                        // pushed into the workspace, so the instance there is the one loaded from
                        // the file. Comparing identity would fork for every such buffer and undo
                        // the reconcile's whole point.
                        if (solution.GetDocument(docId) is { } live
                            && live.TryGetText(out var inWorkspace)
                            && inWorkspace.ContentEquals(text))
                        {
                            applied[docId] = text;
                            continue;
                        }

                        solution = solution.WithDocumentText(docId, text);
                        applied[docId] = text;
                        any = true;
                    }
                }

                overlay = any ? solution : null;

                entry.OverlaySolution = overlay;
                entry.OverlayBase = baseSolution;
                entry.OverlayGeneration = generation;
                entry.OverlayTexts = overlay is null
                    ? new Dictionary<DocumentId, SourceText>()
                    : applied;
            }
        }

        return overlay?.GetProject(project.Id) ?? project;
    }

    /// <summary>
    /// Whether every document the memoized overlay has text for is still open. False means a
    /// buffer was closed (or its project reloaded out from under it) and the overlay has to be
    /// rebuilt from disk state rather than extended.
    /// </summary>
    private static bool StillCoversEveryOverlaidDocument(
        CachedWorkspaceEntry entry, List<(string Path, SourceText Text)> open, Solution baseSolution)
    {
        var live = new HashSet<DocumentId>();
        foreach (var (path, _) in open)
        {
            foreach (var docId in baseSolution.GetDocumentIdsWithFilePath(path))
                live.Add(docId);
        }

        foreach (var docId in entry.OverlayTexts.Keys)
        {
            if (!live.Contains(docId))
                return false;
        }

        return true;
    }

    private static void EvictExpiredEntries(object? state)
    {
        // This runs on a ThreadPool thread from a Timer: any exception that escapes here is
        // unhandled and CRASHES THE PROCESS (observed as "Test host process crashed" during
        // teardown, where Console/semaphore/workspace disposal can throw). So the whole body
        // is wrapped, the lock acquire is guarded, and each eviction is isolated.
        bool acquired = false;
        try
        {
            try { acquired = s_cacheLock.Wait(0); }
            catch (ObjectDisposedException) { return; } // shutting down
            if (!acquired)
                return; // another operation holds the lock — skip this cycle

            // Idle eviction is for the MCP case, where a workspace is loaded to answer a question
            // and then nobody comes back. An editor is the opposite: quiet means the user is
            // reading, or was in a meeting, and the files are still open in front of them.
            //
            // Evicting under a connected editor is what "I didn't use it for a while and now it
            // doesn't work" was. Ten minutes of not typing threw the solution away, silently —
            // the sweep says so on Console.Error, which in the shared-daemon setup is a process
            // the user is not looking at — and the next request had to load it all again from
            // cold, with nothing on screen to say why the editor had gone dead.
            //
            // The LRU cap below still runs, so memory stays bounded either way. That one is
            // deliberate: it evicts because there are too many solutions open at once, which is a
            // real reason, rather than because time passed.
            if (!Lsp.LspSessionRegistry.HasSessions)
            {
                var now = DateTime.UtcNow;
                var expired = s_cache
                    .Where(kvp => (now - kvp.Value.LastAccessedUtc) > IdleTimeout)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expired)
                    TryEvictLoggedLocked(key, "idle workspace");
            }

            // LRU cap: after the idle sweep, if still over the cap, evict the
            // least-recently-used entries down to MaxCachedWorkspaces.
            if (s_cache.Count > MaxCachedWorkspaces)
            {
                var overflow = s_cache
                    .OrderBy(kvp => kvp.Value.LastAccessedUtc)
                    .Take(s_cache.Count - MaxCachedWorkspaces)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in overflow)
                    TryEvictLoggedLocked(key, $"LRU workspace (cap {MaxCachedWorkspaces})");
            }
        }
        catch
        {
            // Never let a background eviction take down the process.
        }
        finally
        {
            if (acquired)
            {
                try { s_cacheLock.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    /// <summary>Evicts one entry under the held lock, isolating failures so one bad disposal
    /// neither aborts the sweep nor escapes to crash the process. Caller holds s_cacheLock.</summary>
    private static void TryEvictLoggedLocked(string key, string label)
    {
        if (!s_cache.TryGetValue(key, out var entry))
            return;
        try
        {
            EvictEntryLocked(key, entry, label);

            // Through ServiceLog, not Console.Error. Under the shared daemon the console is a temp
            // file in a process the user is not looking at, so unloading a solution — the single
            // most surprising thing this service does on its own — was invisible. It is the answer
            // to "did the solution unload?", and the user could not have found it.
            ServiceLog.Warn(
                $"Unloaded {label} for '{Path.GetFileNameWithoutExtension(key)}'. "
                + "The next request will load it again.",
                key: $"evict:{key}");

        }
        catch (Exception ex)
        {
            try { ServiceLog.Warn($"Could not unload '{key}': {ex.Message}", key: $"evict-failed:{key}"); }
            catch { /* logging gone during teardown */ }
        }
    }

    /// <summary>
    /// Removes UnresolvedAnalyzerReference instances from all projects in the solution.
    /// These cause Roslyn's SymbolFinder APIs to crash with switch expression failures.
    /// </summary>
    private static Solution StripUnresolvedAnalyzerReferences(Solution solution, HashSet<ProjectId>? only = null)
    {
        foreach (var project in solution.Projects)
        {
            if (only is not null && !only.Contains(project.Id)) continue;
            foreach (var analyzerRef in project.AnalyzerReferences)
            {
                if (analyzerRef.GetType().Name == "UnresolvedAnalyzerReference")
                {
                    solution = solution.RemoveAnalyzerReference(project.Id, analyzerRef);
                }
            }
        }
        return solution;
    }

    /// <summary>
    /// Atomically replaces <paramref name="workspace"/>'s current solution with
    /// <paramref name="newSolution"/> without persisting any project-file changes to disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Goes through the protected <c>Workspace.SetCurrentSolution(Solution)</c> overload via
    /// reflection because <see cref="Workspace.TryApplyChanges"/> hands every edit to
    /// <c>MSBuildWorkspace</c>'s applier, and nothing the post-open pipeline does belongs in the
    /// .csproj. An analyzer-reference edit is round-tripped back into the project file as a
    /// shadow-copy temp path; an added metadata reference is written out as a <c>&lt;Reference&gt;</c>
    /// element — 117 of them for a legacy project that had its framework references injected.
    /// </para>
    /// <para>
    /// The metadata-reference path never even got that far. <c>ApplyMetadataReferenceAdded</c> asks
    /// <c>IsInGAC</c> first, which reaches Roslyn's <c>GlobalAssemblyCacheLocation</c> and P/Invokes
    /// the Fusion API in <c>clr.dll</c> — a .NET Framework-only native export that a .NET 10 host
    /// process does not have. So injecting framework references through <c>TryApplyChanges</c>
    /// failed with <c>DllNotFoundException('clr')</c>, and every legacy .NET Framework project that
    /// needed the injection was unopenable, under a message blaming the machine's .NET Framework
    /// install.
    /// </para>
    /// </remarks>
    private static void SwapCurrentSolutionInPlace(Workspace workspace, Solution newSolution)
    {
        if (s_setCurrentSolutionMethod is null)
        {
            // Fallback to TryApplyChanges if reflection failed — accept the disk-write side effect
            // rather than skipping the swap entirely. Guarded, because this is the path that used
            // to be taken unconditionally: it is the one that can throw DllNotFoundException('clr')
            // on a metadata-reference edit, and a workspace holding un-swapped analyzer or
            // framework references is still a usable workspace, while a faulted open is not.
            Console.Error.WriteLine(
                "[WorkspaceService] Reflection failed: Workspace.SetCurrentSolution not found; falling back to TryApplyChanges.");
            try
            {
                workspace.TryApplyChanges(newSolution);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[WorkspaceService] TryApplyChanges fallback failed ({ex.Message}); " +
                    "the workspace keeps its current solution.");
            }
            return;
        }

        s_setCurrentSolutionMethod.Invoke(workspace, [newSolution]);
    }

    /// <summary>
    /// Runs the post-open normalization pipeline over the workspace's current solution: strip
    /// unresolved analyzer references, rebind build-output analyzers/source-generators to shadow
    /// copies (so the originals stay unlocked for <c>dotnet build</c>), and inject missing
    /// framework references. <paramref name="newProjects"/> scopes the work to just those project
    /// IDs (null = all) so an incremental add doesn't reprocess — and recompile — already-loaded
    /// projects. Returns the shadow loader now in use and the NEW source directories it pinned on
    /// this call (for the rebuild-eviction watcher).
    /// </summary>
    private static (ShadowCopyAnalyzerAssemblyLoader? Loader, HashSet<string>? Dirs)
        ApplyPostOpenPipeline(
            MSBuildWorkspace workspace, HashSet<ProjectId>? newProjects,
            ShadowCopyAnalyzerAssemblyLoader? existingLoader)
    {
        var stripped = StripUnresolvedAnalyzerReferences(workspace.CurrentSolution, newProjects);
        if (stripped != workspace.CurrentSolution)
            SwapCurrentSolutionInPlace(workspace, stripped);

        // Rebind BEFORE anything can ask these projects for a compilation: Roslyn's default loader
        // opens the original analyzer DLL via PEReader on first compilation access, locking it on
        // disk — a rebind after that is too late. Nothing here forces a compilation any more (the
        // framework probe below reads metadata references only), so what keeps this ordering
        // load-bearing is that the projects are not reachable by any caller until this pipeline
        // returns and the cache mappings are published.
        var (rebound, loader, dirs) =
            RebindAnalyzerReferencesToShadowLoader(workspace.CurrentSolution, newProjects, existingLoader);
        if (rebound != workspace.CurrentSolution)
            SwapCurrentSolutionInPlace(workspace, rebound);

        var injected = InjectMissingFrameworkReferences(workspace.CurrentSolution, newProjects);
        if (injected != workspace.CurrentSolution)
            SwapCurrentSolutionInPlace(workspace, injected);

        return (loader, dirs);
    }

    /// <summary>
    /// Replaces every <see cref="AnalyzerFileReference"/> pointing at a non-NuGet path
    /// (typically a project-output source generator under <c>bin/</c>) with a new
    /// reference whose <c>FullPath</c> points at a shadow copy and whose loader is a
    /// per-workspace <see cref="ShadowCopyAnalyzerAssemblyLoader"/>.
    /// <para>
    /// Both the <c>FullPath</c> and the loader target the shadow copy: Roslyn's
    /// <c>AnalyzerFileReference.GetMetadata()</c> opens <c>FullPath</c> directly with a
    /// <see cref="System.Reflection.PortableExecutable.PEReader"/>, bypassing
    /// <see cref="IAnalyzerAssemblyLoader"/>, so leaving the original path here would
    /// still lock the source-generator DLL on disk and break <c>dotnet build</c>.
    /// </para>
    /// <para>
    /// Returns the rewritten solution, the shared loader (or <c>null</c> when nothing
    /// needed shadowing), and the set of <b>original</b> source directories the loader
    /// will pin (used by the rebuild-eviction watcher).
    /// </para>
    /// </summary>
    private static (Solution Solution, ShadowCopyAnalyzerAssemblyLoader? Loader, HashSet<string>? Dirs)
        RebindAnalyzerReferencesToShadowLoader(
            Solution solution,
            HashSet<ProjectId>? only = null,
            ShadowCopyAnalyzerAssemblyLoader? existingLoader = null)
    {
        var shadowCopy = ShadowCopyService.Instance;
        // Reuse the entry's loader on incremental adds so all shadow copies for one workspace
        // share a single ALC and one watched-directory set.
        ShadowCopyAnalyzerAssemblyLoader? loader = existingLoader;
        HashSet<string>? dirs = null;

        foreach (var project in solution.Projects.ToList())
        {
            if (only is not null && !only.Contains(project.Id)) continue;
            var oldRefs = project.AnalyzerReferences;
            if (oldRefs.Count == 0)
                continue;

            List<AnalyzerReference>? newRefs = null;
            for (int i = 0; i < oldRefs.Count; i++)
            {
                var r = oldRefs[i];
                if (r is AnalyzerFileReference fileRef
                    && !string.IsNullOrEmpty(fileRef.FullPath)
                    && shadowCopy.NeedsShadowCopy(fileRef.FullPath))
                {
                    loader ??= new ShadowCopyAnalyzerAssemblyLoader();
                    dirs ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    dirs.Add(Path.GetDirectoryName(Path.GetFullPath(fileRef.FullPath))!);

                    string shadowPath = loader.Register(fileRef.FullPath);

                    if (newRefs is null)
                    {
                        newRefs = new List<AnalyzerReference>(oldRefs.Count);
                        for (int j = 0; j < i; j++)
                            newRefs.Add(oldRefs[j]);
                    }
                    newRefs.Add(new AnalyzerFileReference(shadowPath, loader));
                }
                else if (newRefs is not null)
                {
                    newRefs.Add(r);
                }
            }

            if (newRefs is not null)
                solution = solution.WithProjectAnalyzerReferences(project.Id, newRefs);
        }

        return (solution, loader, dirs);
    }

    /// <summary>
    /// Detects projects missing core framework references (System.Object, System.Int32, etc.)
    /// and injects the appropriate references based on target framework.
    /// </summary>
    private static Solution InjectMissingFrameworkReferences(
        Solution solution, HashSet<ProjectId>? only)
    {
        foreach (var project in solution.Projects)
        {
            if (only is not null && !only.Contains(project.Id)) continue;
            if (ResolvesCorlib(project)) continue;

            var refsToAdd = GetFrameworkReferences(project);
            if (refsToAdd.Count == 0) continue;

            // Filter out duplicates
            var existingPaths = new HashSet<string>(
                project.MetadataReferences
                    .Select(r => r.Display ?? "")
                    .Where(d => !string.IsNullOrEmpty(d)),
                StringComparer.OrdinalIgnoreCase);

            var filtered = refsToAdd.Where(r => !existingPaths.Contains(r.Display ?? "")).ToList();
            if (filtered.Count == 0) continue;

            var framework = ProjectClassifier.Classify(project).Runtime;
            Console.Error.WriteLine($"[WorkspaceService] Project '{project.Name}' ({framework}) missing framework references, injecting {filtered.Count} assemblies.");

            solution = solution.WithProjectMetadataReferences(
                project.Id,
                project.MetadataReferences.Concat(filtered));
        }

        return solution;
    }

    /// <summary>
    /// Whether <see cref="SpecialType.System_Object"/> resolves from this project's metadata
    /// references alone — the test for "did MSBuild give us a framework at all".
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be <c>await project.GetCompilationAsync(ct)</c> followed by the same
    /// <c>GetSpecialType</c> call. That answers the question, and it answers it by parsing every
    /// document in the project and binding every reference — so loading a solution parsed the whole
    /// codebase, at load time, on the critical path, to ask something that depends only on
    /// <see cref="Project.MetadataReferences"/>. It cost 1.3 seconds on a single generated contracts
    /// project and grows with the source, not with the question.
    /// </para>
    /// <para>
    /// A bare <c>CSharpCompilation</c> over the same references gives the identical verdict:
    /// <c>GetSpecialType</c> reads the corlib's metadata and nothing else, and no syntax tree is
    /// involved either way. The reference metadata it touches is the same memory-mapped, globally
    /// cached metadata a real compilation of this project would touch, so the work is not repeated
    /// later — it is only no longer accompanied by a full parse.
    /// </para>
    /// <para>
    /// Not language-conditional: the probe is about metadata, and a VB or F# project's references
    /// resolve or fail to resolve exactly the same way when read through a C# compilation.
    /// </para>
    /// </remarks>
    private static bool ResolvesCorlib(Project project)
    {
        // No references at all means MSBuild evaluation produced nothing — ProjectFileInfo.CreateEmpty,
        // the shape Roslyn leaves behind for a project whose evaluation failed. Short-circuited
        // because building a compilation over an empty reference list to be told so is pure overhead.
        if (project.MetadataReferences.Count == 0)
            return false;

        try
        {
            var probe = CSharpCompilation.Create("corlib-probe", references: project.MetadataReferences);
            return probe.GetSpecialType(SpecialType.System_Object).TypeKind != TypeKind.Error;
        }
        catch (Exception ex)
        {
            // A reference that cannot be read as metadata (a deleted file, a native DLL wired in by
            // mistake) throws here. That is precisely the broken-references case this probe exists
            // to detect, so it is a "no", not a failure.
            Console.Error.WriteLine(
                $"[WorkspaceService] Framework probe for '{project.Name}' could not read its references: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Returns the correct framework reference assemblies for the project's target framework.
    /// </summary>
    private static List<MetadataReference> GetFrameworkReferences(Project project)
    {
        var refs = new List<MetadataReference>();

        if (ProjectClassifier.Classify(project).Runtime == RuntimeFlavor.NetFramework)
        {
            // .NET Framework — use reference assemblies from the targeting pack
            var refAssembliesBase = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Reference Assemblies", "Microsoft", "Framework", ".NETFramework");

            // Try common versions in order of preference
            string[] versions = ["v4.8.1", "v4.8", "v4.7.2", "v4.7.1", "v4.7", "v4.6.2", "v4.6.1", "v4.6", "v4.5.2", "v4.5.1", "v4.5"];
            string? refDir = null;
            foreach (var ver in versions)
            {
                var candidate = Path.Combine(refAssembliesBase, ver);
                if (Directory.Exists(candidate))
                {
                    refDir = candidate;
                    break;
                }
            }

            if (refDir is null)
            {
                Console.Error.WriteLine("[WorkspaceService] No .NET Framework reference assemblies found. Install the .NET Framework Developer Pack.");
                return refs;
            }

            string[] netfxAssemblies =
            [
                "mscorlib.dll", "System.dll", "System.Core.dll", "System.Data.dll",
                "System.Drawing.dll", "System.Web.dll", "System.Xml.dll", "System.Xml.Linq.dll",
                "System.Configuration.dll", "System.Runtime.Serialization.dll",
                "System.ServiceModel.dll", "System.Net.Http.dll", "System.ComponentModel.DataAnnotations.dll",
            ];

            foreach (var asm in netfxAssemblies)
            {
                var path = Path.Combine(refDir, asm);
                if (File.Exists(path))
                    refs.Add(MetadataReference.CreateFromFile(path));
            }

            // Also check Facades directory for netstandard.dll and type-forwarded assemblies
            var facadesDir = Path.Combine(refDir, "Facades");
            if (Directory.Exists(facadesDir))
            {
                foreach (var facadeDll in Directory.GetFiles(facadesDir, "*.dll"))
                    refs.Add(MetadataReference.CreateFromFile(facadeDll));
            }
        }
        else
        {
            // .NET Standard / .NET Core / .NET 5+ — use current runtime assemblies
            string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();

            string[] essentialAssemblies =
            [
                "netstandard.dll",
                "System.Runtime.dll",
                "mscorlib.dll",
                "System.dll",
                "System.Core.dll",
                "System.Collections.dll",
                "System.Linq.dll",
                "System.Threading.dll",
                "System.Threading.Tasks.dll",
                "System.IO.dll",
                "System.Text.RegularExpressions.dll",
                "System.ComponentModel.dll",
                "System.ComponentModel.Primitives.dll",
                "System.ObjectModel.dll",
                "System.Runtime.Extensions.dll",
                "System.Runtime.InteropServices.dll",
                "System.Collections.Concurrent.dll",
                "System.Diagnostics.Debug.dll",
            ];

            foreach (var asm in essentialAssemblies)
            {
                var path = Path.Combine(runtimeDir, asm);
                if (File.Exists(path))
                    refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        return refs;
    }


    /// <summary>
    /// Splits one project load into the three phases that can each dominate it — restore,
    /// <c>OpenProjectAsync</c>, post-open pipeline — so the load log says which one cost the time.
    /// </summary>
    /// <remarks>
    /// A single "loaded X in 900 ms" line cannot distinguish a slow NuGet restore from a slow
    /// MSBuild evaluation from a compilation forced by the post-open pipeline, and those three have
    /// nothing in common with each other: different fix, different owner, different risk. The cost
    /// of keeping them apart is one <see cref="Stopwatch"/> per load, against a load that is
    /// hundreds of milliseconds at its very best.
    /// </remarks>
    private sealed class LoadPhaseTimings
    {
        private readonly Stopwatch _watch = new();
        private long _lastMs;

        public long RestoreMs;

        /// <summary>Time spent queued behind another project's load on the same solution. Nonzero
        /// here is the signal that the gate, not the work, is the bottleneck.</summary>
        public long GateMs;

        public long OpenMs;
        public long PipelineMs;

        public void Start() => _watch.Restart();

        /// <summary>Charges everything since the previous mark to <paramref name="slot"/>.</summary>
        public void Mark(ref long slot)
        {
            long now = _watch.ElapsedMilliseconds;
            slot = now - _lastMs;
            _lastMs = now;
        }

        public override string ToString() =>
            $"[restore={RestoreMs}ms gate={GateMs}ms open={OpenMs}ms pipeline={PipelineMs}ms]";
    }

    private sealed class CachedWorkspaceEntry : IDisposable
    {
        public string CacheKey { get; }
        public Workspace Workspace { get; }

        /// <summary>The originally-requested project; the fallback when a path isn't mapped
        /// (e.g. a decompiled project whose FilePath is null).</summary>
        public ProjectId PrimaryProjectId { get; }

        /// <summary>Normalized .csproj path → ProjectId, for every project this workspace
        /// holds (the whole solution closure for a solution entry).</summary>
        /// <remarks>
        /// Concurrent because it is read outside <c>s_cacheLock</c> — by the snapshot path and by
        /// watched-file processing — while an incremental project load rewrites it under the lock.
        /// A plain <see cref="Dictionary{TKey,TValue}"/> read during a resize can return a wrong
        /// value, throw, or spin, and a branch switch arriving mid-load hits exactly that.
        /// </remarks>
        public System.Collections.Concurrent.ConcurrentDictionary<string, ProjectId> ProjectIds { get; }

        public DateTime CachedAtUtc { get; }
        public DateTime LastAccessedUtc { get; set; }

        /// <summary>The shared shadow-copy loader for this workspace. Grows as projects are
        /// added incrementally (a later project may be the first to need shadowing).</summary>
        public ShadowCopyAnalyzerAssemblyLoader? ShadowLoader { get; private set; }

        /// <summary>Original source-generator directories pinned by <see cref="ShadowLoader"/>;
        /// the rebuild-eviction watcher keys on these. Accumulates across incremental adds.</summary>
        public HashSet<string>? ShadowDirs { get; private set; }

        /// <summary>Temp directories (decompile reference copies) to delete on disposal.</summary>
        public IReadOnlyList<string>? TempDirs { get; }

        /// <summary>Serializes incremental <c>OpenProjectAsync</c> mutations of this workspace
        /// (MSBuildWorkspace is not safe for concurrent opens; reads stay safe via immutable
        /// solution snapshots).</summary>
        /// <remarks>
        /// Deliberately never disposed. An eviction disposes the entry while loads that captured
        /// it are still running or still queued on this gate, and a disposed
        /// <see cref="SemaphoreSlim"/> neither completes its pending waiters nor accepts a new
        /// wait — so disposal turned an ordinary eviction race into an
        /// <see cref="ObjectDisposedException"/> thrown out of whichever request triggered the
        /// load ("Cannot access a disposed object: 'System.Threading.SemaphoreSlim'"), which is
        /// what took the solution-wide warmup down mid-load. A SemaphoreSlim only holds an
        /// unmanaged handle once <c>AvailableWaitHandle</c> has been read, and nothing here reads
        /// it, so leaving it to the GC costs nothing. <see cref="IsDisposed"/> is what waiters
        /// check instead.
        /// </remarks>
        public SemaphoreSlim LoadGate { get; } = new(1, 1);

        /// <summary>
        /// Whether this entry has been evicted and its workspace torn down. Set inside
        /// <see cref="Dispose"/>; read by everything that acquires <see cref="LoadGate"/>, because
        /// acquiring it says only that no other load is running — not that there is still a
        /// workspace to load into.
        /// </summary>
        public volatile bool IsDisposed;

        /// <summary>Memoized open-editor-buffer overlay (see ApplyOpenDocumentOverlay).</summary>
        public object OverlayLock { get; } = new();
        public Solution? OverlaySolution { get; set; }
        public Solution? OverlayBase { get; set; }
        public long OverlayGeneration { get; set; } = -1;

        /// <summary>The text each document in <see cref="OverlaySolution"/> was overlaid with, so
        /// the next rebuild can re-apply only the buffers that moved instead of all of them.</summary>
        public Dictionary<DocumentId, SourceText> OverlayTexts { get; set; } = new();

        /// <summary>Memoized refreshed-from-disk fork (see MemoizedRefresh): one chained solution
        /// carrying every stale document's disk text, with the checksum each was refreshed from.
        /// Valid only against <see cref="RefreshBase"/>.</summary>
        public object RefreshLock { get; } = new();
        public Solution? RefreshBase { get; set; }
        public Solution? RefreshResult { get; set; }

        public Dictionary<DocumentId, ImmutableArray<byte>> RefreshedDocuments { get; } = new();

        /// <summary>When the last whole-project staleness sweep ran (Environment.TickCount64).
        /// The sweep is the fallback when <see cref="DirtyWatcher"/> is unavailable or lost
        /// events; with a live watcher the dirty set replaces it entirely.</summary>
        public long LastStaleSweepTicks;

        /// <summary>Disk changes under this workspace's root since load, recorded by a file
        /// watcher so refresh consumes exactly what changed. Null when the root cannot be
        /// watched, in which case the throttled stat sweep covers.</summary>
        public WorkspaceDirtyWatcher? DirtyWatcher { get; }

        public CachedWorkspaceEntry(
            string cacheKey,
            Workspace workspace,
            ProjectId primaryProjectId,
            ShadowCopyAnalyzerAssemblyLoader? shadowLoader,
            IReadOnlyCollection<string>? shadowDirs,
            IReadOnlyList<string>? tempDirs = null)
        {
            CacheKey = cacheKey;
            Workspace = workspace;
            PrimaryProjectId = primaryProjectId;
            CachedAtUtc = DateTime.UtcNow;
            LastAccessedUtc = DateTime.UtcNow;
            ShadowLoader = shadowLoader;
            ShadowDirs = shadowDirs is null ? null : new HashSet<string>(shadowDirs, StringComparer.OrdinalIgnoreCase);
            TempDirs = tempDirs;

            ProjectIds = new System.Collections.Concurrent.ConcurrentDictionary<string, ProjectId>(
                StringComparer.OrdinalIgnoreCase);
            RefreshProjectIds();

            DirtyWatcher = WorkspaceDirtyWatcher.TryCreate(Path.GetDirectoryName(cacheKey));
        }

        /// <summary>Re-syncs <see cref="ProjectIds"/> from the workspace's current solution after
        /// an incremental add. Cheap: a handful of projects.</summary>
        public void RefreshProjectIds()
        {
            foreach (var project in Workspace.CurrentSolution.Projects)
            {
                if (!string.IsNullOrEmpty(project.FilePath))
                    ProjectIds[Path.GetFullPath(project.FilePath!)] = project.Id;
            }
        }

        /// <summary>Folds the loader/dirs produced by an incremental post-open pass into the
        /// entry: adopts the loader if the entry had none, and unions the newly-pinned dirs.</summary>
        public void MergeShadow(ShadowCopyAnalyzerAssemblyLoader? loader, HashSet<string>? newDirs)
        {
            ShadowLoader ??= loader;
            if (newDirs is { Count: > 0 })
                (ShadowDirs ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).UnionWith(newDirs);
        }


        /// <summary>
        /// Resolves the <see cref="Project"/> for the requested path, falling back to the
        /// primary project when the path isn't a mapped .csproj (e.g. a decompiled manifest).
        /// </summary>
        public Project GetProject(string requestedProjectPath)
        {
            ProjectId id;
            if (ProjectIds.TryGetValue(requestedProjectPath, out var mapped))
            {
                id = mapped;
            }
            else
            {
                // Expected only for entries whose project has no FilePath (a decompiled
                // manifest). If a real .csproj is unexpectedly unmapped, returning the primary
                // would be the WRONG project — warn so it's diagnosable rather than silent.
                if (ProjectIds.Count > 0 &&
                    requestedProjectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine(
                        $"[WorkspaceService] '{requestedProjectPath}' not found in cached workspace '{CacheKey}'; " +
                        "falling back to the primary project.");
                }
                id = PrimaryProjectId;
            }

            return Workspace.CurrentSolution.GetProject(id)
                ?? throw new InvalidOperationException($"Cached project {id} no longer found in workspace.");
        }

        public void Dispose()
        {
            // First, so a load parked on the gate sees it the moment it gets in, rather than
            // walking into a workspace that is already being torn down.
            IsDisposed = true;
            DirtyWatcher?.Dispose();
            Workspace.Dispose();
            ShadowLoader?.Dispose();
            if (TempDirs is not null)
                foreach (var dir in TempDirs)
                    DecompiledSourceService.TryDeleteTempDir(dir);
        }
    }
}

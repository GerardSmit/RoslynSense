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

/// <summary>
/// Manages MSBuildWorkspace creation, project discovery, document lookup, and
/// workspace/project caching with configurable idle eviction.
/// </summary>
internal static class WorkspaceService
{
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

    private static readonly Dictionary<string, CachedWorkspaceEntry> s_cache = new(StringComparer.OrdinalIgnoreCase);
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
    /// → the <see cref="s_cache"/> key of the workspace that can serve it. One solution
    /// workspace serves all its member projects, so this maps every project in a loaded
    /// solution's transitive closure to that single cache entry. This is what gives both
    /// solution-wide dedup and reuse-by-membership for loose projects.
    /// </summary>
    private static readonly Dictionary<string, string> s_projectToCacheKey =
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
    /// The MEF composition every workspace runs on, built once for the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be built inside <see cref="CreateWorkspace"/>, so every workspace composed the
    /// whole of Roslyn's feature catalogue from scratch — several hundred milliseconds of assembly
    /// loading and export discovery, paid again for the second solution a window opened, and again
    /// for each short-lived workspace the batch loader creates.
    /// </para>
    /// <para>
    /// Sharing one is the intended use: a <c>MefHostServices</c> is a stateless container of
    /// exports, Roslyn's own <c>MefHostServices.DefaultHost</c> is a process-wide singleton for
    /// exactly this reason, and the per-workspace state that does exist lives on the
    /// <see cref="Workspace"/> rather than on the host.
    /// </para>
    /// <para>
    /// The own-assembly addition is what makes this a custom host rather than the default one: it
    /// exports no-op implementations of the VS-only Pythia contracts that the C# feature providers
    /// import, and without them composition fails at the first completion request
    /// (see <c>PythiaStubExports</c>).
    /// </para>
    /// </remarks>
    private static readonly Lazy<Microsoft.CodeAnalysis.Host.Mef.MefHostServices> s_hostServices =
        new(() => Microsoft.CodeAnalysis.Host.Mef.MefHostServices.Create(
                Microsoft.CodeAnalysis.Host.Mef.MefHostServices.DefaultAssemblies
                    .Add(typeof(NullPythiaSignatureHelpImplementation).Assembly)),
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The shared MEF composition, for the few places that need a workspace of their own rather
    /// than one from the cache.
    /// </summary>
    internal static Microsoft.CodeAnalysis.Host.HostServices HostServices => s_hostServices.Value;

    /// <summary>
    /// Builds the MEF composition ahead of the first request that needs it, on a background thread.
    /// </summary>
    /// <remarks>
    /// Pure warm-up: it loads no project, reads no solution and allocates nothing that is not going
    /// to be allocated anyway the moment the editor asks for anything semantic. It exists because
    /// the composition is unavoidable, fixed, and otherwise lands squarely inside the first request
    /// the user waits on.
    /// </remarks>
    public static void WarmHostServicesInBackground() =>
        _ = Task.Run(() =>
        {
            try
            {
                _ = s_hostServices.Value;
            }
            catch (Exception ex)
            {
                // Nothing awaits this. Left to the real caller to fail properly, with its own
                // error handling and its own message.
                Console.Error.WriteLine($"[WorkspaceService] MEF warm-up failed: {ex.Message}");
            }
        });

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
        PatchBuildHostBindingRedirects();
        TryRegisterVisualStudioMSBuild();
        RuntimeHelpers.RunClassConstructor(typeof(CSharpSyntaxTree).TypeHandle);
        s_evictionTimer = new Timer(EvictExpiredEntries, null, EvictionInterval, EvictionInterval);
        ShadowCopyService.Instance.AnalyzerDirectoryChanged += OnAnalyzerDirectoryChanged;
        DecompiledSourceService.CleanupOrphanedTempDirs();
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
        IReadOnlyDictionary<string, string>? extraProperties = null)
    {
        var properties = isLegacy ? CreateLegacyProperties() : CreateDefaultProperties();
        if (extraProperties is not null)
            foreach (var (key, value) in extraProperties)
                properties[key] = value;

        var workspace = MSBuildWorkspace.Create(properties, s_hostServices.Value);

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

                var phases = new LoadPhaseTimings();
                try
                {
                    progress.Report("Restoring packages");
                    phases.Start();
                    await RestoreService.EnsureRestoredAsync(normalizedPath, cancellationToken);
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
                        AddProjectsAndRewireReferences(msbuildWorkspace, seedInfos);
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
                RegisterProjectMappingsLocked(cacheKey, normalizedPath, workspace);
                RegisterShadowDirsLocked(cacheKey, shadowDirs);
                Console.Error.WriteLine(
                    $"[WorkspaceService] Cached workspace for '{cacheKey}' ({newEntry.ProjectIds.Count} project(s)).");

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
        phases.Mark(ref phases.RestoreMs);

        await entry.LoadGate.WaitAsync(cancellationToken);
        try
        {
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

        // Restore first, once, for everything: it is per solution and outside every gate, so doing
        // it here rather than inside the loop below means one subprocess for the whole batch.
        await RestoreService.EnsureRestoredAsync(normalized[0], cancellationToken);

        // The first project both establishes the solution workspace (via the ordinary cached path,
        // including all its fallback behaviour) and tells us which entry the rest belong in.
        await GetOrOpenProjectAsync(normalized[0], cancellationToken: cancellationToken);

        CachedWorkspaceEntry? entry = null;
        await s_cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (s_projectToCacheKey.TryGetValue(normalized[0], out var key)
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
            // Loose projects, decompiled entries, or a solution whose workspace failed to become
            // the owner: nothing to batch into.
            foreach (string path in normalized.Skip(1))
                await LoadOneIgnoringFailureAsync(path, cancellationToken);
            return;
        }

        if (!await TryBatchLoadAsync(entry, normalized, cancellationToken))
        {
            foreach (string path in normalized)
                await LoadOneIgnoringFailureAsync(path, cancellationToken);
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
        CachedWorkspaceEntry entry, IReadOnlyList<string> wanted, CancellationToken cancellationToken)
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
                cancellationToken);
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
            gateMs = watch.ElapsedMilliseconds - evaluateMs;

            // A project the ProjectMap matched to one already in the solution comes back with that
            // project's own id; AddProjectsAndRewireReferences skips those, so this counts what
            // genuinely arrived.
            added = loaded.Count(i => !live.CurrentSolution.ContainsProject(i.Id));
            AddProjectsAndRewireReferences(live, loaded);

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
                        var (_, project) = await GetOrOpenProjectAsync(
                            projectPath, diagnosticWriter: Console.Error, cancellationToken: cancellationToken);

                        if (FindDocumentInProject(project, filePath) != null)
                            return projectPath;
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

        return null;
    }

    /// <summary>
    /// Finds a document in a project by file path (case-insensitive comparison).
    /// </summary>
    public static Document? FindDocumentInProject(Project project, string filePath)
    {
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
        s_cacheLock.Wait();
        try
        {
            // Synthetic entries are skipped. Opening a decompiled file caches an ad-hoc workspace
            // keyed by its manifest, and it is by definition the most recently used one — so the
            // Solution Explorer, which asks this for the solution to list, emptied itself every
            // time someone looked at decompiled source.
            foreach (var (key, e) in s_cache)
            {
                if (DecompiledSourceService.IsDecompiledPath(key))
                    continue;
                if (entry is null || e.LastAccessedUtc > entry.LastAccessedUtc)
                    entry = e;
            }
        }
        finally { s_cacheLock.Release(); }

        if (entry is null)
            return null;

        var project = entry.Workspace.CurrentSolution.GetProject(entry.PrimaryProjectId);
        return project is null
            ? entry.Workspace.CurrentSolution
            : ApplyOpenDocumentOverlay(entry, project).Solution;
    }

    /// <summary>
    /// Evicts all cached workspace entries immediately.
    /// </summary>
    public static async Task EvictAllAsync(CancellationToken cancellationToken = default)
    {
        await s_cacheLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var entry in s_cache.Values)
            {
                foreach (var projectPath in entry.ProjectIds.Keys)
                    AnalyzerService.EvictAnalyzersForProject(projectPath);
                entry.Dispose();
            }
            s_cache.Clear();
            s_dirToProjects.Clear();
            s_projectToCacheKey.Clear();
            Console.Error.WriteLine("[WorkspaceService] All cached workspaces evicted.");
        }
        finally
        {
            s_cacheLock.Release();
        }

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
        try { return s_projectToCacheKey.TryGetValue(key, out var ck) && s_cache.ContainsKey(ck); }
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
            return s_projectToCacheKey.TryGetValue(key, out var ck) && s_cache.TryGetValue(ck, out var entry)
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
        string projectPath, CancellationToken cancellationToken = default)
    {
        string key = Path.GetFullPath(projectPath);
        await s_cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (s_projectToCacheKey.TryGetValue(key, out var ck) && s_cache.TryGetValue(ck, out var entry))
                EvictEntryLocked(ck, entry);
        }
        finally { s_cacheLock.Release(); }
    }

    /// <summary>
    /// Returns an immutable project snapshot with refreshed text for
    /// <paramref name="filePath"/> when the file was modified after
    /// <paramref name="cacheTime"/>. The workspace's internal solution is unchanged.
    /// </summary>
    private static Project RefreshDocumentIfStale(
        Project project, string filePath, DateTime cacheTime)
    {
        var document = FindDocumentInProject(project, filePath);
        if (document is null)
            return project;

        // An open editor buffer (LSP) is authoritative over disk — it was already applied by
        // the overlay pass, and disk mtime says nothing about unsaved edits.
        if (OpenDocumentStore.IsOpen(filePath))
            return project;

        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists || fileInfo.LastWriteTimeUtc <= cacheTime)
            return project;

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var text = SourceText.From(stream);
        var updatedSolution = project.Solution.WithDocumentText(document.Id, text);
        return updatedSolution.GetProject(project.Id) ?? project;
    }

    private static bool TryGetValidCachedEntryLocked(string normalizedProjectPath, out CachedWorkspaceEntry? entry)
    {
        entry = null;
        if (!s_projectToCacheKey.TryGetValue(normalizedProjectPath, out var cacheKey))
            return false;

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
        EvictEntryLocked(cacheKey, entry);
        entry = null;
        return false;
    }

    private static void EvictEntryLocked(string cacheKey, CachedWorkspaceEntry entry)
    {
        s_cache.Remove(cacheKey);

        // Remove every reverse-index mapping that points at this entry.
        foreach (var p in s_projectToCacheKey
                     .Where(kv => string.Equals(kv.Value, cacheKey, StringComparison.OrdinalIgnoreCase))
                     .Select(kv => kv.Key).ToList())
            s_projectToCacheKey.Remove(p);

        UnregisterShadowDirsLocked(cacheKey, entry.ShadowDirs);
        entry.Dispose();

        // Analyzer host entries are keyed per project FilePath, so evict for every project
        // this workspace served (a solution entry served many).
        foreach (var projectPath in entry.ProjectIds.Keys)
            AnalyzerService.EvictAnalyzersForProject(projectPath);
    }

    /// <summary>
    /// Records that the workspace cached under <paramref name="cacheKey"/> can serve the
    /// requested project plus every project in its loaded solution's closure. This powers
    /// both solution-wide dedup and reuse-by-membership.
    /// </summary>
    private static void RegisterProjectMappingsLocked(
        string cacheKey, string requestedProjectPath, Workspace workspace)
    {
        s_projectToCacheKey[requestedProjectPath] = cacheKey;
        foreach (var project in workspace.CurrentSolution.Projects)
        {
            if (!string.IsNullOrEmpty(project.FilePath))
                s_projectToCacheKey[Path.GetFullPath(project.FilePath!)] = cacheKey;
        }
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
                EvictEntryLocked(cacheKey, entry);
            }
        }
    }

    /// <summary>
    /// An entry is stale when the requested project's <c>.csproj</c> OR the entry's own key
    /// file (the <c>.sln</c> for a solution entry, or the same <c>.csproj</c> otherwise) was
    /// modified after the entry was cached.
    /// </summary>
    private static bool IsEntryStale(string cacheKey, string normalizedProjectPath, CachedWorkspaceEntry entry)
    {
        return IsFileNewerThan(normalizedProjectPath, entry.CachedAtUtc)
            || IsFileNewerThan(cacheKey, entry.CachedAtUtc);
    }

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
            project = RefreshDocumentIfStale(project, targetFilePath, entry.CachedAtUtc);

        return (entry.Workspace, project);
    }

    /// <summary>
    /// Overlays every open editor buffer (<see cref="OpenDocumentStore"/>) onto the snapshot,
    /// so cross-file analysis (find usages, diagnostics, rename) sees unsaved edits in ALL
    /// open files, not just the requested one. The forked solution is memoized per store
    /// generation — rebuilding it on every request would re-fork N documents each call.
    /// </summary>
    private static Project ApplyOpenDocumentOverlay(CachedWorkspaceEntry entry, Project project)
    {
        if (OpenDocumentStore.IsEmpty)
            return project;

        long generation = OpenDocumentStore.Generation;
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
                overlay = null;
                var solution = baseSolution;
                bool any = false;
                foreach (var (path, text) in OpenDocumentStore.SnapshotAll())
                {
                    // Multi-targeting: the same file can back several DocumentIds.
                    foreach (var docId in solution.GetDocumentIdsWithFilePath(path))
                    {
                        solution = solution.WithDocumentText(docId, text);
                        any = true;
                    }
                }
                if (any)
                    overlay = solution;

                entry.OverlaySolution = overlay;
                entry.OverlayBase = baseSolution;
                entry.OverlayGeneration = generation;
            }
        }

        return overlay?.GetProject(project.Id) ?? project;
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

            var now = DateTime.UtcNow;
            var expired = s_cache
                .Where(kvp => (now - kvp.Value.LastAccessedUtc) > IdleTimeout)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expired)
                TryEvictLoggedLocked(key, "idle workspace");

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
            EvictEntryLocked(key, entry);
            Console.Error.WriteLine($"[WorkspaceService] Evicted {label} for '{key}'.");
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"[WorkspaceService] Eviction of '{key}' failed: {ex.Message}"); }
            catch { /* console gone during teardown */ }
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
    /// Goes through the protected <c>Workspace.SetCurrentSolution(Solution)</c> overload
    /// via reflection because <see cref="Workspace.TryApplyChanges"/> would round-trip
    /// analyzer-reference edits back to the .csproj file, polluting the user's project
    /// with shadow-copy temp paths.
    /// </summary>
    private static void SwapCurrentSolutionInPlace(Workspace workspace, Solution newSolution)
    {
        if (s_setCurrentSolutionMethod is null)
        {
            // Fallback to TryApplyChanges if reflection failed — accept the disk-write
            // side effect rather than skipping the rebind entirely.
            Console.Error.WriteLine(
                "[WorkspaceService] Reflection failed: Workspace.SetCurrentSolution not found; falling back to TryApplyChanges.");
            workspace.TryApplyChanges(newSolution);
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
            workspace.TryApplyChanges(stripped);

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
            workspace.TryApplyChanges(injected);

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
        public Dictionary<string, ProjectId> ProjectIds { get; }

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
        public SemaphoreSlim LoadGate { get; } = new(1, 1);

        /// <summary>Memoized open-editor-buffer overlay (see ApplyOpenDocumentOverlay).</summary>
        public object OverlayLock { get; } = new();
        public Solution? OverlaySolution { get; set; }
        public Solution? OverlayBase { get; set; }
        public long OverlayGeneration { get; set; } = -1;

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

            ProjectIds = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
            RefreshProjectIds();
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
            Workspace.Dispose();
            ShadowLoader?.Dispose();
            LoadGate.Dispose();
            if (TempDirs is not null)
                foreach (var dir in TempDirs)
                    DecompiledSourceService.TryDeleteTempDir(dir);
        }
    }
}

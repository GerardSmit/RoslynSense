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
    /// True when MSBuild is registered and .NET Framework targeting packs are available.
    /// </summary>
    public static bool IsLegacyProjectSupported { get; private set; }

    /// <summary>
    /// Triggers the static initializer (MSBuildLocator registration). Call this from test
    /// code that creates a bare <see cref="MSBuildWorkspace"/> instead of going through
    /// <see cref="GetOrOpenProjectAsync"/>, so MSBuild is registered before workspace creation.
    /// </summary>
    public static void EnsureRegistered() { }

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

            // Legacy .NET Framework support is determined entirely by what's available to the
            // BuildHost subprocess: a VS install with the MSBuild component AND .NET Framework
            // targeting packs. We probe via vswhere because MSBuildLocator's VS Setup COM
            // discovery often fails in the .NET 10 host even when VS is installed.
            var refAssembliesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Reference Assemblies", "Microsoft", "Framework", ".NETFramework");

            var legacyMsBuildDir = Directory.Exists(refAssembliesPath)
                ? FindLegacyCompatibleMsBuildDirViaVsWhere()
                : null;
            IsLegacyProjectSupported = legacyMsBuildDir is not null;

            if (dotnetSdkInstance is not null)
            {
                MSBuildLocator.RegisterInstance(dotnetSdkInstance);
                Console.Error.WriteLine(
                    $"[WorkspaceService] Registered MSBuild from '{dotnetSdkInstance.Name}' v{dotnetSdkInstance.Version} at '{dotnetSdkInstance.MSBuildPath}'."
                    + (IsLegacyProjectSupported
                        ? $" Legacy .NET Framework projects supported via BuildHost (MSBuild at '{legacyMsBuildDir}')."
                        : " Legacy .NET Framework projects NOT supported (no VS install with MSBuild component, or no targeting packs)."));
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
    public static MSBuildWorkspace CreateWorkspace(TextWriter? diagnosticWriter = null, bool isLegacy = false)
    {
        var properties = isLegacy ? CreateLegacyProperties() : CreateDefaultProperties();
        var workspace = MSBuildWorkspace.Create(properties);

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

        while (true)
        {
            Task<(Workspace, Project)>? inflightTask = null;

            await s_cacheLock.WaitAsync(cancellationToken);
            try
            {
                if (TryGetValidCachedEntryLocked(normalizedPath, out var cachedEntry))
                    return CreateProjectSnapshot(cachedEntry!, normalizedPath, targetFilePath);

                // Cache miss → resolve the owning solution once (brief I/O, miss-path only).
                if (!ownerResolved)
                {
                    if (!isDecompile)
                        solutionPath = TryFindOwnerSolutionKey(normalizedPath).slnKey;
                    loadKey = solutionPath ?? normalizedPath;
                    ownerResolved = true;
                }

                if (s_inflight.TryGetValue(loadKey!, out inflightTask))
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
        // The s_cache key. Starts as loadKey (solution path when in solution-mode); may be
        // demoted to the project path if a solution load can't produce the requested project.
        // loadKey is non-null here: we only reach the loader after resolving it and breaking.
        string cacheKey = loadKey!;

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
                var buildTarget = solutionPath ?? normalizedPath;
                var isLegacy = PathHelper.RequiresMsBuild(buildTarget);
                if (isLegacy && !IsLegacyProjectSupported)
                    throw new NotSupportedException(
                        "Legacy .NET Framework projects require a Visual Studio install with the MSBuild " +
                        "component (VS 2017+ or Build Tools 2017+) and the .NET Framework targeting packs. " +
                        "Install 'Visual Studio Build Tools' and relaunch the MCP server.");
                var msbuildWorkspace = CreateWorkspace(diagnosticWriter, isLegacy);

                try
                {
                    await EnsureRestoredAsync(normalizedPath, cancellationToken);

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
                        if (solutionPath is not null)
                        {
                            // Open the WHOLE solution once into this workspace. Project-to-project
                            // references become shared compilation references (loaded once),
                            // instead of one fully-loaded transitive graph per project.
                            var loadedSolution = await msbuildWorkspace.OpenSolutionAsync(
                                solutionPath, cancellationToken: openLinked.Token)
                                .WaitAsync(OpenProjectTimeout, cancellationToken);

                            openedProject = loadedSolution.Projects.FirstOrDefault(p =>
                                !string.IsNullOrEmpty(p.FilePath) &&
                                string.Equals(Path.GetFullPath(p.FilePath!), normalizedPath, StringComparison.OrdinalIgnoreCase))!;

                            if (openedProject is null)
                            {
                                // The requested project didn't load inside the solution (the
                                // WorkspaceFailed handler logged why). Demote to a clean
                                // per-project load so the caller still gets a workspace.
                                Console.Error.WriteLine(
                                    $"[WorkspaceService] '{Path.GetFileName(normalizedPath)}' not found in solution load; falling back to per-project.");
                                msbuildWorkspace.Dispose();
                                solutionPath = null;
                                cacheKey = normalizedPath;
                                msbuildWorkspace = CreateWorkspace(diagnosticWriter, isLegacy);
                                openedProject = await msbuildWorkspace.OpenProjectAsync(
                                    normalizedPath, cancellationToken: openLinked.Token)
                                    .WaitAsync(OpenProjectTimeout, cancellationToken);
                            }
                        }
                        else
                        {
                            openedProject = await msbuildWorkspace.OpenProjectAsync(
                                normalizedPath,
                                cancellationToken: openLinked.Token)
                                .WaitAsync(OpenProjectTimeout, cancellationToken);
                        }
                    }
                    catch (TimeoutException tex)
                    {
                        throw new TimeoutException(
                            $"Opening '{Path.GetFileName(buildTarget)}' timed out after " +
                            $"{OpenProjectTimeout.TotalSeconds:F0}s. The MSBuild BuildHost subprocess may be wedged " +
                            "(legacy WebForms projects with source generators are a frequent cause). " +
                            "Disposing the workspace will kill the BuildHost; the next attempt should succeed.",
                            tex);
                    }

                    var solution = StripUnresolvedAnalyzerReferences(msbuildWorkspace.CurrentSolution);
                    if (solution != msbuildWorkspace.CurrentSolution)
                    {
                        msbuildWorkspace.TryApplyChanges(solution);
                        openedProject = msbuildWorkspace.CurrentSolution.GetProject(openedProject.Id)!;
                    }

                    // Rebind to shadow-copied analyzer / source-generator paths BEFORE any
                    // call to GetCompilationAsync (the framework-reference probe below does
                    // exactly that). Roslyn's default analyzer loader opens the original DLL
                    // via PEReader on first compilation access, locking it on disk — once
                    // that's happened, our rebind is too late.
                    (solution, shadowLoader, shadowDirs) =
                        RebindAnalyzerReferencesToShadowLoader(msbuildWorkspace.CurrentSolution);
                    if (shadowLoader is not null)
                    {
                        SwapCurrentSolutionInPlace(msbuildWorkspace, solution);
                        openedProject = msbuildWorkspace.CurrentSolution.GetProject(openedProject.Id)!;
                    }

                    solution = await InjectMissingFrameworkReferencesAsync(msbuildWorkspace.CurrentSolution, cancellationToken);
                    if (solution != msbuildWorkspace.CurrentSolution)
                    {
                        msbuildWorkspace.TryApplyChanges(solution);
                        openedProject = msbuildWorkspace.CurrentSolution.GetProject(openedProject.Id)!;
                    }

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
                        Console.Error.WriteLine($"[WorkspaceService] Error opening project '{projectPath}': {ex.Message}");
                        if (ex.InnerException != null)
                            Console.Error.WriteLine($"[WorkspaceService] Inner exception: {ex.InnerException.Message}");
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

    /// <summary>Evicts only the single entry serving <paramref name="projectPath"/> (no global sweep).</summary>
    internal static async Task EvictProjectForTests(string projectPath)
    {
        string key = Path.GetFullPath(projectPath);
        await s_cacheLock.WaitAsync();
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
        Workspace workspace, Project project, string filePath, DateTime cacheTime)
    {
        var document = FindDocumentInProject(project, filePath);
        if (document is null)
            return project;

        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists || fileInfo.LastWriteTimeUtc <= cacheTime)
            return project;

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var text = SourceText.From(stream);
        var updatedSolution = workspace.CurrentSolution.WithDocumentText(document.Id, text);
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
        TryFindOwnerSolutionKey(Path.GetFullPath(projectPath)).slnKey;

    /// <summary>
    /// Walks up from the project to its nearest solution file and, if that solution lists
    /// the project and contains more than one project, returns the normalized solution path
    /// to use as the shared cache key. Returns <c>(null, false)</c> for loose / single-project
    /// solutions, which fall back to per-project loading.
    /// </summary>
    private static (string? slnKey, bool isLegacy) TryFindOwnerSolutionKey(string normalizedProjectPath)
    {
        try
        {
            string? sln = PathHelper.FindNearestSolution(normalizedProjectPath);
            if (string.IsNullOrEmpty(sln))
                return (null, false);

            var projects = PathHelper.GetProjectsFromSolution(sln);
            if (projects.Count <= 1)
                return (null, false);  // single-project solution gains nothing from sharing

            bool contains = projects.Any(p =>
                string.Equals(Path.GetFullPath(p), normalizedProjectPath, StringComparison.OrdinalIgnoreCase));
            if (!contains)
                return (null, false);

            return (Path.GetFullPath(sln), PathHelper.RequiresMsBuild(sln));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WorkspaceService] Solution discovery failed for '{normalizedProjectPath}': {ex.Message}");
            return (null, false);
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

        if (targetFilePath != null)
            project = RefreshDocumentIfStale(entry.Workspace, project, targetFilePath, entry.CachedAtUtc);

        return (entry.Workspace, project);
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
    private static Solution StripUnresolvedAnalyzerReferences(Solution solution)
    {
        foreach (var project in solution.Projects)
        {
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
        RebindAnalyzerReferencesToShadowLoader(Solution solution)
    {
        var shadowCopy = ShadowCopyService.Instance;
        ShadowCopyAnalyzerAssemblyLoader? loader = null;
        HashSet<string>? dirs = null;

        foreach (var project in solution.Projects.ToList())
        {
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
    /// Runs <c>dotnet restore</c> if the project's <c>project.assets.json</c> is missing,
    /// so that MSBuildWorkspace can properly resolve NuGet packages and framework references.
    /// Legacy .NET Framework projects (non-SDK-style) don't use project.assets.json, so this is skipped for them.
    /// </summary>
    private static async Task EnsureRestoredAsync(string projectPath, CancellationToken cancellationToken)
    {
        string? projectDir = Path.GetDirectoryName(projectPath);
        if (projectDir is null) return;

        // Legacy projects use packages.config, not project.assets.json — skip dotnet restore
        if (PathHelper.RequiresMsBuild(projectPath)) return;

        string assetsFile = Path.Combine(projectDir, "obj", "project.assets.json");
        if (File.Exists(assetsFile)) return;

        Console.Error.WriteLine($"[WorkspaceService] project.assets.json missing for '{Path.GetFileName(projectPath)}', running dotnet restore...");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"restore \"{projectPath}\" --verbosity quiet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = projectDir
            }
        };

        BuildProcessHelper.ConfigureMsBuildEnvironment(process.StartInfo);

        try
        {
            process.Start();

            // Drain stdout/stderr in parallel to prevent pipe deadlock
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            // Await both tasks to ensure pipes are fully consumed before disposal
            await Task.WhenAll(stdoutTask, stderrTask);

            if (process.ExitCode == 0)
                Console.Error.WriteLine("[WorkspaceService] Restore completed successfully.");
            else
            {
                var stderr = await stderrTask;
                Console.Error.WriteLine($"[WorkspaceService] Restore failed (exit {process.ExitCode}): {stderr.Trim()}");
            }
        }
        catch (OperationCanceledException)
        {
            await BuildProcessHelper.KillAndDrainAsync(process);
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WorkspaceService] Restore failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Detects projects missing core framework references (System.Object, System.Int32, etc.)
    /// and injects the appropriate references based on target framework.
    /// </summary>
    private static async Task<Solution> InjectMissingFrameworkReferencesAsync(Solution solution, CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            // Check if System.Object is resolvable — if not, framework references are broken
            var objectType = compilation.GetSpecialType(SpecialType.System_Object);
            if (objectType.TypeKind != TypeKind.Error) continue;

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

            string framework = InferTargetFrameworkKind(project);
            Console.Error.WriteLine($"[WorkspaceService] Project '{project.Name}' ({framework}) missing framework references, injecting {filtered.Count} assemblies.");

            solution = solution.WithProjectMetadataReferences(
                project.Id,
                project.MetadataReferences.Concat(filtered));
        }

        return solution;
    }

    /// <summary>
    /// Returns the correct framework reference assemblies for the project's target framework.
    /// </summary>
    private static List<MetadataReference> GetFrameworkReferences(Project project)
    {
        var refs = new List<MetadataReference>();
        string kind = InferTargetFrameworkKind(project);

        if (kind == "netfx")
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
    /// Determines if the project targets .NET Framework, .NET Standard, or modern .NET.
    /// </summary>
    private static string InferTargetFrameworkKind(Project project)
    {
        if (project.ParseOptions is CSharpParseOptions parseOptions)
        {
            var symbols = parseOptions.PreprocessorSymbolNames;
            foreach (var sym in symbols)
            {
                // NET48, NET472, etc. — .NET Framework
                if (sym.StartsWith("NET4", StringComparison.OrdinalIgnoreCase) &&
                    !sym.StartsWith("NET40_OR_GREATER", StringComparison.OrdinalIgnoreCase))
                    return "netfx";

                if (sym.StartsWith("NET35", StringComparison.OrdinalIgnoreCase) ||
                    sym.StartsWith("NET20", StringComparison.OrdinalIgnoreCase))
                    return "netfx";

                // NETFRAMEWORK is the definitive symbol
                if (sym.Equals("NETFRAMEWORK", StringComparison.OrdinalIgnoreCase))
                    return "netfx";

                if (sym.Equals("NETSTANDARD", StringComparison.OrdinalIgnoreCase) ||
                    sym.StartsWith("NETSTANDARD", StringComparison.OrdinalIgnoreCase))
                    return "netstandard";
            }
        }

        return "modern";
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
        public ShadowCopyAnalyzerAssemblyLoader? ShadowLoader { get; }
        public IReadOnlyCollection<string>? ShadowDirs { get; }

        /// <summary>Temp directories (decompile reference copies) to delete on disposal.</summary>
        public IReadOnlyList<string>? TempDirs { get; }

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
            ShadowDirs = shadowDirs;
            TempDirs = tempDirs;

            ProjectIds = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in workspace.CurrentSolution.Projects)
            {
                if (!string.IsNullOrEmpty(project.FilePath))
                    ProjectIds[Path.GetFullPath(project.FilePath!)] = project.Id;
            }
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
            if (TempDirs is not null)
                foreach (var dir in TempDirs)
                    DecompiledSourceService.TryDeleteTempDir(dir);
        }
    }
}

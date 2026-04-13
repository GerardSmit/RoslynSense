using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

    private static readonly Dictionary<string, CachedWorkspaceEntry> s_cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<(Workspace, Project)>> s_inflight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim s_cacheLock = new(1, 1);
    private static readonly Timer s_evictionTimer;

    /// <summary>
    /// Indicates whether legacy .NET Framework projects (non-SDK-style .csproj) are supported.
    /// True when MSBuild is registered and .NET Framework targeting packs are available.
    /// </summary>
    public static bool IsLegacyProjectSupported { get; private set; }

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
        TryRegisterVisualStudioMSBuild();
        RuntimeHelpers.RunClassConstructor(typeof(CSharpSyntaxTree).TypeHandle);
        s_evictionTimer = new Timer(EvictExpiredEntries, null, EvictionInterval, EvictionInterval);
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

            if (instances.Count == 0)
                return;

            // Only full Visual Studio installations can load legacy .csproj files via Roslyn's
            // .NET Framework build host. DotNetSdk instances use the netcore build host which
            // cannot evaluate legacy toolsets (ToolsVersion="15.0", Microsoft.CSharp.targets, etc.).
            //
            // VS 2026+ (MSBuild 18+) introduced a dependency on Microsoft.VisualStudio.Telemetry.dll
            // that is not present in the MSBuild bin directory. Roslyn's .NET Framework build host
            // fails to load legacy projects because it can't resolve that assembly at runtime.
            // Prefer VS 2022 or earlier (MSBuild 17.x) for full legacy project support.
            var vsInstances = instances
                .Where(i => i.DiscoveryType == DiscoveryType.VisualStudioSetup)
                .ToList();

            var legacyCompatibleInstance = vsInstances
                .Where(i => i.Version.Major < 18)
                .OrderByDescending(i => i.Version)
                .FirstOrDefault();

            // Fall back to the best available VS instance (even if incompatible with legacy),
            // then fall back to the highest .NET SDK instance so the workspace still works.
            var instance = legacyCompatibleInstance
                ?? vsInstances.OrderByDescending(i => i.Version).FirstOrDefault()
                ?? instances.OrderByDescending(i => i.Version).First();

            MSBuildLocator.RegisterInstance(instance);

            // Legacy .NET Framework projects require both targeting packs and a compatible MSBuild.
            var refAssembliesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Reference Assemblies", "Microsoft", "Framework", ".NETFramework");

            IsLegacyProjectSupported = legacyCompatibleInstance is not null && Directory.Exists(refAssembliesPath);

            Console.Error.WriteLine(
                $"[WorkspaceService] Registered MSBuild from '{instance.Name}' v{instance.Version} at '{instance.MSBuildPath}'."
                + (IsLegacyProjectSupported
                    ? " Legacy .NET Framework projects supported."
                    : " Legacy .NET Framework projects NOT supported (requires VS 2022 or earlier)."));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[WorkspaceService] MSBuild Locator failed, using SDK-bundled MSBuild: {ex.Message}");
        }
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

        while (true)
        {
            Task<(Workspace, Project)>? inflightTask = null;

            await s_cacheLock.WaitAsync(cancellationToken);
            try
            {
                if (TryGetValidCachedEntryLocked(normalizedPath, out var cachedEntry))
                    return CreateProjectSnapshot(cachedEntry!, targetFilePath);

                if (s_inflight.TryGetValue(normalizedPath, out inflightTask))
                {
                    // Another caller is loading this project — wait for it outside the lock
                }
                else
                {
                    // We are the loader — register ourselves and break out to do the load
                    ourTcs = new TaskCompletionSource<(Workspace, Project)>(TaskCreationOptions.RunContinuationsAsynchronously);
                    s_inflight[normalizedPath] = ourTcs.Task;
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

        try
        {
            if (DecompiledSourceService.IsGeneratedProjectPath(normalizedPath))
            {
                (workspace, openedProject) = await DecompiledSourceService.OpenProjectAsync(
                    normalizedPath,
                    cancellationToken);
            }
            else
            {
                var isLegacy = PathHelper.RequiresMsBuild(normalizedPath);
                var msbuildWorkspace = CreateWorkspace(diagnosticWriter, isLegacy);

                try
                {
                    await EnsureRestoredAsync(normalizedPath, cancellationToken);

                    openedProject = await msbuildWorkspace.OpenProjectAsync(
                        normalizedPath,
                        cancellationToken: cancellationToken);

                    var solution = StripUnresolvedAnalyzerReferences(msbuildWorkspace.CurrentSolution);
                    if (solution != msbuildWorkspace.CurrentSolution)
                    {
                        msbuildWorkspace.TryApplyChanges(solution);
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
                    msbuildWorkspace.Dispose();
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            await RemoveInflightAndSignal(normalizedPath, ourTcs!, ex);
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
            await RemoveInflightAndSignal(normalizedPath, ourTcs!);
            throw;
        }

        try
        {
            s_inflight.Remove(normalizedPath);

            if (TryGetValidCachedEntryLocked(normalizedPath, out var cachedEntry))
            {
                workspace.Dispose();
                result = CreateProjectSnapshot(cachedEntry!, targetFilePath);
            }
            else
            {
                var newEntry = new CachedWorkspaceEntry(workspace, openedProject.Id);
                s_cache[normalizedPath] = newEntry;
                Console.Error.WriteLine($"[WorkspaceService] Cached workspace for '{normalizedPath}'.");

                result = CreateProjectSnapshot(newEntry, targetFilePath);
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
            foreach (var entry in s_cache)
            {
                AnalyzerService.EvictAnalyzersForProject(entry.Key);
                entry.Value.Dispose();
            }
            s_cache.Clear();
            Console.Error.WriteLine("[WorkspaceService] All cached workspaces evicted.");
        }
        finally
        {
            s_cacheLock.Release();
        }
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

    private static bool TryGetValidCachedEntryLocked(string normalizedPath, out CachedWorkspaceEntry? entry)
    {
        if (!s_cache.TryGetValue(normalizedPath, out entry))
            return false;

        if (!IsProjectFileStale(normalizedPath, entry))
            return true;

        Console.Error.WriteLine(
            $"[WorkspaceService] Project file changed, evicting cache for '{normalizedPath}'.");
        s_cache.Remove(normalizedPath);
        entry.Dispose();
        AnalyzerService.EvictAnalyzersForProject(normalizedPath);
        entry = null;
        return false;
    }

    private static bool IsProjectFileStale(string normalizedPath, CachedWorkspaceEntry entry)
    {
        var projectInfo = new FileInfo(normalizedPath);
        return projectInfo.Exists && projectInfo.LastWriteTimeUtc > entry.CachedAtUtc;
    }

    private static (Workspace Workspace, Project Project) CreateProjectSnapshot(
        CachedWorkspaceEntry entry, string? targetFilePath)
    {
        entry.LastAccessedUtc = DateTime.UtcNow;
        var project = entry.GetProject();

        if (targetFilePath != null)
            project = RefreshDocumentIfStale(entry.Workspace, project, targetFilePath, entry.CachedAtUtc);

        return (entry.Workspace, project);
    }

    private static void EvictExpiredEntries(object? state)
    {
        if (!s_cacheLock.Wait(0))
            return; // Skip this cycle if another operation holds the lock

        try
        {
            var now = DateTime.UtcNow;
            var expired = s_cache
                .Where(kvp => (now - kvp.Value.LastAccessedUtc) > IdleTimeout)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expired)
            {
                if (s_cache.TryGetValue(key, out var entry))
                {
                    s_cache.Remove(key);
                    entry.Dispose();
                    AnalyzerService.EvictAnalyzersForProject(key);
                    Console.Error.WriteLine($"[WorkspaceService] Evicted idle workspace for '{key}'.");
                }
            }
        }
        finally
        {
            s_cacheLock.Release();
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

        process.StartInfo.Environment["MSBUILDTERMINALLOGGER"] = "off";

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
            // Kill the process tree to prevent orphaned dotnet restore processes
            try { process.Kill(entireProcessTree: true); } catch { }
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
        public Workspace Workspace { get; }
        public ProjectId ProjectId { get; }
        public DateTime CachedAtUtc { get; }
        public DateTime LastAccessedUtc { get; set; }

        public CachedWorkspaceEntry(Workspace workspace, ProjectId projectId)
        {
            Workspace = workspace;
            ProjectId = projectId;
            CachedAtUtc = DateTime.UtcNow;
            LastAccessedUtc = DateTime.UtcNow;
        }

        public Project GetProject() =>
            Workspace.CurrentSolution.GetProject(ProjectId)
            ?? throw new InvalidOperationException($"Cached project {ProjectId} no longer found in workspace.");

        public void Dispose() => Workspace.Dispose();
    }
}

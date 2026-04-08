using System.Runtime.CompilerServices;
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
    private static readonly SemaphoreSlim s_cacheLock = new(1, 1);
    private static readonly Timer s_evictionTimer;

    /// <summary>
    /// Indicates whether a Visual Studio or Build Tools MSBuild instance was registered.
    /// When <c>true</c>, legacy-format .csproj files (non-SDK-style) are supported.
    /// </summary>
    public static bool IsVisualStudioMSBuildRegistered { get; private set; }

    private static Dictionary<string, string> CreateDefaultProperties() => new()
    {
        { "AlwaysUseNETSdkDefaults", "true" },
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

            var instance = MSBuildLocator.QueryVisualStudioInstances()
                .OrderByDescending(i => i.Version)
                .FirstOrDefault();

            if (instance is null)
                return;

            MSBuildLocator.RegisterInstance(instance);
            IsVisualStudioMSBuildRegistered = true;
            Console.Error.WriteLine(
                $"[WorkspaceService] Registered MSBuild from '{instance.Name}' v{instance.Version} at '{instance.MSBuildPath}'.");
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
    public static MSBuildWorkspace CreateWorkspace(TextWriter? diagnosticWriter = null)
    {
        var workspace = MSBuildWorkspace.Create(CreateDefaultProperties());

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

        CachedWorkspaceEntry? cachedEntry;
        await s_cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (TryGetValidCachedEntryLocked(normalizedPath, out cachedEntry))
                return CreateProjectSnapshot(cachedEntry!, targetFilePath);
        }
        finally
        {
            s_cacheLock.Release();
        }

        Workspace workspace;
        Project openedProject;

        if (DecompiledSourceService.IsGeneratedProjectPath(normalizedPath))
        {
            (workspace, openedProject) = await DecompiledSourceService.OpenProjectAsync(
                normalizedPath,
                cancellationToken);
        }
        else
        {
            var msbuildWorkspace = CreateWorkspace(diagnosticWriter);

            try
            {
                openedProject = await msbuildWorkspace.OpenProjectAsync(
                    normalizedPath,
                    cancellationToken: cancellationToken);
                workspace = msbuildWorkspace;
            }
            catch
            {
                msbuildWorkspace.Dispose();
                throw;
            }
        }

        await s_cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (TryGetValidCachedEntryLocked(normalizedPath, out cachedEntry))
            {
                workspace.Dispose();
                return CreateProjectSnapshot(cachedEntry!, targetFilePath);
            }

            var newEntry = new CachedWorkspaceEntry(workspace, openedProject.Id);
            s_cache[normalizedPath] = newEntry;
            Console.Error.WriteLine($"[WorkspaceService] Cached workspace for '{normalizedPath}'.");

            return CreateProjectSnapshot(newEntry, targetFilePath);
        }
        finally
        {
            s_cacheLock.Release();
        }
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

                        var refPath = line[includeStart..includeEnd];
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

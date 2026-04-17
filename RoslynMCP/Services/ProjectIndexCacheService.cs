using Microsoft.CodeAnalysis;

namespace RoslynMCP.Services;

/// <summary>
/// Caches ASPX project indexes and Razor source maps per-project.
/// Uses <see cref="FileSystemWatcher"/> to automatically invalidate
/// the cache when relevant files change on disk.
/// </summary>
internal static class ProjectIndexCacheService
{
    private static readonly SemaphoreSlim s_lock = new(1, 1);
    private static readonly Dictionary<string, CachedProjectEntry> s_cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] s_aspxExtensions =
        [".aspx", ".ascx", ".asmx", ".asax", ".ashx", ".master"];
    private static readonly string[] s_razorExtensions =
        [".razor", ".cshtml"];

    /// <summary>
    /// Disposes all cached entries (including their FileSystemWatchers).
    /// </summary>
    public static void DisposeAll()
    {
        s_lock.Wait();
        try
        {
            foreach (var entry in s_cache.Values)
                entry.Dispose();
            s_cache.Clear();
        }
        finally
        {
            s_lock.Release();
        }
    }

    /// <summary>
    /// Returns a cached or freshly-built ASPX project index.
    /// Pass <paramref name="compilation"/> when the caller has already obtained the compilation
    /// to avoid a redundant <c>GetCompilationAsync</c> call inside the builder.
    /// </summary>
    public static async Task<AspxProjectIndex> GetAspxIndexAsync(
        Project project, CancellationToken cancellationToken = default,
        Compilation? compilation = null)
    {
        var entry = await GetOrCreateEntryAsync(project, cancellationToken);

        if (entry.AspxIndex is { } cached && !entry.AspxDirty)
            return cached;

        // Capture generation before building; if it changes during the build,
        // we know a file changed and must leave the dirty flag set
        int genBefore;
        await s_lock.WaitAsync(cancellationToken);
        try { genBefore = entry.AspxGeneration; }
        finally { s_lock.Release(); }

        var index = await AspxSourceMappingService.BuildProjectIndexAsync(project, cancellationToken, compilation);

        await s_lock.WaitAsync(cancellationToken);
        try
        {
            entry.AspxIndex = index;
            // Only clear dirty if no file changed during the build
            if (entry.AspxGeneration == genBefore)
                entry.AspxDirty = false;
        }
        finally
        {
            s_lock.Release();
        }

        return index;
    }

    /// <summary>
    /// Returns a cached or freshly-built Razor source map.
    /// </summary>
    public static async Task<RazorSourceMap> GetRazorSourceMapAsync(
        Project project, CancellationToken cancellationToken = default)
    {
        var entry = await GetOrCreateEntryAsync(project, cancellationToken);

        if (entry.RazorSourceMap is { } cached && !entry.RazorDirty)
            return cached;

        int genBefore;
        await s_lock.WaitAsync(cancellationToken);
        try { genBefore = entry.RazorGeneration; }
        finally { s_lock.Release(); }

        var sourceMap = await RazorSourceMappingService.BuildSourceMapAsync(project, cancellationToken);

        await s_lock.WaitAsync(cancellationToken);
        try
        {
            entry.RazorSourceMap = sourceMap;
            if (entry.RazorGeneration == genBefore)
                entry.RazorDirty = false;
        }
        finally
        {
            s_lock.Release();
        }

        return sourceMap;
    }

    /// <summary>
    /// Returns a cached or freshly-discovered list of wrapper methods that delegate
    /// a string parameter to <c>FindControl</c> (e.g. <c>SetText(control, id, value)</c>).
    /// The result is cached per-project and invalidated whenever a <c>.cs</c> file changes.
    /// </summary>
    public static async Task<IReadOnlyList<(string MethodName, int ParamIndex, bool IsExtension)>> GetFindControlWrappersAsync(
        Project project, CancellationToken cancellationToken = default)
    {
        var entry = await GetOrCreateEntryAsync(project, cancellationToken);

        if (entry.FindControlWrappers is { } cached && !entry.WrappersDirty)
            return cached;

        int genBefore;
        await s_lock.WaitAsync(cancellationToken);
        try { genBefore = entry.WrappersGeneration; }
        finally { s_lock.Release(); }

        var wrappers = await AspxSourceMappingService.FindControlAccessorMethodsAsync(project, cancellationToken);

        await s_lock.WaitAsync(cancellationToken);
        try
        {
            entry.FindControlWrappers = wrappers;
            if (entry.WrappersGeneration == genBefore)
                entry.WrappersDirty = false;
        }
        finally { s_lock.Release(); }

        return wrappers;
    }

    /// <summary>
    /// Explicitly invalidates all cached data for a project.
    /// </summary>
    public static void InvalidateProject(string projectPath)
    {
        var key = Path.GetFullPath(projectPath);
        s_lock.Wait();
        try
        {
            if (s_cache.TryGetValue(key, out var entry))
            {
                entry.AspxDirty = true;
                entry.RazorDirty = true;
                entry.WrappersDirty = true;
            }
        }
        finally
        {
            s_lock.Release();
        }
    }

    private static async Task<CachedProjectEntry> GetOrCreateEntryAsync(
        Project project, CancellationToken cancellationToken)
    {
        var key = Path.GetFullPath(project.FilePath!);

        await s_lock.WaitAsync(cancellationToken);
        try
        {
            if (s_cache.TryGetValue(key, out var existing))
                return existing;

            var entry = new CachedProjectEntry();
            SetupFileWatcher(entry, project.FilePath!);
            s_cache[key] = entry;
            return entry;
        }
        finally
        {
            s_lock.Release();
        }
    }

    private static void SetupFileWatcher(CachedProjectEntry entry, string projectFilePath)
    {
        var projectDir = Path.GetDirectoryName(projectFilePath);
        if (projectDir is null || !Directory.Exists(projectDir))
            return;

        try
        {
            var watcher = new FileSystemWatcher(projectDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            watcher.Changed += (_, e) => OnFileChanged(entry, e.FullPath);
            watcher.Created += (_, e) => OnFileChanged(entry, e.FullPath);
            watcher.Deleted += (_, e) => OnFileChanged(entry, e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                OnFileChanged(entry, e.OldFullPath);
                OnFileChanged(entry, e.FullPath);
            };

            entry.Watcher = watcher;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProjectIndexCache] FileSystemWatcher setup failed: {ex.Message}");
        }
    }

    private static void OnFileChanged(CachedProjectEntry entry, string filePath)
    {
        var ext = Path.GetExtension(filePath);
        var fileName = Path.GetFileName(filePath);

        // web.config changes invalidate ASPX cache (globally registered controls may change)
        if (fileName.Equals("web.config", StringComparison.OrdinalIgnoreCase))
        {
            entry.AspxDirty = true;
            Interlocked.Increment(ref entry.AspxGeneration);
            return;
        }

        if (string.IsNullOrEmpty(ext))
            return;

        // Skip obj/bin directories
        var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(s => s.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                              s.Equals("bin", StringComparison.OrdinalIgnoreCase)))
            return;

        bool isAspx = s_aspxExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase));
        bool isRazor = s_razorExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase));
        bool isCSharp = ext.Equals(".cs", StringComparison.OrdinalIgnoreCase);

        if (isAspx || isCSharp)
        {
            entry.AspxDirty = true;
            Interlocked.Increment(ref entry.AspxGeneration);
        }

        if (isRazor || isCSharp)
        {
            entry.RazorDirty = true;
            Interlocked.Increment(ref entry.RazorGeneration);
        }

        if (isCSharp)
        {
            entry.WrappersDirty = true;
            Interlocked.Increment(ref entry.WrappersGeneration);
        }
    }

    private sealed class CachedProjectEntry : IDisposable
    {
        public AspxProjectIndex? AspxIndex { get; set; }
        public RazorSourceMap? RazorSourceMap { get; set; }
        public IReadOnlyList<(string MethodName, int ParamIndex, bool IsExtension)>? FindControlWrappers { get; set; }
        public volatile bool AspxDirty = true;
        public volatile bool RazorDirty = true;
        public volatile bool WrappersDirty = true;
        public int AspxGeneration;
        public int RazorGeneration;
        public int WrappersGeneration;
        public FileSystemWatcher? Watcher { get; set; }

        public void Dispose()
        {
            Watcher?.Dispose();
            Watcher = null;
        }
    }
}

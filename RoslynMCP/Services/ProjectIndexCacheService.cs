using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Languages.WebForms.Core;

namespace RoslynMCP.Services;

/// <summary>
/// Caches ASPX project indexes, Razor source maps, proto import graphs and resource catalogs
/// per-project.
/// Uses <see cref="FileSystemWatcher"/> to automatically invalidate
/// the cache when relevant files change on disk.
/// </summary>
/// <remarks>
/// This watcher is the MCP front end's only freshness mechanism — it has no editor sending
/// <c>didChangeWatchedFiles</c> — so anything the LSP path invalidates has to be invalidated here
/// too, or the tools answer from a snapshot the editor has already moved past.
/// </remarks>
internal static class ProjectIndexCacheService
{
    private static readonly SemaphoreSlim s_lock = new(1, 1);
    private static readonly Dictionary<string, CachedProjectEntry> s_cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Idle period after which a cached entry (and its FileSystemWatcher) is evicted.
    /// Override via <c>ROSLYNMCP_INDEX_IDLE_TIMEOUT_SECONDS</c>. Mirrors WorkspaceService.</summary>
    private static readonly TimeSpan IdleTimeout =
        int.TryParse(Environment.GetEnvironmentVariable("ROSLYNMCP_INDEX_IDLE_TIMEOUT_SECONDS"), out var s) && s > 0
            ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(10);

    private static readonly TimeSpan EvictionInterval = TimeSpan.FromMinutes(1);
    private static readonly Timer s_evictionTimer;

    static ProjectIndexCacheService()
    {
        s_evictionTimer = new Timer(EvictExpiredEntries, null, EvictionInterval, EvictionInterval);
    }

    private static readonly string[] s_aspxExtensions =
        [".aspx", ".ascx", ".asmx", ".asax", ".ashx", ".master"];
    private static readonly string[] s_razorExtensions =
        [".razor", ".cshtml"];
    private static readonly string[] s_protoExtensions =
        [".proto"];

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
        s_evictionTimer.Dispose();
    }

    private static void EvictExpiredEntries(object? state)
    {
        // Runs on a Timer's ThreadPool thread: an escaping exception is unhandled and crashes
        // the process. Guard the lock acquire (disposed at teardown), isolate each disposal.
        bool acquired = false;
        try
        {
            try { acquired = s_lock.Wait(0); }
            catch (ObjectDisposedException) { return; } // shutting down
            if (!acquired)
                return; // another operation holds the lock — skip this cycle

            var now = DateTime.UtcNow;
            var expired = s_cache
                .Where(kvp => (now - kvp.Value.LastAccessedUtc) > IdleTimeout)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expired)
            {
                if (!s_cache.Remove(key, out var entry))
                    continue;
                try
                {
                    entry.Dispose();
                    Console.Error.WriteLine($"[ProjectIndexCache] Evicted idle entry for '{Path.GetFileName(key)}'.");
                }
                catch (Exception ex)
                {
                    try { Console.Error.WriteLine($"[ProjectIndexCache] Eviction of '{Path.GetFileName(key)}' failed: {ex.Message}"); }
                    catch { /* console gone during teardown */ }
                }
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
                try { s_lock.Release(); } catch (ObjectDisposedException) { }
            }
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

        if (entry.AspxIndex is { } current && !entry.AspxDirty && entry.AspxChanged.IsEmpty)
            return current;

        // Capture generation before building; if it changes during the build,
        // we know a file changed and must leave the dirty flag set
        // Both read together: the generation says whether anything moved while the build ran, and
        // the changed set says what to re-parse. A markup parse describes one file, so the rest of
        // the index is still true — rebuilding it re-walked the site and re-parsed every page to
        // account for one save.
        int genBefore;
        string[] changed;
        await s_lock.WaitAsync(cancellationToken);
        try
        {
            genBefore = entry.AspxGeneration;
            changed = [.. entry.AspxChanged.Keys];
        }
        finally { s_lock.Release(); }

        // A parse is not a function of its file alone: it resolves every tag prefix against the
        // project's types, so `<asp:TextBox ID="x">` becomes a control with an id only because the
        // compilation says what asp:TextBox is. Carrying results over is therefore only sound while
        // that answer is the same, and this is the version that moves when it changes — a
        // declaration appearing or disappearing, including in the .designer.cs a markup save
        // regenerates. Editing a method body does not move it, so the ordinary case stays
        // incremental.
        var semantic = await project.GetDependentSemanticVersionAsync(cancellationToken);

        var index = entry.AspxIndex is { } previous
            && !entry.AspxDirty
            && changed.Length > 0
            && entry.AspxIndexSemantic is { } built
            && built.Equals(semantic)
            ? await AspxSourceMappingService.UpdateProjectIndexAsync(
                project, previous, changed, cancellationToken, compilation)
            : await AspxSourceMappingService.BuildProjectIndexAsync(project, cancellationToken, compilation);

        await s_lock.WaitAsync(cancellationToken);
        try
        {
            entry.AspxIndex = index;
            entry.AspxIndexSemantic = semantic;
            // Only clear if no file changed during the build
            if (entry.AspxGeneration == genBefore)
            {
                entry.AspxDirty = false;
                foreach (string file in changed)
                    entry.AspxChanged.TryRemove(file, out _);
            }
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
    /// Returns a cached or freshly-built proto import graph — which <c>.proto</c> in the project
    /// imports which, in both directions.
    /// </summary>
    /// <remarks>
    /// The one thing the proto engine does not memoize for itself, and the reason it is worth a
    /// third index kind here. <c>ProtoDocumentService</c> keys each parse on the file's checksum
    /// and <c>ProtoGeneratedIndex</c> keys the bindings on the compilation plus the protos'
    /// timestamps, so both notice a change without being told; the graph is assembled from every
    /// file in the project on each call and nothing underneath it is a cache miss, so a caller
    /// asking twice pays the whole walk twice. The watcher is what says when to walk again.
    /// </remarks>
    public static async Task<ProtoImportGraph> GetProtoImportGraphAsync(
        Project project, CancellationToken cancellationToken = default)
    {
        var entry = await GetOrCreateEntryAsync(project, cancellationToken);

        if (entry.ProtoImportGraph is { } cached && !entry.ProtoDirty)
            return cached;

        int genBefore;
        await s_lock.WaitAsync(cancellationToken);
        try { genBefore = entry.ProtoGeneration; }
        finally { s_lock.Release(); }

        var graph = await ProtoWorkspace.ImportGraphAsync(project, cancellationToken);

        await s_lock.WaitAsync(cancellationToken);
        try
        {
            entry.ProtoImportGraph = graph;
            if (entry.ProtoGeneration == genBefore)
                entry.ProtoDirty = false;
        }
        finally { s_lock.Release(); }

        return graph;
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

        // The synchronous snapshot the string-literal detection pass reads; see the registry.
        Languages.WebForms.Core.FindControlWrapperRegistry.Publish(project.AssemblyName, wrappers);

        return wrappers;
    }

    /// <summary>
    /// Returns a cached or freshly-grouped resource catalog.
    /// </summary>
    /// <remarks>
    /// The catalog itself lives in <see cref="ResourceCatalogService"/>, which the watcher
    /// invalidates directly; what is cached here is the reference this project last saw, so a
    /// caller that never edits a <c>.resx</c> pays nothing. Both flags matter: a layout change
    /// replaces the catalog object, and a content change leaves it in place but drops the key
    /// tables every family in it was handing out.
    /// </remarks>
    public static async Task<ResourceCatalog> GetResourceCatalogAsync(
        Project project, ResourceDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var entry = await GetOrCreateEntryAsync(project, cancellationToken);

        if (entry.Resources is { } cached && !entry.ResourceLayoutDirty && !entry.ResourceContentDirty)
            return cached;

        int genBefore;
        await s_lock.WaitAsync(cancellationToken);
        try { genBefore = entry.ResourceGeneration; }
        finally { s_lock.Release(); }

        var catalog = ResourceCatalogService.Get(project, options);

        await s_lock.WaitAsync(cancellationToken);
        try
        {
            entry.Resources = catalog;
            if (entry.ResourceGeneration == genBefore)
            {
                entry.ResourceLayoutDirty = false;
                entry.ResourceContentDirty = false;
            }
        }
        finally { s_lock.Release(); }

        return catalog;
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
                entry.ProtoDirty = true;
                entry.WrappersDirty = true;
                entry.ResourceContentDirty = true;
                entry.ResourceLayoutDirty = true;
            }
        }
        finally
        {
            s_lock.Release();
        }
    }

    // ---- Test hooks (exposed via InternalsVisibleTo) ----

    /// <summary>
    /// Raises the same file-change the <see cref="FileSystemWatcher"/> would for
    /// <paramref name="filePath"/>, against the cache entry for <paramref name="projectPath"/>.
    /// </summary>
    /// <remarks>
    /// The invalidation policy is the thing under test — which of these flags a given extension
    /// sets is exactly what decides whether a save costs one file or the whole site. Driving it
    /// through the real watcher instead would mean writing a file and waiting for an OS event
    /// that arrives on its own schedule, coalesced with its neighbours, which tests a
    /// <see cref="FileSystemWatcher"/> rather than this policy and flakes while doing it.
    /// Returns false when the project has no cache entry, so a test cannot silently assert
    /// nothing.
    /// </remarks>
    internal static bool NotifyFileChangedForTests(string projectPath, string filePath, bool movedFiles)
    {
        var key = Path.GetFullPath(projectPath);

        CachedProjectEntry? entry;
        s_lock.Wait();
        try { s_cache.TryGetValue(key, out entry); }
        finally { s_lock.Release(); }

        if (entry is null)
            return false;

        OnFileChanged(entry, Path.GetFullPath(filePath), movedFiles);
        return true;
    }

    private static async Task<CachedProjectEntry> GetOrCreateEntryAsync(
        Project project, CancellationToken cancellationToken)
    {
        var key = Path.GetFullPath(project.FilePath!);

        await s_lock.WaitAsync(cancellationToken);
        try
        {
            if (s_cache.TryGetValue(key, out var existing))
            {
                existing.LastAccessedUtc = DateTime.UtcNow;
                return existing;
            }

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

            // The event kind is kept rather than collapsed: a resource family's membership is a
            // function of the file names in a directory, so a create or a delete has to regroup
            // where a write only has to re-read.
            watcher.Changed += (_, e) => OnFileChanged(entry, e.FullPath, movedFiles: false);
            watcher.Created += (_, e) => OnFileChanged(entry, e.FullPath, movedFiles: true);
            watcher.Deleted += (_, e) => OnFileChanged(entry, e.FullPath, movedFiles: true);
            watcher.Renamed += (_, e) =>
            {
                OnFileChanged(entry, e.OldFullPath, movedFiles: true);
                OnFileChanged(entry, e.FullPath, movedFiles: true);
            };

            entry.Watcher = watcher;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProjectIndexCache] FileSystemWatcher setup failed: {ex.Message}");
        }
    }

    private static void OnFileChanged(CachedProjectEntry entry, string filePath, bool movedFiles)
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
        bool isProto = s_protoExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase));
        bool isCSharp = ext.Equals(".cs", StringComparison.OrdinalIgnoreCase);
        bool isResx = ext.Equals(".resx", StringComparison.OrdinalIgnoreCase);

        // A .cs only counts when one appeared or disappeared. The index maps markup files to the
        // code-behind beside them, so the set of .cs files can change it — but editing the contents
        // of one cannot, and treating every save as a change rebuilt the whole site: a recursive
        // walk over six extensions, then a read and a full parse of every .aspx/.ascx/.master in
        // it. On a large site that is thousands of files for a keystroke's worth of saved text.
        if (isAspx && !movedFiles)
        {
            // One file changed, so one file needs re-parsing. The whole-index flag is for changes
            // that alter what every parse would produce, or which files there are at all.
            entry.AspxChanged[filePath] = 0;
            Interlocked.Increment(ref entry.AspxGeneration);
        }
        else if (isAspx || (isCSharp && movedFiles))
        {
            entry.AspxDirty = true;
            Interlocked.Increment(ref entry.AspxGeneration);
        }

        if (isRazor || (isCSharp && movedFiles))
        {
            entry.RazorDirty = true;
            Interlocked.Increment(ref entry.RazorGeneration);
        }

        // The project file counts here and nowhere else: the graph spans the protos the project
        // compiles, and `Protobuf` items are what say which those are. No .cs, though — generated
        // code changing says nothing about an `import` line, and it lands under obj/ anyway.
        if (isProto || ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            entry.ProtoDirty = true;
            Interlocked.Increment(ref entry.ProtoGeneration);
        }

        // Pushed straight through rather than deferred, the way the resource catalog below is: the
        // parse cache is a process-wide static shared with the LSP handlers, and it keys an entry
        // on the text's checksum — which re-reads a rewritten file by itself, but leaves the entry
        // for a file that is now gone sitting there until the process ends.
        if (isProto && movedFiles)
            ProtoDocumentService.Invalidate(filePath);

        // All entries, not just this project's: the wrapper list is a union over the project and
        // its referenced projects, so a save in a shared utility library changes the answer for
        // every project that references it — and the library's own watcher is the only one that
        // sees the save. Cheap to over-invalidate: the per-document scan memo means a rebuild
        // re-reads only the file that changed.
        if (isCSharp)
            DirtyAllWrappers();

        if (isResx)
        {
            entry.ResourceContentDirty = true;
            entry.ResourceLayoutDirty |= movedFiles;
            Interlocked.Increment(ref entry.ResourceGeneration);

            // Pushed straight through rather than deferred to the next read: the catalog behind
            // this entry is a process-wide static shared with the LSP handlers, and both
            // operations are dictionary removals.
            if (movedFiles)
                ResourceCatalogService.InvalidateLayout(filePath);
            else
                ResourceCatalogService.InvalidateContent(filePath);
        }
    }

    private static void DirtyAllWrappers()
    {
        s_lock.Wait();
        try
        {
            foreach (var entry in s_cache.Values)
            {
                entry.WrappersDirty = true;
                Interlocked.Increment(ref entry.WrappersGeneration);
            }
        }
        finally { s_lock.Release(); }
    }

    private sealed class CachedProjectEntry : IDisposable
    {
        public AspxProjectIndex? AspxIndex { get; set; }
        public RazorSourceMap? RazorSourceMap { get; set; }
        public ProtoImportGraph? ProtoImportGraph { get; set; }
        public IReadOnlyList<(string MethodName, int ParamIndex, bool IsExtension)>? FindControlWrappers { get; set; }
        public ResourceCatalog? Resources { get; set; }
        public volatile bool AspxDirty = true;

        /// <summary>The project's dependent semantic version when <see cref="AspxIndex"/> was
        /// built. Null until one has been. Guards carrying parses over a change to what the tag
        /// prefixes in them resolve to.</summary>
        public VersionStamp? AspxIndexSemantic;

        /// <summary>Markup files whose own contents moved, awaiting a re-parse. Empty means the
        /// index is either current or wholly dirty; see <see cref="AspxDirty"/>.</summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> AspxChanged =
            new(StringComparer.OrdinalIgnoreCase);
        public volatile bool RazorDirty = true;
        public volatile bool ProtoDirty = true;
        public volatile bool WrappersDirty = true;

        /// <summary>A <c>.resx</c> was written. The families keep their members and lose their key
        /// tables.</summary>
        public volatile bool ResourceContentDirty = true;

        /// <summary>A <c>.resx</c> was created, deleted or renamed, so which families exist — and
        /// which files each one holds — has to be worked out again from the names on disk.</summary>
        public volatile bool ResourceLayoutDirty = true;
        public int AspxGeneration;
        public int RazorGeneration;
        public int ProtoGeneration;
        public int WrappersGeneration;
        public int ResourceGeneration;
        public DateTime LastAccessedUtc { get; set; } = DateTime.UtcNow;
        public FileSystemWatcher? Watcher { get; set; }

        public void Dispose()
        {
            Watcher?.Dispose();
            Watcher = null;
        }
    }
}

using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// workspace/didChangeWatchedFiles: keeps the loaded workspace honest about changes made
/// outside the editor — git checkouts, scaffolding, another agent's edits. Without this the
/// server answers from a stale snapshot until someone manually reloads, and the staleness
/// gets blamed on the language server.
///
/// Events are coalesced before acting: a branch switch fires hundreds at once and each one
/// would otherwise trigger its own reload.
/// </summary>
internal static class WatchedFilesHandler
{
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(500);

    private static readonly object s_gate = new();
    private static readonly List<FileEvent> s_pending = [];
    private static CancellationTokenSource? s_debounce;

    /// <summary>What a batch of events did — returned for tests and logging.</summary>
    internal sealed record Outcome(
        bool ReloadedWorkspace,
        IReadOnlyList<string> EvictedProjects,
        IReadOnlyList<string>? InvalidatedMarkup = null)
    {
        public bool DidAnything =>
            ReloadedWorkspace || EvictedProjects.Count > 0 || InvalidatedMarkup is { Count: > 0 };
    }

    public static void Handle(DidChangeWatchedFilesParams p)
    {
        lock (s_gate)
        {
            s_pending.AddRange(p.Changes);
            s_debounce?.Cancel();
            var cts = s_debounce = new CancellationTokenSource();
            _ = FlushAfterDelayAsync(cts.Token);
        }
    }

    private static async Task FlushAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Coalesce, ct);

            FileEvent[] batch;
            lock (s_gate)
            {
                batch = s_pending.ToArray();
                s_pending.Clear();
            }

            var outcome = await ProcessAsync(batch, CancellationToken.None);
            if (outcome.DidAnything)
                await LspSessionRegistry.RequestRefreshAsync(RefreshKind.All, CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lsp] Watched-file processing failed: {ex.Message}");
        }
    }

    /// <summary>The whole decision, without the debounce — the unit under test.</summary>
    internal static async Task<Outcome> ProcessAsync(
        IReadOnlyList<FileEvent> changes, CancellationToken ct)
    {
        var events = changes
            .Select(c => (Path: LspConverters.UriToPath(c.Uri), Change: KindOf(c.Type)))
            .Where(e => !IsIgnored(e.Path))
            .ToList();
        var paths = events.Select(e => e.Path).ToList();

        if (paths.Count == 0)
            return new Outcome(false, []);

        // A source or a credential changed. That needs the feed configuration reloaded, not the
        // workspace — and on its own it is not a reason to reload anything else.
        if (paths.Any(IsNuGetConfig))
        {
            Services.Packages.NuGetFeedContext.Invalidate();
            Services.Packages.PackageUpdateService.Invalidate();
        }

        // A pack's file changed under us. Which paths those are, and what has to be dropped for
        // one, is the pack's own business — a web.config is not markup but decides how every page
        // in the project binds — so each path is offered to every pack rather than to the first
        // one that claims it.
        //
        // The registered packs and not any one connection's enabled set: the caches behind this
        // are process-wide, the debounce below batches events from every window at once, and a
        // window that switched a pack off must not leave another window's editor reporting on
        // markup that has since changed on disk.
        var watchers = LanguageScope.Process.Contributors<ILanguageWatchedFileHandler>();
        var invalidatedMarkup = new List<string>();
        foreach (var (path, change) in events)
        {
            bool invalidated = false;
            foreach (var watcher in watchers)
                invalidated |= watcher.Invalidate(path, change);

            if (invalidated)
                invalidatedMarkup.Add(path);
        }

        // A project-shaping file changed: nothing short of a reload is correct, because
        // references, analyzers, and compile items all come from MSBuild evaluation.
        // Analyzer configuration changed: severities and analyzer options are baked into the
        // loaded project, and every cached analyzer result was computed under the old rules.
        if (paths.Any(IsProjectShaping) || paths.Any(IsAnalyzerConfig))
        {
            await using var progress = await ProgressReporter.BeginAsync("Reloading workspace", ct);
            AnalyzerDiagnosticCache.Clear();
            await WorkspaceService.EvictAllAsync(ct);
            return new Outcome(true, []);
        }

        // Source files added or removed on disk: the owning project's document set is wrong,
        // so evict just that project rather than the whole solution.
        var evicted = new List<string>();
        foreach (var projectPath in paths
                     .Where(IsSourceFile)
                     .SelectMany(FindNearestProjectFiles)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await WorkspaceService.EvictProjectAsync(projectPath, ct);
            evicted.Add(projectPath);
        }

        // No analyzer-cache clear here, and deliberately so: eviction reloads the solution under
        // fresh ids, so every cached result for it is keyed by a DocumentId nothing will ever ask
        // for again and ages out of the cap on its own. Clearing would also drop every other
        // solution's still-valid results.

        return new Outcome(false, evicted, invalidatedMarkup);
    }

    private static WatchedFileChange KindOf(int type) => type switch
    {
        FileChangeType.Created => WatchedFileChange.Created,
        FileChangeType.Deleted => WatchedFileChange.Deleted,
        _ => WatchedFileChange.Changed,
    };

    private static bool IsIgnored(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsProjectShaping(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".csproj" or ".vbproj" or ".fsproj" or ".props" or ".targets" or ".sln" or ".slnx" or ".slnf";

    private static bool IsNuGetConfig(string path) =>
        Path.GetFileName(path).Equals("nuget.config", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnalyzerConfig(string path)
    {
        string name = Path.GetFileName(path);
        return name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSourceFile(string path) =>
        Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Project files in the nearest ancestor directory that has any. Deliberately a
    /// disk walk rather than <see cref="WorkspaceService.FindContainingProjectAsync"/>: a
    /// just-created file is not in any loaded snapshot yet, which is exactly the case that
    /// needs handling.
    /// All candidates in that directory are returned — one folder can hold several projects,
    /// and picking one arbitrarily would evict the wrong snapshot and leave the stale one.</summary>
    internal static IReadOnlyList<string> FindNearestProjectFiles(string path)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(path) ?? path);
        while (dir is not null)
        {
            if (dir.Exists)
            {
                var projects = dir.EnumerateFiles("*.*proj")
                    .Where(f => f.Extension is ".csproj" or ".vbproj" or ".fsproj")
                    .Select(f => f.FullName)
                    .ToList();
                if (projects.Count > 0)
                    return projects;
            }
            dir = dir.Parent;
        }
        return [];
    }
}

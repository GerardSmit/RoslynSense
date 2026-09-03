using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.Language.Xml;

namespace RoslynMCP.Services;

/// <summary>
/// The solution a cross-project search has to run against: the one holding the anchor project,
/// widened with the projects that consume it.
/// </summary>
/// <remarks>
/// <para>
/// Loading is lazy and follows references, which for any project that declares something is exactly
/// the wrong direction: opening it brings in what <em>it</em> references and nothing that
/// references it. The things a search is looking for — the call sites of an extension method, the
/// implementations of an interface, the handlers of a mediator message, the consumers of a
/// contract — live in projects that reference the declaring assembly, so a sweep over the solution
/// as loaded reports "0 references" and "no implementations" for symbols the solution plainly uses.
/// Both are wrong in the way that looks like a working feature with an empty result, which is why
/// it is worth a load rather than a caveat.
/// </para>
/// <para>
/// Grown out of the Proto pack, which hit this first (a <c>.proto</c> lives in a contracts project
/// by construction) — hoisted here so C# navigation and the other packs widen the same way instead
/// of each rediscovering the hole.
/// </para>
/// <para>
/// Whether it runs at all is the caller's decision, and most callers say no. A shared project is
/// consumed by however many projects consume it — for a low-level one that is the entire solution,
/// fifty design-time builds serialized behind a single load gate. So an incidental caller — a code
/// lens resolving as the view scrolls, a hover — passes no budget and no load is started: it
/// searches what is open, and an answer in a project nobody has opened is not found until an
/// explicit gesture asks for it and waits.
/// </para>
/// </remarks>
internal static class SearchScopeService
{
    /// <summary>
    /// The budget an explicit user gesture (Shift+F12, Ctrl+F12, F12 on a dispatch) passes.
    /// </summary>
    /// <remarks>
    /// A capped budget was tried and is wrong. Cross-project search is the one answer this exists
    /// to give, and a cap turns "no results" and "the results are in projects that had not finished
    /// loading" into the same empty list — with nothing on screen to tell them apart. The wait is
    /// bounded by the request's own cancellation token instead, so the editor can still abandon it.
    /// </remarks>
    public static readonly TimeSpan ExplicitSearchBudget = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// <see cref="WidenAsync"/> anchored where the search really starts: the project declaring
    /// <paramref name="symbol"/>, when the solution can name it, and the caret's project otherwise.
    /// The declaration is where the consumers hang off — a caret in a project that merely uses the
    /// symbol says nothing about who else does.
    /// </summary>
    public static async Task<Solution> WidenForSymbolAsync(
        ISymbol symbol, Project project, TimeSpan? budget, CancellationToken ct)
    {
        var anchor = symbol.ContainingAssembly is { } assembly
            ? project.Solution.GetProject(assembly, ct) ?? project
            : project;

        return await WidenAsync(anchor, budget, ct);
    }

    /// <summary>
    /// The solution widened with every project that reaches <paramref name="project"/> through a
    /// <c>ProjectReference</c>, directly or through another project — loaded, when the budget
    /// allows, into the same workspace the project is already in.
    /// </summary>
    /// <param name="budget">
    /// How long the caller may wait for the consumers to load, or <see langword="null"/> — the same
    /// as <see cref="TimeSpan.Zero"/> — to search only what is already open and start nothing.
    /// </param>
    public static async Task<Solution> WidenAsync(
        Project project, TimeSpan? budget, CancellationToken ct)
    {
        if (project.FilePath is not { Length: > 0 } path)
            return project.Solution;

        var (workspace, scoped) = await WorkspaceService.GetOrOpenProjectAsync(
            path, diagnosticWriter: TextWriter.Null, cancellationToken: ct);

        if (budget is { } wait && wait != TimeSpan.Zero)
        {
            // Memoized against the workspace it loaded into, not against the path. The workspace is
            // evicted after an idle timeout or by the LRU cap, and a memo that outlived its
            // workspace would report the consumers as loaded into a snapshot that no longer holds
            // them — narrowing every later search for the life of the process, silently.
            var consumers = ConsumerLoadFor(path, workspace);

            try
            {
                await consumers.WaitAsync(wait, ct);
            }
            catch (TimeoutException)
            {
                // Answer with what is loaded. The rest arrives on a later request.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Un-memoised on failure: a faulted task left in the cache is awaited by every
                // later navigation from this project and re-throws the same failure for the life
                // of the process.
                s_consumers.TryRemove(path, out _);
            }

            // Re-read: the load added projects to the workspace, and the snapshot taken above
            // predates them.
            (_, scoped) = await WorkspaceService.GetOrOpenProjectAsync(
                path, diagnosticWriter: TextWriter.Null, cancellationToken: ct);
        }

        // The returned project's own solution, not the workspace's. They differ by the open-editor
        // overlay: GetOrOpenProjectAsync hands back a snapshot with unsaved buffers applied, and
        // Workspace.CurrentSolution is the same project set without them. Reading the workspace
        // back was wrong twice over — a find-usages ran against the text on disk rather than the
        // text on screen, so a call site the user had just typed was not found and one they had
        // just deleted was; and because the overlay is a fork, the two snapshots each build their
        // own compilation of the project the user has open, which is the most expensive project in
        // the solution to compile twice.
        return scoped.Solution;
    }

    /// <summary>
    /// The consumer-loading task for this project in this workspace, started if there is not one
    /// already, and restarted when the workspace it was started against has been replaced.
    /// </summary>
    private static Task ConsumerLoadFor(string path, Workspace workspace)
    {
        while (true)
        {
            if (s_consumers.TryGetValue(path, out var existing))
            {
                if (ReferenceEquals(existing.Workspace, workspace))
                    return existing.Load;

                // Stale: the workspace this was loaded into is gone. Drop it and fall through to
                // start a fresh one. A racing caller that gets there first wins, and this loop
                // then sees its entry.
                if (!s_consumers.TryRemove(new KeyValuePair<string, ConsumerLoad>(path, existing)))
                    continue;
            }

            // Detached from the caller's token deliberately: a load abandoned half way leaves the
            // workspace in the state the next caller would have to redo, and this task outlives
            // any one request by design.
            var started = new ConsumerLoad(workspace, LoadConsumersAsync(path, CancellationToken.None));

            if (s_consumers.TryAdd(path, started))
                return started.Load;
        }
    }

    private sealed record ConsumerLoad(Workspace Workspace, Task Load);

    private static readonly ConcurrentDictionary<string, ConsumerLoad> s_consumers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Opens every project that consumes this one, in a single batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One <see cref="WorkspaceService.EnsureProjectsLoadedAsync"/> call rather than a
    /// <c>foreach</c> of single opens. Roslyn's per-call cost — a <c>dotnet</c> BuildHost
    /// subprocess and a cold MSBuild <c>ProjectCollection</c> — is paid once for the whole
    /// consumer set instead of once per consumer, which on a generated 34-project solution took
    /// the eight-consumer sweep from 14.3 s to 3.7 s. The declaring project is listed first so the
    /// batch is anchored to the workspace the anchor is already in.
    /// </para>
    /// <para>
    /// A project that will not load is reported and skipped rather than allowed to end the sweep —
    /// a legacy project needing a BuildHost is the usual one. The answer is then as narrow as it
    /// was before this ran, which is a worse result and not a failed request.
    /// </para>
    /// </remarks>
    private static async Task LoadConsumersAsync(string projectPath, CancellationToken ct)
    {
        var consumers = Consumers(projectPath);
        if (consumers.Count == 0)
            return;

        await WorkspaceService.EnsureProjectsLoadedAsync([projectPath, .. consumers], ct);
    }

    /// <summary>
    /// The projects of the owning solution that reach this one through a
    /// <c>ProjectReference</c>, directly or through another project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The solution's own project list rather than a directory walk. It is exact, it is bounded,
    /// and it is the same file the workspace keys its shared cache on — so every project named here
    /// joins the workspace the anchor is already in rather than opening one of its own.
    /// </para>
    /// <para>
    /// Transitive, because one hop is not the shape a shared assembly has: the declaration is in a
    /// contracts project, a service layer consumes it, and the project actually holding the answer
    /// references that. Reading the references out of the XML rather than asking MSBuild, because
    /// the answer is one attribute per element, and evaluating a project to recover it would cost
    /// more than the load it is deciding.
    /// </para>
    /// </remarks>
    private static List<string> Consumers(string projectPath)
    {
        string? solution;
        try
        {
            solution = PathHelper.FindNearestSolution(projectPath);
        }
        catch (ArgumentException)
        {
            return [];
        }

        if (solution is not { Length: > 0 })
            return [];

        var projects = PathHelper.GetProjectsFromSolution(solution)
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var references = projects.ToDictionary(
            project => project, ProjectReferencesOf, StringComparer.OrdinalIgnoreCase);

        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Normalize(projectPath),
        };

        // Repeated until nothing new is reached, rather than one pass: a project's own consumers
        // may be listed before it, and the solution's order says nothing about the graph.
        for (bool grew = true; grew; )
        {
            grew = false;

            foreach (string project in projects)
            {
                if (!reached.Contains(project) && references[project].Any(reached.Contains))
                    grew |= reached.Add(project);
            }
        }

        reached.Remove(Normalize(projectPath));
        return [.. reached];
    }

    /// <summary>The projects one project file references, absolute and normalised. Empty for a
    /// project file that cannot be read, which is the same answer as one that references
    /// nothing.</summary>
    private static List<string> ProjectReferencesOf(string projectPath)
    {
        XmlDocumentSyntax document;
        try
        {
            document = Parser.ParseText(File.ReadAllText(projectPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        string directory = Path.GetDirectoryName(projectPath) ?? string.Empty;
        var results = new List<string>();

        foreach (var element in document.DescendantsByLocalName(
            "ProjectReference", StringComparison.OrdinalIgnoreCase))
        {
            if (element.GetAttributeValue("Include") is not { Length: > 0 } include)
                continue;

            results.Add(Normalize(
                Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar))));
        }

        return results;
    }

    private static string Normalize(string filePath)
    {
        try
        {
            return PathHelper.NormalizePath(filePath);
        }
        catch (ArgumentException)
        {
            return filePath;
        }
    }
}

using System.Globalization;
using Microsoft.CodeAnalysis;
using RoslynMCP.Languages.Routes.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;
using RoslynMCP.Services.Symbols;

namespace RoslynMCP.Languages.Routes;

/// <summary>
/// The <b>Routes</b> section of the Discovery view: which projects serve HTTP, and what each of
/// them answers to.
/// </summary>
/// <remarks>
/// <para>
/// Grouped by project rather than by controller or by path prefix, which is the grouping a reader
/// can act on: an endpoint's project is what they have to run, what they have to deploy and where
/// they have to look. A path prefix would read better and would be a lie in the common case — two
/// projects behind one gateway serve <c>/api</c> between them, and merging their rows would put an
/// endpoint under a heading it does not live in.
/// </para>
/// <para>
/// The section is drawn on the root listing, which happens every time the view becomes visible and
/// must therefore evaluate no project. So the decision to show it comes from
/// <see cref="RouteProjectProbe"/> — the SDK attribute and a text scan of the manifests — or from
/// the user having configured a binding, which is the case a framework probe would miss: an
/// in-house routing layer lives in a project referencing no web framework, and hiding the section
/// from the one person who configured it would be the worst answer available.
/// </para>
/// </remarks>
internal sealed partial class RoutesLanguage : ILanguageDiscoveryContributor
{
    /// <summary>The section, and the prefix of everything under it.</summary>
    private const string Prefix = "routes:";

    /// <summary>One project inside the section.</summary>
    private const string ProjectPrefix = Prefix + "p|";

    /// <summary>One endpoint.</summary>
    private const string EndpointPrefix = Prefix + "e|";

    /// <summary>One shared path prefix inside a project: <c>routes:g|{project}|{prefix}</c>.</summary>
    private const string GroupPrefix = Prefix + "g|";

    /// <summary>What wraps a value that is only knowable at run time. See the cron section.</summary>
    private const char Open = '⟨';

    private const char Close = '⟩';

    public string NodeIdPrefix => Prefix;

    public Task<SolutionTreeNode?> SectionAsync(string solutionPath, CancellationToken ct)
    {
        if (!Settings.Enabled || Projects(ct).Count == 0)
            return Task.FromResult<SolutionTreeNode?>(null);

        return Task.FromResult<SolutionTreeNode?>(new SolutionTreeNode(
            Id: Prefix + solutionPath,
            Kind: SolutionNodeKind.Routes,
            Label: "Routes",
            Description: null,
            ResourceUri: null,
            HasChildren: true,
            ContextValue: SolutionNodeKind.Routes));
    }

    public async Task<SolutionTreeNode[]> ChildrenAsync(
        string nodeId, SolutionTreeParams p, CancellationToken ct)
    {
        if (!Settings.Enabled)
            return [];

        // An endpoint is a leaf, so a request for its children is a client that has lost its place
        // rather than a question. Answering with the project list would fill the row underneath it
        // with the whole section.
        if (nodeId.StartsWith(EndpointPrefix, StringComparison.Ordinal))
            return [];

        if (nodeId.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            string rest = nodeId[GroupPrefix.Length..];
            int split = rest.IndexOf('|', StringComparison.Ordinal);

            return split < 0
                ? []
                : await LevelAsync(rest[..split], rest[(split + 1)..], ct);
        }

        return nodeId.StartsWith(ProjectPrefix, StringComparison.Ordinal)
            ? await LevelAsync(nodeId[ProjectPrefix.Length..], string.Empty, ct)
            : await ProjectNodesAsync(ct);
    }

    /// <summary>
    /// The projects the section lists, one row each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A project that serves nothing is not listed. The probe that decided to look at it reads
    /// manifests, so it says "could serve HTTP" rather than "does" — a project referencing the
    /// framework and declaring no endpoint is ordinary, and a row reading <c>0 routes</c> is a
    /// question the reader then has to answer for themselves.
    /// </para>
    /// <para>
    /// A project the workspace has not loaded is a different answer and is kept, marked. Hiding it
    /// would empty the section during a solution load and then fill it in, which reads as the
    /// section being wrong rather than as the load being unfinished.
    /// </para>
    /// <para>
    /// Counted in parallel, which is the whole cost of drawing this level: each count is a
    /// compilation of that project and its closure. Serially, a solution with thirty web projects
    /// spends the sum of thirty cold compilations before the first row appears; Roslyn builds them
    /// concurrently perfectly well, so the wait becomes the slowest one instead of all of them.
    /// </para>
    /// </remarks>
    private async Task<SolutionTreeNode[]> ProjectNodesAsync(CancellationToken ct)
    {
        var projects = Projects(ct)
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var loaded = LoadedByPath();

        int?[] counts = await Task.WhenAll(
            projects.Select(project => CountAsync(loaded, project.Path, ct)));

        var rows = new List<SolutionTreeNode>(projects.Count);

        for (int i = 0; i < projects.Count; i++)
        {
            if (ProjectNode(projects[i].Name, projects[i].Path, counts[i]) is { } row)
                rows.Add(row);
        }

        return [.. rows];
    }

    /// <summary>
    /// One project as a row, or null when it has earned no row.
    /// </summary>
    /// <remarks>
    /// The three answers the count can carry, in one place and pure, because they are three
    /// different statements to a reader: a number, "the workspace has not got here yet", and
    /// "nothing to say" — and only the last one means no row.
    /// </remarks>
    internal static SolutionTreeNode? ProjectNode(string name, string projectPath, int? count) =>
        count is 0
            ? null
            : new SolutionTreeNode(
                Id: ProjectPrefix + projectPath,
                Kind: SolutionNodeKind.RouteProject,
                Label: name,
                Description: count switch
                {
                    null => "not loaded",
                    1 => "1 route",
                    var many => $"{many} routes",
                },
                ResourceUri: LspConverters.PathToUri(projectPath),
                HasChildren: count is not null,
                ContextValue: SolutionNodeKind.RouteProject,
                Dimmed: count is null);

    /// <summary>How many endpoints a project declares, or null when it is not loaded.</summary>
    private async Task<int?> CountAsync(
        Dictionary<string, Project> loaded, string projectPath, CancellationToken ct)
    {
        if (!loaded.TryGetValue(Path.GetFullPath(projectPath), out var project))
            return null;

        return await project.GetCompilationAsync(ct) is { } compilation
            ? Endpoints.Of(compilation, projectPath, ct).Count
            : null;
    }

    /// <summary>
    /// One level of a project's path tree: the shared prefixes under it, then the endpoints that
    /// sit directly on it.
    /// </summary>
    /// <remarks>
    /// Branches first and then leaves, rather than one sequence ordered by path. A branch is a
    /// heading and a leaf is a row, and interleaving them by name puts headings in the middle of
    /// the list they head. Within each part the order is the route-table one — path, then verb —
    /// so the endpoints of one resource stay together whichever file declares them.
    /// </remarks>
    private async Task<SolutionTreeNode[]> LevelAsync(
        string projectPath, string prefix, CancellationToken ct)
    {
        if (!LoadedByPath().TryGetValue(Path.GetFullPath(projectPath), out var project))
            return [];

        if (await project.GetCompilationAsync(ct) is not { } compilation)
            return [];

        var level = RouteGrouping.Level(Endpoints.Of(compilation, projectPath, ct), prefix);

        return
        [
            .. level.Groups.Select(branch => Branch(projectPath, branch)),
            .. level.Leaves.Select(endpoint => Node(endpoint, prefix)),
        ];
    }

    /// <summary>One shared path prefix, as a row.</summary>
    private static SolutionTreeNode Branch(string projectPath, RouteBranch branch) =>
        new(Id: $"{GroupPrefix}{projectPath}|{branch.Prefix}",
            Kind: SolutionNodeKind.RouteGroup,
            Label: branch.Label,
            Description: branch.Count == 1 ? "1 route" : $"{branch.Count} routes",
            ResourceUri: null,
            HasChildren: true,
            ContextValue: SolutionNodeKind.RouteGroup);

    /// <summary>One endpoint, as a row.</summary>
    /// <remarks>
    /// A pure function from the endpoint to the row, which is what makes the rendering decisions —
    /// what a dynamic path looks like, which target a click gets — checkable without a loaded
    /// workspace behind them.
    /// </remarks>
    internal static SolutionTreeNode Node(RouteEndpoint endpoint) => Node(endpoint, string.Empty);

    /// <summary>One endpoint, as a row under a branch that has already said <paramref name="prefix"/>.</summary>
    internal static SolutionTreeNode Node(RouteEndpoint endpoint, string prefix) =>
        new(
            // The verb belongs in the id as well: one attribute can declare two endpoints —
            // [Route("x")] with [HttpGet] and [HttpPost] beside it — and they would otherwise share
            // an offset, which makes the second row fail to render.
            Id: string.Create(
                CultureInfo.InvariantCulture,
                $"{EndpointPrefix}{endpoint.ProjectPath}|{endpoint.FilePath}|{endpoint.Offset}|{endpoint.Verb}"),
            Kind: SolutionNodeKind.Route,
            Label: Label(endpoint, prefix),
            Description: endpoint.Verb,
            ResourceUri: LspConverters.PathToUri(endpoint.FilePath),
            HasChildren: false,
            ContextValue: ContextValue(endpoint),
            Tooltip: Tooltip(endpoint),

            // The declaration rather than the handler: it is where the path, the verb and the
            // constraints are written, and it is the half of the pair that has no other way in —
            // the handler is a method Ctrl+T already finds.
            GoTo: new SolutionTreeNavigation(
                LspConverters.PathToUri(endpoint.FilePath), endpoint.Declaration),
            GoToSecondary: endpoint.Target is { } target && endpoint.TargetUri is { } uri
                ? new SolutionTreeNavigation(uri, target)
                : null);

    /// <summary>
    /// What the menu is allowed to offer on this row: the base name plus a mark for a path only
    /// knowable at run time, plus one for a handler there is somewhere to go to.
    /// </summary>
    private static string ContextValue(RouteEndpoint endpoint) =>
        SolutionNodeKind.Route
        + (endpoint.IsDynamic ? SolutionNodeKind.RouteDynamicSuffix : string.Empty)
        + (endpoint.Target is not null ? SolutionNodeKind.SecondaryTargetSuffix : string.Empty);

    /// <summary>
    /// The verb and the path, which is what a reader came for.
    /// </summary>
    /// <remarks>
    /// A path nobody could read is shown as where it comes from, never as a path. Printing the half
    /// that was readable would give a reader no way to tell that the row is a guess, and a URL is
    /// the kind of thing people copy.
    /// </remarks>
    /// <summary>
    /// The path, and only the path.
    /// </summary>
    /// <remarks>
    /// The verb is the row's description rather than the head of its label, which is the difference
    /// between a list you read and a list you scan. Leading with the verb ragged-lefts the paths —
    /// the column a reader is actually looking down — behind three, four and six characters of
    /// <c>GET</c>, <c>POST</c> and <c>DELETE</c>, and sorts a resource's own rows apart from each
    /// other. As a description it sits to the right in the dimmed style the tree already uses for
    /// the secondary half of a row.
    /// <para>
    /// No verb is not "GET". An action reachable by every method is a real thing, and the row says
    /// so by leaving the description empty rather than by inventing one.
    /// </para>
    /// </remarks>
    private static string Label(RouteEndpoint endpoint, string prefix) =>
        endpoint.Path.Text is { Length: > 0 }
            ? RouteGrouping.Remainder(endpoint, prefix)
            : $"{Open}{endpoint.Path.Detail ?? "computed"}{Close}";

    /// <summary>What handles it and where, for the hover.</summary>
    /// <remarks>
    /// Not shown on the row. A controller's actions all carry the same type name, so as a
    /// description it repeats itself down the whole branch and pushes nothing but noise into the
    /// column beside the path — which is the one thing a reader is scanning. On the hover it is
    /// worth having, together with the file and line, because "where is this actually served" is
    /// the next question after "what does it serve".
    /// </remarks>
    private static string Tooltip(RouteEndpoint endpoint)
    {
        var parts = new List<string>(3);

        if (endpoint.Path.Text is { Length: > 0 } path)
            parts.Add(endpoint.Verb is { Length: > 0 } verb ? $"{verb} {path}" : path);
        else if (endpoint.Path.Detail is { Length: > 0 } why)
            parts.Add($"Path is only knowable at run time ({why}).");

        if (endpoint.Handler.Text is { Length: > 0 } handler)
            parts.Add(handler);

        // 1-based, because that is what the editor's own status bar says and what somebody reading
        // a hover is about to type into a Go To Line box.
        parts.Add($"{Path.GetFileName(endpoint.FilePath)}:{endpoint.Declaration.Start.Line + 1}");

        return string.Join(" — ", parts);
    }

    /// <summary>
    /// The projects worth listing: those that could serve HTTP, or all of them once the user has
    /// configured a declaration of their own.
    /// </summary>
    private List<(string Path, string Name)> Projects(CancellationToken ct)
    {
        var markers = Settings.SourceMarkers;
        bool configured = Settings.IsConfigured;
        var found = new List<(string Path, string Name)>();

        foreach (var project in SolutionProjectIndex.Projects())
        {
            ct.ThrowIfCancellationRequested();

            if (configured || RouteProjectProbe.Serves(project.Path, markers))
                found.Add(project);
        }

        return found;
    }

    /// <summary>
    /// The loaded projects by full path, or an empty map when no solution is bound.
    /// </summary>
    /// <remarks>
    /// Built once per level rather than scanned per project. The scan it replaces normalised both
    /// sides of every comparison, so listing forty projects out of a solution of two hundred cost
    /// sixteen thousand path normalisations to answer forty questions.
    /// </remarks>
    /// <remarks>
    /// Read off the current snapshot rather than through the workspace cache's index, for the
    /// reason the tree's own <c>IsLoaded</c> gives: the index is behind the lock a solution load
    /// holds, so asking it here would stall the view behind the load whose progress it is drawing.
    /// </remarks>
    private static Dictionary<string, Project> LoadedByPath()
    {
        var byPath = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);

        if (WorkspaceService.TryGetSessionSolution() is not { } solution)
            return byPath;

        foreach (var project in solution.Projects)
        {
            if (project.FilePath is { Length: > 0 } path)
                byPath[Path.GetFullPath(path)] = project;
        }

        return byPath;
    }
}

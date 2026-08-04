using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.Packages;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// The Solution Explorer's server side: the solution's logical structure (solution folders and
/// all), each project's Dependencies subtree, and its files nested the way Visual Studio and
/// Rider nest them.
///
/// Node ids are stable strings so reveal and per-node refresh work without rebuilding the
/// tree, and children are produced on demand — the root listing comes from the .sln parse
/// alone, with no MSBuild evaluation at all until something is expanded.
/// </summary>
internal static class SolutionTreeHandler
{
    private const string DependenciesSuffix = "!deps";

    public static async Task<SolutionTreeNode[]> ChildrenAsync(
        SolutionTreeParams p, CancellationToken ct)
    {
        // The binding first: the tree reads the .sln from disk and needs only its path, which is
        // known from startup. Waiting for a loaded workspace meant an empty Explorer until the
        // user opened a file — and the daemon starts with nothing loaded at all.
        string? solutionPath =
            WorkspaceService.BoundSolutionPath ?? WorkspaceService.TryGetMostRecentSolution()?.FilePath;

        SolutionTreeNode[] nodes;
        try
        {
            nodes = p.NodeId switch
            {
                // A filter replaces the root with what matches, anywhere in the solution.
                // Narrowing the root level by label stopped working the moment the tree grew a
                // solution node: the only thing at that level is the solution's own name, so
                // filtering by anything else emptied the tree instead of narrowing it.
                null or "" when p.Filter is { Length: > 0 } query =>
                    await SolutionTreeSearchHandler.SearchAsync(
                        new SolutionTreeSearchParams(query, Limit: 200), ct),
                null or "" => Roots(solutionPath),
                var id when id.StartsWith("folder:", StringComparison.Ordinal) =>
                    await FolderChildrenAsync(id["folder:".Length..], p, ct),
                var id when id.EndsWith(DependenciesSuffix, StringComparison.Ordinal) =>
                    await DependencyGroupsAsync(id[..^DependenciesSuffix.Length], ct),
                var id when id.StartsWith("group:", StringComparison.Ordinal) =>
                    await GroupChildrenAsync(id["group:".Length..], ct),
                var id when id.StartsWith("package:", StringComparison.Ordinal) =>
                    PackageChildren(id["package:".Length..]),
                var id when id.StartsWith("project:", StringComparison.Ordinal) =>
                    await ProjectChildrenAsync(id["project:".Length..], p, ct),
                var id when id.StartsWith("slnfolder:", StringComparison.Ordinal) =>
                    SolutionFolderChildren(solutionPath, id["slnfolder:".Length..], p),
                var id when id.StartsWith("solution:", StringComparison.Ordinal) =>
                    SolutionChildren(id["solution:".Length..], p),
                var id when id.StartsWith("file:", StringComparison.Ordinal) =>
                    await NestedChildrenAsync(id["file:".Length..], p, ct),
                _ => [],
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The client cannot tell a failed request from an empty node, so a crash in here
            // reads as "this project has nothing in it". Saying so out loud is the difference
            // between a bug report and a mystery.
            ServiceLog.Warn(
                $"Could not list '{p.NodeId ?? "the solution"}': {ex.Message}",
                key: $"solution-tree:{p.NodeId}");
            return [];
        }

        // The root already answered with matches from across the solution; filtering those by
        // label again would drop the ones that ranked rather than matched literally. Every
        // other level narrows in place, which is what filtering inside a project means.
        return string.IsNullOrEmpty(p.NodeId) ? nodes : Filter(nodes, p.Filter);
    }

    /// <summary>
    /// The solution itself, as one node with everything beneath it.
    /// </summary>
    /// <remarks>
    /// Visual Studio and Rider both root the tree at the solution rather than at its top-level
    /// contents, and it is not only cosmetic: without a node for the solution there is nothing
    /// to right-click to act on it, so adding a solution folder or opening the <c>.sln</c> had
    /// no home in the tree.
    /// </remarks>
    private static SolutionTreeNode[] Roots(string? solutionPath)
    {
        if (solutionPath is null)
            return [];

        var all = SolutionFileService.Read(solutionPath);
        int projects = all.Count(n => !n.IsFolder);

        return
        [
            new SolutionTreeNode(
                Id: $"solution:{solutionPath}",
                Kind: SolutionNodeKind.Solution,
                Label: Path.GetFileNameWithoutExtension(solutionPath),
                Description: projects == 1 ? "1 project" : $"{projects} projects",
                ResourceUri: LspConverters.PathToUri(solutionPath),
                HasChildren: all.Count > 0,
                ContextValue: SolutionNodeKind.Solution),
        ];
    }

    /// <summary>
    /// Every project in the solution, flat — what a "reference another project" picker needs.
    /// </summary>
    public static SolutionProjectInfo[] Projects()
    {
        return [.. SolutionProjectIndex.Projects()
            .Select(p => new SolutionProjectInfo(p.Path, p.Name))];
    }

    /// <summary>
    /// The framework assemblies a project could reference. Empty for anything but a .NET
    /// Framework target, which is what the client uses to decide whether to offer the command.
    /// </summary>
    public static string[] AssemblyReferences(SolutionTreeSearchParams p) =>
        [.. ProjectMutationService.AvailableAssemblyReferences(p.Query)];

    /// <summary>What a new project can be created from, and for which frameworks.</summary>
    public static async Task<ProjectTemplateChoices> TemplatesAsync(CancellationToken ct)
    {
        var templates = await ProjectTemplateService.ListAsync(ct);
        var frameworks = await ProjectTemplateService.TargetFrameworksAsync(ct);

        return new ProjectTemplateChoices(
            templates.Select(t => new ProjectTemplateChoice(t.Name, t.ShortName, t.Tags)).ToArray(),
            frameworks);
    }

    /// <summary>
    /// Solution folders first, then projects, each run alphabetically.
    /// </summary>
    /// <remarks>
    /// Without this the tree shows whatever order the <c>.sln</c> lists its <c>Project(...)</c>
    /// blocks in, which is the order things happened to be added and reads as no order at all.
    /// </remarks>
    private static IEnumerable<SolutionNode> Ordered(IEnumerable<SolutionNode> nodes) =>
        nodes
            .OrderBy(n => n.IsFolder ? 0 : 1)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase);

    private static SolutionTreeNode[] SolutionChildren(string solutionPath, SolutionTreeParams p)
    {
        var all = SolutionFileService.Read(solutionPath);
        var roots = all.Where(n => n.ParentId is null).ToList();

        // A solution with no folder structure still has projects; fall back to listing them.
        if (roots.Count == 0)
            roots = all.ToList();

        return Ordered(roots).Select(node => ToNode(node, all, p)).ToArray();
    }

    private static SolutionTreeNode[] SolutionFolderChildren(
        string? solutionPath, string folderId, SolutionTreeParams p)
    {
        if (solutionPath is null)
            return [];

        var all = SolutionFileService.Read(solutionPath);
        var folder = all.FirstOrDefault(n => n.Id == folderId);

        var children = Ordered(all.Where(n => n.ParentId == folderId)).Select(n => ToNode(n, all, p));
        var files = (folder?.Files ?? [])
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(file => new SolutionTreeNode(
                // Not "file:", which is what the same file is called inside the project that
                // compiles it. A file can be both, and the client keys tree items by id: two
                // nodes sharing one id makes the second branch fail to render. The folder is
                // part of the id because it is also the only way back to which folder listed
                // it, which detaching the item needs.
                Id: $"slnitem:{folderId}|{file}",
                Kind: SolutionNodeKind.SolutionItem,
                Label: Path.GetFileName(file),
                Description: null,
                ResourceUri: LspConverters.PathToUri(file),
                HasChildren: false,
                ContextValue: SolutionNodeKind.SolutionItem));

        return children.Concat(files).ToArray();
    }

    private static SolutionTreeNode ToNode(
        SolutionNode node, IReadOnlyList<SolutionNode> all, SolutionTreeParams p)
    {
        if (node.IsFolder)
        {
            int projectCount = CountProjects(node.Id, all);
            return new SolutionTreeNode(
                Id: $"slnfolder:{node.Id}",
                Kind: SolutionNodeKind.SolutionFolder,
                Label: node.Name,
                Description: projectCount > 0 ? $"{projectCount} projects" : null,
                ResourceUri: null,
                HasChildren: all.Any(n => n.ParentId == node.Id) || node.Files.Count > 0,
                ContextValue: SolutionNodeKind.SolutionFolder);
        }

        // An unloaded project is drawn but not evaluated: the point of unloading it is to stop
        // paying for it, and expanding it is the expensive part.
        bool unloaded = node.Path is { Length: > 0 } && IsUnloaded(node.Path, p);

        return new SolutionTreeNode(
            Id: $"project:{node.Path}",
            Kind: SolutionNodeKind.Project,
            Label: node.Name,
            Description: unloaded ? "unloaded" : null,
            ResourceUri: node.Path is null ? null : LspConverters.PathToUri(node.Path),
            HasChildren: !unloaded,
            // The context value carries runnability so the row's Run and Debug actions are
            // shown only where they would do something. Classification is a file scan with an
            // mtime cache, which keeps the root listing free of MSBuild evaluation.
            ContextValue: unloaded
                ? SolutionNodeKind.UnloadedProject
                : node.Path is { Length: > 0 } path && ProjectClassifier.Classify(path).IsRunnable
                    ? SolutionNodeKind.RunnableProject
                    : SolutionNodeKind.Project,
            Dimmed: unloaded);
    }

    private static bool IsUnloaded(string projectPath, SolutionTreeParams p) =>
        p.UnloadedProjects is { Length: > 0 } unloaded
        && unloaded.Any(path => string.Equals(
            Path.GetFullPath(path), Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase));

    /// <summary>Projects anywhere beneath a solution folder — the count Rider shows next to it.</summary>
    private static int CountProjects(string folderId, IReadOnlyList<SolutionNode> all)
    {
        int count = 0;
        foreach (var child in all.Where(n => n.ParentId == folderId))
            count += child.IsFolder ? CountProjects(child.Id, all) : 1;
        return count;
    }

    private static async Task<SolutionTreeNode[]> ProjectChildrenAsync(
        string projectPath, SolutionTreeParams p, CancellationToken ct)
    {
        bool isNetFramework =
            ProjectClassifier.Classify(projectPath).DebugRuntime == DebugRuntime.NetFramework;

        var nodes = new List<SolutionTreeNode>
        {
            new($"{projectPath}{DependenciesSuffix}", SolutionNodeKind.Dependencies,
                "Dependencies", null, null, HasChildren: true,
                isNetFramework ? SolutionNodeKind.DependenciesNetFx : SolutionNodeKind.Dependencies),
        };

        nodes.AddRange(await FolderContentsAsync(
            projectPath, Path.GetDirectoryName(projectPath) ?? projectPath, p, ct));
        return nodes.ToArray();
    }

    private static async Task<SolutionTreeNode[]> DependencyGroupsAsync(
        string projectPath, CancellationToken ct)
    {
        var evaluation = await ProjectEvaluationService.EvaluateAsync(projectPath, ct);
        if (evaluation is null)
            return [];

        var groups = new List<SolutionTreeNode>();

        void Add(string kind, string label, int count)
        {
            if (count > 0)
                groups.Add(new SolutionTreeNode(
                    $"group:{kind}|{projectPath}", kind, label,
                    Description: null, ResourceUri: null, HasChildren: true, ContextValue: kind));
        }

        Add(SolutionNodeKind.Imports, "Imports", evaluation.Imports.Count);

        foreach (string framework in evaluation.TargetFrameworks)
        {
            groups.Add(new SolutionTreeNode(
                $"group:{SolutionNodeKind.Framework}|{projectPath}|{framework}",
                SolutionNodeKind.Framework, framework,
                Description: null, ResourceUri: null, HasChildren: false,
                ContextValue: SolutionNodeKind.Framework));
        }

        // Generated files hang off Dependencies rather than the project's folders, because they
        // have no folder — they exist only inside the compilation.
        var generated = await VirtualDocumentHandler.ListGeneratedAsync(projectPath, ct);
        if (generated.Length > 0)
        {
            groups.Add(new SolutionTreeNode(
                $"group:{SolutionNodeKind.Generator}|{projectPath}", SolutionNodeKind.Generator,
                "Source Generators", $"{generated.Length} files", ResourceUri: null,
                HasChildren: true, ContextValue: SolutionNodeKind.Generator));
        }

        Add(SolutionNodeKind.Packages, "Packages", evaluation.PackageReferences.Count);

        // Everything restore resolved that the project never asked for by name. This is where a
        // vulnerable dependency two levels down is actually visible; nothing in the project file
        // mentions it.
        Add(SolutionNodeKind.Transitive, "Transitive",
            ProjectAssetsService.TransitiveOnly(projectPath, targetFramework: null).Count);

        Add(SolutionNodeKind.Projects, "Projects", evaluation.ProjectReferences.Count);
        Add(SolutionNodeKind.Assemblies, "Assemblies", evaluation.AssemblyReferences.Count);
        Add(SolutionNodeKind.Analyzers, "Analyzers", evaluation.Analyzers.Count);

        return groups.ToArray();
    }

    /// <summary>
    /// What one package brought in with it, from the resolved graph.
    /// </summary>
    /// <remarks>
    /// One level only, and the children do not expand further. <c>project.assets.json</c> stores a
    /// flat resolved set rather than a tree, so a deeper hierarchy would be a shape we invented —
    /// and one that can loop, because two packages may legitimately depend on each other's
    /// resolved versions.
    /// </remarks>
    private static SolutionTreeNode[] PackageChildren(string packageId)
    {
        var parts = packageId.Split('|');
        if (parts.Length < 2)
            return [];

        return [.. ProjectAssetsService
            .DependenciesOf(parts[0], parts[1], targetFramework: null)
            .Select(package => TransitiveNode(parts[0], parts[1], package))
            .OrderBy(node => node.Label, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// A transitive package node. The id carries the parent and the framework because the same
    /// package legitimately appears under several parents and once per target framework, and the
    /// tree requires ids to be unique across the whole view — a repeat makes the node fail to
    /// render rather than merely look odd.
    /// </summary>
    private static SolutionTreeNode TransitiveNode(
        string projectPath, string parent, TransitivePackage package) =>
        new($"transitive:{projectPath}|{parent}|{package.TargetFramework}|{package.Id}",
            SolutionNodeKind.TransitivePackage,
            package.Id,
            package.TargetFramework.Length > 0
                ? $"{package.Version} · {package.TargetFramework}"
                : package.Version,
            ResourceUri: null,
            HasChildren: false,
            ContextValue: SolutionNodeKind.TransitivePackage,
            Dimmed: true);

    private static async Task<SolutionTreeNode[]> GroupChildrenAsync(
        string groupId, CancellationToken ct)
    {
        var parts = groupId.Split('|');
        if (parts.Length < 2)
            return [];

        string kind = parts[0];
        string projectPath = parts[1];

        // A dependency list comes back in MSBuild evaluation order, which is neither the order
        // they were written in nor one anybody can predict; by name is the only useful one.
        static SolutionTreeNode[] Sorted(IEnumerable<SolutionTreeNode> nodes) =>
            [.. nodes.OrderBy(n => n.Label, StringComparer.OrdinalIgnoreCase)];

        if (kind == SolutionNodeKind.Generator)
        {
            return Sorted((await VirtualDocumentHandler.ListGeneratedAsync(projectPath, ct))
                .Select(file => new SolutionTreeNode(
                    Id: $"generated:{file.Uri}",
                    Kind: SolutionNodeKind.GeneratedFile,
                    Label: file.HintName,
                    Description: file.Generator,
                    ResourceUri: file.Uri,
                    HasChildren: false,
                    ContextValue: SolutionNodeKind.GeneratedFile)));
        }

        var evaluation = await ProjectEvaluationService.EvaluateAsync(projectPath, ct);
        if (evaluation is null)
            return [];

        return Sorted(kind switch
        {
            SolutionNodeKind.Imports => evaluation.Imports
                .Select(path => Leaf(SolutionNodeKind.Import, Path.GetFileName(path), path,
                    Path.GetDirectoryName(path)))
                .ToArray(),

            SolutionNodeKind.Packages => evaluation.PackageReferences
                .Select(package => new SolutionTreeNode(
                    $"package:{projectPath}|{package.Id}", SolutionNodeKind.Package,
                    package.Id,
                    // Under Central Package Management the version lives elsewhere, which is
                    // worth saying here rather than showing a bare number with no context.
                    package.IsCentrallyManaged ? $"{package.Version} (central)" : package.Version,
                    ResourceUri: null,
                    HasChildren: ProjectAssetsService
                        .DependenciesOf(projectPath, package.Id, targetFramework: null).Count > 0,
                    ContextValue: SolutionNodeKind.Package, Dimmed: package.IsImplicit))
                .ToArray(),

            SolutionNodeKind.Transitive => ProjectAssetsService
                .TransitiveOnly(projectPath, targetFramework: null)
                .Select(package => TransitiveNode(projectPath, "", package))
                .ToArray(),

            SolutionNodeKind.Projects => evaluation.ProjectReferences
                .Select(path => new SolutionTreeNode(
                    $"project:{path}", SolutionNodeKind.ProjectRef,
                    Path.GetFileNameWithoutExtension(path), null,
                    LspConverters.PathToUri(path), HasChildren: true,
                    ContextValue: SolutionNodeKind.ProjectRef))
                .ToArray(),

            SolutionNodeKind.Assemblies => evaluation.AssemblyReferences
                .Select(name => Leaf(SolutionNodeKind.Assembly, name, null, null))
                .ToArray(),

            SolutionNodeKind.Analyzers => evaluation.Analyzers
                .Select(path => Leaf(SolutionNodeKind.Analyzer, Path.GetFileName(path), path, null))
                .ToArray(),

            _ => Array.Empty<SolutionTreeNode>(),
        });
    }

    private static SolutionTreeNode Leaf(string kind, string label, string? path, string? description) =>
        new(
            Id: $"{kind}:{path ?? label}",
            Kind: kind,
            Label: label,
            Description: description,
            ResourceUri: path is null ? null : LspConverters.PathToUri(path),
            HasChildren: false,
            ContextValue: kind);

    private static async Task<SolutionTreeNode[]> FolderChildrenAsync(
        string folderId, SolutionTreeParams p, CancellationToken ct)
    {
        // folder ids are "<projectPath>|<directory>".
        var parts = folderId.Split('|');
        if (parts.Length != 2)
            return [];

        return await FolderContentsAsync(parts[0], parts[1], p, ct);
    }

    /// <summary>
    /// The project's visible items keyed by path, keeping the entry that says the most when a
    /// file evaluates more than once.
    /// </summary>
    /// <remarks>
    /// One path with several items is ordinary MSBuild, not a broken project: an explicit
    /// <c>&lt;None Include="Fixtures\**\*"&gt;</c> overlaps the SDK's default glob and the file
    /// evaluates twice. Building this with <c>ToDictionary</c> threw on the duplicate, and
    /// because the tree request has no error path of its own the client read the failure as
    /// "this project contains nothing" — an empty node, no message, on every project with an
    /// overlapping include.
    /// </remarks>
    private static Dictionary<string, ProjectItemInfo> ByPath(IReadOnlyList<ProjectItemInfo>? items)
    {
        var map = new Dictionary<string, ProjectItemInfo>(StringComparer.OrdinalIgnoreCase);
        if (items is null)
            return map;

        foreach (var item in items)
        {
            if (!item.Visible)
                continue;

            // DependentUpon is the one piece of metadata the tree acts on, so an item carrying
            // it wins over one that does not; otherwise the first evaluated wins.
            if (map.TryGetValue(item.FullPath, out var existing)
                && (existing.DependentUpon is not null || item.DependentUpon is null))
            {
                continue;
            }

            map[item.FullPath] = item;
        }

        return map;
    }

    /// <summary>
    /// Whether a directory is worth showing in "Project items" mode.
    /// </summary>
    /// <remarks>
    /// Anything the project compiles, obviously — but also a directory with no files under it
    /// at all. A folder created from the tree starts empty, so requiring project content hid it
    /// the moment it was made: the folder appeared to not be created. An empty directory is
    /// something the user just asked for, and hiding it is never what they meant. A directory
    /// that holds only excluded files is a different case and stays hidden.
    /// </remarks>
    private static bool HasProjectContent(
        string directory, Dictionary<string, ProjectItemInfo> projectFiles)
    {
        string prefix = directory + Path.DirectorySeparatorChar;
        if (projectFiles.Keys.Any(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return true;

        try
        {
            return !Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception)
        {
            // Unreadable — treat it as having nothing to show rather than failing the listing.
            return false;
        }
    }

    /// <summary>
    /// The files nested under one file — <c>Default.aspx.cs</c> and
    /// <c>Default.aspx.designer.cs</c> under <c>Default.aspx</c>.
    /// </summary>
    /// <remarks>
    /// Nesting is worked out for a whole directory at once, so this recomputes it for the
    /// file's own directory and takes that file's share. Without it a nested parent drew an
    /// expand arrow that opened onto nothing: the children were counted when the folder was
    /// listed and then never served, because the tree had no case for a file node at all.
    /// </remarks>
    private static async Task<SolutionTreeNode[]> NestedChildrenAsync(
        string filePath, SolutionTreeParams p, CancellationToken ct)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (directory is null || FindOwningProject(directory) is not { } projectPath)
            return [];

        var evaluation = await ProjectEvaluationService.EvaluateAsync(projectPath, ct);
        var projectFiles = ByPath(evaluation?.Items);

        var files = Directory.EnumerateFiles(directory)
            .Where(f => p.ShowAllFiles || projectFiles.ContainsKey(f))
            .Where(f => p.ShowIgnored || !IsHidden(Path.GetFileName(f)))
            .ToList();

        var dependentUpon = projectFiles
            .Where(pair => pair.Value.DependentUpon is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value.DependentUpon!, StringComparer.OrdinalIgnoreCase);

        var parent = FileNestingService.Nest(files, dependentUpon, p.FileNesting)
            .FirstOrDefault(n => string.Equals(
                Path.GetFullPath(n.FullPath), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase));

        return parent is null
            ? []
            : [.. parent.Children.Select(child =>
                FileNode(projectPath, child, projectFiles.ContainsKey(child.FullPath)))];
    }

    /// <summary>The nearest project above a directory.</summary>
    private static string? FindOwningProject(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            string? project = Directory
                .EnumerateFiles(current.FullName, "*.*proj", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => !f.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase));
            if (project is not null)
                return project;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>Directories and nested files directly under one directory of a project.</summary>
    private static async Task<SolutionTreeNode[]> FolderContentsAsync(
        string projectPath, string directory, SolutionTreeParams p, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
            return [];

        var evaluation = await ProjectEvaluationService.EvaluateAsync(projectPath, ct);
        var projectFiles = ByPath(evaluation?.Items);

        var nodes = new List<SolutionTreeNode>();

        foreach (var subdirectory in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(subdirectory);
            if (IsHidden(name) && !p.ShowIgnored)
                continue;
            if (!p.ShowAllFiles && !HasProjectContent(subdirectory, projectFiles))
                continue;

            nodes.Add(new SolutionTreeNode(
                Id: $"folder:{projectPath}|{subdirectory}",
                Kind: SolutionNodeKind.Folder,
                Label: name,
                Description: null,
                ResourceUri: LspConverters.PathToUri(subdirectory),
                HasChildren: true,
                ContextValue: SolutionNodeKind.Folder));
        }

        var files = Directory.EnumerateFiles(directory)
            .Where(f => p.ShowAllFiles || projectFiles.ContainsKey(f))
            .Where(f => p.ShowIgnored || !IsHidden(Path.GetFileName(f)))
            .ToList();

        var dependentUpon = projectFiles
            .Where(pair => pair.Value.DependentUpon is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value.DependentUpon!, StringComparer.OrdinalIgnoreCase);

        foreach (var nested in FileNestingService.Nest(files, dependentUpon, p.FileNesting))
        {
            nodes.Add(FileNode(projectPath, nested, projectFiles.ContainsKey(nested.FullPath)));
        }

        return nodes.ToArray();
    }

    private static SolutionTreeNode FileNode(string projectPath, NestedFile file, bool inProject) =>
        new(
            Id: $"file:{file.FullPath}",
            Kind: SolutionNodeKind.File,
            Label: Path.GetFileName(file.FullPath),
            Description: inProject ? null : "not in project",
            ResourceUri: LspConverters.PathToUri(file.FullPath),
            HasChildren: file.Children.Count > 0,
            ContextValue: SolutionNodeKind.File,
            Dimmed: !inProject);

    private static bool IsHidden(string name) =>
        name is "bin" or "obj" or ".git" or ".vs" or "node_modules" ||
        name.StartsWith('.');

    private static SolutionTreeNode[] Filter(SolutionTreeNode[] nodes, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return nodes;

        return nodes
            .Where(n => n.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(n => n with { Highlights = HighlightsOf(n.Label, filter) })
            .ToArray();
    }

    private static int[][] HighlightsOf(string label, string filter)
    {
        int index = label.IndexOf(filter, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? [] : [[index, index + filter.Length]];
    }
}

using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
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

        var nodes = p.NodeId switch
        {
            null or "" => Roots(solutionPath),
            var id when id.StartsWith("folder:", StringComparison.Ordinal) =>
                await FolderChildrenAsync(id["folder:".Length..], p, ct),
            var id when id.EndsWith(DependenciesSuffix, StringComparison.Ordinal) =>
                await DependencyGroupsAsync(id[..^DependenciesSuffix.Length], ct),
            var id when id.StartsWith("group:", StringComparison.Ordinal) =>
                await GroupChildrenAsync(id["group:".Length..], ct),
            var id when id.StartsWith("project:", StringComparison.Ordinal) =>
                await ProjectChildrenAsync(id["project:".Length..], p, ct),
            var id when id.StartsWith("slnfolder:", StringComparison.Ordinal) =>
                SolutionFolderChildren(solutionPath, id["slnfolder:".Length..]),
            _ => [],
        };

        return Filter(nodes, p.Filter);
    }

    private static SolutionTreeNode[] Roots(string? solutionPath)
    {
        if (solutionPath is null)
            return [];

        var all = SolutionFileService.Read(solutionPath);
        var roots = all.Where(n => n.ParentId is null).ToList();

        // A solution with no folder structure still has projects; fall back to listing them.
        if (roots.Count == 0)
            roots = all.ToList();

        return roots.Select(node => ToNode(node, all)).ToArray();
    }

    private static SolutionTreeNode[] SolutionFolderChildren(string? solutionPath, string folderId)
    {
        if (solutionPath is null)
            return [];

        var all = SolutionFileService.Read(solutionPath);
        var folder = all.FirstOrDefault(n => n.Id == folderId);

        var children = all.Where(n => n.ParentId == folderId).Select(n => ToNode(n, all));
        var files = (folder?.Files ?? []).Select(file => new SolutionTreeNode(
            Id: $"file:{file}",
            Kind: SolutionNodeKind.SolutionItem,
            Label: Path.GetFileName(file),
            Description: null,
            ResourceUri: LspConverters.PathToUri(file),
            HasChildren: false,
            ContextValue: SolutionNodeKind.SolutionItem));

        return children.Concat(files).ToArray();
    }

    private static SolutionTreeNode ToNode(SolutionNode node, IReadOnlyList<SolutionNode> all)
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

        return new SolutionTreeNode(
            Id: $"project:{node.Path}",
            Kind: SolutionNodeKind.Project,
            Label: node.Name,
            Description: null,
            ResourceUri: node.Path is null ? null : LspConverters.PathToUri(node.Path),
            HasChildren: true,
            ContextValue: SolutionNodeKind.Project);
    }

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
        var nodes = new List<SolutionTreeNode>
        {
            new($"{projectPath}{DependenciesSuffix}", SolutionNodeKind.Dependencies,
                "Dependencies", null, null, HasChildren: true, SolutionNodeKind.Dependencies),
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
        Add(SolutionNodeKind.Projects, "Projects", evaluation.ProjectReferences.Count);
        Add(SolutionNodeKind.Assemblies, "Assemblies", evaluation.AssemblyReferences.Count);
        Add(SolutionNodeKind.Analyzers, "Analyzers", evaluation.Analyzers.Count);

        return groups.ToArray();
    }

    private static async Task<SolutionTreeNode[]> GroupChildrenAsync(
        string groupId, CancellationToken ct)
    {
        var parts = groupId.Split('|');
        if (parts.Length < 2)
            return [];

        string kind = parts[0];
        string projectPath = parts[1];

        if (kind == SolutionNodeKind.Generator)
        {
            return (await VirtualDocumentHandler.ListGeneratedAsync(projectPath, ct))
                .Select(file => new SolutionTreeNode(
                    Id: $"generated:{file.Uri}",
                    Kind: SolutionNodeKind.GeneratedFile,
                    Label: file.HintName,
                    Description: file.Generator,
                    ResourceUri: file.Uri,
                    HasChildren: false,
                    ContextValue: SolutionNodeKind.GeneratedFile))
                .ToArray();
        }

        var evaluation = await ProjectEvaluationService.EvaluateAsync(projectPath, ct);
        if (evaluation is null)
            return [];

        return kind switch
        {
            SolutionNodeKind.Imports => evaluation.Imports
                .Select(path => Leaf(SolutionNodeKind.Import, Path.GetFileName(path), path,
                    Path.GetDirectoryName(path)))
                .ToArray(),

            SolutionNodeKind.Packages => evaluation.PackageReferences
                .Select(package => new SolutionTreeNode(
                    $"package:{projectPath}|{package.Id}", SolutionNodeKind.Package,
                    package.Id, package.Version, ResourceUri: null, HasChildren: false,
                    ContextValue: SolutionNodeKind.Package, Dimmed: package.IsImplicit))
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

            _ => [],
        };
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

    /// <summary>Directories and nested files directly under one directory of a project.</summary>
    private static async Task<SolutionTreeNode[]> FolderContentsAsync(
        string projectPath, string directory, SolutionTreeParams p, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
            return [];

        var evaluation = await ProjectEvaluationService.EvaluateAsync(projectPath, ct);
        var projectFiles = evaluation?.Items
            .Where(i => i.Visible)
            .ToDictionary(i => i.FullPath, i => i, StringComparer.OrdinalIgnoreCase)
            ?? [];

        var nodes = new List<SolutionTreeNode>();

        foreach (var subdirectory in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(subdirectory);
            if (IsHidden(name) && !p.ShowIgnored)
                continue;
            if (!p.ShowAllFiles && !projectFiles.Keys.Any(f =>
                    f.StartsWith(subdirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
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

        var dependentUpon = projectFiles.Values
            .Where(i => i.DependentUpon is not null)
            .ToDictionary(i => i.FullPath, i => i.DependentUpon!, StringComparer.OrdinalIgnoreCase);

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

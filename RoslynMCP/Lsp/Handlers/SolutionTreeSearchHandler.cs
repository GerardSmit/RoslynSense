using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>
/// Finding things in the Solution Explorer: a solution-wide search that does not require
/// expanding the tree first, and the ancestor chain needed to reveal a file in it.
/// </summary>
/// <remarks>
/// Both walk the file system per project rather than the evaluated item model, because a search
/// that only finds files someone already expanded is not a search, and evaluating every project
/// to answer a keystroke would stall the tree on a large solution.
/// </remarks>
internal static class SolutionTreeSearchHandler
{
    /// <summary>Enough of a large solution to answer honestly without walking all of it.</summary>
    private const int MaxScannedFiles = 20_000;

    public static Task<SolutionTreeNode[]> SearchAsync(SolutionTreeSearchParams p, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.Query))
            return Task.FromResult<SolutionTreeNode[]>([]);

        // The same resolution the tree itself uses. Asking only for the most recently
        // *loaded* solution meant search found nothing until Roslyn had opened one, so a
        // freshly opened folder showed its projects in the tree and matched none of them.
        string? solutionPath =
            WorkspaceService.BoundSolutionPath ?? WorkspaceService.TryGetMostRecentSolution()?.FilePath;
        if (solutionPath is null)
            return Task.FromResult<SolutionTreeNode[]>([]);

        var matches = new List<(int Score, SolutionTreeNode Node)>();
        int scanned = 0;

        foreach (var node in SolutionFileService.Read(solutionPath))
        {
            ct.ThrowIfCancellationRequested();

            if (node.IsFolder || node.Path is null)
                continue;

            if (Score(Path.GetFileNameWithoutExtension(node.Path), p.Query) is { } projectScore)
            {
                matches.Add((projectScore, new SolutionTreeNode(
                    Id: $"project:{node.Path}",
                    Kind: SolutionNodeKind.Project,
                    Label: node.Name,
                    Description: null,
                    ResourceUri: LspConverters.PathToUri(node.Path),
                    HasChildren: true,
                    ContextValue: SolutionNodeKind.Project,
                    Highlights: HighlightsOf(node.Name, p.Query))));
            }

            string? directory = Path.GetDirectoryName(node.Path);
            if (directory is null || !Directory.Exists(directory))
                continue;

            // The cached walk rather than a fresh enumeration: this runs per keystroke, and the
            // 30-second staleness the index accepts is invisible next to typing a filter.
            foreach (string file in Search.SolutionFileIndex.FilesUnder(directory, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (IsIgnored(file))
                    continue;
                if (++scanned > MaxScannedFiles)
                    break;

                string name = Path.GetFileName(file);
                if (Score(name, p.Query) is not { } score)
                    continue;

                matches.Add((score, new SolutionTreeNode(
                    Id: $"file:{file}",
                    Kind: SolutionNodeKind.File,
                    Label: name,
                    Description: Path.GetRelativePath(directory, file) is var relative && relative != name
                        ? Path.GetDirectoryName(relative)
                        : node.Name,
                    ResourceUri: LspConverters.PathToUri(file),
                    HasChildren: false,
                    ContextValue: SolutionNodeKind.File,
                    Highlights: HighlightsOf(name, p.Query))));
            }

            if (scanned > MaxScannedFiles)
                break;
        }

        var results = matches
            .OrderBy(m => m.Score)
            .ThenBy(m => m.Node.Label, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, p.Limit))
            .Select(m => m.Node)
            .ToArray();

        return Task.FromResult(results);
    }

    /// <summary>
    /// The chain of node ids from the solution root down to a file, so the client can expand each
    /// ancestor before revealing it.
    /// </summary>
    public static Task<SolutionTreeRevealResult> RevealAsync(
        SolutionTreeRevealParams p, CancellationToken ct)
    {
        string? path = LspConverters.UriToPath(p.Uri);
        string? solutionPath =
            WorkspaceService.BoundSolutionPath ?? WorkspaceService.TryGetMostRecentSolution()?.FilePath;
        if (path is null || solutionPath is null)
            return Task.FromResult(new SolutionTreeRevealResult([]));

        var all = SolutionFileService.Read(solutionPath);

        var owner = all
            .Where(n => !n.IsFolder && n.Path is not null)
            .Where(n => IsUnder(path, Path.GetDirectoryName(n.Path!)))
            // The most deeply nested project wins, so a file in a nested project is not claimed
            // by an ancestor project that merely shares the directory prefix.
            .OrderByDescending(n => n.Path!.Length)
            .FirstOrDefault();

        if (owner?.Path is null)
            return Task.FromResult(new SolutionTreeRevealResult([]));

        // The tree is rooted at the solution, so every chain starts there.
        var chain = new List<string> { $"solution:{solutionPath}" };

        // Solution folders above the project, outermost first.
        var ancestors = new List<string>();
        for (var folder = all.FirstOrDefault(n => n.Id == owner.ParentId);
             folder is not null;
             folder = all.FirstOrDefault(n => n.Id == folder.ParentId))
        {
            ancestors.Insert(0, $"slnfolder:{folder.Id}");
        }
        chain.AddRange(ancestors);
        chain.Add($"project:{owner.Path}");

        // Then each directory between the project and the file.
        string projectDirectory = Path.GetDirectoryName(owner.Path)!;
        string? current = Path.GetDirectoryName(Path.GetFullPath(path));
        var directories = new List<string>();
        while (current is not null &&
               !current.Equals(projectDirectory, StringComparison.OrdinalIgnoreCase) &&
               IsUnder(current, projectDirectory))
        {
            directories.Insert(0, $"folder:{owner.Path}|{current}");
            current = Path.GetDirectoryName(current);
        }
        chain.AddRange(directories);

        // And then whichever files this one is nested under, since those are the rows the folder
        // actually listed.
        foreach (string ancestor in
                 SolutionTreeHandler.NestingAncestorsOf(owner.Path, path, p.FileNesting))
        {
            chain.Add($"file:{Path.GetFullPath(ancestor)}");
        }

        chain.Add($"file:{Path.GetFullPath(path)}");

        return Task.FromResult(new SolutionTreeRevealResult(chain.ToArray()));
    }

    /// <summary>
    /// Lower is better: a prefix match beats a word-start match beats a substring, so typing
    /// "ord" puts Order.cs above ReorderTests.cs.
    /// </summary>
    private static int? Score(string candidate, string query)
    {
        int index = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;
        if (index == 0)
            return 0;

        bool wordStart = !char.IsLetterOrDigit(candidate[index - 1]) ||
                         char.IsUpper(candidate[index]) && !char.IsUpper(candidate[index - 1]);
        return wordStart ? 1 : 2;
    }

    private static int[][]? HighlightsOf(string label, string query)
    {
        int index = label.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : [[index, index + query.Length]];
    }

    private static bool IsUnder(string path, string? directory) =>
        directory is not null &&
        Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnored(string path)
    {
        foreach (var segment in Path.GetRelativePath(Path.GetPathRoot(path) ?? "", path)
                     .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "bin" or "obj" or ".git" or ".vs" or "node_modules" ||
                (segment.StartsWith('.') && segment.Length > 1))
            {
                return true;
            }
        }
        return false;
    }
}

namespace RoslynMCP.Services.ProjectModel;

/// <summary>
/// Every project in the current solution, whether or not Roslyn has loaded it.
/// </summary>
/// <remarks>
/// <para>
/// "The projects in this solution" has two answers and they differ for a long time after a
/// folder is opened. <see cref="WorkspaceService.TryGetSessionSolution"/> knows what Roslyn
/// has actually loaded — nothing, until a document is opened, and never a legacy project that
/// needs the out-of-process build host. The <c>.sln</c> on disk knows the rest immediately.
/// </para>
/// <para>
/// Asking only the first produced the same failure in four places: the Solution Explorer's
/// search found nothing, reveal did nothing, the Test Explorer stayed empty, and launching a
/// WebForms project reported it was "not a project in the loaded solution". They are one
/// question, so they get one answer here.
/// </para>
/// </remarks>
public static class SolutionProjectIndex
{
    /// <summary>Project file paths, solution file first and loaded projects folded in.</summary>
    public static IReadOnlyList<string> ProjectPaths()
    {
        var paths = new List<string>();

        if (WorkspaceService.BoundSolutionPath is { Length: > 0 } solutionPath)
        {
            paths.AddRange(SolutionFileService.Read(solutionPath)
                .Where(node => !node.IsFolder && node.Path is { Length: > 0 })
                .Select(node => node.Path!));
        }

        // A project loaded without a solution — someone opened a single file — is not in any
        // .sln, so the loaded set is a supplement rather than a fallback.
        foreach (var project in WorkspaceService.TryGetSessionSolution()?.Projects ?? [])
        {
            if (project.FilePath is { Length: > 0 } path)
                paths.Add(path);
        }

        return [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The project a file belongs to, or <c>null</c> when it belongs to none.
    /// </summary>
    /// <remarks>
    /// Roslyn answers first, because it knows about linked files and generated ones that live
    /// nowhere near the project that compiles them. Containment is the fallback, and it takes the
    /// longest matching directory so a project nested inside another is not lost to its parent.
    /// It also covers what Roslyn never sees: .aspx, .config, and anything in a project the
    /// workspace has not loaded.
    /// </remarks>
    public static string? ProjectForFile(string filePath)
    {
        if (filePath.Length == 0)
            return null;

        string normalized = PathHelper.NormalizePath(filePath);

        foreach (var project in WorkspaceService.TryGetSessionSolution()?.Projects ?? [])
        {
            if (project.FilePath is not { Length: > 0 } projectPath)
                continue;

            foreach (var document in project.Documents)
            {
                if (document.FilePath is { Length: > 0 } documentPath &&
                    string.Equals(
                        PathHelper.NormalizePath(documentPath), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return projectPath;
                }
            }
        }

        string? best = null;
        foreach (string projectPath in ProjectPaths())
        {
            string? directory = System.IO.Path.GetDirectoryName(projectPath);
            if (directory is not { Length: > 0 })
                continue;

            string prefix = PathHelper.NormalizePath(directory);
            if (!normalized.StartsWith(prefix + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;

            if (best is null || prefix.Length > PathHelper.NormalizePath(
                    System.IO.Path.GetDirectoryName(best) ?? "").Length)
            {
                best = projectPath;
            }
        }

        return best;
    }

    /// <summary>The same set, paired with the name each project should be shown under.</summary>
    public static IReadOnlyList<(string Path, string Name)> Projects() =>
        [.. ProjectPaths()
            .Select(path => (path, System.IO.Path.GetFileNameWithoutExtension(path)))
            .OrderBy(p => p.Item2, StringComparer.OrdinalIgnoreCase)];
}

using RoslynMCP.Services.ProjectModel;

namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>
/// Paths for the item specs that name a file — <c>ProjectReference</c>, <c>Compile</c>,
/// <c>Content</c> and the rest.
/// </summary>
/// <remarks>
/// One directory level per request, resolved against the project's own directory the way MSBuild
/// resolves an item spec. Listing the whole tree would be both slow and useless: a path is typed a
/// segment at a time, and each separator re-triggers completion for the next level down.
/// </remarks>
internal static class MsBuildPathCompletion
{
    /// <summary>Directories that are build output or tooling, never something to reference.</summary>
    private static readonly string[] Skipped = ["bin", "obj", "node_modules", ".git", ".vs", ".idea"];

    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    public static IReadOnlyList<MsBuildValue> For(MsBuildDocument document, MsBuildContext context)
    {
        if (Path.GetDirectoryName(document.FilePath) is not { Length: > 0 } root)
            return [];

        string typed = context.Attribute?.Value is { } value ? XmlSpans.Decode(value) : string.Empty;

        // Everything before the last separator is the directory being listed; what follows is the
        // prefix the client filters on, and is not ours to filter again.
        int separator = typed.LastIndexOfAny(['/', '\\']);
        string prefix = separator >= 0 ? typed[..(separator + 1)] : string.Empty;

        string directory = Combine(root, prefix);
        if (directory.Length == 0 || !Directory.Exists(directory))
            return [];

        bool projectsOnly = context.ElementName.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase);
        var results = new List<MsBuildValue>();

        try
        {
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                string name = Path.GetFileName(child);
                if (Skipped.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;

                // The trailing separator both marks it as a directory and re-triggers completion
                // for the next level, so a path can be walked without retyping the trigger.
                results.Add(new MsBuildValue(prefix + name + "\\", "folder"));
            }

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                string name = Path.GetFileName(file);

                if (projectsOnly
                    && !ProjectExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(new MsBuildValue(Escape(prefix + name), Detail(file, projectsOnly)));
            }
        }
        catch (IOException)
        {
            return results;
        }
        catch (UnauthorizedAccessException)
        {
            return results;
        }

        return results;
    }

    /// <summary>
    /// Whether this project is one the solution already knows about.
    /// </summary>
    /// <remarks>
    /// Worth saying, because a <c>ProjectReference</c> to a project outside the solution builds
    /// locally and then fails for everyone else — it is the reference most likely to be a mistake,
    /// and the only signal available at the moment it is typed.
    /// </remarks>
    private static string? Detail(string file, bool projectsOnly)
    {
        if (!projectsOnly)
            return null;

        return SolutionProjectIndex.ProjectPaths()
            .Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
            ? "in this solution"
            : "not in this solution";
    }

    /// <summary>
    /// Escapes the two characters an item spec reads as syntax.
    /// </summary>
    /// <remarks>
    /// A semicolon separates item specs, so an unescaped one silently turns one file into two
    /// items — neither of which exists. A percent sign introduces an escape, so it has to escape
    /// itself first or the character after it is eaten.
    /// </remarks>
    private static string Escape(string path) =>
        path.Replace("%", "%25", StringComparison.Ordinal)
            .Replace(";", "%3B", StringComparison.Ordinal);

    private static string Combine(string root, string prefix)
    {
        if (prefix.Length == 0)
            return root;

        try
        {
            string combined = Path.GetFullPath(Path.Combine(root, prefix));

            // A prefix of `..\..\..\` is legitimate — sibling projects live above the project
            // directory — so this is not a containment check, only a well-formedness one.
            return combined;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (PathTooLongException)
        {
            return string.Empty;
        }
    }
}

using System.Collections.Concurrent;

namespace RoslynMCP.Languages.MsBuild.Core;

/// <summary>Which of the pack's file types a path is.</summary>
/// <remarks>
/// The pack owns five grammars that happen to share XML, and almost every provider has to branch on
/// which one it is looking at: <c>LangVersion</c> means nothing in a <c>packages.config</c>, and a
/// <c>&lt;PackageVersion&gt;</c> outside a props file is not central package management. Classified
/// once at the top of each request rather than re-derived from the extension in a dozen places.
/// </remarks>
internal enum MsBuildFileKind
{
    /// <summary>Not one of ours.</summary>
    None,

    /// <summary>A <c>.csproj</c>, <c>.fsproj</c> or <c>.vbproj</c>.</summary>
    Project,

    /// <summary>A <c>.props</c> — including <c>Directory.Packages.props</c>.</summary>
    Properties,

    /// <summary>A <c>.targets</c>.</summary>
    Targets,

    /// <summary>A <c>packages.config</c>, from before <c>PackageReference</c>.</summary>
    PackagesConfig,

    /// <summary>A <c>nuget.config</c>, in any of the casings that occur on disk.</summary>
    NuGetConfig,
}

/// <summary>
/// The language a project file builds.
/// </summary>
/// <remarks>
/// Completion has to know. <c>LangVersion</c> is generated from Roslyn's C# <c>LanguageVersion</c>,
/// whose values are wrong for a <c>.vbproj</c> and meaningless in an <c>.fsproj</c> — offering them
/// there is worse than offering nothing, because they look authoritative.
/// </remarks>
internal enum MsBuildFlavour
{
    /// <summary>A file that does not name a language: a props, targets or config file.</summary>
    None,
    CSharp,
    FSharp,
    VisualBasic,
}

internal static class MsBuildFile
{
    /// <summary>Every extension the pack claims, for <c>ILanguagePack.FileExtensions</c>.</summary>
    public static readonly string[] Extensions =
        [".csproj", ".fsproj", ".vbproj", ".props", ".targets"];

    /// <summary>
    /// The whole file names the pack claims, matched ahead of the extensions.
    /// </summary>
    /// <remarks>
    /// Named rather than claiming <c>.config</c>, which would also take <c>web.config</c> and
    /// <c>app.config</c> — those belong to the webconfig pack, with
    /// <c>BindingRedirectHandler</c> answering beside it about the same file. Matching is
    /// case-insensitive, which matters here: NuGet itself writes <c>NuGet.Config</c>, the CLI
    /// writes <c>nuget.config</c>, and both occur in the same tree.
    /// </remarks>
    public static readonly string[] Names = ["packages.config", "nuget.config"];

    public static MsBuildFileKind KindOf(string? filePath)
    {
        if (filePath is not { Length: > 0 })
            return MsBuildFileKind.None;

        string name = Path.GetFileName(filePath);

        if (name.Equals("packages.config", StringComparison.OrdinalIgnoreCase))
            return MsBuildFileKind.PackagesConfig;

        if (name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase))
            return MsBuildFileKind.NuGetConfig;

        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".csproj" or ".fsproj" or ".vbproj" => MsBuildFileKind.Project,
            ".props" => MsBuildFileKind.Properties,
            ".targets" => MsBuildFileKind.Targets,
            _ => MsBuildFileKind.None,
        };
    }

    public static MsBuildFlavour FlavourOf(string? filePath)
    {
        var named = Path.GetExtension(filePath ?? string.Empty).ToLowerInvariant() switch
        {
            ".csproj" => MsBuildFlavour.CSharp,
            ".fsproj" => MsBuildFlavour.FSharp,
            ".vbproj" => MsBuildFlavour.VisualBasic,
            _ => MsBuildFlavour.None,
        };

        // A props or targets file names no language, but it sets the same properties for the
        // projects it governs — a Directory.Build.props is where LangVersion belongs, more so than
        // any one project file. So the neighbours are asked instead.
        return named is MsBuildFlavour.None && KindOf(filePath) is MsBuildFileKind.Properties or MsBuildFileKind.Targets
            ? NeighbourFlavour(filePath!)
            : named;
    }

    private static readonly ConcurrentDictionary<string, MsBuildFlavour> s_neighbours =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How far below the file projects are looked for.</summary>
    /// <remarks>
    /// A repository-root <c>Directory.Build.props</c> has its projects a level or two down —
    /// <c>src/Api/Api.csproj</c> — and past that the walk costs more than the answer is worth. The
    /// caps are what keep this off the critical path of a keystroke on a large tree.
    /// </remarks>
    private const int MaxDepth = 3;

    /// <inheritdoc cref="MaxDepth"/>
    private const int MaxDirectories = 400;

    /// <summary>
    /// The language of the projects a props or targets file sits above.
    /// </summary>
    /// <remarks>
    /// Unanimity or nothing. A tree of C# projects makes <c>LangVersion</c>'s C# values the right
    /// list, but one that mixes C# and Visual Basic has no single right list, and the flavour gate
    /// exists precisely so a value that looks authoritative is never offered where it does not
    /// apply. Memoized per file, because this walks the disk and completion runs on a keystroke.
    /// </remarks>
    private static MsBuildFlavour NeighbourFlavour(string filePath) =>
        s_neighbours.GetOrAdd(filePath, static path =>
        {
            string? root = Path.GetDirectoryName(path);
            if (root is not { Length: > 0 })
                return MsBuildFlavour.None;

            var found = MsBuildFlavour.None;
            var queue = new Queue<(string Directory, int Depth)>();
            queue.Enqueue((root, 0));

            for (int visited = 0; queue.Count > 0 && visited < MaxDirectories; visited++)
            {
                var (directory, depth) = queue.Dequeue();

                try
                {
                    foreach (string file in Directory.EnumerateFiles(directory))
                    {
                        var flavour = Path.GetExtension(file).ToLowerInvariant() switch
                        {
                            ".csproj" => MsBuildFlavour.CSharp,
                            ".fsproj" => MsBuildFlavour.FSharp,
                            ".vbproj" => MsBuildFlavour.VisualBasic,
                            _ => MsBuildFlavour.None,
                        };

                        if (flavour is MsBuildFlavour.None)
                            continue;

                        if (found is not MsBuildFlavour.None && found != flavour)
                            return MsBuildFlavour.None;

                        found = flavour;
                    }

                    if (depth >= MaxDepth)
                        continue;

                    foreach (string child in Directory.EnumerateDirectories(directory))
                    {
                        if (!Skipped(Path.GetFileName(child)))
                            queue.Enqueue((child, depth + 1));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A directory we cannot read is one this answer does without.
                }
            }

            return found;
        });

    /// <summary>Directories with no project files in them, and thousands of everything else.</summary>
    private static bool Skipped(string name) =>
        name is "bin" or "obj" or "node_modules" or "packages" or ".git" or ".vs" or ".idea"
        || name.StartsWith('.');

    internal static void ClearFlavourCache() => s_neighbours.Clear();

    /// <summary>Whether this file carries an MSBuild project, as opposed to NuGet's own XML.</summary>
    public static bool IsMsBuild(MsBuildFileKind kind) =>
        kind is MsBuildFileKind.Project or MsBuildFileKind.Properties or MsBuildFileKind.Targets;
}

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
    /// <c>app.config</c> — those are <c>BindingRedirectHandler</c>'s, and it answers ahead of pack
    /// dispatch. Matching is case-insensitive, which matters here: NuGet itself writes
    /// <c>NuGet.Config</c>, the CLI writes <c>nuget.config</c>, and both occur in the same tree.
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

    public static MsBuildFlavour FlavourOf(string? filePath) =>
        Path.GetExtension(filePath ?? string.Empty).ToLowerInvariant() switch
        {
            ".csproj" => MsBuildFlavour.CSharp,
            ".fsproj" => MsBuildFlavour.FSharp,
            ".vbproj" => MsBuildFlavour.VisualBasic,
            _ => MsBuildFlavour.None,
        };

    /// <summary>Whether this file carries an MSBuild project, as opposed to NuGet's own XML.</summary>
    public static bool IsMsBuild(MsBuildFileKind kind) =>
        kind is MsBuildFileKind.Project or MsBuildFileKind.Properties or MsBuildFileKind.Targets;
}

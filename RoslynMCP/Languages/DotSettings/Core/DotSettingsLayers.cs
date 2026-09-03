using System.Collections.Immutable;
using RoslynMCP.Services;

namespace RoslynMCP.Languages.DotSettings.Core;

/// <summary>
/// The stack of <c>.DotSettings</c> files that applies to one project, weakest first.
/// </summary>
/// <remarks>
/// <para>
/// ReSharper resolves a setting through named layers in priority order, and the two that travel
/// with a repository are the ones that matter here: <c>&lt;Solution&gt;.sln.DotSettings</c>, which
/// teams commit, and <c>&lt;Solution&gt;.sln.DotSettings.user</c>, which they gitignore. The same
/// pair exists per project as <c>&lt;Project&gt;.csproj.DotSettings</c>, and in practice that is
/// where the folder and exclusion rules actually live — 185 of the 222 files in a survey of real
/// solutions were project-level.
/// </para>
/// <para>
/// The machine-wide layer (<c>GlobalSettingsStorage.DotSettings</c> under <c>%APPDATA%</c>) is
/// deliberately not read. It is one developer's IDE state — fonts, licence prompts, MRU lists —
/// and letting it reach the answers a language server gives would make those answers differ
/// between two people looking at the same commit, which is the one thing a shared analysis must
/// not do.
/// </para>
/// </remarks>
internal static class DotSettingsLayers
{
    /// <summary>
    /// The layer files for a project, weakest first: solution team-shared, solution personal,
    /// project team-shared, project personal. Only the ones that exist are returned.
    /// </summary>
    public static ImmutableArray<string> For(string projectPath)
    {
        var layers = ImmutableArray.CreateBuilder<string>(4);

        if (PathHelper.FindNearestSolution(projectPath) is { Length: > 0 } solution)
            AddPair(layers, solution);

        AddPair(layers, projectPath);

        return layers.ToImmutable();
    }

    /// <summary>The layer files hanging off one solution, weakest first.</summary>
    public static ImmutableArray<string> ForSolution(string solutionPath)
    {
        var layers = ImmutableArray.CreateBuilder<string>(2);
        AddPair(layers, solutionPath);
        return layers.ToImmutable();
    }

    /// <summary>
    /// Whether a path is a settings layer. Note the ordering: <c>.DotSettings.user</c> has to be
    /// tested before <c>.DotSettings</c>, since the personal layer ends with both.
    /// </summary>
    public static bool IsLayerPath(string path) =>
        path.EndsWith(".DotSettings.user", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".DotSettings", StringComparison.OrdinalIgnoreCase);

    /// <summary>The team-shared layer beside a file, then the personal one over it.</summary>
    private static void AddPair(ImmutableArray<string>.Builder layers, string owner)
    {
        string shared = owner + ".DotSettings";

        if (File.Exists(shared))
            layers.Add(shared);

        string personal = shared + ".user";

        if (File.Exists(personal))
            layers.Add(personal);
    }
}

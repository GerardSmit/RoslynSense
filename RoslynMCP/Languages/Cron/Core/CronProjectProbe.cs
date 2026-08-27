using System.Collections.Concurrent;

namespace RoslynMCP.Languages.Cron.Core;

/// <summary>
/// Which projects reference a scheduler, answered from the project file alone.
/// </summary>
/// <remarks>
/// <para>
/// The Solution Explorer promises that drawing the solution's root evaluates no project, and the
/// scheduled-jobs section is drawn there — so the question "does this solution schedule anything"
/// has to be answered without MSBuild, without a restore and without a workspace. A text scan of
/// the manifests is what is left, and it is enough: a <c>PackageReference</c> to Hangfire is a
/// line with the word Hangfire in it, whatever else the project does.
/// </para>
/// <para>
/// Cached on the file's timestamp, like <see cref="Services.ProjectClassifier"/>: the root is
/// redrawn on every refresh and the answer changes only when somebody edits a project file.
/// </para>
/// </remarks>
internal static class CronProjectProbe
{
    /// <summary>
    /// The manifests worth reading beside the project file itself, in the order a project can
    /// carry them.
    /// </summary>
    /// <remarks>
    /// <c>packages.config</c> for a .NET Framework project, where the <c>.csproj</c> names an
    /// assembly path rather than a package — and <c>Directory.Packages.props</c> for central
    /// package management, where the version lives outside the project and, in some solutions, the
    /// package reference does too.
    /// </remarks>
    private static readonly string[] Siblings = ["packages.config", "Directory.Packages.props"];

    /// <summary>The words that mean a scheduler, matched case-insensitively.</summary>
    /// <remarks>
    /// Bare product names rather than exact package ids, because the ecosystem is a family:
    /// Hangfire.Core, Hangfire.AspNetCore, Hangfire.SqlServer, Quartz.Extensions.Hosting. Matching
    /// the family is what a probe is for — the exact question is settled later, by the compilation,
    /// and being generous here only costs a project row that expands to nothing.
    /// </remarks>
    private static readonly string[] Schedulers = ["Hangfire", "Quartz"];

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, bool Value)> s_cache = new();

    /// <summary>Whether this project's manifests mention a scheduling library.</summary>
    public static bool Schedules(string projectPath)
    {
        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(projectPath);
        }
        catch
        {
            stamp = DateTime.MinValue;
        }

        if (s_cache.TryGetValue(projectPath, out var cached) && cached.Stamp == stamp)
            return cached.Value;

        bool schedules = Scan(projectPath);
        s_cache[projectPath] = (stamp, schedules);
        return schedules;
    }

    private static bool Scan(string projectPath)
    {
        if (Mentions(projectPath))
            return true;

        string? directory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(directory))
            return false;

        foreach (string sibling in Siblings)
        {
            if (Mentions(Path.Combine(directory, sibling)))
                return true;
        }

        return false;
    }

    private static bool Mentions(string path)
    {
        string text;
        try
        {
            // The whole file rather than a line-by-line read: a project file is a few kilobytes,
            // and an unreadable one is a project with nothing to show rather than an error worth
            // reporting — the tree is being drawn.
            text = File.ReadAllText(path);
        }
        catch
        {
            return false;
        }

        foreach (string scheduler in Schedulers)
        {
            if (text.Contains(scheduler, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

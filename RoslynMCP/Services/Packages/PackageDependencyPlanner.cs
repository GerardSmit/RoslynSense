using NuGet.Versioning;

namespace RoslynMCP.Services.Packages;

/// <summary>How far an update is allowed to reach beyond the packages that were selected.</summary>
public enum DependencyUpdateMode
{
    /// <summary>Only what was asked for. Restore reports the fallout.</summary>
    SelectedOnly,

    /// <summary>Bump conflicting references to the lowest version that satisfies the requirement.</summary>
    Minimal,

    /// <summary>Bump conflicting references to the newest version the lock allows.</summary>
    Latest,
}

/// <param name="RequiredBy">The package whose new version asks for this one.</param>
public sealed record InducedUpdate(
    string Id,
    string CurrentVersion,
    string Version,
    string ProjectPath,
    string ProjectName,
    string RequiredBy,
    string RequiredByVersion);

/// <summary>
/// The updates a selected update drags along with it.
/// </summary>
/// <remarks>
/// <para>
/// Updating A to a version that wants B ≥ 9 while the project holds a direct reference to B 8 is
/// not a warning: the direct reference wins the resolution, so restore fails outright with NU1605.
/// Nothing before this planned for it, which meant the failure arrived after every project file had
/// already been written.
/// </para>
/// <para>
/// Only direct references are considered. A transitive dependency at too low a version is lifted by
/// restore on its own, and writing it into the project file would turn an implementation detail of
/// today's graph into a reference the project maintains forever.
/// </para>
/// <para>
/// The result is a proposal. It is shown before anything is written, because a package the user did
/// not tick is not one this can quietly edit on their behalf.
/// </para>
/// </remarks>
public static class PackageDependencyPlanner
{
    /// <summary>
    /// A ceiling on graph walking, not a tuning knob. A real conflict resolves in a handful of
    /// steps; a number this large means the walk found a cycle the version comparison failed to
    /// break, and stopping beats hanging the panel.
    /// </summary>
    private const int MaxNodes = 250;

    public static async Task<IReadOnlyList<InducedUpdate>> PlanAsync(
        IReadOnlyList<PackageUpdateRequest> requests,
        DependencyUpdateMode mode,
        UpdateQuery query,
        CancellationToken ct)
    {
        if (mode == DependencyUpdateMode.SelectedOnly || requests.Count == 0)
            return [];

        var projects = (await NuGetService.InstalledAsync(ct))
            .ToDictionary(project => project.ProjectPath, StringComparer.OrdinalIgnoreCase);

        var frameworks = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var induced = new Dictionary<string, InducedUpdate>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // What each reference will be on once this batch is applied — the selected versions to
        // begin with, then whatever the walk adds. Comparing against the *current* version instead
        // would re-report a requirement that an earlier step already satisfied.
        var pending = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string ProjectPath, string Id, NuGetVersion Version)>();

        foreach (var request in requests)
        {
            if (!NuGetVersion.TryParse(request.Version, out var version))
                continue;

            foreach (string projectPath in request.ProjectPaths)
            {
                pending[Key(projectPath, request.Id)] = version;
                queue.Enqueue((projectPath, request.Id, version));
            }
        }

        int nodes = 0;

        while (queue.Count > 0 && nodes++ < MaxNodes)
        {
            ct.ThrowIfCancellationRequested();
            var (projectPath, id, version) = queue.Dequeue();

            if (!projects.TryGetValue(projectPath, out var project))
                continue;

            if (!visited.Add($"{Key(projectPath, id)}|{version.ToNormalizedString()}"))
                continue;

            if (!frameworks.TryGetValue(projectPath, out var projectFrameworks))
            {
                projectFrameworks = await PackageFrameworkService.FrameworksOfAsync(projectPath, ct);
                frameworks[projectPath] = projectFrameworks;
            }

            var dependencies = await PackageFrameworkService.DependenciesForAsync(
                id, version.ToNormalizedString(), projectFrameworks, ct);

            foreach (var dependency in dependencies)
            {
                var reference = project.Packages.FirstOrDefault(
                    p => p.Id.Equals(dependency.Id, StringComparison.OrdinalIgnoreCase));

                if (reference is null || PackageUpdateService.Current(reference.Version) is not { } current)
                    continue;

                if (!VersionRange.TryParse(dependency.VersionRange, out var range) ||
                    range.MinVersion is not { } required)
                {
                    continue;
                }

                string key = Key(projectPath, dependency.Id);
                var effective = pending.GetValueOrDefault(key) ?? current;
                if (effective >= required)
                    continue;

                var target = mode == DependencyUpdateMode.Minimal
                    ? required
                    : await LatestAsync(reference, current, required, projectFrameworks, query, ct);

                pending[key] = target;
                induced[key] = new InducedUpdate(
                    reference.Id,
                    reference.Version,
                    target.ToNormalizedString(),
                    projectPath,
                    project.ProjectName,
                    id,
                    version.ToNormalizedString());

                queue.Enqueue((projectPath, reference.Id, target));
            }
        }

        return induced.Values
            .OrderBy(update => update.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(update => update.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The newest version the lock allows, never below what the dependency actually requires.
    /// </summary>
    /// <remarks>
    /// The floor matters: "same minor only" against a requirement of 9.0.0 has no candidate at all,
    /// and honouring the lock there would produce a plan that cannot restore. The requirement is
    /// the point of the exercise; the lock only decides how far past it to go.
    /// </remarks>
    private static async Task<NuGetVersion> LatestAsync(
        PackageSummary reference,
        NuGetVersion current,
        NuGetVersion required,
        IReadOnlyList<string> projectFrameworks,
        UpdateQuery query,
        CancellationToken ct)
    {
        var lookup = await PackageUpdateService.VersionsAsync(reference.Id, query, ct);
        if (lookup.Results.Count == 0)
            return required;

        int? cap = query.Lock == VersionLock.Framework &&
            FrameworkVersionPolicy.TracksPlatformVersion(reference.Id)
                ? FrameworkVersionPolicy.PlatformMajor(projectFrameworks)
                : null;

        var resolved = PackageUpdateService.Resolve(
            current, reference.Version, lookup.Results, query, cap);

        return resolved is { } best && best > required ? best : required;
    }

    private static string Key(string projectPath, string packageId) => $"{projectPath}|{packageId}";
}

using System.Collections.Concurrent;
using System.Text.Json;

namespace RoslynMCP.Services.Packages;

/// <param name="Version">The version restore actually resolved, not the range something asked for.</param>
public sealed record TransitivePackage(
    string Id,
    string Version,
    string TargetFramework,
    IReadOnlyList<string> Dependencies);

public sealed record TransitiveGraph(
    string ProjectPath,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<TransitivePackage> Packages,
    IReadOnlyList<string> RootIds);

/// <summary>
/// The resolved dependency graph, read straight out of <c>obj/project.assets.json</c>.
/// </summary>
/// <remarks>
/// No restore is triggered: the file is whatever the last one produced, which is exactly what the
/// build is currently using. Two things it gets right that a naive read does not — every target
/// framework is parsed rather than the first one, and a dependency's version is taken from the
/// entry restore created for it rather than from the range its consumer requested. Those differ
/// constantly: a package asking for 13.0.1 in a solution that resolved 13.0.3 gets 13.0.3.
/// </remarks>
public static class ProjectAssetsService
{
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, TransitiveGraph Graph)> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Invalidate() => s_cache.Clear();

    public static TransitiveGraph Read(string projectPath)
    {
        string assets = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? "", "obj", "project.assets.json");

        if (!File.Exists(assets))
            return new TransitiveGraph(projectPath, [], [], []);

        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(assets); }
        catch { return new TransitiveGraph(projectPath, [], [], []); }

        // The file runs to megabytes on a large solution, so it must not be reparsed every time
        // a tree node is expanded.
        if (s_cache.TryGetValue(assets, out var cached) && cached.Stamp == stamp)
            return cached.Graph;

        var graph = Parse(projectPath, assets);
        s_cache[assets] = (stamp, graph);
        return graph;
    }

    /// <summary>Resolved packages that nothing in the project references directly.</summary>
    public static IReadOnlyList<TransitivePackage> TransitiveOnly(string projectPath, string? targetFramework)
    {
        var graph = Read(projectPath);
        var roots = graph.RootIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return graph.Packages
            .Where(p => !roots.Contains(p.Id))
            .Where(p => targetFramework is not { Length: > 0 } ||
                        p.TargetFramework.Equals(targetFramework, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(p => (p.Id, p.TargetFramework))
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>What one package pulled in, for expanding it in the tree.</summary>
    public static IReadOnlyList<TransitivePackage> DependenciesOf(
        string projectPath, string packageId, string? targetFramework)
    {
        var graph = Read(projectPath);

        var parent = graph.Packages.FirstOrDefault(p =>
            p.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase) &&
            (targetFramework is not { Length: > 0 } ||
             p.TargetFramework.Equals(targetFramework, StringComparison.OrdinalIgnoreCase)));

        if (parent is null)
            return [];

        // A project with a RuntimeIdentifier gets one target per framework/RID pair, and both fold
        // to the same moniker — so the same package legitimately appears more than once here.
        var byId = graph.Packages
            .Where(p => p.TargetFramework.Equals(parent.TargetFramework, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        return parent.Dependencies
            .Select(id => byId.GetValueOrDefault(id))
            .Where(p => p is not null)
            .Select(p => p!)
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static TransitiveGraph Parse(string projectPath, string assetsPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(assetsPath));
            var root = document.RootElement;

            var packages = new List<TransitivePackage>();
            var frameworks = new List<string>();

            if (root.TryGetProperty("targets", out var targets) &&
                targets.ValueKind == JsonValueKind.Object)
            {
                foreach (var target in targets.EnumerateObject())
                {
                    // A target is keyed by framework, optionally with a runtime identifier after
                    // a slash. Both belong to the same framework for a dependency listing.
                    string moniker = target.Name.Split('/')[0];
                    frameworks.Add(moniker);

                    foreach (var entry in target.Value.EnumerateObject())
                    {
                        var parts = entry.Name.Split('/');
                        if (parts.Length != 2)
                            continue;

                        // Project references appear in targets too; they are not packages.
                        if (entry.Value.TryGetProperty("type", out var type) &&
                            type.GetString() is "project")
                        {
                            continue;
                        }

                        var dependencies = entry.Value.TryGetProperty("dependencies", out var deps) &&
                                           deps.ValueKind == JsonValueKind.Object
                            ? deps.EnumerateObject().Select(d => d.Name).ToList()
                            : [];

                        packages.Add(new TransitivePackage(parts[0], parts[1], moniker, dependencies));
                    }
                }
            }

            return new TransitiveGraph(
                projectPath,
                frameworks.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                packages,
                DirectDependencies(root));
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not read '{Path.GetFileName(assetsPath)}': {ex.Message}",
                key: $"assets:{assetsPath}");
            return new TransitiveGraph(projectPath, [], [], []);
        }
    }

    /// <summary>The ids the project itself declares, which is what makes everything else transitive.</summary>
    private static IReadOnlyList<string> DirectDependencies(JsonElement root)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("project", out var project) &&
            project.TryGetProperty("frameworks", out var frameworks) &&
            frameworks.ValueKind == JsonValueKind.Object)
        {
            foreach (var framework in frameworks.EnumerateObject())
            {
                if (framework.Value.TryGetProperty("dependencies", out var dependencies) &&
                    dependencies.ValueKind == JsonValueKind.Object)
                {
                    foreach (var dependency in dependencies.EnumerateObject())
                        ids.Add(dependency.Name);
                }
            }
        }

        return ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

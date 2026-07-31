using System.Collections.Concurrent;
using Microsoft.Build.Evaluation;

namespace RoslynMCP.Services.ProjectModel;

/// <summary>One evaluated item with the metadata the Solution Explorer needs.</summary>
public sealed record ProjectItemInfo(
    string ItemType,
    string EvaluatedInclude,
    string FullPath,
    string? DependentUpon,
    string? Link,
    bool Visible);

public sealed record PackageReferenceInfo(
    string Id,
    string? Version,
    bool IsImplicit);

/// <summary>The parts of a project's item model the tree renders.</summary>
public sealed record ProjectEvaluation(
    string ProjectPath,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<ProjectItemInfo> Items,
    IReadOnlyList<PackageReferenceInfo> PackageReferences,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> AssemblyReferences,
    IReadOnlyList<string> Analyzers,
    IReadOnlyList<string> Imports);

/// <summary>
/// Evaluates a project's MSBuild item model. Roslyn's <see cref="Microsoft.CodeAnalysis.Project"/>
/// exposes documents and references but not item metadata, so file nesting (<c>DependentUpon</c>),
/// the Imports node, and package references have to come from here.
///
/// Evaluation is on demand — never for the whole solution up front — and cached against the
/// project file and every file it imports, because a change to Directory.Build.props changes
/// the answer as surely as a change to the csproj.
/// </summary>
public static class ProjectEvaluationService
{
    private static readonly ConcurrentDictionary<string, CacheEntry> s_cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim s_gate = new(Math.Max(1, Environment.ProcessorCount / 2));

    private sealed record CacheEntry(ProjectEvaluation Evaluation, IReadOnlyDictionary<string, DateTime> Stamps);

    public static async Task<ProjectEvaluation?> EvaluateAsync(
        string projectPath, CancellationToken cancellationToken = default)
    {
        string key = Path.GetFullPath(projectPath);
        if (!File.Exists(key))
            return null;

        if (s_cache.TryGetValue(key, out var cached) && IsFresh(cached.Stamps))
            return cached.Evaluation;

        await s_gate.WaitAsync(cancellationToken);
        try
        {
            if (s_cache.TryGetValue(key, out cached) && IsFresh(cached.Stamps))
                return cached.Evaluation;

            var (evaluation, stamps) = await Task.Run(() => Evaluate(key), cancellationToken);
            if (evaluation is not null)
                s_cache[key] = new CacheEntry(evaluation, stamps);
            return evaluation;
        }
        finally
        {
            s_gate.Release();
        }
    }

    public static void Evict(string projectPath) =>
        s_cache.TryRemove(Path.GetFullPath(projectPath), out _);

    public static void Clear() => s_cache.Clear();

    private static bool IsFresh(IReadOnlyDictionary<string, DateTime> stamps)
    {
        foreach (var (path, stamp) in stamps)
        {
            try
            {
                if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) != stamp)
                    return false;
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    private static (ProjectEvaluation?, IReadOnlyDictionary<string, DateTime>) Evaluate(string projectPath)
    {
        var stamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // A private collection keeps this evaluation out of any global state and lets it be
        // unloaded immediately; MSBuild's global collection would otherwise hold every project
        // it ever evaluated for the life of the process.
        using var collection = new ProjectCollection();
        try
        {
            var project = collection.LoadProject(projectPath);
            string projectDir = Path.GetDirectoryName(projectPath) ?? "";

            Stamp(stamps, projectPath);
            foreach (var import in project.Imports)
                Stamp(stamps, import.ImportedProject.FullPath);

            var evaluation = new ProjectEvaluation(
                ProjectPath: projectPath,
                TargetFrameworks: ReadTargetFrameworks(project),
                Items: ReadItems(project, projectDir),
                PackageReferences: ReadPackages(project),
                ProjectReferences: ReadPaths(project, "ProjectReference", projectDir),
                AssemblyReferences: project.GetItems("Reference")
                    .Select(i => i.EvaluatedInclude)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(i => i, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Analyzers: ReadPaths(project, "Analyzer", projectDir),
                Imports: project.Imports
                    .Select(i => i.ImportedProject.FullPath)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList());

            return (evaluation, stamps);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn(
                $"Could not evaluate '{Path.GetFileName(projectPath)}': {ex.Message}",
                key: $"evaluate:{projectPath}");
            return (null, stamps);
        }
        finally
        {
            collection.UnloadAllProjects();
        }
    }

    private static void Stamp(Dictionary<string, DateTime> stamps, string? path)
    {
        if (string.IsNullOrEmpty(path) || stamps.ContainsKey(path))
            return;
        try { stamps[path] = File.GetLastWriteTimeUtc(path); }
        catch { /* transient file, not worth tracking */ }
    }

    private static IReadOnlyList<string> ReadTargetFrameworks(Project project)
    {
        string? plural = project.GetPropertyValue("TargetFrameworks");
        if (!string.IsNullOrWhiteSpace(plural))
        {
            return plural.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        string? single = project.GetPropertyValue("TargetFramework");
        if (!string.IsNullOrWhiteSpace(single))
            return [single];

        string? legacy = project.GetPropertyValue("TargetFrameworkVersion");
        return string.IsNullOrWhiteSpace(legacy) ? [] : [legacy];
    }

    private static readonly string[] s_fileItemTypes =
        ["Compile", "None", "Content", "EmbeddedResource", "Page", "AdditionalFiles", "ApplicationDefinition"];

    private static IReadOnlyList<ProjectItemInfo> ReadItems(Project project, string projectDir)
    {
        var items = new List<ProjectItemInfo>();

        foreach (string itemType in s_fileItemTypes)
        {
            foreach (var item in project.GetItems(itemType))
            {
                string include = item.EvaluatedInclude;
                if (include.Length == 0)
                    continue;

                string full = Path.GetFullPath(Path.Combine(projectDir, include));
                string? dependentUpon = Nullify(item.GetMetadataValue("DependentUpon"));
                string? link = Nullify(item.GetMetadataValue("Link"));
                bool visible = !string.Equals(
                    item.GetMetadataValue("Visible"), "false", StringComparison.OrdinalIgnoreCase);

                items.Add(new ProjectItemInfo(itemType, include, full, dependentUpon, link, visible));
            }
        }

        return items;
    }

    private static IReadOnlyList<PackageReferenceInfo> ReadPackages(Project project) =>
        project.GetItems("PackageReference")
            .Select(item => new PackageReferenceInfo(
                item.EvaluatedInclude,
                Nullify(item.GetMetadataValue("Version")),
                string.Equals(item.GetMetadataValue("IsImplicitlyDefined"), "true", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> ReadPaths(Project project, string itemType, string projectDir) =>
        project.GetItems(itemType)
            .Select(item => Path.GetFullPath(Path.Combine(projectDir, item.EvaluatedInclude)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? Nullify(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

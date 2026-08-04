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

/// <summary>
/// A package reference and where its version actually came from.
/// </summary>
/// <param name="VersionSource">
/// The file whose XML carried the version. Under Central Package Management that is a
/// Directory.Packages.props somewhere up the tree, which is the only useful answer to "where do
/// I change this".
/// </param>
public sealed record PackageReferenceInfo(
    string Id,
    string? Version,
    bool IsImplicit,
    bool IsCentrallyManaged = false,
    bool IsVersionOverride = false,
    bool IsGlobalPackageReference = false,
    string? VersionSource = null);

/// <summary>The parts of a project's item model the tree renders.</summary>
public sealed record ProjectEvaluation(
    string ProjectPath,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<ProjectItemInfo> Items,
    IReadOnlyList<PackageReferenceInfo> PackageReferences,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> AssemblyReferences,
    IReadOnlyList<string> Analyzers,
    IReadOnlyList<string> Imports,
    IReadOnlyDictionary<string, string> Properties);

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

            // Microsoft.Build ships with runtime assets excluded, so its assemblies resolve
            // only through MSBuildLocator's resolver. Touching a Microsoft.Build type before
            // registration takes down the process rather than throwing.
            WorkspaceService.EnsureRegistered();

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

    // NoInlining matters: the JIT resolves a method's types on entry, so inlining this into a
    // caller would load Microsoft.Build before EnsureRegistered() had run.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
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
                    .ToList(),
                Properties: ReadProperties(project));

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

    /// <summary>
    /// Properties the package panel and the AI need to reason about a project, captured from a
    /// fixed list rather than wholesale — an evaluated project carries roughly two thousand
    /// properties, and every one of them would be cached and serialized with the tree.
    /// </summary>
    private static readonly string[] s_capturedProperties =
    [
        "ManagePackageVersionsCentrally", "CentralPackageTransitivePinningEnabled",
        "CentralPackageVersionOverrideEnabled", "NuGetAudit", "NuGetAuditMode", "NuGetAuditLevel",
        "RestorePackagesWithLockFile", "TargetFramework", "TargetFrameworks", "TargetFrameworkVersion",
        "AssemblyName", "OutputType", "OutputPath",
    ];

    private static IReadOnlyDictionary<string, string> ReadProperties(Project project)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in s_capturedProperties)
        {
            if (Nullify(project.GetPropertyValue(name)) is { } value)
                properties[name] = value;
        }
        return properties;
    }

    /// <summary>
    /// Package references with their versions resolved, including under Central Package Management.
    /// </summary>
    /// <remarks>
    /// A centrally managed <c>PackageReference</c> carries no <c>Version</c> metadata at all —
    /// NuGet joins it to a <c>PackageVersion</c> during restore, not during evaluation. Reading
    /// only the reference's own metadata therefore yields empty strings, which is why the Updates
    /// and Consolidate tabs used to come up silently empty on every CPM repo. The join is done
    /// here instead: <c>Directory.Packages.props</c> is imported through <c>Microsoft.Common.props</c>,
    /// so its items are already in the evaluation and already stamped for cache invalidation.
    /// </remarks>
    private static IReadOnlyList<PackageReferenceInfo> ReadPackages(Project project)
    {
        bool centrallyManaged = string.Equals(
            project.GetPropertyValue("ManagePackageVersionsCentrally"), "true", StringComparison.OrdinalIgnoreCase);

        // Include= and Update= forms both land in GetItems, so both are covered.
        var central = new Dictionary<string, (string? Version, string? Source)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in project.GetItems("PackageVersion"))
            central[item.EvaluatedInclude] = (Nullify(item.GetMetadataValue("Version")), DefiningFile(item));

        // The SDK's NuGet.targets projects every GlobalPackageReference into a version-less
        // PackageReference during evaluation, so the same package arrives twice. The global entry
        // is the one that knows where its version lives.
        var global = project.GetItems("GlobalPackageReference")
            .Select(item => item.EvaluatedInclude)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var packages = new List<PackageReferenceInfo>();

        foreach (var item in project.GetItems("PackageReference"))
        {
            string id = item.EvaluatedInclude;
            bool implicitly = string.Equals(
                item.GetMetadataValue("IsImplicitlyDefined"), "true", StringComparison.OrdinalIgnoreCase);

            // A project can override a global reference's version for itself. That override lives
            // in the csproj, so it has to win — skipping the reference first would report the
            // central version and point any edit at the wrong file.
            if (global.Contains(id) && Nullify(item.GetMetadataValue("VersionOverride")) is null)
                continue;

            if (Nullify(item.GetMetadataValue("VersionOverride")) is { } overridden)
            {
                packages.Add(new PackageReferenceInfo(
                    id, overridden, implicitly, IsCentrallyManaged: centrallyManaged,
                    IsVersionOverride: true, VersionSource: DefiningFile(item)));
            }
            else if (Nullify(item.GetMetadataValue("Version")) is { } inline)
            {
                packages.Add(new PackageReferenceInfo(
                    id, inline, implicitly, VersionSource: DefiningFile(item)));
            }
            else if (centrallyManaged && central.TryGetValue(id, out var managed))
            {
                packages.Add(new PackageReferenceInfo(
                    id, managed.Version, implicitly, IsCentrallyManaged: true, VersionSource: managed.Source));
            }
            else
            {
                packages.Add(new PackageReferenceInfo(id, null, implicitly));
            }
        }

        // A GlobalPackageReference is a real, user-authored dependency of every project in the
        // repo, so it is listed rather than treated as implicit — but flagged, because removing
        // it from one project is not a thing that can be done.
        foreach (var item in project.GetItems("GlobalPackageReference"))
        {
            string id = item.EvaluatedInclude;
            var managed = central.GetValueOrDefault(id);
            packages.Add(new PackageReferenceInfo(
                id,
                Nullify(item.GetMetadataValue("Version")) ?? managed.Version,
                IsImplicit: false,
                IsCentrallyManaged: true,
                IsGlobalPackageReference: true,
                VersionSource: managed.Source ?? DefiningFile(item)));
        }

        return packages
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? DefiningFile(ProjectItem item)
    {
        try { return Nullify(item.Xml?.ContainingProject?.FullPath); }
        catch { return null; }
    }

    private static IReadOnlyList<string> ReadPaths(Project project, string itemType, string projectDir) =>
        project.GetItems(itemType)
            .Select(item => Path.GetFullPath(Path.Combine(projectDir, item.EvaluatedInclude)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? Nullify(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

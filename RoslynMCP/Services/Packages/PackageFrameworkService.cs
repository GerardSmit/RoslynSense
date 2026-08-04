using System.Runtime.CompilerServices;
using NuGet.Frameworks;

namespace RoslynMCP.Services.Packages;

/// <param name="UnsupportedFrameworks">
/// The project's target frameworks the package has nothing for. Empty when it is usable
/// everywhere the project builds.
/// </param>
public sealed record FrameworkCompatibility(
    bool Compatible,
    IReadOnlyList<string> UnsupportedFrameworks,
    IReadOnlyList<string> PackageFrameworks);

/// <summary>
/// Whether a package version has anything a project's target frameworks can actually use.
/// </summary>
/// <remarks>
/// Without this the first sign that a package does not support the project is an NU1202 from
/// restore, after the reference has already been written. The check is advisory and never blocks:
/// analyzer, native and content-only packages legitimately declare no dependency groups at all,
/// and refusing a valid install is a worse failure than a warning nobody reads.
/// </remarks>
public static class PackageFrameworkService
{
    public static async Task<FrameworkCompatibility> CheckAsync(
        string packageId, string version, IReadOnlyList<string> projectFrameworks, CancellationToken ct)
    {
        var groups = await NuGetMetadataService.DependencyGroupsAsync(packageId, version, ct);

        // NuGet.Frameworks ships with runtime assets excluded, so its types resolve only through
        // MSBuildLocator's resolver. Touching one before registration takes down the process.
        WorkspaceService.EnsureRegistered();
        return Reduce(projectFrameworks, groups);
    }

    /// <summary>
    /// What a package version depends on when it is consumed by a project with these target
    /// frameworks.
    /// </summary>
    /// <remarks>
    /// The group is chosen the way restore chooses it. Flattening every group instead would report
    /// the netstandard2.0 group's dependencies to a net8.0 project — usually a longer list, at
    /// older versions, none of which that project would ever resolve.
    ///
    /// Ids can repeat when a project multi-targets and the groups disagree; the caller decides
    /// which requirement wins, because only it knows whether it wants the strictest or the loosest.
    /// </remarks>
    public static async Task<IReadOnlyList<PackageDependencyInfo>> DependenciesForAsync(
        string packageId, string version, IReadOnlyList<string> projectFrameworks, CancellationToken ct)
    {
        var groups = await NuGetMetadataService.DependencyGroupsAsync(packageId, version, ct);

        WorkspaceService.EnsureRegistered();
        return Nearest(projectFrameworks, groups);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IReadOnlyList<PackageDependencyInfo> Nearest(
        IReadOnlyList<string> projectFrameworks,
        IReadOnlyList<PackageDependencyGroupInfo> groups)
    {
        var parsed = new List<(NuGetFramework Framework, PackageDependencyGroupInfo Group)>();
        foreach (var group in groups)
        {
            // The empty moniker is the "any framework" group, which is what a package with a
            // single flat dependency list produces.
            var framework = group.TargetFramework.Length == 0
                ? NuGetFramework.AnyFramework
                : NuGetFramework.Parse(group.TargetFramework);

            if (!framework.IsUnsupported)
                parsed.Add((framework, group));
        }

        if (parsed.Count == 0)
            return [];

        var reducer = new FrameworkReducer();
        var dependencies = new List<PackageDependencyInfo>();

        foreach (string moniker in projectFrameworks)
        {
            var target = NuGetFramework.Parse(Normalize(moniker));
            if (target.IsUnsupported)
                continue;

            if (reducer.GetNearest(target, parsed.Select(p => p.Framework)) is not { } nearest)
                continue;

            dependencies.AddRange(parsed.First(p => p.Framework.Equals(nearest)).Group.Dependencies);
        }

        return dependencies;
    }

    /// <summary>The project's frameworks, or an empty list when it targets nothing recognizable.</summary>
    public static async Task<IReadOnlyList<string>> FrameworksOfAsync(
        string projectPath, CancellationToken ct)
    {
        var evaluation = await ProjectModel.ProjectEvaluationService.EvaluateAsync(projectPath, ct);
        return evaluation?.TargetFrameworks ?? [];
    }

    // NoInlining: the JIT resolves a method's types when it prepares the method, so inlining this
    // would load NuGet.Frameworks before EnsureRegistered() had run.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FrameworkCompatibility Reduce(
        IReadOnlyList<string> projectFrameworks,
        IReadOnlyList<PackageDependencyGroupInfo> groups)
    {
        var packageFrameworks = groups
            .Select(g => g.TargetFramework)
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A package that declares no dependency groups says nothing about compatibility — that is
        // every analyzer and every native-asset package. Give it the benefit of the doubt.
        if (packageFrameworks.Count == 0)
            return new FrameworkCompatibility(true, [], packageFrameworks);

        var candidates = packageFrameworks
            .Select(NuGetFramework.Parse)
            .Where(f => !f.IsUnsupported)
            .ToList();

        if (candidates.Count == 0)
            return new FrameworkCompatibility(true, [], packageFrameworks);

        var reducer = new FrameworkReducer();
        var unsupported = new List<string>();

        foreach (string moniker in projectFrameworks)
        {
            var parsed = NuGetFramework.Parse(Normalize(moniker));

            // An unparseable moniker is our problem, not the package's; reporting it as
            // incompatible would warn about every legacy project.
            if (parsed.IsUnsupported)
                continue;

            if (reducer.GetNearest(parsed, candidates) is null)
                unsupported.Add(moniker);
        }

        return new FrameworkCompatibility(unsupported.Count == 0, unsupported, packageFrameworks);
    }

    /// <summary>
    /// A non-SDK project reports its framework as <c>v4.7.2</c>, which NuGet reads as unsupported.
    /// Same digit-stripping the packages.config installer does.
    /// </summary>
    private static string Normalize(string moniker) =>
        moniker.StartsWith('v') && moniker.Skip(1).All(c => char.IsAsciiDigit(c) || c == '.')
            ? "net" + new string([.. moniker.Where(char.IsAsciiDigit)])
            : moniker;
}

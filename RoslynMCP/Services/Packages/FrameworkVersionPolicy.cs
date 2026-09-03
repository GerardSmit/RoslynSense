using System.Runtime.CompilerServices;
using NuGet.Frameworks;

namespace RoslynMCP.Services.Packages;

/// <summary>
/// The .NET major a reference should stay on, for the package families that ship in lockstep with
/// the platform.
/// </summary>
/// <remarks>
/// <para>
/// A net8.0 project on <c>Microsoft.Extensions.Logging</c> 8.0.1 is not out of date because 9.0.0
/// exists. The 9.x band is built for net9.0 and merely happens to also declare netstandard2.0, so
/// nothing in the version numbers or the dependency groups stops restore from taking it — the
/// project ends up a band ahead of the runtime it targets, which is how a solution acquires two
/// copies of the same assembly.
/// </para>
/// <para>
/// The cap is deliberately two conditions rather than one. A prefix list alone would cap
/// <c>System.Reactive</c>, whose 6.x has nothing to do with .NET 6; requiring that the package
/// actually publishes the platform's major means a family that does not version this way falls
/// straight back to the unbounded behaviour rather than being pinned to a version that was never
/// released.
/// </para>
/// </remarks>
public static class FrameworkVersionPolicy
{
    /// <summary>
    /// Package ids whose major version tracks the .NET major.
    /// </summary>
    /// <remarks>
    /// <c>System.</c> is here because the out-of-band runtime libraries — Text.Json,
    /// Diagnostics.DiagnosticSource, Collections.Immutable — carry the band in their major and are
    /// the packages most often dragged forward by a transitive reference. The ones that only look
    /// like they belong (System.Reactive, System.CommandLine) are excluded by the second condition,
    /// not by this list.
    /// </remarks>
    private static readonly string[] s_families =
    [
        "Microsoft.AspNetCore.",
        "Microsoft.Bcl.",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Extensions.",
        "Microsoft.JSInterop",
        "Microsoft.NETCore.",
        "System.",
    ];

    public static bool TracksPlatformVersion(string packageId) =>
        s_families.Any(family => packageId.StartsWith(family, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The .NET major every one of a project's target frameworks can use, or <c>null</c> when the
    /// project targets nothing the cap applies to.
    /// </summary>
    /// <remarks>
    /// The lowest wins in a multi-targeting project: a package built for net10.0 is of no use to
    /// the net8.0 leg, and the reference is one version for both. Frameworks outside .NET 5+ —
    /// netstandard, .NET Framework — say nothing about the band and are ignored rather than
    /// treated as a cap of their own.
    /// </remarks>
    public static int? PlatformMajor(IReadOnlyList<string> targetFrameworks)
    {
        if (targetFrameworks.Count == 0)
            return null;

        WorkspaceService.EnsureRegistered();
        return PlatformMajorCore(targetFrameworks);
    }

    // NoInlining: NuGet.Frameworks resolves only through MSBuildLocator's resolver, so its types
    // must not be prepared before EnsureRegistered() has run.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int? PlatformMajorCore(IReadOnlyList<string> targetFrameworks)
    {
        int? lowest = null;

        foreach (string moniker in targetFrameworks)
        {
            var parsed = NuGetFramework.Parse(moniker);
            if (parsed.IsUnsupported ||
                !parsed.Framework.Equals(FrameworkConstants.FrameworkIdentifiers.NetCoreApp, StringComparison.OrdinalIgnoreCase) ||
                parsed.Version.Major < 5)
            {
                continue;
            }

            lowest = lowest is { } current ? Math.Min(current, parsed.Version.Major) : parsed.Version.Major;
        }

        return lowest;
    }
}

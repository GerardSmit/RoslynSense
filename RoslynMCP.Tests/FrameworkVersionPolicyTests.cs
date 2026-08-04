using System.Reflection;
using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The platform band a reference is held to, and which packages that band applies to.
/// </summary>
/// <remarks>
/// The pair of conditions is the point. A prefix list on its own caps System.Reactive to a .NET
/// major it never shipped; requiring the package to actually publish that major is what turns a
/// wrong cap into no cap.
/// </remarks>
public class FrameworkVersionPolicyTests
{
    [Theory]
    [InlineData("Microsoft.Extensions.Logging", true)]
    [InlineData("Microsoft.AspNetCore.Authentication.JwtBearer", true)]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", true)]
    [InlineData("System.Text.Json", true)]
    [InlineData("Newtonsoft.Json", false)]
    [InlineData("Grpc.Net.Client", false)]
    [InlineData("Serilog", false)]
    public void PlatformFamiliesAreRecognizedByPrefix(string packageId, bool expected) =>
        Assert.Equal(expected, FrameworkVersionPolicy.TracksPlatformVersion(packageId));

    [Theory]
    // A single modern target is the band.
    [InlineData(new[] { "net8.0" }, 8)]
    [InlineData(new[] { "net10.0" }, 10)]
    // Platform-specific monikers are the same .NET major.
    [InlineData(new[] { "net8.0-windows" }, 8)]
    // Multi-targeting takes the lowest: the reference is one version, and a net10.0 build is of no
    // use to the net8.0 leg.
    [InlineData(new[] { "net10.0", "net8.0" }, 8)]
    // Frameworks outside .NET 5+ say nothing about the band rather than capping to their own.
    [InlineData(new[] { "net8.0", "netstandard2.0" }, 8)]
    [InlineData(new[] { "netstandard2.0" }, null)]
    [InlineData(new[] { "net472" }, null)]
    [InlineData(new string[0], null)]
    public void PlatformMajorIsTheLowestModernTarget(string[] frameworks, int? expected) =>
        Assert.Equal(expected, FrameworkVersionPolicy.PlatformMajor(frameworks));

    /// <summary>
    /// A dependency group is chosen the way restore chooses it, not by flattening every group.
    /// </summary>
    [Fact]
    public void DependenciesComeFromTheNearestGroup()
    {
        var groups = new[]
        {
            new PackageDependencyGroupInfo("netstandard2.0", [new PackageDependencyInfo("Old.Shim", "[1.0.0, )")]),
            new PackageDependencyGroupInfo("net8.0", [new PackageDependencyInfo("New.Thing", "[9.0.0, )")]),
        };

        var picked = InvokeNearest(["net8.0"], groups);

        Assert.Equal(["New.Thing"], picked.Select(d => d.Id));
    }

    /// <summary>
    /// A package with one flat dependency list declares the "any" group, which has to match
    /// everything rather than nothing.
    /// </summary>
    [Fact]
    public void TheAnyGroupAppliesToEveryTarget()
    {
        var groups = new[]
        {
            new PackageDependencyGroupInfo("", [new PackageDependencyInfo("Contoso.Core", "[2.0.0, )")]),
        };

        Assert.Equal(["Contoso.Core"], InvokeNearest(["net8.0"], groups).Select(d => d.Id));
    }

    [Fact]
    public void AProjectTargetWithNoUsableGroupContributesNothing()
    {
        var groups = new[]
        {
            new PackageDependencyGroupInfo("net10.0", [new PackageDependencyInfo("New.Thing", "[1.0.0, )")]),
        };

        Assert.Empty(InvokeNearest(["netstandard2.0"], groups));
    }

    /// <summary>
    /// Calls the MSBuildLocator-gated core directly: the selection is pure, so it can be covered
    /// against hand-built groups without a feed.
    /// </summary>
    private static IReadOnlyList<PackageDependencyInfo> InvokeNearest(
        IReadOnlyList<string> projectFrameworks, IReadOnlyList<PackageDependencyGroupInfo> groups)
    {
        RoslynMCP.Services.WorkspaceService.EnsureRegistered();

        var method = typeof(PackageFrameworkService).GetMethod(
            "Nearest", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return (IReadOnlyList<PackageDependencyInfo>)method!.Invoke(null, [projectFrameworks, groups])!;
    }
}

using RoslynMCP.Services.Packages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Target-framework compatibility, checked before an install rather than discovered as an NU1202
/// after the reference is already written.
/// </summary>
public class PackageFrameworkServiceTests
{
    [Fact]
    public async Task ProjectFrameworksComeFromTheEvaluatedModel()
    {
        var frameworks = await PackageFrameworkService.FrameworksOfAsync(
            FixturePaths.CpmMultiTfmProjectFile, default);

        Assert.Equal(["net10.0", "netstandard2.0"], frameworks);
    }

    [Fact]
    public async Task AProjectThatCannotBeEvaluatedReportsNoFrameworks() =>
        Assert.Empty(await PackageFrameworkService.FrameworksOfAsync(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "App.csproj"), default));

    [Fact]
    public async Task AnUnknownPackageIsNotReportedIncompatible()
    {
        // Nothing is known about it, so nothing is claimed. Warning about a package we simply
        // could not look up would train people to ignore the warning.
        var result = await PackageFrameworkService.CheckAsync(
            $"Nonexistent.Package.{Guid.NewGuid():N}", "1.0.0", ["net10.0"], default);

        Assert.True(result.Compatible);
        Assert.Empty(result.UnsupportedFrameworks);
    }

    [Theory]
    // A netstandard2.0 package is usable from a modern target and from netstandard2.0 itself.
    [InlineData("netstandard2.0", "net10.0", true)]
    [InlineData("netstandard2.0", "netstandard2.0", true)]
    // The other direction never works: netstandard2.0 cannot consume a net10.0-only package.
    [InlineData("net10.0", "netstandard2.0", false)]
    [InlineData("net10.0", "net10.0", true)]
    // Legacy monikers, which is the whole reason the packages.config path exists.
    [InlineData("netstandard2.0", "net472", true)]
    [InlineData("netstandard2.1", "net472", false)]
    public void FrameworkCompatibilityMatchesNuGetsOwnReducer(
        string packageFramework, string projectFramework, bool expected)
    {
        var result = Reduce([projectFramework], packageFramework);

        Assert.Equal(expected, result.Compatible);
        Assert.Equal(expected, result.UnsupportedFrameworks.Count == 0);
    }

    [Fact]
    public void APackageWithNoDependencyGroupsIsAlwaysCompatible()
    {
        // Analyzer, native-asset and content-only packages legitimately declare none. Refusing
        // them would be a worse failure than the warning this exists to produce.
        var result = InvokeReduce(["net10.0"], []);

        Assert.True(result.Compatible);
        Assert.Empty(result.PackageFrameworks);
    }

    [Fact]
    public void ALegacyTargetFrameworkVersionIsNormalizedBeforeComparison()
    {
        // A non-SDK project reports "v4.7.2", which NuGet reads as Unsupported. Left alone, every
        // .NET Framework project would be warned about every package.
        var result = Reduce(["v4.7.2"], "net472");

        Assert.True(result.Compatible);
        Assert.Empty(result.UnsupportedFrameworks);
    }

    [Fact]
    public void AProjectFrameworkThatCannotBeParsedIsSkippedNotFlagged()
    {
        // An unparseable moniker is our problem, not the package's.
        var result = Reduce(["not-a-framework"], "net10.0");

        Assert.True(result.Compatible);
    }

    [Fact]
    public void EveryUnsupportedFrameworkOfAMultiTargetedProjectIsNamed()
    {
        var result = Reduce(["net10.0", "netstandard2.0"], "net10.0");

        Assert.False(result.Compatible);
        // The message has to say which target framework is the problem, not just that one is.
        Assert.Equal(["netstandard2.0"], result.UnsupportedFrameworks);
    }

    /// <summary>
    /// The MSBuildLocator gate. NuGet.Frameworks ships with runtime assets excluded and resolves
    /// only through the resolver MSBuildLocator installs; touching one of its types before
    /// registration takes down the process rather than throwing. Reaching this assertion at all
    /// means the gate held.
    /// </summary>
    [Fact]
    public async Task CheckingCompatibilityDoesNotBringDownTheProcess()
    {
        var frameworks = await PackageFrameworkService.FrameworksOfAsync(
            FixturePaths.CpmManagedProjectFile, default);

        var result = await PackageFrameworkService.CheckAsync(
            "Newtonsoft.Json", "13.0.3", frameworks, default);

        Assert.NotNull(result);
    }

    private static FrameworkCompatibility Reduce(IReadOnlyList<string> projectFrameworks, string packageFramework) =>
        InvokeReduce(
            projectFrameworks,
            [new PackageDependencyGroupInfo(packageFramework, [])]);

    /// <summary>
    /// Calls the gated core directly. The reduction is pure, so testing it against hand-built
    /// dependency groups covers the compatibility matrix without a feed.
    /// </summary>
    private static FrameworkCompatibility InvokeReduce(
        IReadOnlyList<string> projectFrameworks, IReadOnlyList<PackageDependencyGroupInfo> groups)
    {
        RoslynMCP.Services.WorkspaceService.EnsureRegistered();

        var method = typeof(PackageFrameworkService).GetMethod(
            "Reduce", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        return (FrameworkCompatibility)method!.Invoke(null, [projectFrameworks, groups])!;
    }
}

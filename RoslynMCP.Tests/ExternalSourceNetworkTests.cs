using RoslynMCP.Services;
using RoslynMCP.Services.ExternalSource;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The end-to-end claim, against the real symbol server: navigating to a framework type reaches
/// the source its author wrote rather than a decompilation of it.
/// </summary>
[Collection(SharedState.Name)]
public class ExternalSourceNetworkTests
{
    [RequiresNetworkFact]
    public async Task WhenNavigatingToAFrameworkTypeThenItsRealSourceIsFetched()
    {
        var symbol = await RoslynTestHelpers.GetNamedTypeAsync(
            FixturePaths.SampleProjectFile, "System.String");
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        var result = await SourceLinkService.TryResolveAsync(symbol, project, default);

        Assert.NotNull(result);

        // Which repository the framework is published from has changed between releases
        // (dotnet/runtime, then the dotnet/dotnet monorepo), so only the host is asserted.
        Assert.StartsWith("https://raw.githubusercontent.com/", result!.Url);
        Assert.True(File.Exists(result.FilePath));

        // The distinguishing mark of real source over decompilation: the author's own prose.
        string text = await File.ReadAllTextAsync(result.FilePath);
        Assert.Contains("<summary>", text);
        Assert.Contains("partial class String", text);
    }

    [RequiresNetworkFact]
    public async Task WhenAFrameworkAssemblyIsAskedAboutThenItsSymbolsCarryASourceLinkMap()
    {
        var symbol = await RoslynTestHelpers.GetNamedTypeAsync(
            FixturePaths.SampleProjectFile, "System.Text.StringBuilder");
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        var result = await SourceLinkService.TryResolveAsync(symbol, project, default);

        Assert.NotNull(result);
        Assert.StartsWith("https://", result!.Url);
        Assert.EndsWith(".cs", result.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// .NET Framework has no Source Link at all, so this is the whole of its story: the published
    /// snapshot for the exact version, found by name and confirmed by reading it.
    /// </summary>
    [RequiresFrameworkSnapshotFact]
    public async Task WhenAFrameworkAssemblyHasNoSymbolsThenThePublishedSnapshotIsRead()
    {
        var result = await ReferenceSourceService.TryResolveAsync(
            symbol: null, "System.Net.WebClient", FrameworkSystemAssembly()!, default);

        Assert.NotNull(result);
        Assert.Equal(ExternalSourceKind.ReferenceSource, result!.Kind);
        Assert.Contains("3b1eaf5", result.Origin);

        string text = await File.ReadAllTextAsync(result.FilePath);
        Assert.Contains("class WebClient", text);
    }

    internal static string? FrameworkSystemAssembly()
    {
        string candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\System.dll");

        return File.Exists(candidate) ? candidate : null;
    }
}

/// <summary>
/// Skips unless the network is allowed and .NET Framework 4.7.2 is installed to resolve against.
/// </summary>
public sealed class RequiresFrameworkSnapshotFactAttribute : RequiresNetworkFactAttribute
{
    public RequiresFrameworkSnapshotFactAttribute()
    {
        if (Skip is null && ExternalSourceNetworkTests.FrameworkSystemAssembly() is null)
            Skip = "No .NET Framework 4.7.2 reference assemblies to resolve against.";
    }
}

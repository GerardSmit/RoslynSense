using RoslynMCP.Services.ExternalSource;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The promise that made the feature safe to turn on: whatever fails, navigation still lands.
/// </summary>
[Collection(SharedState.Name)]
public class ExternalSourceFallbackTests
{
    [Fact]
    public async Task WhenNothingMayBeFetchedThenNavigationStillDecompiles()
    {
        using var offline = ExternalSourceScope.Offline();

        var symbol = await RoslynTestHelpers.GetNamedTypeAsync(
            FixturePaths.SampleProjectFile, "System.String");
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        var result = await ExternalSourceService.TryResolveAsync(symbol, project, default);

        Assert.NotNull(result);
        Assert.Equal(ExternalSourceKind.Decompiled, result!.Kind);
        Assert.True(File.Exists(result.FilePath));
    }
}

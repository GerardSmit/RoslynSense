using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

public class DecompiledSourceServiceTests
{
    [Fact]
    public void WhenManifestFileNameProvidedThenIsGeneratedProjectPathReturnsTrue()
    {
        Assert.True(DecompiledSourceService.IsGeneratedProjectPath(
            Path.Combine("some", "dir", DecompiledSourceService.ManifestFileName)));
    }

    [Fact]
    public void WhenRegularCsprojProvidedThenIsGeneratedProjectPathReturnsFalse()
    {
        Assert.False(DecompiledSourceService.IsGeneratedProjectPath("MyProject.csproj"));
    }

    [Fact]
    public void WhenFileInNonExistentDirectoryThenTryGetGeneratedProjectPathReturnsNull()
    {
        var result = DecompiledSourceService.TryGetGeneratedProjectPath(
            Path.Combine("Z:", "nonexistent", "file.cs"));

        Assert.Null(result);
    }

    [Fact]
    public void WhenEmptyDirectoryThenTryGetGeneratedProjectPathReturnsNull()
    {
        var result = DecompiledSourceService.TryGetGeneratedProjectPath("file.cs");

        Assert.Null(result);
    }

    [Fact]
    public void WhenFileInRealDirectoryWithoutManifestThenTryGetGeneratedProjectPathReturnsNull()
    {
        // Use a known directory that doesn't have a manifest
        var result = DecompiledSourceService.TryGetGeneratedProjectPath(
            FixturePaths.CalculatorFile);

        Assert.Null(result);
    }
}

using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public class SourceGeneratedFilesToolTests
{
    [Fact]
    public async Task GetSourceGeneratedFileContent_WhenEmptyPath_ThenReturnsError()
    {
        var result = await SourceGeneratedFilesTool.GetSourceGeneratedFileContent("", "test.g.cs");
        Assert.StartsWith("Error", result);
    }

    [Fact]
    public async Task GetSourceGeneratedFileContent_WhenEmptyHintName_ThenReturnsError()
    {
        var result = await SourceGeneratedFilesTool.GetSourceGeneratedFileContent(
            FixturePaths.BlazorProjectFile, "");
        Assert.StartsWith("Error", result);
    }

    [Fact]
    public async Task GetSourceGeneratedFileContent_WhenInvalidHintName_ThenReturnsError()
    {
        var result = await SourceGeneratedFilesTool.GetSourceGeneratedFileContent(
            FixturePaths.BlazorProjectFile, "NonExistent_File_That_Cannot_Exist.g.cs");
        Assert.Contains("No source-generated file matching", result);
    }

    [RequiresRazorSourceGeneratorFact]
    public async Task GetSourceGeneratedFileContent_WhenValidHintName_ThenReturnsContent()
    {
        // The hint name comes from Roslyn directly, which is what the tool resolves against.
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.BlazorProjectFile);
        var generatedDocs = (await project.GetSourceGeneratedDocumentsAsync()).ToList();
        Assert.NotEmpty(generatedDocs);

        var firstDoc = generatedDocs[0];
        var hintName = firstDoc.HintName ?? firstDoc.Name;

        var result = await SourceGeneratedFilesTool.GetSourceGeneratedFileContent(
            FixturePaths.BlazorProjectFile, hintName);

        Assert.Contains("Source-generated file", result);
        Assert.Contains("Generator", result);
        Assert.Contains("Lines", result);
        // Should have line numbers
        Assert.Contains("    1. ", result);
    }

    [RequiresRazorSourceGeneratorFact]
    public async Task GetSourceGeneratedFileContent_WhenPartialHintName_ThenMatchesByContains()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.BlazorProjectFile);
        var generatedDocs = (await project.GetSourceGeneratedDocumentsAsync()).ToList();
        Assert.NotEmpty(generatedDocs);

        // Use just the last part of the hint name (partial match)
        var firstDoc = generatedDocs[0];
        var fullHintName = firstDoc.HintName ?? firstDoc.Name ?? "";
        var partial = Path.GetFileNameWithoutExtension(fullHintName);

        var result = await SourceGeneratedFilesTool.GetSourceGeneratedFileContent(
            FixturePaths.BlazorProjectFile, partial);

        Assert.Contains("Source-generated file", result);
    }
}

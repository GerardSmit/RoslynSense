using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>The AI-facing tools that change files: formatting and file rename.</summary>
[Collection(SharedState.Name)]
public class McpMutationToolsTests
{
    [Fact]
    public async Task FormatDocumentRewritesBadlyFormattedCode()
    {
        string path = Path.Combine(FixturePaths.SampleProjectDir, $"Unformatted{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(path, """
            namespace SampleProject;
            public class Unformatted{
                  public int  Value=>1;
            }
            """);
        await WorkspaceService.EvictAllAsync();

        try
        {
            string result = await FormatDocumentTool.FormatDocument(path);

            Assert.Contains("Formatted", result);
            string after = await File.ReadAllTextAsync(path);
            Assert.NotEqual("      public int  Value=>1;", after);
            Assert.Contains("public int Value => 1;", after);
        }
        finally
        {
            File.Delete(path);
            await WorkspaceService.EvictAllAsync();
        }
    }

    [Fact]
    public async Task FormattingAnAlreadyFormattedFileSaysSoWithoutRewriting()
    {
        string result = await FormatDocumentTool.FormatDocument(FixturePaths.CalculatorFile);

        Assert.Contains("Calculator.cs", result);
    }

    [Fact]
    public async Task FormatDocumentReportsAMissingFile()
    {
        string result = await FormatDocumentTool.FormatDocument(
            Path.Combine(FixturePaths.SampleProjectDir, $"missing-{Guid.NewGuid():N}.cs"));

        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public async Task PackageToolsListWhatTheProjectReferences()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        string result = await PackageTool.ListPackages(
            new MarkdownFormatter(), projectPath: FixturePaths.SampleProjectFile);

        Assert.Contains("Packages", result);
    }

    [Fact]
    public async Task AddPackageRequiresAProject()
    {
        string result = await PackageTool.AddPackage("Some.Package", "");

        Assert.StartsWith("Error:", result);
    }
}

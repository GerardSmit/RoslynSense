using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>The AI-facing tools that change files: formatting and file rename.</summary>
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
    public async Task RenameFileMovesTheFileAndUpdatesReferences()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string typePath = Path.Combine(FixturePaths.SampleProjectDir, $"Movable{suffix}.cs");
        string userPath = Path.Combine(FixturePaths.SampleProjectDir, $"MovableUser{suffix}.cs");

        await File.WriteAllTextAsync(typePath, $$"""
            namespace SampleProject;

            public class Movable{{suffix}}
            {
                public int Value => 7;
            }
            """);
        await File.WriteAllTextAsync(userPath, $$"""
            namespace SampleProject;

            public class MovableUser{{suffix}}
            {
                public int Use() => new Movable{{suffix}}().Value;
            }
            """);
        await WorkspaceService.EvictAllAsync();

        string renamed = Path.Combine(FixturePaths.SampleProjectDir, $"Relocated{suffix}.cs");
        try
        {
            string result = await RenameFileTool.RenameFile(typePath, $"Relocated{suffix}");

            Assert.Contains("Renamed", result);
            Assert.False(File.Exists(typePath));
            Assert.True(File.Exists(renamed));

            // The type moved with the file, and its use site followed.
            Assert.Contains($"class Relocated{suffix}", await File.ReadAllTextAsync(renamed));
            Assert.Contains($"new Relocated{suffix}()", await File.ReadAllTextAsync(userPath));
        }
        finally
        {
            File.Delete(renamed);
            File.Delete(typePath);
            File.Delete(userPath);
            await WorkspaceService.EvictAllAsync();
        }
    }

    [Fact]
    public async Task RenameFileRefusesToOverwriteAnExistingFile()
    {
        string result = await RenameFileTool.RenameFile(
            FixturePaths.CalculatorFile, Path.GetFileName(FixturePaths.ServicesFile));

        Assert.StartsWith("Error:", result);
        Assert.True(File.Exists(FixturePaths.CalculatorFile));
    }

    [Fact]
    public async Task PackageToolsListWhatTheProjectReferences()
    {
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        string result = await PackageTool.ListPackages(
            new MarkdownFormatter(), FixturePaths.SampleProjectFile);

        Assert.Contains("Packages", result);
    }

    [Fact]
    public async Task AddPackageRequiresAProject()
    {
        string result = await PackageTool.AddPackage("Some.Package", "");

        Assert.StartsWith("Error:", result);
    }
}

using Xunit;

namespace RoslynMCP.Tests;

public class ProjectStructureToolTests
{
    [Fact]
    public async Task WhenEmptyPathThenReturnsError()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure("");
        Assert.StartsWith("Error: Path cannot be empty", result);
    }

    [Fact]
    public async Task WhenFileNotFoundThenReturnsError()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            @"C:\nonexistent\project.csproj");
        Assert.Contains("does not exist", result);
    }

    [Fact]
    public async Task WhenCsprojProvidedThenShowsProjectStructure()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.SampleProjectFile);

        Assert.Contains("# Project: SampleProject", result);
        Assert.Contains("Framework", result);
        Assert.Contains("Files", result);
        Assert.Contains("Calculator.cs", result);
        Assert.Contains("Services.cs", result);
    }

    [Fact]
    public async Task WhenSourceFileProvidedThenFindsProjectAndShowsStructure()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.CalculatorFile);

        Assert.Contains("# Project: SampleProject", result);
        Assert.Contains("Calculator.cs", result);
    }

    [Fact]
    public async Task WhenProjectHasTypesThenShowsNamespaceTree()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.SampleProjectFile);

        Assert.Contains("Types", result);
        Assert.Contains("Calculator", result);
        Assert.Contains("StatisticsCalculator", result);
        Assert.Contains("IStringFormatter", result);
        // Should use kind badges
        Assert.Contains("[C]", result); // Class
        Assert.Contains("[I]", result); // Interface
    }

    [Fact]
    public async Task WhenProjectHasReferenceThenShowsAssemblyReferences()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.SampleProjectFile);

        Assert.Contains("Assembly References", result);
    }

    [Fact]
    public async Task WhenProjectUsesNet10ThenInfersFramework()
    {
        // SampleProject targets net10.0
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.SampleProjectFile);

        Assert.Contains("net10.0", result);
    }

    [Fact]
    public async Task WhenFilesGroupedByFolderThenShowsCompactListing()
    {
        var result = await RoslynMCP.Tools.ProjectStructureTool.GetProjectStructure(
            FixturePaths.SampleProjectFile);

        // Models subfolder should be listed
        Assert.Contains("Models", result);
        Assert.Contains("Result.cs", result);
    }

    [Fact]
    public void InferTargetFramework_WithNet10Symbols_ReturnsNet10()
    {
        // Test the internal InferTargetFramework method
        var result = RoslynMCP.Tools.ProjectStructureTool.InferTargetFramework(
            CreateProjectWithSymbols("NET10_0", "NET9_0_OR_GREATER", "NET8_0_OR_GREATER"));
        Assert.Equal("net10.0", result);
    }

    [Fact]
    public void InferTargetFramework_WithNetStandard20_ReturnsNetstandard20()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.InferTargetFramework(
            CreateProjectWithSymbols("NETSTANDARD2_0", "NETSTANDARD1_0_OR_GREATER"));
        Assert.Equal("netstandard2.0", result);
    }

    [Fact]
    public void InferTargetFramework_WithNoSymbols_ReturnsNull()
    {
        var result = RoslynMCP.Tools.ProjectStructureTool.InferTargetFramework(
            CreateProjectWithSymbols());
        Assert.Null(result);
    }

    private static Microsoft.CodeAnalysis.Project CreateProjectWithSymbols(params string[] symbols)
    {
        var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var parseOptions = new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions()
            .WithPreprocessorSymbols(symbols);
        var projectId = Microsoft.CodeAnalysis.ProjectId.CreateNewId();
        var projectInfo = Microsoft.CodeAnalysis.ProjectInfo.Create(
            projectId, Microsoft.CodeAnalysis.VersionStamp.Default, "TestProject", "TestProject",
            Microsoft.CodeAnalysis.LanguageNames.CSharp, parseOptions: parseOptions);
        workspace.AddProject(projectInfo);
        return workspace.CurrentSolution.GetProject(projectId)!;
    }
}

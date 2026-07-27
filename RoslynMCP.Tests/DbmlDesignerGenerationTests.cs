using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Services.Designers;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers LINQ to SQL designer regeneration through SqlMetal.
/// </summary>
/// <remarks>
/// SqlMetal stamps the generating runtime's version into its header, so the output is not stable
/// across machines. These tests assert structure rather than exact bytes.
/// </remarks>
public class DbmlDesignerGenerationTests
{
    [RequiresSqlMetalFact]
    public async Task WhenDbmlRegeneratedThenContextAndEntityTypesAreEmitted()
    {
        using var workspace = new AdhocWorkspace();
        var project = CreateProject(workspace, defaultNamespace: "DbmlProject");

        var result = await new DbmlDesignerGenerator()
            .GenerateAsync(FixturePaths.ShopDbmlFile, project, default);

        Assert.Empty(result.Errors);
        var content = Assert.IsType<string>(result.Content);

        // The model declares Class="ShopDataContext" with one Products table of type Product.
        Assert.Contains("partial class ShopDataContext", content);
        Assert.Contains("partial class Product", content);
        Assert.Contains("Products", content);
    }

    [RequiresSqlMetalFact]
    public async Task WhenDbmlOmitsNamespaceThenProjectDefaultNamespaceIsApplied()
    {
        using var workspace = new AdhocWorkspace();
        var project = CreateProject(workspace, defaultNamespace: "Contoso.Data");

        var result = await new DbmlDesignerGenerator()
            .GenerateAsync(FixturePaths.ShopDbmlFile, project, default);

        var content = Assert.IsType<string>(result.Content);

        // Shop.dbml sets no ContextNamespace/EntityNamespace, so Visual Studio's fallback applies.
        Assert.Contains("namespace Contoso.Data", content);
    }

    [Fact]
    public async Task WhenGeneratedThenNothingIsWrittenByTheGeneratorItself()
    {
        var designerPath = new DbmlDesignerGenerator().GetDesignerPath(FixturePaths.ShopDbmlFile);

        using var workspace = new AdhocWorkspace();
        await new DbmlDesignerGenerator()
            .GenerateAsync(FixturePaths.ShopDbmlFile, CreateProject(workspace, "DbmlProject"), default);

        // Writing is the regeneration service's job; the generator only returns content, so the
        // fixture directory must stay clean.
        Assert.False(File.Exists(designerPath));
    }

    private static Project CreateProject(AdhocWorkspace workspace, string defaultNamespace)
    {
        var projectId = ProjectId.CreateNewId();
        var info = ProjectInfo.Create(
                projectId, VersionStamp.Create(), "DbmlProject", "DbmlProject", LanguageNames.CSharp,
                filePath: Path.Combine(FixturePaths.DbmlProjectDir, "DbmlProject.csproj"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithDefaultNamespace(defaultNamespace);

        return workspace.CurrentSolution.AddProject(info).GetProject(projectId)!;
    }
}

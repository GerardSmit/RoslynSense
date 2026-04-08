using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Tests that validate legacy-format (non-SDK-style) .csproj support.
/// These tests require Visual Studio or Build Tools MSBuild to be installed.
/// </summary>
public class LegacyProjectTests
{
    [RequiresVisualStudioFact]
    public async Task WhenLegacyProjectOpenedThenWorkspaceLoadsSuccessfully()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.LegacyProjectFile);

        Assert.NotNull(project);
        Assert.NotEmpty(project.Documents);
    }

    [RequiresVisualStudioFact]
    public async Task WhenLegacyProjectOpenedThenDocumentsAreDiscovered()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.LegacyProjectFile);

        var documentNames = project.Documents.Select(d => Path.GetFileName(d.FilePath)).ToList();
        Assert.Contains("Calculator.cs", documentNames);
        Assert.Contains("Customer.cs", documentNames);
    }

    [RequiresVisualStudioFact]
    public async Task WhenLegacyProjectOpenedThenSymbolsAreResolvable()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.LegacyProjectFile);
        var compilation = await project.GetCompilationAsync();

        Assert.NotNull(compilation);
        var calculatorType = compilation!.GetTypeByMetadataName("LegacyProject.Calculator");
        Assert.NotNull(calculatorType);

        var addMethod = calculatorType!.GetMembers("Add").FirstOrDefault();
        Assert.NotNull(addMethod);
    }

    [RequiresVisualStudioFact]
    public async Task WhenLegacyProjectOpenedThenGoToDefinitionWorks()
    {
        var symbol = await RoslynTestHelpers.ResolveSymbolAsync(
            FixturePaths.LegacyCalculatorFile,
            "public int [|Add|](int a, int b)");

        Assert.Equal("Add", symbol.Name);
        Assert.Equal(Microsoft.CodeAnalysis.SymbolKind.Method, symbol.Kind);
    }

    [RequiresVisualStudioFact]
    public async Task WhenLegacyProjectOpenedThenCrossFileNavigationWorks()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.LegacyProjectFile);
        var compilation = await project.GetCompilationAsync();

        Assert.NotNull(compilation);
        var customerType = compilation!.GetTypeByMetadataName("LegacyProject.Models.Customer");
        Assert.NotNull(customerType);

        var ordersProperty = customerType!.GetMembers("Orders").FirstOrDefault();
        Assert.NotNull(ordersProperty);
    }

    [RequiresVisualStudioFact]
    public async Task WhenLegacyProjectOpenedThenFrameworkReferencesResolve()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.LegacyProjectFile);
        var compilation = await project.GetCompilationAsync();

        Assert.NotNull(compilation);

        // System.Xml.Linq is referenced in the legacy project
        var xdocumentType = compilation!.GetTypeByMetadataName("System.Xml.Linq.XDocument");
        Assert.NotNull(xdocumentType);
    }
}

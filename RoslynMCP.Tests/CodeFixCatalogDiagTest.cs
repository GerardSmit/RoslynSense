using Microsoft.CodeAnalysis;
using RoslynMCP.Lsp;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public class CodeFixCatalogTests
{
    [Fact]
    public async Task CatalogLoadsMefComposedProviders()
    {
        var document = await LspDocumentResolver.ResolveAsync(FixturePaths.CalculatorFile, default);
        Assert.NotNull(document);

        var workspace = document!.Project.Solution.Workspace;
        var fixes = CodeFixCatalog.GetCodeFixProviders(workspace);
        var refactorings = CodeFixCatalog.GetRefactoringProviders(workspace);
        Assert.True(fixes.Count > 0, $"fix providers: {fixes.Count}");
        Assert.True(refactorings.Count > 0, $"refactoring providers: {refactorings.Count}");
    }
}

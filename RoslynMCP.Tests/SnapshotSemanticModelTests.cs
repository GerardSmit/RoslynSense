using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.SemanticModelReuse;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

[Collection(SharedState.Name)]
public sealed class SnapshotSemanticModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedCallerTreeCannotReuseAModelFromBeforeADeclarationEdit(bool referencedProject)
    {
        using var workspace = new AdhocWorkspace(WorkspaceService.HostServices);
        var service = workspace.Services.GetRequiredService<ISemanticModelReuseWorkspaceService>();
        Assert.IsType<SnapshotSemanticModelService>(service);
        var consumerProject = ProjectId.CreateNewId();
        var modelProject = referencedProject ? ProjectId.CreateNewId() : consumerProject;
        var reference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var solution = workspace.CurrentSolution.AddProject(consumerProject, "Consumer", "Consumer", LanguageNames.CSharp)
            .WithProjectCompilationOptions(consumerProject, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(consumerProject, reference);
        if (referencedProject)
        {
            solution = solution.AddProject(modelProject, "Contracts", "Contracts", LanguageNames.CSharp)
                .WithProjectCompilationOptions(modelProject, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddMetadataReference(modelProject, reference)
                .AddProjectReference(consumerProject, new ProjectReference(modelProject));
        }
        var modelId = DocumentId.CreateNewId(modelProject);
        var consumerId = DocumentId.CreateNewId(consumerProject);
        const string consumer = "class Consumer { object Read(Product product) { return product.VersionOne; } }";
        solution = solution.AddDocument(modelId, "Product.cs", SourceText.From("public class Product { public int VersionOne; }"))
            .AddDocument(consumerId, "Consumer.cs", SourceText.From(consumer));
        var document = solution.GetDocument(consumerId)!;
        var root = await document.GetSyntaxRootAsync();
        var node = root!.FindToken(consumer.LastIndexOf("product", StringComparison.Ordinal)).Parent!;
        var before = await service.ReuseExistingSpeculativeModelAsync(document, node, default);
        Assert.NotEmpty(before.Compilation.GetTypeByMetadataName("Product")!.GetMembers("VersionOne"));
        Assert.Same(before, await service.ReuseExistingSpeculativeModelAsync(document, node, default));

        var changedDocument = solution.WithDocumentText(modelId,
            SourceText.From("public class Product { public string VersionTwo; }")).GetDocument(consumerId)!;
        Assert.Same(await document.GetSyntaxTreeAsync(), await changedDocument.GetSyntaxTreeAsync());
        var after = await service.ReuseExistingSpeculativeModelAsync(changedDocument, node, default);

        Assert.NotSame(before, after);
        Assert.NotEmpty(after.Compilation.GetTypeByMetadataName("Product")!.GetMembers("VersionTwo"));
        Assert.Empty(after.Compilation.GetTypeByMetadataName("Product")!.GetMembers("VersionOne"));
        Assert.Same(after, await service.ReuseExistingSpeculativeModelAsync(changedDocument, node, default));
    }
}

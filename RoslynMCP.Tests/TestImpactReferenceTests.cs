using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services.Testing;
using Xunit;

namespace RoslynMCP.Tests;

public class TestImpactReferenceTests
{
    [Fact]
    public async Task EditingATestSelectsItEvenWhenItHasNoCallersOrCoverage()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocuments(workspace, """
            namespace Demo;
            class Tests
            {
                [Xunit.Fact]
                public void Edited() { }

                [Xunit.Fact]
                public void Untouched() { }
            }
            """);

        var tests = await TestImpactService.FindTestsReachingAsync(document,
            new ChangedFile(document.FilePath!, [new LineRange(5, 5)]));

        var selected = Assert.Single(tests);
        Assert.Equal("Demo.Tests.Edited", selected.FullyQualifiedName);
        Assert.Equal(ImpactReason.TestChanged, selected.Reason);
        Assert.Equal(document.Project.FilePath, selected.ProjectPath);
    }

    [Fact]
    public async Task ReferenceWalkPassesThroughTraitHelpersAndRecognizesDerivedTestAttributes()
    {
        using var workspace = new AdhocWorkspace();
        var document = CreateDocuments(workspace, """
            namespace Demo;
            class Tests
            {
                [CustomFact]
                public void Runs() => Helper();

                [Xunit.Trait]
                private void Helper() => Product.Value();
            }
            class CustomFactAttribute : Xunit.FactAttribute { }
            """);
        var product = document.Project.Documents.Single(item => item.Name == "Product.cs");

        var tests = await TestImpactService.FindTestsReachingAsync(product, new ChangedFile(product.FilePath!, []));

        var selected = Assert.Single(tests);
        Assert.Equal("Demo.Tests.Runs", selected.FullyQualifiedName);
        Assert.Equal(ImpactReason.ReferencesChangedCode, selected.Reason);
    }

    private static Document CreateDocuments(AdhocWorkspace workspace, string tests)
    {
        string directory = Path.Combine(Path.GetTempPath(), "roslyn-sense-impact-reference-tests", Guid.NewGuid().ToString("N"));
        var project = ProjectId.CreateNewId();
        var document = DocumentId.CreateNewId(project);
        var solution = workspace.CurrentSolution
            .AddProject(project, "Tests", "Tests", LanguageNames.CSharp)
            .WithProjectFilePath(project, Path.Combine(directory, "Tests.csproj"))
            .WithProjectCompilationOptions(project, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(project, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument(DocumentId.CreateNewId(project), "Attributes.cs", SourceText.From("""
                namespace Xunit
                {
                    public class FactAttribute : System.Attribute { }
                    public class TraitAttribute : System.Attribute { }
                }
                """), filePath: Path.Combine(directory, "Attributes.cs"))
            .AddDocument(DocumentId.CreateNewId(project), "Product.cs", SourceText.From("""
                namespace Demo;
                public class Product { public static int Value() => 42; }
                """), filePath: Path.Combine(directory, "Product.cs"))
            .AddDocument(document, "Tests.cs", SourceText.From(tests), filePath: Path.Combine(directory, "Tests.cs"));
        return solution.GetDocument(document)!;
    }
}

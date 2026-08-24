using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using RoslynMCP.Services.ExternalSource;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// A dependency's source is only useful if the editor can answer questions about it. Reading it
/// is not enough — it has to resolve to a document with a semantic model behind it.
/// </summary>
[Collection(SharedState.Name)]
public class ExternalSourceProjectTests
{
    [Fact]
    public async Task WhenSourceWasFetchedThenItResolvesToADocumentWithSemantics()
    {
        string file = WriteFetchedFile(
            """
            namespace Example
            {
                public class Widget
                {
                    public string Describe() => "widget";
                }
            }
            """);

        var document = await WorkspaceService.FindDocumentAsync(file, default);

        Assert.NotNull(document);

        // The point of the project: a compilation, so hover and F12 have something to ask.
        var model = await document!.GetSemanticModelAsync(default);
        Assert.NotNull(model);

        var root = await document.GetSyntaxRootAsync(default);
        var declaration = root!.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
            .Single();

        var symbol = model!.GetDeclaredSymbol(declaration);
        Assert.Equal("Example.Widget", symbol!.ToDisplayString());
    }

    /// <summary>
    /// The return type is <c>string</c>, which is not in the file — it resolves only if the
    /// assembly's own references were carried into the project.
    /// </summary>
    [Fact]
    public async Task WhenTheFileNamesAFrameworkTypeThenItStillBinds()
    {
        string file = WriteFetchedFile(
            """
            namespace Example
            {
                public class Gadget
                {
                    public string Name => "gadget";
                }
            }
            """);

        var document = await WorkspaceService.FindDocumentAsync(file, default);
        var model = await document!.GetSemanticModelAsync(default);
        var root = await document.GetSyntaxRootAsync(default);

        var property = root!.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax>()
            .Single();

        var type = model!.GetTypeInfo(property.Type).Type;

        Assert.Equal("string", type!.ToDisplayString());
    }

    [Fact]
    public void WhenAFileHasNoSidecarThenItBelongsToNoProject()
    {
        string orphan = Path.Combine(
            ExternalSourceCache.ReferenceSourceDirectory, "orphan", "Nothing.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        File.WriteAllText(orphan, "class Nothing { }");

        Assert.Null(ExternalSourceProject.TryGetProjectPath(orphan));
    }

    /// <summary>Writes a file where a fetch would have put it, sidecar and all.</summary>
    private static string WriteFetchedFile(string text)
    {
        string directory = Path.Combine(
            ExternalSourceCache.ReferenceSourceDirectory, "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string file = Path.Combine(directory, "Fetched.cs");
        File.WriteAllText(file, text);

        ExternalSourceProject.Ensure(
            new ExternalSourceResult(
                ExternalSourceKind.ReferenceSource,
                typeof(object).Assembly.Location,
                file,
                [new LinePosition(0, 0)],
                Origin: "test"),
            "Example.Widget");

        return file;
    }
}

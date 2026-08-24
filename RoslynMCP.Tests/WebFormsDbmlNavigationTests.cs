using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Dbml;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Tests;

/// <summary>
/// Navigation from an <c>Eval</c> path whose property SqlMetal generated: the answer is the
/// <c>.dbml</c> line the property was written from, not the designer restating it.
/// </summary>
/// <remarks>
/// The gap these pin down: the markup handler resolves the bound member itself — the projection
/// binds the literal to <c>System.String</c>, so the property is reachable only from the item
/// type — and used to stop at the member's raw declaration, which is the designer. The
/// contributor pass that redirects the C# side has to run on every markup verb too, or the same
/// property answers differently depending on which file the gesture started in.
/// </remarks>
[Collection(SharedState.Name)]
public class WebFormsDbmlNavigationTests
{
    [Fact]
    public async Task DefinitionOnADbmlBackedEvalPathLandsInTheModel()
    {
        PublishPacks();

        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.SalesGridAspxFile), EvalReferenceCaret()),
            typeDefinition: false,
            default);

        // Single, and that is half of it: before the contribution this was one location too, and
        // it was Sales.designer.cs.
        var location = Assert.Single(locations);
        AssertFile(FixturePaths.SalesDbmlFile, location.Uri);
    }

    [Fact]
    public async Task ReferencesOnADbmlBackedEvalPathListTheModelAndNotTheDesigner()
    {
        PublishPacks();

        var locations = await AspxLanguageHandler.ReferencesAsync(
            new ReferenceParams(
                Doc(FixturePaths.SalesGridAspxFile), EvalReferenceCaret(),
                new ReferenceContext(IncludeDeclaration: true)),
            default);

        // The model line stands in for the declaration, and the designer's restatements of it —
        // the declaration Roslyn found — are withdrawn beside it.
        Assert.Contains(locations, location => SamePath(location.Uri, FixturePaths.SalesDbmlFile));
        Assert.DoesNotContain(
            locations, location => SamePath(location.Uri, FixturePaths.SalesDesignerFile));

        // The Eval path itself is a reference, and stays one.
        Assert.Contains(
            locations, location => SamePath(location.Uri, FixturePaths.SalesGridAspxFile));
    }

    /// <summary>
    /// Ctrl+F12 has no implementations to offer for a generated property, and its fallback is a
    /// definition — so it goes where the definition goes.
    /// </summary>
    [Fact]
    public async Task ImplementationOnADbmlBackedEvalPathFallsBackToTheModel()
    {
        PublishPacks();

        var locations = await AspxLanguageHandler.ImplementationAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.SalesGridAspxFile), EvalReferenceCaret()),
            default);

        var location = Assert.Single(locations);
        AssertFile(FixturePaths.SalesDbmlFile, location.Uri);
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static void PublishPacks() =>
        new LanguageRegistry(
            [new WebFormsLanguage(new MarkdownFormatter()), new DbmlLanguage()]).Publish();

    private static TextDocumentIdentifier Doc(string path) => new(LspConverters.PathToUri(path));

    private static Position EvalReferenceCaret() =>
        PositionOf(FixturePaths.SalesGridAspxFile, "Eval(\"Reference\")", "Eval(\"Refe".Length);

    private static Position PositionOf(string path, string needle, int offsetIntoNeedle)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");

        var line = SourceText.From(text).Lines.GetLinePosition(index + offsetIntoNeedle);
        return new Position(line.Line, line.Character);
    }

    private static bool SamePath(string uri, string path) =>
        string.Equals(
            Path.GetFullPath(LspConverters.UriToPath(uri)),
            Path.GetFullPath(path),
            StringComparison.OrdinalIgnoreCase);

    private static void AssertFile(string expected, string uri) =>
        Assert.Equal(
            Path.GetFullPath(expected),
            Path.GetFullPath(LspConverters.UriToPath(uri)),
            StringComparer.OrdinalIgnoreCase);
}

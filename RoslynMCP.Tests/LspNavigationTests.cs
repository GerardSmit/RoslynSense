using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>Drives the LSP feature handlers directly (no transport) against the fixture project.</summary>
public class LspNavigationTests
{
    [Fact]
    public async Task DefinitionResolvesMethodCallToDeclaration()
    {
        // Calculator.cs line 11 (1-based): `return new Result(Add(a, b), ...)` — the Add call.
        var p = await PositionOf(FixturePaths.CalculatorFile, "Add(a, b), Subtract");
        var locations = await NavigationHandlers.DefinitionAsync(p, typeDefinition: false, default);

        var location = Assert.Single(locations);
        Assert.EndsWith("Calculator.cs", LspConverters.UriToPath(location.Uri), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, location.Range.Start.Line); // `public int Add` on 1-based line 5
    }

    [Fact]
    public async Task ReferencesIncludeDeclarationAndCallSite()
    {
        var p = await PositionOf(FixturePaths.CalculatorFile, "Add(int a, int b)");
        var locations = await NavigationHandlers.ReferencesAsync(
            new ReferenceParams(p.TextDocument, p.Position, new ReferenceContext(IncludeDeclaration: true)),
            default);

        Assert.True(locations.Length >= 2, $"expected declaration + call site, got {locations.Length}");
    }

    [Fact]
    public async Task HoverShowsSignatureForMethod()
    {
        var p = await PositionOf(FixturePaths.CalculatorFile, "Add(int a, int b)");
        var hover = await HoverHandler.HoverAsync(p, default);

        Assert.NotNull(hover);
        Assert.Contains("Add", hover!.Contents.Value);
        Assert.Equal("markdown", hover.Contents.Kind);
    }

    [Fact]
    public async Task DocumentSymbolsReturnTypeWithMembers()
    {
        var symbols = await SymbolHandlers.DocumentSymbolsAsync(
            new DocumentSymbolParams(new TextDocumentIdentifier(
                LspConverters.PathToUri(FixturePaths.CalculatorFile))),
            default);

        var ns = Assert.Single(symbols);
        Assert.Equal("SampleProject", ns.Name);
        var calculator = Assert.Single(ns.Children);
        Assert.Equal("Calculator", calculator.Name);
        Assert.Equal(LspSymbolKind.Class, calculator.Kind);
        Assert.Contains(calculator.Children, c => c.Name == "Add");
        Assert.Contains(calculator.Children, c => c.Name == "Compute");
    }

    [Fact]
    public async Task RenameReturnsWorkspaceEditWithoutTouchingDisk()
    {
        string diskBefore = await File.ReadAllTextAsync(FixturePaths.CalculatorFile);

        var p = await PositionOf(FixturePaths.CalculatorFile, "Subtract(int a, int b)");
        var edit = await RenameHandler.RenameAsync(
            new RenameParams(p.TextDocument, p.Position, "Minus"), default);

        Assert.NotNull(edit);
        Assert.NotEmpty(edit!.Changes);
        var allEdits = edit.Changes.Values.SelectMany(e => e).ToList();
        Assert.True(allEdits.Count >= 2, "declaration + call site");
        Assert.All(allEdits, e => Assert.Equal("Minus", e.NewText));

        // LSP rename must never write to disk — the editor applies the WorkspaceEdit.
        Assert.Equal(diskBefore, await File.ReadAllTextAsync(FixturePaths.CalculatorFile));
    }

    /// <summary>Builds LSP position params pointing at the first character of
    /// <paramref name="anchor"/>'s first occurrence in <paramref name="filePath"/>.</summary>
    private static async Task<TextDocumentPositionParams> PositionOf(string filePath, string anchor)
    {
        string text = await File.ReadAllTextAsync(filePath);
        int index = text.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(index >= 0, $"anchor '{anchor}' not found in {filePath}");

        int line = 0, lineStart = 0;
        for (int i = 0; i < index; i++)
        {
            if (text[i] == '\n') { line++; lineStart = i + 1; }
        }

        return new TextDocumentPositionParams(
            new TextDocumentIdentifier(LspConverters.PathToUri(filePath)),
            new Position(line, index - lineStart));
    }
}

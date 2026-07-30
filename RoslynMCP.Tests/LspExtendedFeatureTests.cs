using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>foldingRange, rangeFormatting, call/type hierarchy, semanticTokens handlers.</summary>
public class LspExtendedFeatureTests
{
    [Fact]
    public async Task FoldingRangesCoverClassAndMethodBodies()
    {
        string uri = LspConverters.PathToUri(FixturePaths.CalculatorFile);
        var ranges = await FoldingRangeHandler.FoldingRangesAsync(
            new FoldingRangeParams(new TextDocumentIdentifier(uri)), default);

        Assert.NotEmpty(ranges);
        // Class body spans multiple lines; Compute's block body folds too.
        Assert.Contains(ranges, r => r.EndLine - r.StartLine >= 8); // class Calculator braces
        Assert.All(ranges, r => Assert.True(r.EndLine > r.StartLine));
    }

    [Fact]
    public async Task FoldingRangesIncludeCommentRuns()
    {
        string uri = LspConverters.PathToUri(FixturePaths.ServicesFile);
        var ranges = await FoldingRangeHandler.FoldingRangesAsync(
            new FoldingRangeParams(new TextDocumentIdentifier(uri)), default);

        // Services.cs has multi-line /// doc comments.
        Assert.Contains(ranges, r => r.Kind == FoldingRangeKind.Comment);
    }

    [Fact]
    public async Task RangeFormattingFixesIndentationInsideRangeOnly()
    {
        string path = FixturePaths.CalculatorFile;
        string uri = LspConverters.PathToUri(path);
        string original = await File.ReadAllTextAsync(path);
        string mangled = original.Replace("    public int Add(int a, int b) => a + b;",
            "            public int Add(int a, int b) => a + b;");
        Assert.NotEqual(original, mangled);

        string session = Guid.NewGuid().ToString("N");
        try
        {
            OpenDocumentStore.Open(session, path, SourceText.From(mangled), 1);

            var text = SourceText.From(mangled);
            int line = text.Lines.First(l => l.ToString().Contains("public int Add")).LineNumber;
            var edits = await FormattingHandler.FormatRangeAsync(
                new DocumentRangeFormattingParams(
                    new TextDocumentIdentifier(uri),
                    new RoslynMCP.Lsp.Protocol.Range(new Position(line, 0), new Position(line + 1, 0))),
                default);

            Assert.NotEmpty(edits);
            Assert.Contains(edits, e => e.Range.Start.Line == line);
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }
    }

    [Fact]
    public async Task CallHierarchyPrepareAndIncomingOutgoing()
    {
        string uri = LspConverters.PathToUri(FixturePaths.CalculatorFile);
        string text = await File.ReadAllTextAsync(FixturePaths.CalculatorFile);
        var (line, character) = PositionOf(text, "Add(int a, int b) => a + b;");

        var items = await CallHierarchyHandler.PrepareAsync(
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), new Position(line, character)),
            default);
        var add = Assert.Single(items);
        Assert.Equal("Add", add.Name);

        var incoming = await CallHierarchyHandler.IncomingCallsAsync(
            new CallHierarchyCallsParams(add), default);
        Assert.Contains(incoming, c => c.From.Name == "Compute" && c.FromRanges.Length > 0);

        // Outgoing from Compute: calls Add, Subtract, and the Result constructor.
        var (computeLine, computeChar) = PositionOf(text, "Compute(int a, int b)");
        var computeItems = await CallHierarchyHandler.PrepareAsync(
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), new Position(computeLine, computeChar)),
            default);
        var compute = Assert.Single(computeItems);

        var outgoing = await CallHierarchyHandler.OutgoingCallsAsync(
            new CallHierarchyCallsParams(compute), default);
        Assert.Contains(outgoing, c => c.To.Name == "Add");
        Assert.Contains(outgoing, c => c.To.Name == "Subtract");
    }

    [Fact]
    public async Task TypeHierarchySupertypesAndSubtypes()
    {
        string uri = LspConverters.PathToUri(FixturePaths.ServicesFile);
        string text = await File.ReadAllTextAsync(FixturePaths.ServicesFile);

        var (classLine, classChar) = PositionOf(text, "StatisticsCalculator : IStringFormatter");
        var classItems = await TypeHierarchyHandler.PrepareAsync(
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), new Position(classLine, classChar)),
            default);
        var calculator = Assert.Single(classItems);
        Assert.Equal("StatisticsCalculator", calculator.Name);

        var supertypes = await TypeHierarchyHandler.SupertypesAsync(
            new TypeHierarchyItemParams(calculator), default);
        Assert.Contains(supertypes, s => s.Name == "IStringFormatter");

        var (ifaceLine, ifaceChar) = PositionOf(text, "interface IStringFormatter");
        var ifaceItems = await TypeHierarchyHandler.PrepareAsync(
            new TextDocumentPositionParams(
                new TextDocumentIdentifier(uri),
                new Position(ifaceLine, ifaceChar + "interface ".Length + 1)),
            default);
        var formatter = Assert.Single(ifaceItems);

        var subtypes = await TypeHierarchyHandler.SubtypesAsync(
            new TypeHierarchyItemParams(formatter), default);
        Assert.Contains(subtypes, s => s.Name == "StatisticsCalculator");
    }

    [Fact]
    public async Task SemanticTokensProduceValidDeltaEncoding()
    {
        string uri = LspConverters.PathToUri(FixturePaths.CalculatorFile);
        var tokens = await SemanticTokensHandler.SemanticTokensFullAsync(
            new SemanticTokensParams(new TextDocumentIdentifier(uri)), default);

        Assert.True(tokens.Data.Length > 0);
        Assert.Equal(0, tokens.Data.Length % 5);

        for (int i = 0; i < tokens.Data.Length; i += 5)
        {
            Assert.True(tokens.Data[i] >= 0, "deltaLine must be non-negative");
            if (tokens.Data[i] == 0 && i > 0)
                Assert.True(tokens.Data[i + 1] >= 0, "same-line deltaStart must be non-negative");
            Assert.True(tokens.Data[i + 2] > 0, "token length must be positive");
            Assert.InRange(tokens.Data[i + 3], 0, SemanticTokensHandler.TokenTypes.Length - 1);
        }
    }

    private static (int Line, int Character) PositionOf(string text, string anchor)
    {
        int index = text.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(index >= 0, $"anchor '{anchor}' not found");
        int line = 0, lineStart = 0;
        for (int i = 0; i < index; i++)
        {
            if (text[i] == '\n') { line++; lineStart = i + 1; }
        }
        return (line, index - lineStart);
    }
}

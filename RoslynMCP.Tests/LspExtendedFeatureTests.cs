using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>foldingRange, rangeFormatting, call/type hierarchy, semanticTokens handlers.</summary>
[Collection(SharedState.Name)]
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
            "session", new SemanticTokensParams(new TextDocumentIdentifier(uri)), default);

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

    /// <summary>
    /// The distinction the client cannot make for itself. LSP's standard vocabulary has one
    /// <c>variable</c>, so a field, a local and a parameter arrive indistinguishable and every
    /// theme paints them one colour — which is what this asserts is no longer true.
    /// </summary>
    [Fact]
    public async Task SemanticTokensSeparateFieldsParametersAndProperties()
    {
        string text = await File.ReadAllTextAsync(FixturePaths.ServicesFile);
        var tokens = await SemanticTokensHandler.SemanticTokensFullAsync(
            "session",
            new SemanticTokensParams(new TextDocumentIdentifier(
                LspConverters.PathToUri(FixturePaths.ServicesFile))),
            default);

        string TypeAt(string anchor, int offsetInAnchor = 0)
        {
            var (line, character) = PositionOf(text, anchor);
            return TokenTypeAt(tokens.Data, line, character + offsetInAnchor);
        }

        Assert.Equal("field", TypeAt("_results.Add(result)"));
        Assert.Equal("parameter", TypeAt("result) => _results"));
        Assert.Equal("property", TypeAt("Sum)"));
        Assert.Equal("enumMember", TypeAt("Pending,"));
        Assert.Equal("interface", TypeAt("IStringFormatter"));
        Assert.Equal("method", TypeAt("AddResult(Result result)"));
    }

    /// <summary>
    /// The token types only mean something if the shipped theme has an opinion about them.
    /// Adding a type to the legend without adding it here is the easy way to reintroduce exactly
    /// the flat colouring the split was for.
    /// </summary>
    [Fact]
    public void ShippedThemeColoursEveryTokenTypeInTheLegend()
    {
        if (FixturePaths.VsCodeExtensionDir is not { } extension)
            return; // Running from output with no source tree above; nothing to read.

        using var theme = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(extension, "themes", "rider-islands-dark.json")));
        var colours = theme.RootElement.GetProperty("semanticTokenColors");

        var missing = SemanticTokensHandler.TokenTypes
            .Where(type => !colours.TryGetProperty(type, out _))
            .ToArray();

        Assert.True(missing.Length == 0, $"theme has no colour for: {string.Join(", ", missing)}");
    }

    /// <summary>Decodes the delta encoding far enough to name the token covering a position.</summary>
    private static string TokenTypeAt(int[] data, int line, int character)
    {
        int currentLine = 0, currentChar = 0;
        for (int i = 0; i < data.Length; i += 5)
        {
            currentLine += data[i];
            currentChar = data[i] == 0 ? currentChar + data[i + 1] : data[i + 1];

            if (currentLine == line
                && character >= currentChar
                && character < currentChar + data[i + 2])
            {
                return SemanticTokensHandler.TokenTypes[data[i + 3]];
            }
        }

        return $"(no token at {line}:{character})";
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

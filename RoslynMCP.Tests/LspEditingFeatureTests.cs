using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Tests;

/// <summary>
/// selectionRange, linkedEditingRange, inlineValue, and the semantic-token range and delta
/// requests â€” the editing-surface handlers added after the original LSP batch.
/// </summary>
[Collection(SharedState.Name)]
public class LspEditingFeatureTests
{
    [Fact]
    public async Task SelectionRangeWidensFromTokenOutwards()
    {
        string uri = LspConverters.PathToUri(FixturePaths.CalculatorFile);
        string text = await File.ReadAllTextAsync(FixturePaths.CalculatorFile);
        var (line, character) = PositionOf(text, "Add(a, b)");

        var ranges = await SelectionRangeHandler.SelectionRangesAsync(
            new SelectionRangeParams(
                new TextDocumentIdentifier(uri), [new Position(line, character)]),
            default);

        var chain = Assert.Single(ranges);

        // Each step must contain the one inside it, and the chain must actually get wider â€”
        // a chain of one is the failure mode this endpoint exists to avoid.
        int steps = 1;
        for (var step = chain; step.Parent is not null; step = step.Parent)
        {
            steps++;
            Assert.True(Contains(step.Parent.Range, step.Range),
                "each parent range must contain its child");
        }

        Assert.True(steps >= 4, $"expected the chain to widen through several nodes, got {steps}");
    }

    [Fact]
    public async Task LinkedEditingCoversEveryUseOfAParameter()
    {
        string uri = LspConverters.PathToUri(FixturePaths.CalculatorFile);
        string text = await File.ReadAllTextAsync(FixturePaths.CalculatorFile);

        // `a` in `Compute(int a, int b)` â€” declaration plus the two uses in the body.
        var (line, character) = PositionOf(text, "public Result Compute(int a");
        int column = character + "public Result Compute(int ".Length;

        var linked = await LinkedEditingHandler.RangesAsync(
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), new Position(line, column)),
            default);

        Assert.NotNull(linked);
        Assert.Equal(3, linked!.Ranges.Length);
        Assert.All(linked.Ranges, r => Assert.Equal(1, r.End.Character - r.Start.Character));
    }

    [Fact]
    public async Task LinkedEditingFollowsTheCaretAtTheEndOfTheName()
    {
        string uri = LspConverters.PathToUri(FixturePaths.CalculatorFile);
        string text = await File.ReadAllTextAsync(FixturePaths.CalculatorFile);

        // Against the end of `a`, which is where the caret sits while the name is being typed —
        // the position the feature exists for, and the one the token at the offset is not.
        var (line, character) = PositionOf(text, "public Result Compute(int a");
        int column = character + "public Result Compute(int a".Length;

        var linked = await LinkedEditingHandler.RangesAsync(
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), new Position(line, column)),
            default);

        Assert.NotNull(linked);
        Assert.Equal(3, linked!.Ranges.Length);
    }

    [Fact]
    public async Task PrepareRenameAtTheEndOfANameAnswersWithTheName()
    {
        string uri = LspConverters.PathToUri(FixturePaths.CalculatorFile);
        string text = await File.ReadAllTextAsync(FixturePaths.CalculatorFile);

        // `Add(` — the caret between the name and its paren, where the offset belongs to the paren.
        var (line, character) = PositionOf(text, "return new Result(Add(a, b)");
        int column = character + "return new Result(Add".Length;

        var prepared = await RenameHandler.PrepareRenameAsync(
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), new Position(line, column)),
            default);

        Assert.NotNull(prepared);

        // The placeholder is what the editor prefills the box with, so answering with the paren is
        // not a near miss — it is a rename box offering to call the method "(".
        Assert.Equal("Add", prepared!.Placeholder);
        Assert.Equal(column - "Add".Length, prepared.Range.Start.Character);
        Assert.Equal(column, prepared.Range.End.Character);
    }

    [Fact]
    public async Task PrepareRenameDeclinesAPositionThatNamesNothing()
    {
        string uri = LspConverters.PathToUri(FixturePaths.CalculatorFile);
        string text = await File.ReadAllTextAsync(FixturePaths.CalculatorFile);

        var (line, character) = PositionOf(text, "return new Result(Add(a, b)");
        int column = character + "return new Result(Add(a, b)".Length;

        var prepared = await RenameHandler.PrepareRenameAsync(
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), new Position(line, column)),
            default);

        Assert.Null(prepared);
    }

    [Fact]
    public async Task LinkedEditingDeclinesSymbolsVisibleOutsideTheFile()
    {
        string uri = LspConverters.PathToUri(FixturePaths.CalculatorFile);
        string text = await File.ReadAllTextAsync(FixturePaths.CalculatorFile);
        var (line, character) = PositionOf(text, "Add(int a, int b)");

        var linked = await LinkedEditingHandler.RangesAsync(
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), new Position(line, character)),
            default);

        // A public method has callers this file cannot see; rewriting it as you type would
        // break them silently.
        Assert.Null(linked);
    }

    [Fact]
    public async Task InlineValuesReportLocalsUpToTheStoppedLine()
    {
        string uri = LspConverters.PathToUri(FixturePaths.ServicesFile);
        string text = await File.ReadAllTextAsync(FixturePaths.ServicesFile);
        var (returnLine, _) = PositionOf(text, "return _results.Average");
        var (methodLine, _) = PositionOf(text, "public double ComputeAverageSum()");

        var values = await InlineValueHandler.InlineValuesAsync(
            new InlineValueParams(
                new TextDocumentIdentifier(uri),
                new Range(new Position(0, 0), new Position(returnLine + 5, 0)),
                new InlineValueContext(
                    1, new Range(new Position(returnLine, 0), new Position(returnLine, 0)))),
            default);

        Assert.NotEmpty(values);

        // `_results` is a field, so it is an expression to evaluate rather than a name to look
        // up in the frame's scopes.
        Assert.Contains(values, v =>
            v is InlineValueEvaluatableExpression { Expression: "_results" }
            or InlineValueVariableLookup { VariableName: "_results" });

        // Nothing outside the stopped frame's own method.
        foreach (var value in values)
        {
            var range = value switch
            {
                InlineValueVariableLookup lookup => lookup.Range,
                InlineValueEvaluatableExpression expression => expression.Range,
                _ => throw new InvalidOperationException("unexpected inline value shape"),
            };
            Assert.InRange(range.Start.Line, methodLine, returnLine);
        }
    }

    [Fact]
    public async Task InlineValuesAreEmptyOutsideAMethod()
    {
        string uri = LspConverters.PathToUri(FixturePaths.ServicesFile);

        // Line 0 is the namespace declaration: no frame, nothing to show.
        var values = await InlineValueHandler.InlineValuesAsync(
            new InlineValueParams(
                new TextDocumentIdentifier(uri),
                new Range(new Position(0, 0), new Position(3, 0)),
                new InlineValueContext(1, new Range(new Position(0, 0), new Position(0, 0)))),
            default);

        Assert.Empty(values);
    }

    [Fact]
    public async Task SemanticTokensRangeIsASubsetOfTheWholeFile()
    {
        string uri = LspConverters.PathToUri(FixturePaths.ServicesFile);
        string text = await File.ReadAllTextAsync(FixturePaths.ServicesFile);
        int lines = SourceText.From(text).Lines.Count;

        var full = await SemanticTokensHandler.SemanticTokensFullAsync(
            "range-session", new SemanticTokensParams(new TextDocumentIdentifier(uri)), default);
        var partial = await SemanticTokensHandler.SemanticTokensRangeAsync(
            new SemanticTokensRangeParams(
                new TextDocumentIdentifier(uri),
                new Range(new Position(0, 0), new Position(Math.Min(5, lines - 1), 0))),
            default);

        Assert.True(partial.Data.Length > 0);
        Assert.Equal(0, partial.Data.Length % 5);
        Assert.True(partial.Data.Length < full.Data.Length,
            "a five-line window must produce fewer tokens than the whole file");
        Assert.Null(partial.ResultId);
    }

    [Fact]
    public async Task SemanticTokensDeltaReportsOnlyWhatChanged()
    {
        string path = FixturePaths.ServicesFile;
        string uri = LspConverters.PathToUri(path);
        const string session = "delta-session";
        string original = await File.ReadAllTextAsync(path);

        var first = await SemanticTokensHandler.SemanticTokensFullAsync(
            session, new SemanticTokensParams(new TextDocumentIdentifier(uri)), default);
        Assert.NotNull(first.ResultId);

        string overlaySession = Guid.NewGuid().ToString("N");
        try
        {
            // One added statement at the end of a method: the tokens before it are unchanged,
            // so the edit must not span the file.
            string edited = original.Replace(
                "        if (_results.Count == 0) return 0;",
                "        if (_results.Count == 0) return 0;\n        var extraLocalName = 1;");
            Assert.NotEqual(original, edited);
            OpenDocumentStore.Open(overlaySession, path, SourceText.From(edited), 1);

            var delta = await SemanticTokensHandler.SemanticTokensDeltaAsync(
                session,
                new SemanticTokensDeltaParams(new TextDocumentIdentifier(uri), first.ResultId!),
                default);

            var edits = Assert.IsType<SemanticTokensDelta>(delta);
            var edit = Assert.Single(edits.Edits);
            Assert.True(edit.Start > 0, "the unchanged prefix must not be re-sent");
            Assert.NotNull(edits.ResultId);
            Assert.NotEqual(first.ResultId, edits.ResultId);
        }
        finally
        {
            OpenDocumentStore.Close(overlaySession, path);
        }
    }

    [Fact]
    public async Task SemanticTokensDeltaFallsBackToFullWhenTheBaselineIsUnknown()
    {
        string uri = LspConverters.PathToUri(FixturePaths.CalculatorFile);

        var result = await SemanticTokensHandler.SemanticTokensDeltaAsync(
            "unknown-session",
            new SemanticTokensDeltaParams(new TextDocumentIdentifier(uri), "no-such-result"),
            default);

        var tokens = Assert.IsType<SemanticTokens>(result);
        Assert.True(tokens.Data.Length > 0);
        Assert.NotNull(tokens.ResultId);
    }

    private static bool Contains(Range outer, Range inner) =>
        (outer.Start.Line < inner.Start.Line
            || (outer.Start.Line == inner.Start.Line && outer.Start.Character <= inner.Start.Character))
        && (outer.End.Line > inner.End.Line
            || (outer.End.Line == inner.End.Line && outer.End.Character >= inner.End.Character));

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

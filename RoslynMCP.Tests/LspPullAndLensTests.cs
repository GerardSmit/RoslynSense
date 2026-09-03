using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>Pull diagnostics, codeLens (test/references/inheritance), onAutoInsert,
/// executeCommand handlers.</summary>
[Collection(SharedState.Name)]
public class LspPullAndLensTests
{
    [Fact]
    public async Task PullDiagnosticsReportsSemanticErrors()
    {
        string uri = LspConverters.PathToUri(FixturePaths.BrokenSemanticFile);
        var report = Assert.IsType<FullDocumentDiagnosticReport>(await DiagnosticsHandler.PullAsync(
            new DocumentDiagnosticParams(new TextDocumentIdentifier(uri)), default));

        Assert.Equal("full", report.Kind);
        Assert.Contains(report.Items, d => d.Code == "CS0029");
    }

    [Fact]
    public async Task CodeLensMarksTestMethodsAndReferences()
    {
        string uri = LspConverters.PathToUri(FixturePaths.DebugCalculatorTestsFile);
        var lenses = await CodeLensHandler.CodeLensAsync(
            new CodeLensParams(new TextDocumentIdentifier(uri)), default);

        var runLenses = lenses.Where(l => l.Command?.Name == "roslynSense.runTest").ToArray();
        Assert.Equal(2, runLenses.Length);
        Assert.Contains(runLenses, l =>
            l.Command!.Arguments![0] is string fqn
            && fqn == "DebugTestProject.CalculatorTests.Add_ReturnsSum");

        // Every member also gets an unresolved reference lens.
        Assert.Contains(lenses, l => l.Command is null && l.Data is { Kind: "references" });
    }

    [Fact]
    public async Task CodeLensResolveCountsReferences()
    {
        string uri = LspConverters.PathToUri(FixturePaths.CalculatorFile);
        var lenses = await CodeLensHandler.CodeLensAsync(
            new CodeLensParams(new TextDocumentIdentifier(uri)), default);

        string text = await File.ReadAllTextAsync(FixturePaths.CalculatorFile);
        var (addLine, _) = PositionOf(text, "Add(int a, int b)");
        var addLens = lenses.Single(l =>
            l.Data is { Kind: "references" } && l.Range.Start.Line == addLine);

        var resolved = await CodeLensHandler.ResolveAsync(addLens, default);
        Assert.NotNull(resolved.Command);
        Assert.Equal("roslynSense.showReferences", resolved.Command!.Name);
        // Add is called from Compute and ManyUsages — multiple references.
        Assert.Matches(@"^\d+ references?$", resolved.Command.Title);
        Assert.NotEqual("0 references", resolved.Command.Title);
        Assert.NotEqual("1 reference", resolved.Command.Title);
    }

    [Fact]
    public async Task OnAutoInsertGeneratesDocSkeletonWithParams()
    {
        string path = FixturePaths.CalculatorFile;
        string original = await File.ReadAllTextAsync(path);
        string marker = "    public int Add(int a, int b) => a + b;";
        string modified = original.Replace(marker, "    ///\r\n" + marker);
        Assert.NotEqual(original, modified);

        string session = Guid.NewGuid().ToString("N");
        try
        {
            OpenDocumentStore.Open(session, path, SourceText.From(modified), 1);

            var (line, _) = PositionOf(modified, "///");
            var result = await OnAutoInsertHandler.OnAutoInsertAsync(
                new OnAutoInsertParams(
                    new TextDocumentIdentifier(LspConverters.PathToUri(path)),
                    new Position(line, 7)), // caret right after "    ///"
                default);

            Assert.NotNull(result);
            Assert.Contains("<summary>", result!.Edit.NewText);
            Assert.Contains("<param name=\"a\">", result.Edit.NewText);
            Assert.Contains("<param name=\"b\">", result.Edit.NewText);
            Assert.Contains("<returns>", result.Edit.NewText);
            Assert.Equal(line + 1, result.Cursor.Line);
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }
    }

    [Fact]
    public async Task OnAutoInsertSkipsAlreadyDocumentedMembers()
    {
        // Services.cs members already have /// docs; typing another "///" inside them
        // must not double-generate. Simulate: caret on an existing doc line.
        string path = FixturePaths.ServicesFile;
        string text = await File.ReadAllTextAsync(path);
        var (line, character) = PositionOf(text, "/// Formats a value for display.");

        var result = await OnAutoInsertHandler.OnAutoInsertAsync(
            new OnAutoInsertParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(path)),
                new Position(line, character + 3)),
            default);

        Assert.Null(result);
    }

    [Fact]
    public async Task InlayHintsShowVarTypesAndParameterNames()
    {
        // CalculatorTests fixture: `var a = 3;` (type hint "int") and Assert.Equal(8, ...)
        // (literal argument → parameter-name hint).
        string uri = LspConverters.PathToUri(FixturePaths.DebugCalculatorTestsFile);
        string text = await File.ReadAllTextAsync(FixturePaths.DebugCalculatorTestsFile);
        int lineCount = text.Count(c => c == '\n') + 1;

        var hints = await InlayHintHandler.InlayHintsAsync(
            new InlayHintParams(
                new TextDocumentIdentifier(uri),
                new RoslynMCP.Lsp.Protocol.Range(new Position(0, 0), new Position(lineCount - 1, 0))),
            default);

        Assert.NotEmpty(hints);
        Assert.Contains(hints, h => h.Kind == 1 && h.Label == "int");
        Assert.Contains(hints, h => h.Kind == 2 && h.Label.EndsWith(":"));
    }

    [Fact]
    public async Task ExecuteCommandRejectsUnknownCommand()
    {
        // Commands return heterogeneous payloads now (build returns a structured result), so
        // the handler's contract is object; the unknown-command case is still a message.
        var result = await ExecuteCommandHandler.ExecuteAsync(
            new ExecuteCommandParams("does.not.exist", null), default);
        Assert.Contains("Unknown command", Assert.IsType<string>(result));
    }

    /// <summary>
    /// A project that has been built answers in full on the first pull, refresh support or not.
    /// </summary>
    /// <remarks>
    /// The regression this pins: gating the list on a cached semantic model looks reasonable and
    /// is not, because an edit throws that cache away and the gate then misses on every keystroke.
    /// The inheritance lenses flickered out of a warm file constantly, which is how a lens the
    /// user meant to click stops being there by the time they click it.
    /// </remarks>
    [Fact]
    public async Task ABuiltProjectGetsItsInheritanceLensesOnTheFirstPull()
    {
        string uri = LspConverters.PathToUri(FixturePaths.ServicesFile);
        var request = new CodeLensParams(new TextDocumentIdentifier(uri));
        CodeLensHandler.ClearWarmupState();

        var lenses = await CodeLensHandler.CodeLensAsync(request, default, clientRefreshes: true);

        Assert.Contains(lenses, lens => lens.Command?.Name == "roslynSense.showInheritanceAt");
        Assert.Contains(lenses, lens => lens.Data is { Kind: "references" });
    }

    /// <summary>
    /// Nothing in a lens list is a lens the editor cannot act on: every entry either carries a
    /// command it can run or the data to resolve one. A command with an empty title still draws —
    /// as a bare separator beside its neighbours — and swallows the click.
    /// </summary>
    [Fact]
    public async Task CodeLensDrawsNothingItCannotAnswer()
    {
        foreach (string path in new[]
                 { FixturePaths.ServicesFile, FixturePaths.CalculatorFile,
                   FixturePaths.DebugCalculatorTestsFile })
        {
            var lenses = await CodeLensHandler.CodeLensAsync(
                new CodeLensParams(new TextDocumentIdentifier(LspConverters.PathToUri(path))),
                default,
                clientRefreshes: true);

            Assert.All(lenses, lens => Assert.True(
                lens.Command is { Name.Length: > 0, Title.Length: > 0 } || lens.Data is not null,
                $"{Path.GetFileName(path)} has a lens that renders and cannot act"));
        }
    }

    /// <summary>
    /// And a lens the editor cannot answer stays uncommanded rather than claiming a number.
    /// A position that no longer resolves is a stale lens, not a symbol with no references.
    /// </summary>
    [Fact]
    public async Task ResolvingAStalePositionCommandsNothing()
    {
        string uri = LspConverters.PathToUri(FixturePaths.CalculatorFile);
        string text = await File.ReadAllTextAsync(FixturePaths.CalculatorFile);
        int past = text.Count(c => c == '\n') + 500;

        var stale = new RoslynMCP.Lsp.Protocol.CodeLens(
            new RoslynMCP.Lsp.Protocol.Range(new Position(past, 0), new Position(past, 1)),
            Command: null)
        {
            Data = new CodeLensData(uri, past, 0, "references"),
        };

        var resolved = await CodeLensHandler.ResolveAsync(stale, default);

        Assert.Null(resolved.Command);
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

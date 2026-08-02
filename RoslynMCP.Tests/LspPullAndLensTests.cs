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

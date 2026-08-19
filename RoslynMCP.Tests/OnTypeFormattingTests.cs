using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// textDocument/onTypeFormatting, and the one thing it must never do.
/// </summary>
[Collection(SharedState.Name)]
public class OnTypeFormattingTests
{
    [Fact]
    public void NewlineIsATriggerOnlyBecauseTheBraceOneGetsCancelled()
    {
        // Registering newline once made every Enter send a request whose only possible answer
        // was to delete the indentation the editor had just inserted. It is back because the
        // editor drops the "{" format the moment Enter arrives, and "{" then Enter is how a
        // block gets opened — but only for that, which is what the tests below pin down.
        var server = new LspServer(new EmptyServices());

        var options = server
            .Initialize(new InitializeParams(null, null, null, null))
            .Capabilities.DocumentOnTypeFormattingProvider;

        Assert.NotNull(options);
        Assert.Contains("\n", options!.MoreTriggerCharacter);
        Assert.Contains("{", options.MoreTriggerCharacter);
    }

    [Fact]
    public async Task PressingEnterAfterAnOpenBraceStillMovesIt()
    {
        // The editor's own Enter handling has already put the caret one level in; all that is
        // left is the brace the cancelled "{" request never got to move.
        string result = await TypeAsync("\n", "        if (true) {\r\n            $$");

        Assert.Contains("        if (true)\r\n        {", result, StringComparison.Ordinal);
        // And the line the caret is on keeps the indentation the editor gave it.
        Assert.Contains("        {\r\n            ", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PressingEnterAfterAStatementChangesNothing()
    {
        string path = FixturePaths.CalculatorFile;
        string uri = LspConverters.PathToUri(path);
        string original = await File.ReadAllTextAsync(path);

        // The state the editor is in when it asks: the newline is in, and so is the indent it
        // carried over. An edit that touches this line is the bug.
        const string anchor = "        return new Result(Add(a, b), Subtract(a, b));";
        Assert.Contains(anchor, original);
        string typed = original.Replace(anchor, anchor + "\n        ");

        var text = SourceText.From(typed);
        int caretLine = text.Lines.First(l => l.ToString() == "        ").LineNumber;

        string session = Guid.NewGuid().ToString("N");
        try
        {
            OpenDocumentStore.Open(session, path, text, 1);

            var edits = await FormattingHandler.FormatOnTypeAsync(
                new DocumentOnTypeFormattingParams(
                    new TextDocumentIdentifier(uri),
                    new Position(caretLine, 8),
                    "\n"),
                default);

            Assert.Empty(edits);
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }
    }

    [Fact]
    public async Task FormattingAcrossAFreshLineWouldDeleteItsIndentation()
    {
        // What the newline trigger used to do, inlined: format the span from the previous line
        // through the caret's. This is the reason the trigger is gone, and it is asserted rather
        // than described because the next person to think "we should re-indent on Enter" needs
        // to see the formatter's answer, not be told about it.
        string path = FixturePaths.CalculatorFile;
        string original = await File.ReadAllTextAsync(path);

        const string anchor = "        return new Result(Add(a, b), Subtract(a, b));";
        var text = SourceText.From(original.Replace(anchor, anchor + "\n        "));
        int caretLine = text.Lines.First(l => l.ToString() == "        ").LineNumber;

        string session = Guid.NewGuid().ToString("N");
        try
        {
            OpenDocumentStore.Open(session, path, text, 1);
            var document = await LspDocumentResolver.ResolveAsync(path, default);
            Assert.NotNull(document);

            var span = TextSpan.FromBounds(
                text.Lines[caretLine - 1].Start, text.Lines[caretLine].End);
            var formatted = await Formatter.FormatAsync(document!, span, cancellationToken: default);
            var changes = await formatted.GetTextChangesAsync(document!, default);

            // An edit that replaces the caret line's whitespace with nothing — the caret ends
            // up in column zero.
            var caretSpan = text.Lines[caretLine].Span;
            Assert.Contains(changes, c =>
                c.Span.IntersectsWith(caretSpan) && string.IsNullOrEmpty(c.NewText?.TrimStart('\r', '\n')));
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }
    }

    [Fact]
    public async Task ASemicolonStillFormatsTheStatementItEnded()
    {
        string path = FixturePaths.CalculatorFile;
        string uri = LspConverters.PathToUri(path);
        string original = await File.ReadAllTextAsync(path);

        const string anchor = "        return new Result(Add(a, b), Subtract(a, b));";
        string mangled = original.Replace(
            anchor, "                    return new Result(Add(a, b),Subtract(a, b));");

        var text = SourceText.From(mangled);
        var line = text.Lines.First(l => l.ToString().Contains("return new Result"));

        string session = Guid.NewGuid().ToString("N");
        try
        {
            OpenDocumentStore.Open(session, path, text, 1);

            var edits = await FormattingHandler.FormatOnTypeAsync(
                new DocumentOnTypeFormattingParams(
                    new TextDocumentIdentifier(uri),
                    new Position(line.LineNumber, line.ToString().TrimEnd().Length),
                    ";"),
                default);

            Assert.NotEmpty(edits);
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }
    }

    [Fact]
    public async Task TypingAnOpenBraceMovesItBelowTheStatement()
    {
        string result = await TypeAsync("{", "        if (true) {$$");

        Assert.Contains("        if (true)\r\n        {", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOpenBraceLandsAtTheIndentOfWhatItOpens()
    {
        string result = await TypeAsync("{", "        if (true)\r\n            {$$");

        Assert.Contains("        if (true)\r\n        {", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Why the formatted spans stop at the braces: the <c>try</c> nested in the body is not
    /// what was being typed on, and closing the <c>if</c> around it must not rewrite it.
    /// </summary>
    [Fact]
    public async Task ClosingABraceLeavesTheBodyAlone()
    {
        string result = await TypeAsync("}", string.Join("\r\n",
            "        if (true) {",
            "            try",
            "            {}",
            "            catch",
            "            {}",
            "        }$$"));

        Assert.Contains("        if (true)\r\n        {", result, StringComparison.Ordinal);
        Assert.Contains("            try\r\n            {}", result, StringComparison.Ordinal);
        Assert.Contains("            catch\r\n            {}", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABraceInsideAStringIsNotABrace()
    {
        string result = await TypeAsync("{", "        var s = \"{$$\";");

        Assert.Contains("        var s = \"{\";", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs the handler over Calculator.cs with <paramref name="body"/> spliced into Compute —
    /// "$$" marking where the caret is, just past the character that was typed — and returns
    /// the text the edits it answered with produce.
    /// </summary>
    private static async Task<string> TypeAsync(string character, string body)
    {
        string path = FixturePaths.CalculatorFile;
        string original = await File.ReadAllTextAsync(path);

        const string anchor = "        return new Result(Add(a, b), Subtract(a, b));";
        int caretInBody = body.IndexOf("$$", StringComparison.Ordinal);
        Assert.True(caretInBody >= 0, "the body has to say where the caret is");
        string typed = original.Replace(anchor, body.Replace("$$", ""));
        Assert.NotEqual(original, typed);

        var text = SourceText.From(typed);
        int caret = typed.IndexOf(body[..caretInBody], StringComparison.Ordinal) + caretInBody;
        var position = text.Lines.GetLinePosition(caret);

        string session = Guid.NewGuid().ToString("N");
        try
        {
            OpenDocumentStore.Open(session, path, text, 1);

            var edits = await FormattingHandler.FormatOnTypeAsync(
                new DocumentOnTypeFormattingParams(
                    new TextDocumentIdentifier(LspConverters.PathToUri(path)),
                    new Position(position.Line, position.Character),
                    character),
                default);

            return text.WithChanges(edits.Select(e => new TextChange(
                LspConverters.ToTextSpan(text, e.Range), e.NewText))).ToString();
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
        }
    }

    /// <summary>Initialize reads no services; this exists only to satisfy the constructor.</summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

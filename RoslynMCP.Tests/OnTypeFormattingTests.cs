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
    public void NewlineIsNotAdvertisedAsATrigger()
    {
        // The capability is what decides whether the editor asks at all. Registering newline
        // made every Enter send a request whose only possible answer was to delete the
        // indentation the editor had just inserted.
        var server = new LspServer(new EmptyServices());

        var options = server
            .Initialize(new InitializeParams(null, null, null, null))
            .Capabilities.DocumentOnTypeFormattingProvider;

        Assert.NotNull(options);
        Assert.NotEqual("\n", options!.FirstTriggerCharacter);
        Assert.DoesNotContain("\n", options.MoreTriggerCharacter);
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

    /// <summary>Initialize reads no services; this exists only to satisfy the constructor.</summary>
    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

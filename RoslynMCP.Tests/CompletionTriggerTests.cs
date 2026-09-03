using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using CompletionItem = RoslynMCP.Lsp.Protocol.CompletionItem;

namespace RoslynMCP.Tests;

/// <summary>
/// Which typed character opens the list. The server advertises the characters; Roslyn's own
/// ShouldTriggerCompletion decides whether the request that follows gets an answer, so a
/// character is only worth advertising if these round trips return something.
/// </summary>
[Collection(SharedState.Name)]
public class CompletionTriggerTests
{
    /// <summary>
    /// The open paren of a keyword's condition. VS and Rider open the list here, and before "("
    /// was advertised the editor never asked — the condition of an if was the one expression
    /// position with no completion at all.
    /// </summary>
    [Theory]
    [InlineData("if ($) { }")]
    [InlineData("while ($) { }")]
    [InlineData("switch ($) { }")]
    [InlineData("lock ($) { }")]
    // A call's first argument: the same keystroke that opens signature help.
    [InlineData("var max = System.Math.Max($, 1);")]
    public async Task AnOpenParenOpensTheList(string statement)
    {
        var items = await CompleteAsync(statement, "(");

        Assert.NotEmpty(items);
        // Locals in scope, not just the keyword list a syntax-only fallback would produce.
        Assert.Contains(items, item => item.Label == "seed");
    }

    /// <summary>
    /// The indexer's bracket, which was already advertised — here to keep the paren's arrival from
    /// being the only covered case.
    /// </summary>
    [Fact]
    public async Task AnOpenBracketOpensTheList()
    {
        Assert.NotEmpty(await CompleteAsync("var value = numbers[$];", "["));
    }

    /// <summary>
    /// Roslyn declines a bare comma — it wants the space that follows it — so advertising "," would
    /// buy an argument-position list that never arrives, and cost a request per comma typed.
    /// </summary>
    [Fact]
    public async Task ACommaIsDeclinedRatherThanAnswered()
    {
        Assert.Empty(await CompleteAsync("var max = System.Math.Max(seed,$);", ","));
    }

    /// <summary>
    /// Completion runs the statement through the real handler, with the trigger the editor would
    /// send, and answers from the caret at the end of it.
    /// </summary>
    private static async Task<CompletionItem[]> CompleteAsync(string statement, string trigger)
    {
        // The caret sits where "$" is: the editor closes the paren as it is typed, so the request
        // arrives against a statement that still parses.
        int caret = statement.IndexOf('$', StringComparison.Ordinal);
        statement = statement.Replace("$", "");
        string source = $$"""
            public class TriggerProbe
            {
                public void Probe(int seed, int[] numbers)
                {
                    {{statement}}
                }
            }
            """;

        string path = FixturePaths.CalculatorFile;
        string sessionId = $"trigger-{Guid.NewGuid():N}";
        var text = SourceText.From(source);

        OpenDocumentStore.Open(sessionId, path, text, version: 1);
        try
        {
            int offset = source.IndexOf(statement, StringComparison.Ordinal) + caret;
            var position = text.Lines.GetLinePosition(offset);

            var list = await CompletionHandler.CompletionAsync(
                new CompletionParams(
                    new TextDocumentIdentifier(LspConverters.PathToUri(path)),
                    new Position(position.Line, position.Character),
                    new LspCompletionContext(TriggerKind: 2, trigger)),
                new LspResolveCache(),
                default);

            return list.Items;
        }
        finally
        {
            OpenDocumentStore.Close(sessionId, path);
        }
    }
}

using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>Lazy resolve endpoints: codeAction/resolve and completionItem/resolve.</summary>
public class LspResolveTests
{
    [Fact]
    public async Task CodeActionsReturnTitlesOnlyAndResolveComputesEdit()
    {
        // `return 123;` in a string-returning method: CS0029 with cast/change-return-type fixes.
        string uri = LspConverters.PathToUri(FixturePaths.BrokenSemanticFile);
        string text = await File.ReadAllTextAsync(FixturePaths.BrokenSemanticFile);
        var (line, character) = PositionOf(text, "return 123;");

        var cache = new LspResolveCache();
        var actions = await CodeActionHandler.CodeActionsAsync(
            new CodeActionParams(
                new TextDocumentIdentifier(uri),
                new RoslynMCP.Lsp.Protocol.Range(
                    new Position(line, character), new Position(line, character + 11)),
                new CodeActionContext([])),
            cache, default);

        Assert.NotEmpty(actions);
        Assert.All(actions, a =>
        {
            Assert.Null(a.Edit); // lazy: no edits in the initial response
            Assert.NotNull(a.Data);
        });

        // At least one action must resolve to a concrete workspace edit.
        bool anyEdit = false;
        foreach (var action in actions)
        {
            var resolved = await CodeActionHandler.ResolveAsync(action, cache, default);
            if (resolved.Edit is { Changes.Count: > 0 })
            {
                anyEdit = true;
                break;
            }
        }
        Assert.True(anyEdit, "no code action resolved to an edit");
    }

    [Fact]
    public async Task CompletionItemsCarryResolveDataAndResolveSucceeds()
    {
        string uri = LspConverters.PathToUri(FixturePaths.CalculatorFile);
        string text = await File.ReadAllTextAsync(FixturePaths.CalculatorFile);
        var (line, character) = PositionOf(text, "Add(a, b), Subtract");

        var cache = new LspResolveCache();
        var list = await CompletionHandler.CompletionAsync(
            new CompletionParams(new TextDocumentIdentifier(uri), new Position(line, character + 1)),
            cache, default);

        Assert.NotEmpty(list.Items);
        Assert.All(list.Items, i => Assert.NotNull(i.Data));

        var method = list.Items.FirstOrDefault(i => i.Label == "Add") ?? list.Items[0];
        var resolved = await CompletionHandler.ResolveAsync(method, cache, default);
        Assert.Equal(method.Label, resolved.Label);
        // Documentation is best-effort (fixture has no XML docs) — resolving must not throw
        // and must round-trip the item.
    }

    [Fact]
    public async Task ResolveWithStaleDataReturnsItemUnchanged()
    {
        var cache = new LspResolveCache();
        var stale = new RoslynMCP.Lsp.Protocol.CodeAction("stale", "quickfix", null)
        {
            Data = new CodeActionData(999),
        };
        var resolved = await CodeActionHandler.ResolveAsync(stale, cache, default);
        Assert.Null(resolved.Edit);
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

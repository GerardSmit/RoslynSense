using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>Lazy resolve endpoints: codeAction/resolve and completionItem/resolve.</summary>
[Collection(SharedState.Name)]
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
    public async Task CompletionIncludesUnimportedTypesForTypedPrefix()
    {
        // Calculator.cs has no 'using System.Text' — StringBuilder can only appear via
        // import completion. With a typed prefix the prefix-ranked list must surface it
        // even though the raw list exceeds the item cap.
        string path = FixturePaths.CalculatorFile;
        string original = await File.ReadAllTextAsync(path);
        string anchor = "return new Result(Add(a, b), Subtract(a, b));";
        string modified = original.Replace(anchor, "StringB\r\n        " + anchor);
        Assert.NotEqual(original, modified);

        string session = Guid.NewGuid().ToString("N");
        try
        {
            RoslynMCP.Services.OpenDocumentStore.Open(session, path,
                Microsoft.CodeAnalysis.Text.SourceText.From(modified), 1);

            // Completion serves the import-completion index but never builds it on the request
            // thread (that was the post-edit stall); build it here the deterministic way, standing
            // in for ImportCompletionWarmer's background queue.
            var document = await LspDocumentResolver.ResolveAsync(path, default);
            Assert.NotNull(document);
            await Microsoft.CodeAnalysis.Completion.Providers.AbstractTypeImportCompletionService
                .BatchUpdateCacheAsync(
                    Microsoft.CodeAnalysis.Collections.ImmutableSegmentedList.Create(document!.Project),
                    default);

            var (line, character) = PositionOf(modified, "StringB");
            var cache = new LspResolveCache();
            var list = await CompletionHandler.CompletionAsync(
                new CompletionParams(
                    new TextDocumentIdentifier(LspConverters.PathToUri(path)),
                    new Position(line, character + "StringB".Length)),
                cache, default);

            Assert.Contains(list.Items, i => i.Label == "StringBuilder");
        }
        finally
        {
            RoslynMCP.Services.OpenDocumentStore.Close(session, path);
            // Close's reconcile runs on a background task; settle it here so the next test's
            // disk-computed positions meet a workspace already restored to the disk text.
            await RoslynMCP.Services.WorkspaceService.ReconcileOpenBufferAsync(path);
        }
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

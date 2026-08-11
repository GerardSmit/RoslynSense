using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using Range = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Tests;

/// <summary>
/// The escape hatch for a rule you disagree with: suppress it, or change its severity, without
/// leaving the editor.
/// </summary>
[Collection(SharedState.Name)]
public class SuppressionCodeActionTests
{
    [Fact]
    public void ConfigurationFixProvidersLoadFromTheFeaturesAssemblies()
    {
        // These are internal Roslyn exports reached through the publicized reference. A Roslyn
        // upgrade that moves or renames them fails here rather than silently removing every
        // suppression action from the lightbulb.
        var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace(
            Microsoft.CodeAnalysis.Host.Mef.MefHostServices.DefaultHost);

        var providers = CodeFixCatalog.GetConfigurationFixProviders(workspace);

        Assert.NotEmpty(providers);
    }

    [Fact]
    public async Task LightbulbOffersSuppressAndConfigureForAWarning()
    {
        string path = FixturePaths.WarningsFile;
        string uri = LspConverters.PathToUri(path);
        string text = await File.ReadAllTextAsync(path);

        // `int x = 42;` is assigned and never used — CS0219.
        int line = LineOf(text, "int x = 42;");

        var actions = await CodeActionHandler.CodeActionsAsync(
            new CodeActionParams(
                new TextDocumentIdentifier(uri),
                new Range(new Position(line, 0), new Position(line, 20)),
                new CodeActionContext([])),
            new LspResolveCache(),
            default);

        Assert.NotEmpty(actions);
        Assert.Contains(actions, a =>
            a.Title.Contains("Suppress", StringComparison.OrdinalIgnoreCase)
            || a.Title.Contains("Configure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ASuppressionActionResolvesToARealEdit()
    {
        string path = FixturePaths.WarningsFile;
        string uri = LspConverters.PathToUri(path);
        string text = await File.ReadAllTextAsync(path);
        int line = LineOf(text, "int x = 42;");

        var cache = new LspResolveCache();
        var actions = await CodeActionHandler.CodeActionsAsync(
            new CodeActionParams(
                new TextDocumentIdentifier(uri),
                new Range(new Position(line, 0), new Position(line, 20)),
                new CodeActionContext([])),
            cache,
            default);

        var pragma = actions.FirstOrDefault(a =>
            a.Title.Contains("pragma", StringComparison.OrdinalIgnoreCase));

        // Not every Roslyn version words this the same way; when the pragma action is present
        // it has to actually produce an edit, because an action that resolves to nothing is a
        // menu entry that does nothing when clicked.
        if (pragma is null)
            return;

        var resolved = await CodeActionHandler.ResolveAsync(pragma, cache, default);

        Assert.NotNull(resolved.Edit);
        Assert.NotEmpty(resolved.Edit!.Changes);
    }

    /// <summary>
    /// The flattening is what turned three severity families into fifteen lightbulb entries. A
    /// client that implements the picker gets one entry per family instead, and nothing that
    /// reads as a flattened child.
    /// </summary>
    [Fact]
    public async Task AGroupCollapsesToOneEntryForAClientThatCanPickFromIt()
    {
        var (actions, _) = await LightbulbAsync(clientPicksNestedActions: true);

        var groups = actions
            .Where(a => a.Command?.Name == CodeActionHandler.PickNestedActionCommand)
            .ToList();

        Assert.NotEmpty(groups);
        Assert.DoesNotContain(actions, a => a.Title.Contains(" • ", StringComparison.Ordinal));

        foreach (var group in groups)
        {
            // The picker is handed the tree and nothing else — no document, no range — so
            // everything it needs to reach a leaf has to be in this one argument.
            var payload = Assert.IsType<NestedCodeActionGroup>(Assert.Single(group.Command!.Arguments!));
            Assert.NotEmpty(payload.Children ?? []);
            Assert.Null(group.Data);
        }
    }

    /// <summary>
    /// A leaf resolves through exactly the request the editor sends for an ordinary entry: its
    /// cached id and nothing else. If that stops working the group opens and then does nothing,
    /// which is worse than the long menu it replaced.
    /// </summary>
    [Fact]
    public async Task EveryLeafOfACollapsedGroupResolvesToAnEdit()
    {
        var (actions, cache) = await LightbulbAsync(clientPicksNestedActions: true);

        var leaves = actions
            .Where(a => a.Command?.Name == CodeActionHandler.PickNestedActionCommand)
            .SelectMany(a => Leaves((NestedCodeActionGroup)a.Command!.Arguments![0]))
            .ToList();

        Assert.NotEmpty(leaves);

        foreach (var leaf in leaves)
        {
            var resolved = await CodeActionHandler.ResolveAsync(
                new CodeAction(leaf.Title, "quickfix", null) { Data = new CodeActionData(leaf.Id!.Value) },
                cache,
                default);

            Assert.NotNull(resolved.Edit);
            Assert.NotEmpty(resolved.Edit!.Changes);
        }
    }

    /// <summary>
    /// Configuring a severity writes .editorconfig, which is neither a source document nor — in a
    /// project that has never had one — an existing file. Both were invisible to the resolver, so
    /// every severity in the menu used to resolve to nothing at all.
    /// </summary>
    [Fact]
    public async Task ConfiguringASeverityCreatesTheEditorConfigItWritesTo()
    {
        var (actions, cache) = await LightbulbAsync(clientPicksNestedActions: true);

        var severities = actions
            .Where(a => a.Command?.Name == CodeActionHandler.PickNestedActionCommand
                && a.Title.Contains("severity", StringComparison.OrdinalIgnoreCase))
            .SelectMany(a => Leaves((NestedCodeActionGroup)a.Command!.Arguments![0]))
            .ToList();

        Assert.NotEmpty(severities);

        var resolved = await CodeActionHandler.ResolveAsync(
            new CodeAction(severities[0].Title, "quickfix", null)
            {
                Data = new CodeActionData(severities[0].Id!.Value),
            },
            cache,
            default);

        Assert.Contains(resolved.Edit!.Changes, entry =>
            entry.Key.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase));

        // The fixture has no .editorconfig, so the edit has to bring the file into existence
        // first — a text edit against a file that is not there applies to nothing.
        Assert.NotNull(resolved.Edit.DocumentChanges);
        Assert.Contains(resolved.Edit.DocumentChanges!.OfType<CreateFile>(), c =>
            c.Uri.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<NestedCodeActionGroup> Leaves(NestedCodeActionGroup group) =>
        group.Children is { Length: > 0 } children
            ? children.SelectMany(Leaves)
            : [group];

    /// <summary>The lightbulb over `int x = 42;` — assigned and never used, so CS0219.</summary>
    private static async Task<(CodeAction[] Actions, LspResolveCache Cache)> LightbulbAsync(
        bool clientPicksNestedActions)
    {
        string path = FixturePaths.WarningsFile;
        string text = await File.ReadAllTextAsync(path);
        int line = LineOf(text, "int x = 42;");

        var cache = new LspResolveCache();
        var actions = await CodeActionHandler.CodeActionsAsync(
            new CodeActionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(path)),
                new Range(new Position(line, 0), new Position(line, 20)),
                new CodeActionContext([])),
            cache,
            default,
            clientPicksNestedActions);

        return (actions, cache);
    }

    private static int LineOf(string text, string anchor)
    {
        int index = text.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(index >= 0, $"anchor '{anchor}' not found");
        return text[..index].Count(c => c == '\n');
    }
}

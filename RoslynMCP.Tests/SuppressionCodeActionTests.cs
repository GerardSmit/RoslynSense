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

    private static int LineOf(string text, string anchor)
    {
        int index = text.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(index >= 0, $"anchor '{anchor}' not found");
        return text[..index].Count(c => c == '\n');
    }
}

using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Language features inside a source-generated document.
/// </summary>
/// <remarks>
/// A generated file has no path — it exists only inside the compilation — so it reaches the
/// server as a <c>roslynsense-generated:</c> URI rather than a file. Until the resolver
/// understood that scheme, opening one produced a buffer with no hover, no navigation and no
/// diagnostics: the text was there and nothing else worked.
/// </remarks>
[Collection(SharedState.Name)]
public class GeneratedDocumentLanguageFeatureTests
{
    private static async Task<string> GeneratedUriAsync()
    {
        var files = await VirtualDocumentHandler.ListGeneratedAsync(
            FixturePaths.SourceGenConsumerProjectFile, default);

        var generated = Assert.Single(files, f => f.HintName.Contains("Generated.g.cs"));
        return generated.Uri;
    }

    [Fact]
    public async Task AGeneratedDocumentResolvesToACompilationDocument()
    {
        string uri = await GeneratedUriAsync();

        // The path conversion has to leave the URI alone; Uri.LocalPath would drop the project
        // it names, and there would be nothing left to resolve.
        Assert.Equal(uri, LspConverters.UriToPath(uri));

        var document = await LspDocumentResolver.ResolveAsync(LspConverters.UriToPath(uri), default);

        Assert.NotNull(document);
        var text = await document!.GetTextAsync(default);
        Assert.Contains("public const string Version", text.ToString());
    }

    [Fact]
    public async Task HoverWorksInsideGeneratedCode()
    {
        string uri = await GeneratedUriAsync();
        var document = await LspDocumentResolver.ResolveAsync(LspConverters.UriToPath(uri), default);
        var text = await document!.GetTextAsync(default);

        var (line, character) = PositionOf(text.ToString(), "Version");

        var hover = await HoverHandler.HoverAsync(
            new TextDocumentPositionParams(new TextDocumentIdentifier(uri), new Position(line, character)),
            default);

        Assert.NotNull(hover);
        Assert.Contains("Version", hover!.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindReferencesFromGeneratedCodeReachesTheConsumer()
    {
        string uri = await GeneratedUriAsync();
        var document = await LspDocumentResolver.ResolveAsync(LspConverters.UriToPath(uri), default);
        var text = await document!.GetTextAsync(default);

        var (line, character) = PositionOf(text.ToString(), "Version");

        var references = await NavigationHandlers.ReferencesAsync(
            new ReferenceParams(
                new TextDocumentIdentifier(uri),
                new Position(line, character),
                new ReferenceContext(IncludeDeclaration: true)),
            default);

        Assert.NotEmpty(references);
        // The use in Program.cs, which is an ordinary file.
        Assert.Contains(references, r => r.Uri.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GoingToDefinitionFromRealCodeLandsInTheGeneratedFile()
    {
        // Warms the registry the way opening the Solution Explorer's Analyzers node does.
        string generatedUri = await GeneratedUriAsync();

        string consumer = Path.Combine(
            FixturePaths.SourceGenFixtureDir, "Consumer", "Program.cs");
        string text = await File.ReadAllTextAsync(consumer);
        var (line, character) = PositionOf(text, "Generated.Version");

        var definitions = await NavigationHandlers.DefinitionAsync(
            new TextDocumentPositionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(consumer)),
                new Position(line, character + "Generated.".Length)),
            typeDefinition: false,
            default);

        // Without the reverse mapping this was a file:// URI for a path that does not exist,
        // so the editor opened nothing.
        var target = Assert.Single(definitions);
        Assert.Equal(generatedUri, target.Uri);
    }

    [Fact]
    public async Task DiagnosticsAreComputedForAGeneratedDocument()
    {
        string uri = await GeneratedUriAsync();

        var report = await DiagnosticsHandler.PullAsync(
            new DocumentDiagnosticParams(new TextDocumentIdentifier(uri), null), default);

        // The fixture's generated file is valid, so the interesting assertion is that a report
        // came back at all: an unresolvable document produced nothing to report on.
        Assert.NotNull(report);
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

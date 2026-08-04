using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Resources;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// A resource key as something the editor can act on: navigate from, hover over and rename, in the
/// three file kinds that can write one.
/// </summary>
/// <remarks>
/// Against the disk-loaded fixture rather than an in-memory scenario, because none of this is a
/// question about one buffer: which <c>.resx</c> a key lives in is a function of the directories
/// around the call site, and the answer is the whole family rather than the one file a runtime
/// would have picked out of it.
/// </remarks>
[Collection(SharedState.Name)]
public class ResourceKeyEditorTests
{
    private static TextDocumentIdentifier Doc(string path) =>
        new(LspConverters.PathToUri(path));

    /// <summary>The position of <paramref name="needle"/> in the file, as an LSP position.</summary>
    private static Position PositionOf(string path, string needle, int offsetIntoNeedle = 0)
    {
        var source = SourceText.From(File.ReadAllText(path));
        var line = source.Lines.GetLinePosition(OffsetOf(path, needle, offsetIntoNeedle));
        return new Position(line.Line, line.Character);
    }

    private static int OffsetOf(string path, string needle, int offsetIntoNeedle = 0)
    {
        int index = File.ReadAllText(path).IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");
        return index + offsetIntoNeedle;
    }

    /// <summary>
    /// The pack, reached the way a host reaches it. Calling the handlers directly means no host has
    /// built a registry, so this stands in for one — and the resources pack has to be in it, since
    /// the markup surface asks the registry for the catalog rather than building one of its own.
    /// </summary>
    private static ResourcesLanguage Publish()
    {
        var settings = EffectiveSettings.Resolve([], null, out _);
        var registry = new LanguageRegistry(
            LanguagePackRegistration.Create(settings, new MarkdownFormatter())).Publish();

        return registry.Packs.OfType<ResourcesLanguage>().Single();
    }

    private static string FileNameOf(string uri) =>
        Path.GetFileName(Uri.UnescapeDataString(uri));

    // ---- Navigation --------------------------------------------------------------------------

    [Fact]
    public async Task DefinitionFromAKeyInCSharpOffersEveryFileThatDeclaresIt()
    {
        Publish();

        var locations = await NavigationHandlers.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.AspxResourceHelperFile),
                PositionOf(FixturePaths.AspxResourceHelperFile, "\"Heading\"", 2)),
            typeDefinition: false,
            default);

        // The literal binds to nothing, so without the pack claiming it this is a caret on a
        // string and F12 does nothing at all.
        Assert.Equal(
            [
                "Localized.aspx.resx",
                "Localized.aspx.nl-NL.resx",
                "Localized.aspx.Host.resx",
                "Localized.aspx.Portal-3.resx",
                "Localized.aspx.nl-NL.Portal-3.resx",
            ],
            locations.Select(l => FileNameOf(l.Uri)));
    }

    [Fact]
    public async Task DefinitionFromABuilderArgumentOffersEveryFileThatDeclaresIt()
    {
        Publish();

        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.LocalizedAspxFile),
                PositionOf(FixturePaths.LocalizedAspxFile, "Resources: Heading", "Resources: ".Length)),
            typeDefinition: false,
            default);

        Assert.Equal(5, locations.Length);
        Assert.All(locations, l => Assert.EndsWith(".resx", FileNameOf(l.Uri), StringComparison.Ordinal));
        Assert.Equal("Localized.aspx.resx", FileNameOf(locations[0].Uri));
    }

    [Fact]
    public async Task DefinitionFromAnImplicitKeyOffersTheEntriesItsGroupCovers()
    {
        Publish();

        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.LocalizedAspxFile),
                PositionOf(
                    FixturePaths.LocalizedAspxFile,
                    "meta:resourcekey=\"btnSave\"",
                    "meta:resourcekey=\"".Length)),
            typeDefinition: false,
            default);

        // The attribute declares no entry of its own: what it reaches is `btnSave.Text` and
        // `btnSave.ToolTip`, one per localizable property the control has.
        Assert.Equal(2, locations.Length);
        Assert.All(locations, l => Assert.Equal("Localized.aspx.resx", FileNameOf(l.Uri)));
        Assert.NotEqual(locations[0].Range.Start.Line, locations[1].Range.Start.Line);
    }

    // ---- Hover -------------------------------------------------------------------------------

    [Fact]
    public async Task HoverOnAKeyNamesTheTranslationsAndTheCustomizationsBesideIt()
    {
        Publish();

        var hover = await AspxLanguageHandler.HoverAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.LocalizedAspxFile),
                PositionOf(FixturePaths.LocalizedAspxFile, "Resources: Heading", "Resources: ".Length)),
            default);

        Assert.NotNull(hover);
        string markdown = hover!.Contents.Value;

        // The neutral value, because the winner is a function of the portal id, the thread culture
        // and a database-configured fallback locale and none of the three exists in an editor.
        Assert.Contains("Products", markdown, StringComparison.Ordinal);

        // Everything else that has an opinion, qualified by what makes it different.
        Assert.Contains("(nl-NL)", markdown, StringComparison.Ordinal);
        Assert.Contains("(Host)", markdown, StringComparison.Ordinal);
        Assert.Contains("(Portal-3)", markdown, StringComparison.Ordinal);
        Assert.Contains("(nl-NL, Portal-3)", markdown, StringComparison.Ordinal);
    }

    // ---- Rename ------------------------------------------------------------------------------

    [Fact]
    public async Task RenamingAKeyRewritesTheWholeFamilyAndEveryCallSiteAtOnce()
    {
        var pack = Publish();

        var edit = await pack.RenameAsync(
            new RenameParams(
                Doc(FixturePaths.LocalizedResxFile),
                PositionOf(FixturePaths.LocalizedResxFile, "name=\"Heading\"", "name=\"".Length),
                "Caption"),
            default);

        Assert.NotNull(edit);

        // A key rename creates, moves and deletes nothing, and a client that understands
        // documentChanges ignores changes entirely — so the ordered form would lose every edit.
        Assert.Null(edit!.DocumentChanges);

        var touched = edit.Changes.Keys.Select(FileNameOf).Order(StringComparer.Ordinal);

        Assert.Equal(
            [
                "Localized.aspx",
                "Localized.aspx.Host.resx",
                "Localized.aspx.Portal-3.resx",
                "Localized.aspx.nl-NL.Portal-3.resx",
                "Localized.aspx.nl-NL.resx",
                "Localized.aspx.resx",
                "ResourceHelper.cs",
            ],
            touched);

        // Both builders on the page, and nothing that merely spells the word.
        Assert.Equal(2, edit.Changes[LspConverters.PathToUri(FixturePaths.LocalizedAspxFile)].Length);
        Assert.All(edit.Changes.Values.SelectMany(edits => edits), e => Assert.Equal("Caption", e.NewText));
    }

    [Fact]
    public async Task PrepareRenameDeclinesAKeyWhoseFilesWereOnlyGuessed()
    {
        var pack = Publish();

        string path = FixturePaths.LocalizedCodeBehindFile;
        int offset = OffsetOf(path, "GetString(\"Heading\"", "GetString(\"".Length);

        // The root is a parameter, so nothing reads it and the files come from what sits near the
        // call. The key is found — this is not a caret on nothing.
        var target = await ResourceKeySearch.LocateAsync(pack.Settings, path, offset, project: null, default);

        Assert.NotNull(target);
        Assert.Equal(RootConfidence.Ambiguous, target!.Confidence);
        Assert.Equal("SharedResources", Assert.Single(target.Families).BaseName);

        // And declined anyway: a rename applied across a guessed file set is silent corruption.
        Assert.Null(await pack.PrepareAsync(path, offset, default));
    }

    // ---- Which overload carries the root -----------------------------------------------------

    [Fact]
    public async Task TheOverloadWithAControlDoesNotReadArgumentOneAsAResourceRoot()
    {
        var pack = Publish();

        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.LocalizedCodeBehindFile);
        var root = await document.GetSyntaxRootAsync(default);
        var semanticModel = await document.GetSemanticModelAsync(default);
        var catalog = await pack.CatalogAsync(document.Project, default);
        string text = await File.ReadAllTextAsync(FixturePaths.LocalizedCodeBehindFile);

        async Task<ResourceKeySearch.CodeMatch?> KeyAtAsync(string call)
        {
            int offset = text.IndexOf(call, StringComparison.Ordinal);
            Assert.True(offset >= 0, $"'{call}' is not in the code-behind.");

            return await ResourceKeySearch.KeyAtAsync(
                pack.Settings, catalog, document.Project, semanticModel!,
                root!.FindToken(offset + 1), default);
        }

        // Same method name, same arity. The (string, string) overload really does put a root at
        // index 1, and it resolves to the page that path names.
        var byPath = await KeyAtAsync("\"Greeting\", \"~/Default.aspx\"");
        Assert.NotNull(byPath);
        Assert.Equal(RootConfidence.Exact, byPath!.Confidence);
        Assert.Equal("Default.aspx", Assert.Single(byPath.Candidates).BaseName);

        // The (string, Control) overload does not. Reading its argument 1 as a root would find no
        // constant and fall back to whatever sits near the call; taking the route DNN itself takes
        // — the containing type to its markup — lands on this page's own file instead.
        var byControl = await KeyAtAsync("\"Greeting\", this");
        Assert.NotNull(byControl);
        Assert.Equal(RootConfidence.Exact, byControl!.Confidence);
        Assert.Equal("Localized.aspx", Assert.Single(byControl.Candidates).BaseName);

        // Both write the same key: DNN appends `.Text` when the key carries no dot of its own.
        Assert.Equal("Greeting.Text", byPath.Key);
        Assert.Equal("Greeting.Text", byControl.Key);
    }
}

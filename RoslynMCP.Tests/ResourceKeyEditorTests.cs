using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Config;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Resources;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Tools;
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

    [Fact]
    public async Task DefinitionFromAKeyInsideAnInlineCodeBlockReachesTheResx()
    {
        Publish();

        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.DefaultAspxFile),
                PositionOf(FixturePaths.DefaultAspxFile, "GetString(\"Greeting\"", "GetString(\"".Length)),
            typeDefinition: false,
            default);

        // The caret is in a string literal, which binds to nothing, so the projection's symbol
        // lookup answers null and this is the whole feature: without the markup path asking the
        // embedded languages, F12 here does nothing while the identical call in the code-behind
        // beside it navigates.
        Assert.Equal(
            ["Default.aspx.resx"],
            locations.Select(l => FileNameOf(l.Uri)));
    }

    [Fact]
    public async Task TheDefinitionToolReachesTheResxFromInsideAnInlineCodeBlockToo()
    {
        Publish();

        var result = await GoToDefinitionSnippetTool.GoToDefinitionSnippet(
            filePath: FixturePaths.DefaultAspxFile,
            markupSnippet: "GetString(\"[|Greeting|]\", this)",
            fmt: new MarkdownFormatter(),
            handlers: TestHandlers.GoToDefinition);

        // The tool surface answered "No symbol found" here for the same reason F12 answered
        // nothing: a key is not a symbol. A session and the editor must not resolve one caret two
        // different ways.
        Assert.Contains("Default.aspx.resx", result, StringComparison.Ordinal);
        Assert.Contains("Greeting.Text", result, StringComparison.Ordinal);
    }

    // ---- Hover -------------------------------------------------------------------------------

    [Fact]
    public async Task HoverOnAKeyInsideAnInlineCodeBlockDescribesIt()
    {
        Publish();

        var hover = await AspxLanguageHandler.HoverAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.DefaultAspxFile),
                PositionOf(FixturePaths.DefaultAspxFile, "GetString(\"Greeting\"", "GetString(\"".Length)),
            default);

        Assert.NotNull(hover);
        Assert.Contains("Greeting.Text", hover!.Contents.Value, StringComparison.Ordinal);

        // The pack computed it against the projection, whose offsets name characters no one can
        // see. Reporting it would highlight the wrong run of markup.
        Assert.Null(hover.Range);
    }


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

    /// <summary>
    /// Find-references on a key lists what reads it, not what translates it.
    /// </summary>
    /// <remarks>
    /// The fixture family has five files — neutral, a culture, two portal overrides and a host
    /// override — which is a modest version of what a shipped product has. Listing all of them
    /// buried the two or three real call sites under a list of every language the product ships in,
    /// and none of those entries is a place the key is used: they are other spellings of the same
    /// string. The rename test above is the other half of this one, and has to keep passing: a
    /// rename still has to rewrite every one of them.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReferencesToAKeyLeaveTheTranslationsOut(bool includeDeclaration)
    {
        var pack = Publish();

        var locations = await pack.ReferencesAsync(
            new ReferenceParams(
                Doc(FixturePaths.LocalizedResxFile),
                PositionOf(FixturePaths.LocalizedResxFile, "name=\"Heading\"", "name=\"".Length),
                new ReferenceContext(includeDeclaration)),
            default);

        var files = locations.Select(l => FileNameOf(l.Uri)).ToList();

        Assert.NotEmpty(files);
        Assert.Contains("Localized.aspx", files);

        // The neutral file is the definition, so it follows the flag the editor sent. The rest of
        // the family never appears either way.
        Assert.Equal(includeDeclaration, files.Contains("Localized.aspx.resx"));

        Assert.DoesNotContain("Localized.aspx.nl-NL.resx", files);
        Assert.DoesNotContain("Localized.aspx.Host.resx", files);
        Assert.DoesNotContain("Localized.aspx.Portal-3.resx", files);
        Assert.DoesNotContain("Localized.aspx.nl-NL.Portal-3.resx", files);
    }

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

    // ---- Keys nothing writes out -------------------------------------------------------------

    /// <summary>The one markup site a composed key has, and the characters it covers.</summary>
    private static async Task<string> BindingSiteAsync(string key)
    {
        var pack = Publish();

        var locations = await pack.ReferencesAsync(
            new ReferenceParams(
                Doc(FixturePaths.ImplicitResxFile),
                PositionOf(FixturePaths.ImplicitResxFile, $"name=\"{key}\"", "name=\"".Length),
                new ReferenceContext(IncludeDeclaration: false)),
            default);

        var markup = Assert.Single(
            locations, l => FileNameOf(l.Uri).Equals("Implicit.aspx", StringComparison.Ordinal));

        var text = SourceText.From(File.ReadAllText(FixturePaths.ImplicitAspxFile));
        return text.ToString(LspConverters.ToTextSpan(text, markup.Range));
    }

    /// <summary>
    /// A key composed from a control's id, which is the majority of an App_LocalResources file.
    /// </summary>
    /// <remarks>
    /// Nothing on the page spells <c>litStatus.Text</c>: the localizer walks the control tree and
    /// asks for it. Before this bound, find-references on the entry answered with the entry — the
    /// question "what is this string for" had no answer at all.
    /// </remarks>
    [Fact]
    public async Task AKeyComposedFromAControlIdReportsTheControl() =>
        Assert.Equal("litStatus", await BindingSiteAsync("litStatus.Text"));

    /// <summary>
    /// The same for a grid column, whose key is the prefix and its <c>UniqueName</c>.
    /// </summary>
    /// <remarks>
    /// The case a prefix search cannot reach from either end: the page does not contain
    /// <c>HeaderAmount</c>, and searching for the bare <c>Amount</c> instead would parse most of a
    /// site to match a common word. Only the family knows which page to open.
    /// </remarks>
    [Fact]
    public async Task AColumnHeadingReportsTheColumnItNames() =>
        Assert.Equal("Amount", await BindingSiteAsync("HeaderAmount.Text"));

    /// <summary>
    /// A key no pattern could have composed reaches no markup, however much of it a control's id
    /// happens to spell.
    /// </summary>
    /// <remarks>
    /// <c>Heading</c> ends in neither <c>.Text</c> nor <c>.ToolTip</c>, so no shipped pattern
    /// produces it — and the page does hold a <c>Header</c>-prefixed column, so a rule that matched
    /// on the prefix alone would have reported one here.
    /// </remarks>
    [Fact]
    public async Task AKeyNoPatternComposesStaysOutOfTheMarkup()
    {
        var pack = Publish();

        var locations = await pack.ReferencesAsync(
            new ReferenceParams(
                Doc(FixturePaths.ImplicitResxFile),
                PositionOf(FixturePaths.ImplicitResxFile, "name=\"Heading\"", "name=\"".Length),
                new ReferenceContext(IncludeDeclaration: true)),
            default);

        Assert.Equal(
            ["Implicit.aspx.resx"], locations.Select(l => FileNameOf(l.Uri)).Distinct());
    }

    /// <summary>
    /// Renaming a composed key moves every file of the family and leaves the control alone.
    /// </summary>
    /// <remarks>
    /// The half of the feature that is a refusal. The characters at the binding site are the
    /// control's name: writing <c>litReady</c> over <c>ID="litStatus"</c> would rename the control,
    /// orphan the field its designer declares and break every line of code-behind that touches it.
    /// So the site is reported and not edited, and the markup goes on naming a key that has moved —
    /// the same trade the pack already makes for <c>meta:resourcekey</c>.
    /// </remarks>
    [Fact]
    public async Task RenamingAComposedKeyLeavesTheControlItWasComposedFromAlone()
    {
        var pack = Publish();

        var edit = await pack.RenameAsync(
            new RenameParams(
                Doc(FixturePaths.ImplicitResxFile),
                PositionOf(FixturePaths.ImplicitResxFile, "name=\"litStatus.Text\"", "name=\"".Length),
                "litReady.Text"),
            default);

        Assert.NotNull(edit);

        Assert.Equal(
            ["Implicit.aspx.nl-NL.resx", "Implicit.aspx.resx"],
            edit!.Changes.Keys.Select(FileNameOf).Order(StringComparer.Ordinal));

        Assert.All(
            edit.Changes.Values.SelectMany(edits => edits),
            e => Assert.Equal("litReady.Text", e.NewText));
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

    // ---- A lookup that names no type ---------------------------------------------------------

    [Fact]
    public async Task ALookupWithNoContainingTypeMatchesAWrapperTheConfigurationCannotName()
    {
        var pack = Publish();

        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.LocalizedCodeBehindFile);
        var root = await document.GetSyntaxRootAsync(default);
        var semanticModel = await document.GetSemanticModelAsync(default);
        var catalog = await pack.CatalogAsync(document.Project, default);
        string text = await File.ReadAllTextAsync(FixturePaths.LocalizedCodeBehindFile);

        int offset = text.IndexOf("GetString(\"Greeting\")", StringComparison.Ordinal);
        Assert.True(offset >= 0, "the wrapper call is not in the code-behind.");
        var token = root!.FindToken(offset + "GetString(\"".Length);

        async Task<ResourceKeySearch.CodeMatch?> KeyAtAsync(ResourceSettings settings) =>
            await ResourceKeySearch.KeyAtAsync(
                settings, catalog, document.Project, semanticModel!, token, default);

        // The page declares the wrapper itself, so no preset lookup — every one of which names the
        // type declaring the member — reaches it.
        Assert.Null(await KeyAtAsync(pack.Settings));

        // Matched on the shape of the call alone — and the root still comes from the containing
        // type, so it lands on the page's own family rather than on a guess.
        var match = await KeyAtAsync(TypelessGetString("string"));

        Assert.NotNull(match);
        Assert.Equal("Greeting.Text", match!.Key);
        Assert.Equal(RootConfidence.Exact, match.Confidence);
        Assert.Equal("Localized.aspx", Assert.Single(match.Candidates).BaseName);

        // The same signature in the other spelling. A configuration reaching for the framework
        // name is writing house style, not a different claim about the code, and a lookup that
        // binds nothing because of it says nothing about why.
        var framework = await KeyAtAsync(TypelessGetString("System.String"));

        Assert.NotNull(framework);
        Assert.Equal("Greeting.Text", framework!.Key);
    }

    // ---- A key inside markup inline code -----------------------------------------------------

    /// <summary>The offset of the <c>"Greeting"</c> literal inside <c>Default.aspx</c>'s
    /// <c>&lt;%= … %&gt;</c> block.</summary>
    private static int InlineKeyOffset() =>
        OffsetOf(FixturePaths.DefaultAspxFile, "GetString(\"Greeting\"", "GetString(\"".Length);

    [Fact]
    public async Task AKeyInsideAnInlineCodeBlockLocatesAgainstTheMarkupFile()
    {
        var pack = Publish();

        var target = await ResourceKeySearch.LocateAsync(
            pack.Settings, FixturePaths.DefaultAspxFile, InlineKeyOffset(), project: null, default);

        Assert.NotNull(target);

        // The key the runtime probes for, and the abbreviation the page wrote — DNN appends `.Text`
        // when the key carries no dot of its own.
        Assert.Equal("Greeting.Text", target!.Key);
        Assert.Equal("Greeting", target.Written);
        Assert.Equal(".Text", target.KeySuffix);
        Assert.Equal(RootConfidence.Exact, target.Confidence);
        Assert.Equal("Default.aspx", Assert.Single(target.Families).BaseName);

        // The markup file and a span inside it, not the projection: every span on a target is
        // going to reach an editor as a location or an edit.
        Assert.Equal(FixturePaths.DefaultAspxFile, target.FilePath);
        Assert.False(target.Group);
    }

    [Fact]
    public async Task AStringInInlineCodeThatIsNotAKeyLocatesNothing()
    {
        var pack = Publish();

        int offset = OffsetOf(
            FixturePaths.DefaultAspxFile, "HtmlEncode(\"test\"", "HtmlEncode(\"".Length);

        // The page's other inline literal. Claiming every string in a page would put rename and
        // find-references on text that has nothing to do with resources.
        Assert.Null(await ResourceKeySearch.LocateAsync(
            pack.Settings, FixturePaths.DefaultAspxFile, offset, project: null, default));
    }

    [Fact]
    public async Task FindReferencesFromInlineCodeReachesTheDeclarationAndBack()
    {
        var pack = Publish();

        var results = await pack.ReferencesAsync(
            FixturePaths.DefaultAspxFile, InlineKeyOffset(), project: null, default);

        Assert.NotNull(results);
        var files = results!.Select(r => FileNameOf(r.Uri)).Order(StringComparer.Ordinal).ToList();

        // The declaration, the site the search started from — a reference list that omits the
        // caret's own site is one a rename would apply incompletely — and the code-behind of
        // another page, which reads the same entry through an explicit `"~/Default.aspx"` root.
        Assert.Equal(["Default.aspx", "Default.aspx.resx", "Localized.aspx.cs"], files);
    }

    [Fact]
    public async Task FindReferencesFromTheResxReachesIntoMarkupInlineCode()
    {
        var pack = Publish();

        var results = await pack.ReferencesAsync(
            FixturePaths.DefaultAspxResxFile,
            OffsetOf(FixturePaths.DefaultAspxResxFile, "name=\"Greeting.Text\"", "name=\"".Length),
            project: null,
            default);

        Assert.NotNull(results);

        // The other direction, which is the one a rename runs in: the declaration has to know that
        // a page reads it from inside a code block.
        var markup = Assert.Single(
            results!, r => FileNameOf(r.Uri).Equals("Default.aspx", StringComparison.Ordinal));

        var text = SourceText.From(File.ReadAllText(FixturePaths.DefaultAspxFile));
        var span = LspConverters.ToTextSpan(text, markup.Range);

        Assert.Equal("Greeting", text.ToString(span));
    }

    [Fact]
    public async Task RenamingFromTheResxRewritesTheMarkupInlineCodeInItsOwnForm()
    {
        var pack = Publish();

        var edit = await pack.RenameAsync(
            new RenameParams(
                Doc(FixturePaths.DefaultAspxResxFile),
                PositionOf(FixturePaths.DefaultAspxResxFile, "name=\"Greeting.Text\"", "name=\"".Length),
                "Salutation.Text"),
            default);

        Assert.NotNull(edit);

        Assert.Equal(
            ["Default.aspx", "Default.aspx.resx", "Localized.aspx.cs"],
            edit!.Changes.Keys.Select(FileNameOf).Order(StringComparer.Ordinal));

        // The declaration takes the whole key; the call site takes the form it wrote, because the
        // `.Text` is the lookup's and not the page's to spell.
        Assert.Equal(
            "Salutation.Text",
            Assert.Single(edit.Changes[LspConverters.PathToUri(FixturePaths.DefaultAspxResxFile)]).NewText);

        Assert.Equal(
            "Salutation",
            Assert.Single(edit.Changes[LspConverters.PathToUri(FixturePaths.DefaultAspxFile)]).NewText);
    }

    [Fact]
    public async Task RenamingFromMarkupInlineCodeRewritesTheDeclaration()
    {
        Publish();

        // Through the markup handler, which is what the editor calls for an .aspx buffer: the pack
        // is reached as a contributor ahead of the symbol resolve, because a key binds to nothing.
        var edit = await AspxLanguageHandler.RenameAsync(
            new RenameParams(
                Doc(FixturePaths.DefaultAspxFile),
                PositionOf(FixturePaths.DefaultAspxFile, "GetString(\"Greeting\"", "GetString(\"".Length),
                "Salutation"),
            default);

        Assert.NotNull(edit);

        Assert.Equal(
            ["Default.aspx", "Default.aspx.resx", "Localized.aspx.cs"],
            edit!.Changes.Keys.Select(FileNameOf).Order(StringComparer.Ordinal));

        // The name was typed in the call site's form, so the entry it renames is the suffixed one.
        Assert.Equal(
            "Salutation.Text",
            Assert.Single(edit.Changes[LspConverters.PathToUri(FixturePaths.DefaultAspxResxFile)]).NewText);
    }

    [Fact]
    public async Task PrepareRenameFromMarkupInlineCodeOffersTheWrittenKey()
    {
        Publish();

        var prepared = await AspxLanguageHandler.PrepareRenameAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.DefaultAspxFile),
                PositionOf(FixturePaths.DefaultAspxFile, "GetString(\"Greeting\"", "GetString(\"".Length)),
            default);

        Assert.NotNull(prepared);

        // The abbreviation, not the entry: the box the user types into has to start from what the
        // page says, or every rename from a DNN call site would begin by deleting a `.Text`.
        Assert.Equal("Greeting", prepared!.Placeholder);

        var text = SourceText.From(File.ReadAllText(FixturePaths.DefaultAspxFile));
        Assert.Equal("Greeting", text.ToString(LspConverters.ToTextSpan(text, prepared.Range)));
    }

    [Fact]
    public async Task CompletionInsideAnInlineCodeBlockOffersThePagesKeys()
    {
        Publish();

        var completions = await AspxCompletionHandler.CompletionAsync(
            new CompletionParams(
                Doc(FixturePaths.DefaultAspxFile),
                PositionOf(FixturePaths.DefaultAspxFile, "GetString(\"Greeting\"", "GetString(\"".Length)),
            new LspResolveCache(),
            default);

        // The list a caret inside the literal gets is the resx's, not C#'s: the entries of the
        // page's own `App_LocalResources` file, written the way the call site writes them.
        Assert.Contains("Greeting", completions.Items.Select(i => i.Label));
    }

    // ---- The find_usages tool ------------------------------------------------------------------

    [Fact]
    public async Task TheFindUsagesToolReachesTheKeysSitesFromInsideAnInlineCodeBlock()
    {
        Publish();

        string result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.DefaultAspxFile,
            markupSnippet: "GetString(\"[|Greeting|]\", this)",
            fmt: new MarkdownFormatter(),
            handlers: TestHandlers.FindUsages);

        // Not "No symbol found for 'Greeting' in ASPX file." — a session asking the tool about a
        // key has to get the same answer Shift+F12 gives in the editor.
        Assert.Contains("Default.aspx.resx", result, StringComparison.Ordinal);
        Assert.Contains("Localized.aspx.cs", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFindUsagesToolFindsTheSameKeyFromCSharp()
    {
        Publish();

        string result = await FindUsagesTool.FindUsages(
            filePath: FixturePaths.LocalizedCodeBehindFile,
            markupSnippet: "GetString(\"[|Greeting|]\", \"~/Default.aspx\")",
            fmt: new MarkdownFormatter(),
            handlers: TestHandlers.FindUsages);

        // The other side of the same search. A key is symbol-free in C# too, so the tool's own
        // path needs the pack consulted just as the markup handler's does — and the markup site
        // is in the answer, which is the whole point of indexing inline code.
        Assert.Contains("Default.aspx.resx", result, StringComparison.Ordinal);
        Assert.Contains("Default.aspx", result, StringComparison.Ordinal);
    }

    // ---- The missing-key diagnostic in markup inline code -------------------------------------

    [Fact]
    public async Task AnUndeclaredKeyInInlineCodeIsReportedInTheMarkup()
    {
        using var page = TemporaryPage.Beside(
            FixturePaths.DefaultAspxFile,
            "<%@ Page Language=\"C#\" CodeBehind=\"Default.aspx.cs\" Inherits=\"AspxProject.DefaultPage\" %>\n"
            + "<div><%= DotNetNuke.Services.Localization.Localization.GetString(\"Nope\", this) %></div>\n");

        PublishWithMissingKeyDiagnostic();

        var diagnostics = await AspxLanguageHandler.DiagnosticsAsync(page.Path, default);

        var reported = Assert.Single(diagnostics, d => d.Code == "RSX0003");

        // The squiggle is on the key in the page, not on the projection this server wrote to bind
        // it — and it names the entry the runtime would have looked for.
        var text = SourceText.From(File.ReadAllText(page.Path));
        Assert.Equal("Nope", text.ToString(LspConverters.ToTextSpan(text, reported.Range)));
        Assert.Contains("Nope.Text", reported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeclaredKeyInInlineCodeIsNotReported()
    {
        PublishWithMissingKeyDiagnostic();

        var diagnostics = await AspxLanguageHandler.DiagnosticsAsync(
            FixturePaths.DefaultAspxFile, default);

        // `Greeting.Text` is in the page's own file, and the other inline literal is not a key at
        // all. A rule that fires on either is a rule a solution switches off.
        Assert.DoesNotContain(diagnostics, d => d.Code == "RSX0003");
    }

    [Fact]
    public async Task AnUndeclaredKeyIsReportedInAnIncludedFragmentToo()
    {
        using var fragment = TemporaryPage.Included(
            FixturePaths.DefaultAspxFile,
            "<%@ Page Language=\"C#\" CodeBehind=\"Default.aspx.cs\" Inherits=\"AspxProject.DefaultPage\" %>\n"
            + "<div><%= DotNetNuke.Services.Localization.Localization.GetString(\"Nope\", this) %></div>\n");

        PublishWithMissingKeyDiagnostic();

        // The premises of the test, asserted rather than assumed: something includes this file, so
        // it takes the include-scoped path — and its inline code projects, which is the only way a
        // literal in it is ever bound. Stated here so a run where the project failed to load says
        // so, instead of reporting an empty list as a missing diagnostic.
        var document = await AspxDocumentService.GetAsync(fragment.Path, default);
        Assert.NotEmpty(
            AspxIncludeService.GetGraph(document!.Project).RootIncluders(document.FilePath));
        Assert.NotNull(AspxProjectionService.Get(document));

        var diagnostics = await AspxLanguageHandler.DiagnosticsAsync(fragment.Path, default);

        // A file someone includes answers its *parse* out of the includers', because its tags and
        // prefixes are theirs. What a key names is not: it is this file's own question, and a
        // fragment that stopped reporting missing keys the moment a page included it would go
        // quiet exactly where a DNN skin keeps its markup.
        var reported = Assert.Single(diagnostics, d => d.Code == "RSX0003");
        Assert.Contains("Nope.Text", reported.Message, StringComparison.Ordinal);
    }

    /// <summary>The registry with the missing-key rule on, which ships off.</summary>
    private static void PublishWithMissingKeyDiagnostic()
    {
        var settings = EffectiveSettings.Resolve(
            [],
            new RoslynSenseConfig { Resources = new ResourcesConfig { MissingKeyDiagnostic = true } },
            out _);

        new LanguageRegistry(
            LanguagePackRegistration.Create(settings, new MarkdownFormatter())).Publish();
    }

    /// <summary>
    /// Markup written into the fixture project for one test and deleted after it, for the cases
    /// that need a page the shared fixtures deliberately do not have.
    /// </summary>
    /// <remarks>
    /// Every page gets the <c>App_LocalResources</c> file the local convention names for it, and
    /// that is what keeps these tests from being a coin flip rather than a decoration. A page
    /// directive is the only way to give inline code a class to bind against, so these pages copy
    /// <c>Default.aspx</c>'s — which leaves two or three files in the project claiming
    /// <c>AspxProject.DefaultPage</c>, and the root resolves to whichever of them the project index
    /// lists first. Landing on a page with no resource file of its own, the root finds nothing and
    /// falls through to proximity, and proximity is <c>Ambiguous</c>, which switches the
    /// missing-key rule off; with one, every claimant answers the same.
    /// </remarks>
    private sealed class TemporaryPage : IDisposable
    {
        /// <summary>A resource file that parses and declares one key, which is not the key any of
        /// these tests asks about.</summary>
        private const string EmptyResources =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
              <resheader name="version"><value>2.0</value></resheader>
              <data name="Placeholder.Text" xml:space="preserve"><value>Placeholder</value></data>
            </root>

            """;

        private readonly List<string> _written = [];

        /// <summary>The file the test asks about.</summary>
        public required string Path { get; init; }

        public static TemporaryPage Beside(string sibling, string text)
        {
            var page = new TemporaryPage { Path = NameBeside(sibling) };
            page.WritePage(page.Path, text);

            return page.Settled();
        }

        /// <summary>
        /// The same page, plus a second one that server-side-includes it — which is what makes the
        /// first a fragment rather than a page, and sends its diagnostics down the include-scoped
        /// path.
        /// </summary>
        public static TemporaryPage Included(string sibling, string text)
        {
            var fragment = new TemporaryPage { Path = NameBeside(sibling) };
            fragment.WritePage(fragment.Path, text);
            fragment.WritePage(
                NameBeside(sibling),
                $"<!--#include file=\"{System.IO.Path.GetFileName(fragment.Path)}\" -->\n");

            return fragment.Settled();
        }

        private static string NameBeside(string sibling) =>
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(sibling)!,
                "TemporaryKey" + Guid.NewGuid().ToString("N")[..8] + ".aspx");

        /// <summary>The page, and the file its own keys would be declared in.</summary>
        private void WritePage(string path, string text)
        {
            Write(path, text);
            Write(ResourcesFor(path), EmptyResources);
        }

        private static string ResourcesFor(string page) =>
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(page)!,
                "App_LocalResources",
                System.IO.Path.GetFileName(page) + ".resx");

        private void Write(string path, string text)
        {
            File.WriteAllText(path, text);
            _written.Add(path);
            Announce(path);
        }

        /// <summary>What an editor's watcher would have said: a file entered or left the project,
        /// so the markup index and the resource catalog both have to be regrouped.</summary>
        private static void Announce(string path) =>
            ProjectIndexCacheService.NotifyFileChangedForTests(
                FixturePaths.AspxProjectFile, path, movedFiles: true);

        private TemporaryPage Settled()
        {
            AspxReferenceService.ResetFileListCache();
            return this;
        }

        public void Dispose()
        {
            foreach (string path in _written)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }

                Announce(path);
            }

            AspxReferenceService.ResetFileListCache();
        }
    }

    // ---- The root a call in markup is measured from ------------------------------------------

    [Fact]
    public async Task AContainingFileRootInMarkupIsTheMarkupFile()
    {
        Publish();

        // `containingFile` is the one root source whose answer differs between a page and its
        // code-behind, and inline code is where it is written: the file the call sits in *is* the
        // page. Measured from the projection instead, it would look for a resx named after a
        // generated file — `App_LocalResources/Default.aspx.aspx-inline.g.cs.resx` — and find none.
        var target = await ResourceKeySearch.LocateAsync(
            ContainingFileGetString(), FixturePaths.DefaultAspxFile, InlineKeyOffset(),
            project: null, default);

        Assert.NotNull(target);
        Assert.Equal("Greeting.Text", target!.Key);
        Assert.Equal(RootConfidence.Exact, target.Confidence);
        Assert.Equal("Default.aspx", Assert.Single(target.Families).BaseName);
    }

    /// <summary>A <c>GetString(key, *)</c> lookup that reads the file the call is written in.</summary>
    private static ResourceSettings ContainingFileGetString()
    {
        var warnings = new List<string>();

        var settings = ResourceSettings.Resolve(
            enabled: true,
            new ResourcesConfig
            {
                Preset = "none",
                Conventions =
                [
                    new ResourceConventionConfig { Id = "local", SiblingFolder = "App_LocalResources" },
                ],
                Lookups =
                [
                    new ResourceLookupConfig
                    {
                        ContainingType = "DotNetNuke.Services.Localization.Localization",
                        MethodName = "GetString",
                        ParameterTypes = ["string", "*"],
                        KeyIndex = 0,
                        RootSource = "containingFile",
                        RootInterpretation = "virtualPath",
                        DefaultKeySuffix = ".Text",
                    },
                ],
            },
            warnings);

        Assert.Empty(warnings);
        return settings;
    }

    /// <summary>A <c>GetString(key)</c> lookup that names no declaring type, with its one
    /// parameter spelled as given.</summary>
    private static ResourceSettings TypelessGetString(string parameterType)
    {
        var warnings = new List<string>();

        var settings = ResourceSettings.Resolve(
            enabled: true,
            new ResourcesConfig
            {
                Lookups =
                [
                    new ResourceLookupConfig
                    {
                        MethodName = "GetString",
                        ParameterTypes = [parameterType],
                        KeyIndex = 0,
                        RootSource = "containingType",
                        RootInterpretation = "virtualPath",
                        DefaultKeySuffix = ".Text",
                    },
                ],
            },
            warnings);

        Assert.Empty(warnings);
        return settings;
    }
}

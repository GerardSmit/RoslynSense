using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Languages;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The LSP entry points for markup, end to end: the document resolves through the real
/// workspace, so this also covers project lookup and the web.config prefixes that
/// <see cref="WebFormsLanguageTests"/>'s in-memory scenarios skip.
/// </summary>
[Collection(SharedState.Name)]
public class WebFormsLspTests
{
    private static TextDocumentIdentifier Doc(string path) =>
        new(LspConverters.PathToUri(path));

    /// <summary>The position of <paramref name="needle"/> in the file, as an LSP position.</summary>
    private static Position PositionOf(string path, string needle, int offsetIntoNeedle = 0)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");

        var source = Microsoft.CodeAnalysis.Text.SourceText.From(text);
        var line = source.Lines.GetLinePosition(index + offsetIntoNeedle);
        return new Position(line.Line, line.Character);
    }

    [Fact]
    public async Task RenamingAHandlerFromTheCodeBehindRewritesTheAttributeThatNamesIt()
    {
        // The markup pass runs over the registered packs, and calling the handler directly rather
        // than through a server means no host has built a registry, so this stands in for one.
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

        // The C# half of the gesture. Roslyn's Renamer cannot see OnClick=, so without the markup
        // pass this rename leaves the attribute naming a method that no longer exists — and the
        // page throws at runtime rather than failing to build.
        var edit = await RenameHandler.RenameAsync(
            new RenameParams(
                Doc(FixturePaths.EventWiringCodeBehindFile),
                PositionOf(FixturePaths.EventWiringCodeBehindFile, "Existing_Click", 2),
                "Saved_Click"),
            default);

        Assert.NotNull(edit);

        var markupEdits = edit!.Changes[LspConverters.PathToUri(FixturePaths.EventWiringAspxFile)];
        var handlerEdit = Assert.Single(markupEdits);
        Assert.Equal("Saved_Click", handlerEdit.NewText);

        Assert.Contains(
            LspConverters.PathToUri(FixturePaths.EventWiringCodeBehindFile),
            edit.Changes.Keys);
    }

    [Fact]
    public async Task RenamingFromTheCodeBehindLeavesLookalikeWordsAlone()
    {
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

        // Total is called twice from markup, and also sits in a comment and a string literal.
        // The markup pass is bound, not textual, so only the two calls move.
        var edit = await RenameHandler.RenameAsync(
            new RenameParams(
                Doc(FixturePaths.EventWiringCodeBehindFile),
                PositionOf(FixturePaths.EventWiringCodeBehindFile, "int Total()", 4),
                "GrandTotal"),
            default);

        Assert.NotNull(edit);

        var markupEdits = edit!.Changes[LspConverters.PathToUri(FixturePaths.EventWiringAspxFile)];
        Assert.Equal(2, markupEdits.Length);
        Assert.All(markupEdits, e => Assert.Equal("GrandTotal", e.NewText));
    }

    [Fact]
    public void MarkupFilesAreRoutedAwayFromTheCSharpHandlers()
    {
        Assert.True(AspxLanguageHandler.Handles(LspConverters.PathToUri(FixturePaths.DesignerAspxFile)));
        Assert.True(AspxLanguageHandler.Handles(LspConverters.PathToUri(FixturePaths.SiteMasterFile)));
        Assert.False(AspxLanguageHandler.Handles(LspConverters.PathToUri(FixturePaths.AspxPageHelperFile)));
    }

    [Fact]
    public async Task ADocumentResolvesToItsProjectAndCodeBehind()
    {
        var document = await AspxDocumentService.GetAsync(FixturePaths.DesignerAspxFile, default);

        Assert.NotNull(document);
        Assert.Equal("DesignerPage", document!.CodeBehind?.Name);
        Assert.EndsWith("AspxProject.csproj", document.Project.FilePath);
    }

    [Fact]
    public async Task GoToDefinitionOnAHandlerNameLandsInTheCodeBehind()
    {
        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.DesignerAspxFile),
                PositionOf(FixturePaths.DesignerAspxFile, "BtnSave_Click", 2)),
            typeDefinition: false,
            default);

        var location = Assert.Single(locations);
        Assert.EndsWith("Designer.aspx.cs", Uri.UnescapeDataString(location.Uri));
    }

    [Fact]
    public async Task GoToDefinitionOnAControlIdLandsOnItsField()
    {
        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.DesignerAspxFile),
                PositionOf(FixturePaths.DesignerAspxFile, "\"lblHeading\"", 3)),
            typeDefinition: false,
            default);

        // Declared by the generated designer half, which is where the field lives.
        var location = Assert.Single(locations);
        Assert.EndsWith("Designer.aspx.designer.cs", Uri.UnescapeDataString(location.Uri));
    }

    [Fact]
    public async Task HoverOnATagDescribesTheControlClass()
    {
        var hover = await AspxLanguageHandler.HoverAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.DesignerAspxFile),
                PositionOf(FixturePaths.DesignerAspxFile, "asp:Repeater", 5)),
            default);

        Assert.NotNull(hover);
        Assert.Contains("Repeater", hover!.Contents.Value);
    }

    [Fact]
    public async Task TheOutlineListsControlsUnderTheirId()
    {
        var symbols = await AspxLanguageHandler.DocumentSymbolAsync(
            new DocumentSymbolParams(Doc(FixturePaths.DesignerAspxFile)), default);

        var names = Flatten(symbols).Select(s => s.Name).ToList();

        Assert.Contains("@Page", names);
        Assert.Contains("designerForm", names);
        Assert.Contains("btnSave", names);
        // Controls inside a template are nested, not lost.
        Assert.Contains("lblNested", names);
    }

    [Fact]
    public async Task AWiredUpFixturePageHasNoMarkupDiagnostics()
    {
        var diagnostics = await AspxLanguageHandler.DiagnosticsAsync(
            FixturePaths.DesignerAspxFile, default);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CompletionInsideAControlTagOffersItsEventsAndProperties()
    {
        var completions = await AspxCompletionHandler.CompletionAsync(
            new CompletionParams(
                Doc(FixturePaths.DesignerAspxFile),
                // Just after `<asp:TextBox ` — an attribute-name position.
                PositionOf(FixturePaths.DesignerAspxFile, "<asp:TextBox ", "<asp:TextBox ".Length)),
            new LspResolveCache(),
            default);

        var labels = completions.Items.Select(i => i.Label).ToList();

        Assert.Contains("OnTextChanged", labels);
        Assert.Contains("Text", labels);
        // Already written on this tag, so offering them again would be noise.
        Assert.DoesNotContain("runat", labels);
        // Each name once, even though Control declares ID and the handler offers it up front.
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task CompletionForATagNameOffersTheRegisteredPrefixes()
    {
        var completions = await AspxCompletionHandler.CompletionAsync(
            new CompletionParams(
                Doc(FixturePaths.DesignerAspxFile),
                PositionOf(FixturePaths.DesignerAspxFile, "<asp:Label", "<asp:La".Length)),
            new LspResolveCache(),
            default);

        Assert.Contains("asp:Label", completions.Items.Select(i => i.Label));
    }

    [Fact]
    public async Task RenamingAHandlerFromMarkupRewritesBothHalves()
    {
        var edit = await AspxLanguageHandler.RenameAsync(
            new RenameParams(
                Doc(FixturePaths.DesignerAspxFile),
                PositionOf(FixturePaths.DesignerAspxFile, "BtnSave_Click", 2),
                "Save_Click"),
            default);

        Assert.NotNull(edit);

        var touched = edit!.Changes.Keys
            .Select(uri => Path.GetFileName(Uri.UnescapeDataString(uri)))
            .ToList();

        Assert.Contains("Designer.aspx.cs", touched);
        Assert.Contains("Designer.aspx", touched);
    }

    [Fact]
    public async Task AMissingHandlerIsReportedWhereItIsNamed()
    {
        var diagnostics = await AspxLanguageHandler.DiagnosticsAsync(
            FixturePaths.EventWiringAspxFile, default);

        var diagnostic = Assert.Single(diagnostics, d => d.Code == "WFC0008");
        Assert.Contains("MissingHandler", diagnostic.Message);
        Assert.Equal(2, diagnostic.Severity); // warning
    }

    [Fact]
    public async Task AMissingHandlerOffersAQuickFixThatGeneratesIt()
    {
        var position = PositionOf(FixturePaths.EventWiringAspxFile, "MissingHandler", 3);

        var actions = await AspxCodeActionHandler.CodeActionsAsync(
            new CodeActionParams(
                Doc(FixturePaths.EventWiringAspxFile),
                new Lsp.Protocol.Range(position, position),
                new CodeActionContext([])),
            default);

        var action = Assert.Single(actions, a => a.Kind == "quickfix");
        Assert.Contains("MissingHandler", action.Title);
        Assert.Contains("EventWiring.aspx.cs", action.Title);

        // The method itself is generated by the command, not by an edit computed while the
        // client was merely listing what it could offer.
        Assert.Null(action.Edit);
        Assert.NotNull(action.Command);
        Assert.Equal("roslynSense.generateEventHandler", action.Command!.Name);
        Assert.Equal("OnClick", action.Command.Arguments![2]);
        Assert.Equal("MissingHandler", action.Command.Arguments[3]);
    }

    [Fact]
    public async Task AWiredHandlerOffersNoQuickFix()
    {
        var position = PositionOf(FixturePaths.EventWiringAspxFile, "Existing_Click", 3);

        var actions = await AspxCodeActionHandler.CodeActionsAsync(
            new CodeActionParams(
                Doc(FixturePaths.EventWiringAspxFile),
                new Lsp.Protocol.Range(position, position),
                new CodeActionContext([])),
            default);

        Assert.DoesNotContain(actions, a => a.Kind == "quickfix");
    }

    [Fact]
    public async Task GeneratingAHandlerThroughTheCommandWritesItToTheCodeBehind()
    {
        // generateEventHandler is the WebForms pack's own command, dispatched by name across the
        // registered packs. Calling the handler directly rather than through a server means no
        // host has built a registry, so this stands in for one.
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

        string codeBehindPath = FixturePaths.EventWiringCodeBehindFile;
        string original = await File.ReadAllTextAsync(codeBehindPath);

        var document = await AspxDocumentService.GetAsync(FixturePaths.EventWiringAspxFile, default);
        var control = AspxSymbolResolver.EnumerateControls(document!.Tree!)
            .Single(c => c.Id == "btnUnwired");

        try
        {
            var result = await ExecuteCommandHandler.ExecuteAsync(
                new ExecuteCommandParams(
                    ExecuteCommandHandler.GenerateEventHandlerCommand,
                    JsonArguments(
                        LspConverters.PathToUri(FixturePaths.EventWiringAspxFile),
                        control.StartTag.Range.Start.Offset,
                        "OnClick",
                        "MissingHandler")),
                default);

            Assert.Contains("MissingHandler", result.ToString());

            string updated = await File.ReadAllTextAsync(codeBehindPath);
            Assert.Contains("void MissingHandler(object sender", updated);
            Assert.Contains("Existing_Click", updated);
        }
        finally
        {
            await File.WriteAllTextAsync(codeBehindPath, original);
            AspxDocumentService.Invalidate(FixturePaths.EventWiringAspxFile);
        }
    }

    private static System.Text.Json.JsonElement[] JsonArguments(params object[] values) =>
        System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(
            System.Text.Json.JsonSerializer.Serialize(values))!;

    [Fact]
    public async Task FindReferencesOnAHandlerIncludesTheAttributeThatNamesIt()
    {
        var locations = await AspxLanguageHandler.ReferencesAsync(
            new ReferenceParams(
                Doc(FixturePaths.DesignerAspxFile),
                PositionOf(FixturePaths.DesignerAspxFile, "BtnSave_Click", 2),
                new ReferenceContext(IncludeDeclaration: true)),
            default);

        Assert.Contains(locations, l =>
            Uri.UnescapeDataString(l.Uri).EndsWith("Designer.aspx", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReferencesInCodeBlocksAreBoundRatherThanTextMatched()
    {
        var document = await AspxDocumentService.GetAsync(FixturePaths.EventWiringAspxFile, default);
        var total = document!.CodeBehind!.GetMembers("Total").Single();

        var references = await AspxReferenceService.FindAsync(total, document.Project, default);

        var inMarkup = references
            .Where(r => string.Equals(r.FilePath, FixturePaths.EventWiringAspxFile, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Span.Start)
            .OrderBy(start => start)
            .ToList();

        string text = document.Text;

        // The call inside <script runat="server"> and the one inside <%= %>. Not the mention in
        // the comment, and not the "Total" string literal — which is the whole point of binding
        // these rather than matching the name.
        Assert.Equal(2, inMarkup.Count);
        Assert.All(inMarkup, start => Assert.Equal("Total", text.Substring(start, 5)));

        int scriptBlock = text.IndexOf("return Total() * 2", StringComparison.Ordinal);
        int expression = text.IndexOf("<%= Total()", StringComparison.Ordinal);
        Assert.Contains(inMarkup, start => start > scriptBlock && start < scriptBlock + 20);
        Assert.Contains(inMarkup, start => start > expression && start < expression + 10);

        int comment = text.IndexOf("// Total is only", StringComparison.Ordinal);
        int literal = text.IndexOf("\"Total\"", StringComparison.Ordinal);
        Assert.DoesNotContain(inMarkup, start => start > comment && start < comment + 12);
        Assert.DoesNotContain(inMarkup, start => start > literal && start < literal + 8);
    }

    [Fact]
    public async Task RenamingAMethodUsedInACodeBlockRewritesTheCallAndNothingElse()
    {
        var edit = await AspxLanguageHandler.RenameAsync(
            new RenameParams(
                Doc(FixturePaths.EventWiringAspxFile),
                PositionOf(FixturePaths.EventWiringAspxFile, "Total()", 2),
                "GrandTotal"),
            default);

        Assert.NotNull(edit);

        var markupEdits = edit!.Changes[LspConverters.PathToUri(FixturePaths.EventWiringAspxFile)];

        // Two calls; the comment and the string keep the old word.
        Assert.Equal(2, markupEdits.Length);
        Assert.All(markupEdits, e => Assert.Equal("GrandTotal", e.NewText));
    }

    [Fact]
    public async Task APageWithNoCodeBehindStillBindsItsScriptBlock()
    {
        // HeaderControl.ascx has no Inherits: its code lives entirely in the markup, and the
        // projection gives it a class of its own so that code still binds.
        var document = await AspxDocumentService.GetAsync(FixturePaths.HeaderControlFile, default);
        Assert.NotNull(document);

        var projection = AspxProjectionService.Get(document);
        Assert.NotNull(projection);

        // A class of its own, not a second declaration of the framework base it derives from.
        string text = projection!.Text.ToString();
        Assert.Contains("__AspxPage_HeaderControl_ascx", text);
        Assert.Contains("Title", text);
    }

    private static IEnumerable<DocumentSymbol> Flatten(IEnumerable<DocumentSymbol> symbols)
    {
        foreach (var symbol in symbols)
        {
            yield return symbol;
            foreach (var child in Flatten(symbol.Children))
                yield return child;
        }
    }
}

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

    /// <summary>
    /// The caret is on the declaration, so the answer is the usages rather than a definition.
    /// </summary>
    /// <remarks>
    /// This used to land on the designer field, which is the same dead end the C# side of the
    /// gesture stopped offering: the <c>ID</c> is what makes that field exist, so the designer is a
    /// transcription of the line the caret is already on. Go-to-definition on a declaration has
    /// nowhere to go, and Visual Studio answers the identical caret in C# with the usages.
    /// </remarks>
    [Fact]
    public async Task GoToDefinitionOnAControlIdListsItsUsagesRatherThanTheDesigner()
    {
        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.DesignerAspxFile),
                PositionOf(FixturePaths.DesignerAspxFile, "\"lblHeading\"", 3)),
            typeDefinition: false,
            default);

        // `lblHeading.Text = "Heading";` in the code-behind, and nothing else.
        var location = Assert.Single(locations);
        Assert.EndsWith("Designer.aspx.cs", Uri.UnescapeDataString(location.Uri));

        // Not the transcription, and not the caret's own position either.
        Assert.DoesNotContain(
            locations,
            candidate => Uri.UnescapeDataString(candidate.Uri)
                .EndsWith("Designer.aspx.designer.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            locations,
            candidate => Uri.UnescapeDataString(candidate.Uri)
                .EndsWith("Designer.aspx", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A tag naming a user control opens the control's markup, not its class.
    /// </summary>
    /// <remarks>
    /// A user control <em>is</em> its <c>.ascx</c>: the tags, the layout and the IDs a caller came
    /// to read are there, while the class holds the handlers. F12 used to offer both halves of that
    /// class's partial — the code-behind and the generated designer — so the gesture was a picker
    /// between two files, one of which the next regeneration overwrites.
    /// </remarks>
    [Fact]
    public async Task GoToDefinitionOnAUserControlTagOpensItsMarkup()
    {
        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.UsesUserControlFile),
                PositionOf(FixturePaths.UsesUserControlFile, "uc:OrderItems runat", 4)),
            typeDefinition: false,
            default);

        var location = Assert.Single(locations);
        Assert.EndsWith("OrderItems.ascx", Uri.UnescapeDataString(location.Uri));

        // Not the code-behind beside it: staying in markup is the point.
        Assert.DoesNotContain(
            locations,
            candidate => Uri.UnescapeDataString(candidate.Uri)
                .EndsWith("OrderItems.ascx.cs", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A control that is only a class still resolves to the class.
    /// </summary>
    /// <remarks>
    /// <c>LocalizedLabel</c> is a plain <c>WebControl</c> subclass with no markup of its own, which
    /// is also the shape of every control from a referenced assembly. The markup preference has to
    /// decline for those rather than leave F12 with nothing.
    /// </remarks>
    [Fact]
    public async Task GoToDefinitionOnACodeOnlyControlTagStillReachesItsClass()
    {
        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.LocalizedAspxFile),
                PositionOf(FixturePaths.LocalizedAspxFile, "uc:LocalizedLabel", 4)),
            typeDefinition: false,
            default);

        Assert.NotEmpty(locations);
        Assert.All(
            locations,
            location => Assert.EndsWith(
                "LocalizedLabel.cs", Uri.UnescapeDataString(location.Uri)));
    }

    /// <summary>
    /// No markup-side answer ever includes a generated designer.
    /// </summary>
    /// <remarks>
    /// The withdrawal used to live only on the C# side, where it runs through
    /// <c>ILanguageSupersedingContributor</c>; the markup handler calls
    /// <c>DefinitionLocationsAsync</c> directly and never consulted a contributor, so the same
    /// designer that F12 from C# had stopped offering was still offered from the page.
    /// </remarks>
    [Fact]
    public async Task NoMarkupDefinitionOffersAGeneratedDesigner()
    {
        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.DesignerAspxFile),
                PositionOf(FixturePaths.DesignerAspxFile, "asp:Repeater", 5)),
            typeDefinition: false,
            default);

        Assert.DoesNotContain(
            locations,
            candidate => Uri.UnescapeDataString(candidate.Uri)
                .EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The type is still a definition, so Ctrl+F12 on the same caret is unaffected.</summary>
    [Fact]
    public async Task TypeDefinitionOnAControlIdStillReachesTheControlClass()
    {
        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.DesignerAspxFile),
                PositionOf(FixturePaths.DesignerAspxFile, "\"lblHeading\"", 3)),
            typeDefinition: true,
            default);

        Assert.NotEmpty(locations);
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

    /// <summary>
    /// Hover reaches the bound member through the whole request, not only through its describer.
    /// </summary>
    /// <remarks>
    /// The ordering is the load-bearing part: the projection binds an <c>Eval</c> argument to
    /// <c>System.String</c>, so a branch reached after the symbol lookup would describe the literal
    /// and never get here. Pinned end to end because that ordering is what a later edit would
    /// quietly undo.
    /// </remarks>
    [Fact]
    public async Task HoverInsideAnEvalDescribesTheBoundMember()
    {
        var hover = await AspxLanguageHandler.HoverAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.TypedRepeaterAscxFile),
                PositionOf(FixturePaths.TypedRepeaterAscxFile, "Eval(\"Length\")", 7)),
            default);

        Assert.NotNull(hover);
        Assert.Contains("Length", hover!.Contents.Value);
        Assert.Contains("int", hover.Contents.Value);
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
    public async Task CompletionInsideAnExpressionBlockOffersTheCurrentScope()
    {
        // `<%= Total() %>` is C# in the page's own class, so the members of that class are what a
        // caret inside it should offer. Answering nothing leaves the editor's word matcher to
        // suggest whatever words happen to be in the file.
        var completions = await AspxCompletionHandler.CompletionAsync(
            new CompletionParams(
                Doc(FixturePaths.EventWiringAspxFile),
                PositionOf(FixturePaths.EventWiringAspxFile, "<%= Total()", "<%= Tot".Length)),
            new LspResolveCache(),
            default);

        Assert.Contains("Total", completions.Items.Select(i => i.Label));
    }

    [Fact]
    public async Task CompletionInsideADataBindingBlockOffersMembersOfTheBoundType()
    {
        // `<%# PageHelper.FormatDate(…) %>` inside an ItemTemplate. The block binds through the
        // projection the same way the ones outside a template do.
        var completions = await AspxCompletionHandler.CompletionAsync(
            new CompletionParams(
                Doc(FixturePaths.RepeaterAspxFile),
                PositionOf(FixturePaths.RepeaterAspxFile, "PageHelper.FormatDate", "PageHelper.".Length)),
            new LspResolveCache(),
            default);

        Assert.Contains("FormatDate", completions.Items.Select(i => i.Label));
    }

    [Fact]
    public async Task CompletionInsideAStronglyTypedTemplateOffersMembersOfItem()
    {
        // The Repeater declares ItemType="System.String", so `Item` inside its ItemTemplate is a
        // string and `Item.` has to offer string members. This is what a declared ItemType is for.
        var completions = await AspxCompletionHandler.CompletionAsync(
            new CompletionParams(
                Doc(FixturePaths.TypedRepeaterAscxFile),
                PositionOf(FixturePaths.TypedRepeaterAscxFile, "Item.Length", "Item.".Length)),
            new LspResolveCache(),
            default);

        var labels = completions.Items.Select(i => i.Label).ToList();

        Assert.Contains("Length", labels);
        Assert.Contains("Substring", labels);
    }

    [Fact]
    public async Task ItemIsInScopeInAStronglyTypedTemplate()
    {
        // The name itself, not just its members: a caret at the start of the block has to know
        // `Item` exists before anyone can type a dot after it.
        var completions = await AspxCompletionHandler.CompletionAsync(
            new CompletionParams(
                Doc(FixturePaths.TypedRepeaterAscxFile),
                PositionOf(FixturePaths.TypedRepeaterAscxFile, "Item.Length", 0)),
            new LspResolveCache(),
            default);

        Assert.Contains("Item", completions.Items.Select(i => i.Label));
    }

    [Fact]
    public async Task CompletionOnContainerOffersTheTemplateContainerMembers()
    {
        // Repeater.ItemTemplate carries [TemplateContainer(typeof(RepeaterItem))], so `Container`
        // inside the template is a RepeaterItem and `Container.` has to offer its members.
        var completions = await AspxCompletionHandler.CompletionAsync(
            new CompletionParams(
                Doc(FixturePaths.TypedRepeaterAscxFile),
                PositionOf(FixturePaths.TypedRepeaterAscxFile, "Container.DataItem", "Container.".Length)),
            new LspResolveCache(),
            default);

        Assert.Contains("DataItem", completions.Items.Select(i => i.Label));
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

    /// <summary>Runs <paramref name="body"/> with <paramref name="path"/> showing
    /// <paramref name="buffer"/> instead of what is on disk.</summary>
    private static async Task WithBufferAsync(string path, string buffer, Func<Task> body)
    {
        string session = Guid.NewGuid().ToString("N");
        try
        {
            OpenDocumentStore.Open(
                session, path, Microsoft.CodeAnalysis.Text.SourceText.From(buffer), 1);
            await body();
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
            AspxDocumentService.Invalidate(path);
        }
    }

    /// <summary>The position of <paramref name="needle"/> in <paramref name="text"/> itself,
    /// for a buffer that differs from the file on disk.</summary>
    private static Position PositionIn(string text, string needle, int offsetIntoNeedle = 0)
    {
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in the buffer");

        var source = Microsoft.CodeAnalysis.Text.SourceText.From(text);
        var line = source.Lines.GetLinePosition(index + offsetIntoNeedle);
        return new Position(line.Line, line.Character);
    }

    [Fact]
    public async Task CommittingATagNameWritesRunatServerWithIt()
    {
        // A half-typed control tag: committing "asp:Repeater" must land as a server control,
        // because without runat the tag is literal text and every other feature goes dark.
        string path = FixturePaths.DesignerAspxFile;
        string buffer = (await File.ReadAllTextAsync(path))
            .Replace("<asp:TextBox ID=\"txtName\" runat=\"server\" />", "<asp:Rep");

        await WithBufferAsync(path, buffer, async () =>
        {
            var completions = await AspxCompletionHandler.CompletionAsync(
                new CompletionParams(Doc(path), PositionIn(buffer, "<asp:Rep", "<asp:Rep".Length)),
                new LspResolveCache(),
                default);

            var item = completions.Items.Single(i => i.Label == "asp:Repeater");
            Assert.Equal("asp:Repeater runat=\"server\"", item.TextEdit!.NewText);
        });
    }

    [Fact]
    public async Task RetypingATagNameDoesNotDuplicateItsRunat()
    {
        // The caret is in the name of a tag that already carries runat="server", so the commit
        // replaces the name alone.
        var completions = await AspxCompletionHandler.CompletionAsync(
            new CompletionParams(
                Doc(FixturePaths.DesignerAspxFile),
                PositionOf(FixturePaths.DesignerAspxFile, "<asp:Label", "<asp:La".Length)),
            new LspResolveCache(),
            default);

        var item = completions.Items.Single(i => i.Label == "asp:Label");
        Assert.Equal("asp:Label", item.TextEdit!.NewText);
    }

    [Fact]
    public async Task CompletionInsideAParseChildrenControlOffersItsTemplates()
    {
        // Direct children of a Repeater are its properties, not controls — that is what
        // [ParseChildren(true)] means — so a tag opened there completes the template names.
        var completions = await AspxCompletionHandler.CompletionAsync(
            new CompletionParams(
                Doc(FixturePaths.DesignerAspxFile),
                PositionOf(FixturePaths.DesignerAspxFile, "<ItemTemplate>", 1)),
            new LspResolveCache(),
            default);

        var labels = completions.Items.Select(i => i.Label).ToList();

        Assert.Contains("ItemTemplate", labels);
        Assert.Contains("FooterTemplate", labels);
        Assert.DoesNotContain("asp:Label", labels);
    }

    [Fact]
    public async Task CompletionInsideACollectionElementOffersItsItemTypes()
    {
        // Inside <Columns> only what the collection's Add accepts fits, so the item type is
        // offered — without runat, which a plain object rejects.
        var completions = await AspxCompletionHandler.CompletionAsync(
            new CompletionParams(
                Doc(FixturePaths.ImplicitAspxFile),
                PositionOf(FixturePaths.ImplicitAspxFile, "<uc:ItemGridColumn", 1)),
            new LspResolveCache(),
            default);

        var item = completions.Items.Single(i => i.Label == "uc:ItemGridColumn");
        Assert.Equal("uc:ItemGridColumn", item.TextEdit!.NewText);
        Assert.DoesNotContain("asp:Label", completions.Items.Select(i => i.Label));
    }

    [Fact]
    public async Task AttributeCompletionOnACollectionItemOffersNeitherIdNorRunat()
    {
        // A grid column is a plain object: it has the properties its class declares, but no
        // ID and no runat — offering them would invite the very attribute WFR0001 warns about.
        var completions = await AspxCompletionHandler.CompletionAsync(
            new CompletionParams(
                Doc(FixturePaths.ImplicitAspxFile),
                PositionOf(
                    FixturePaths.ImplicitAspxFile,
                    "<uc:ItemGridColumn UniqueName=\"Amount\"",
                    "<uc:ItemGridColumn ".Length)),
            new LspResolveCache(),
            default);

        var labels = completions.Items.Select(i => i.Label).ToArray();
        Assert.Contains("UniqueName", labels);
        Assert.DoesNotContain("runat", labels);
        Assert.DoesNotContain("ID", labels);
    }

    [Fact]
    public async Task CompletingRunatAsAnAttributeWritesItsValueWithIt()
    {
        // `server` is the only value runat takes, so committing the attribute writes the whole
        // thing rather than leaving the caret in empty quotes.
        string path = FixturePaths.DesignerAspxFile;
        string buffer = (await File.ReadAllTextAsync(path))
            .Replace("<asp:TextBox ID=\"txtName\" runat=\"server\" />", "<asp:TextBox  />");

        await WithBufferAsync(path, buffer, async () =>
        {
            var completions = await AspxCompletionHandler.CompletionAsync(
                new CompletionParams(
                    Doc(path), PositionIn(buffer, "<asp:TextBox  />", "<asp:TextBox ".Length)),
                new LspResolveCache(),
                default);

            var item = completions.Items.Single(i => i.Label == "runat");
            Assert.Equal("runat=\"server\"", item.TextEdit!.NewText);
        });
    }

    [Fact]
    public async Task AControlTagWithoutRunatIsAnError()
    {
        string path = FixturePaths.DesignerAspxFile;
        string buffer = (await File.ReadAllTextAsync(path))
            .Replace(
                "<asp:TextBox ID=\"txtName\" runat=\"server\" />",
                "<asp:TextBox ID=\"txtName\" />");

        await WithBufferAsync(path, buffer, async () =>
        {
            var diagnostics = await AspxLanguageHandler.DiagnosticsAsync(path, default);

            var missing = Assert.Single(diagnostics, d => d.Code == "WFR0001");
            Assert.Equal(1, missing.Severity);
            Assert.Contains("runat", missing.Message);
        });
    }

    [Fact]
    public async Task CollectionItemsNeedNoRunat()
    {
        // <uc:ItemGridColumn> inside <Columns> is an item of the collection, not a control
        // that forgot its runat.
        var diagnostics = await AspxLanguageHandler.DiagnosticsAsync(
            FixturePaths.ImplicitAspxFile, default);

        Assert.DoesNotContain(diagnostics, d => d.Code == "WFR0001");
    }

    [Fact]
    public async Task ANonControlTagNeedsNoRunatEvenAsAPlainElement()
    {
        // When the parser cannot attach an item to its collection (mixed-case legacy markup
        // does this) the tag is a plain element, but its type is still not a Control — runat
        // would be wrong on it, so no diagnostic.
        string path = FixturePaths.ImplicitAspxFile;
        string buffer = (await File.ReadAllTextAsync(path))
            .Replace(
                "<asp:Literal ID=\"litStatus\" runat=\"server\" />",
                "<uc:ItemGridColumn UniqueName=\"Loose\" />");

        await WithBufferAsync(path, buffer, async () =>
        {
            var diagnostics = await AspxLanguageHandler.DiagnosticsAsync(path, default);

            Assert.DoesNotContain(diagnostics, d => d.Code == "WFR0001");
        });
    }

    [Fact]
    public async Task TheMissingRunatQuickFixWritesTheAttribute()
    {
        string path = FixturePaths.DesignerAspxFile;
        string buffer = (await File.ReadAllTextAsync(path))
            .Replace(
                "<asp:TextBox ID=\"txtName\" runat=\"server\" />",
                "<asp:TextBox ID=\"txtName\" />");

        await WithBufferAsync(path, buffer, async () =>
        {
            var position = PositionIn(buffer, "<asp:TextBox ID=\"txtName\" />", 2);
            var actions = await AspxCodeActionHandler.CodeActionsAsync(
                new CodeActionParams(
                    Doc(path),
                    new Lsp.Protocol.Range(position, position),
                    new CodeActionContext([])),
                default);

            var fix = Assert.Single(actions, a => a.Title == "Add runat=\"server\"");
            var edit = Assert.Single(fix.Edit!.Changes[LspConverters.PathToUri(path)]);
            Assert.Equal(" runat=\"server\"", edit.NewText);

            // The attribute lands right after the tag name.
            var expected = PositionIn(buffer, "<asp:TextBox ID=\"txtName\" />", "<asp:TextBox".Length);
            Assert.Equal(expected, edit.Range.Start);
        });
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
        // The markup half of the answer comes through the pack's reference contributor, so the
        // pack has to be registered the way a serving host registers it.
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

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
    public async Task GoToDefinitionOnAnIdStaysInsideThePageThatDeclaresIt()
    {
        // Designer.aspx declares an <asp:Repeater ID="rptItems"> of its own, and its field is
        // AspxProject.DesignerPage.rptItems — a different class, reached through a different
        // Inherits. What makes two same-named controls two controls is the class behind the page,
        // so a markup match on the name alone answered this with every page in the project that
        // happens to use the ID.
        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.RepeaterAspxFile),
                PositionOf(FixturePaths.RepeaterAspxFile, "rptItems", 2)),
            typeDefinition: false,
            default);

        // The ID is the declaration, so the gesture answers with usages — the code that reads the
        // field is what the reader came for.
        Assert.Contains(locations, l => FileName(l.Uri) == "Repeater.aspx.cs");
        Assert.DoesNotContain(locations, l => FileName(l.Uri) == "Designer.aspx");
        Assert.DoesNotContain(locations, l => FileName(l.Uri) == "Designer.aspx.designer.cs");
    }

    [Fact]
    public async Task FindReferencesOnAHandlerStaysInsideTheControlThatDeclaresIt()
    {
        // OrderItems.ascx wires OnItemDataBound to a method of the same name and the same
        // signature on AspxProject.OrderItemsControl. Nothing but the containing type tells the
        // two apart, and a rename that rewrote both would break a control nobody touched.
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

        var locations = await AspxLanguageHandler.ReferencesAsync(
            new ReferenceParams(
                Doc(FixturePaths.RepeaterAspxFile),
                PositionOf(FixturePaths.RepeaterAspxFile, "rpt_OnItemDataBound", 2),
                new ReferenceContext(IncludeDeclaration: true)),
            default);

        Assert.Contains(locations, l => FileName(l.Uri) == "Repeater.aspx");
        Assert.Contains(locations, l => FileName(l.Uri) == "Repeater.aspx.cs");
        Assert.DoesNotContain(locations, l => FileName(l.Uri) == "OrderItems.ascx");
    }

    [Fact]
    public async Task GoToDefinitionOnAControlInACodeBlockReachesTheMarkupThatDeclaresIt()
    {
        // `<%= txtName.ClientID %>` binds to the same field that `txtName` in the code-behind binds
        // to, so it has to answer the same way: the ID attribute that declares the control, not the
        // designer line generated from it. Answering one way from a .ascx.cs and another from the
        // .ascx beside it is the two halves of one relationship disagreeing.
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.DesignerAspxFile),
                PositionOf(FixturePaths.DesignerAspxFile, "txtName.ClientID", 3)),
            typeDefinition: false,
            default);

        Assert.Contains(locations, l => FileName(l.Uri) == "Designer.aspx");
        Assert.DoesNotContain(locations, l => FileName(l.Uri) == "Designer.aspx.designer.cs");

        // The ID attribute, not the top of the file.
        var markup = locations.Single(l => FileName(l.Uri) == "Designer.aspx");
        string line = (await File.ReadAllLinesAsync(FixturePaths.DesignerAspxFile))[markup.Range.Start.Line];
        Assert.Contains("txtName", line, StringComparison.Ordinal);
        Assert.Contains("asp:TextBox", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATemplateTagResolvesToThePropertyItFills()
    {
        // <ItemTemplate> is Repeater.ItemTemplate. The tag is a member reference the same way an
        // attribute name is, and it used to resolve to nothing — so F12 and hover did nothing on
        // the tags most of a control's markup sits inside.
        var document = await AspxDocumentService.GetAsync(FixturePaths.RepeaterAspxFile, default);
        int offset = document!.Text.IndexOf("<ItemTemplate>", StringComparison.Ordinal) + 3;

        var hit = AspxSymbolResolver.ResolveAt(document, offset);

        Assert.NotNull(hit);
        Assert.Equal(AspxHitKind.PropertyName, hit!.Kind);
        Assert.Equal("ItemTemplate", hit.Symbol!.Name);
        Assert.Equal("Repeater", hit.Symbol.ContainingType.Name);
    }

    [Fact]
    public async Task HoverOnATemplateTagDescribesTheProperty()
    {
        var hover = await AspxLanguageHandler.HoverAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.RepeaterAspxFile),
                PositionOf(FixturePaths.RepeaterAspxFile, "<ItemTemplate>", 3)),
            default);

        Assert.NotNull(hover);
        Assert.Contains("ItemTemplate", hover!.Contents.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoToDefinitionOnATemplateTagReachesThePropertyDeclaration()
    {
        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.RepeaterAspxFile),
                PositionOf(FixturePaths.RepeaterAspxFile, "<ItemTemplate>", 3)),
            typeDefinition: false,
            default);

        // The stubs declare Repeater.ItemTemplate, so it is a source location rather than metadata.
        Assert.NotEmpty(locations);
        Assert.Contains(locations, l => FileName(l.Uri) == "SystemWebStubs.cs");
    }

    private static string FileName(string uri) =>
        Path.GetFileName(LspConverters.UriToPath(uri));

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

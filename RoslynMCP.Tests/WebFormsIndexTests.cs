using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The markup index, and the three providers that exist because of it: <c>workspace/symbol</c>,
/// <c>semanticTokens</c> and <c>codeLens</c>.
/// </summary>
/// <remarks>
/// All three used to be answerable only by parsing every page in the project per request, which is
/// why none of them was answered at all. The index is what makes them affordable, so the tests for
/// it and for them belong together: a summary that stopped carrying control IDs would take
/// Ctrl+T and the reference counts with it.
/// </remarks>
[Collection(SharedState.Name)]
public class WebFormsIndexTests
{
    private static TextDocumentIdentifier Doc(string path) =>
        new(LspConverters.PathToUri(path));

    private static WebFormsLanguage Pack() => new(new MarkdownFormatter());

    // ---- The index -------------------------------------------------------------------------

    [Fact]
    public async Task TheSummaryNamesEveryControlAndTheClassBehindThePage()
    {
        var index = await WebFormsIndex.GetAsync(FixturePaths.DesignerAspxFile, default);

        Assert.NotNull(index);
        Assert.Equal("AspxProject.DesignerPage", index!.Inherits);
        Assert.Equal("DesignerPage", index.InheritsName);
        Assert.Equal("AspxProject", index.InheritsNamespace);

        string[] ids = [.. index.Controls.Select(c => c.Id)];
        Assert.Contains("designerForm", ids);
        Assert.Contains("lblHeading", ids);
        Assert.Contains("rptItems", ids);

        // Templates sit outside the child hierarchy, so a plain walk of it loses everything in
        // an ItemTemplate.
        Assert.Contains("lblNested", ids);

        var heading = index.Controls.Single(c => c.Id == "lblHeading");
        Assert.Equal("asp", heading.Prefix);
        Assert.Equal("Label", heading.TagName);
    }

    [Fact]
    public async Task TheSpanOfAnIdIsWhereTheIdIsWritten()
    {
        var index = await WebFormsIndex.GetAsync(FixturePaths.DesignerAspxFile, default);

        var heading = index!.Controls.Single(c => c.Id == "lblHeading");
        string line = (await File.ReadAllLinesAsync(FixturePaths.DesignerAspxFile))[heading.Span.Start.Line];

        Assert.Equal("lblHeading", line.Substring(
            heading.Span.Start.Character,
            heading.Span.End.Character - heading.Span.Start.Character));
    }

    [Fact]
    public async Task TagPrefixesIncludeTheOnesWebConfigRegisters()
    {
        var index = await WebFormsIndex.GetAsync(FixturePaths.DesignerAspxFile, default);

        // asp is injected because the compilation has System.Web in it; app and uc are only in
        // web.config, which is the input the file itself does not mention.
        Assert.Contains("asp", index!.TagPrefixes);
        Assert.Contains("app", index.TagPrefixes);
        Assert.Contains("uc", index.TagPrefixes);
    }

    [Fact]
    public async Task HandlerNamesComeFromTheAttributesThatNameThem()
    {
        var index = await WebFormsIndex.GetAsync(FixturePaths.RepeaterAspxFile, default);

        var handler = Assert.Single(index!.Handlers);
        Assert.Equal("OnItemDataBound", handler.AttributeName);
        Assert.Equal("rpt_OnItemDataBound", handler.MethodName);
    }

    [Fact]
    public async Task AnIdOnPlainHtmlIsNotAControl()
    {
        var index = await BuildAsync("""
            <%@ Page Language="C#" %>
            <div id="wrapper">
                <asp:Label ID="lblReal" runat="server" />
            </div>
            <span id="footer" runat="server"></span>
            """);

        // An HTML id belongs to nobody; the same attribute on a control names a field, and
        // runat="server" is the difference even without a prefix.
        Assert.Equal(["lblReal", "footer"], index.Controls.Select(c => c.Id));
    }

    [Fact]
    public async Task RegisteredUserControlsCarryTheFileTheyCameFrom()
    {
        var index = await BuildAsync("""
            <%@ Page Language="C#" %>
            <%@ Register TagPrefix="uc" TagName="Order" Src="~/Controls/OrderItems.ascx" %>
            <uc:Order runat="server" />
            """);

        var registration = Assert.Single(index.Registrations);
        Assert.Equal("uc", registration.Prefix);
        Assert.Equal("Order", registration.TagName);
        Assert.Equal("~/Controls/OrderItems.ascx", registration.SourcePath);
    }

    [Fact]
    public async Task TheSummaryIsReusedWhileTheFileStandsStill()
    {
        var first = await WebFormsIndex.GetAsync(FixturePaths.DesignerAspxFile, default);
        var second = await WebFormsIndex.GetAsync(FixturePaths.DesignerAspxFile, default);

        // The point of the whole file: a request that arrives on every keystroke must not walk
        // the tree again to answer the same question.
        Assert.Same(first, second);
    }

    [Fact]
    public async Task AProjectIsSummarizedOnceForAllOfItsMarkup()
    {
        var document = await AspxDocumentService.GetAsync(FixturePaths.DesignerAspxFile, default);

        var files = await WebFormsIndex.ForProjectAsync(document!.Project, default);

        Assert.Contains(files, f => f.FilePath.EndsWith("Designer.aspx", StringComparison.Ordinal));
        Assert.Contains(files, f => f.FilePath.EndsWith("Site.master", StringComparison.Ordinal));
        Assert.Contains(files, f => f.FilePath.EndsWith("OrderItems.ascx", StringComparison.Ordinal));
    }

    // ---- workspace/symbol ------------------------------------------------------------------

    [Fact]
    public async Task WorkspaceSymbolFindsAControlIdThatOnlyExistsInMarkup()
    {
        var document = await AspxDocumentService.GetAsync(FixturePaths.DesignerAspxFile, default);

        var symbols = await Pack().WorkspaceSymbolsAsync(
            "lblHeading", document!.Project.Solution, default);

        // Narrowed to the page under test rather than to the whole query, because a control ID is
        // only unique within its own markup: another fixture page declaring its own lblHeading is
        // a correct second answer, not a regression, and the assertions below are all about this
        // one.
        var heading = Assert.Single(
            symbols,
            s => s.Name == "lblHeading"
                && Uri.UnescapeDataString(s.Location.Uri).EndsWith("Designer.aspx", StringComparison.Ordinal));

        Assert.Equal(LspSymbolKind.Field, heading.Kind);
        Assert.Equal("DesignerPage", heading.ContainerName);
        Assert.EndsWith("Designer.aspx", Uri.UnescapeDataString(heading.Location.Uri));
    }

    [Fact]
    public async Task WorkspaceSymbolFindsThePageClassAtTheDirectiveThatNamesIt()
    {
        var document = await AspxDocumentService.GetAsync(FixturePaths.DesignerAspxFile, default);

        var symbols = await Pack().WorkspaceSymbolsAsync(
            "DesignerPage", document!.Project.Solution, default);

        // The markup half only: the code-behind's own declaration is Roslyn's to report, and it
        // is a different location in a different file.
        var page = Assert.Single(symbols, s => s.Location.Uri.EndsWith("Designer.aspx", StringComparison.Ordinal));
        Assert.Equal(LspSymbolKind.Class, page.Kind);
        Assert.Equal("AspxProject", page.ContainerName);
        Assert.Equal(0, page.Location.Range.Start.Line);
    }

    [Fact]
    public async Task AQueryThatMatchesNothingInMarkupReturnsNothing()
    {
        var document = await AspxDocumentService.GetAsync(FixturePaths.DesignerAspxFile, default);

        var symbols = await Pack().WorkspaceSymbolsAsync(
            "NoSuchThingAnywhere", document!.Project.Solution, default);

        Assert.Empty(symbols);
    }

    // ---- semanticTokens --------------------------------------------------------------------

    [Fact]
    public async Task AKnownControlIsColouredApartFromAControlThatDoesNotExist()
    {
        const string Markup = """
            <%@ Page Language="C#" %>
            <asp:Button ID="btnKnown" runat="server" Text="hello" OnClick="Ignored" />
            <asp:Buton ID="btnTypo" runat="server" />
            """;

        await WithTemporaryPageAsync("TokenColours.aspx", Markup, async path =>
        {
            var tokens = Decode(await Pack().SemanticTokensFullAsync(
                new SemanticTokensParams(Doc(path)), new LanguageSession([Pack()]), default));

            int known = TypeAt(Markup, tokens, "asp:Button");
            int unknown = TypeAt(Markup, tokens, "asp:Buton");

            // The whole reason markup answers semanticTokens at all: a TextMate grammar matches
            // the typo exactly as happily as the real control.
            Assert.NotEqual(known, unknown);
            Assert.Equal(LanguageSession.SharedTokenType("class"), known);

            // A colour C# has no name for, so it is the pack's own — past the end of C#'s legend.
            Assert.True(unknown >= SemanticTokensHandler.TokenTypes.Length,
                $"the unknown-control token must be the pack's own, not C# index {unknown}");

            // Recognised attribute names map onto the C# legend entries for what they really are.
            Assert.Equal(LanguageSession.SharedTokenType("property"), TypeAt(Markup, tokens, "Text=\"hello\""));
            Assert.Equal(LanguageSession.SharedTokenType("event"), TypeAt(Markup, tokens, "OnClick"));
        });
    }

    [Fact]
    public async Task TheDirectiveIsColouredWithoutSwallowingItsAttributes()
    {
        const string Markup = """
            <%@ Page Language="C#" %>
            <div>plain</div>
            """;

        await WithTemporaryPageAsync("TokenDirective.aspx", Markup, async path =>
        {
            var tokens = Decode(await Pack().SemanticTokensFullAsync(
                new SemanticTokensParams(Doc(path)), new LanguageSession([Pack()]), default));

            int macro = LanguageSession.SharedTokenType("macro");
            var directive = Assert.Single(tokens, t => t.Type == macro);
            Assert.Equal(0, directive.Line);

            // Language="C#" keeps the colours the grammar gives it, and so does the plain div.
            Assert.All(tokens, t => Assert.Equal(0, t.Line));
        });
    }

    [Fact]
    public async Task RangeTokensAreLimitedToTheWindow()
    {
        const string Markup = """
            <%@ Page Language="C#" %>
            <asp:Label ID="lblFirst" runat="server" />
            <asp:Label ID="lblSecond" runat="server" />
            """;

        await WithTemporaryPageAsync("TokenWindow.aspx", Markup, async path =>
        {
            var session = new LanguageSession([Pack()]);
            var window = new Lsp.Protocol.Range(new Position(2, 0), new Position(2, 60));

            var all = Decode(await Pack().SemanticTokensFullAsync(
                new SemanticTokensParams(Doc(path)), session, default));
            var visible = Decode(await Pack().SemanticTokensRangeAsync(
                new SemanticTokensRangeParams(Doc(path), window), session, default));

            Assert.Contains(all, t => t.Line == 1);
            Assert.All(visible, t => Assert.Equal(2, t.Line));
        });
    }

    // ---- codeLens --------------------------------------------------------------------------

    [Fact]
    public async Task EveryControlWithAFieldGetsALens()
    {
        var lenses = await Pack().CodeLensAsync(
            new CodeLensParams(Doc(FixturePaths.DesignerAspxFile)), default);

        var lines = await File.ReadAllLinesAsync(FixturePaths.DesignerAspxFile);
        string[] covered = [.. lenses.Select(l => lines[l.Range.Start.Line].Trim())];

        Assert.Contains(covered, line => line.Contains("lblHeading", StringComparison.Ordinal));

        // Declared by hand in the code-behind rather than by the designer, and still a field.
        Assert.Contains(covered, line => line.Contains("lblHandWritten", StringComparison.Ordinal));

        // A control inside a template is reached through FindControl and has no field, so there
        // is no declaration for a count to be about.
        Assert.DoesNotContain(covered, line => line.Contains("lblNested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALensDoesNotCountItsOwnDeclaration()
    {
        // The markup pass reports the ID attribute the way Roslyn reports a declaration, so
        // without the filter a control nothing uses would read "1 reference".
        new LanguageRegistry([Pack()]).Publish();

        var lenses = await Pack().CodeLensAsync(
            new CodeLensParams(Doc(FixturePaths.DesignerAspxFile)), default);

        var lines = await File.ReadAllLinesAsync(FixturePaths.DesignerAspxFile);
        var lens = Assert.Single(lenses, l => lines[l.Range.Start.Line].Contains("btnSave", StringComparison.Ordinal));

        Assert.Null(lens.Command);

        var resolved = await Pack().ResolveCodeLensAsync(lens, default);

        Assert.NotNull(resolved.Command);
        Assert.Equal("roslynSense.showReferences", resolved.Command!.Name);
        Assert.Equal("0 references", resolved.Command.Title);
    }

    [Fact]
    public async Task AMarkupFileWithNoCodeBehindGetsNoLenses()
    {
        // Site.master declares controls but names no class, so no ID is a field declaration.
        var lenses = await Pack().CodeLensAsync(
            new CodeLensParams(Doc(FixturePaths.SiteMasterFile)), default);

        Assert.Empty(lenses);
    }

    [Fact]
    public async Task AUserControlGetsNoLenses()
    {
        // OrderItems.ascx names a class and declares rptOrderItems, so every condition for a lens
        // is met — a .ascx is close to nothing but control declarations, and a count over each one
        // spaces the markup out rather than annotating it.
        var lenses = await Pack().CodeLensAsync(
            new CodeLensParams(Doc(FixturePaths.OrderItemsAscxFile)), default);

        Assert.Empty(lenses);
    }

    // ---- Helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Summarizes markup that is not on disk, against the fixture project's real compilation so
    /// that prefixes and control types resolve the way they would in the editor.
    /// </summary>
    private static async Task<WebFormsFileIndex> BuildAsync(string markup)
    {
        var host = await AspxDocumentService.GetAsync(FixturePaths.DesignerAspxFile, default);
        string path = Path.Combine(FixturePaths.AspxProjectDir, "InMemory.aspx");

        var parse = AspxSourceMappingService.Parse(
            path, markup, host!.Compilation, rootDirectory: FixturePaths.AspxProjectDir);

        return WebFormsIndex.Build(path, parse.ParseTree!);
    }

    /// <summary>
    /// Runs the body against a real file in the fixture project, because the LSP entry points
    /// resolve a URI through the workspace and there is no in-memory way in.
    /// </summary>
    /// <remarks>
    /// The page deliberately names no <c>Inherits</c>: the designer regeneration tests watch this
    /// directory, and a page claiming a code-behind class would have them write fields for its
    /// controls into a fixture that other tests assert on.
    /// </remarks>
    private static async Task WithTemporaryPageAsync(
        string fileName, string markup, Func<string, Task> body)
    {
        string path = Path.Combine(FixturePaths.AspxProjectDir, fileName);
        await File.WriteAllTextAsync(path, markup);

        try
        {
            await body(path);
        }
        finally
        {
            AspxDocumentService.Invalidate(path);
            File.Delete(path);
        }
    }

    private static List<(int Line, int Char, int Length, int Type)> Decode(SemanticTokens tokens)
    {
        var decoded = new List<(int, int, int, int)>();
        int line = 0, character = 0;

        for (int i = 0; i < tokens.Data.Length; i += 5)
        {
            line += tokens.Data[i];
            character = tokens.Data[i] == 0 ? character + tokens.Data[i + 1] : tokens.Data[i + 1];
            decoded.Add((line, character, tokens.Data[i + 2], tokens.Data[i + 3]));
        }

        return decoded;
    }

    /// <summary>The token type at the position <paramref name="needle"/> starts on.</summary>
    private static int TypeAt(
        string text, List<(int Line, int Char, int Length, int Type)> tokens, string needle)
    {
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in the markup");

        var position = SourceText.From(text).Lines.GetLinePosition(index);
        var token = Assert.Single(
            tokens, t => t.Line == position.Line && t.Char == position.Character);

        return token.Type;
    }
}

using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The four editor features the WebForms pack answers from the parse tree alone — linked
/// editing, selection ranges, auto-insert and document links.
/// </summary>
/// <remarks>
/// Each goes through the real <see cref="AspxDocumentService"/>, so the markup is parsed against
/// the fixture project's compilation rather than a stub. The two that need markup no fixture
/// contains supply it through <see cref="OpenDocumentStore"/>, which is also how the feature is
/// reached in practice: an unsaved buffer mid-keystroke.
/// </remarks>
[Collection(SharedState.Name)]
public class WebFormsEditorFeatureTests
{
    private static readonly WebFormsLanguage s_language = new(new MarkdownFormatter());

    private static TextDocumentIdentifier Doc(string path) =>
        new(LspConverters.PathToUri(path));

    private static Position PositionOf(string text, string needle, int offsetIntoNeedle = 0)
    {
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in the document");

        var line = SourceText.From(text).Lines.GetLinePosition(index + offsetIntoNeedle);
        return new Position(line.Line, line.Character);
    }

    private static string TextOf(string document, Lsp.Protocol.Range range)
    {
        var source = SourceText.From(document);
        return source.ToString(LspConverters.ToTextSpan(source, range));
    }

    /// <summary>Runs <paramref name="body"/> with <paramref name="path"/> showing
    /// <paramref name="buffer"/> instead of what is on disk.</summary>
    private static async Task WithBufferAsync(string path, string buffer, Func<Task> body)
    {
        string session = Guid.NewGuid().ToString("N");
        try
        {
            OpenDocumentStore.Open(session, path, SourceText.From(buffer), 1);
            await body();
        }
        finally
        {
            OpenDocumentStore.Close(session, path);
            AspxDocumentService.Invalidate(path);
        }
    }

    [Fact]
    public async Task LinkedEditingPairsATagWithItsCloseTag()
    {
        string path = FixturePaths.DesignerAspxFile;
        string text = await File.ReadAllTextAsync(path);

        var ranges = await s_language.LinkedEditingRangesAsync(
            new TextDocumentPositionParams(Doc(path), PositionOf(text, "asp:Repeater", 4)),
            default);

        Assert.NotNull(ranges);
        Assert.Equal(2, ranges!.Ranges.Length);

        // Both ends carry the prefix, so retyping "asp" keeps the pair in step as well as
        // retyping "Repeater" does.
        Assert.All(ranges.Ranges, r => Assert.Equal("asp:Repeater", TextOf(text, r)));
        Assert.True(ranges.Ranges[0].Start.Line < ranges.Ranges[1].Start.Line);
    }

    [Fact]
    public async Task SelectionRangeWidensFromAnAttributeValueOutThroughTheElement()
    {
        string path = FixturePaths.DesignerAspxFile;
        string text = await File.ReadAllTextAsync(path);

        var chains = await s_language.SelectionRangesAsync(
            new SelectionRangeParams(Doc(path), [PositionOf(text, "\"Heading\"", 3)]),
            default);

        var innermost = Assert.Single(chains);

        // value -> attribute -> open tag -> element, which is the chain Ctrl+W should walk.
        Assert.Equal("Heading", TextOf(text, innermost.Range));

        var attribute = innermost.Parent;
        Assert.NotNull(attribute);
        Assert.Equal("Text=\"Heading\"", TextOf(text, attribute!.Range));

        var startTag = attribute.Parent;
        Assert.NotNull(startTag);
        Assert.StartsWith("<asp:Label", TextOf(text, startTag!.Range));
        Assert.EndsWith("/>", TextOf(text, startTag.Range));

        // The chain ends at the whole file, so expanding never dead-ends part way up.
        var outermost = startTag;
        while (outermost.Parent is { } parent)
            outermost = parent;
        Assert.Equal(new Position(0, 0), outermost.Range.Start);
    }

    [Fact]
    public async Task AutoInsertClosesATagThatHasNoCloseTagYet()
    {
        string path = FixturePaths.DesignerAspxFile;
        string original = await File.ReadAllTextAsync(path);

        // Appended past </html> so the rest of the page still lexes as it does on disk; this is
        // the state the buffer is in the instant the user types the ">".
        const string OpenTag = "<asp:Panel ID=\"pnlNew\" runat=\"server\">";
        string buffer = original + Environment.NewLine + OpenTag;
        var caret = PositionOf(buffer, OpenTag, OpenTag.Length);

        await WithBufferAsync(path, buffer, async () =>
        {
            var result = await s_language.OnAutoInsertAsync(
                new OnAutoInsertParams(Doc(path), caret), default);

            Assert.NotNull(result);
            Assert.Equal("</asp:Panel>", result!.Edit.NewText);
            Assert.Equal(caret, result.Edit.Range.Start);
            Assert.Equal(caret, result.Edit.Range.End);

            // The caret stays between the tags, which is where the content goes.
            Assert.Equal(caret, result.Cursor);
        });
    }

    [Fact]
    public async Task AutoInsertFinishesAServerComment()
    {
        string path = FixturePaths.DesignerAspxFile;
        string original = await File.ReadAllTextAsync(path);

        string buffer = original + Environment.NewLine + "<%--";
        var caret = PositionOf(buffer, "<%--", 4);

        await WithBufferAsync(path, buffer, async () =>
        {
            var result = await s_language.OnAutoInsertAsync(
                new OnAutoInsertParams(Doc(path), caret), default);

            Assert.NotNull(result);
            Assert.Equal(" --%>", result!.Edit.NewText);
            Assert.Equal(new Position(caret.Line, caret.Character + 1), result.Cursor);
        });
    }

    [Fact]
    public async Task DocumentLinksCoverTheFilesAPageNamesAndNothingElse()
    {
        string path = FixturePaths.DesignerAspxFile;
        string original = await File.ReadAllTextAsync(path);

        // MasterPageFile is app-root relative, CodeBehind is relative to the page itself, and
        // missing.js is neither — it is not on disk, so it must not be underlined at all.
        string buffer = original
            .Replace(
                "CodeBehind=\"Designer.aspx.cs\"",
                "CodeBehind=\"Designer.aspx.cs\" MasterPageFile=\"~/Site.master\"")
            .Replace("</body>", "    <script src=\"missing.js\"></script>\r\n</body>");

        await WithBufferAsync(path, buffer, async () =>
        {
            var links = await s_language.DocumentLinksAsync(
                new DocumentLinkParams(Doc(path)), default);

            Assert.Contains(links, l => l.Target!.EndsWith("Site.master", StringComparison.Ordinal));
            Assert.Contains(links, l => l.Target!.EndsWith("Designer.aspx.cs", StringComparison.Ordinal));
            Assert.DoesNotContain(links, l => l.Target!.Contains("missing.js", StringComparison.Ordinal));

            var master = links.First(l => l.Target!.EndsWith("Site.master", StringComparison.Ordinal));
            Assert.Equal("~/Site.master", TextOf(buffer, master.Range));
        });
    }
}

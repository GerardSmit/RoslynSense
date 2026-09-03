using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Tests;

/// <summary>
/// F12 on a control field in a code-behind, which has to land on the <c>ID</c> in the markup.
/// </summary>
/// <remarks>
/// The designer file is a transcription: it restates the <c>ID</c> and the control's type and
/// nothing else, and the next regeneration overwrites it. Roslyn cannot know that — the field is a
/// perfectly ordinary declaration in a perfectly ordinary <c>.cs</c> — so F12 on <c>lblHeading</c>
/// answered with <c>Designer.aspx.designer.cs</c>, which is where the question came from rather than
/// where it is answered. Everything the reader wants (the tag, its properties, its handler wiring)
/// is on the line the designer was generated from.
/// </remarks>
[Collection(SharedState.Name)]
public class WebFormsDefinitionContributorTests
{
    [Fact]
    public async Task DefinitionOnADesignerGeneratedControlFieldLandsOnTheIdInTheMarkup()
    {
        PublishWebFormsPack();

        var locations = await DefinitionAsync("lblHeading.Text", 2);

        // Single, and that is half of it: before the contribution this was one location too, and
        // it was the designer.
        var location = Assert.Single(locations);

        AssertFile(FixturePaths.DesignerAspxFile, location.Uri);

        // The ID's own value, not the tag or the line: the field is declared by that attribute and
        // a jump that selects it puts the caret where an F2 would act.
        Assert.Equal("lblHeading", TextAt(location));
    }

    [Fact]
    public async Task TheDesignerIsNotOfferedBesideTheMarkupItWasGeneratedFrom()
    {
        PublishWebFormsPack();

        var locations = await DefinitionAsync("lblHeading.Text", 2);

        // By path, which is what the reporter read off the editor's tab. A second entry turns F12
        // from a jump into a picker, and half of the choice is a file the next regeneration
        // rewrites.
        Assert.DoesNotContain(
            locations, location => SamePath(location.Uri, FixturePaths.DesignerAspxDesignerFile));
    }

    /// <summary>
    /// The limit of the withdrawal: a field the author wrote by hand keeps its declaration.
    /// </summary>
    /// <remarks>
    /// <c>lblHandWritten</c> is declared in <c>Designer.aspx.cs</c>, a file nothing regenerates, and
    /// the markup is offered beside it rather than instead of it. Withdrawing that one too would be
    /// the pack deciding markup is the more interesting half of every page — which is false as soon
    /// as the code-behind configures the control.
    /// </remarks>
    [Fact]
    public async Task AHandWrittenControlFieldKeepsItsCSharpDeclarationAndGainsTheMarkup()
    {
        PublishWebFormsPack();

        var locations = await DefinitionAsync("lblHandWritten.Text", 2);

        Assert.Equal(2, locations.Length);

        var markup = Assert.Single(
            locations, location => SamePath(location.Uri, FixturePaths.DesignerAspxFile));
        Assert.Equal("lblHandWritten", TextAt(markup));

        Assert.Contains(
            locations, location => SamePath(location.Uri, FixturePaths.DesignerAspxCodeBehindFile));
    }

    /// <summary>
    /// A control inside a template has no field, so nothing about it changes.
    /// </summary>
    /// <remarks>
    /// <c>lblNested</c> lives in the repeater's <c>ItemTemplate</c> and is reached through
    /// <c>FindControl</c>; the designer generates nothing for it. The assertion is that the
    /// contributor answers on the field's name rather than on any <c>ID</c> that happens to be in
    /// the file — a match by file alone would make F12 on one control jump to another.
    /// </remarks>
    [Fact]
    public async Task AnIdWithNoCodeBehindFieldIsNotContributedForADifferentField()
    {
        PublishWebFormsPack();

        var locations = await DefinitionAsync("lblHeading.Text", 2);

        foreach (var location in locations)
            Assert.NotEqual("lblNested", TextAt(location));
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static void PublishWebFormsPack() =>
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

    private static TextDocumentIdentifier Doc(string path) => new(LspConverters.PathToUri(path));

    private static Task<LspLocation[]> DefinitionAsync(string needle, int offsetIntoNeedle) =>
        NavigationHandlers.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.DesignerAspxCodeBehindFile),
                PositionOf(FixturePaths.DesignerAspxCodeBehindFile, needle, offsetIntoNeedle)),
            typeDefinition: false,
            default);

    private static Position PositionOf(string path, string needle, int offsetIntoNeedle)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");

        var line = SourceText.From(text).Lines.GetLinePosition(index + offsetIntoNeedle);
        return new Position(line.Line, line.Character);
    }

    /// <summary>The source the location's range actually covers, so an assertion is about the
    /// declaration's own name rather than about a line number.</summary>
    private static string TextAt(LspLocation location)
    {
        var text = SourceText.From(File.ReadAllText(LspConverters.UriToPath(location.Uri)));
        return text.ToString(LspConverters.ToTextSpan(text, location.Range));
    }

    private static bool SamePath(string uri, string path) =>
        string.Equals(
            Path.GetFullPath(LspConverters.UriToPath(uri)),
            Path.GetFullPath(path),
            StringComparison.OrdinalIgnoreCase);

    private static void AssertFile(string expected, string uri) =>
        Assert.Equal(
            Path.GetFullPath(expected),
            Path.GetFullPath(LspConverters.UriToPath(uri)),
            StringComparer.OrdinalIgnoreCase);
}

using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Languages.WebForms.Tools;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Tools;
using Xunit;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Tests;

/// <summary>
/// F12 across the FindControl seam, in both directions: from the <c>"id"</c> literal in C# to the
/// markup <c>ID</c> it names, and from a template-nested <c>ID</c> to the call sites that reach it.
/// </summary>
/// <remarks>
/// A control inside a multi-instance template has no designer field — the string literal is the
/// only thing in C# that refers to it, and the <c>ID</c> attribute is the only declaration it has.
/// Which <c>ID</c> a literal means depends on the naming container the lookup runs in, which is
/// what <c>NamingScope.aspx</c> exists to pin down: the same id declared in two repeaters resolves
/// to one of them inside the handler wired to the first, and to both from a method wired to
/// neither.
/// </remarks>
[Collection(SharedState.Name)]
public class WebFormsFindControlNavigationTests
{
    [Fact]
    public async Task AFindControlLiteralNavigatesToTheTemplateId()
    {
        PublishWebFormsPack();

        var locations = await DefinitionAsync(
            FixturePaths.RepeaterCodeBehindFile, "FindControl(\"btnAction\")", 13);

        var location = Assert.Single(locations);
        AssertFile(FixturePaths.RepeaterAspxFile, location.Uri);
        Assert.Equal("btnAction", TextAt(location));
    }

    /// <summary>
    /// The same jump through a wrapper — <c>SetText("btnAction", …)</c> forwards its parameter to
    /// <c>FindControl</c>, which is what makes its argument a control id.
    /// </summary>
    [Fact]
    public async Task AWrapperCallsIdLiteralNavigatesToTheTemplateId()
    {
        PublishWebFormsPack();
        await WarmWrappersAsync(FixturePaths.AspxProjectFile);

        var locations = await DefinitionAsync(
            FixturePaths.RepeaterCodeBehindFile, "SetText(\"btnAction\"", 9);

        var location = Assert.Single(locations);
        AssertFile(FixturePaths.RepeaterAspxFile, location.Uri);
        Assert.Equal("btnAction", TextAt(location));
    }

    /// <summary>
    /// Both repeaters declare <c>lblDup</c>, and the lookup runs inside <c>rptA</c>'s
    /// ItemDataBound handler — so only rptA's declaration answers.
    /// </summary>
    [Fact]
    public async Task AHandlerScopedLookupStaysInsideItsOwnNamingContainer()
    {
        PublishWebFormsPack();

        var locations = await DefinitionAsync(
            FixturePaths.NamingScopeCodeBehindFile, "e.Item.FindControl(\"lblDup\")", 20);

        var location = Assert.Single(locations);
        AssertFile(FixturePaths.NamingScopeAspxFile, location.Uri);
        Assert.Equal("lblDup", TextAt(location));

        // rptA's declaration is the first lblDup in the file; rptB's sits further down.
        var expected = PositionOf(FixturePaths.NamingScopeAspxFile, "lblDup");
        Assert.Equal(expected.Line, location.Range.Start.Line);
        Assert.Equal(expected.Character, location.Range.Start.Character);
    }

    /// <summary>A lookup from a method wired to no control cannot pick a container, so every
    /// declaration in the page answers.</summary>
    [Fact]
    public async Task ALookupOutsideAnyHandlerReturnsEveryDeclarationInThePage()
    {
        PublishWebFormsPack();

        var locations = await DefinitionAsync(
            FixturePaths.NamingScopeCodeBehindFile, "rptA.FindControl(\"lblDup\")", 18);

        Assert.Equal(2, locations.Length);
        Assert.All(locations, location =>
        {
            AssertFile(FixturePaths.NamingScopeAspxFile, location.Uri);
            Assert.Equal("lblDup", TextAt(location));
        });
        Assert.Equal(2, locations.Select(l => l.Range.Start.Line).Distinct().Count());
    }

    /// <summary>
    /// A computed id is not a literal argument of the call — the literal's parent is the
    /// concatenation — so the language never claims it and F12 stays empty rather than guessing.
    /// </summary>
    [Fact]
    public async Task AComputedIdIsNotClaimed()
    {
        PublishWebFormsPack();

        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(
            FixturePaths.NamingScopeCodeBehindFile);
        string text = File.ReadAllText(FixturePaths.NamingScopeCodeBehindFile);
        int offset = text.IndexOf("prefix + \"Dup\"", StringComparison.Ordinal) + 11;

        Assert.Null(await RoslynEmbeddedLanguages.Current.DetectAsync(document, offset, default));

        var locations = await DefinitionAsync(
            FixturePaths.NamingScopeCodeBehindFile, "prefix + \"Dup\"", 10);
        Assert.Empty(locations);
    }

    /// <summary>
    /// The reverse gesture: F12 on the template-nested <c>ID</c> itself, which used to return
    /// nothing because no symbol binds to it. Its usages are the FindControl call sites — the
    /// direct call and the wrapper call both.
    /// </summary>
    [Fact]
    public async Task ATemplateNestedIdListsItsFindControlCallSites()
    {
        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.RepeaterAspxFile),
                PositionOf(FixturePaths.RepeaterAspxFile, "\"btnAction\"", 3)),
            typeDefinition: false,
            default);

        Assert.Equal(2, locations.Length);
        Assert.All(locations, location =>
        {
            AssertFile(FixturePaths.RepeaterCodeBehindFile, location.Uri);
            Assert.Equal("btnAction", TextAt(location));
        });
    }

    /// <summary>Shift+F12 on the same caret gives the same call sites instead of an empty list.</summary>
    [Fact]
    public async Task ReferencesOnATemplateNestedIdListTheSameCallSites()
    {
        var position = PositionOf(FixturePaths.RepeaterAspxFile, "\"btnAction\"", 3);

        var locations = await AspxLanguageHandler.ReferencesAsync(
            new ReferenceParams(
                Doc(FixturePaths.RepeaterAspxFile), position,
                new ReferenceContext(IncludeDeclaration: false)),
            default);

        Assert.Equal(2, locations.Length);
        Assert.All(locations, location =>
            AssertFile(FixturePaths.RepeaterCodeBehindFile, location.Uri));
    }

    /// <summary>
    /// The wrappers live in a referenced class library, the way real sites keep them — the scan
    /// has to cross the project reference for the literal to resolve at all.
    /// </summary>
    [Fact]
    public async Task AWrapperFromAReferencedProjectNavigatesToTheTemplateId()
    {
        PublishWebFormsPack();
        await WarmWrappersAsync(FixturePaths.WebAppProjectFile);

        var locations = await DefinitionAsync(
            FixturePaths.CrossCodeBehindFile, "\"lblCross\"", 1);

        var location = Assert.Single(locations);
        AssertFile(FixturePaths.CrossAspxFile, location.Uri);
        Assert.Equal("lblCross", TextAt(location));
    }

    /// <summary>The MCP tool rides the same seam: a snippet marking the literal answers with the
    /// markup file rather than "no symbol found".</summary>
    [Fact]
    public async Task GoToDefinitionSnippetOnTheLiteralAnswersWithTheMarkup()
    {
        PublishWebFormsPack();

        string result = await GoToDefinitionSnippetTool.GoToDefinitionSnippet(
            FixturePaths.RepeaterCodeBehindFile,
            "var btn = item.FindControl(\"[|btnAction|]\")",
            new MarkdownFormatter());

        Assert.Contains("Repeater.aspx", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No symbol", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The markup-side MCP tool: a marked template-nested <c>ID</c> answers with the
    /// call sites rather than "no symbol found".</summary>
    [Fact]
    public async Task AspxGoToDefinitionOnATemplateNestedIdAnswersWithTheCallSites()
    {
        var tool = new AspxGoToDefinition(new MarkdownFormatter());

        string result = await tool.ResolveAsync(
            FixturePaths.RepeaterAspxFile,
            "<asp:Button ID=\"[|btnAction|]\" runat=\"server\"",
            contextLines: 2,
            default);

        Assert.Contains("Repeater.aspx.cs", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FindControl", result, StringComparison.Ordinal);
        Assert.DoesNotContain("No symbol", result, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static void PublishWebFormsPack() =>
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

    /// <summary>The wrapper scan the synchronous detection pass reads from — awaited here so a
    /// test asserts on resolution rather than on cold-start timing.</summary>
    private static async Task WarmWrappersAsync(string projectFile)
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(projectFile);
        await ProjectIndexCacheService.GetFindControlWrappersAsync(project);
    }

    private static TextDocumentIdentifier Doc(string path) => new(LspConverters.PathToUri(path));

    private static Task<LspLocation[]> DefinitionAsync(
        string path, string needle, int offsetIntoNeedle) =>
        NavigationHandlers.DefinitionAsync(
            new TextDocumentPositionParams(Doc(path), PositionOf(path, needle, offsetIntoNeedle)),
            typeDefinition: false,
            default);

    private static Position PositionOf(string path, string needle, int offsetIntoNeedle = 0)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");

        var line = SourceText.From(text).Lines.GetLinePosition(index + offsetIntoNeedle);
        return new Position(line.Line, line.Character);
    }

    /// <summary>The source the location's range actually covers, so an assertion is about the
    /// id's own name rather than about a line number.</summary>
    private static string TextAt(LspLocation location)
    {
        var text = SourceText.From(File.ReadAllText(LspConverters.UriToPath(location.Uri)));
        return text.ToString(LspConverters.ToTextSpan(text, location.Range));
    }

    private static void AssertFile(string expected, string uri) =>
        Assert.Equal(
            Path.GetFullPath(expected),
            Path.GetFullPath(LspConverters.UriToPath(uri)),
            StringComparer.OrdinalIgnoreCase);
}

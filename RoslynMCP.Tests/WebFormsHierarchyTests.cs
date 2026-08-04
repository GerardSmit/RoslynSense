using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Tests;

/// <summary>
/// Call and type hierarchy started from markup. The point of every assertion here is the same:
/// the answers come out of the projected C#, and the URIs that come back have to be the markup
/// file — a <c>.aspx-inline.g.cs</c> is a document only Roslyn can see.
/// </summary>
/// <remarks>
/// The last test goes the other way, from C#. A hierarchy rooted in the code-behind has to reach
/// the markup that calls it: find-references at the identical caret does, and two navigation
/// features disagreeing about one symbol is worse than either answer on its own.
/// </remarks>
[Collection(SharedState.Name)]
public class WebFormsHierarchyTests
{
    private static WebFormsLanguage Pack() => new(new MarkdownFormatter());

    private static Position PositionOf(string path, string needle, int offsetIntoNeedle = 0)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");

        var line = SourceText.From(text).Lines.GetLinePosition(index + offsetIntoNeedle);
        return new Position(line.Line, line.Character);
    }

    /// <summary>The text of the line a range starts on, so a mapped range can be checked against
    /// the markup it claims to point at.</summary>
    private static string LineAt(string path, LspRange range) =>
        File.ReadAllLines(path)[range.Start.Line];

    private static TextDocumentPositionParams At(string path, Position position) =>
        new(new TextDocumentIdentifier(LspConverters.PathToUri(path)), position);

    private static void AssertOpenable(string uri) =>
        Assert.DoesNotContain(".aspx-inline.g.cs", Uri.UnescapeDataString(uri));

    [Fact]
    public async Task PrepareTypeHierarchyFromMarkupAnswersWithThePageClassInTheMarkup()
    {
        string path = FixturePaths.EventWiringAspxFile;

        var items = await Pack().PrepareTypeHierarchyAsync(
            At(path, PositionOf(path, "AspxProject.EventWiringPage")), default);

        var item = Assert.Single(items);
        Assert.Equal("EventWiringPage", item.Name);
        AssertOpenable(item.Uri);
        Assert.EndsWith("EventWiring.aspx", Uri.UnescapeDataString(item.Uri), StringComparison.Ordinal);

        // The directive is where the file declares which class it is, so that is what the item
        // selects — and resolving that position again is what makes supertypes work.
        Assert.Contains("Inherits", LineAt(path, item.SelectionRange));
    }

    [Fact]
    public async Task SupertypesOfThePageClassReachPage()
    {
        string path = FixturePaths.EventWiringAspxFile;
        var pack = Pack();

        var items = await pack.PrepareTypeHierarchyAsync(
            At(path, PositionOf(path, "AspxProject.EventWiringPage")), default);

        var supertypes = await pack.SupertypesAsync(
            new TypeHierarchyItemParams(Assert.Single(items)), default);

        Assert.Contains(supertypes, s => s.Name == "Page");
        Assert.All(supertypes, s => AssertOpenable(s.Uri));
    }

    [Fact]
    public async Task PrepareTypeHierarchyOnAnOverrideWrittenInMarkupAnswersWithThePageClass()
    {
        // The override is a member of the merged partial type, so the type the caret is inside of
        // is the page class — which the markup declares nowhere but in its directive.
        string path = FixturePaths.EventWiringAspxFile;

        var items = await Pack().PrepareTypeHierarchyAsync(
            At(path, PositionOf(path, "OnLoad(System.EventArgs e)", 2)), default);

        var item = Assert.Single(items);
        Assert.Equal("EventWiringPage", item.Name);
        Assert.EndsWith("EventWiring.aspx", Uri.UnescapeDataString(item.Uri), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareCallHierarchyOnAMethodDeclaredInMarkupPointsAtTheMarkup()
    {
        string path = FixturePaths.EventWiringAspxFile;

        var items = await Pack().PrepareCallHierarchyAsync(
            At(path, PositionOf(path, "Doubled()", 2)), default);

        var item = Assert.Single(items);
        Assert.Equal("Doubled", item.Name);
        AssertOpenable(item.Uri);
        Assert.EndsWith("EventWiring.aspx", Uri.UnescapeDataString(item.Uri), StringComparison.Ordinal);
        Assert.Contains("Doubled", LineAt(path, item.SelectionRange));
    }

    [Fact]
    public async Task OutgoingCallsFromAMarkupMethodLandOnTheCodeBehindAndReportTheMarkupCall()
    {
        string path = FixturePaths.EventWiringAspxFile;
        var pack = Pack();

        var items = await pack.PrepareCallHierarchyAsync(
            At(path, PositionOf(path, "Doubled()", 2)), default);

        var calls = await pack.OutgoingCallsAsync(
            new CallHierarchyCallsParams(Assert.Single(items)), default);

        var total = Assert.Single(calls, c => c.To.Name == "Total");
        Assert.EndsWith(
            "EventWiring.aspx.cs", Uri.UnescapeDataString(total.To.Uri), StringComparison.Ordinal);

        // The call itself is in the markup: fromRanges are read against the item's file.
        var range = Assert.Single(total.FromRanges);
        Assert.Contains("Total()", LineAt(path, range));
    }

    [Fact]
    public async Task AnOverrideInMarkupCallsTheBaseMethodItOverrides()
    {
        // Nothing else proves the projection puts script-block members on the code-behind type:
        // base.OnLoad only binds if the markup member really is an override of Control.OnLoad.
        string path = FixturePaths.EventWiringAspxFile;
        var pack = Pack();

        var items = await pack.PrepareCallHierarchyAsync(
            At(path, PositionOf(path, "OnLoad(System.EventArgs e)", 2)), default);

        var item = Assert.Single(items);
        Assert.Equal("OnLoad", item.Name);
        Assert.EndsWith("EventWiring.aspx", Uri.UnescapeDataString(item.Uri), StringComparison.Ordinal);

        var calls = await pack.OutgoingCallsAsync(new CallHierarchyCallsParams(item), default);

        var baseCall = Assert.Single(calls, c => c.To.Name == "OnLoad");
        Assert.EndsWith(
            "SystemWebStubs.cs", Uri.UnescapeDataString(baseCall.To.Uri), StringComparison.Ordinal);
        Assert.All(calls, c => AssertOpenable(c.To.Uri));
    }

    [Fact]
    public async Task IncomingCallsOnACodeBehindMethodReachTheMarkupThatCallsIt()
    {
        // Total is declared in EventWiring.aspx.cs and called from nowhere but EventWiring.aspx —
        // once from a method in <script runat="server"> and once from <%= %>. The hierarchy is
        // rooted on a .cs URI, so the request goes to the C# handler over the real workspace, where
        // neither call site exists; only the call hierarchy contributor puts them back. Publishing
        // the registry is what makes that possible — with no packs registered the C# answer is all
        // there is.
        new LanguageRegistry([Pack()]).Publish();

        string codeBehind = FixturePaths.EventWiringCodeBehindFile;
        string aspx = FixturePaths.EventWiringAspxFile;
        var caret = At(codeBehind, PositionOf(codeBehind, "Total() => 42"));

        var items = await CallHierarchyHandler.PrepareAsync(caret, default);
        var total = Assert.Single(items);
        Assert.Equal("Total", total.Name);

        var incoming = await CallHierarchyHandler.IncomingCallsAsync(
            new CallHierarchyCallsParams(total), default);

        // Every URI stays openable: reporting the projection would be the bug this file exists to
        // prevent, and it is the one way a markup call site could be "reported" and still useless.
        Assert.All(incoming, c => AssertOpenable(c.From.Uri));

        var markup = incoming
            .Where(c => Uri.UnescapeDataString(c.From.Uri)
                .EndsWith("EventWiring.aspx", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, markup.Count);

        var fromScript = Assert.Single(markup, c => c.From.Name == "Doubled");
        Assert.Contains("Total()", LineAt(aspx, Assert.Single(fromScript.FromRanges)));

        // The <%= %> was lifted into a method the projection invented, which has no declaration
        // anyone can open, so the page class stands in for it at the call itself.
        var fromInline = Assert.Single(markup, c => c.From.Name == "EventWiringPage");
        Assert.Contains("<%= Total() %>", LineAt(aspx, Assert.Single(fromInline.FromRanges)));

        // The point of the whole exercise: references at the identical caret already reported these
        // call sites, and the two features have to agree about them.
        var references = await NavigationHandlers.ReferencesAsync(
            new ReferenceParams(
                caret.TextDocument, caret.Position, new ReferenceContext(IncludeDeclaration: false)),
            default);

        Assert.Contains(references, r => Uri.UnescapeDataString(r.Uri)
            .EndsWith("EventWiring.aspx", StringComparison.Ordinal));
    }
}

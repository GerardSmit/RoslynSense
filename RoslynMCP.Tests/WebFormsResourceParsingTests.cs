using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Languages;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Services;
using WebFormsCore.Models;
using WebFormsCore.Nodes;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Expression builders and implicit-localization keys as the parser sees them: three shapes that
/// used to be read as C# or as a property name, and are neither.
/// </summary>
/// <remarks>
/// Every failure here is silent. A builder read as a statement produces a CS syntax error against
/// text the user cannot fix; a builder in an attribute produces an empty value and a node in the
/// wrong container; and <c>meta:resourcekey</c> produces a warning on every implicit-localization
/// page there is. Nothing throws, so a test that looks at the tree is the only way any of it shows.
/// </remarks>
[Collection(SharedState.Name)]
public class WebFormsResourceParsingTests
{
    [Fact]
    public async Task ABuilderInElementContentIsANodeOfItsOwnAndReachesNoProjection()
    {
        var document = await AspxDocumentService.GetAsync(FixturePaths.LocalizedAspxFile, default);
        Assert.NotNull(document);

        // Three builders on the page and exactly one node: the two written in attribute values
        // belong to the control they sit on, and none of the three is a statement.
        var builder = Assert.Single(document!.Tree!.AllChildren.OfType<ExpressionBuilderNode>());
        Assert.Equal("Resources", builder.Prefix.Value);
        Assert.Equal("Heading", builder.Argument.Value);
        Assert.Empty(document.Tree.AllChildren.OfType<StatementNode>());

        // The page's only `<% … %>` regions are those three builders, and a projection with no
        // copied run in it is not built at all — so there is no C# document for a diagnostic to
        // be reported against. Read as statements they would have been copied into
        // `__AspxInline0` verbatim, and `$ Resources: Heading` is a syntax error against text the
        // user has no way to correct.
        Assert.Null(AspxProjectionService.Get(document));
    }

    [Fact]
    public async Task ABuilderInAQuotedAttributeIsTheAttributesValue()
    {
        var document = await AspxDocumentService.GetAsync(FixturePaths.LocalizedAspxFile, default);
        Assert.NotNull(document);

        var label = Assert.Single(
            AspxSymbolResolver.EnumerateControls(document!.Tree!), c => c.Id == "lblCatalogue");

        var value = label.RawAttributes.Single(a => a.Key.Value == "Text").Value;

        Assert.Equal(AttributeValueKind.ExpressionBuilder, value.Kind);
        Assert.Equal("Resources", value.Prefix.Value);
        Assert.Equal("Strings, Title", value.Value);

        // The range is the argument alone, so an edit to the key replaces the key rather than the
        // delimiters around it.
        Assert.Equal(
            "Strings, Title",
            document.Text.Substring(
                value.Range.Start.Offset, value.Range.End.Offset - value.Range.Start.Offset));

        // The property still binds. Before, the peek for an attribute value saw a statement token,
        // recorded an empty value, and left the builder as a node in the parent container.
        var property = Assert.Single(label.Properties, p => p.Member.Name == "Text");
        Assert.Equal(AttributeValueKind.ExpressionBuilder, property.Value.Kind);
    }

    [Fact]
    public void AnUnterminatedBuilderStillLeavesTheTagAndTheRestOfTheFile()
    {
        // The first value has no `%>` of its own, so it runs on to the next one. What matters is
        // where that leaves the offset: past the delimiter, so the attribute scan resumes, the tag
        // still terminates, and the markup after it is parsed rather than eaten.
        var parse = Parse("""
            <%@ Page Language="C#" Inherits="AspxProject.DefaultPage" %>
            <asp:Label ID="lblFirst" runat="server" Text="<%$ Resources: X" ToolTip="<%$ Resources: Y %>" />
            <p>after</p>
            """);

        Assert.NotNull(parse.ParseTree);

        var label = Assert.Single(parse.ParseTree!.AllChildren.OfType<ControlNode>());
        Assert.Equal("lblFirst", label.Id);

        Assert.Contains(
            parse.ParseTree.AllChildren.OfType<TextNode>(),
            t => t.Text.Value.Contains("after", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NeitherSpellingOfAnImplicitLocalizationKeyIsReportedAsAMissingProperty()
    {
        // The markup pass runs over the registered packs, and calling the handler directly rather
        // than through a server means no host has built a registry, so this stands in for one.
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

        foreach (string page in new[] { FixturePaths.LocalizedAspxFile, FixturePaths.DnnLocalizedAscxFile })
        {
            var diagnostics = await AspxLanguageHandler.DiagnosticsAsync(page, default);

            // No CLR member name contains a colon, so ASP.NET's `meta:resourcekey` can never be the
            // property the author meant; DNN spells the same idea without the prefix.
            Assert.DoesNotContain(diagnostics, d => d.Code == "WFC0002");
        }
    }

    [Fact]
    public async Task AControlThatReallyDeclaresResourceKeyStillBindsIt()
    {
        var document = await AspxDocumentService.GetAsync(FixturePaths.LocalizedAspxFile, default);
        Assert.NotNull(document);

        var control = Assert.Single(
            AspxSymbolResolver.EnumerateControls(document!.Tree!),
            c => c.ControlType.Name == "LocalizedLabel");

        // The member lookup runs before the passthrough, so letting the unprefixed spelling
        // through does not stop a control that has the property from getting it.
        var property = Assert.Single(control.Properties, p => p.Member.Name == "ResourceKey");
        Assert.Equal("Greeting", property.Value.Value);
        Assert.Empty(control.Attributes);
    }

    [Fact]
    public void AnAttributeNoControlDeclaresIsStillReported()
    {
        // The colon rule and the `resourcekey` passthrough are both narrow: a genuine typo has to
        // keep costing the warning it always did.
        var parse = Parse("""
            <%@ Page Language="C#" Inherits="AspxProject.DefaultPage" %>
            <asp:Label ID="lbl" runat="server" Bogus="x" />
            """);

        var diagnostic = Assert.Single(
            parse.RawDiagnostics.Select(d => (Diagnostic)d), d => d.Id == "WFC0002");
        Assert.Contains("Bogus", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    // ---- In-memory markup ------------------------------------------------------------------

    private static readonly string s_stubSource =
        File.ReadAllText(Path.Combine(FixturePaths.AspxProjectDir, "SystemWebStubs.cs"));

    /// <summary>The stubs shadow the System.Web types, so only the core runtime is needed.</summary>
    private static readonly Compilation s_stubs = CSharpCompilation.Create(
        "Fixture",
        [CSharpSyntaxTree.ParseText(s_stubSource)],
        new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "netstandard.dll" }
            .Select(name => Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, name))
            .Where(File.Exists)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    /// <summary>
    /// Markup parsed against the stubs, for the shapes no fixture page should carry: a page whose
    /// builder never closes is a broken page, and leaving one on disk would put its recovery
    /// diagnostics in front of every other test that reads the fixture.
    /// </summary>
    private static AspxParseResult Parse(string markup) =>
        AspxSourceMappingService.Parse(
            Path.Combine(FixturePaths.AspxProjectDir, "InMemoryResources.aspx"), markup, s_stubs);
}

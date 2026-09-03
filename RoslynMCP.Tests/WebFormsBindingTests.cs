using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.DependencyInjection;
using RoslynMCP.Config;
using RoslynMCP.Languages;
using RoslynMCP.Languages.WebForms;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Languages.WebForms.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Code that lives in an attribute value — <c>Text='&lt;%# … %&gt;'</c> — and the completion
/// positions where the markup, not Roslyn, decides what the answer is.
/// </summary>
/// <remarks>
/// The attribute-value half runs against the shared <c>AspxProject</c> fixture because it is the
/// round trip that matters: the expression has to reach a real compilation, bind to a symbol in a
/// real <c>.cs</c> file, and come back mapped to the markup. The completion half is in-memory, so
/// that a master page or an <c>ItemType</c> written for one case does not change what the fixture
/// tests see.
/// </remarks>
[Collection(SharedState.Name)]
public class WebFormsBindingTests
{
    private static TextDocumentIdentifier Doc(string path) =>
        new(LspConverters.PathToUri(path));

    /// <summary>The position of <paramref name="needle"/> in the file, as an LSP position.</summary>
    private static Position PositionOf(string path, string needle, int offsetIntoNeedle = 0)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");

        var source = SourceText.From(text);
        var line = source.Lines.GetLinePosition(index + offsetIntoNeedle);
        return new Position(line.Line, line.Character);
    }

    // ---- Attribute-value code --------------------------------------------------------------

    [Fact]
    public async Task ACaretInsideADataBindingAttributeIsCodeRatherThanAValue()
    {
        var document = await AspxDocumentService.GetAsync(FixturePaths.RepeaterAspxFile, default);
        Assert.NotNull(document);

        int offset = document!.Text.IndexOf("FormatDate", StringComparison.Ordinal);
        var hit = AspxSymbolResolver.ResolveAt(document, offset);

        // Not the Text property the attribute names: inside `<%# … %>` the value is an expression,
        // and the symbol behind it comes from the projection rather than from the parse tree.
        Assert.Equal(AspxHitKind.Code, hit!.Kind);
        Assert.Null(hit.Symbol);
    }

    [Fact]
    public async Task CodeInAnAttributeValueReachesTheProjection()
    {
        var document = await AspxDocumentService.GetAsync(FixturePaths.RepeaterAspxFile, default);
        Assert.NotNull(document);

        // The page has no `<% %>` block and no `<script runat="server">`, so the data-binding
        // attribute is the only thing there is to project.
        var projection = AspxProjectionService.Get(document);
        Assert.NotNull(projection);

        int offset = document!.Text.IndexOf("FormatDate", StringComparison.Ordinal) + 3;
        int? projected = projection!.ToProjected(offset);
        Assert.NotNull(projected);

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            projection.Document, projected!.Value, default);

        Assert.Equal("FormatDate", symbol?.Name);
    }

    [Fact]
    public async Task GoToDefinitionInsideADataBindingAttributeLandsOnTheMethod()
    {
        var locations = await AspxLanguageHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.RepeaterAspxFile),
                PositionOf(FixturePaths.RepeaterAspxFile, "FormatDate", 3)),
            typeDefinition: false,
            default);

        var location = Assert.Single(locations);
        Assert.EndsWith("PageHelper.cs", Uri.UnescapeDataString(location.Uri));
    }

    [Fact]
    public async Task RenamingFromCSharpRewritesTheCodeInsideAnAttributeValue()
    {
        // The markup pass runs over the registered packs, and calling the handler directly rather
        // than through a server means no host has built a registry, so this stands in for one.
        new LanguageRegistry([new WebFormsLanguage(new MarkdownFormatter())]).Publish();

        var edit = await RenameHandler.RenameAsync(
            new RenameParams(
                Doc(FixturePaths.AspxPageHelperFile),
                PositionOf(FixturePaths.AspxPageHelperFile, "FormatDate", 3),
                "FormatShortDate"),
            default);

        Assert.NotNull(edit);

        string markupUri = LspConverters.PathToUri(FixturePaths.RepeaterAspxFile);
        Assert.True(
            edit!.Changes.ContainsKey(markupUri),
            "The rename left the call inside the data-binding attribute naming the old method.");

        var applied = Assert.Single(edit.Changes[markupUri]);
        Assert.Equal("FormatShortDate", applied.NewText);
    }

    [Fact]
    public async Task AttributeCodeBindsAgainstTheCodeBehindClass()
    {
        // The shape the fixture cannot show, because its code-behind is shared with the designer
        // tests: an attribute calling a method of the page itself, through the synthetic partial.
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Label ID="lblTotal" runat="server" Text='<%# Tot|al() %>' />
            """,
            """
            namespace Fixture
            {
                public partial class SamplePage : System.Web.UI.Page
                {
                    protected int Total() => 42;
                }
            }
            """);

        var projection = AspxProjectionService.Get(scenario.Document);
        Assert.NotNull(projection);

        int? projected = projection!.ToProjected(scenario.Caret);
        Assert.NotNull(projected);

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            projection.Document, projected!.Value, default);

        Assert.Equal("Total", symbol?.Name);
        Assert.Equal("SamplePage", symbol?.ContainingType.Name);
    }

    [Fact]
    public async Task WebConfigPagesNamespacesReachInlineCode()
    {
        // `Formatting` is qualified nowhere: no @Import, no using in the code-behind. The only
        // thing making the bare name visible is web.config's <pages><namespaces> entry, the way
        // the runtime would make it visible to every page of the site.
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Label ID="lblPrice" runat="server" Text='<%# Forma|tting.Money(42) %>' />
            """,
            """
            namespace Fixture
            {
                public partial class SamplePage : System.Web.UI.Page
                {
                }
            }

            namespace Fixture.Helpers
            {
                public static class Formatting
                {
                    public static string Money(int value) => value.ToString();
                }
            }
            """,
            files:
            [
                ("web.config", """
                    <configuration>
                      <system.web>
                        <pages>
                          <namespaces>
                            <add namespace="Fixture.Helpers" />
                          </namespaces>
                        </pages>
                      </system.web>
                    </configuration>
                    """),
            ]);

        var projection = AspxProjectionService.Get(scenario.Document);
        Assert.NotNull(projection);

        int? projected = projection!.ToProjected(scenario.Caret);
        Assert.NotNull(projected);

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            projection.Document, projected!.Value, default);

        Assert.Equal("Formatting", symbol?.Name);
        Assert.Equal("Helpers", symbol?.ContainingNamespace.Name);
    }

    // ---- Register directive ----------------------------------------------------------------

    [Fact]
    public async Task RegisterOffersTheAttributesItAccepts()
    {
        using var scenario = Scenario.Create("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <%@ Register TagPrefix="uc" |
            """);

        var labels = (await scenario.CompleteAsync()).Items.Select(i => i.Label).ToList();

        Assert.Contains("TagName", labels);
        Assert.Contains("Src", labels);
        Assert.Contains("Namespace", labels);
        Assert.Contains("Assembly", labels);
        // Already written on this directive, so offering it again would be noise.
        Assert.DoesNotContain("TagPrefix", labels);
    }

    [Fact]
    public async Task ADirectiveValueIsNotAnAttributeNamePosition()
    {
        using var scenario = Scenario.Create("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <%@ Register TagPrefix="u|" %>
            """);

        Assert.Empty((await scenario.CompleteAsync()).Items);
    }

    [Fact]
    public async Task OtherDirectivesAreLeftAlone()
    {
        using var scenario = Scenario.Create("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" |
            """);

        // Page takes two dozen attributes whose spellings this does not know; offering the
        // Register set there would be worse than offering nothing.
        Assert.Empty((await scenario.CompleteAsync()).Items);
    }

    // ---- ContentPlaceHolderID --------------------------------------------------------------

    [Fact]
    public async Task ContentPlaceHolderIdOffersWhatTheMasterDeclares()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" MasterPageFile="Site.master" Inherits="Fixture.SamplePage" %>
            <asp:Content ContentPlaceHolderID="|" runat="server" />
            """,
            files:
            [
                ("Site.master", """
                    <%@ Master Language="C#" %>
                    <asp:ContentPlaceHolder ID="TitleContent" runat="server" />
                    <asp:ContentPlaceHolder ID="MainContent" runat="server" />
                    """),
            ]);

        var items = (await scenario.CompleteAsync()).Items;

        Assert.Equal(
            ["MainContent", "TitleContent"],
            items.Select(i => i.Label).OrderBy(l => l, StringComparer.Ordinal));
        Assert.All(items, i => Assert.Equal("Site.master", i.Detail));
    }

    [Fact]
    public async Task ContentPlaceHolderIdOffersNothingWithoutAMaster()
    {
        using var scenario = Scenario.Create("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Content ContentPlaceHolderID="|" runat="server" />
            """);

        Assert.Empty((await scenario.CompleteAsync()).Items);
    }

    // ---- Eval and Bind ---------------------------------------------------------------------

    private const string ItemCodeBehind = """
        namespace Fixture
        {
            public class Client
            {
                public string Name { get; set; } = "";
            }

            public class Order
            {
                public string Customer { get; set; } = "";
                public decimal Amount { get; set; }
                public int Id;
                public System.DateTime CompletedDate { get; set; }

                public Client Buyer { get; set; } = new Client();

                private string Secret { get; set; } = "";
            }

            public partial class SamplePage : System.Web.UI.Page
            {
            }
        }
        """;

    /// <summary>
    /// Sets the configured attributes for one test and puts them back afterwards.
    /// </summary>
    /// <remarks>
    /// The settings are process-wide because the markup handlers are static, so a test that left
    /// them set would decide what every later test in the collection sees.
    /// </remarks>
    private sealed class Configured : IDisposable
    {
        private readonly MarkupBindingSettings _previous = MarkupBindingSettings.Current;

        public Configured(params MarkupBinding[] attributes) =>
            MarkupBindingSettings.Current =
                new MarkupBindingSettings { Attributes = [.. attributes] };

        public void Dispose() => MarkupBindingSettings.Current = _previous;
    }

    private static MarkupBinding Member(string tag, string attribute) =>
        new(tag, attribute, MarkupBindingKind.Member, Source: null);

    private static MarkupBinding Format(string tag, string attribute, string? source = null) =>
        new(tag, attribute, MarkupBindingKind.Format, source);

    /// <summary>
    /// A configured attribute reads exactly as an <c>Eval</c> argument does.
    /// </summary>
    /// <remarks>
    /// Which is the point of routing it through the same three calls rather than teaching the
    /// attribute its own parser: the dotted path, the case-insensitive lookup and the item type
    /// traced from an ancestor all come along, because they are not the attribute's behaviour —
    /// they are the binding's.
    /// </remarks>
    [Fact]
    public async Task AConfiguredAttributeResolvesLikeAnEvalArgument()
    {
        using var configured = new Configured(Member("grid:Column", "SortExpression"));
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <grid:Column runat="server" ID="col" SortExpression="Buyer.Na|me" />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        string? hover = await scenario.HoverBindingAsync();

        Assert.NotNull(hover);
        Assert.Contains("Name", hover);
        Assert.Empty(await scenario.BindingDiagnosticsAsync());
    }

    /// <summary>
    /// And it offers the same completions, at the caret inside the attribute's quotes.
    /// </summary>
    /// <remarks>
    /// The list has to be reached from the attribute-value branch rather than the code branch:
    /// `SortExpression="…"` is not inline C#, so nothing takes it through the projection, and the
    /// branch that does own it resolves the control's property, finds a string, and offers
    /// nothing — at the one caret where the bound item's fields are the whole answer.
    /// </remarks>
    [Fact]
    public async Task AConfiguredAttributeOffersTheBoundItemsFields()
    {
        using var configured = new Configured(Member("grid:Column", "SortExpression"));
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <grid:Column runat="server" ID="col" SortExpression="|" />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        var labels = (await scenario.CompleteAsync()).Items.Select(i => i.Label).ToArray();

        Assert.Contains("Amount", labels);
        Assert.Contains("Buyer", labels);
        Assert.DoesNotContain("Secret", labels);
    }

    /// <summary>A dotted path walks the same way it does inside an <c>Eval</c>.</summary>
    [Fact]
    public async Task AConfiguredAttributeOffersTheMembersOfASegmentAlreadyWritten()
    {
        using var configured = new Configured(Member("grid:Column", "SortExpression"));
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <grid:Column runat="server" ID="col" SortExpression="Buyer.|" />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        var labels = (await scenario.CompleteAsync()).Items.Select(i => i.Label).ToArray();

        Assert.Equal(["Name"], labels);
    }

    /// <summary>
    /// And it is coloured like one: the member it names, in the colour a member gets.
    /// </summary>
    /// <remarks>
    /// The grammar paints an attribute value as one string whatever is in it, so a page's grid
    /// columns looked untouched next to the templates below them even where every name in them had
    /// been resolved. The colour is the only thing that says the check happened.
    /// </remarks>
    [Fact]
    public async Task AConfiguredAttributeIsColouredLikeAnEvalArgument()
    {
        using var configured = new Configured(Member("grid:Column", "SortExpression"));
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <grid:Column runat="server" ID="c|ol" SortExpression="Buyer.Name" />
                    <grid:Column runat="server" ID="bad" SortExpression="Amont" />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        var coloured = await scenario.BindingColoursAsync();

        Assert.True(coloured["Buyer"]);
        Assert.True(coloured["Name"]);
        Assert.False(coloured["Amont"]);
    }

    /// <summary>And a misspelling in one is reported the same way.</summary>
    [Fact]
    public async Task AMisspelledMemberInAConfiguredAttributeIsReported()
    {
        using var configured = new Configured(Member("grid:Column", "SortExpression"));
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <grid:Column runat="server" ID="c|ol" SortExpression="Amont" />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        var diagnostic = Assert.Single(await scenario.BindingDiagnosticsAsync());

        Assert.Equal("WFB0001", diagnostic.Code);
        Assert.Contains("Amont", diagnostic.Message);
    }

    /// <summary>
    /// An attribute nobody configured is left alone.
    /// </summary>
    /// <remarks>
    /// The whole reason the registry ships empty. <c>SortExpression</c> holds a member path on one
    /// vendor's grid and a SQL fragment on another's, and an attribute wrongly claimed turns every
    /// use of it into a warning.
    /// </remarks>
    [Fact]
    public async Task AnUnconfiguredAttributeIsNotReadAsABinding()
    {
        using var configured = new Configured(Member("grid:Column", "SortExpression"));
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <grid:Column runat="server" ID="c|ol" DataField="Amont" />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        Assert.Empty(await scenario.BindingDiagnosticsAsync());
    }

    /// <summary>
    /// The container the editor talks to publishes the configured attributes, not just the
    /// container-less one.
    /// </summary>
    /// <remarks>
    /// Two registration paths exist — <c>Create</c> for a host that builds no container, and
    /// <c>AddLanguagePacks</c> for the daemon and the MCP server — and the settings are a static
    /// because the markup handlers are static. Publishing them from one path only is invisible in
    /// every test that sets the static itself, and leaves every configured <c>SortExpression</c>
    /// dead in the only host an editor ever connects to.
    /// </remarks>
    [Fact]
    public void RegisteringThePacksIntoAContainerPublishesTheConfiguredAttributes()
    {
        var previous = MarkupBindingSettings.Current;
        MarkupBindingSettings.Current = MarkupBindingSettings.None;

        try
        {
            var config = new RoslynSenseConfig
            {
                WebForms = new WebFormsConfig
                {
                    DataExpressions = [new MarkupBindingEntry { Tag = "*", Attribute = "SortExpression" }],
                },
            };

            var settings = EffectiveSettings.Resolve([], config, out _);
            new ServiceCollection().AddLanguagePacks(settings);

            var published = Assert.Single(MarkupBindingSettings.Current.Attributes);
            Assert.Equal("SortExpression", published.Attribute);
            Assert.NotNull(MarkupBindingSettings.Current.For(prefix: "telerik", "GridTemplateColumn", "SortExpression"));
        }
        finally
        {
            MarkupBindingSettings.Current = previous;
        }
    }

    // ---- The container form ----------------------------------------------------------------

    /// <summary>
    /// A path handed the container rather than the item walks from the item all the same.
    /// </summary>
    /// <remarks>
    /// <c>DataBinder.Eval(Container, "DataItem.Amount")</c> is the long-hand a generated page and a
    /// hand-written one are both full of, and it was read as though the container were the item —
    /// so its first segment named a member no order has and the whole path was reported wrong.
    /// Which of the two forms is written is a question about the call, not about the path: the
    /// first argument is the only thing that says it.
    /// </remarks>
    [Fact]
    public async Task APathWrittenAgainstTheContainerResolvesFromTheItem()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <%# DataBinder.Eval(Container, "DataItem.Am|ount") %>
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        string? hover = await scenario.HoverBindingAsync();

        Assert.NotNull(hover);
        Assert.Contains("Amount", hover);
        Assert.Empty(await scenario.BindingDiagnosticsAsync());
    }

    /// <summary>A misspelling after the hop is still reported.</summary>
    [Fact]
    public async Task AMisspellingAfterTheContainerHopIsReported()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <%# DataBinder.Eval(Container, "DataItem.Am|ont") %>
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        var diagnostic = Assert.Single(await scenario.BindingDiagnosticsAsync());

        Assert.Equal("WFB0001", diagnostic.Code);
        Assert.Contains("Amont", diagnostic.Message);
    }

    /// <summary>
    /// And the item form is left as it was: there the container's own hop is a mistake.
    /// </summary>
    [Fact]
    public async Task APathWrittenAgainstTheItemStillReportsTheContainersHop()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <%# DataBinder.Eval(Container.DataItem, "DataIt|em.Amount") %>
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        var diagnostic = Assert.Single(await scenario.BindingDiagnosticsAsync());

        Assert.Equal("WFB0001", diagnostic.Code);
        Assert.Contains("DataItem", diagnostic.Message);
    }

    /// <summary>
    /// A cast container reads as a container: what is not a <c>DataItem</c> holds one.
    /// </summary>
    [Fact]
    public async Task ACastContainerIsStillAContainer()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <%# DataBinder.Eval((RepeaterItem)Container, "DataItem.Buyer.Na|me") %>
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        string? hover = await scenario.HoverBindingAsync();

        Assert.NotNull(hover);
        Assert.Contains("Name", hover);
        Assert.Empty(await scenario.BindingDiagnosticsAsync());
    }

    // ---- Format strings --------------------------------------------------------------------

    /// <summary>
    /// The page holding a grid column that writes both halves: which member it shows, and how.
    /// </summary>
    private const string Column = """
        <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
        <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
            <ItemTemplate>
                <grid:Column runat="server" ID="col" DataField="{0}" DataFormatString="{1}" />
            </ItemTemplate>
        </asp:Repeater>
        """;

    /// <summary>
    /// Hovering a component says what it prints, worked out rather than described.
    /// </summary>
    /// <remarks>
    /// The question a specifier raises has always been the same one — what does this actually
    /// produce? — and until now the only way to answer it was to request the page.
    /// </remarks>
    [Fact]
    public async Task HoveringAFormatComponentWorksAnExample()
    {
        using var configured = new Configured(Format("grid:Column", "DataFormatString", "DataField"));
        using var scenario = Scenario.Create(
            string.Format(Column, "CompletedDate", "{0:dd-M|M-yyyy}"), ItemCodeBehind);

        string? hover = await scenario.FormatHoverAsync();

        Assert.NotNull(hover);
        Assert.Contains("Month, two digits", hover, StringComparison.Ordinal);
        Assert.Contains("`dd-MM-yyyy` → `27-03-2026`", hover, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sibling attribute the entry names is what says which grammar the specifier is in.
    /// </summary>
    /// <remarks>
    /// The whole reason <c>source</c> exists. <c>MM</c> is a two-digit month on a date and two
    /// literal Ms on a decimal, and <c>N2</c> is a number pattern on a decimal and the letter N
    /// followed by a 2 on a date — so without the sibling, half of every description would be
    /// about a value the column does not hold.
    /// </remarks>
    [Fact]
    public async Task TheSourceAttributeDecidesHowTheSpecifierReads()
    {
        using var configured = new Configured(Format("grid:Column", "DataFormatString", "DataField"));
        using var scenario = Scenario.Create(
            string.Format(Column, "Amount", "{0:N|2}"), ItemCodeBehind);

        string? hover = await scenario.FormatHoverAsync();

        Assert.NotNull(hover);
        Assert.Contains("Number, with thousands separators, 2 decimal places", hover, StringComparison.Ordinal);
        Assert.Contains("`1,234.57`", hover, StringComparison.Ordinal);
    }

    /// <summary>
    /// An entry that named no source still reads the specifier, from what it contains.
    /// </summary>
    /// <remarks>
    /// Standing down entirely would be the wrong trade: the components are the same characters
    /// whatever the value is, and a date reading of a specifier holding date letters is right far
    /// more often than it is not.
    /// </remarks>
    [Fact]
    public async Task AFormatWithNoSourceIsStillRead()
    {
        using var configured = new Configured(Format("grid:Column", "DataFormatString"));
        using var scenario = Scenario.Create(
            string.Format(Column, "CompletedDate", "{0:M|M-yyyy}"), ItemCodeBehind);

        string? hover = await scenario.FormatHoverAsync();

        Assert.NotNull(hover);
        Assert.Contains("Month, two digits", hover, StringComparison.Ordinal);
    }

    /// <summary>Hovering the hole itself names the value the column formats.</summary>
    [Fact]
    public async Task HoveringTheHoleNamesWhatIsBeingFormatted()
    {
        using var configured = new Configured(Format("grid:Column", "DataFormatString", "DataField"));
        using var scenario = Scenario.Create(
            string.Format(Column, "CompletedDate", "{|0:dd-MM-yyyy}"), ItemCodeBehind);

        string? hover = await scenario.FormatHoverAsync();

        Assert.NotNull(hover);
        Assert.Contains("System.DateTime", hover, StringComparison.Ordinal);
    }

    /// <summary>An attribute nobody configured as a format string is not read as one.</summary>
    [Fact]
    public async Task AnUnconfiguredAttributeIsNotReadAsAFormat()
    {
        using var configured = new Configured(Format("grid:Column", "SomethingElse"));
        using var scenario = Scenario.Create(
            string.Format(Column, "CompletedDate", "{0:dd-M|M-yyyy}"), ItemCodeBehind);

        Assert.Null(await scenario.FormatHoverAsync());
    }

    /// <summary>
    /// The components are coloured apart from one another.
    /// </summary>
    /// <remarks>
    /// <c>dd-MM-yyyy</c> and <c>dd-mm-yyyy</c> are one keystroke apart, both look like a date, and
    /// only one of them prints a month. Three different colours are what makes the pair visibly
    /// different before anyone reads the letters.
    /// </remarks>
    [Fact]
    public async Task AFormatStringIsColouredComponentByComponent()
    {
        using var configured = new Configured(Format("grid:Column", "DataFormatString", "DataField"));
        using var scenario = Scenario.Create(
            string.Format(Column, "CompletedDate", "{0:dd-MM-yyyy}") + "|", ItemCodeBehind);

        var coloured = await scenario.FormatColoursAsync();

        var used = new[] { "dd", "MM", "yyyy" }.Select(part => coloured[part]);
        Assert.Equal(3, new HashSet<int>(used).Count);

        // The literal text between them prints as itself, and the string colour already says so.
        Assert.False(coloured.ContainsKey("-"));
    }

    /// <summary>
    /// Completion inside the specifier offers the components, each with what it prints.
    /// </summary>
    /// <remarks>
    /// The list is the documentation. Nobody remembers whether the month is <c>MM</c> or
    /// <c>mm</c>, and the usual way that gets settled is by writing one and requesting the page.
    /// </remarks>
    [Fact]
    public async Task CompletionInsideASpecifierOffersTheComponents()
    {
        using var configured = new Configured(Format("grid:Column", "DataFormatString", "DataField"));
        using var scenario = Scenario.Create(
            string.Format(Column, "CompletedDate", "{0:dd-|}"), ItemCodeBehind);

        var items = (await scenario.CompleteAsync()).Items;

        Assert.Contains(items, item => item.Label == "MM" && item.Detail!.Contains("03"));
        Assert.Contains(items, item => item.Label == "yyyy" && item.Detail!.Contains("2026"));
    }

    /// <summary>
    /// A decimal column is offered digit placeholders rather than date components.
    /// </summary>
    /// <remarks>
    /// Offering <c>dd</c> for a <c>decimal</c> would insert a specifier that prints its own
    /// letters, which is the mistake the colouring exists to make visible.
    /// </remarks>
    [Fact]
    public async Task CompletionFollowsTheSourcesType()
    {
        using var configured = new Configured(Format("grid:Column", "DataFormatString", "DataField"));
        using var scenario = Scenario.Create(
            string.Format(Column, "Amount", "{0:|}"), ItemCodeBehind);

        var labels = (await scenario.CompleteAsync()).Items.Select(item => item.Label).ToList();

        Assert.Contains("N2", labels);
        Assert.DoesNotContain("yyyy", labels);
    }

    /// <summary>A caret on the index is choosing a value, not a component.</summary>
    [Fact]
    public async Task CompletionOnTheIndexOffersNoComponents()
    {
        using var configured = new Configured(Format("grid:Column", "DataFormatString", "DataField"));
        using var scenario = Scenario.Create(
            string.Format(Column, "CompletedDate", "{|0:dd}"), ItemCodeBehind);

        var labels = (await scenario.CompleteAsync()).Items.Select(item => item.Label).ToList();

        Assert.DoesNotContain("MM", labels);
    }

    /// <summary>A tag of <c>*</c> claims the attribute wherever it is written.</summary>
    [Fact]
    public async Task AnEntryForAnyTagClaimsTheAttributeEverywhere()
    {
        using var configured = new Configured(Member("*", "DataField"));
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <telerik:GridBoundColumn runat="server" ID="c|ol" DataField="Amont" />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        Assert.Single(await scenario.BindingDiagnosticsAsync());
    }

    /// <summary>
    /// A misspelled member is reported, because nothing else will report it.
    /// </summary>
    /// <remarks>
    /// <c>Eval</c> takes a string and reflects over it, so this is not a build error, not a test
    /// failure and not anything at all until the page renders — at which point it throws at the
    /// user rather than at whoever wrote it.
    /// </remarks>
    [Fact]
    public async Task AMisspelledBoundMemberIsReported()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <asp:Label ID="lbl|Amount" runat="server" Text='<%# Eval("Amont") %>' />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        var diagnostic = Assert.Single(await scenario.BindingDiagnosticsAsync());

        Assert.Equal("WFB0001", diagnostic.Code);
        Assert.Contains("Amont", diagnostic.Message);
        Assert.Contains("Fixture.Order", diagnostic.Message);
    }

    /// <summary>One mistake is reported once, not once per dot after it.</summary>
    [Fact]
    public async Task OnlyTheSegmentThatBrokeThePathIsReported()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <asp:Label ID="lbl|Name" runat="server" Text='<%# Eval("Byer.Name") %>' />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        var diagnostic = Assert.Single(await scenario.BindingDiagnosticsAsync());

        Assert.Contains("Byer", diagnostic.Message);
        Assert.DoesNotContain("Name", diagnostic.Message);
    }

    [Fact]
    public async Task AMemberThatBindsIsNotReported()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <asp:Label ID="lbl|Name" runat="server" Text='<%# Eval("Buyer.Name") %>' />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        Assert.Empty(await scenario.BindingDiagnosticsAsync());
    }

    /// <summary>
    /// With no item type, nothing is claimed.
    /// </summary>
    /// <remarks>
    /// A container declaring no <c>ItemType</c> whose <c>DataSource</c> cannot be traced is
    /// ordinary, and every path under it would light up — which teaches the reader to ignore the
    /// rule everywhere, including where it is right.
    /// </remarks>
    [Fact]
    public async Task NothingIsReportedWhenTheItemTypeIsUnknown()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server">
                <ItemTemplate>
                    <asp:Label ID="lbl|Amount" runat="server" Text='<%# Eval("Amont") %>' />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        Assert.Empty(await scenario.BindingDiagnosticsAsync());
    }

    /// <summary>
    /// Hovering a bound member says what the member is.
    /// </summary>
    /// <remarks>
    /// The projection binds the argument to <c>System.String</c>, because that is what it is — so
    /// before this the hover over a bound property described the string literal holding its name,
    /// which is true and useless. The property is reachable only from the item type.
    /// </remarks>
    [Fact]
    public async Task HoveringABoundMemberDescribesTheMemberRatherThanTheString()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <asp:Label ID="lblAmount" runat="server" Text='<%# Eval("Am|ount") %>' />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        string? hover = await scenario.HoverBindingAsync();

        Assert.NotNull(hover);
        Assert.Contains("Amount", hover);
        Assert.Contains("decimal", hover);
        Assert.DoesNotContain("System.String", hover);
    }

    /// <summary>Each half of a path is described on its own terms.</summary>
    [Fact]
    public async Task HoveringEitherHalfOfAPathDescribesThatHalf()
    {
        const string Markup = """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <asp:Label ID="lblName" runat="server" Text='<%# Eval("{0}") %>' />
                </ItemTemplate>
            </asp:Repeater>
            """;

        using (var outer = Scenario.Create(string.Format(Markup, "Bu|yer.Name"), ItemCodeBehind))
        {
            string? hover = await outer.HoverBindingAsync();
            Assert.NotNull(hover);
            Assert.Contains("Buyer", hover);
            Assert.Contains("Client", hover);
        }

        using var inner = Scenario.Create(string.Format(Markup, "Buyer.Na|me"), ItemCodeBehind);

        string? nested = await inner.HoverBindingAsync();
        Assert.NotNull(nested);
        Assert.Contains("Name", nested);
        Assert.Contains("string", nested);
    }

    /// <summary>
    /// A name that binds to nothing says which type it was looked for on.
    /// </summary>
    /// <remarks>
    /// The one thing the user needs and cannot get from the markup: the item type is declared on an
    /// ancestor, or on no ancestor and inferred from a <c>DataSource</c> assignment in the
    /// code-behind, so "what am I even reading this off" is a question the file does not answer.
    /// </remarks>
    [Fact]
    public async Task HoveringAMisspelledMemberNamesTheTypeItWasLookedForOn()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <asp:Label ID="lblAmount" runat="server" Text='<%# Eval("Amo|nt") %>' />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        string? hover = await scenario.HoverBindingAsync();

        Assert.NotNull(hover);
        Assert.Contains("Fixture.Order", hover);
        Assert.Contains("Amont", hover);
    }

    /// <summary>
    /// With no item type there is nothing to say, so nothing is said.
    /// </summary>
    /// <remarks>
    /// "Not found on nothing" is worse than silence: a container declaring no <c>ItemType</c> whose
    /// <c>DataSource</c> could not be traced is ordinary, and a hover claiming the name is wrong
    /// would be asserting something the tool does not know.
    /// </remarks>
    [Fact]
    public async Task HoveringWithNoItemTypeSaysNothing()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server">
                <ItemTemplate>
                    <asp:Label ID="lblAmount" runat="server" Text='<%# Eval("Amo|unt") %>' />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        Assert.Null(await scenario.HoverBindingAsync());
    }

    [Fact]
    public async Task EvalOffersTheFieldsOfTheContainersItemType()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <asp:Label ID="lblCustomer" runat="server" Text='<%# Eval("|") %>' />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        var labels = (await scenario.CompleteAsync()).Items.Select(i => i.Label).ToList();

        Assert.Contains("Customer", labels);
        Assert.Contains("Amount", labels);
        Assert.Contains("Id", labels);
        // Eval reads through the public surface only.
        Assert.DoesNotContain("Secret", labels);
    }

    [Fact]
    public async Task BindOffersTheSameFieldsAsEval()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
                <ItemTemplate>
                    <asp:Label ID="lblCustomer" runat="server" Text='<%# Bind("Cust|") %>' />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        var labels = (await scenario.CompleteAsync()).Items.Select(i => i.Label).ToList();

        Assert.Contains("Customer", labels);
    }

    [Fact]
    public async Task EvalOffersNothingWhenTheItemTypeIsUnknown()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server">
                <ItemTemplate>
                    <asp:Label ID="lblCustomer" runat="server" Text='<%# Eval("|") %>' />
                </ItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        // An untyped DataSource makes the field names a runtime detail. Guessing them from the
        // rest of the page would put a name that compiles and then throws in front of the user.
        Assert.Empty((await scenario.CompleteAsync()).Items);
    }

    // ---- F12 on a binding path ---------------------------------------------------------------

    private const string TypedRepeater = """
        <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
        <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
            <ItemTemplate>
                <asp:Label ID="lblCustomer" runat="server" Text='<%# Eval("{0}") %>' />
            </ItemTemplate>
        </asp:Repeater>
        """;

    [Fact]
    public async Task DefinitionOnABindingPathReachesTheProperty()
    {
        using var scenario = Scenario.Create(
            string.Format(TypedRepeater, "Cust|omer"), ItemCodeBehind);

        var member = await AspxLanguageHandler.DataBoundMemberAsync(
            scenario.Document, scenario.Caret, default);

        // The projection binds this literal to System.String, which is never what the caret meant.
        Assert.NotNull(member);
        Assert.Equal("Customer", member!.Name);
        Assert.Equal("Order", member.ContainingType.Name);
    }

    [Fact]
    public async Task DefinitionOnANestedSegmentReachesThePropertyOfTheTypeBeforeIt()
    {
        using var scenario = Scenario.Create(
            string.Format(TypedRepeater, "Buyer.Na|me"), ItemCodeBehind);

        var member = await AspxLanguageHandler.DataBoundMemberAsync(
            scenario.Document, scenario.Caret, default);

        // Resolved through Buyer, so the answer is Client.Name and not the item's own.
        Assert.NotNull(member);
        Assert.Equal("Name", member!.Name);
        Assert.Equal("Client", member.ContainingType.Name);
    }

    [Fact]
    public async Task DefinitionOnTheFirstHalfOfAPathReachesThatHalf()
    {
        using var scenario = Scenario.Create(
            string.Format(TypedRepeater, "Buy|er.Name"), ItemCodeBehind);

        var member = await AspxLanguageHandler.DataBoundMemberAsync(
            scenario.Document, scenario.Caret, default);

        Assert.NotNull(member);
        Assert.Equal("Buyer", member!.Name);
    }

    [Fact]
    public async Task ASegmentNoMemberDeclaresResolvesToNothing()
    {
        using var scenario = Scenario.Create(
            string.Format(TypedRepeater, "Custo|mner"), ItemCodeBehind);

        Assert.Null(await AspxLanguageHandler.DataBoundMemberAsync(
            scenario.Document, scenario.Caret, default));
    }

    // ---- Indexed paths -------------------------------------------------------------------------

    private const string IndexedCodeBehind = """
        using System.Collections.Generic;

        namespace Fixture
        {
            public class Client
            {
                public string Name { get; set; } = "";
            }

            public class Order
            {
                public string this[string key] => "";

                public List<Client> Lines { get; set; } = new List<Client>();
            }

            public partial class SamplePage : System.Web.UI.Page
            {
            }
        }
        """;

    [Fact]
    public async Task AnIndexedSegmentResolvesToTheIndexer()
    {
        using var scenario = Scenario.Create(
            string.Format(TypedRepeater, "It|em['index']"), IndexedCodeBehind);

        var member = await AspxLanguageHandler.DataBoundMemberAsync(
            scenario.Document, scenario.Caret, default);

        // `Item` is the name C# gives an indexer, and DataBinder's `Item['index']` is a call to
        // exactly that member.
        Assert.NotNull(member);
        Assert.True(Assert.IsAssignableFrom<IPropertySymbol>(member).IsIndexer);
        Assert.Equal("Order", member!.ContainingType.Name);
    }

    [Fact]
    public async Task APathContinuingThroughAnIndexerResolvesAgainstTheIndexedType()
    {
        using var scenario = Scenario.Create(
            string.Format(TypedRepeater, "Lines[0].Na|me"), IndexedCodeBehind);

        var member = await AspxLanguageHandler.DataBoundMemberAsync(
            scenario.Document, scenario.Caret, default);

        // `Lines[0]` is a Client, so the segment after it is Client's.
        Assert.NotNull(member);
        Assert.Equal("Name", member!.Name);
        Assert.Equal("Client", member.ContainingType.Name);
    }

    [Fact]
    public void ADotInsideBracketsDoesNotSplitTheSegment()
    {
        const string text = """<%# Eval("Item['a.b'].Name") %>""";

        var argument = Assert.Single(DataBindingService.AllArguments(text));
        var segments = DataBindingService.Segments(text, argument, itemType: null);

        Assert.Equal(["Item", "Name"], segments.Select(s => s.Name));
    }

    // ---- The item type, when nothing declares one ---------------------------------------------

    private const string DataSourceCodeBehind = """
        using System.Collections.Generic;

        namespace Fixture
        {
            public class Client
            {
                public string Name { get; set; } = "";
            }

            public class Order
            {
                public string Customer { get; set; } = "";
                public Client Buyer { get; set; } = new Client();
            }

            public partial class SamplePage : System.Web.UI.Page
            {
                private List<Order> GetOrders() => new List<Order>();

                protected void Bind()
                {
                    rptOrders.DataSource = GetOrders();
                }
            }
        }
        """;

    private const string UntypedRepeater = """
        <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
        <asp:Repeater ID="rptOrders" runat="server">
            <ItemTemplate>
                <asp:Label ID="lblCustomer" runat="server" Text='<%# Eval("{0}") %>' />
            </ItemTemplate>
        </asp:Repeater>
        """;

    [Fact]
    public async Task TheItemTypeIsInferredFromWhatTheCodeBehindAssignsToDataSource()
    {
        using var scenario = Scenario.Create(
            string.Format(UntypedRepeater, "Cust|omer"), DataSourceCodeBehind);

        var member = await AspxLanguageHandler.DataBoundMemberAsync(
            scenario.Document, scenario.Caret, default);

        // Nothing on the page says what it binds. `List<Order>` from the code-behind does.
        Assert.NotNull(member);
        Assert.Equal("Customer", member!.Name);
        Assert.Equal("Order", member.ContainingType.Name);
    }

    [Fact]
    public async Task AnInferredItemTypeAlsoOffersCompletions()
    {
        using var scenario = Scenario.Create(
            string.Format(UntypedRepeater, "|"), DataSourceCodeBehind);

        var labels = (await scenario.CompleteAsync()).Items.Select(i => i.Label).ToList();

        Assert.Contains("Customer", labels);
        Assert.Contains("Buyer", labels);
    }

    [Fact]
    public async Task TwoDataSourcesOfDifferentTypesInferNothing()
    {
        using var scenario = Scenario.Create(
            string.Format(UntypedRepeater, "Cust|omer"),
            DataSourceCodeBehind.Replace(
                "rptOrders.DataSource = GetOrders();",
                """
                rptOrders.DataSource = GetOrders();
                        rptOrders.DataSource = new List<Client>();
                """));

        // Two answers is no answer: colouring the page's fields against whichever assignment was
        // found first would be wrong half the time, and silently.
        Assert.Null(await AspxLanguageHandler.DataBoundMemberAsync(
            scenario.Document, scenario.Caret, default));
    }

    // ---- DataBinder.Eval, and where a path is not a path ---------------------------------------

    /// <summary>
    /// The shape real markup is written in: an untyped <c>Container.DataItem</c> handed to
    /// <c>DataBinder.Eval</c>, with the item type coming from the repeater rather than the call.
    /// </summary>
    private const string BinderRepeater = """
        <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
        <asp:Repeater ID="rptOrders" runat="server" ItemType="Fixture.Order">
            <ItemTemplate>
                <span>{0}</span>
            </ItemTemplate>
        </asp:Repeater>
        """;

    [Fact]
    public async Task DataBinderEvalResolvesItsSecondArgumentAsAPath()
    {
        using var scenario = Scenario.Create(
            string.Format(
                BinderRepeater, """<%# DataBinder.Eval(Container.DataItem, "Cust|omer") %>"""),
            ItemCodeBehind);

        var member = await AspxLanguageHandler.DataBoundMemberAsync(
            scenario.Document, scenario.Caret, default);

        // The long-hand of `Eval("Customer")`, and it has to answer the same property.
        Assert.NotNull(member);
        Assert.Equal("Customer", member!.Name);
        Assert.Equal("Order", member.ContainingType.Name);
    }

    [Fact]
    public async Task DataBinderEvalOffersTheItemsFields()
    {
        using var scenario = Scenario.Create(
            string.Format(BinderRepeater, """<%# DataBinder.Eval(Container.DataItem, "|") %>"""),
            ItemCodeBehind);

        var labels = (await scenario.CompleteAsync()).Items.Select(i => i.Label).ToList();

        Assert.Contains("Customer", labels);
        Assert.Contains("Buyer", labels);
    }

    [Fact]
    public async Task GetPropertyValueIsAPathToo()
    {
        using var scenario = Scenario.Create(
            string.Format(
                BinderRepeater,
                """<%# DataBinder.GetPropertyValue(Container.DataItem, "Buyer.Na|me") %>"""),
            ItemCodeBehind);

        var member = await AspxLanguageHandler.DataBoundMemberAsync(
            scenario.Document, scenario.Caret, default);

        Assert.NotNull(member);
        Assert.Equal("Name", member!.Name);
        Assert.Equal("Client", member.ContainingType.Name);
    }

    [Fact]
    public async Task TheFormatArgumentOfDataBinderEvalIsNotAPath()
    {
        using var scenario = Scenario.Create(
            string.Format(
                BinderRepeater,
                """<%# DataBinder.Eval(Container.DataItem, "Amount", "{0:c|}") %>"""),
            ItemCodeBehind);

        // A format string is not a field name. Offering `Amount` inside it would insert something
        // that renders as itself.
        Assert.Empty((await scenario.CompleteAsync()).Items);
    }

    [Fact]
    public async Task TheFormatArgumentOfEvalIsNotAPath()
    {
        using var scenario = Scenario.Create(
            string.Format(BinderRepeater, """<%# Eval("Amount", "{0:c|}") %>"""),
            ItemCodeBehind);

        Assert.Empty((await scenario.CompleteAsync()).Items);
    }

    [Fact]
    public async Task AnUnqualifiedTwoArgumentCallIsNotABinder()
    {
        using var scenario = Scenario.Create(
            string.Format(BinderRepeater, """<%# Describe(Container.DataItem, "Cust|omer") %>"""),
            ItemCodeBehind);

        // `Eval` and `Bind` read their *first* argument as a path. A page method that happens to
        // take a string second argument is nobody's binder, and its argument is nobody's field.
        Assert.Null(await AspxLanguageHandler.DataBoundMemberAsync(
            scenario.Document, scenario.Caret, default));
    }

    [Fact]
    public void ABinderCallNestedInsideAnotherCallIsStillFound()
    {
        // Straight out of a real page: the path is the second argument of the inner call, and the
        // format string wrapping it is a string that must not be mistaken for one.
        const string text =
            """<img src='<%# ResolveUrl(string.Format("~/icons/{0}", DataBinder.Eval(Container.DataItem, "Icon"))) %>' />""";

        var found = DataBindingService.AllArguments(text)
            .Select(span => text.Substring(span.Start, span.Length))
            .ToList();

        Assert.Equal(["Icon"], found);
    }

    // ---- Container ------------------------------------------------------------------------------

    [Fact]
    public async Task ContainerIsTypedEvenWhenTheTemplateDeclaresNothing()
    {
        using var scenario = Scenario.Create(
            """
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Repeater ID="rptOrders" runat="server">
                <AlternatingItemTemplate>
                    <span><%# Container.I|D %></span>
                </AlternatingItemTemplate>
            </asp:Repeater>
            """,
            ItemCodeBehind);

        var projection = AspxProjectionService.Get(scenario.Document);
        Assert.NotNull(projection);

        int? projected = projection!.ToProjected(scenario.Caret);
        Assert.NotNull(projected);

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            projection.Document, projected!.Value, default);

        // `AlternatingItemTemplate` carries no [TemplateContainer], so the type is the one ASP.NET
        // falls back to rather than nothing at all.
        Assert.Equal("ID", symbol?.Name);
        Assert.Equal("Control", symbol?.ContainingType.Name);
    }

    // ---- A path is a reference ------------------------------------------------------------------

    [Fact]
    public async Task FindReferencesOfAPropertyIncludesThePathsThatReadIt()
    {
        // The caret is only there because the harness wants one; this asks about the whole file.
        using var scenario = Scenario.Create(
            string.Format(
                BinderRepeater, """<%# DataBinder.Eval(Container.DataItem, "Cust|omer") %>"""),
            ItemCodeBehind);

        var property = scenario.Document.Compilation
            .GetTypeByMetadataName("Fixture.Order")!
            .GetMembers("Customer")
            .Single();

        var references = await AspxReferenceService.FindInDocumentAsync(
            scenario.Document, property, default);

        var reference = Assert.Single(references);
        Assert.Equal("Customer", scenario.Document.Text.Substring(
            reference.Span.Start, reference.Span.Length));

        // What rename would write there — the whole segment, since a path segment carries no
        // prefix to preserve.
        Assert.Equal("Client", AspxReferenceService.RenamedText(reference, "Client"));
    }

    // ---- What the colouring pass sees ----------------------------------------------------------

    [Fact]
    public void EveryBindingLiteralInTheFileIsFound()
    {
        string text = string.Join(
            "\n",
            """<asp:Label runat="server" Text='<%# Eval("Customer") %>' />""",
            """<%# Bind("Buyer.Name") %>""",
            """<asp:Label runat="server" Text="<%# Eval('Amount') %>" />""",
            """<asp:Label runat="server" Text="plain" ToolTip='not a binding' />""");

        var found = DataBindingService.AllArguments(text)
            .Select(span => text.Substring(span.Start, span.Length))
            .ToList();

        // Both quote styles, and nothing that merely looks like a string.
        Assert.Equal(["Customer", "Buyer.Name", "Amount"], found);
    }

    /// <summary>
    /// An in-memory WebForms page written to a directory of its own, so that the files a page
    /// points at — a master page, an <c>.ascx</c> — resolve the way they do on disk.
    /// </summary>
    private sealed class Scenario : IDisposable
    {
        private const string DefaultCodeBehind = """
            namespace Fixture
            {
                public partial class SamplePage : System.Web.UI.Page
                {
                }
            }
            """;

        private static readonly string StubSource =
            File.ReadAllText(Path.Combine(FixturePaths.AspxProjectDir, "SystemWebStubs.cs"));

        public required string Directory { get; init; }
        public required int Caret { get; init; }
        public required AspxDocument Document { get; init; }

        public static Scenario Create(
            string markup, string? codeBehind = null, (string Name, string Text)[]? files = null)
        {
            int caret = markup.IndexOf('|');
            Assert.True(caret >= 0, "The markup carries no caret marker.");
            string text = markup.Remove(caret, 1);

            string directory = Path.Combine(
                Path.GetTempPath(), "roslynsense-binding-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);

            foreach (var (name, content) in files ?? [])
                File.WriteAllText(Path.Combine(directory, name), content);

            string markupPath = Path.Combine(directory, "SamplePage.aspx");
            File.WriteAllText(markupPath, text);

            var project = BuildProject(directory, codeBehind ?? DefaultCodeBehind);
            var compilation = project.GetCompilationAsync().GetAwaiter().GetResult()!;

            var parse = AspxSourceMappingService.Parse(
                markupPath, text, compilation, rootDirectory: directory,
                imports: AspxSourceMappingService.LoadWebConfigImports(directory));

            return new Scenario
            {
                Directory = directory,
                Caret = caret,
                Document = new AspxDocument(
                    markupPath, text, SourceText.From(text), project, compilation, parse),
            };
        }

        private static Project BuildProject(string directory, string codeBehind)
        {
            var workspace = new AdhocWorkspace();

            // The stubs shadow the System.Web types, so only the core runtime is needed.
            var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "netstandard.dll" }
                .Select(name => Path.Combine(
                    Path.GetDirectoryName(typeof(object).Assembly.Location)!, name))
                .Where(File.Exists)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

            var projectId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(
                    projectId, VersionStamp.Create(), "Fixture", "Fixture", LanguageNames.CSharp,
                    filePath: Path.Combine(directory, "Fixture.csproj"),
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)))
                .AddMetadataReferences(projectId, references)
                .AddDocument(DocumentId.CreateNewId(projectId), "SystemWebStubs.cs", StubSource,
                    filePath: Path.Combine(directory, "SystemWebStubs.cs"))
                .AddDocument(DocumentId.CreateNewId(projectId), "SamplePage.aspx.cs", codeBehind,
                    filePath: Path.Combine(directory, "SamplePage.aspx.cs"));

            return solution.GetProject(projectId)!;
        }

        public Task<CompletionList> CompleteAsync() =>
            AspxCompletionHandler.CompleteAsync(
                Document, Caret, trigger: null, new LspResolveCache(), default);

        public Task<RoslynMCP.Lsp.Protocol.Diagnostic[]> BindingDiagnosticsAsync() =>
            AspxBindingDiagnostics.DiagnosticsAsync(Document, default);

        /// <summary>
        /// The coloured runs of the page's binding paths, by the text each one covers, with the
        /// resolved ones told apart from the unresolved ones.
        /// </summary>
        public async Task<Dictionary<string, bool>> BindingColoursAsync()
        {
            var found = new Dictionary<string, bool>(StringComparer.Ordinal);

            await WebFormsLanguage.ColourBindingPathsAsync(
                Document,
                (span, type) => found[Document.Text[span.Start..span.End]] = type == Resolved,
                property: Resolved, unknown: Unknown, default);

            return found;
        }

        private const int Resolved = 1;
        private const int Unknown = 2;

        /// <summary>What hover says about the format string under the caret.</summary>
        public async Task<string?> FormatHoverAsync() =>
            (await AspxLanguageHandler.FormatHoverAsync(Document, Caret, default))?.Contents.Value;

        /// <summary>
        /// The coloured runs of the page's format strings, by the text each one covers.
        /// </summary>
        /// <remarks>
        /// The colouring pass directly rather than through <c>SemanticTokensFullAsync</c>, which
        /// starts by resolving a URI through the document service against the real workspace —
        /// this scenario's document lives in an <c>AdhocWorkspace</c> the service never saw.
        /// </remarks>
        public async Task<Dictionary<string, int>> FormatColoursAsync()
        {
            var found = new Dictionary<string, int>(StringComparer.Ordinal);

            await WebFormsLanguage.ColourFormatStringsAsync(
                Document,
                (span, type) => found[Document.Text[span.Start..span.End]] = type,
                default);

            return found;
        }

        /// <summary>
        /// What hover says about the binding segment under the caret, or null for no hover.
        /// </summary>
        /// <remarks>
        /// The two halves of the hover path rather than <c>HoverAsync</c> itself, which starts by
        /// resolving a URI through the document service against the real workspace — this scenario
        /// builds its document in an <c>AdhocWorkspace</c> that the service has never heard of.
        /// </remarks>
        public async Task<string?> HoverBindingAsync()
        {
            if (await AspxLanguageHandler.DataBoundSegmentAsync(Document, Caret, default)
                is not { } binding)
            {
                return null;
            }

            return AspxLanguageHandler.DescribeBinding(
                binding.Segment, binding.ItemType, Document, default);
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
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

                public Client Buyer { get; set; } = new Client();

                private string Secret { get; set; } = "";
            }

            public partial class SamplePage : System.Web.UI.Page
            {
            }
        }
        """;

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

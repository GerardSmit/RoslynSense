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
            public class Order
            {
                public string Customer { get; set; } = "";
                public decimal Amount { get; set; }
                public int Id;

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
                markupPath, text, compilation, rootDirectory: directory);

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

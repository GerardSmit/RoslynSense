using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Services;
using RoslynMCP.Languages.WebForms.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The WebForms language surface: where a caret lands, what it resolves to, and what completion
/// and handler generation do with that.
/// </summary>
/// <remarks>
/// The markup carries a <c>|</c> to mark the caret, which <see cref="WebFormsScenario"/> strips
/// before parsing. Scenarios are in-memory rather than on the shared <c>AspxProject</c> fixture
/// so that a test needing a broken handler or an extra control does not change what the designer
/// regeneration tests see.
/// </remarks>
public class WebFormsLanguageTests
{
    // ---- Caret classification --------------------------------------------------------------

    [Theory]
    [InlineData("<asp:But|", AspxContextKind.TagName)]
    [InlineData("<asp:|", AspxContextKind.TagName)]
    [InlineData("<asp:Button |", AspxContextKind.AttributeName)]
    [InlineData("<asp:Button Te|", AspxContextKind.AttributeName)]
    [InlineData("<asp:Button Text=\"|\"", AspxContextKind.AttributeValue)]
    [InlineData("<asp:Button Text=\"Hi\" OnClick=\"Btn|\"", AspxContextKind.AttributeValue)]
    [InlineData("<asp:Button Text=\"Hi\" />|", AspxContextKind.None)]
    [InlineData("plain text |", AspxContextKind.None)]
    [InlineData("<% var x = |", AspxContextKind.Code)]
    [InlineData("<%= Model.|", AspxContextKind.Code)]
    [InlineData("<%@ Page |", AspxContextKind.Directive)]
    [InlineData("<% done %> after |", AspxContextKind.None)]
    internal void CaretIsClassifiedByWhereItSitsInTheMarkup(string markup, AspxContextKind expected)
    {
        var (text, caret) = SplitCaret(markup);

        var context = AspxCompletionContextScanner.Classify(text, caret);

        Assert.Equal(expected, context.Kind);
    }

    [Fact]
    public void TagNameCaretCarriesThePrefixAndNameBeingTyped()
    {
        var (text, caret) = SplitCaret("<asp:But|ton runat=\"server\" />");

        var context = AspxCompletionContextScanner.Classify(text, caret);

        Assert.Equal(AspxContextKind.TagName, context.Kind);
        Assert.Equal("asp", context.TagPrefix);
        // The replaced span is the whole tag name, not just what precedes the caret — committing
        // an item has to overwrite the rest of the word.
        Assert.Equal("asp:Button", text.Substring(context.ReplaceSpan.Start, context.ReplaceSpan.Length));
    }

    [Fact]
    public void AttributeValueCaretCarriesTheAttributeItBelongsTo()
    {
        var (text, caret) = SplitCaret("<asp:Button OnClick=\"Handle|r\" />");

        var context = AspxCompletionContextScanner.Classify(text, caret);

        Assert.Equal(AspxContextKind.AttributeValue, context.Kind);
        Assert.Equal("OnClick", context.AttributeName);
        Assert.Equal("Handler", text.Substring(context.ReplaceSpan.Start, context.ReplaceSpan.Length));
    }

    // ---- Symbol resolution -----------------------------------------------------------------

    [Fact]
    public async Task TagNameResolvesToTheControlClass()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:But|ton ID="btnSave" runat="server" Text="Save" OnClick="BtnSave_Click" />
            """);

        var hit = scenario.Resolve();

        Assert.Equal(AspxHitKind.ControlType, hit!.Kind);
        Assert.Equal("Button", hit.Symbol!.Name);
    }

    [Fact]
    public async Task AttributeNameResolvesToTheProperty()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Button ID="btnSave" runat="server" Te|xt="Save" />
            """);

        var hit = scenario.Resolve();

        Assert.Equal(AspxHitKind.PropertyName, hit!.Kind);
        Assert.IsAssignableFrom<IPropertySymbol>(hit.Symbol);
        Assert.Equal("Text", hit.Symbol!.Name);
    }

    [Fact]
    public async Task EventAttributeNameResolvesToTheEvent()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Button ID="btnSave" runat="server" OnCl|ick="BtnSave_Click" />
            """);

        var hit = scenario.Resolve();

        Assert.Equal(AspxHitKind.EventName, hit!.Kind);
        Assert.Equal("Click", hit.Symbol!.Name);
    }

    [Fact]
    public async Task EventAttributeValueResolvesToTheHandlerMethod()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Button ID="btnSave" runat="server" OnClick="BtnSave|_Click" />
            """);

        var hit = scenario.Resolve();

        Assert.Equal(AspxHitKind.EventHandler, hit!.Kind);
        Assert.Equal("BtnSave_Click", hit.Symbol!.Name);
        Assert.Equal("Click", hit.Event!.Name);
    }

    [Fact]
    public async Task MissingHandlerStillResolvesToTheEventSoAFixCanBeOffered()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Button ID="btnSave" runat="server" OnClick="NoSuch|Handler" />
            """);

        var hit = scenario.Resolve();

        Assert.Equal(AspxHitKind.EventHandler, hit!.Kind);
        Assert.Null(hit.Symbol);
        Assert.Equal("Click", hit.Event!.Name);
        Assert.Equal("NoSuchHandler", hit.Name);
    }

    [Fact]
    public async Task ControlIdResolvesToTheCodeBehindField()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Button ID="btn|Save" runat="server" />
            """);

        var hit = scenario.Resolve();

        Assert.Equal(AspxHitKind.ControlId, hit!.Kind);
        Assert.Equal("btnSave", hit.Symbol!.Name);
    }

    [Fact]
    public async Task InheritsResolvesToTheCodeBehindClass()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.Sample|Page" %>
            """);

        var hit = scenario.Resolve();

        Assert.Equal(AspxHitKind.Inherits, hit!.Kind);
        Assert.Equal("SamplePage", hit.Symbol!.Name);
    }

    [Fact]
    public async Task ACaretInPlainMarkupResolvesToNothing()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <div>hel|lo</div>
            """);

        Assert.Null(scenario.Resolve());
    }

    // ---- Diagnostics -----------------------------------------------------------------------

    [Fact]
    public async Task AnEventWithNoHandlerIsReportedOnTheHandlerName()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Button ID="btnSave" runat="server" OnClick="NoSuchHandler" />
            """);

        var diagnostic = Assert.Single(
            scenario.Document.Parse.RawDiagnostics
                .Select(d => (Diagnostic)d)
                .Where(d => d.Id == "WFC0008"));

        // The span has to be the handler name so the quick fix knows what to generate.
        Assert.Equal("NoSuchHandler", scenario.Text.Substring(
            diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public async Task AWiredUpEventIsNotReported()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Button ID="btnSave" runat="server" OnClick="BtnSave_Click" />
            """);

        Assert.DoesNotContain(
            scenario.Document.Parse.RawDiagnostics.Select(d => (Diagnostic)d),
            d => d.Id == "WFC0008");
    }

    // ---- Event handler generation ----------------------------------------------------------

    [Fact]
    public async Task TheSuggestedHandlerNameFollowsTheDesignerConvention()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Button ID="btnCancel" runat="server" />
            """);

        var control = scenario.Controls().Single();
        var click = control.ControlType.GetMembers("Click").OfType<IEventSymbol>().Single();

        Assert.Equal("btnCancel_Click",
            AspxEventHandlerService.SuggestName(control, click, scenario.Document.CodeBehind));
    }

    [Fact]
    public async Task ATakenHandlerNameGetsASuffixRatherThanACollision()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Button ID="BtnSave" runat="server" />
            """);

        var control = scenario.Controls().Single();
        var click = control.ControlType.GetMembers("Click").OfType<IEventSymbol>().Single();

        // The code-behind already declares BtnSave_Click.
        Assert.Equal("BtnSave_Click1",
            AspxEventHandlerService.SuggestName(control, click, scenario.Document.CodeBehind));
    }

    [Fact]
    public async Task GeneratingAHandlerWritesAMethodWithTheDelegatesSignature()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Button ID="btnCancel" runat="server" OnClick="BtnCancel_Click" />
            """);

        var control = scenario.Controls().Single();
        var click = control.ControlType.GetMembers("Click").OfType<IEventSymbol>().Single();

        string updated = await scenario.ApplyGeneratedHandlerAsync(click, "BtnCancel_Click");

        Assert.Contains("void BtnCancel_Click(", updated);
        Assert.Contains("object sender", updated);
        Assert.Contains("EventArgs e", updated);
        // It goes into the class the Inherits directive names, not into a new one.
        Assert.Equal(1, CountOccurrences(updated, "class SamplePage"));
    }

    [Fact]
    public async Task GeneratingAHandlerLeavesTheExistingMembersAlone()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Button ID="btnCancel" runat="server" />
            """);

        var control = scenario.Controls().Single();
        var click = control.ControlType.GetMembers("Click").OfType<IEventSymbol>().Single();

        string updated = await scenario.ApplyGeneratedHandlerAsync(click, "BtnCancel_Click");

        Assert.Contains("BtnSave_Click", updated);
        Assert.Contains("protected global::System.Web.UI.WebControls.Button btnSave", updated);
    }

    [Fact]
    public async Task AnExistingCompatibleMethodIsOfferedAsAHandler()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Button ID="btnCancel" runat="server" />
            """);

        var control = scenario.Controls().Single();
        var click = control.ControlType.GetMembers("Click").OfType<IEventSymbol>().Single();

        var handlers = AspxEventHandlerService
            .CompatibleHandlers(scenario.Document.CodeBehind, click)
            .Select(m => m.Name)
            .ToList();

        Assert.Contains("BtnSave_Click", handlers);
        // Not a candidate: it takes no parameters.
        Assert.DoesNotContain("Reset", handlers);
    }

    // ---- Catalog ---------------------------------------------------------------------------

    [Fact]
    public async Task RegisteredPrefixesOfferTheirControls()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Button ID="btnSave" runat="server" />
            """);

        var labels = AspxCatalog.Controls(scenario.Document)
            .Select(c => $"{c.Prefix}:{c.TagName}")
            .ToList();

        Assert.Contains("asp:Button", labels);
        Assert.Contains("asp:Label", labels);
    }

    [Fact]
    public async Task AControlOffersItsPropertiesAndEvents()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Button ID="btnSave" runat="server" />
            """);

        var button = scenario.Controls().Single().ControlType;

        Assert.Contains("Text", AspxCatalog.WritableProperties(button).Select(p => p.Name));
        Assert.Contains("Click", AspxCatalog.Events(button).Select(e => e.Name));
    }

    // ---- Inline C# projection --------------------------------------------------------------

    [Fact]
    public async Task CodeInAnExpressionBindsAgainstTheCodeBehind()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <div><%= Greet|ing %></div>
            """);

        var projection = AspxProjectionService.Get(scenario.Document);
        Assert.NotNull(projection);

        int? projected = projection!.ToProjected(scenario.Caret);
        Assert.NotNull(projected);

        var symbol = await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder
            .FindSymbolAtPositionAsync(projection.Document, projected!.Value, default);

        Assert.Equal("Greeting", symbol!.Name);
    }

    [Fact]
    public async Task AProjectedSpanMapsBackToTheMarkupItCameFrom()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <div><%= Greeting %></div>
            """);

        var projection = AspxProjectionService.Get(scenario.Document)!;
        int markup = scenario.Text.IndexOf("Greeting", StringComparison.Ordinal);
        int projected = projection.ToProjected(markup)!.Value;

        var roundTripped = projection.ToAspx(new TextSpan(projected, "Greeting".Length));

        Assert.Equal(new TextSpan(markup, "Greeting".Length), roundTripped);
    }

    [Fact]
    public async Task ScaffoldingInTheProjectionMapsBackToNothing()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <div><%= Greeting %></div>
            """);

        var projection = AspxProjectionService.Get(scenario.Document)!;

        // Offset 0 is the generated header comment: it exists in no markup file, and a result
        // landing there must not be reported as a location in one.
        Assert.Null(projection.ToAspx(new TextSpan(0, 4)));
    }

    [Fact]
    public async Task AVisualBasicPageIsNotProjectedAsCSharp()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="VB" Inherits="Fixture.SamplePage" %>
            <div><%= Greeting %></div>
            <% Dim x As Integer = 1 %>
            """);

        // The markup half still works — controls and attributes are symbols either way — but
        // emitting VB into a C# document would bind to nothing and squiggle everything.
        Assert.Equal(WebFormsCore.Nodes.Language.VisualBasic, scenario.Document.Tree!.Language);
        Assert.Null(AspxProjectionService.Get(scenario.Document));
    }

    [Fact]
    public async Task ASingleFilePageProjectsIntoAClassOfItsOwn()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" %>
            <script runat="server">
                private string Caption() { return "hi"; }
            </script>
            <div><%= Caption() %></div>
            """);

        // With no Inherits the parser falls back to Page itself, which has no source here —
        // so the projection derives from it rather than reopening it as a partial.
        Assert.Equal("Page", scenario.Document.CodeBehind?.Name);

        var projection = AspxProjectionService.Get(scenario.Document);
        Assert.NotNull(projection);

        string text = projection!.Text.ToString();
        Assert.Contains(": global::System.Web.UI.Page", text);
        Assert.DoesNotContain("partial class Page ", text);
        Assert.Contains("Caption", text);
    }

    // ---- Markup references -----------------------------------------------------------------

    [Fact]
    public async Task AHandlerMethodIsReferencedByTheAttributeThatNamesIt()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Button ID="btnSave" runat="server" OnClick="BtnSave_Click" />
            """);

        var handler = scenario.Document.CodeBehind!
            .GetMembers("BtnSave_Click").Single();

        var references = scenario.MarkupReferences(handler);

        var span = Assert.Single(references);
        Assert.Equal("BtnSave_Click", scenario.Text.Substring(span.Start, span.Length));
    }

    [Fact]
    public async Task AControlTypeIsReferencedByEveryTagThatUsesIt()
    {
        var scenario = await WebFormsScenario.CreateAsync("""
            <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
            <asp:Label ID="lblOne" runat="server" />
            <asp:Label ID="lblTwo" runat="server"></asp:Label>
            """);

        var label = scenario.Controls().First().ControlType;

        // Three: the two opening tags plus the one closing tag.
        Assert.Equal(3, scenario.MarkupReferences(label).Count);
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static (string Text, int Caret) SplitCaret(string markup)
    {
        int caret = markup.IndexOf('|');
        return caret < 0 ? (markup, markup.Length) : (markup.Remove(caret, 1), caret);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal);
             i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// An in-memory WebForms page: markup plus a code-behind, compiled against the fixture's
    /// System.Web stubs, with the caret marker stripped out.
    /// </summary>
    private sealed class WebFormsScenario
    {
        private const string DefaultCodeBehind = """
            namespace Fixture
            {
                public partial class SamplePage : System.Web.UI.Page
                {
                    protected global::System.Web.UI.WebControls.Button btnSave = null!;
                    protected global::System.Web.UI.WebControls.Button btnCancel = null!;
                    protected global::System.Web.UI.WebControls.Button BtnSave = null!;
                    protected global::System.Web.UI.WebControls.Label lblOne = null!;
                    protected global::System.Web.UI.WebControls.Label lblTwo = null!;

                    public string Greeting => "hello";

                    protected void BtnSave_Click(object sender, System.EventArgs e) { }

                    protected void Reset() { }
                }
            }
            """;

        private static readonly string StubSource =
            File.ReadAllText(Path.Combine(FixturePaths.AspxProjectDir, "SystemWebStubs.cs"));

        public required string Directory { get; init; }
        public required string MarkupPath { get; init; }
        public required string Text { get; init; }
        public required int Caret { get; init; }
        public required AspxDocument Document { get; init; }

        public static Task<WebFormsScenario> CreateAsync(
            string markup, string? codeBehind = null)
        {
            var (text, caret) = SplitCaret(markup);

            string directory = Path.Combine(
                Path.GetTempPath(), "roslynsense-webforms-" + Guid.NewGuid().ToString("N"));
            string markupPath = Path.Combine(directory, "SamplePage.aspx");

            var project = BuildProject(directory, codeBehind ?? DefaultCodeBehind);
            var compilation = project.GetCompilationAsync().GetAwaiter().GetResult()!;

            var parse = AspxSourceMappingService.Parse(
                markupPath, text, compilation, rootDirectory: directory);

            return Task.FromResult(new WebFormsScenario
            {
                Directory = directory,
                MarkupPath = markupPath,
                Text = text,
                Caret = caret,
                Document = new AspxDocument(
                    markupPath, text, SourceText.From(text), project, compilation, parse),
            });
        }

        private static Project BuildProject(string directory, string codeBehind)
        {
            var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();

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

        public AspxHit? Resolve() => AspxSymbolResolver.ResolveAt(Document, Caret);

        public IEnumerable<WebFormsCore.Nodes.ControlNode> Controls() =>
            AspxSymbolResolver.EnumerateControls(Document.Tree!);

        public List<TextSpan> MarkupReferences(ISymbol symbol) =>
            AspxReferenceService.FindInDocument(Document, symbol).Select(r => r.Span).ToList();

        /// <summary>Generates the handler and returns the code-behind as it would end up.</summary>
        public async Task<string> ApplyGeneratedHandlerAsync(IEventSymbol @event, string name)
        {
            var generated = await AspxEventHandlerService.GenerateAsync(Document, @event, name, default);
            Assert.NotNull(generated);

            var (filePath, changes) = generated!.Value;
            var target = WorkspaceService.FindDocumentInProject(Document.Project, filePath);
            Assert.NotNull(target);

            var text = await target!.GetTextAsync();
            return text.WithChanges(changes).ToString();
        }
    }
}

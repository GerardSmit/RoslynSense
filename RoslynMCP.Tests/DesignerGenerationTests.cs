using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.Designers;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers ASPX designer regeneration.
/// </summary>
/// <remarks>
/// Variations run against an isolated in-memory project (see <see cref="MarkupScenario"/>) rather
/// than the shared <c>AspxProject</c> fixture: writing extra designer files into that fixture would
/// declare the same partial-class members twice and change what regeneration legitimately emits.
/// One end-to-end test still exercises the real service against the committed fixture.
/// </remarks>
[Collection(SharedState.Name)]
public class DesignerGenerationTests
{
    [Fact]
    public async Task WhenMarkupUnchangedThenRegenerationReproducesCommittedDesignerExactly()
    {
        var service = new DesignerRegenerationService([new AspxDesignerGenerator()]);

        var result = await service.RegenerateAsync(FixturePaths.DesignerAspxFile, dryRun: true, default);

        // Unchanged means the generated text was byte-identical to the checked-in file.
        Assert.Empty(result.Errors);
        Assert.Equal(DesignerOutcome.Unchanged, result.Outcome);
    }

    [Fact]
    public async Task WhenPageHasControlsThenEachTopLevelControlGetsATypedField()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: """
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <form id="theForm" runat="server">
                        <asp:Label ID="lblHeading" runat="server" />
                        <asp:TextBox ID="txtName" runat="server" />
                    </form>
                    """,
            codeBehind: "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }");

        var content = await scenario.GenerateAsync();

        Assert.Contains("protected global::System.Web.UI.HtmlControls.HtmlForm theForm;", content);
        Assert.Contains("protected global::System.Web.UI.WebControls.Label lblHeading;", content);
        Assert.Contains("protected global::System.Web.UI.WebControls.TextBox txtName;", content);
        Assert.Equal(3, CountFields(content));
    }

    [Fact]
    public async Task WhenControlAddedThenExactlyOneFieldIsAdded()
    {
        const string codeBehind = "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }";
        const string before = """
                              <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                              <asp:Label ID="lblHeading" runat="server" />
                              """;

        await using var original = await MarkupScenario.CreateAsync(before, codeBehind);
        var baseline = await original.GenerateAsync();

        await using var extended = await MarkupScenario.CreateAsync(
            before + "\r\n<asp:Label ID=\"lblAdded\" runat=\"server\" />", codeBehind);
        var updated = await extended.GenerateAsync();

        Assert.Contains("protected global::System.Web.UI.WebControls.Label lblAdded;", updated);
        Assert.Equal(CountFields(baseline) + 1, CountFields(updated));
    }

    [Fact]
    public async Task WhenControlNestedInTemplateThenNoFieldIsGenerated()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: """
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <asp:Repeater ID="rptItems" runat="server">
                        <ItemTemplate>
                            <asp:Label ID="lblNested" runat="server" />
                        </ItemTemplate>
                    </asp:Repeater>
                    """,
            codeBehind: "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }");

        var content = await scenario.GenerateAsync();

        // A template-nested control is reached through FindControl, never a designer field.
        Assert.Contains("rptItems;", content);
        Assert.DoesNotContain("lblNested", content);
        Assert.Equal(1, CountFields(content));
    }

    [Fact]
    public async Task WhenControlSitsInSingleInstanceTemplateThenItStillGetsAField()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: """
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <asp:UpdatePanel ID="upMain" runat="server">
                        <ContentTemplate>
                            <asp:Label ID="lblInside" runat="server" />
                            <asp:Repeater ID="rptInside" runat="server">
                                <ItemTemplate>
                                    <asp:Label ID="lblPerItem" runat="server" />
                                </ItemTemplate>
                            </asp:Repeater>
                        </ContentTemplate>
                    </asp:UpdatePanel>
                    """,
            codeBehind: "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }");

        var content = await scenario.GenerateAsync();

        // ContentTemplate is [TemplateInstance(Single)]: its controls exist exactly once, so
        // Visual Studio gives them fields. The Repeater's ItemTemplate stays excluded.
        Assert.Contains("protected global::System.Web.UI.UpdatePanel upMain;", content);
        Assert.Contains("protected global::System.Web.UI.WebControls.Label lblInside;", content);
        Assert.Contains("protected global::System.Web.UI.WebControls.Repeater rptInside;", content);
        Assert.DoesNotContain("lblPerItem", content);
        Assert.Equal(3, CountFields(content));
    }

    [Fact]
    public async Task WhenHtmlElementsRunAtServerThenFieldsUseTheSpecificHtmlControlTypes()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: """
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <input runat="server" id="txtName" />
                    <INPUT runat="server" id="chkAgree" TYPE="CHECKBOX" />
                    <input runat="server" id="txtWhen" type="date" />
                    <input runat="server" id="btnGo" type="submit" />
                    <select runat="server" id="ddlCountry"></select>
                    <textarea runat="server" id="txtNotes"></textarea>
                    <a runat="server" id="lnkHome"></a>
                    """,
            codeBehind: "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }");

        var content = await scenario.GenerateAsync();

        // Visual Studio types these fields through System.Web's tag mapper; an `<input>` that
        // came out as HtmlGenericControl would break every code-behind touching `.Value`.
        // Tag and type dispatch are case-insensitive; an HTML5 type the mapper does not know
        // is HtmlInputGenericControl, and submit falls back to HtmlInputButton when the
        // framework has no HtmlInputSubmit (this stub set does not, like pre-4.5).
        Assert.Contains("protected global::System.Web.UI.HtmlControls.HtmlInputText txtName;", content);
        Assert.Contains("protected global::System.Web.UI.HtmlControls.HtmlInputCheckBox chkAgree;", content);
        Assert.Contains("protected global::System.Web.UI.HtmlControls.HtmlInputGenericControl txtWhen;", content);
        Assert.Contains("protected global::System.Web.UI.HtmlControls.HtmlInputButton btnGo;", content);
        Assert.Contains("protected global::System.Web.UI.HtmlControls.HtmlSelect ddlCountry;", content);
        Assert.Contains("protected global::System.Web.UI.HtmlControls.HtmlTextArea txtNotes;", content);
        Assert.Contains("protected global::System.Web.UI.HtmlControls.HtmlAnchor lnkHome;", content);
        Assert.Equal(7, CountFields(content));
    }

    [Fact]
    public async Task WhenItemsSitInADefaultCollectionThenTheyNeverBecomeFields()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: """
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <asp:DropDownList ID="ddlChoice" runat="server">
                        <asp:ListItem id="itemFirst" Text="First" Value="1" />
                        <asp:ListItem Text="Second" Value="2" />
                    </asp:DropDownList>
                    """,
            codeBehind: "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }");

        var content = await scenario.GenerateAsync();

        // [ParseChildren(true, "Items")]: the ListItems are collection items without an <Items>
        // wrapper. They are plain objects, so even an id on one adds no field.
        Assert.Contains("protected global::System.Web.UI.WebControls.DropDownList ddlChoice;", content);
        Assert.DoesNotContain("itemFirst", content);
        Assert.Equal(1, CountFields(content));
    }

    [Fact]
    public async Task WhenTwoMarkupFilesShareAClassThenTheCanonicalDesignerUnionsThem()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: """
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <asp:Label ID="lblShared" runat="server" />
                    <asp:Label ID="lblOnlyMain" runat="server" />
                    """,
            codeBehind: "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }",
            ("Variant.aspx", """
                             <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                             <asp:Label ID="lblShared" runat="server" />
                             <asp:TextBox ID="txtOnlyVariant" runat="server" />
                             """));

        var content = await scenario.GenerateAsync();

        // The class is loaded from either markup file, so the designer carries the union; a
        // control missing from one variant really is null when that variant is the one loaded.
        Assert.Contains("#nullable enable", content);
        Assert.Contains("protected global::System.Web.UI.WebControls.Label lblShared;", content);
        Assert.Contains("protected global::System.Web.UI.WebControls.Label? lblOnlyMain;", content);
        Assert.Contains("protected global::System.Web.UI.WebControls.TextBox? txtOnlyVariant;", content);
        Assert.Equal(3, CountFields(content));
    }

    [Fact]
    public async Task WhenMarkupIsNotTheCanonicalFileThenItsDesignerIsAnEmptyPartial()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: """
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <asp:Label ID="lblMain" runat="server" />
                    """,
            codeBehind: "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }",
            ("Variant.aspx", """
                             <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                             <asp:Label ID="lblVariant" runat="server" />
                             """));

        var generator = new AspxDesignerGenerator();
        var result = await generator.GenerateAsync(
            scenario.SiblingPaths.Single(), scenario.Project, default);

        // The fields live in the canonical designer (beside the class's own code-behind);
        // emitting them here too would declare every member twice.
        Assert.NotNull(result.Content);
        Assert.Contains("public partial class SamplePage {", result.Content);
        Assert.Equal(0, CountFields(result.Content!));
        Assert.Equal(scenario.MarkupPath, Assert.Single(result.RelatedSources), ignoreCase: true);
    }

    [Fact]
    public async Task WhenSharedIdHasDifferentTypesThenTheFieldUsesTheirCommonBase()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: """
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <asp:Button ID="ctlAction" runat="server" />
                    """,
            codeBehind: "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }",
            ("Variant.aspx", """
                             <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                             <asp:LinkButton ID="ctlAction" runat="server" />
                             """));

        var content = await scenario.GenerateAsync();

        // Button and LinkButton share WebControl; only a common base can hold both.
        Assert.Contains("protected global::System.Web.UI.WebControls.WebControl ctlAction;", content);
        Assert.Equal(1, CountFields(content));
    }

    [Fact]
    public async Task WhenFieldDeclaredInCodeBehindThenDesignerSkipsItToAvoidDuplicate()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: """
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <asp:Label ID="lblHandWritten" runat="server" />
                    <asp:Label ID="lblGenerated" runat="server" />
                    """,
            codeBehind: """
                        namespace Fixture {
                            public partial class SamplePage : System.Web.UI.Page {
                                protected System.Web.UI.WebControls.Label lblHandWritten;
                            }
                        }
                        """);

        var content = await scenario.GenerateAsync();

        // Emitting lblHandWritten here as well would be a duplicate member (CS0102).
        Assert.DoesNotContain("lblHandWritten", content);
        Assert.Contains("lblGenerated;", content);
        Assert.Equal(1, CountFields(content));
    }

    [Fact]
    public async Task WhenInheritsCannotBeResolvedThenGenerationFailsWithoutContent()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: "<%@ Page Language=\"C#\" Inherits=\"Fixture.NoSuchClassAnywhere\" %>\r\n<html>",
            codeBehind: "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }");

        var result = await scenario.GenerateResultAsync();

        Assert.Null(result.Content);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task WhenGenerationFailsThenExistingDesignerIsLeftUntouched()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: "<%@ Page Language=\"C#\" Inherits=\"Fixture.NoSuchClassAnywhere\" %>\r\n<html>",
            codeBehind: "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }");

        const string existing = "// previously generated content";
        await File.WriteAllTextAsync(scenario.DesignerPath, existing);

        var result = await scenario.RegenerateThroughServiceAsync(dryRun: false);

        Assert.Equal(DesignerOutcome.Failed, result.Outcome);
        Assert.Equal(existing, await File.ReadAllTextAsync(scenario.DesignerPath));
    }

    [Fact]
    public async Task WhenDryRunThenNothingIsWritten()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: """
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <asp:Label ID="lblHeading" runat="server" />
                    """,
            codeBehind: "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }");

        var result = await scenario.GenerateResultAsync();

        Assert.NotNull(result.Content);
        Assert.False(File.Exists(scenario.DesignerPath));
    }

    [Theory]
    [InlineData("Page.aspx", "Page.aspx.designer.cs")]
    [InlineData("Control.ascx", "Control.ascx.designer.cs")]
    [InlineData("Site.master", "Site.master.designer.cs")]
    public void WhenMarkupFileThenDesignerPathAppendsSuffix(string source, string expected)
    {
        var generator = new AspxDesignerGenerator();

        Assert.True(generator.CanHandle(source));
        Assert.Equal(expected, generator.GetDesignerPath(source));
    }

    [Theory]
    [InlineData("Handler.ashx")]
    [InlineData("Service.asmx")]
    [InlineData("Model.dbml")]
    public void WhenFileHasNoControlTreeThenAspxGeneratorDeclinesIt(string source) =>
        Assert.False(new AspxDesignerGenerator().CanHandle(source));

    [Fact]
    public void WhenDbmlThenDesignerPathReplacesExtensionRatherThanAppending()
    {
        var generator = new DbmlDesignerGenerator();

        Assert.True(generator.CanHandle("Northwind.dbml"));

        // LINQ to SQL emits Northwind.designer.cs, not Northwind.dbml.designer.cs.
        Assert.Equal("Northwind.designer.cs", generator.GetDesignerPath("Northwind.dbml"));
    }

    [Theory]
    [InlineData("// existing\r\nline\r\n", "\r\n")]
    [InlineData("// existing\nline\n", "\n")]
    public async Task WhenDesignerExistsThenRegenerationKeepsItsLineEndings(
        string existing, string expectedNewline)
    {
        // A repository with `text=auto eol=lf` checks designer files out as LF even on Windows,
        // while the generator's platform newline is CRLF. Without matching the file, every
        // regeneration would rewrite every line — and the byte-for-byte comparison would fail.
        var path = Path.Combine(Path.GetTempPath(), $"roslynsense-eol-{Guid.NewGuid():N}.designer.cs");
        await File.WriteAllTextAsync(path, existing);

        try
        {
            var result = DesignerRegenerationService.MatchLineEndings("alpha\r\nbeta\r\n", path);

            Assert.Equal($"alpha{expectedNewline}beta{expectedNewline}", result);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void WhenDesignerIsNewThenCrlfIsUsedAsVisualStudioWould()
    {
        var absent = Path.Combine(Path.GetTempPath(), $"roslynsense-absent-{Guid.NewGuid():N}.designer.cs");

        Assert.Equal("alpha\r\nbeta\r\n", DesignerRegenerationService.MatchLineEndings("alpha\nbeta\n", absent));
    }

    [Fact]
    public async Task WhenTheEditorInitializesThenAnAddedControlGetsItsFieldWithoutATool()
    {
        // The gap: regeneration was armed only by the MCP open_solution tool, so a control typed
        // into markup in VS Code produced no field and stayed invisible to its code-behind.
        string workingDirectory = Path.Combine(
            Path.GetTempPath(), $"roslynsense-designer-watch-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(workingDirectory);

        string markupPath = FixturePaths.DesignerAspxFile;
        string designerPath = FixturePaths.DesignerAspxDesignerFile;
        string originalMarkup = await File.ReadAllTextAsync(markupPath);
        string originalDesigner = await File.ReadAllTextAsync(designerPath);
        string? previousSolution = WorkspaceService.BoundSolutionPath;

        var session = new SolutionSessionService(
            new DesignerRegenerationService([new AspxDesignerGenerator()]));

        try
        {
            // The watcher follows the solution this process is bound to, and the fixture project
            // has none of its own.
            string solution = Path.Combine(workingDirectory, "Watched.slnx");
            await File.WriteAllTextAsync(
                solution, $"""<Solution><Project Path="{FixturePaths.AspxProjectFile}" /></Solution>""");
            WorkspaceService.BindSolution(solution);

            using var server = new LspServer(new DesignerServices(session));
            server.Initialize(new InitializeParams(
                ProcessId: null, RootUri: null, WorkspaceFolders: null, Capabilities: null));

            Assert.True(session.IsWatching, "initialize left the designer watcher off.");
            Assert.Equal(Path.GetFullPath(solution), session.SolutionPath!, ignoreCase: true);

            var regenerated = new TaskCompletionSource<WatchedRegeneration>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            session.Regenerated += entry => regenerated.TrySetResult(entry);

            await File.WriteAllTextAsync(markupPath, originalMarkup.Replace(
                "    </form>",
                "        <asp:Label ID=\"lblWatcherAdded\" runat=\"server\" />\r\n    </form>"));

            WatchedRegeneration reported;
            try
            {
                // Generous: the first regeneration loads the project through MSBuild.
                reported = await regenerated.Task.WaitAsync(TimeSpan.FromMinutes(2));
            }
            catch (TimeoutException)
            {
                Assert.Fail("No designer was rewritten. Watcher history: " + Describe(session));
                throw;
            }

            Assert.Equal(DesignerOutcome.Updated, reported.Outcome);
            Assert.Equal(
                Path.GetFullPath(designerPath), Path.GetFullPath(reported.DesignerPath), ignoreCase: true);
            Assert.Contains(
                "protected global::System.Web.UI.WebControls.Label lblWatcherAdded;",
                await File.ReadAllTextAsync(designerPath));
        }
        finally
        {
            // Stop watching before the fixture goes back, or restoring the markup regenerates
            // over the designer we just restored.
            session.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(750));

            await File.WriteAllTextAsync(markupPath, originalMarkup);
            await File.WriteAllTextAsync(designerPath, originalDesigner);

            // Through the setter rather than BindSolution, which ignores a null by design: this
            // temporary solution is about to be deleted, and leaving the process bound to it
            // would change what every later solution-scoped query in the run resolves to.
            typeof(WorkspaceService)
                .GetProperty(nameof(WorkspaceService.BoundSolutionPath))!
                .SetValue(null, previousSolution);

            try { System.IO.Directory.Delete(workingDirectory, recursive: true); } catch { }
        }
    }

    private static string Describe(SolutionSessionService session) =>
        session.History.Count == 0
            ? "(nothing was regenerated)"
            : string.Join("; ", session.History.Select(entry =>
                $"{Path.GetFileName(entry.SourcePath)} {entry.Outcome} {string.Join(",", entry.Errors)}"));

    /// <summary>The one service <c>initialize</c> needs to start the watcher.</summary>
    private sealed class DesignerServices(SolutionSessionService session) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(SolutionSessionService) ? session : null;
    }

    private static int CountFields(string designerContent) =>
        designerContent.Split("        protected global::").Length - 1;

    /// <summary>
    /// A self-contained markup + code-behind pair in a temp directory, backed by an in-memory
    /// Roslyn project that reuses the fixture's System.Web stubs so <c>asp:*</c> tags resolve.
    /// </summary>
    /// <summary>
    /// A user control registered under a tag name containing an underscore — the WebForms norm for
    /// module-scoped controls — must still resolve to its concrete code-behind type.
    /// </summary>
    /// <remarks>
    /// The lexer used to read tag names as alphanumerics only, so <c>&lt;uc:Order_Panel&gt;</c>
    /// truncated to <c>Order_Panel</c>'s first segment, missed its <c>&lt;%@ Register %&gt;</c>
    /// entry and degraded to the untyped <c>Control</c> base — a designer that no longer compiles
    /// against a code-behind calling the concrete type's members. Controls whose tag name happened
    /// to be alphanumeric, in the same file, came out fine, which is what made it look arbitrary.
    /// </remarks>
    [Fact]
    public async Task WhenUserControlTagNameContainsUnderscoreThenTheConcreteTypeIsStillEmitted()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: """
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <%@ Register TagPrefix="uc" TagName="ChatBox" Src="~/ChatBox.ascx" %>
                    <%@ Register TagPrefix="uc" TagName="Order_Panel_Sent" Src="~/Order_Panel_Sent.ascx" %>
                    <uc:ChatBox runat="server" ID="theChatBox" />
                    <uc:Order_Panel_Sent runat="server" ID="theOrderPanel" />
                    """,
            codeBehind: """
                        namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }
                        namespace Fixture.Widgets { public partial class ChatBox : System.Web.UI.UserControl { } }
                        namespace Fixture.Widgets { public partial class Order_Panel_Sent : System.Web.UI.UserControl { } }
                        """,
            ("ChatBox.ascx",
                """<%@ Control Language="C#" Inherits="Fixture.Widgets.ChatBox" CodeBehind="ChatBox.ascx.cs" %>"""),
            ("Order_Panel_Sent.ascx",
                """<%@ Control Language="C#" Inherits="Fixture.Widgets.Order_Panel_Sent" CodeBehind="Order_Panel_Sent.ascx.cs" %>"""));

        var content = await scenario.GenerateAsync();

        Assert.Contains("protected global::Fixture.Widgets.ChatBox theChatBox;", content);
        Assert.Contains("protected global::Fixture.Widgets.Order_Panel_Sent theOrderPanel;", content);
        Assert.DoesNotContain("global::System.Web.UI.Control ", content);
    }

    /// <summary>
    /// The rest of the characters a tag name is allowed to be spelled with: the hyphen a custom
    /// element is defined by, the dot System.Web's own tag regex permits, and non-ASCII letters.
    /// </summary>
    [Theory]
    [InlineData("order-panel")]
    [InlineData("order.panel")]
    [InlineData("bestelpaneel")]
    [InlineData("Bestellübersicht")]
    public async Task WhenUserControlTagNameIsNotPlainAsciiThenTheConcreteTypeIsStillEmitted(string tag)
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: $"""
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <%@ Register TagPrefix="uc" TagName="{tag}" Src="~/OrderPanel.ascx" %>
                    <uc:{tag} runat="server" ID="theOrderPanel" />
                    """,
            codeBehind: """
                        namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }
                        namespace Fixture.Widgets { public partial class OrderPanel : System.Web.UI.UserControl { } }
                        """,
            ("OrderPanel.ascx",
                """<%@ Control Language="C#" Inherits="Fixture.Widgets.OrderPanel" CodeBehind="OrderPanel.ascx.cs" %>"""));

        var content = await scenario.GenerateAsync();

        Assert.Contains("protected global::Fixture.Widgets.OrderPanel theOrderPanel;", content);
    }

    /// <summary>
    /// Lines of a designer file in the shape these files are actually found in: written by Visual
    /// Studio years ago and reformatted by the team's tooling since — Allman braces, blank lines
    /// stripped back to empty — while keeping the trailing space Visual Studio left in the banner.
    /// </summary>
    private static string ReformattedDesigner(params string[] fieldNames)
    {
        var lines = new List<string>
        {
            "//------------------------------------------------------------------------------",
            "// <auto-generated>",
            "//     This code was generated by a tool.",
            "//",
            "//     Changes to this file may cause incorrect behavior and will be lost if",
            "//     the code is regenerated. ", // The trailing space is Visual Studio's, and load-bearing here.
            "// </auto-generated>",
            "//------------------------------------------------------------------------------",
            "",
            "namespace Fixture",
            "{",
            "",
            "",
            "    public partial class SamplePage",
            "    {",
        };

        foreach (var name in fieldNames)
        {
            lines.AddRange([
                "",
                "        /// <summary>",
                $"        /// {name} control.",
                "        /// </summary>",
                "        /// <remarks>",
                "        /// Auto-generated field.",
                "        /// To modify move field declaration from designer file to code-behind file.",
                "        /// </remarks>",
                $"        protected global::System.Web.UI.WebControls.Label {name};",
            ]);
        }

        lines.AddRange(["    }", "}", ""]);
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// A designer file whose markup has not changed must come back byte-for-byte identical, whatever
    /// conventions it happens to be written in.
    /// </summary>
    /// <remarks>
    /// Regeneration used to impose one canonical style on every file it touched: it stripped the
    /// UTF-8 byte order mark, moved opening braces onto the declaration line and re-indented blank
    /// separator lines. On a file that had been through a formatter that is a diff of every single
    /// line — produced by a watcher the user never asked to run, hiding whatever really changed.
    /// </remarks>
    [Fact]
    public async Task WhenDesignerWasReformattedThenRegeneratingItChangesNothing()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: """
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <asp:Label ID="lblHeading" runat="server" />
                    """,
            codeBehind: "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }");

        var existing = new UTF8Encoding(true)
            .GetBytes(ReformattedDesigner("lblHeading"));
        await File.WriteAllBytesAsync(scenario.DesignerPath, existing);

        var result = await scenario.RegenerateThroughServiceAsync(dryRun: false);

        Assert.Equal(DesignerOutcome.Unchanged, result.Outcome);
        Assert.Equal(existing, await File.ReadAllBytesAsync(scenario.DesignerPath));
    }

    /// <summary>
    /// Adding a control to a reformatted designer changes only the lines that control needs — the
    /// rest of the file, byte order mark included, is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task WhenControlAddedToReformattedDesignerThenOnlyTheNewFieldIsWritten()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: """
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <asp:Label ID="lblHeading" runat="server" />
                    <asp:Label ID="lblAdded" runat="server" />
                    """,
            codeBehind: "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }");

        await File.WriteAllBytesAsync(
            scenario.DesignerPath,
            new UTF8Encoding(true).GetBytes(ReformattedDesigner("lblHeading")));

        var result = await scenario.RegenerateThroughServiceAsync(dryRun: false);

        Assert.Equal(DesignerOutcome.Updated, result.Outcome);

        var expected = new UTF8Encoding(true)
            .GetBytes(ReformattedDesigner("lblHeading", "lblAdded"));

        Assert.Equal(expected, await File.ReadAllBytesAsync(scenario.DesignerPath));
    }

    /// <summary>
    /// A control moved in the markup does not drag its field across the designer file with it.
    /// </summary>
    /// <remarks>
    /// Emitting in markup order is what Visual Studio does, but the file on disk stopped matching
    /// markup order long ago — Visual Studio only rewrites it while the page is open in the
    /// designer. Re-sorting turns a one-line change into hundreds of moved lines, so declaration
    /// order on disk wins and new fields land at the end.
    /// </remarks>
    [Fact]
    public async Task WhenControlsReorderedInMarkupThenExistingFieldsKeepTheirPlace()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: """
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <asp:Label ID="lblSecond" runat="server" />
                    <asp:Label ID="lblFirst" runat="server" />
                    <asp:Label ID="lblThird" runat="server" />
                    """,
            codeBehind: "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }");

        await File.WriteAllTextAsync(scenario.DesignerPath, ReformattedDesigner("lblFirst", "lblSecond"));

        var content = await scenario.GenerateAsync();

        var order = new[] { "lblFirst", "lblSecond", "lblThird" }
            .Select(name => content.IndexOf($"Label {name};", StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(-1, order);
        Assert.True(order[0] < order[1], "lblFirst keeps its place ahead of lblSecond");
        Assert.True(order[1] < order[2], "the newly added lblThird is appended at the end");
    }

    /// <summary>
    /// With no file to take conventions from, the generator writes what Visual Studio would have:
    /// CodeDOM's same-line braces, under a UTF-8 byte order mark.
    /// </summary>
    [Fact]
    public async Task WhenDesignerDoesNotExistYetThenItIsWrittenTheWayVisualStudioWouldHave()
    {
        await using var scenario = await MarkupScenario.CreateAsync(
            markup: """
                    <%@ Page Language="C#" Inherits="Fixture.SamplePage" %>
                    <asp:Label ID="lblHeading" runat="server" />
                    """,
            codeBehind: "namespace Fixture { public partial class SamplePage : System.Web.UI.Page { } }");

        var result = await scenario.RegenerateThroughServiceAsync(dryRun: false);

        Assert.Equal(DesignerOutcome.Updated, result.Outcome);

        var written = await File.ReadAllBytesAsync(scenario.DesignerPath);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, written[..3]);

        var text = new UTF8Encoding(false).GetString(written[3..]);
        Assert.Contains("namespace Fixture {", text);
        Assert.Contains("    public partial class SamplePage {", text);
    }

    private sealed class MarkupScenario : IAsyncDisposable
    {
        private static readonly string StubSource =
            File.ReadAllText(Path.Combine(FixturePaths.AspxProjectDir, "SystemWebStubs.cs"));

        private readonly AdhocWorkspace _workspace = new();

        public required string Directory { get; init; }
        public required string MarkupPath { get; init; }
        public Project Project { get; private set; } = null!;

        /// <summary>Extra markup files written beside the main one, for shared-class scenarios.</summary>
        public IReadOnlyList<string> SiblingPaths { get; private init; } = [];

        public string DesignerPath => MarkupPath + ".designer.cs";

        public static Task<MarkupScenario> CreateAsync(
            string markup, string codeBehind, params (string FileName, string Content)[] siblings)
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "roslynsense-designer-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);

            var markupPath = Path.Combine(directory, "SamplePage.aspx");
            File.WriteAllText(markupPath, markup);

            var siblingPaths = new List<string>();
            foreach (var (fileName, content) in siblings)
            {
                var path = Path.Combine(directory, fileName);
                File.WriteAllText(path, content);
                siblingPaths.Add(path);
            }

            var scenario = new MarkupScenario
            {
                Directory = directory,
                MarkupPath = markupPath,
                SiblingPaths = siblingPaths,
            };

            // The C# sources go to disk as well as into the in-memory project, so the tests that run
            // the real service — which loads the project from disk — see the same code the in-memory
            // ones do.
            File.WriteAllText(Path.Combine(directory, "SamplePage.aspx.cs"), codeBehind);
            File.WriteAllText(Path.Combine(directory, "SystemWebStubs.cs"), StubSource);

            scenario.Project = scenario.BuildProject(codeBehind);
            return Task.FromResult(scenario);
        }

        private Project BuildProject(string codeBehind)
        {
            // The stubs shadow System.Web types, so only the core runtime assemblies are needed.
            var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "netstandard.dll" }
                .Select(name => Path.Combine(
                    Path.GetDirectoryName(typeof(object).Assembly.Location)!, name))
                .Where(File.Exists)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

            var projectId = ProjectId.CreateNewId();
            var solution = _workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(
                    projectId, VersionStamp.Create(), "Fixture", "Fixture", LanguageNames.CSharp,
                    filePath: Path.Combine(Directory, "Fixture.csproj"),
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)))
                .AddMetadataReferences(projectId, references);

            solution = AddDocument(solution, projectId, "SystemWebStubs.cs", StubSource);
            solution = AddDocument(solution, projectId, "SamplePage.aspx.cs", codeBehind);

            return solution.GetProject(projectId)!;
        }

        private Solution AddDocument(Solution solution, ProjectId projectId, string name, string text) =>
            solution.AddDocument(
                DocumentId.CreateNewId(projectId), name, text,
                filePath: Path.Combine(Directory, name));

        public async Task<DesignerResult> GenerateResultAsync() =>
            await new AspxDesignerGenerator().GenerateAsync(MarkupPath, Project, default);

        public async Task<string> GenerateAsync()
        {
            var result = await GenerateResultAsync();
            Assert.True(result.Content is not null,
                $"Generation failed: {string.Join("; ", result.Errors)}");
            return result.Content!;
        }

        /// <summary>Runs the full service, which resolves the project from disk rather than in memory.</summary>
        public async Task<DesignerRegeneration> RegenerateThroughServiceAsync(bool dryRun)
        {
            // A minimal SDK project file so the service's project lookup succeeds.
            await File.WriteAllTextAsync(
                Path.Combine(Directory, "Fixture.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);

            var service = new DesignerRegenerationService([new AspxDesignerGenerator()]);
            return await service.RegenerateAsync(MarkupPath, dryRun, default);
        }

        public ValueTask DisposeAsync()
        {
            _workspace.Dispose();
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch { }
            return ValueTask.CompletedTask;
        }
    }
}

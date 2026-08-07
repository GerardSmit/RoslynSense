using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Languages.WebForms.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Closing tags that close nothing.
/// </summary>
/// <remarks>
/// <para>
/// A page's tags are not one stream. A control's templates render in order but are written apart,
/// and opening a tag in a <c>HeaderTemplate</c> to close it in a <c>FooterTemplate</c> is how a
/// repeater wraps its items — markup unbalanced in every template alone and balanced in the page
/// the control renders. So "this tag matches nothing" means something more careful here than it
/// does in HTML.
/// </para>
/// <para>
/// Only unexpected <em>closing</em> tags belong here, never unclosed opening ones: HTML lets
/// <c>&lt;li&gt;</c>, <c>&lt;td&gt;</c> and <c>&lt;p&gt;</c> close themselves and real pages are
/// written that way, so demanding every tag be closed would report working pages by the hundred.
/// </para>
/// </remarks>
public class AspxTagBalanceTests
{
    private const string UnexpectedClosingTag = "WFC0006";
    private const string ClosingTagWithNothingOpen = "WFC0011";

    private static bool ClosesNothing(string id) =>
        id == UnexpectedClosingTag || id == ClosingTagWithNothingOpen;

    private static Compilation Compilation()
    {
        string stubs = File.ReadAllText(Path.Combine(FixturePaths.AspxProjectDir, "SystemWebStubs.cs"));
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        return CSharpCompilation.Create("TagBalance",
            [CSharpSyntaxTree.ParseText(stubs)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>The ids of the unexpected-closing-tag diagnostics the real parse reports.</summary>
    private static string[] Report(string markup) =>
        AspxSourceMappingService.Parse(@"C:\site\Controls\Balance.ascx", markup, Compilation())
            .RawDiagnostics
            .Where(d => ClosesNothing(d.Descriptor.Id))
            .Select(d => d.Descriptor.Id)
            .ToArray();

    private static string[] Messages(string markup) =>
        AspxSourceMappingService.Parse(@"C:\site\Controls\Balance.ascx", markup, Compilation())
            .RawDiagnostics
            .Where(d => ClosesNothing(d.Descriptor.Id))
            .Select(d => ((Diagnostic)d).GetMessage())
            .ToArray();

    [Fact]
    public void AClosingTagThatMatchesNothingIsReported()
    {
        Assert.Single(Report("""
            <div>
            </incorrect>
            """));
    }

    [Fact]
    public void BalancedMarkupIsQuiet()
    {
        Assert.Empty(Report("""
            <div class="a"><span>text</span></div>
            <asp:Label ID="lbl" runat="server" />
            """));
    }

    [Fact]
    public void ATagOpenedInAHeaderTemplateMayCloseInTheFooter()
    {
        // The idiom this whole file exists for.
        Assert.Empty(Report("""
            <asp:Repeater runat="server">
                <HeaderTemplate>
                    <div>
                </HeaderTemplate>
                <ItemTemplate>
                    <span>item</span>
                </ItemTemplate>
                <FooterTemplate>
                    </div>
                </FooterTemplate>
            </asp:Repeater>
            """));
    }

    [Fact]
    public void AFooterClosingATagTheHeaderNeverOpenedIsReported()
    {
        Assert.Single(Report("""
            <asp:Repeater runat="server">
                <HeaderTemplate>
                    <div>
                </HeaderTemplate>
                <FooterTemplate>
                    </incorrect>
                </FooterTemplate>
            </asp:Repeater>
            """));
    }

    [Fact]
    public void HtmlThatClosesItsOwnTagsIsNotReported()
    {
        Assert.Empty(Report("""
            <ul>
                <li>one
                <li>two
            </ul>
            <table><tr><td>a<td>b</tr></table>
            """));
    }

    [Fact]
    public void VoidElementsAreNeverOnTheStack()
    {
        Assert.Empty(Report("""
            <div><br><img src="a.png"><hr>
            <input type="text" />
            </div>
            """));
    }

    [Fact]
    public void ServerCodeAndCommentsHoldNoTags()
    {
        // A `</div>` written inside a code block, an expression or a server comment is text, and
        // reading it as markup would report the page for something it does not contain.
        Assert.Empty(Report("""
            <div>
                <%-- </div> --%>
                <% var html = "</div>"; %>
                <%= "</span>" %>
                <!-- </div> -->
            </div>
            """));
    }

    [Fact]
    public void AClosingTagInsideAnAttributeValueIsAttributeText()
    {
        // The lexer leaves plain tags as text, so it re-enters at the '<' inside the
        // onclick value as if a tag started there. That is attribute text, not markup.
        Assert.Empty(Report("""
            <div onclick="x('</div>')"></div>
            """));
    }

    [Fact]
    public void AFragmentMayCloseATagAnotherFileOpened()
    {
        // A Footer.ascx that closes the wrapper a Header.ascx opened: nothing is open in
        // this file, and that is exactly when a stray close can be deliberate.
        Assert.Empty(Report("""
            </div>
            </div>
            """));
    }

    [Fact]
    public void ClosingTagCaseMayDifferFromItsOpeningTag()
    {
        Assert.Empty(Report("""
            <div></DIV>
            """));
    }

    [Fact]
    public void TheMessageNamesThePrefixedControlInFull()
    {
        // The lexer reads "asp" and "PlaceHolder" as two tokens; the message must not
        // stop at the prefix.
        var diagnostic = AspxSourceMappingService.Parse(@"C:\site\Controls\Balance.ascx", """
            <asp:PlaceHolder runat="server">
            </div>
            """, Compilation())
            .RawDiagnostics
            .Single(d => d.Descriptor.Id == UnexpectedClosingTag);

        Assert.Contains("asp:PlaceHolder", ((Diagnostic)diagnostic).GetMessage());
    }

    [Fact]
    public void ACorrectlyClosedPrefixedControlIsQuiet()
    {
        // Guards the full-name fix: if only the opening side stored "asp:PlaceHolder",
        // every matching </asp:PlaceHolder> would stop closing its tag.
        Assert.Empty(Report("""
            <asp:PlaceHolder runat="server"><span>x</span></asp:PlaceHolder>
            """));
    }

    [Fact]
    public void ScriptBodiesAreNotMarkup()
    {
        Assert.Empty(Report("""
            <div>
                <script type="text/javascript">
                    if (a < b && b > c) { document.write("</div>"); }
                </script>
            </div>
            """));
    }

    [Fact]
    public void AClosingTagAfterEverythingIsAlreadyClosedIsReported()
    {
        // The third tag closes nothing: the first <p> was already closed by the second.
        Assert.Single(Report("""
            <p></p></p>
            """));
    }

    [Fact]
    public void TheReportSaysNothingIsOpenRatherThanNamingATag()
    {
        var message = Messages("""
            <p></p></p>
            """).Single();

        Assert.Contains("nothing is open", message, StringComparison.Ordinal);
    }
}

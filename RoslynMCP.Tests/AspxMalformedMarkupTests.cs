using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Languages.WebForms.Core;
using WebFormsCore.Models;
using WebFormsCore.Nodes;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Markup that is wrong, or merely unusual, must still parse.
/// </summary>
/// <remarks>
/// <para>
/// Every markup feature asks for the parse first — hover, folding, document symbols, semantic
/// tokens, document links, code actions, code lens and diagnostics — and the code-behind's C# code
/// lens asks this pack for markup references. So a parser that throws does not lose one feature on
/// one file; it loses every feature on two files, on every keystroke.
/// </para>
/// <para>
/// These are the shapes that did exactly that, each found only when somebody opened the one page
/// that contained it.
/// </para>
/// </remarks>
public class AspxMalformedMarkupTests
{
    private static Compilation Compilation() =>
        CSharpCompilation.Create("TestAssembly",
            [CSharpSyntaxTree.ParseText("class Dummy {}")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    /// <summary>A compilation that can resolve the <c>asp:</c> prefix, for the cases whose point
    /// is which control came out of the markup rather than that the parse survived it.</summary>
    private static Compilation WebCompilation()
    {
        string stubs = File.ReadAllText(Path.Combine(FixturePaths.AspxProjectDir, "SystemWebStubs.cs"));
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        return CSharpCompilation.Create("MalformedMarkup",
            [CSharpSyntaxTree.ParseText(stubs)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>
    /// A tag writing the same attribute twice is reported, not thrown.
    /// </summary>
    /// <remarks>
    /// It is a mistake that exists in real markup — a merge that duplicated <c>runat="server"</c>,
    /// a copied tag with a leftover attribute — and ASP.NET renders such a page. It used to throw
    /// "An item with the same key has already been added. Key: runat" out of the middle of parsing.
    /// </remarks>
    [Fact]
    public void ATagWithTheSameAttributeTwiceStillParses()
    {
        const string markup = """
            <%@ Control Language="C#" %>
            <div runat="server" id="wrapper" runat="server">
              <span>text</span>
            </div>
            """;

        var result = AspxSourceMappingService.Parse(@"C:\site\Controls\Duplicate.ascx", markup, Compilation());

        Assert.NotNull(result);
        Assert.NotEmpty(result.Directives);

        // Reported rather than swallowed, so the author can see what is wrong with the tag.
        Assert.Contains(result.RawDiagnostics, d => d.Descriptor.Id == "WFC0009");
    }

    /// <summary>
    /// A directive repeating an attribute is the same case one level up.
    /// </summary>
    [Fact]
    public void ADirectiveWithTheSameAttributeTwiceStillParses()
    {
        const string markup = """
            <%@ Control Language="C#" Language="C#" %>
            <div>text</div>
            """;

        var result = AspxSourceMappingService.Parse(@"C:\site\Controls\DuplicateDirective.ascx", markup, Compilation());

        Assert.NotNull(result);
        Assert.NotEmpty(result.Directives);
    }

    /// <summary>
    /// A file whose last character is a backslash, with no trailing newline.
    /// </summary>
    /// <remarks>
    /// The lexer's escape handling advanced twice against one bounds check, so the offset ended one
    /// past the buffer and the token cut from it sliced out of range — an exception from the middle
    /// of parsing, on a file that is not malformed in any way. A trailing newline hid it, since the
    /// newline became the escaped character and the offset landed exactly on the end, which is why
    /// it survived every fixture.
    /// </remarks>
    [Theory]
    [InlineData(@"<p>x\")]
    [InlineData(@"\")]
    [InlineData("<div>C:\\path\\\\")]
    public void AFileEndingInABackslashStillParses(string markup)
    {
        var result = AspxSourceMappingService.Parse(@"C:\site\Controls\Trailing.ascx", markup, Compilation());

        Assert.NotNull(result);
        Assert.Equal(@"C:\site\Controls\Trailing.ascx", result.FilePath);

        // Parse catches anything the parser throws, so "it returned a result" proves nothing on its
        // own — WFC0001 is the marker that catch leaves behind, and its absence is what says the
        // lexer actually reached the end of the file.
        Assert.DoesNotContain(result.RawDiagnostics, d => d.Descriptor.Id == "WFC0001");
    }

    /// <summary>
    /// A server control written inside another tag's attribute list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not malformed at all — ASP.NET renders it, and it is how a page whose <c>&lt;html&gt;</c>
    /// attributes are decided in code-behind writes them: the literal replaces the attribute list
    /// rather than sitting anywhere in the content.
    /// </para>
    /// <para>
    /// The tag scan looked for <c>runat</c> as far as the next '&gt;', which for a host tag is
    /// the nested tag's, so <c>&lt;html&gt;</c> inherited the literal's <c>runat</c> and became a
    /// server control. The nested closing tag then had no runat of its own, fell through to the
    /// attribute reader, and left "asp:literal&gt;&gt;" behind as text with an attribute named
    /// "&lt;" — which reported a property that does not exist, swallowed the rest of the document
    /// into the literal and put <c>&lt;/html&gt;</c> out of reach of the tag that opened it. One
    /// page's outline, folding, selection ranges and tag balance were wrong from that line down.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("""
        <html <asp:Literal id="attributeList" runat="server"></asp:Literal>>
        <body><asp:Label ID="lbl" runat="server" /></body>
        </html>
        """)]
    [InlineData("""
        <html <asp:Literal id="attributeList" runat="server" />>
        <body><asp:Label ID="lbl" runat="server" /></body>
        </html>
        """)]
    public void AServerControlInsideAnotherTagsAttributeListParses(string markup)
    {
        var result = AspxSourceMappingService.Parse(@"C:\site\Attributes.aspx", markup, WebCompilation());

        // The control is the page's, with its own id and type, wherever in the tag it was written.
        Assert.Contains(result.Controls, c => c is { Id: "attributeList", TagName: "Literal" });

        // And it stays one control among several rather than becoming the parent of the rest of
        // the file: the label that follows is still found.
        Assert.Contains(result.Controls, c => c is { Id: "lbl", TagName: "Label" });

        // "<" is not an attribute, so nothing looks for a property by that name (WFC0002), and
        // </html> still closes the tag that opened it (WFC0006, WFC0011).
        Assert.Empty(result.RawDiagnostics);
    }

    /// <summary>
    /// The host tag's own <c>runat</c> still counts when it is written before the nested one.
    /// </summary>
    [Fact]
    public void ATagWithBothItsOwnRunatAndANestedControlIsStillAServerControl()
    {
        const string markup = """
            <body runat="server" <asp:Literal id="bodyAttrs" runat="server" />>text</body>
            """;

        var result = AspxSourceMappingService.Parse(@"C:\site\Both.aspx", markup, WebCompilation());

        Assert.Contains(result.Controls, c => c is { Id: "bodyAttrs", TagName: "Literal" });
        Assert.Contains(result.Controls, c => c.TypeName.EndsWith("HtmlGenericControl", StringComparison.Ordinal));
        Assert.Empty(result.RawDiagnostics);
    }

    /// <summary>
    /// A quoted attribute value is text, whatever markup it happens to spell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounding the <c>runat</c> search at a nested tag only works if quoted values are stepped
    /// over: '&lt;', '&gt;' and the word "runat" are all ordinary characters inside one, and the
    /// page writing them was not asking for a tag. ASP.NET reads no control in an attribute value
    /// either — only <c>&lt;% %&gt;</c> interrupts one.
    /// </para>
    /// <para>
    /// The case that named this test is the first: <c>onload="if (a&lt;b) f()"</c> re-entered at
    /// the '&lt;', found the tag's own <c>runat</c> ahead of it, and read <c>b) f()" runat="server"</c>
    /// as a server control named "b" — with the body it was written on nested inside it, and
    /// everything after that misparented in turn.
    /// </para>
    /// </remarks>
    [Theory]
    // A '<' inside a quoted value is not a tag, so runat after it is still this tag's own.
    [InlineData("""<body onload="if (a<b) f()" runat="server">t</body>""", "body")]
    // A '>' inside one does not end the tag.
    [InlineData("""<body title="a>b" runat="server">t</body>""", "body")]
    // "runat" written inside one is text, not a declaration, so nothing here is a control.
    [InlineData("""<body title="runat=server">t</body>""", null)]
    // And '<%' stays an inline block rather than becoming a tag of its own.
    [InlineData("""<asp:Label Text='<%# Eval("X") %>' runat="server" ID="bound" />""", "Label")]
    public void AQuotedAttributeValueIsReadAsText(string markup, string? controlTag)
    {
        var result = AspxSourceMappingService.Parse(@"C:\site\Quoted.aspx", markup, WebCompilation());

        // The tag name matters as much as the count: the misread produced exactly one control
        // too, just the wrong one, cut out of the middle of an attribute value.
        Assert.Equal(controlTag is null ? [] : new[] { controlTag },
            result.Controls.Select(c => c.TagName).ToArray());

        // Not Assert.Empty: a `runat="server"` HTML tag reports every attribute the stub type has
        // no property for, which says nothing about where the tag was read to end.
        Assert.Empty(MisreadTag(result));
    }

    /// <summary>
    /// The value itself survives intact, and a data binding written in one stays a binding.
    /// </summary>
    /// <remarks>
    /// Where the tag was read to end is only half of it — the other half is what the attribute is
    /// worth afterwards. Reading <c>&lt;%</c> as a tag left <c>Text</c> holding the literal string
    /// "%# Eval("X") %&gt;", which compiles to a label that renders its own markup.
    /// </remarks>
    [Fact]
    public void AValueWithMarkupInItKeepsItsText()
    {
        const string markup = """
            <asp:Label Text='<%# Eval("X") %>' runat="server" ID="bound" />
            <asp:Panel CssClass="if (a<b) f()" runat="server" ID="literal" />
            <asp:Label Text="<%$ Resources: Strings, Hello %>" runat="server" ID="built" />
            """;

        var result = AspxSourceMappingService.Parse(@"C:\site\Value.aspx", markup, WebCompilation());
        var controls = FindControls(result.ParseTree);

        Assert.Empty(MisreadTag(result));

        var text = Assert.Single(
            Assert.Single(controls, c => c.Id == "bound").Properties, p => p.Member.Name == "Text");
        Assert.Equal(AttributeValueKind.Code, text.Value.Kind);
        Assert.Equal(@" Eval(""X"") ", text.Value.Value);

        var cssClass = Assert.Single(
            Assert.Single(controls, c => c.Id == "literal").Properties, p => p.Member.Name == "CssClass");
        Assert.Equal(AttributeValueKind.Literal, cssClass.Value.Kind);
        Assert.Equal("if (a<b) f()", cssClass.Value.Value);

        // The value reader hands `<%` straight to the inline reader now, so the builder form —
        // which is the same entry point — is checked with it rather than assumed.
        var built = Assert.Single(
            Assert.Single(controls, c => c.Id == "built").Properties, p => p.Member.Name == "Text");
        Assert.Equal(AttributeValueKind.ExpressionBuilder, built.Value.Kind);
        Assert.Equal("Resources", built.Value.Prefix.Value);
        Assert.Equal("Strings, Hello", built.Value.Value);
    }

    private static List<ControlNode> FindControls(RootNode? root) =>
        root is null ? [] : root.AllChildren.OfType<ControlNode>().ToList();

    /// <summary>
    /// The diagnostics that only appear when a tag was read as ending somewhere other than where
    /// it does — an attribute cut out of the markup around it, or a closing tag left with nothing
    /// to close.
    /// </summary>
    private static string[] MisreadTag(AspxParseResult result) =>
        result.RawDiagnostics
            .Where(d => d.Descriptor.Id is "WFC0006" or "WFC0011"
                        || d.Descriptor.Id == "WFC0002" && ((Diagnostic)d).GetMessage().Contains("'<'"))
            .Select(d => ((Diagnostic)d).GetMessage())
            .ToArray();

    /// <summary>
    /// Nothing the parser does to itself escapes into the request that asked for the parse.
    /// </summary>
    /// <remarks>
    /// The backstop for the next parser bug rather than for any particular one. Three separate
    /// crashes surfaced this way in a single afternoon; each is fixed at its source, and this is
    /// what makes the fourth cost a file's markup features rather than the file. A failed parse
    /// still says so, as a diagnostic, so it is visible rather than quietly empty.
    /// </remarks>
    [Fact]
    public void AParseThatCannotCompleteReturnsAResultRatherThanThrowing()
    {
        // Deliberately hostile: unterminated tags, stray quotes, an unclosed directive and a
        // control prefix that resolves to nothing.
        const string markup = """
            <%@ Register TagPrefix="uc" TagName="Thing" Src="~/does/not/exist.ascx" %>
            <%@ Control Language="C#"
            <uc:Thing runat="server" class="a" style='b" data-x=<%# Eval("y") %>>
            <div><span></div>
            <asp:Label runat="server" Text="<%$ Resources: Missing, Key %>" />
            """;

        var result = AspxSourceMappingService.Parse(@"C:\site\Controls\Hostile.ascx", markup, Compilation());

        Assert.NotNull(result);
        Assert.Equal(@"C:\site\Controls\Hostile.ascx", result.FilePath);
    }
}

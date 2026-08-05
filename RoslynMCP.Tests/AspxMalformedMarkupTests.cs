using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Languages.WebForms.Core;
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

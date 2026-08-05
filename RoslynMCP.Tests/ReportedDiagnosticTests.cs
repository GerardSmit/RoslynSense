using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using WebFormsCore.Models;
using WebFormsCore.SourceGenerator.Models;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// A parser diagnostic that knows no file must still convert to a Roslyn diagnostic.
/// </summary>
/// <remarks>
/// <para>
/// <c>Location.Create</c> names its first parameter <c>filePath</c> and rejects null, and
/// <c>FileLinePositionSpan.Path</c> is null whenever the reported location had no syntax tree
/// behind it — which is an ordinary thing for a markup parser to produce, not an error.
/// </para>
/// <para>
/// The blast radius is why this is pinned. The conversion runs while markup is being parsed, so
/// the exception surfaced out of <c>Parse</c> rather than out of whatever reported the diagnostic,
/// and it took down every feature for that file at once — hover, folding, document symbols,
/// semantic tokens, code lens and diagnostics each ask for the parse first. Reported from the
/// field as "Value cannot be null. (Parameter 'filePath')" on one <c>.ascx</c>, triggered by a
/// control registered with a <c>src=</c> that <c>File.Exists</c> could not confirm — which is what
/// a symlinked web root does.
/// </para>
/// </remarks>
public class ReportedDiagnosticTests
{
    private static readonly DiagnosticDescriptor Descriptor = new(
        "TEST001", "Title", "Message {0}", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    [Fact]
    public void ADiagnosticWithNoFileConvertsToOneWithNoLocation()
    {
        var reported = ReportedDiagnostic.Create(Descriptor, Location.None, "argument");

        // The precondition this test is about: no syntax tree behind it means no path.
        Assert.True(string.IsNullOrEmpty(reported.FileLineSpan.Path));

        Diagnostic converted = reported;

        Assert.Equal("TEST001", converted.Id);
        Assert.Equal(Location.None, converted.Location);
        Assert.Contains("argument", converted.GetMessage());
    }

    /// <summary>
    /// A range belonging to no file converts to no location rather than throwing.
    /// </summary>
    /// <remarks>
    /// <c>TokenString</c> converts implicitly from <c>string</c> and gives the result
    /// <c>default</c> for its range, so every name the parser synthesizes rather than reads out of
    /// the markup carries a range whose <c>File</c> is null. Reporting a diagnostic against one —
    /// <c>PropertyNotFound</c> on an attribute, say — is perfectly reasonable, and used to throw
    /// out of the middle of parsing.
    /// </remarks>
    [Fact]
    public void ARangeWithNoFileConvertsToNoLocation()
    {
        TokenString synthesized = "SomeAttributeName";

        Assert.True(string.IsNullOrEmpty(synthesized.Range.File));

        Location location = synthesized.Range;

        Assert.Equal(Location.None, location);
    }

    [Fact]
    public void ARangeWithAFileKeepsIt()
    {
        var range = new TokenRange(
            @"C:\site\Controls\Widget.ascx",
            new TokenPosition(Offset: 20, Line: 2, Column: 4),
            new TokenPosition(Offset: 25, Line: 2, Column: 9));

        Location location = range;

        Assert.Equal(@"C:\site\Controls\Widget.ascx", location.GetLineSpan().Path);
        Assert.Equal(new TextSpan(20, 5), location.SourceSpan);
    }

    [Fact]
    public void ADiagnosticWithAFileKeepsItsLocation()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { }", path: @"C:\site\Controls\Widget.ascx");
        var span = new TextSpan(6, 1);

        var reported = ReportedDiagnostic.Create(Descriptor, Location.Create(tree, span), "argument");

        Diagnostic converted = reported;

        Assert.Equal(@"C:\site\Controls\Widget.ascx", converted.Location.GetLineSpan().Path);
        Assert.Equal(span, converted.Location.SourceSpan);
    }
}

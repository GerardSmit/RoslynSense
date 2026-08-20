using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using Xunit;

// Both namespaces above define a Diagnostic. This file is about the wire shape, so that is the one
// the bare name means here; Roslyn's is spelled out where the compilation hands it over.
using Diagnostic = RoslynMCP.Lsp.Protocol.Diagnostic;

namespace RoslynMCP.Tests;

/// <summary>
/// LSP diagnostic tags: what makes an unused <c>using</c> fade instead of doing nothing, and an
/// obsolete call strike through.
/// </summary>
/// <remarks>
/// The tags were declared on <see cref="Protocol.Diagnostic"/> and set by exactly one language pack, so
/// C# — the reason the field exists — emitted none of them. Worth pinning at the converter rather
/// than through a fixture project, because the interesting cases are a Hidden diagnostic that must
/// now survive the severity filter and a Hidden one that must still be dropped.
/// </remarks>
public class DiagnosticTagTests
{
    [Fact]
    public void AnUnusedUsingIsHintSeverityAndTaggedUnnecessary()
    {
        var diagnostics = Compile(
            """
            using System;
            using System.Text;

            class C
            {
                void M() => Console.WriteLine("hi");
            }
            """);

        // CS8019 is Hidden: it draws no squiggle and never enters the Problems panel. The tag is
        // the whole payload — it is what greys the directive out.
        var unused = Assert.Single(diagnostics, d => d.Code == "CS8019");
        Assert.Equal(4, unused.Severity);
        Assert.Equal([LspDiagnosticTag.Unnecessary], unused.Tags!);
    }

    [Fact]
    public void AnObsoleteCallIsTaggedDeprecated()
    {
        var diagnostics = Compile(
            """
            using System;

            class C
            {
                [Obsolete("gone")]
                void Old() { }

                void M() => Old();
            }
            """);

        var obsolete = Assert.Single(diagnostics, d => d.Code == "CS0618");
        Assert.Equal([LspDiagnosticTag.Deprecated], obsolete.Tags!);
    }

    /// <summary>
    /// A warning fades too. Unreachable code is reported at Warning severity, so it keeps its
    /// squiggle and its place in the Problems panel and is greyed out on top — which is what
    /// Visual Studio does with it.
    /// </summary>
    [Fact]
    public void UnreachableCodeIsAWarningAndStillTaggedUnnecessary()
    {
        var diagnostics = Compile(
            """
            class C
            {
                int M()
                {
                    return 1;
                    return 2;
                }
            }
            """);

        var unreachable = Assert.Single(diagnostics, d => d.Code == "CS0162");
        Assert.Equal(2, unreachable.Severity);
        Assert.Equal([LspDiagnosticTag.Unnecessary], unreachable.Tags!);
    }

    /// <summary>
    /// A diagnostic that points at a mistake rather than at removable text carries nothing, and the
    /// severity filter still drops what it always dropped: only a tag buys a Hidden diagnostic its
    /// way onto the wire, or every IDE-only suggestion in a file would be reported with nothing to
    /// draw.
    /// </summary>
    [Fact]
    public void ADiagnosticWithNothingToGreyCarriesNoTags()
    {
        var diagnostics = Compile(
            """
            class C
            {
                void M()
                {
                    int x = "not an int";
                }
            }
            """);

        // CS0029 points at a mistake to correct, not at text to delete. Greying it out would tell
        // the user the line is redundant, which is the opposite of what it says.
        var badAssignment = Assert.Single(diagnostics, d => d.Code == "CS0029");
        Assert.Equal(1, badAssignment.Severity);
        Assert.Null(badAssignment.Tags);

        Assert.DoesNotContain(diagnostics, d => d.Severity == 4 && d.Tags is null);
    }

    private static Diagnostic[] Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "TagFixture",
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return DiagnosticsHandler.ToProtocol(
            compilation.GetSemanticModel(tree).GetDiagnostics());
    }

    private static ImmutableArray<MetadataReference> References =>
        [.. AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))];
}

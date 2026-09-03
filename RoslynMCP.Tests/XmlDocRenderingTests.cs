using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The prose half of a tooltip. A <c>&lt;see cref="..."/&gt;</c> carries a documentation comment
/// id — <c>T:System.Collections.Generic.Dictionary`2</c> — and printing it as it stands put a bare
/// <c>T:</c> and a stray backtick in front of the reader, with the arity marker opening a code
/// span that swallowed the rest of the sentence.
/// </summary>
public class XmlDocRenderingTests
{
    private static Compilation Compilation()
    {
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        return CSharpCompilation.Create("XmlDocs",
            [],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    [Fact]
    public void CrefBecomesTheTypeAsItIsWritten()
    {
        string xml = """
            <summary>Creates a <see cref="T:System.Collections.Generic.Dictionary`2"/> from an
            <see cref="T:System.Collections.Generic.IEnumerable`1"/>.</summary>
            """;

        string? summary = SymbolFormatter.ExtractXmlDocSection(xml, "summary", Compilation());

        Assert.Equal(
            "Creates a `Dictionary<TKey, TValue>` from an `IEnumerable<T>`.",
            summary);
    }

    /// <summary>A member cref keeps the type it is declared on — the name alone would not say
    /// which <c>Add</c> it means.</summary>
    [Fact]
    public void MemberCrefKeepsItsContainingType()
    {
        string xml = """
            <summary>See <see cref="M:System.String.Concat(System.String,System.String)"/>.</summary>
            """;

        string? summary = SymbolFormatter.ExtractXmlDocSection(xml, "summary", Compilation());

        Assert.Equal("See `string.Concat(string, string)`.", summary);
    }

    /// <summary>Without a compilation the type parameter names are gone, but the metadata
    /// spelling still must not reach the tooltip.</summary>
    [Fact]
    public void UnresolvedCrefIsStillWrittenAsAName()
    {
        string xml = """
            <summary>Wraps <see cref="T:Some.Missing.Cache`1"/> and
            <see cref="P:Some.Missing.Cache`1.Count"/>.</summary>
            """;

        string? summary = SymbolFormatter.ExtractXmlDocSection(xml, "summary", compilation: null);

        Assert.Equal("Wraps `Cache` and `Cache.Count`.", summary);
    }

    [Fact]
    public void ParamrefAndLangwordReadAsCode()
    {
        string xml = """
            <summary>Returns <see langword="null"/> when <paramref name="keySelector"/> is
            <c>default</c>.</summary>
            """;

        string? summary = SymbolFormatter.ExtractXmlDocSection(xml, "summary", Compilation());

        Assert.Equal("Returns `null` when `keySelector` is `default`.", summary);
    }

    /// <summary>The text between the tags is the author's own wording for the target, and the
    /// name we would derive is not an improvement on it.</summary>
    [Fact]
    public void CrefWithItsOwnTextKeepsTheText()
    {
        string xml = """
            <summary>Backed by <see cref="T:System.Collections.Generic.Dictionary`2">a hash
            table</see>.</summary>
            """;

        string? summary = SymbolFormatter.ExtractXmlDocSection(xml, "summary", Compilation());

        Assert.Equal("Backed by a hash table.", summary);
    }

    [Theory]
    [InlineData(
        """<see href="https://example.org/spec">the spec</see>""",
        "See [the spec](https://example.org/spec).")]
    [InlineData(
        """<see href="https://example.org/spec"/>""",
        "See https://example.org/spec.")]
    public void HrefBecomesALink(string reference, string expected)
    {
        string? summary = SymbolFormatter.ExtractXmlDocSection(
            $"<summary>See {reference}.</summary>", "summary", Compilation());

        Assert.Equal(expected, summary);
    }

    /// <summary><c>seealso</c> is the same reference in a different tag, and inside a summary it
    /// has to read the same way.</summary>
    [Fact]
    public void SeeAlsoRendersLikeSee()
    {
        string xml = """
            <summary>Compare <seealso cref="T:System.Collections.Generic.List`1"/>.</summary>
            """;

        string? summary = SymbolFormatter.ExtractXmlDocSection(xml, "summary", Compilation());

        Assert.Equal("Compare `List<T>`.", summary);
    }

    /// <summary>Doc comments the compiler never validated do exist; a broken one loses its tags
    /// rather than the whole tooltip.</summary>
    [Fact]
    public void MalformedDocFallsBackToTextualCleanup()
    {
        string xml = "<summary>A <see cref=\"T:System.String\"/> and an <unclosed> tag.</summary>";

        string? summary = SymbolFormatter.ExtractXmlDocSection(xml, "summary", Compilation());

        Assert.Equal("A `string` and an tag.", summary);
    }

    [Fact]
    public void ParameterDocsGetTheSameTreatment()
    {
        string xml = """
            <member><param name="source">The <see cref="T:System.Collections.Generic.List`1"/> to copy.</param></member>
            """;

        var docs = SymbolFormatter.ExtractXmlDocParams(xml, Compilation());

        var (name, description) = Assert.Single(docs);
        Assert.Equal("source", name);
        Assert.Equal("The `List<T>` to copy.", description);
    }
}

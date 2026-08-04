using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMCP.Languages;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The embedded-language seam: a string literal that holds another language, found through
/// Roslyn's own detector rather than through a scan of our own.
/// </summary>
/// <remarks>
/// These assert the adapter, not Roslyn — that <c>[StringSyntax]</c> and <c>// lang=</c> both
/// arrive as an <see cref="EmbeddedStringContext"/> pointing at the right token and the right
/// language, that an identifier nobody registered is declined, and that a caret outside a literal
/// costs nothing. Roslyn tests the detection itself, and far more thoroughly than we could.
/// </remarks>
public class EmbeddedLanguageTests
{
    [Fact]
    public async Task AStringSyntaxParameterMarksTheLiteralPassedToIt()
    {
        var context = await DetectAsync(
            """
            using System.Diagnostics.CodeAnalysis;

            class C
            {
                static void Match([StringSyntax("Regex")] string pattern) { }

                static void Use() => Match("^[a-z]+$");
            }
            """,
            at: "^[a-z]+$",
            identifiers: ["Regex"]);

        Assert.NotNull(context);
        Assert.Equal("Regex", context!.Value.Identifier);
        Assert.Equal("^[a-z]+$", context.Value.Token.ValueText);
        // Only a comment carries options; an attribute names the language and nothing else.
        Assert.Empty(context.Value.Options);
    }

    [Fact]
    public async Task ALanguageCommentMarksTheLiteralBelowIt()
    {
        var context = await DetectAsync(
            """
            class C
            {
                static void Use()
                {
                    // lang=json
                    var payload = "[1, 2]";
                }
            }
            """,
            at: "[1, 2]",
            identifiers: ["Json"]);

        Assert.NotNull(context);
        // The comment decides the spelling, so matching it is case-insensitive.
        Assert.Equal("json", context!.Value.Identifier, ignoreCase: true);
        Assert.Equal("[1, 2]", context.Value.Token.ValueText);
    }

    [Fact]
    public async Task ALanguageCommentCarriesItsOptions()
    {
        var context = await DetectAsync(
            """
            class C
            {
                static void Use()
                {
                    // lang=json,strict
                    var payload = "[1, 2]";
                }
            }
            """,
            at: "[1, 2]",
            identifiers: ["Json"]);

        Assert.NotNull(context);
        Assert.Equal("strict", Assert.Single(context!.Value.Options));
    }

    [Fact]
    public async Task TheContextNamesTheLanguageThatDeclaredTheIdentifier()
    {
        var json = new TestLanguage(["Json"]);
        var regex = new TestLanguage(["Regex"]);

        var context = await DetectAsync(
            """
            using System.Diagnostics.CodeAnalysis;

            class C
            {
                static void Match([StringSyntax("Regex")] string pattern) { }

                static void Use() => Match("^[a-z]+$");
            }
            """,
            at: "^[a-z]+$",
            languages: new RoslynEmbeddedLanguages([json, regex]));

        Assert.Same(regex, context!.Value.Language);
    }

    [Fact]
    public async Task AnIdentifierNoLanguageRegisteredIsNotAnEmbeddedLiteral()
    {
        // The attribute still says Regex; nothing here can do anything with one.
        var context = await DetectAsync(
            """
            using System.Diagnostics.CodeAnalysis;

            class C
            {
                static void Match([StringSyntax("Regex")] string pattern) { }

                static void Use() => Match("^[a-z]+$");
            }
            """,
            at: "^[a-z]+$",
            identifiers: ["Json"]);

        Assert.Null(context);
    }

    [Fact]
    public async Task APlainLiteralIsNotAnEmbeddedLiteral()
    {
        var context = await DetectAsync(
            """
            class C
            {
                static void Use()
                {
                    var greeting = "^[a-z]+$";
                }
            }
            """,
            at: "^[a-z]+$",
            identifiers: ["Regex", "Json"]);

        Assert.Null(context);
    }

    [Fact]
    public async Task ACaretOutsideALiteralIsNotAnEmbeddedLiteral()
    {
        var context = await DetectAsync(
            """
            using System.Diagnostics.CodeAnalysis;

            class C
            {
                static void Match([StringSyntax("Regex")] string pattern) { }

                static void Use() => Match("^[a-z]+$");
            }
            """,
            at: "Use()",
            identifiers: ["Regex"]);

        Assert.Null(context);
    }

    [Fact]
    public async Task WithNoLanguageRegisteredNothingIsDetected()
    {
        var context = await DetectAsync(
            """
            using System.Diagnostics.CodeAnalysis;

            class C
            {
                static void Match([StringSyntax("Regex")] string pattern) { }

                static void Use() => Match("^[a-z]+$");
            }
            """,
            at: "^[a-z]+$",
            languages: new RoslynEmbeddedLanguages([]));

        Assert.Null(context);
    }

    [Fact]
    public async Task TheDocumentWidePassFindsEveryEmbeddedLiteral()
    {
        var document = CreateDocument(
            """
            using System.Diagnostics.CodeAnalysis;

            class C
            {
                static void Match([StringSyntax("Regex")] string pattern) { }

                static void Use()
                {
                    Match("^one$");
                    Match("^two$");
                    var unrelated = "^three$";
                }
            }
            """);

        var found = await new RoslynEmbeddedLanguages([new TestLanguage(["Regex"])])
            .DetectAllAsync(document, default);

        Assert.Equal(new[] { "^one$", "^two$" }, found.Select(c => c.Token.ValueText));
    }

    // ---- Helpers -------------------------------------------------------------------------

    private static Task<EmbeddedStringContext?> DetectAsync(
        string source, string at, ImmutableArray<string> identifiers) =>
        DetectAsync(source, at, new RoslynEmbeddedLanguages([new TestLanguage(identifiers)]));

    private static async Task<EmbeddedStringContext?> DetectAsync(
        string source, string at, RoslynEmbeddedLanguages languages)
    {
        int position = source.IndexOf(at, StringComparison.Ordinal);
        Assert.True(position >= 0, $"'{at}' does not occur in the source.");

        return await languages.DetectAsync(CreateDocument(source), position, default);
    }

    /// <summary>
    /// One C# document compiled against the runtime, which is where
    /// <c>StringSyntaxAttribute</c> comes from — the detector matches it by fully-qualified name,
    /// so a stub declared in the test source would not do.
    /// </summary>
    private static Document CreateDocument(string source)
    {
        var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "netstandard.dll" }
            .Select(name => Path.Combine(
                Path.GetDirectoryName(typeof(object).Assembly.Location)!, name))
            .Where(File.Exists)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Create(), "Embedded", "Embedded", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)))
            .AddMetadataReferences(projectId, references)
            .AddDocument(documentId, "Embedded.cs", source);

        return solution.GetDocument(documentId)!;
    }

    private sealed class TestLanguage(ImmutableArray<string> identifiers) : IEmbeddedStringLanguage
    {
        public ImmutableArray<string> StringSyntaxIdentifiers { get; } = identifiers;
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ExternalSource;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Find references from inside decompiled or downloaded source.
/// </summary>
/// <remarks>
/// Such a file is opened in an ad-hoc project holding it and nothing else, which is what gives it
/// a semantic model — and which also meant the one question whose answer is elsewhere by
/// definition was asked of a solution with one file in it. Shift+F12 on a framework method every
/// project calls reported the uses inside the decompiled file, and nothing else.
/// </remarks>
[Collection(SharedState.Name)]
public class ExternalSourceReferenceTests
{
    /// <summary>What a decompiler writes back out: the signature, without the body.</summary>
    private const string DecompiledStringBuilder =
        """
        namespace System.Text
        {
            public sealed class StringBuilder
            {
                public StringBuilder Append(string value) => this;
            }
        }
        """;

    [Fact]
    public async Task TheSymbolADecompiledFileDeclaresIsFoundInTheSessionSolution()
    {
        string file = WriteFetchedFile(DecompiledStringBuilder, "System.Text.StringBuilder");
        var document = await WorkspaceService.FindDocumentAsync(file, default);
        Assert.NotNull(document);

        var declared = await DeclaredMethodAsync(document!, "Append");

        var mapped = await ExternalSymbolBridge.TryMapAsync(
            declared, document!, ConsumerSolution(), default);

        Assert.NotNull(mapped);

        // The assembly's member, not the one the decompiled file declares: it is the only one the
        // solution's own call sites could possibly refer to.
        Assert.All(mapped!.Value.Symbol.Locations, l => Assert.True(l.IsInMetadata));
        // Annotated, which the decompiled declaration is not: the answer came from the assembly.
        Assert.Equal(
            "System.Text.StringBuilder.Append(string?)",
            mapped.Value.Symbol.ToDisplayString());
    }

    /// <summary>The point of the mapping: the call site is in the solution, so it is found.</summary>
    [Fact]
    public async Task TheMappedSymbolFindsTheCallSitesInTheSessionSolution()
    {
        string file = WriteFetchedFile(DecompiledStringBuilder, "System.Text.StringBuilder");
        var document = await WorkspaceService.FindDocumentAsync(file, default);
        var declared = await DeclaredMethodAsync(document!, "Append");

        var session = ConsumerSolution();
        var mapped = await ExternalSymbolBridge.TryMapAsync(declared, document!, session, default);
        Assert.NotNull(mapped);

        var found = await SymbolFinder.FindReferencesAsync(mapped!.Value.Symbol, session, default);
        var locations = found.SelectMany(r => r.Locations).ToList();

        Assert.Single(locations);
        Assert.EndsWith(
            "Consumer.cs",
            locations[0].Location.SourceTree!.FilePath,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A caret on something the outside world cannot name stays where it is. Its references are in
    /// the file anyway, and a search widened to the solution would only be a slower way to find
    /// them.
    /// </summary>
    [Fact]
    public async Task ALocalInDecompiledSourceIsNotMappedAnywhere()
    {
        string file = WriteFetchedFile(
            """
            namespace System.Text
            {
                public sealed class StringBuilder
                {
                    public string Describe()
                    {
                        string local = "x";
                        return local;
                    }
                }
            }
            """,
            "System.Text.StringBuilder");

        var document = await WorkspaceService.FindDocumentAsync(file, default);
        var model = await document!.GetSemanticModelAsync(default);
        var root = await document.GetSyntaxRootAsync(default);

        var declarator = root!.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();
        var local = model!.GetDeclaredSymbol(declarator);

        Assert.Null(await ExternalSymbolBridge.TryMapAsync(
            local!, document, ConsumerSolution(), default));
    }

    /// <summary>A file the user owns is already in the solution it belongs to.</summary>
    [Fact]
    public async Task AFileTheUserOwnsIsNotMappedAnywhere()
    {
        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.CalculatorFile);
        var symbol = await DeclaredMethodAsync(document, "Add");

        Assert.Null(await ExternalSymbolBridge.TryMapAsync(
            symbol, document, ConsumerSolution(), default));
    }

    /// <summary>
    /// The whole gesture, over the handler the editor calls: a caret on a member declaration in
    /// decompiled source answers with the fixture's call site.
    /// </summary>
    [Fact]
    public async Task ReferencesFromDecompiledSourceReachTheSolutionsCallSites()
    {
        // The session solution the handler asks for is the most recently used one, so the project
        // holding the call site is opened last.
        await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        string file = WriteFetchedFile(DecompiledStringBuilder, "System.Text.StringBuilder");
        var position = PositionOf(file, DecompiledStringBuilder, "Append(string value)");

        var locations = await NavigationHandlers.ReferencesAsync(
            new ReferenceParams(
                position.TextDocument, position.Position, new ReferenceContext(IncludeDeclaration: false)),
            default);

        // FrameworkReferences.BuildMessage appends to a StringBuilder; nothing else in the fixture
        // does.
        Assert.Contains(locations, l =>
            LspConverters.UriToPath(l.Uri)
                .EndsWith("FrameworkReferences.cs", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A solution that calls the member, standing in for the user's own.</summary>
    private static Solution ConsumerSolution()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Consumer");

        return workspace.CurrentSolution
            .AddProject(projectId, "Consumer", "Consumer", LanguageNames.CSharp)
            .AddMetadataReference(
                projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "Consumer.cs",
                SourceText.From(
                    """
                    using System.Text;

                    public static class Consumer
                    {
                        public static string Run(string value)
                        {
                            var builder = new StringBuilder();
                            return builder.Append(value).ToString();
                        }
                    }
                    """),
                filePath: Path.Combine(Path.GetTempPath(), "RoslynMCP.Tests", "Consumer.cs"));
    }

    private static async Task<ISymbol> DeclaredMethodAsync(Document document, string name)
    {
        var model = await document.GetSemanticModelAsync(default);
        var root = await document.GetSyntaxRootAsync(default);

        var declaration = root!.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == name);

        var symbol = model!.GetDeclaredSymbol(declaration);
        Assert.NotNull(symbol);
        return symbol!;
    }

    /// <summary>Writes a file where a fetch would have put it, sidecar and all.</summary>
    private static string WriteFetchedFile(string text, string reflectionTypeName)
    {
        string directory = Path.Combine(
            ExternalSourceCache.ReferenceSourceDirectory, "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string file = Path.Combine(directory, "Fetched.cs");
        File.WriteAllText(file, text);

        ExternalSourceProject.Ensure(
            new ExternalSourceResult(
                ExternalSourceKind.ReferenceSource,
                typeof(System.Text.StringBuilder).Assembly.Location,
                file,
                [new LinePosition(0, 0)],
                Origin: "test"),
            reflectionTypeName);

        return file;
    }

    /// <summary>LSP position params for the first character of <paramref name="anchor"/>.</summary>
    private static TextDocumentPositionParams PositionOf(string filePath, string text, string anchor)
    {
        int index = text.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(index >= 0, $"anchor '{anchor}' not found");

        int line = 0, lineStart = 0;
        for (int i = 0; i < index; i++)
        {
            if (text[i] == '\n') { line++; lineStart = i + 1; }
        }

        return new TextDocumentPositionParams(
            new TextDocumentIdentifier(LspConverters.PathToUri(filePath)),
            new Position(line, index - lineStart));
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using RoslynMCP.Services.ExternalSource;
using Xunit;
using LspCodeLens = RoslynMCP.Lsp.Protocol.CodeLens;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Tests;

/// <summary>
/// The rest of the navigation gestures, started from inside decompiled or downloaded source.
/// </summary>
/// <remarks>
/// Each of them asks a question whose answer is somewhere else by definition — what implements
/// this, what derives from it, who calls it — and each used to be asked of the ad-hoc project such
/// a file is opened in, which holds that one file. They all answered "nothing", which reads as a
/// working feature over a type nobody uses.
///
/// The fixture is a decompilation of a solution's own assembly, which is what opening something
/// from <c>bin\</c> gives you: the declarations in the file are copies of declarations the
/// solution also has in source, so what the mapping finds is checkable.
/// </remarks>
[Collection(SharedState.Name)]
public class ExternalSourceNavigationTests
{
    /// <summary>
    /// The interface the fixture solution declares, as a decompiler would write it back out.
    /// </summary>
    private const string DecompiledFormatter =
        """
        namespace DecompiledConsumer
        {
            public interface IReportFormatter
            {
                string FormatReport(int value);
            }
        }
        """;

    /// <summary>A framework type the fixture calls into.</summary>
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
    public async Task ImplementationsReachTheSolutionsOwn()
    {
        using var bound = await BoundSolutionAsync();
        var position = await ExternalFileAsync(DecompiledFormatter, "IReportFormatter");

        var locations = await NavigationHandlers.ImplementationAsync(position, default);

        Assert.Contains(locations, l => IsIn(l, "Reporting.cs"));
    }

    [Fact]
    public async Task SubtypesReachTheSolutionsOwn()
    {
        using var bound = await BoundSolutionAsync();
        var position = await ExternalFileAsync(DecompiledFormatter, "IReportFormatter");

        var root = Assert.Single(await TypeHierarchyHandler.PrepareAsync(position, default));
        var subtypes = await TypeHierarchyHandler.SubtypesAsync(
            new TypeHierarchyItemParams(root), default);

        Assert.Contains(subtypes, s => s.Name == "PlainReportFormatter");
    }

    [Fact]
    public async Task IncomingCallsReachTheSolutionsCallSites()
    {
        using var bound = await BoundSolutionAsync();
        var position = await ExternalFileAsync(DecompiledStringBuilder, "Append(string value)");

        var root = Assert.Single(await CallHierarchyHandler.PrepareAsync(position, default));
        var calls = await CallHierarchyHandler.IncomingCallsAsync(
            new CallHierarchyCallsParams(root), default);

        // PlainReportFormatter.Build appends to a StringBuilder; nothing else does.
        Assert.Contains(calls, c => c.From.Name == "Build");
    }

    /// <summary>
    /// Reading a decompilation of your own assembly, F12 on a declaration should land in the
    /// source it was compiled from rather than on the copy under the caret.
    /// </summary>
    [Fact]
    public async Task DefinitionPrefersTheSolutionsSourceOverTheDecompilation()
    {
        using var bound = await BoundSolutionAsync();
        var position = await ExternalFileAsync(DecompiledFormatter, "IReportFormatter");

        var locations = await NavigationHandlers.DefinitionAsync(position, typeDefinition: false, default);

        Assert.Contains(locations, l => IsIn(l, "Reporting.cs"));
    }

    /// <summary>
    /// The gutter count over a decompiled member. Nothing is warmed on the client's behalf here —
    /// a lens re-resolves on every scroll — so the count is the solution's only once its
    /// compilation exists, which is what the sweep leaves behind and what this arranges.
    /// </summary>
    [Fact]
    public async Task TheReferenceLensCountsTheSolutionsUses()
    {
        using var bound = await BoundSolutionAsync(warm: true);
        var position = await ExternalFileAsync(DecompiledStringBuilder, "Append(string value)");

        var lens = new LspCodeLens(
            new LspRange(position.Position, position.Position), Command: null)
        {
            Data = new CodeLensData(
                position.TextDocument.Uri, position.Position.Line, position.Position.Character,
                "references"),
        };

        var resolved = await CodeLensHandler.ResolveAsync(lens, default);

        Assert.NotNull(resolved.Command);
        Assert.Equal("1 reference", resolved.Command!.Title);
    }

    /// <summary>The same relationship the type hierarchy answers, in the gutter.</summary>
    [Fact]
    public async Task InheritanceMarkersPointAtTheSolutionsImplementations()
    {
        using var bound = await BoundSolutionAsync(warm: true);
        var position = await ExternalFileAsync(DecompiledFormatter, "IReportFormatter");

        var markers = await InheritanceMarkersHandler.MarkersAsync(
            new InheritanceMarkersParams(position.TextDocument), default);

        Assert.Contains(markers, m => m.Kind == "implemented"
            && m.Targets.Any(t => t.Title.Contains("PlainReportFormatter")));
    }

    /// <summary>
    /// Outgoing calls out of a decompilation of your own assembly. Everything such a file calls is
    /// metadata to the one-file project it is opened in — including the members of the solution
    /// that produced it — so every call was dropped for having no declaration to open.
    /// </summary>
    [Fact]
    public async Task OutgoingCallsReachWhatTheSolutionDeclaresInSource()
    {
        var position = await ExternalFileAsync(
            """
            namespace System.Text
            {
                public sealed class Caller
                {
                    public string Describe() => new StringBuilder().Append("x").ToString();
                }
            }
            """,
            "Describe()");

        string path = LspConverters.UriToPath(position.TextDocument.Uri);
        var document = await WorkspaceService.FindDocumentAsync(path, default);
        var caller = await CallerSymbolAsync(document!, "Describe");

        // Stands in for the assembly this file was decompiled from: the same members, declared in
        // source, which is what the solution that built it has.
        var session = SessionDeclaring(
            """
            namespace System.Text
            {
                public sealed class StringBuilder
                {
                    public StringBuilder Append(string value) => this;
                }

                public sealed class Caller
                {
                    public string Describe() => "";
                }
            }
            """);

        var bridge = await ExternalSymbolBridge.TryOpenAsync(document!, caller, session, default);
        Assert.NotNull(bridge);

        var calls = await CallHierarchyHandler.OutgoingCallsAsync(
            caller, document!.Project.Solution, position.TextDocument.Uri, mapper: null, default,
            bridge);

        var call = Assert.Single(calls, c => c.To.Name == "Append");
        Assert.EndsWith(
            "Session.cs", LspConverters.UriToPath(call.To.Uri), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Renaming is the one gesture that must not follow the file into the solution: the buffer is
    /// a copy, written read-only, and the edits would land nowhere useful.
    /// </summary>
    [Fact]
    public async Task RenameIsRefusedRatherThanAppliedToTheCopy()
    {
        using var bound = await BoundSolutionAsync();
        var position = await ExternalFileAsync(DecompiledFormatter, "IReportFormatter");

        Assert.Null(await RenameHandler.PrepareRenameAsync(position, default));
        Assert.Null(await RenameHandler.RenameAsync(
            new RenameParams(position.TextDocument, position.Position, "IFormatterOfReports"),
            default));
    }

    /// <summary>A solution of one project holding <paramref name="source"/>.</summary>
    private static Solution SessionDeclaring(string source)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("Session");

        return workspace.CurrentSolution
            .AddProject(projectId, "Session", "Session", LanguageNames.CSharp)
            .AddMetadataReference(
                projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument(
                DocumentId.CreateNewId(projectId),
                "Session.cs",
                SourceText.From(source),
                filePath: Path.Combine(Path.GetTempPath(), "RoslynMCP.Tests", "Session.cs"));
    }

    private static async Task<ISymbol> CallerSymbolAsync(Document document, string name)
    {
        var model = await document.GetSemanticModelAsync(default);
        var root = await document.GetSyntaxRootAsync(default);

        var declaration = root!.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == name);

        var symbol = model!.GetDeclaredSymbol(declaration);
        Assert.NotNull(symbol);
        return symbol!;
    }

    /// <summary>
    /// Binds the fixture solution for the returned scope's lifetime and loads it.
    /// </summary>
    /// <remarks>
    /// Bound rather than merely loaded, because what a session answers when nothing is bound is
    /// whichever project was touched last — and in a parallel run that is whatever another
    /// collection happened to open a moment earlier. These tests passed alone and failed in the
    /// suite until they said which solution they meant.
    /// </remarks>
    private static async Task<IDisposable> BoundSolutionAsync(bool warm = false)
    {
        var bound = WorkspaceService.BindSolutionForTesting(
            FixturePaths.DecompiledConsumerSolutionFile);

        try
        {
            var project = await RoslynTestHelpers.OpenProjectAsync(
                FixturePaths.DecompiledConsumerProjectFile);

            // The callers that run without anybody having asked — a lens, a gutter marker — take
            // only projects that are compiled already, which is what the warm-up sweep leaves
            // behind in a real session.
            if (warm)
                await project.GetCompilationAsync(default);

            return bound;
        }
        catch
        {
            bound.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes a decompiled file where the cache would have it, and returns the position of
    /// <paramref name="anchor"/> in it.
    /// </summary>
    private static async Task<TextDocumentPositionParams> ExternalFileAsync(
        string text, string anchor)
    {
        string directory = Path.Combine(
            ExternalSourceCache.DecompiledDirectory, "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string file = Path.Combine(directory, "Decompiled.cs");
        await File.WriteAllTextAsync(file, text);

        await File.WriteAllTextAsync(
            Path.Combine(directory, DecompiledSourceService.ManifestFileName),
            $$"""
            {
                "AssemblyPath": {{System.Text.Json.JsonSerializer.Serialize(typeof(object).Assembly.Location)}},
                "SourceFilePath": {{System.Text.Json.JsonSerializer.Serialize(file)}},
                "TypeReflectionName": "Decompiled"
            }
            """);

        int index = text.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(index >= 0, $"anchor '{anchor}' not found");

        int line = 0, lineStart = 0;
        for (int i = 0; i < index; i++)
        {
            if (text[i] == '\n') { line++; lineStart = i + 1; }
        }

        return new TextDocumentPositionParams(
            new TextDocumentIdentifier(LspConverters.PathToUri(file)),
            new Position(line, index - lineStart));
    }

    private static bool IsIn(LspLocation location, string fileName) =>
        LspConverters.UriToPath(location.Uri)
            .EndsWith(fileName, StringComparison.OrdinalIgnoreCase);
}

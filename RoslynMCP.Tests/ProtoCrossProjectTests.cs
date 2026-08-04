using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Navigation from a <c>.proto</c> into the projects that consume it, over the layout the pack was
/// built for: the contract in one assembly, the implementation in a second, the callers in a third.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProtoLspTests"/> asks the same questions of a fixture that holds all three roles in
/// one project, where every answer already sits in the project the caret is in. That proves the
/// bindings and nothing about the reach: the workspace loads a project by following its references,
/// which from a contracts project points away from every answer — so a search that never widened
/// the solution would pass there and report an unimplemented service and an uncalled rpc here.
/// </para>
/// <para>
/// Every assertion below is therefore about a file in a different project from the <c>.proto</c>,
/// and the last one is about a project that is loaded, is in the same solution, spells the same
/// member names, and has to contribute nothing.
/// </para>
/// </remarks>
[Collection(SharedState.Name)]
public class ProtoCrossProjectTests
{
    // ---- Implementation, across a project boundary -------------------------------------------

    [Fact]
    public async Task GoToImplementationOnAServiceLandsInTheServerProject()
    {
        var locations = await Implementation("service WidgetService", "service ".Length);

        var location = Assert.Single(locations);
        AssertFile(FixturePaths.ProtoServerServiceFile, location.Uri);
        Assert.Contains(
            "class WidgetGrpcService",
            LineAt(FixturePaths.ProtoServerServiceFile, location.Range.Start.Line));
    }

    [Fact]
    public async Task GoToImplementationOnAnRpcLandsOnTheOverrideInTheServerProject()
    {
        var locations = await Implementation("rpc GetWidgetsById", "rpc ".Length);

        var location = Assert.Single(locations);
        AssertFile(FixturePaths.ProtoServerServiceFile, location.Uri);

        // The override itself, not the class holding it: a three-rpc service has three of them and
        // a caret on one rpc is a question about one.
        string line = LineAt(FixturePaths.ProtoServerServiceFile, location.Range.Start.Line);
        Assert.Contains("override", line);
        Assert.Contains("GetWidgetsById(", line);
    }

    [Fact]
    public async Task GoToImplementationOnTheStreamingRpcAlsoCrossesTheBoundary()
    {
        // The streaming base method takes a response writer and returns a bare Task, so it is the
        // one an override match built around the unary shape would drop without a sign.
        var locations = await Implementation("rpc WatchWidgets", "rpc ".Length);

        var location = Assert.Single(locations);
        AssertFile(FixturePaths.ProtoServerServiceFile, location.Uri);
        Assert.Contains(
            "WatchWidgets(", LineAt(FixturePaths.ProtoServerServiceFile, location.Range.Start.Line));
    }

    // ---- References, across two project boundaries at once ------------------------------------

    [Fact]
    public async Task FindReferencesOnAnRpcAnswersWithBothConsumingProjectsAtOnce()
    {
        var locations = await References("rpc GetWidgetsById", "rpc ".Length);

        Assert.Contains(locations, location => SamePath(location.Uri, FixturePaths.ProtoServerServiceFile));
        Assert.Contains(locations, location => SamePath(location.Uri, FixturePaths.ProtoClientCallerFile));

        // The call site is spelled GetWidgetsByIdAsync — a name written in neither the .proto nor
        // the override, so only the client binding reaches it, and only in Client.
        Assert.Contains(
            locations.Where(location => SamePath(location.Uri, FixturePaths.ProtoClientCallerFile)),
            location => LineAt(FixturePaths.ProtoClientCallerFile, location.Range.Start.Line)
                .Contains("GetWidgetsByIdAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FindReferencesOnAServiceAnswersWithBothConsumingProjectsAtOnce()
    {
        var locations = await References("service WidgetService", "service ".Length);

        Assert.Contains(locations, location => SamePath(location.Uri, FixturePaths.ProtoServerServiceFile));
        Assert.Contains(locations, location => SamePath(location.Uri, FixturePaths.ProtoClientCallerFile));
    }

    [Fact]
    public async Task FindReferencesOnAMessageReachesBothConsumingProjects()
    {
        // Widget is built in Server and read in Client, and is declared in neither.
        var locations = await References("message Widget {", "message ".Length);

        Assert.Contains(locations, location => SamePath(location.Uri, FixturePaths.ProtoServerServiceFile));
        Assert.Contains(locations, location => SamePath(location.Uri, FixturePaths.ProtoClientCallerFile));

        // The class it binds to is back in Contracts and is not part of the answer: protoc rewrites
        // that file on every build and mentions the message on twenty lines of it, so the
        // declaration is reported as the `message` line the developer actually wrote.
        Assert.DoesNotContain(
            locations,
            location => SamePath(location.Uri, FixturePaths.ProtoSolutionWidgetsGeneratedFile));

        Assert.Contains(
            locations,
            location => SamePath(location.Uri, FixturePaths.ProtoSolutionWidgetsProtoFile));
    }

    // ---- What the widening actually did -------------------------------------------------------

    [Fact]
    public async Task TheProtoBindsThroughContractsWhileItsAnswersLiveElsewhere()
    {
        var view = await ViewAsync();

        // The caret's nearest .csproj is Contracts, and that is where the bindings come from:
        // Server and Client compile no generated code of their own, they reference it.
        Assert.NotNull(view.Project);
        Assert.Equal(
            Path.GetFullPath(FixturePaths.ProtoContractsProjectFile),
            Path.GetFullPath(view.Project!.FilePath!),
            StringComparer.OrdinalIgnoreCase);

        Assert.False(
            view.Index.IsEmpty,
            "the Contracts fixture produced no generated documents; the project failed to load");

        var hit = ProtoSymbolResolver.ResolveAt(view, OffsetOf("service WidgetService", "service ".Length));
        Assert.NotNull(hit);

        var service = Assert.IsType<ProtoService>(hit!.Target);
        Assert.Equal("WidgetServiceBase", view.Index.ServiceBaseFor(service)?.Name);

        var implementations = await ProtoReferenceService.FindImplementationsAsync(
            hit, view.Index, view.Project!, default);

        // Bound in Contracts, implemented outside it. Both halves matter: an index that bound
        // nothing and a search that found nothing produce the same empty answer.
        Assert.NotEmpty(implementations);
        Assert.All(implementations, symbol => Assert.Equal(
            Path.GetFullPath(FixturePaths.ProtoServerServiceFile),
            Path.GetFullPath(SourcePath(symbol)),
            StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheSearchPullsTheConsumingProjectsIntoTheSolutionItRunsAgainst()
    {
        // Opening Contracts loads what Contracts references, which is the opposite direction from
        // every answer. Unless the pack widens the solution first, Server and Client are simply not
        // in it, and both searches above come back empty while looking like they worked.
        await References("service WidgetService", "service ".Length);

        var view = await ViewAsync();
        string[] loaded =
        [
            .. view.Project!.Solution.Projects
                .Select(project => project.FilePath)
                .OfType<string>()
                .Select(Path.GetFullPath)
        ];

        Assert.Contains(
            Path.GetFullPath(FixturePaths.ProtoServerProjectFile), loaded, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            Path.GetFullPath(FixturePaths.ProtoClientProjectFile), loaded, StringComparer.OrdinalIgnoreCase);
    }

    // ---- The control ---------------------------------------------------------------------------

    [Fact]
    public async Task AProjectThatDoesNotReferenceTheContractContributesNothing()
    {
        // Opened first, on purpose. Left out of the workspace these assertions would hold for the
        // uninteresting reason — a project nobody loaded cannot be reported — and would say nothing
        // about whether the search follows bound symbols or spellings.
        var (_, unrelated) = await WorkspaceService.GetOrOpenProjectAsync(
            FixturePaths.ProtoUnrelatedProjectFile, cancellationToken: default);

        var compilation = await unrelated.GetCompilationAsync(default);
        var lookup = compilation?.GetTypeByMetadataName("ProtoSolution.Unrelated.WidgetLookup");
        Assert.NotNull(lookup);
        Assert.NotEmpty(lookup!.GetMembers("GetWidgetsById"));

        var references = await References("rpc GetWidgetsById", "rpc ".Length);
        var implementations = await Implementation("rpc GetWidgetsById", "rpc ".Length);

        var view = await ViewAsync();
        Assert.Contains(view.Project!.Solution.Projects, project => project.Name == "Unrelated");

        Assert.DoesNotContain(
            references, location => SamePath(location.Uri, FixturePaths.ProtoUnrelatedLookupFile));
        Assert.DoesNotContain(
            implementations, location => SamePath(location.Uri, FixturePaths.ProtoUnrelatedLookupFile));

        // and the same for the message, whose name Unrelated also declares a type for.
        Assert.DoesNotContain(
            await References("message Widget {", "message ".Length),
            location => SamePath(location.Uri, FixturePaths.ProtoUnrelatedLookupFile));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static TextDocumentIdentifier Doc(string path) => new(LspConverters.PathToUri(path));

    private static async Task<ProtoProjectView> ViewAsync()
    {
        var view = await ProtoWorkspace.GetAsync(FixturePaths.ProtoSolutionWidgetsProtoFile, default);
        Assert.NotNull(view);
        return view!;
    }

    private static Task<Location[]> Implementation(string needle, int offsetIntoNeedle) =>
        ProtoNavigationHandler.ImplementationAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.ProtoSolutionWidgetsProtoFile),
                PositionOf(needle, offsetIntoNeedle)),
            default);

    private static Task<Location[]> References(string needle, int offsetIntoNeedle) =>
        ProtoNavigationHandler.ReferencesAsync(
            new ReferenceParams(
                Doc(FixturePaths.ProtoSolutionWidgetsProtoFile),
                PositionOf(needle, offsetIntoNeedle),
                new ReferenceContext(IncludeDeclaration: true)),
            default);

    /// <summary>The character offset of <paramref name="needle"/> in the contract.</summary>
    private static int OffsetOf(string needle, int offsetIntoNeedle)
    {
        string text = File.ReadAllText(FixturePaths.ProtoSolutionWidgetsProtoFile);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in widgets.proto");
        return index + offsetIntoNeedle;
    }

    private static Position PositionOf(string needle, int offsetIntoNeedle)
    {
        var line = SourceText.From(File.ReadAllText(FixturePaths.ProtoSolutionWidgetsProtoFile))
            .Lines.GetLinePosition(OffsetOf(needle, offsetIntoNeedle));

        return new Position(line.Line, line.Character);
    }

    /// <summary>The file a symbol is written in.</summary>
    private static string SourcePath(Microsoft.CodeAnalysis.ISymbol symbol) =>
        symbol.Locations.First(location => location.IsInSource).SourceTree?.FilePath ?? string.Empty;

    private static string LineAt(string path, int line) => File.ReadAllLines(path)[line];

    private static bool SamePath(string uri, string path) =>
        string.Equals(
            Path.GetFullPath(LspConverters.UriToPath(uri)),
            Path.GetFullPath(path),
            StringComparison.OrdinalIgnoreCase);

    private static void AssertFile(string expected, string uri) =>
        Assert.Equal(
            Path.GetFullPath(expected),
            Path.GetFullPath(LspConverters.UriToPath(uri)),
            StringComparer.OrdinalIgnoreCase);
}

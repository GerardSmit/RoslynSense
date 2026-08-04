using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Proto;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Tests;

/// <summary>
/// The gesture the pack was reported broken on: F12 in a project that <em>consumes</em> a contract,
/// which has to land on the <c>rpc</c> in the <c>.proto</c> and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProtoContributorTests"/> asks the same questions of the single-project fixture, where
/// the caret, the <c>.proto</c> and protoc's output are all in one assembly. That layout hides the
/// two defects this file exists for. The first is that an index built for the caret's project finds
/// no protoc output when the caret is in a consumer — the normal layout, since a <c>.proto</c> lives
/// in a contracts project by construction — so the pack contributed nothing at all and F12 fell
/// through to Roslyn, which lands in <c>obj</c>. The second is that a contribution could only add,
/// so even once the <c>.proto</c> line was offered the generated file was offered beside it and the
/// editor put up a picker.
/// </para>
/// <para>
/// Every caret below is therefore in <c>Client</c>, and every assertion is about a file in
/// <c>Contracts</c>. The wrong-project defect is stated as an assertion of its own rather than left
/// to be inferred from an empty result: reverting to the caret's project makes the answer empty,
/// which is the failure that looks like a feature that simply found nothing.
/// </para>
/// </remarks>
[Collection(SharedState.Name)]
public class ProtoDefinitionContributorTests
{
    /// <summary>The caret the owner held: a call to the generated client, in a hand-written file in
    /// a project that owns no <c>.proto</c> and compiles no generated code.</summary>
    private const string ClientCallNeedle = "_client.GetWidgetsByIdAsync(request)";

    private const int ClientCallOffset = 10;

    // ---- The headline --------------------------------------------------------------------------

    [Fact]
    public async Task DefinitionOnAGeneratedClientCallFromAConsumerLandsOnTheRpcInTheContract()
    {
        PublishProtoPack();

        var locations = await DefinitionAsync(ClientCallNeedle, ClientCallOffset);

        // Single, and that is half the point: before the fix this was one location too, and it was
        // Contracts\Generated\widgets\WidgetsGrpc.cs.
        var location = Assert.Single(locations);

        Assert.EndsWith(
            ".proto", LspConverters.UriToPath(location.Uri), StringComparison.OrdinalIgnoreCase);
        AssertFile(FixturePaths.ProtoSolutionWidgetsProtoFile, location.Uri);

        // The rpc's own name and not the line, the service, or the file: three rpcs are declared in
        // that service and a caret on one call is a question about one of them.
        Assert.Equal("GetWidgetsById", TextAt(location));
    }

    // ---- The suppression -----------------------------------------------------------------------

    [Fact]
    public async Task TheGeneratedFileIsNotOfferedBesideTheRpcItWasBuiltFrom()
    {
        PublishProtoPack();

        var locations = await DefinitionAsync(ClientCallNeedle, ClientCallOffset);

        // By path, which is what the owner read off the editor's title bar.
        Assert.DoesNotContain(
            locations,
            location => SamePath(location.Uri, FixturePaths.ProtoSolutionWidgetsGrpcGeneratedFile));
        Assert.DoesNotContain(
            locations,
            location => SamePath(location.Uri, FixturePaths.ProtoSolutionWidgetsGeneratedFile));

        // And by asking the thing that decides it, so a generated file this test never thought to
        // name is covered too.
        var index = await ContractsIndexAsync();
        Assert.DoesNotContain(
            locations, location => index.IsGenerated(LspConverters.UriToPath(location.Uri)));

        // One target rather than two. Asserted separately from the file names because the complaint
        // was not only about where F12 went — a second entry makes the editor ask which one, and
        // the answer it offers alongside is a file the next build overwrites.
        Assert.Single(locations);
    }

    // ---- The wrong-project regression ----------------------------------------------------------

    [Fact]
    public async Task TheCaretsProjectIsNotTheProjectThatDeclaresTheSymbol()
    {
        var (caret, declaring, symbol) = await ProjectsBehindAsync(ClientCallNeedle, ClientCallOffset);

        // The premise of the whole file, spelled out. If these two were ever the same project the
        // assertions below would pass for the wrong reason and the fixture would have stopped
        // reproducing the report.
        Assert.Equal("Client", caret.Name);
        Assert.Equal("Contracts", declaring.Name);
        Assert.NotEqual(caret.Id, declaring.Id);

        // What indexing the caret's project actually produces: nothing. Client has no .proto under
        // it, so the index is the shared empty one and every lookup against it is null — which is
        // why the old code contributed nothing rather than merely ranking its answer badly.
        var caretIndex = await ProtoGeneratedIndex.GetAsync(caret, default);
        Assert.True(
            caretIndex.IsEmpty,
            "Client compiles generated protobuf code after all; this test no longer reproduces the "
            + "layout the bug needs");
        Assert.False(
            caretIndex.DeclarationFor(symbol, includeInherited: true).HasValue,
            "the caret's project answered for a symbol it does not declare");

        // And what indexing the declaring project produces: the answer.
        var declaringIndex = await ProtoGeneratedIndex.GetAsync(declaring, default);
        Assert.True(
            declaringIndex.DeclarationFor(symbol, includeInherited: true).HasValue,
            "Contracts' index does not know the symbol Contracts compiled");
    }

    // ---- The same three, for the other two kinds the owner asked about --------------------------

    [Fact]
    public async Task TheSameHoldsForACaretOnAGeneratedMessageType()
    {
        // `Widget widget = call.ResponseStream.Current;` — a type reference rather than a `new`,
        // because a caret inside `new Widget()` binds to the constructor protoc emitted, which
        // stands for no proto declaration of its own.
        await AssertReachesOnlyTheContractAsync("Widget widget = call", 2, "Widget");
    }

    [Fact]
    public async Task TheSameHoldsForACaretOnAGeneratedProperty()
    {
        // `widget.Label`, which is the `string label = 2;` in the message — the field name, in the
        // spelling the author of the .proto wrote rather than the one protoc derived.
        await AssertReachesOnlyTheContractAsync("widget.Label", 8, "label");
    }

    // ---- Find references, from the same project ------------------------------------------------

    [Fact]
    public async Task FindReferencesFromAConsumerReportsTheContractAndNotProtocsOutput()
    {
        PublishProtoPack();

        var locations = await ReferencesAsync(ClientCallNeedle, ClientCallOffset);

        var declaration = Assert.Single(
            locations,
            location => LspConverters.UriToPath(location.Uri)
                .EndsWith(".proto", StringComparison.OrdinalIgnoreCase));

        AssertFile(FixturePaths.ProtoSolutionWidgetsProtoFile, declaration.Uri);
        Assert.Equal("GetWidgetsById", TextAt(declaration));

        // The hand-written call site the search started from is still reported, so the absences
        // below are a filter rather than a search that came back empty.
        Assert.Contains(
            locations, location => SamePath(location.Uri, FixturePaths.ProtoClientCallerFile));

        var index = await ContractsIndexAsync();
        Assert.DoesNotContain(
            locations, location => index.IsGenerated(LspConverters.UriToPath(location.Uri)));
        Assert.DoesNotContain(
            locations,
            location => SamePath(location.Uri, FixturePaths.ProtoSolutionWidgetsGrpcGeneratedFile));
    }

    // ---- That the withdrawal did not overreach -------------------------------------------------

    [Fact]
    public async Task GoToImplementationStillReachesTheHandWrittenOverrideInServer()
    {
        PublishProtoPack();

        // From the .proto, which is the gesture the pack owns end to end.
        var fromContract = await ProtoNavigationHandler.ImplementationAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.ProtoSolutionWidgetsProtoFile),
                PositionOf(FixturePaths.ProtoSolutionWidgetsProtoFile, "rpc GetWidgetsById", 4)),
            default);

        var location = Assert.Single(fromContract);
        AssertFile(FixturePaths.ProtoServerServiceFile, location.Uri);
        Assert.Contains(
            "override", LineAt(FixturePaths.ProtoServerServiceFile, location.Range.Start.Line));

        // And from C#, through the handler the withdrawal lives next to. An override in a
        // hand-written service is not generated, so nothing may take it out of the answer — which is
        // why textDocument/implementation asks no contributor anything.
        var fromCSharp = await NavigationHandlers.ImplementationAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.ProtoServerServiceFile),
                PositionOf(FixturePaths.ProtoServerServiceFile, "WidgetService.WidgetServiceBase", 16)),
            default);

        var derived = Assert.Single(fromCSharp);
        AssertFile(FixturePaths.ProtoServerServiceFile, derived.Uri);
        Assert.Equal("WidgetGrpcService", TextAt(derived));
    }

    [Fact]
    public async Task DefinitionInAProjectWithNoProtobufIsUntouched()
    {
        PublishProtoPack();

        // Warmed on purpose, and first. Supersedes reads the indexes that have been built, so an
        // absence of withdrawal here would hold for the uninteresting reason unless one exists that
        // could have withdrawn something.
        await DefinitionAsync(ClientCallNeedle, ClientCallOffset);
        var index = await ContractsIndexAsync();
        Assert.False(index.IsEmpty);

        // Unrelated references no contract and no protobuf runtime, and spells the contract's names
        // anyway. F12 on its own type has to land on its own declaration.
        var locations = await NavigationHandlers.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.ProtoUnrelatedLookupFile),
                PositionOf(FixturePaths.ProtoUnrelatedLookupFile, "List<Widget> GetWidgetsById", 5)),
            typeDefinition: false,
            default);

        var location = Assert.Single(locations);
        AssertFile(FixturePaths.ProtoUnrelatedLookupFile, location.Uri);
        Assert.Equal("Widget", TextAt(location));
    }

    [Fact]
    public async Task DefinitionInsideTheProtoStillResolvesATypeReferenceToItsDeclaration()
    {
        PublishProtoPack();

        // The other front-end entirely: a caret in the .proto goes to ProtoNavigationHandler, which
        // never sees an ILanguageDefinitionContributor. Guarded because a withdrawal written one
        // level too high would take the .proto's own declarations out of its own answers.
        var locations = await ProtoNavigationHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.ProtoSolutionWidgetsProtoFile),
                PositionOf(
                    FixturePaths.ProtoSolutionWidgetsProtoFile,
                    "returns (GetWidgetsByIdReply)",
                    "returns (".Length)),
            typeDefinition: false,
            default);

        var location = Assert.Single(locations);
        AssertFile(FixturePaths.ProtoSolutionWidgetsProtoFile, location.Uri);
        Assert.Equal("GetWidgetsByIdReply", TextAt(location));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>
    /// The three claims the report makes, for one caret in the Client project: the answer is the
    /// declaration in the contract, nothing protoc wrote is offered beside it, and the project the
    /// caret sits in is not the project that declares the symbol.
    /// </summary>
    private static async Task AssertReachesOnlyTheContractAsync(
        string needle, int offsetIntoNeedle, string expectedName)
    {
        PublishProtoPack();

        var locations = await DefinitionAsync(needle, offsetIntoNeedle);

        var location = Assert.Single(locations);
        AssertFile(FixturePaths.ProtoSolutionWidgetsProtoFile, location.Uri);
        Assert.Equal(expectedName, TextAt(location));

        var index = await ContractsIndexAsync();
        Assert.DoesNotContain(
            locations, candidate => index.IsGenerated(LspConverters.UriToPath(candidate.Uri)));

        var (caret, declaring, symbol) = await ProjectsBehindAsync(needle, offsetIntoNeedle);
        Assert.NotEqual(caret.Id, declaring.Id);
        Assert.True(
            (await ProtoGeneratedIndex.GetAsync(caret, default)).IsEmpty,
            $"the project holding '{needle}' compiles generated code, so this caret no longer "
            + "reproduces the cross-project layout");
        Assert.True(
            (await ProtoGeneratedIndex.GetAsync(declaring, default))
                .DeclarationFor(symbol, includeInherited: true).HasValue,
            $"the project declaring '{needle}' does not know the symbol it compiled");
    }

    /// <summary>
    /// The project the caret is in, the project whose assembly declares what it binds to, and that
    /// symbol — resolved the way the pack resolves it, so a change to either side shows up here.
    /// </summary>
    private static async Task<(Project Caret, Project Declaring, ISymbol Symbol)> ProjectsBehindAsync(
        string needle, int offsetIntoNeedle)
    {
        var (_, document) = await RoslynTestHelpers.OpenDocumentAsync(FixturePaths.ProtoClientCallerFile);

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            document,
            OffsetOf(FixturePaths.ProtoClientCallerFile, needle, offsetIntoNeedle),
            default);

        Assert.NotNull(symbol);

        var declaring = document.Project.Solution.GetProject(symbol!.OriginalDefinition.ContainingAssembly);
        Assert.NotNull(declaring);

        return (document.Project, declaring!, symbol);
    }

    /// <summary>
    /// The index of the project that really compiles the contract, with the check that it recognises
    /// protoc's output — without which every "no generated location" assertion above would hold
    /// because the index recognises nothing.
    /// </summary>
    private static async Task<ProtoGeneratedIndex> ContractsIndexAsync()
    {
        var contracts = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.ProtoContractsProjectFile);
        var index = await ProtoGeneratedIndex.GetAsync(contracts, default);

        Assert.True(
            index.IsGenerated(FixturePaths.ProtoSolutionWidgetsGrpcGeneratedFile)
            && index.IsGenerated(FixturePaths.ProtoSolutionWidgetsGeneratedFile),
            "Contracts' index does not call its own protoc output generated, so nothing asserted "
            + "against it means anything");

        return index;
    }

    private static void PublishProtoPack() =>
        new LanguageRegistry([new ProtoLanguage(new MarkdownFormatter())]).Publish();

    private static TextDocumentIdentifier Doc(string path) => new(LspConverters.PathToUri(path));

    private static Task<LspLocation[]> DefinitionAsync(string needle, int offsetIntoNeedle) =>
        NavigationHandlers.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.ProtoClientCallerFile),
                PositionOf(FixturePaths.ProtoClientCallerFile, needle, offsetIntoNeedle)),
            typeDefinition: false,
            default);

    private static Task<LspLocation[]> ReferencesAsync(string needle, int offsetIntoNeedle) =>
        NavigationHandlers.ReferencesAsync(
            new ReferenceParams(
                Doc(FixturePaths.ProtoClientCallerFile),
                PositionOf(FixturePaths.ProtoClientCallerFile, needle, offsetIntoNeedle),
                new ReferenceContext(IncludeDeclaration: true)),
            default);

    private static int OffsetOf(string path, string needle, int offsetIntoNeedle)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");
        return index + offsetIntoNeedle;
    }

    private static Position PositionOf(string path, string needle, int offsetIntoNeedle)
    {
        var line = SourceText.From(File.ReadAllText(path))
            .Lines.GetLinePosition(OffsetOf(path, needle, offsetIntoNeedle));

        return new Position(line.Line, line.Character);
    }

    /// <summary>The source the location's range actually covers. Asserting on this rather than on a
    /// line number is what proves the answer points at the declaration's own name.</summary>
    private static string TextAt(LspLocation location)
    {
        var text = SourceText.From(File.ReadAllText(LspConverters.UriToPath(location.Uri)));
        return text.ToString(LspConverters.ToTextSpan(text, location.Range));
    }

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

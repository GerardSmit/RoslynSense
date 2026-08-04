using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Languages.Proto;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The four editor features of the pack that answer about structure rather than about a symbol:
/// call and type hierarchy, expand-selection, Ctrl+T, and the workspace diagnostic sweep.
/// </summary>
[Collection(SharedState.Name)]
public class ProtoEditorFeatureTests : IDisposable
{
    private readonly string _session = $"proto-editor-{Guid.NewGuid():N}";
    private readonly List<string> _buffers = [];

    public void Dispose()
    {
        OpenDocumentStore.CloseSession(_session);

        // The parses these buffers produced were of unsaved text; leaving one memoized would hand
        // the next reader a version of the file that is not on disk.
        foreach (string path in _buffers)
            ProtoDocumentService.Invalidate(path);
    }

    private static ProtoLanguage Pack() => new(new MarkdownFormatter());

    private static TextDocumentIdentifier Doc(string path) =>
        new(LspConverters.PathToUri(path));

    private static Position PositionOf(string path, string needle, int offsetIntoNeedle = 0)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");

        var line = SourceText.From(text).Lines.GetLinePosition(index + offsetIntoNeedle);
        return new Position(line.Line, line.Character);
    }

    // ---- textDocument/selectionRange ----------------------------------------------------------

    /// <summary>The source each step of the expand-selection chain covers, innermost first.</summary>
    private static async Task<string[]> ChainAsync(string path, Position position)
    {
        var chains = await Pack().SelectionRangesAsync(
            new SelectionRangeParams(Doc(path), [position]), default);

        var source = SourceText.From(File.ReadAllText(path));
        var texts = new List<string>();

        for (var step = Assert.Single(chains); step is not null; step = step.Parent)
            texts.Add(source.ToString(LspConverters.ToTextSpan(source, step.Range)));

        return [.. texts];
    }

    [Fact]
    public async Task ExpandingFromAFieldNameWalksOutOneDeclarationAtATimeToTheFile()
    {
        string[] chain = await ChainAsync(
            FixturePaths.WidgetTypesProtoFile,
            PositionOf(FixturePaths.WidgetTypesProtoFile, "int32 row = 1;", "int32 ".Length));

        // Every step and nothing between them. A chain that skipped the body, or that jumped from
        // the field to the file, is what makes Ctrl+W unusable: the user presses it again and
        // overshoots what they were reaching for, with no way back except the mouse.
        Assert.Equal(7, chain.Length);

        Assert.Equal("row", chain[0]);
        Assert.Equal("int32 row = 1;", chain[1]);

        // The `{ … }` of the message the field is in, without its header — the step reached for
        // when moving fields around.
        AssertBraced(chain[2], "int32 row");
        AssertDeclaration(chain[3], "message Placement");

        // And then one enclosing declaration per keypress, which is the whole point of building the
        // chain from the nested declaration list rather than from the punctuation nearby.
        AssertBraced(chain[4], "message Placement");
        AssertDeclaration(chain[5], "message Widget");

        Assert.Equal(File.ReadAllText(FixturePaths.WidgetTypesProtoFile), chain[6]);
    }

    [Fact]
    public async Task AFieldsWireNumberIsAStepOfItsOwn()
    {
        string[] chain = await ChainAsync(
            FixturePaths.WidgetTypesProtoFile,
            PositionOf(FixturePaths.WidgetTypesProtoFile, "string label = 3;", "string label = ".Length));

        // The number is the field's identity on the wire — renaming a field is safe and renumbering
        // it is a breaking change — so selecting it alone is a step somebody actually wants, rather
        // than punctuation on the way out to the field.
        Assert.Equal("3", chain[0]);
        Assert.Equal("string label = 3;", chain[1]);
        Assert.Equal(File.ReadAllText(FixturePaths.WidgetTypesProtoFile), chain[^1]);
    }

    [Fact]
    public async Task ExpandingFromAnImportPathReachesTheWholeStatement()
    {
        string[] chain = await ChainAsync(
            FixturePaths.WidgetTypesProtoFile,
            PositionOf(FixturePaths.WidgetTypesProtoFile, "\"common/types.proto\"", 1));

        // An import is not a declaration and so is in no walk over the declaration list; without
        // the statement the chain would go from the path straight to the file.
        Assert.Equal(3, chain.Length);
        Assert.Equal("\"common/types.proto\"", chain[0]);
        Assert.Equal("import \"common/types.proto\";", chain[1]);
    }

    [Fact]
    public async Task ACaretOnNothingStillGetsTheWholeFile()
    {
        string[] chain = await ChainAsync(FixturePaths.WidgetTypesProtoFile, new Position(1, 0));

        // The blank line under `syntax`. Returning nothing at all would make the keypress do
        // nothing, where selecting the document is the answer every other language gives.
        Assert.Equal(File.ReadAllText(FixturePaths.WidgetTypesProtoFile), Assert.Single(chain));
    }

    [Fact]
    public async Task EachRequestedPositionGetsItsOwnChain()
    {
        var chains = await Pack().SelectionRangesAsync(
            new SelectionRangeParams(
                Doc(FixturePaths.WidgetTypesProtoFile),
                [
                    PositionOf(FixturePaths.WidgetTypesProtoFile, "int64 id = 1;", "int64 ".Length),
                    PositionOf(FixturePaths.WidgetTypesProtoFile, "string label = 3;", "string ".Length),
                ]),
            default);

        // Multi-caret editing sends every caret in one request, and the protocol pairs the answers
        // with the positions by index — one chain short and every caret below it expands to the
        // wrong thing.
        Assert.Equal(2, chains.Length);

        var source = SourceText.From(File.ReadAllText(FixturePaths.WidgetTypesProtoFile));
        Assert.Equal("id", source.ToString(LspConverters.ToTextSpan(source, chains[0].Range)));
        Assert.Equal("label", source.ToString(LspConverters.ToTextSpan(source, chains[1].Range)));
    }

    private static void AssertBraced(string text, string contains)
    {
        Assert.StartsWith("{", text);
        Assert.EndsWith("}", text);
        Assert.Contains(contains, text);
    }

    private static void AssertDeclaration(string text, string header)
    {
        Assert.StartsWith(header, text);
        Assert.EndsWith("}", text);
    }

    // ---- Call hierarchy -----------------------------------------------------------------------

    [Fact]
    public async Task ACallHierarchyIsRootedOnTheRpcAsTheProtoSpellsIt()
    {
        var item = Assert.Single(await Pack().PrepareCallHierarchyAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.WidgetsProtoFile),
                PositionOf(FixturePaths.WidgetsProtoFile, "rpc GetWidgetsById", "rpc ".Length)),
            default));

        // `GetWidgetsById`, not the `GetWidgetsByIdAsync` protoc generated beside it: the user is
        // looking at the .proto and that is the name they wrote.
        Assert.Equal("GetWidgetsById", item.Name);
        Assert.Equal(LspSymbolKind.Method, item.Kind);
        Assert.Equal("widgets.WidgetService", item.Detail);

        // Nothing is stashed on the item, so a follow-up request has to be able to re-resolve it
        // from its own selection range. That only works if the range really is the rpc's name.
        var source = SourceText.From(File.ReadAllText(FixturePaths.WidgetsProtoFile));
        Assert.Equal("GetWidgetsById", source.ToString(LspConverters.ToTextSpan(source, item.SelectionRange)));
    }

    [Fact]
    public async Task NothingButAnRpcRootsACallHierarchy()
    {
        (string Needle, int Offset)[] carets =
        [
            ("message WidgetEvent", 8),
            ("service WidgetService", 8),
            ("Kind kind = 1;", 5),
        ];

        foreach (var (needle, offset) in carets)
        {
            // A message is data and a field is a property on it: both are used rather than called,
            // and offering a call hierarchy on one promises a tree that can only ever be empty.
            Assert.Empty(await Pack().PrepareCallHierarchyAsync(
                new TextDocumentPositionParams(
                    Doc(FixturePaths.WidgetsProtoFile),
                    PositionOf(FixturePaths.WidgetsProtoFile, needle, offset)),
                default));
        }
    }

    [Fact]
    public async Task TheRpcsIncomingCallsAreTheCallSitesOfEveryMethodProtocGeneratedForIt()
    {
        var item = Assert.Single(await Pack().PrepareCallHierarchyAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.WidgetsProtoFile),
                PositionOf(FixturePaths.WidgetsProtoFile, "rpc GetWidgetsById", "rpc ".Length)),
            default));

        var calls = await Pack().IncomingCallsAsync(new CallHierarchyCallsParams(item), default);

        string[] callers = [.. calls.Select(c => c.From.Name)];

        // Both hand-written call sites, and neither of them names anything that appears in the
        // .proto: the client's method is GetWidgetsByIdAsync, which is reached only through the
        // binding. A hierarchy rooted on the base method alone would report neither.
        Assert.Contains("GetWidgetLabelsAsync", callers);
        Assert.Contains("GetSingleWidgetAsync", callers);
        Assert.Contains(calls, call =>
            Uri.UnescapeDataString(call.From.Uri).EndsWith("WidgetClientCaller.cs", StringComparison.Ordinal));

        // One rpc is several C# symbols — a virtual on the base, its overrides and four client
        // overloads — and Roslyn's caller search takes one symbol at a time. A caller reached
        // through two of them must still be one node, with each of its call sites listed once.
        Assert.Equal(
            calls.Length,
            calls.Select(call => (call.From.Uri, call.From.SelectionRange)).Distinct().Count());
        Assert.All(calls, call => Assert.Equal(call.FromRanges.Length, call.FromRanges.Distinct().Count()));
    }

    [Fact]
    public async Task AnRpcCallsNothing()
    {
        var item = Assert.Single(await Pack().PrepareCallHierarchyAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.WidgetsProtoFile),
                PositionOf(FixturePaths.WidgetsProtoFile, "rpc WatchWidgets", "rpc ".Length)),
            default));

        // An rpc declares a signature and has no body. The generated method standing for it has one
        // made of protoc's marshalling, which is the runtime's business rather than a call the
        // contract makes — reporting it would fill the tree with Google.Protobuf internals.
        Assert.Empty(await Pack().OutgoingCallsAsync(new CallHierarchyCallsParams(item), default));
    }

    [Fact]
    public async Task ACallHierarchyOnGeneratedCSharpReachesTheRpcItWasGeneratedFrom()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.ProtoProjectFile);
        var compilation = await project.GetCompilationAsync(default);

        var implementation = compilation!.GetTypeByMetadataName("ProtoFixture.WidgetGrpcService");
        Assert.NotNull(implementation);

        var method = Assert.Single(implementation!.GetMembers("GetWidgetsById"));

        var calls = await ((ILanguageCallHierarchyContributor)Pack())
            .IncomingCallsAsync(method, project, default);

        // Find-references from this same caret already lists the rpc. A call hierarchy on the
        // identical caret omitting it would be two features disagreeing about one symbol, which is
        // worse than either answer alone.
        var call = Assert.Single(calls);
        Assert.Equal("GetWidgetsById", call.From.Name);
        Assert.Equal(LspSymbolKind.Method, call.From.Kind);
        Assert.EndsWith("widgets.proto", Uri.UnescapeDataString(call.From.Uri), StringComparison.Ordinal);
    }

    // ---- Type hierarchy -----------------------------------------------------------------------

    [Fact]
    public async Task ATypeHierarchyIsRootedOnAServiceAndOnNothingElse()
    {
        var item = Assert.Single(await Pack().PrepareTypeHierarchyAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.WidgetsProtoFile),
                PositionOf(FixturePaths.WidgetsProtoFile, "service WidgetService", "service ".Length)),
            default));

        Assert.Equal("WidgetService", item.Name);
        Assert.Equal(LspSymbolKind.Class, item.Kind);

        // A message is a sealed generated class and an enum is a generated enum: nothing derives
        // from either, so rooting a hierarchy on one promises a tree that is always a single node.
        Assert.Empty(await Pack().PrepareTypeHierarchyAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.WidgetsProtoFile),
                PositionOf(FixturePaths.WidgetsProtoFile, "message WidgetEvent", "message ".Length)),
            default));
    }

    [Fact]
    public async Task TheServicesSubtypesAreTheHandWrittenImplementations()
    {
        var item = Assert.Single(await Pack().PrepareTypeHierarchyAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.WidgetsProtoFile),
                PositionOf(FixturePaths.WidgetsProtoFile, "service WidgetService", "service ".Length)),
            default));

        var subtypes = await Pack().SubtypesAsync(new TypeHierarchyItemParams(item), default);

        // Nothing in the .proto names WidgetGrpcService and nothing in WidgetGrpcService names
        // widgets.proto: the two are joined only through the abstract base protoc generated, which
        // is the class the subtype search has to have been rooted on.
        Assert.Equal("WidgetGrpcService", Assert.Single(subtypes).Name);

        // protoc's service base derives from object and implements no interface, so there is
        // nothing above it. Asking anyway is what keeps the answer right if that ever changes.
        Assert.Empty(await Pack().SupertypesAsync(new TypeHierarchyItemParams(item), default));
    }

    // ---- workspace/symbol ---------------------------------------------------------------------

    /// <summary>
    /// The picker's answer for a query, narrowed to the fixture project.
    /// </summary>
    /// <remarks>
    /// Narrowed because the workspace is shared with every other test in this collection and may
    /// hold other solutions' schemas by the time this runs. What is being asserted is the shape of
    /// each entry, not how many projects happened to be open.
    /// </remarks>
    private static async Task<IReadOnlyList<SymbolInformation>> WorkspaceSymbolsAsync(string query)
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.ProtoProjectFile);
        var symbols = await Pack().WorkspaceSymbolsAsync(query, project.Solution, default);

        string root = Path.GetFullPath(FixturePaths.ProtoProjectDir);

        return
        [
            .. symbols.Where(s =>
                LspConverters.UriToPath(s.Location.Uri).StartsWith(root, StringComparison.OrdinalIgnoreCase))
        ];
    }

    [Fact]
    public async Task CtrlTFindsAServiceAndTheRpcsUnderIt()
    {
        var service = Assert.Single(await WorkspaceSymbolsAsync("WidgetService"), s => s.Name == "WidgetService");

        // Interface, because a service is the contract a hand-written class implements — the same
        // thing the outline says, so the picker and the outline cannot show different icons for it.
        Assert.Equal(LspSymbolKind.Interface, service.Kind);

        // The package, not the C# namespace protoc derives: Ctrl+T over a .proto is a question
        // about the schema, and the namespace depends on an option most files do not set.
        Assert.Equal("widgets", service.ContainerName);
        Assert.EndsWith("widgets.proto", Uri.UnescapeDataString(service.Location.Uri), StringComparison.Ordinal);

        var rpc = Assert.Single(await WorkspaceSymbolsAsync("GetMembersForGroups"), s => s.Name == "GetMembersForGroups");
        Assert.Equal(LspSymbolKind.Method, rpc.Kind);
        Assert.Equal("widgets.WidgetService", rpc.ContainerName);
    }

    [Fact]
    public async Task ANestedDeclarationIsListedUnderTheMessageThatHoldsIt()
    {
        var placement = Assert.Single(await WorkspaceSymbolsAsync("Placement"), s => s.Name == "Placement");
        Assert.Equal(LspSymbolKind.Class, placement.Kind);
        Assert.Equal("widgets.Widget", placement.ContainerName);

        var channel = Assert.Single(await WorkspaceSymbolsAsync("Channel"), s => s.Name == "Channel");
        Assert.Equal(LspSymbolKind.Enum, channel.Kind);
        Assert.Equal("common", channel.ContainerName);
    }

    [Fact]
    public async Task TheSymbolsNameIsWhatTheLocationCovers()
    {
        var widget = Assert.Single(await WorkspaceSymbolsAsync("GetWidgetsByIdReply"), s => s.Name == "GetWidgetsByIdReply");

        // Picking an entry puts the caret on the declaration's name. A range over the whole message
        // would select its body instead, which is not what a picker is for.
        var source = SourceText.From(File.ReadAllText(FixturePaths.WidgetsProtoFile));
        Assert.Equal(
            "GetWidgetsByIdReply",
            source.ToString(LspConverters.ToTextSpan(source, widget.Location.Range)));
    }

    [Fact]
    public async Task FieldsAndEnumValuesAreNotInThePicker()
    {
        // Every schema names them the same way — id, name, created_at, UNSPECIFIED — so a query
        // that matched one would match forty and push the message somebody was looking for off the
        // end of the list. The document outline is what answers about them, one file at a time.
        Assert.DoesNotContain(await WorkspaceSymbolsAsync("label"), s => s.Name == "label");
        Assert.DoesNotContain(await WorkspaceSymbolsAsync("CHANNEL_ALPHA"), s => s.Name == "CHANNEL_ALPHA");

        // The enum itself is there, so the two absences above are a decision rather than a picker
        // that answers nothing for this file.
        Assert.Contains(await WorkspaceSymbolsAsync("Channel"), s => s.Name == "Channel");
    }

    [Fact]
    public async Task AnEmptyQueryIsNotAnInvitationToListTheSolution()
    {
        Assert.Empty(await WorkspaceSymbolsAsync(""));
        Assert.Empty(await WorkspaceSymbolsAsync("   "));
        Assert.Empty(await WorkspaceSymbolsAsync("NoSuchDeclarationAnywhere"));
    }

    // ---- workspace/diagnostic -----------------------------------------------------------------

    private static async Task<IReadOnlyList<object>> SweepAsync(
        Project project, IReadOnlyDictionary<string, string>? previous = null) =>
        await Pack().DiagnoseProjectAsync(project, previous ?? new Dictionary<string, string>(), default);

    private static Dictionary<string, string> ResultIds(IEnumerable<object> reports) =>
        reports
            .OfType<WorkspaceFullDocumentDiagnosticReport>()
            .Where(r => r.ResultId is not null)
            .ToDictionary(r => r.Uri, r => r.ResultId!);

    private static WorkspaceFullDocumentDiagnosticReport Full(IEnumerable<object> reports, string fileName) =>
        Assert.Single(
            reports.OfType<WorkspaceFullDocumentDiagnosticReport>(),
            r => Uri.UnescapeDataString(r.Uri).EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task EveryProtoTheProjectCompilesIsSweptWithoutBeingOpened()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.ProtoProjectFile);
        var reports = await SweepAsync(project);

        // A field renumbered in a contract nobody has open is a wire-breaking change no C# compiler
        // will ever complain about, so it has to reach Problems from a closed file.
        Assert.False(OpenDocumentStore.IsOpen(FixturePaths.WidgetsProtoFile));

        foreach (string file in (string[])["common/types.proto", "widgets/types.proto", "widgets/widgets.proto"])
        {
            var report = Full(reports, file);

            // The fixture compiles, so a clean sweep is the baseline every other report is worth
            // something against.
            Assert.Empty(report.Items);
            Assert.NotNull(report.ResultId);
        }
    }

    [Fact]
    public async Task AnUnchangedProjectAnswersUnchangedOnASecondSweep()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.ProtoProjectFile);

        var previous = ResultIds(await SweepAsync(project));
        Assert.NotEmpty(previous);

        var second = await SweepAsync(project, previous);

        // Re-parsing and re-diagnosing every schema in a solution on every sweep is what makes the
        // feature unusable, so the result id has to survive a round trip through the client.
        Assert.All(second, report => Assert.IsType<WorkspaceUnchangedDocumentDiagnosticReport>(report));
    }

    [Fact]
    public async Task EditingAnImportedFileReDiagnosesTheFilesThatCanSeeIt()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.ProtoProjectFile);
        var previous = ResultIds(await SweepAsync(project));

        string imported = FixturePaths.CommonTypesProtoFile;
        string edited = await File.ReadAllTextAsync(imported) + "\nmessage Added {\n  string value = 1;\n}\n";

        OpenDocumentStore.Open(_session, imported, SourceText.From(edited), 1);
        _buffers.Add(imported);

        var second = await SweepAsync(project, previous);

        // Half of what a .proto is diagnosed for is decided by the files it imports: a message
        // deleted from common/types.proto makes every reference to it in widgets/types.proto
        // unresolvable while the text of widgets/types.proto and the compilation both stand still.
        // A result id built from the file's own bytes alone would report it unchanged and leave a
        // squiggle on screen that is no longer true — or hide one that is.
        var importer = Full(second, "widgets/types.proto");
        Assert.Empty(importer.Items);

        // And the unsaved buffer is what was read, not the file behind it.
        Assert.NotNull(importer.ResultId);
        Assert.NotEqual(previous[importer.Uri], importer.ResultId!);
    }

    [Fact]
    public async Task AProjectThatHasNeverBeenBuiltIsToldSoOncePerFile()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.ProtoNeverBuiltProjectFile);

        var report = Full(await SweepAsync(project), "contracts.proto");

        // With no generated C# every navigation feature answers "nothing", and "nothing" reads
        // exactly like "nobody implements this service".
        var never = Assert.Single(report.Items, d => d.Code == "PROTO019");
        Assert.Equal(3, never.Severity);

        // Information and nothing louder, and nothing else at all: contracts.proto is valid
        // protobuf, so a warning here would put a permanent mark on a clean checkout.
        Assert.DoesNotContain(report.Items, d => d.Severity < 3);
    }

    [Fact]
    public async Task AProjectWithNoProtoInItIsSweptForNothing()
    {
        var project = await RoslynTestHelpers.OpenProjectAsync(FixturePaths.SampleProjectFile);

        // The sweep runs over every project in the solution on a timer. A project with no schema in
        // it has to cost a memoized lookup, not a walk of its directory tree.
        Assert.Empty(await SweepAsync(project));
    }
}

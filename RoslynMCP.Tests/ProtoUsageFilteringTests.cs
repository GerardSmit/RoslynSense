using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Languages.Proto.Tools;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Tests;

/// <summary>
/// What find-usages on a <c>.proto</c> declaration is allowed to answer with: the code a person
/// wrote, and never the code protoc wrote.
/// </summary>
/// <remarks>
/// <para>
/// One <c>string label = 3;</c> becomes fifteen mentions of <c>Label</c> in the file protoc emits —
/// the property, the copy constructor, <c>Clone</c>, <c>Equals</c>, <c>GetHashCode</c>,
/// <c>WriteTo</c>, <c>CalculateSize</c> and both <c>MergeFrom</c> overloads — every one of them
/// inside <c>#region Designer generated code</c>. Reported as usages they bury the one assignment
/// and the one read that a developer asking the question wanted, and <c>maxResults</c> then
/// truncates those away entirely. An rpc only looked like it worked because its generated mentions
/// are few enough to scroll past.
/// </para>
/// <para>
/// Hence the two halves pinned here, one per assertion group: a use inside a generated document is
/// not a use, and a <i>declaration</i> inside one is reported as the <c>.proto</c> line it was
/// generated from — the place the developer actually declared it, and the only place editing it has
/// any effect. Every kind the resolver can land on gets its own test, because each reaches the
/// generated code through a different binding and a filter that missed one of them would look like
/// a working feature for the other six.
/// </para>
/// <para>
/// <c>ProtoProject</c> carries the seven, being the only fixture that declares all of them.
/// <c>ProtoSolution</c> carries the one assertion the single-project layout cannot make: there,
/// whether a document is protoc's output has to be decided about projects other than the one the
/// caret is in.
/// </para>
/// </remarks>
[Collection(SharedState.Name)]
public class ProtoUsageFilteringTests
{
    // ---- The seven kinds a caret can be on ---------------------------------------------------

    [Fact]
    public async Task AMessageAnswersWithTheCodeThatBuildsItAndDeclaresItsProtoLine()
    {
        var (all, references) = await UsagesAsync(
            FixturePaths.WidgetTypesProtoFile, "message Widget {", "message ".Length);

        AssertNothingGenerated(all, await IndexAsync(FixturePaths.WidgetTypesProtoFile));

        // Both hand-written ends of the message: the server that builds one and the client that
        // takes one apart.
        AssertReference(references, FixturePaths.WidgetGrpcServiceFile, "var widget = new Widget");
        AssertReference(references, FixturePaths.WidgetClientCallerFile, "new Widget()");

        AssertProtoDeclaration(all, FixturePaths.WidgetTypesProtoFile, "Widget");
    }

    [Fact]
    public async Task AMessageFieldAnswersWithTheAssignmentAndTheReadRatherThanProtocsFifteenMentions()
    {
        // The sharpest case, and the one the owner reported as broken. Before the filter this came
        // back as a wall of protoc's own plumbing with the two real sites somewhere inside it.
        Assert.True(
            File.ReadLines(FixturePaths.WidgetTypesGeneratedFile).Count(
                line => line.Contains("Label", StringComparison.Ordinal)) > 10,
            "the generated fixture no longer mentions Label enough times for this test to mean anything");

        var (all, references) = await UsagesAsync(
            FixturePaths.WidgetTypesProtoFile, "label = 3", 0);

        AssertNothingGenerated(all, await IndexAsync(FixturePaths.WidgetTypesProtoFile));

        AssertReference(references, FixturePaths.WidgetGrpcServiceFile, "Label = \"widget-\" + id");
        AssertReference(references, FixturePaths.WidgetClientCallerFile, "labels.Add(widget.Label)");

        AssertProtoDeclaration(all, FixturePaths.WidgetTypesProtoFile, "label");
    }

    [Fact]
    public async Task AnEnumAnswersWithBothSitesThatNameItAndDeclaresTheProtoEnum()
    {
        var (all, references) = await UsagesAsync(
            FixturePaths.CommonTypesProtoFile, "enum Channel", "enum ".Length);

        AssertNothingGenerated(all, await IndexAsync(FixturePaths.CommonTypesProtoFile));

        AssertReference(references, FixturePaths.WidgetGrpcServiceFile, "Channel = Channel.Alpha");
        AssertReference(references, FixturePaths.WidgetClientCallerFile, "Channel = Channel.Beta");

        AssertProtoDeclaration(all, FixturePaths.CommonTypesProtoFile, "Channel");
    }

    [Fact]
    public async Task AnEnumValueAnswersWithTheOneSiteThatSetsItAndDeclaresTheProtoValue()
    {
        var (all, references) = await UsagesAsync(
            FixturePaths.CommonTypesProtoFile, "CHANNEL_ALPHA", 2);

        AssertNothingGenerated(all, await IndexAsync(FixturePaths.CommonTypesProtoFile));

        AssertReference(references, FixturePaths.WidgetGrpcServiceFile, "Channel = Channel.Alpha");

        // And nowhere else. The caller sets CHANNEL_BETA on the same enum one line below this one in
        // the .proto, so a search that answered per enum rather than per member would report it here
        // and look just as plausible.
        string[] files = [.. references.Select(PathOf).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        Assert.Equal(
            Path.GetFullPath(FixturePaths.WidgetGrpcServiceFile),
            Assert.Single(files),
            StringComparer.OrdinalIgnoreCase);

        AssertProtoDeclaration(all, FixturePaths.CommonTypesProtoFile, "CHANNEL_ALPHA");
    }

    [Fact]
    public async Task AOneofAnswersWithTheCaseChecksAndPointsAtNoneOfProtocsMembers()
    {
        var (all, references) = await UsagesAsync(
            FixturePaths.WidgetTypesProtoFile, "oneof image", "oneof ".Length);

        AssertNothingGenerated(all, await IndexAsync(FixturePaths.WidgetTypesProtoFile));

        // A oneof is three generated members at once — the case property, the case enum and the
        // clear method — and the switch below reaches two of them on one expression.
        AssertReference(references, FixturePaths.WidgetClientCallerFile, "widget.ImageCase switch");
        AssertReference(references, FixturePaths.WidgetClientCallerFile, "Widget.ImageOneofCase.ImageUrl");
        AssertReference(references, FixturePaths.WidgetClientCallerFile, "Widget.ImageOneofCase.ImageHash");

        // No declaration row, and deliberately none: a oneof leaves protoc no anchor in its output —
        // no descriptor index, no `…FieldNumber`, no `OriginalName` — so ProtoGeneratedIndex records
        // no way back from ImageCase to the `oneof` line, and the rule is to drop a generated
        // declaration it cannot place rather than fall back to the generated line. That fallback is
        // the whole thing this filter exists to prevent, so an empty answer here is the correct one.
        Assert.DoesNotContain(all, IsProto);
    }

    [Fact]
    public async Task AServiceAnswersWithBothEndsOfTheContractAndOneProtoLineForItsThreeClasses()
    {
        var (all, references) = await UsagesAsync(
            FixturePaths.WidgetsProtoFile, "service WidgetService", "service ".Length);

        AssertNothingGenerated(all, await IndexAsync(FixturePaths.WidgetsProtoFile));

        AssertReference(references, FixturePaths.WidgetGrpcServiceFile, "WidgetService.WidgetServiceBase");
        AssertReference(references, FixturePaths.WidgetClientCallerFile, "WidgetService.WidgetServiceClient");

        // The hand-written implementation is a declaration in the answer and stays one: it derives
        // from a generated base, but it is not generated code and it is the pack's best result.
        Assert.Contains(
            all.Where(location => SamePath(location, FixturePaths.WidgetGrpcServiceFile)),
            location => LineAt(FixturePaths.WidgetGrpcServiceFile, location.Range.Start.Line)
                .Contains("class WidgetGrpcService", StringComparison.Ordinal));

        // One row, not three. A service is a static holder, an abstract base and a client, and each
        // of the three is declared in generated code and maps back to this same `service` line.
        AssertProtoDeclaration(all, FixturePaths.WidgetsProtoFile, "WidgetService");
    }

    [Fact]
    public async Task AnRpcAnswersWithTheCallSitesAndTheOverrideAndOneProtoLineForItsFiveMethods()
    {
        var (all, references) = await UsagesAsync(
            FixturePaths.WidgetsProtoFile, "rpc GetWidgetsById", "rpc ".Length);

        AssertNothingGenerated(all, await IndexAsync(FixturePaths.WidgetsProtoFile));

        // Spelled GetWidgetsByIdAsync, a name written in neither the .proto nor the override: only
        // the client binding reaches it.
        AssertReference(
            references, FixturePaths.WidgetClientCallerFile, "_client.GetWidgetsByIdAsync(request)");

        Assert.Contains(
            all.Where(location => SamePath(location, FixturePaths.WidgetGrpcServiceFile)),
            location => LineAt(FixturePaths.WidgetGrpcServiceFile, location.Range.Start.Line)
                .Contains("override Task<GetWidgetsByIdReply> GetWidgetsById(", StringComparison.Ordinal));

        // The base's virtual, two client overloads and two async client overloads are five generated
        // declarations of one rpc, and they collapse to one line.
        AssertProtoDeclaration(all, FixturePaths.WidgetsProtoFile, "GetWidgetsById");
    }

    // ---- Dedup, where several generated members stand for one declaration ---------------------

    [Fact]
    public async Task AFieldWithPresenceMembersStillDeclaresExactlyOneProtoLine()
    {
        // image_url is a oneof member, which is the proto3 shape that gives a field explicit
        // presence: protoc emits HasImageUrl and ClearImageUrl beside the property, so the symbol
        // set for one `.proto` field is three declarations in the generated file.
        string generated = File.ReadAllText(FixturePaths.WidgetTypesGeneratedFile);
        Assert.Contains("public bool HasImageUrl", generated, StringComparison.Ordinal);
        Assert.Contains("public void ClearImageUrl()", generated, StringComparison.Ordinal);

        var (all, references) = await UsagesAsync(
            FixturePaths.WidgetTypesProtoFile, "image_url = 6", 0);

        AssertNothingGenerated(all, await IndexAsync(FixturePaths.WidgetTypesProtoFile));

        AssertReference(references, FixturePaths.WidgetGrpcServiceFile, "ImageUrl = \"https://");
        AssertReference(references, FixturePaths.WidgetClientCallerFile, "widget.ImageUrl");

        AssertProtoDeclaration(all, FixturePaths.WidgetTypesProtoFile, "image_url");
    }

    /// <summary>
    /// The same rule where the answers are in projects that generate nothing at all.
    /// </summary>
    /// <remarks>
    /// Whether a document is protoc's output is decided per project, so a result in Server or Client
    /// is put to an index that is <see cref="ProtoGeneratedIndex.Empty"/> — and an empty index
    /// claiming a file would delete every real answer the pack has while leaving the generated ones
    /// in place, which is the exact inverse of what this filter is for.
    /// </remarks>
    [Fact]
    public async Task AMessageAnsweredAcrossProjectsKeepsTheProjectsThatGenerateNothing()
    {
        var (all, references) = await UsagesAsync(
            FixturePaths.ProtoSolutionWidgetsProtoFile, "message Widget {", "message ".Length);

        var view = await ProtoWorkspace.GetAsync(FixturePaths.ProtoSolutionWidgetsProtoFile, default);
        Assert.NotNull(view);
        AssertNothingGenerated(all, view!.Index);

        AssertReference(references, FixturePaths.ProtoServerServiceFile, "new Widget { Id = id, Label =");
        AssertReference(references, FixturePaths.ProtoClientCallerFile, "Widget widget = call.ResponseStream.Current");

        AssertProtoDeclaration(all, FixturePaths.ProtoSolutionWidgetsProtoFile, "Widget");
    }

    // ---- The guard on the other direction ------------------------------------------------------

    [Fact]
    public async Task FindImplementationsStillReachesTheServerProjectThroughAGeneratedBase()
    {
        var view = await ProtoWorkspace.GetAsync(FixturePaths.ProtoSolutionWidgetsProtoFile, default);
        Assert.NotNull(view);

        // The premise of the guard: the base the implementation derives from and the method it
        // overrides are both declared in a document this index calls generated, so the filter had
        // every opportunity to swallow the answer on its way out.
        Assert.True(
            view!.Index.IsGenerated(FixturePaths.ProtoSolutionWidgetsGrpcGeneratedFile),
            "the fixture's gRPC stubs are no longer recognised as generated; the guard proves nothing");

        var service = await ImplementationAsync("service WidgetService", "service ".Length);
        var location = Assert.Single(service);
        AssertFile(FixturePaths.ProtoServerServiceFile, location);
        Assert.Contains(
            "class WidgetGrpcService",
            LineAt(FixturePaths.ProtoServerServiceFile, location.Range.Start.Line));

        var rpc = await ImplementationAsync("rpc GetWidgetsById", "rpc ".Length);
        var overriding = Assert.Single(rpc);
        AssertFile(FixturePaths.ProtoServerServiceFile, overriding);
        Assert.Contains(
            "override Task<GetWidgetsByIdReply> GetWidgetsById(",
            LineAt(FixturePaths.ProtoServerServiceFile, overriding.Range.Start.Line));
    }

    // ---- The two front-ends over one engine ----------------------------------------------------

    [Fact]
    public async Task TheMcpToolReportsExactlyWhatTheLspHandlerReturns()
    {
        // Two entry points, one ProtoReferenceService behind them. A filter applied in the LSP
        // provider alone would leave an AI session reading the wall of generated code the editor
        // stopped showing, which is the drift the shared engine exists to make impossible.
        string report = await new ProtoFindUsages(new MarkdownFormatter()).FindUsagesAsync(
            FixturePaths.WidgetTypesProtoFile, "string [|label|] = 3;", maxResults: 200, default);

        var locations = await ReferencesAsync(
            FixturePaths.WidgetTypesProtoFile, "label = 3", 0, includeDeclaration: true);

        var rows = ReportRows(report);

        Assert.Equal(
            locations
                .Select(location => Key(
                    PathOf(location), location.Range.Start.Line + 1, location.Range.Start.Character + 1))
                .OrderBy(key => key, StringComparer.Ordinal),
            rows
                .Select(row => Key(row.Path, row.Line, row.Column))
                .OrderBy(key => key, StringComparer.Ordinal));

        // Both halves of the rule, restated in the report's own terms rather than only in the set
        // above: the .proto line is listed, and protoc's output is not.
        Assert.Contains(
            rows,
            row => string.Equals(
                Path.GetFullPath(row.Path),
                Path.GetFullPath(FixturePaths.WidgetTypesProtoFile),
                StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(rows, row => HasSegment(row.Path, "Generated"));
    }

    // ---- Assertions ----------------------------------------------------------------------------

    /// <summary>
    /// That nothing in the answer is protoc's output, said twice.
    /// </summary>
    /// <remarks>
    /// By path because that is what a reader would check by eye, and by the index because that is
    /// what the product actually decides on: <c>Protobuf_OutputPath</c> points wherever the build
    /// points it, so a rule that only knew about <c>obj\</c> and <c>Generated\</c> would pass this
    /// while letting protoc's output through in any project laid out differently.
    /// </remarks>
    private static void AssertNothingGenerated(IEnumerable<LspLocation> locations, ProtoGeneratedIndex index)
    {
        foreach (var location in locations)
        {
            string path = PathOf(location);
            string name = Path.GetFileName(path);

            Assert.False(HasSegment(path, "Generated"), $"'{name}' is under Generated\\");
            Assert.False(HasSegment(path, "obj"), $"'{name}' is under obj\\");
            Assert.False(index.IsGenerated(path), $"'{name}' is a document the index calls generated");
            Assert.False(
                ProtoGeneratedIndex.IsKnownGenerated(path),
                $"'{name}' is generated according to some other project's index");
        }
    }

    /// <summary>One reference, in the file and on the line named — never merely a non-empty
    /// answer.</summary>
    private static void AssertReference(IEnumerable<LspLocation> references, string path, string code)
    {
        Assert.Contains(
            references.Where(location => SamePath(location, path)),
            location => LineAt(path, location.Range.Start.Line).Contains(code, StringComparison.Ordinal));
    }

    /// <summary>
    /// That the declaration in the answer is the <c>.proto</c> line, exactly one of them, and that
    /// its range covers the declaration's own name rather than its whole block.
    /// </summary>
    private static void AssertProtoDeclaration(IEnumerable<LspLocation> usages, string path, string name)
    {
        var location = Assert.Single(usages, IsProto);
        AssertFile(path, location);

        var text = SourceText.From(File.ReadAllText(path));
        Assert.Equal(name, text.ToString(LspConverters.ToTextSpan(text, location.Range)));
    }

    // ---- Driving the handlers --------------------------------------------------------------------

    /// <summary>
    /// The answer with declarations and the answer without, from one caret.
    /// </summary>
    /// <remarks>
    /// Both, because the protocol carries no flag on a location saying which it is: subtracting the
    /// second from the first is the only way a test can tell the client's call site from the server's
    /// <c>override</c>, and asking twice also pins that excluding declarations excludes the
    /// substituted <c>.proto</c> rows with them.
    /// </remarks>
    private static async Task<(LspLocation[] All, LspLocation[] References)> UsagesAsync(
        string path, string needle, int offsetIntoNeedle)
    {
        var all = await ReferencesAsync(path, needle, offsetIntoNeedle, includeDeclaration: true);
        var references = await ReferencesAsync(path, needle, offsetIntoNeedle, includeDeclaration: false);

        Assert.NotEmpty(all);
        Assert.DoesNotContain(references, IsProto);

        return (all, references);
    }

    private static Task<LspLocation[]> ReferencesAsync(
        string path, string needle, int offsetIntoNeedle, bool includeDeclaration) =>
        ProtoNavigationHandler.ReferencesAsync(
            new ReferenceParams(
                Doc(path),
                PositionOf(path, needle, offsetIntoNeedle),
                new ReferenceContext(includeDeclaration)),
            default);

    private static Task<LspLocation[]> ImplementationAsync(string needle, int offsetIntoNeedle) =>
        ProtoNavigationHandler.ImplementationAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.ProtoSolutionWidgetsProtoFile),
                PositionOf(FixturePaths.ProtoSolutionWidgetsProtoFile, needle, offsetIntoNeedle)),
            default);

    private static async Task<ProtoGeneratedIndex> IndexAsync(string protoPath)
    {
        var view = await ProtoWorkspace.GetAsync(protoPath, default);
        Assert.NotNull(view);
        Assert.False(view!.Index.IsEmpty, "the fixture produced no generated documents; the project failed to load");
        return view.Index;
    }

    private static TextDocumentIdentifier Doc(string path) => new(LspConverters.PathToUri(path));

    private static Position PositionOf(string path, string needle, int offsetIntoNeedle)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");

        var line = SourceText.From(text).Lines.GetLinePosition(index + offsetIntoNeedle);
        return new Position(line.Line, line.Character);
    }

    // ---- Reading the report back ------------------------------------------------------------------

    /// <summary>
    /// The located rows of a find-usages report: a level-4 header names a file and the table under
    /// it carries one 1-based line and column per usage.
    /// </summary>
    private static List<(string Path, int Line, int Column)> ReportRows(string report)
    {
        var rows = new List<(string, int, int)>();
        string? file = null;

        foreach (string raw in report.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.StartsWith("#### ", StringComparison.Ordinal))
            {
                file = line["#### ".Length..].Trim();
                continue;
            }

            if (file is null || !line.StartsWith('|'))
                continue;

            string[] cells = line.Split('|');
            if (cells.Length > 3
                && int.TryParse(cells[1].Trim(), out int row)
                && int.TryParse(cells[2].Trim(), out int column))
            {
                rows.Add((file, row, column));
            }
        }

        return rows;
    }

    private static string Key(string path, int line, int column) =>
        $"{Path.GetFullPath(path).ToUpperInvariant()}|{line}|{column}";

    // ---- Paths ------------------------------------------------------------------------------------

    private static bool IsProto(LspLocation location) =>
        PathOf(location).EndsWith(".proto", StringComparison.OrdinalIgnoreCase);

    private static string PathOf(LspLocation location) => LspConverters.UriToPath(location.Uri);

    private static bool SamePath(LspLocation location, string path) =>
        string.Equals(
            Path.GetFullPath(PathOf(location)), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    private static void AssertFile(string expected, LspLocation location) =>
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(PathOf(location)), StringComparer.OrdinalIgnoreCase);

    private static bool HasSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals(segment, StringComparison.OrdinalIgnoreCase));

    private static string LineAt(string path, int line) => File.ReadAllLines(path)[line];
}

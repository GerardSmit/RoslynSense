using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto;
using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using LspLocation = RoslynMCP.Lsp.Protocol.Location;

namespace RoslynMCP.Tests;

/// <summary>
/// The MCP side of the pack, asserted against the LSP side of it.
/// </summary>
/// <remarks>
/// <para>
/// The reason the pack owns both front-ends is that an AI session and the editor beside it must not
/// be able to disagree about the same file. A tool that quietly answered about a different
/// occurrence, a narrower search scope or an older parse would be worse than one that failed: the
/// person reading the answer has no way to tell. So these tests do not check that the tools return
/// something plausible — they check that what comes back is the same set of locations the editor's
/// own request returned for the same caret.
/// </para>
/// <para>
/// The one thing that genuinely differs is how the caret is named. An editor has a position; an AI
/// session quotes the line instead, which is what <c>[| |]</c> and <c>hintLine</c> are for.
/// </para>
/// </remarks>
[Collection(SharedState.Name)]
public class ProtoToolsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"proto-tools-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ProtoLanguage Pack() => new(new MarkdownFormatter());

    private static TextDocumentIdentifier Doc(string path) =>
        new(LspConverters.PathToUri(path));

    /// <summary>The position of <paramref name="needle"/> in the file, as an LSP position.</summary>
    private static Position PositionOf(string path, string needle, int offsetIntoNeedle = 0)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");

        var source = SourceText.From(text);
        var line = source.Lines.GetLinePosition(index + offsetIntoNeedle);
        return new Position(line.Line, line.Character);
    }

    // ---- find_usages --------------------------------------------------------------------------

    [Fact]
    public async Task FindUsagesReturnsExactlyTheLocationsTheEditorsReferencesRequestDoes()
    {
        string report = await Pack().FindUsagesAsync(
            FixturePaths.WidgetsProtoFile, "service [|WidgetService|] {", 500, default);

        var editor = await ProtoNavigationHandler.ReferencesAsync(
            new ReferenceParams(
                Doc(FixturePaths.WidgetsProtoFile),
                PositionOf(FixturePaths.WidgetsProtoFile, "service WidgetService", "service ".Length),
                new ReferenceContext(IncludeDeclaration: true)),
            default);

        // Non-empty first, so the equality below cannot pass by both sides failing.
        Assert.NotEmpty(editor);
        Assert.Equal(Locations(editor), UsagesIn(report));

        // A service is three generated classes and the hand-written server derives from one of
        // them, so an answer that stopped at the generated file would be the pack's whole point
        // missed. Both front-ends have to reach the implementation.
        Assert.Contains(
            Locations(editor),
            key => key.Contains("WidgetGrpcService.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AHintLineChoosesBetweenTwoFieldsWrittenExactlyTheSameWay()
    {
        var pack = Pack();

        // `repeated int64 ids = 1;` is written once in GetWidgetsByIdRequest and once in
        // WatchWidgetsRequest. They are different fields of different messages, so answering about
        // the wrong one is a wrong answer that looks exactly like a right one.
        const string snippet = "int64 [|ids|] = 1;";

        string first = await pack.FindUsagesAsync(
            FixturePaths.WidgetsProtoFile, snippet, 500, default, hintLine: 18);
        string second = await pack.FindUsagesAsync(
            FixturePaths.WidgetsProtoFile, snippet, 500, default, hintLine: 34);

        Assert.Contains("**Proto**: widgets.GetWidgetsByIdRequest.ids (Field)", first);
        Assert.Contains("**Proto**: widgets.WatchWidgetsRequest.ids (Field)", second);

        // Naming a different declaration is only half of it; the search has to have followed.
        Assert.NotEmpty(UsagesIn(first));
        Assert.NotEqual(UsagesIn(first), UsagesIn(second));

        // With nothing to choose between them the first match wins, which is the documented rule
        // and the reason the hint exists rather than a fallback worth relying on.
        string unhinted = await pack.FindUsagesAsync(FixturePaths.WidgetsProtoFile, snippet, 500, default);
        Assert.Equal(UsagesIn(first), UsagesIn(unhinted));
    }

    [Fact]
    public async Task ADeclarationNothingGeneratedSaysSoRatherThanReportingNoUsages()
    {
        // orphan.proto is valid and nothing compiles it. "No usages" on its own is
        // indistinguishable from "nobody implements this service", which is the wrong conclusion.
        string report = await Pack().FindUsagesAsync(
            FixturePaths.OrphanProtoFile, "service [|OrphanService|] {", 500, default);

        Assert.Contains("**Proto**: orphan.OrphanService (Service)", report);
        Assert.Contains("**Found**: 0 location(s)", report);
        Assert.Contains("not bound to any generated symbol", report);

        // The project has been built, so telling the reader to build it would send them to fix the
        // wrong thing — that message belongs to the other empty case.
        Assert.DoesNotContain("build it and run this again", report, StringComparison.OrdinalIgnoreCase);
    }

    // ---- go_to_definition ---------------------------------------------------------------------

    [Fact]
    public async Task GoToDefinitionOnAnRpcNameLandsWhereTheEditorWouldOpen()
    {
        string report = await Pack().ResolveAsync(
            FixturePaths.WidgetsProtoFile, "rpc [|GetWidgetsById|](", contextLines: 3, default);

        var editor = await ProtoNavigationHandler.DefinitionAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.WidgetsProtoFile),
                PositionOf(FixturePaths.WidgetsProtoFile, "rpc GetWidgetsById(", "rpc ".Length)),
            typeDefinition: false,
            default);

        // The caret is already on the declaration, so the answer is the code on the other end of it:
        // the hand-written override, not the abstract method protoc wrote into a file the next build
        // rewrites. Asserted here as well as in the LSP tests because an AI session and the editor
        // reading one caret differently is the drift the shared core exists to prevent.
        var location = Assert.Single(editor);
        Assert.EndsWith(
            "WidgetGrpcService.cs",
            LspConverters.UriToPath(location.Uri),
            StringComparison.OrdinalIgnoreCase);

        var (file, line) = DefinitionTargetIn(report);
        Assert.Equal(
            Path.GetFullPath(LspConverters.UriToPath(location.Uri)),
            Path.GetFullPath(file),
            ignoreCase: true);
        Assert.Equal(location.Range.Start.Line + 1, line);
    }

    // ---- get_file_outline ---------------------------------------------------------------------

    [Fact]
    public async Task TheOutlineListsTheServiceItsRpcsAndTheMessagesTheyCarry()
    {
        string outline = await Pack().GetOutlineAsync(FixturePaths.WidgetsProtoFile, default);

        Assert.Contains("- **service WidgetService**", outline);
        Assert.Contains(
            "- **rpc** `GetWidgetsById(GetWidgetsByIdRequest) returns (GetWidgetsByIdReply)`",
            outline);

        // `stream` is a flag on the rpc rather than part of the type it qualifies, so an outline
        // that lost it would show a server-streaming call as a plain unary one.
        Assert.Contains(
            "- **rpc** `WatchWidgets(WatchWidgetsRequest) returns (stream WidgetEvent)`",
            outline);

        Assert.Contains("- **message GetWidgetsByIdRequest**", outline);

        // A map is one type in the source and two in the parse; putting it back together is what
        // makes the entry readable at all.
        Assert.Contains("`map<int64, GroupMemberList> group_members = 1`", outline);

        // Each entry carries the C# it bound to, which is the answer the pack exists to give and
        // the reason an outline is worth asking for rather than opening the file. A service binds
        // to the static holder protoc names after it, and an rpc to the virtual method on the base.
        Assert.EndsWith("WidgetService", BindingFor(outline, "**service WidgetService**"), StringComparison.Ordinal);
        Assert.EndsWith("GetWidgetsByIdRequest", BindingFor(outline, "**message GetWidgetsByIdRequest**"), StringComparison.Ordinal);
        Assert.Equal("GetWidgetsById", BindingFor(outline, "**rpc** `GetWidgetsById("));
    }

    [Fact]
    public async Task TheOutlineNestsAMessagesOwnTypesAndItsOneofUnderIt()
    {
        string outline = await Pack().GetOutlineAsync(FixturePaths.WidgetTypesProtoFile, default);

        Assert.Contains("- **message Widget**", outline);

        // Two levels of indentation, because a oneof groups the fields written inside it and the
        // typed Fields collection of the message deliberately excludes them: printing the two
        // lists one after the other would show the message's fields in an order it does not have.
        Assert.Contains("  - **oneof image**", outline);
        Assert.Contains("    - `string image_url = 6` ", outline);

        // protoc puts a message's nested declarations inside a `Types` container and indexes them
        // separately from the top-level ones, so these two are the binder reading `.NestedTypes[N]`
        // and `Descriptor.EnumTypes[N]` rather than guessing a name.
        Assert.Contains("  - **message Placement**", outline);
        Assert.EndsWith("Widget.Types.Placement", BindingFor(outline, "**message Placement**"), StringComparison.Ordinal);
        Assert.Contains("  - **enum Visibility**", outline);
        Assert.EndsWith("Widget.Types.Visibility", BindingFor(outline, "**enum Visibility**"), StringComparison.Ordinal);

        // `message Note { string note = 1; }` would give the class a property with its own name,
        // so protoc emits `Note_`. Nothing here predicts that rule — the binding is read back off
        // the generated member, which is the only way it survives protoc changing its mind.
        Assert.Equal("Note_", BindingFor(outline, "`string note = 1`"));
    }

    // ---- validate -----------------------------------------------------------------------------

    [Fact]
    public async Task ValidationReportsExactlyWhatTheEditorWouldSquiggle()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "broken.proto");
        await File.WriteAllTextAsync(path, """
            syntax = "proto3";

            package temp;

            message Broken {
              string first = 1;
              Missing second = 1;
            }

            enum Mood {
              MOOD_HAPPY = 1;
            }
            """);

        var editor = await ProtoDiagnosticsHandler.DiagnosticsAsync(path, default);
        string report = await Pack().ValidateAsync(path, new MarkdownFormatter(), default);

        // Three different faults, so the comparison below is comparing something. The ids are the
        // ones a user would suppress, which is why they have to survive the trip through the tool.
        var reported = DiagnosticsIn(report);
        Assert.Contains(reported, key => key.StartsWith("PROTO012@7:", StringComparison.Ordinal));
        Assert.Contains(reported, key => key.StartsWith("PROTO013@7:", StringComparison.Ordinal));
        Assert.Contains(reported, key => key.StartsWith("PROTO018@11:", StringComparison.Ordinal));

        Assert.Equal(
            Sorted(editor
                .Select(d => $"{d.Code}@{d.Range.Start.Line + 1}:{d.Range.Start.Character + 1}")
                .ToHashSet(StringComparer.Ordinal)),
            reported);

        Assert.Contains($"**Errors**: {editor.Count(d => d.Severity == 1)}", report);
        Assert.Contains($"**Warnings**: {editor.Count(d => d.Severity == 2)}", report);
    }

    [Fact]
    public async Task AFixtureFileWhoseImportsAllResolveIsCleanInBothFrontEnds()
    {
        // widgets.proto imports google/protobuf/timestamp.proto, which is not on this machine —
        // protoc ships it inside Grpc.Tools. Reporting it would put a permanent warning on every
        // file that uses a Timestamp, and a wall of red on a building solution is how a rule gets
        // switched off for good. The cross-file `common/types.proto` beside it must resolve.
        Assert.Empty(await ProtoDiagnosticsHandler.DiagnosticsAsync(FixturePaths.WidgetsProtoFile, default));

        string report = await Pack().ValidateAsync(
            FixturePaths.WidgetsProtoFile, new MarkdownFormatter(), default);

        Assert.Empty(DiagnosticsIn(report));
        Assert.Contains("None — the file parses and every name in it resolves.", report);
        Assert.Contains("**Errors**: 0", report);
        Assert.Contains("**Warnings**: 0", report);
    }

    // ---- Reading the reports back -------------------------------------------------------------

    /// <summary>
    /// The C# name the outline shows beside the entry whose label contains <paramref name="label"/>.
    /// </summary>
    /// <remarks>
    /// Read off the line rather than matched as a whole string, because how a symbol spells itself
    /// is Roslyn's business and not this pack's — what the outline has to get right is which entry
    /// carries which binding.
    /// </remarks>
    private static string BindingFor(string outline, string label)
    {
        string line = Assert.Single(
            outline.Split('\n').Select(candidate => candidate.TrimEnd('\r')),
            candidate => candidate.Contains(label, StringComparison.Ordinal));

        int arrow = line.IndexOf("→ `", StringComparison.Ordinal);
        Assert.True(arrow >= 0, $"'{label}' is listed with no C# bound to it: {line}");

        return line[(arrow + 3)..].TrimEnd('`');
    }

    /// <summary>
    /// Every usage the find-usages report names, as <c>&lt;path&gt;|&lt;line&gt;:&lt;column&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The report groups rows under a level-4 header carrying the file's path, so the current
    /// header is the file every row below it belongs to. Definitions and references are two
    /// sections of the same shape and both are collected: the editor's request was made with
    /// <c>includeDeclaration</c>, so its answer holds both too.
    /// </remarks>
    private static string[] UsagesIn(string report)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? file = null;

        foreach (string raw in report.Split('\n'))
        {
            string line = raw.Trim();

            if (line.StartsWith("#### ", StringComparison.Ordinal))
            {
                file = Canonical(line["#### ".Length..].Trim());
                continue;
            }

            if (file is null || !line.StartsWith('|'))
                continue;

            string[] cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length > 3 && int.TryParse(cells[1], out int number) && int.TryParse(cells[2], out int column))
                found.Add($"{file}|{number}:{column}");
        }

        return Sorted(found);
    }

    /// <summary>The same set, from the editor's answer.</summary>
    private static string[] Locations(IEnumerable<LspLocation> locations) =>
        Sorted(locations
            .Select(location =>
                $"{Canonical(LspConverters.UriToPath(location.Uri))}|" +
                $"{location.Range.Start.Line + 1}:{location.Range.Start.Character + 1}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>One spelling for a path, so that comparing the two front-ends compares locations
    /// rather than how each of them happened to case a drive letter.</summary>
    private static string Canonical(string path) => Path.GetFullPath(path).ToLowerInvariant();

    /// <summary>Deduplicated and ordered, so that comparing two of these compares sets and reads
    /// as a diff when it fails.</summary>
    private static string[] Sorted(HashSet<string> keys) =>
        [.. keys.Order(StringComparer.Ordinal)];

    /// <summary>Every diagnostic the validation report names, as <c>&lt;code&gt;@&lt;line&gt;:&lt;column&gt;</c>.</summary>
    private static string[] DiagnosticsIn(string report)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (string raw in report.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith('|'))
                continue;

            string[] cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length > 4 && int.TryParse(cells[3], out int number) && int.TryParse(cells[4], out int column))
                found.Add($"{cells[2]}@{number}:{column}");
        }

        return Sorted(found);
    }

    /// <summary>The file and 1-based line the go-to-definition report sends the reader to.</summary>
    private static (string File, int Line) DefinitionTargetIn(string report)
    {
        string? file = null;

        foreach (string raw in report.Split('\n'))
        {
            string line = raw.Trim().TrimStart('-').Trim();

            if (line.StartsWith("**File**:", StringComparison.Ordinal))
            {
                file = line["**File**:".Length..].Trim();
                continue;
            }

            if (file is null
                || !(line.StartsWith("**Line**:", StringComparison.Ordinal)
                     || line.StartsWith("**Lines**:", StringComparison.Ordinal)))
            {
                continue;
            }

            string value = line[(line.IndexOf(':') + 1)..].Trim();
            int end = 0;
            while (end < value.Length && char.IsAsciiDigit(value[end]))
                end++;

            return (file, int.Parse(value[..end]));
        }

        Assert.Fail($"The report named no definition location:{Environment.NewLine}{report}");
        return (string.Empty, 0);
    }
}

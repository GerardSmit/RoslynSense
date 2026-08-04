using System.Globalization;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Languages.Proto.Lsp;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Tests;

/// <summary>
/// The LSP entry points for a <c>.proto</c>, end to end through the real workspace.
/// </summary>
/// <remarks>
/// The fixture's <c>Generated\</c> tree is protoc output committed as ordinary source, so every
/// binding here is read out of the anchors protoc really leaves — the <c>source:</c> header, the
/// descriptor index, the <c>…FieldNumber</c> constants, the <c>OriginalName</c> attributes and
/// <c>__ServiceName</c>. That makes these tests cover the ownership lookup and the import roots as
/// well, which a parse-only test skips.
/// </remarks>
[Collection(SharedState.Name)]
public class ProtoLspTests
{
    private static TextDocumentIdentifier Doc(string path) =>
        new(LspConverters.PathToUri(path));

    private static ProtoLanguage Pack() => new(new MarkdownFormatter());

    /// <summary>The position of <paramref name="needle"/> in the file, as an LSP position.</summary>
    private static Position PositionOf(string path, string needle, int offsetIntoNeedle = 0)
    {
        string text = File.ReadAllText(path);
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");

        var line = SourceText.From(text).Lines.GetLinePosition(index + offsetIntoNeedle);
        return new Position(line.Line, line.Character);
    }

    // ---- The four answers the pack exists to give -------------------------------------------

    [Fact]
    public async Task GoToImplementationOnAServiceLandsOnTheHandWrittenServerClass()
    {
        // The whole point of the pack. Nothing in the .proto names WidgetGrpcService, and nothing
        // in WidgetGrpcService names widgets.proto: the two are joined only through the base class
        // protoc generated, which is what the binder had to find first.
        var locations = await ProtoNavigationHandler.ImplementationAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.WidgetsProtoFile),
                PositionOf(FixturePaths.WidgetsProtoFile, "service WidgetService", "service ".Length)),
            default);

        var location = Assert.Single(locations);
        AssertFile(FixturePaths.WidgetGrpcServiceFile, location.Uri);
        Assert.Contains(
            "class WidgetGrpcService",
            LineAt(FixturePaths.WidgetGrpcServiceFile, location.Range.Start.Line));
    }

    [Fact]
    public async Task GoToImplementationOnAnRpcLandsOnTheOverrideThatImplementsIt()
    {
        var locations = await ProtoNavigationHandler.ImplementationAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.WidgetsProtoFile),
                PositionOf(FixturePaths.WidgetsProtoFile, "rpc GetWidgetsById", "rpc ".Length)),
            default);

        var location = Assert.Single(locations);
        AssertFile(FixturePaths.WidgetGrpcServiceFile, location.Uri);

        // The override itself, not the class holding it: an implementation of a three-rpc service
        // has three of them, and a caret on one rpc is a question about one.
        string line = LineAt(FixturePaths.WidgetGrpcServiceFile, location.Range.Start.Line);
        Assert.Contains("override", line);
        Assert.Contains("GetWidgetsById(", line);
    }

    [Fact]
    public async Task FindReferencesOnAnRpcReportsTheOverrideAndTheClientCallSiteTogether()
    {
        var locations = await References(
            FixturePaths.WidgetsProtoFile, "rpc GetWidgetsById", "rpc ".Length, includeDeclaration: true);

        string[] files = [.. locations.Select(l => FileName(l.Uri))];

        // One rpc is several C# symbols — a virtual on the base, its overrides, and four client
        // overloads — and searching any one of them alone reports half the answer. Losing either
        // of these is the under-reporting the pack exists to fix.
        Assert.Contains("WidgetGrpcService.cs", files);
        Assert.Contains("WidgetClientCaller.cs", files);

        // The call site is spelled GetWidgetsByIdAsync, which is a name that appears in neither the
        // .proto nor the override: only the client binding reaches it.
        Assert.Contains(
            locations.Where(l => FileName(l.Uri) == "WidgetClientCaller.cs"),
            l => LineAt(FixturePaths.WidgetClientCallerFile, l.Range.Start.Line)
                .Contains("GetWidgetsByIdAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FindReferencesOnAServiceIncludesTheClassThatDerivesFromItsBase()
    {
        var locations = await References(
            FixturePaths.WidgetsProtoFile, "service WidgetService", "service ".Length,
            includeDeclaration: true);

        string[] files = [.. locations.Select(l => FileName(l.Uri))];

        Assert.Contains("WidgetGrpcService.cs", files);

        // The consumer of the generated client is on the other end of the same contract, and the
        // search is solution-wide precisely so both ends come back.
        Assert.Contains("WidgetClientCaller.cs", files);
    }

    // ---- The rest of the navigation table ---------------------------------------------------

    [Fact]
    public async Task GoToDefinitionOnATypeReferenceOpensTheProtoThatDeclaresIt()
    {
        var locations = await Definition(
            FixturePaths.WidgetsProtoFile, "common.UUID deleted_uuid", 3, typeDefinition: false);

        // The .proto, not the class protoc generated from it: the author of a proto pressing F12 on
        // a type name is asking where the message is declared, and the generated class is a build
        // artefact they did not write.
        var location = Assert.Single(locations);
        AssertFile(FixturePaths.CommonTypesProtoFile, location.Uri);
        Assert.Contains(
            "message UUID",
            LineAt(FixturePaths.CommonTypesProtoFile, location.Range.Start.Line));
    }

    [Fact]
    public async Task TypeDefinitionOnATypeReferenceReachesTheGeneratedClassInstead()
    {
        var locations = await Definition(
            FixturePaths.WidgetsProtoFile, "common.UUID deleted_uuid", 3, typeDefinition: true);

        // The other gesture, for the reader who came from C# and wants the class.
        var location = Assert.Single(locations);
        AssertFile(FixturePaths.CommonTypesGeneratedFile, location.Uri);
        Assert.Contains(
            "class UUID",
            LineAt(FixturePaths.CommonTypesGeneratedFile, location.Range.Start.Line));
    }

    [Fact]
    public async Task GoToDefinitionOnAnImportOpensTheFileItNames()
    {
        var locations = await Definition(
            FixturePaths.WidgetsProtoFile, "\"common/types.proto\"", 1, typeDefinition: false);

        // An import path is resolved against the proto root rather than the importing file's own
        // directory, so "common/types.proto" written in widgets/ means the sibling folder. That is
        // the one thing in the file a reader cannot follow by eye.
        var location = Assert.Single(locations);
        AssertFile(FixturePaths.CommonTypesProtoFile, location.Uri);
        Assert.Equal(0, location.Range.Start.Line);
    }

    [Fact]
    public async Task EveryResolvableImportIsAClickableLinkOverTheQuotedPath()
    {
        var links = await Pack().DocumentLinksAsync(
            new DocumentLinkParams(Doc(FixturePaths.WidgetsProtoFile)), default);

        var toCommon = Assert.Single(
            links, l => l.Target is not null && SamePath(l.Target, FixturePaths.CommonTypesProtoFile));
        Assert.Single(
            links, l => l.Target is not null && SamePath(l.Target, FixturePaths.WidgetTypesProtoFile));

        // The quotes are part of the underline: the click target a user aims at is the string they
        // see, and an underline stopping inside the quotes reads as a rendering mistake.
        var source = SourceText.From(File.ReadAllText(FixturePaths.WidgetsProtoFile));
        Assert.Equal(
            "\"common/types.proto\"",
            source.ToString(LspConverters.ToTextSpan(source, toCommon.Range)));
    }

    [Fact]
    public async Task GoToDefinitionOnAFieldNameLandsOnTheGeneratedProperty()
    {
        var locations = await Definition(
            FixturePaths.WidgetTypesProtoFile, "label = 3", 0, typeDefinition: false);

        // Bound through the LabelFieldNumber constant beside the property, not by guessing that
        // label becomes Label: renaming a proto field is source-compatible and renumbering one is a
        // wire break, so the number is the field's identity and the name is not.
        var location = Assert.Single(locations);
        AssertFile(FixturePaths.WidgetTypesGeneratedFile, location.Uri);
        Assert.Contains(
            "string Label",
            LineAt(FixturePaths.WidgetTypesGeneratedFile, location.Range.Start.Line));
    }

    [Fact]
    public async Task GoToDefinitionOnAnEnumValueLandsOnTheMemberProtocRenamedItTo()
    {
        var locations = await Definition(
            FixturePaths.CommonTypesProtoFile, "CHANNEL_ALPHA", 2, typeDefinition: false);

        var location = Assert.Single(locations);
        AssertFile(FixturePaths.CommonTypesGeneratedFile, location.Uri);

        // protoc strips the enum-name prefix, so CHANNEL_ALPHA is Alpha in C# and nothing about the
        // proto name survives into the member name. The OriginalName attribute on the same line is
        // what the binding was read off.
        string line = LineAt(FixturePaths.CommonTypesGeneratedFile, location.Range.Start.Line);
        Assert.Contains("Alpha = 1", line);
        Assert.Contains("OriginalName(\"CHANNEL_ALPHA\")", line);
    }

    // ---- Document structure -----------------------------------------------------------------

    [Fact]
    public async Task TheOutlineNestsEveryRpcUnderItsService()
    {
        var symbols = await ProtoNavigationHandler.DocumentSymbolAsync(
            new DocumentSymbolParams(Doc(FixturePaths.WidgetsProtoFile)), default);

        var service = Root(symbols, "WidgetService");

        // Interface, because a service is the contract a hand-written class implements — which is
        // what find-implementations goes looking for and what the icon should say.
        Assert.Equal(LspSymbolKind.Interface, service.Kind);

        Assert.Equal(
            new[] { "GetWidgetsById", "GetMembersForGroups", "WatchWidgets" },
            service.Children.Select(c => c.Name).ToArray());
        Assert.All(service.Children, c => Assert.Equal(LspSymbolKind.Method, c.Kind));

        // The detail is the only place the two message types appear, and the only place `stream`
        // does: without it a server-streaming rpc is indistinguishable from a unary one.
        Assert.Equal(
            "(WatchWidgetsRequest) returns (stream WidgetEvent)",
            Child(service, "WatchWidgets").Detail);
    }

    [Fact]
    public async Task TheOutlineNestsFieldsOneofsAndNestedTypesUnderTheirMessage()
    {
        var symbols = await ProtoNavigationHandler.DocumentSymbolAsync(
            new DocumentSymbolParams(Doc(FixturePaths.WidgetTypesProtoFile)), default);

        var widget = Root(symbols, "Widget");
        Assert.Equal(LspSymbolKind.Class, widget.Kind);

        string[] children = [.. widget.Children.Select(c => c.Name)];
        Assert.Contains("id", children);
        Assert.Contains("attributes", children);
        Assert.Contains("Placement", children);
        Assert.Contains("Visibility", children);

        Assert.Equal(LspSymbolKind.Field, Child(widget, "id").Kind);
        Assert.Equal(LspSymbolKind.Class, Child(widget, "Placement").Kind);
        Assert.Equal(LspSymbolKind.Enum, Child(widget, "Visibility").Kind);
        Assert.Equal(
            new[] { "VISIBILITY_UNSPECIFIED", "VISIBILITY_PRIVATE", "VISIBILITY_PUBLIC" },
            Child(widget, "Visibility").Children.Select(c => c.Name).ToArray());

        // A map is one field in the source and two type references in the parse; the detail is
        // where it is put back together into something a reader recognises.
        Assert.Equal("map<string, string> = 8", Child(widget, "attributes").Detail);

        // A oneof's members are numbered in the enclosing message's space, so they are parented on
        // the message — listing them from the parent links would show each of them twice, once
        // under the oneof they are written in and once beside it.
        var image = Child(widget, "image");
        Assert.Equal(LspSymbolKind.Object, image.Kind);
        Assert.Equal(
            new[] { "image_url", "image_hash" },
            image.Children.Select(c => c.Name).ToArray());
        Assert.DoesNotContain("image_url", children);
    }

    [Fact]
    public async Task AnOutlineEntrySelectsItsOwnNameRatherThanItsWholeDeclaration()
    {
        var symbols = await ProtoNavigationHandler.DocumentSymbolAsync(
            new DocumentSymbolParams(Doc(FixturePaths.WidgetsProtoFile)), default);

        var source = SourceText.From(File.ReadAllText(FixturePaths.WidgetsProtoFile));

        foreach (var symbol in Flatten(symbols))
        {
            // Picking an entry puts the caret on the word rather than selecting the body, and the
            // protocol requires the selection range to sit inside the full range — a client drops
            // the whole response when it does not.
            Assert.Equal(
                symbol.Name,
                source.ToString(LspConverters.ToTextSpan(source, symbol.SelectionRange)));
            Assert.NotEqual(symbol.Range, symbol.SelectionRange);
            Assert.True(
                Encloses(symbol.Range, symbol.SelectionRange),
                $"'{symbol.Name}' selects outside its own range");
        }
    }

    [Fact]
    public async Task FoldingCoversTheBodyOfEachDeclarationAndNothingWithoutOne()
    {
        var ranges = await ProtoNavigationHandler.FoldingRangeAsync(
            new FoldingRangeParams(Doc(FixturePaths.WidgetsProtoFile)), default);

        foreach (string header in
                 (string[])["service WidgetService", "message WidgetEvent", "enum Kind", "oneof payload"])
        {
            var (start, end) = BodyLines(FixturePaths.WidgetsProtoFile, header);
            Assert.Contains(ranges, r => r.StartLine == start && r.EndLine == end);
        }

        // From the opening brace, so the declaration's own line stays on screen when it collapses.
        // A field and a body-less rpc have no braces and so nothing to fold: offering a range there
        // puts a chevron in the gutter that does nothing.
        foreach (string bodyless in (string[])["rpc GetWidgetsById", "Kind kind = 1;"])
        {
            int line = LineOf(FixturePaths.WidgetsProtoFile, bodyless);
            Assert.DoesNotContain(ranges, r => r.StartLine == line);
        }
    }

    // ---- Diagnostics ------------------------------------------------------------------------

    [Fact]
    public async Task ACorrectProtoInABuiltProjectReportsNothingAtAll()
    {
        // The baseline every other diagnostic is worth something against: a contract that compiles
        // must come back clean, or the ones that do not are lost in the noise.
        var diagnostics = await ProtoDiagnosticsHandler.DiagnosticsAsync(
            FixturePaths.WidgetsProtoFile, default);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task TwoFieldsClaimingOneWireNumberAreReportedOnTheSecondOne()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            message Duplicated {
              string first = 1;
              string second = 1;
            }
            """;

        await WithScratchProtoAsync("duplicate.proto", Proto, async path =>
        {
            var diagnostics = await ProtoDiagnosticsHandler.DiagnosticsAsync(path, default);

            var report = Assert.Single(diagnostics);
            Assert.Equal("PROTO013", report.Code);

            // An error: protoc rejects it, and it is decidable from this file alone — nothing about
            // where the build's proto roots are could make it legal.
            Assert.Equal(1, report.Severity);

            // On the number that collides and naming the field that already claimed it, which is
            // the half of the answer a reader does not have on screen.
            Assert.Contains("first", report.Message);
            Assert.Equal(LineOf(path, "string second = 1;"), report.Range.Start.Line);
        });
    }

    [Fact]
    public async Task AnImportThatNamesNoFileIsReportedOnThePathItself()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            import "nowhere/missing.proto";

            message Uses {
              string value = 1;
            }
            """;

        await WithScratchProtoAsync("missing-import.proto", Proto, async path =>
        {
            var diagnostics = await ProtoDiagnosticsHandler.DiagnosticsAsync(path, default);

            var report = Assert.Single(diagnostics);
            Assert.Equal("PROTO011", report.Code);

            // A warning rather than an error, deliberately: the per-item ProtoRoot metadata MSBuild
            // hands protoc is invisible from here, so a project that sets one compiles cleanly while
            // every import in it looks missing — and a wall of red on a building solution is how a
            // rule gets switched off for good.
            Assert.Equal(2, report.Severity);
            Assert.Equal(LineOf(path, "import "), report.Range.Start.Line);
        });
    }

    [Fact]
    public async Task ATypeNameNothingDeclaresIsReported()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            message Known {
              string value = 1;
            }

            message Uses {
              Known good = 1;
              Knwon typo = 2;
            }
            """;

        await WithScratchProtoAsync("unresolved-type.proto", Proto, async path =>
        {
            var diagnostics = await ProtoDiagnosticsHandler.DiagnosticsAsync(path, default);

            // Exactly one, which is the assertion that matters as much as the report itself: the
            // name one letter away from the typo resolves, so this is protobuf's own name lookup
            // and not a pattern that fires on every dotted word in the file.
            var report = Assert.Single(diagnostics);
            Assert.Equal("PROTO012", report.Code);
            Assert.Contains("Knwon", report.Message);
            Assert.Equal(LineOf(path, "Knwon typo"), report.Range.Start.Line);

            // A warning for the same reason an unresolved import is one: the roots this can see are
            // not necessarily the roots protoc was given.
            Assert.Equal(2, report.Severity);
        });
    }

    [Fact]
    public async Task AProtoNothingHasBeenGeneratedFromSaysSoWithoutMarkingTheFileWrong()
    {
        // A project whose index is empty outright, not ProtoProject's orphan: the report is gated
        // on the whole project having generated nothing, because a per-file gate would mark a
        // linked-in .proto whose source: header failed to resolve as never built when it is
        // building fine. ProtoProject generates C# for its other files, so no file in it can
        // reach this rule.
        var diagnostics = await ProtoDiagnosticsHandler.DiagnosticsAsync(
            FixturePaths.ContractsProtoFile, default);

        // With no generated C# every navigation feature answers "nothing", and "nothing" reads
        // exactly like "nobody implements this service". Saying it once is the difference between a
        // pack that looks broken and one that says what it needs.
        var report = Assert.Single(diagnostics, d => d.Code == "PROTO019");

        // Information, never a warning and never an error. The schema is correct and the build
        // simply has not run; anything louder puts a permanent red mark on a clean checkout, which
        // is how the whole rule ends up suppressed.
        Assert.Equal(3, report.Severity);

        // And nothing else: contracts.proto is valid protobuf, so reporting a fault in it would
        // send the reader to fix a file that compiles.
        Assert.DoesNotContain(diagnostics, d => d.Severity < 3);
    }

    [Fact]
    public async Task AProtoInAProjectThatHasBeenBuiltIsNeverToldToBuildItEvenWhenNothingBoundToIt()
    {
        // orphan.proto binds to nothing, but its project has been built. Reporting "build this"
        // here would be the false positive the project-wide gate exists to prevent: a .proto whose
        // generated counterpart the binder failed to match would send the user to run a build that
        // has already run and will change nothing.
        var diagnostics = await ProtoDiagnosticsHandler.DiagnosticsAsync(
            FixturePaths.OrphanProtoFile, default);

        Assert.DoesNotContain(diagnostics, d => d.Code == "PROTO019");
    }

    // ---- codeLens ---------------------------------------------------------------------------

    [Fact]
    public async Task TheServiceLensCountsWhatGoToImplementationFinds()
    {
        var implementations = await ProtoNavigationHandler.ImplementationAsync(
            new TextDocumentPositionParams(
                Doc(FixturePaths.WidgetsProtoFile),
                PositionOf(FixturePaths.WidgetsProtoFile, "service WidgetService", "service ".Length)),
            default);

        var lens = await ResolvedLensOn(FixturePaths.WidgetsProtoFile, "service WidgetService");

        // A gutter count that disagrees with the peek window behind it is worse than either alone:
        // the number is the only thing on screen, and the list is what a click opens.
        Assert.Equal(implementations.Length, Count(lens));

        // One implementation, said in the singular. A lens is read at a glance and "1
        // implementations" is the kind of thing a reader stops on.
        Assert.Single(implementations);
        Assert.Equal("1 implementation", lens.Title);
        Assert.Equal("roslynSense.showReferences", lens.Name);
    }

    [Fact]
    public async Task TheRpcLensCountsWhatFindReferencesFinds()
    {
        // Definitions excluded on both sides: an rpc has several by construction — the base's
        // virtual, the client's overloads and every hand-written override — so counting them would
        // read "5 references" over an rpc nobody calls.
        var references = await References(
            FixturePaths.WidgetsProtoFile, "rpc GetWidgetsById", "rpc ".Length,
            includeDeclaration: false);

        var lens = await ResolvedLensOn(FixturePaths.WidgetsProtoFile, "rpc GetWidgetsById");

        Assert.Equal(references.Length, Count(lens));
        Assert.NotEmpty(references);
    }

    [Fact]
    public async Task EveryServiceAndEveryRpcGetsALensAndNothingElseDoes()
    {
        var lenses = await Pack().CodeLensAsync(
            new CodeLensParams(Doc(FixturePaths.WidgetsProtoFile)), default);

        int[] lines = [.. lenses.Select(l => l.Range.Start.Line).Order()];

        int[] expected =
        [
            .. new[] { "service WidgetService", "rpc GetWidgetsById", "rpc GetMembersForGroups", "rpc WatchWidgets" }
                .Select(header => LineOf(FixturePaths.WidgetsProtoFile, header))
                .Order(),
        ];

        // A message is a generated class nothing derives from and a field is a generated property,
        // so neither has an answer worth a line in the gutter.
        Assert.Equal(expected, lines);

        // Counting is deferred: codeLens is re-requested on every edit and every scroll, and each
        // count is a solution-wide SymbolFinder sweep.
        Assert.All(lenses, l => Assert.Null(l.Command));
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private static Task<Location[]> Definition(
        string path, string needle, int offsetIntoNeedle, bool typeDefinition) =>
        ProtoNavigationHandler.DefinitionAsync(
            new TextDocumentPositionParams(Doc(path), PositionOf(path, needle, offsetIntoNeedle)),
            typeDefinition,
            default);

    private static Task<Location[]> References(
        string path, string needle, int offsetIntoNeedle, bool includeDeclaration) =>
        ProtoNavigationHandler.ReferencesAsync(
            new ReferenceParams(
                Doc(path),
                PositionOf(path, needle, offsetIntoNeedle),
                new ReferenceContext(includeDeclaration)),
            default);

    /// <summary>The command a lens on <paramref name="header"/>'s line resolves to.</summary>
    private static async Task<Command> ResolvedLensOn(string path, string header)
    {
        var pack = Pack();
        var lenses = await pack.CodeLensAsync(new CodeLensParams(Doc(path)), default);

        int line = LineOf(path, header);
        var lens = Assert.Single(lenses, l => l.Range.Start.Line == line);

        var resolved = await pack.ResolveCodeLensAsync(lens, default);
        Assert.NotNull(resolved.Command);
        return resolved.Command!;
    }

    /// <summary>The count a resolved lens leads its title with.</summary>
    private static int Count(Command command) =>
        int.Parse(command.Title.Split(' ')[0], CultureInfo.InvariantCulture);

    /// <summary>
    /// Runs the body against a real <c>.proto</c> outside any project.
    /// </summary>
    /// <remarks>
    /// Outside deliberately. Everything asserted through here is answered from the parse and the
    /// import graph alone, and putting the file in the fixture project instead would make it a
    /// member of a built project — which is a different code path and, for the never-generated
    /// reports, the opposite one.
    /// </remarks>
    private static async Task WithScratchProtoAsync(string fileName, string text, Func<string, Task> body)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"rsense-proto-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(path, text);

        try
        {
            await body(path);
        }
        finally
        {
            ProtoDocumentService.Invalidate(path);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DocumentSymbol Root(DocumentSymbol[] symbols, string name) =>
        Assert.Single(symbols, s => s.Name == name);

    private static DocumentSymbol Child(DocumentSymbol parent, string name) =>
        Assert.Single(parent.Children, c => c.Name == name);

    private static IEnumerable<DocumentSymbol> Flatten(IEnumerable<DocumentSymbol> symbols)
    {
        foreach (var symbol in symbols)
        {
            yield return symbol;
            foreach (var child in Flatten(symbol.Children))
                yield return child;
        }
    }

    private static bool Encloses(LspRange outer, LspRange inner) =>
        AtOrBefore(outer.Start, inner.Start) && AtOrBefore(inner.End, outer.End);

    private static bool AtOrBefore(Position first, Position second) =>
        first.Line < second.Line || (first.Line == second.Line && first.Character <= second.Character);

    private static int LineOf(string path, string needle)
    {
        string[] lines = File.ReadAllLines(path);
        int line = Array.FindIndex(lines, l => l.Contains(needle, StringComparison.Ordinal));
        Assert.True(line >= 0, $"'{needle}' is not in {Path.GetFileName(path)}");
        return line;
    }

    private static string LineAt(string path, int line) => File.ReadAllLines(path)[line];

    /// <summary>The lines the braces opened on <paramref name="header"/>'s line span.</summary>
    private static (int Start, int End) BodyLines(string path, string header)
    {
        string[] lines = File.ReadAllLines(path);
        int start = LineOf(path, header);
        int depth = 0;
        int end = -1;
        bool opened = false;

        for (int i = start; i < lines.Length && end < 0; i++)
        {
            foreach (char c in lines[i])
            {
                if (c == '{')
                {
                    depth++;
                    opened = true;
                }
                else if (c == '}')
                {
                    depth--;
                }
            }

            if (opened && depth == 0)
                end = i;
        }

        Assert.True(end > start, $"'{header}' has no body in {Path.GetFileName(path)}");
        return (start, end);
    }

    private static string FileName(string uri) => Path.GetFileName(Uri.UnescapeDataString(uri));

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

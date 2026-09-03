using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Handlers;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Tests;

/// <summary>
/// The two quick fixes a <c>.proto</c> has: writing the <c>import</c> that would make an unresolved
/// name resolve, and building the project when protoc has never run over the file.
/// </summary>
/// <remarks>
/// The import fix is asserted by applying its edit and comparing the whole resulting document, not
/// by checking that an action with the right title came back. An import statement inserted one line
/// off is still an <c>import</c> — it lands inside the previous statement, or above the
/// <c>syntax</c> line where protoc rejects it — and a test that only counted actions would pass on
/// every one of those.
/// </remarks>
[Collection(SharedState.Name)]
public class ProtoCodeActionTests : IDisposable
{
    private readonly string _session = $"proto-codeaction-{Guid.NewGuid():N}";
    private readonly List<string> _buffers = [];

    public void Dispose()
    {
        OpenDocumentStore.CloseSession(_session);

        foreach (string path in _buffers)
            ProtoDocumentService.Invalidate(path);
    }

    private static ProtoLanguage Pack() => new(new MarkdownFormatter());

    /// <summary>One request and the buffer it was asked about.</summary>
    private readonly record struct Fixes(CodeAction[] Actions, SourceText Source)
    {
        /// <summary>The import actions only. The build action rides along on any file nothing has
        /// generated C# from, which every buffer here is.</summary>
        public CodeAction[] Imports =>
            [.. Actions.Where(a => a.Title.StartsWith("Import ", StringComparison.Ordinal))];

        /// <summary>The document as it stands once the action's edit has been applied.</summary>
        public string Applied(CodeAction action)
        {
            Assert.NotNull(action.Edit);

            var edits = Assert.Single(action.Edit!.Changes).Value;
            var edit = Assert.Single(edits);

            return Source
                .WithChanges(new TextChange(LspConverters.ToTextSpan(Source, edit.Range), edit.NewText))
                .ToString();
        }
    }

    /// <summary>
    /// Asks for the fixes at <paramref name="caretOn"/> in a buffer that exists only in the editor,
    /// inside the fixture project so that its imports resolve against a real proto root.
    /// </summary>
    /// <param name="lineStartCaret">Puts the request's own range at the start of the line instead of
    /// on the name, which is where a client's lightbulb sits.</param>
    /// <param name="alsoAsDiagnostic">Passes the name's position in the context the way a client
    /// passes the diagnostics it is showing.</param>
    private async Task<Fixes> FixesAsync(
        string fileName,
        string text,
        string caretOn,
        bool lineStartCaret = false,
        bool alsoAsDiagnostic = false)
    {
        string path = Path.Combine(FixturePaths.ProtoProjectDir, fileName);
        var source = SourceText.From(text);

        OpenDocumentStore.Open(_session, path, source, 1);
        _buffers.Add(path);

        int index = text.IndexOf(caretOn, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{caretOn}' is not in the buffer");

        var linePosition = source.Lines.GetLinePosition(index);
        var on = new Position(linePosition.Line, linePosition.Character);
        var caret = lineStartCaret ? new Position(linePosition.Line, 0) : on;

        Diagnostic[] diagnostics = alsoAsDiagnostic
            ? [new Diagnostic(new LspRange(on, on), 2, "PROTO012", "proto", $"'{caretOn}' resolves to nothing.")]
            : [];

        var actions = await Pack().CodeActionsAsync(
            new CodeActionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(path)),
                new LspRange(caret, caret),
                new CodeActionContext(diagnostics)),
            default);

        return new Fixes(actions, source);
    }

    // ---- Where the import statement lands -----------------------------------------------------

    [Fact]
    public async Task TheImportIsWrittenAlphabeticallyBetweenTheOnesTheFileAlreadyHas()
    {
        const string Proto = """
            syntax = "proto3";

            package widgets;

            import "common/types.proto";
            import "widgets/types.proto";

            message Uses {
              google.protobuf.Timestamp when = 1;
            }
            """;

        const string Expected = """
            syntax = "proto3";

            package widgets;

            import "common/types.proto";
            import "google/protobuf/timestamp.proto";
            import "widgets/types.proto";

            message Uses {
              google.protobuf.Timestamp when = 1;
            }
            """;

        var fixes = await FixesAsync("fix-alphabetical.proto", Proto, "google.protobuf.Timestamp when");

        var action = Assert.Single(fixes.Imports);
        Assert.Equal("Import \"google/protobuf/timestamp.proto\" for 'google.protobuf.Timestamp'", action.Title);
        Assert.Equal("quickfix", action.Kind);

        // protoc's style guide asks for alphabetical order, and it is also what keeps the diff to
        // the one line that was added rather than to every line below the insertion point.
        Assert.Equal(Expected, fixes.Applied(action));
    }

    [Fact]
    public async Task AnImportThatSortsLastGoesUnderTheOnesAlreadyThere()
    {
        const string Proto = """
            syntax = "proto3";

            package widgets;

            import "common/types.proto";

            message Uses {
              Widget widget = 1;
            }
            """;

        const string Expected = """
            syntax = "proto3";

            package widgets;

            import "common/types.proto";
            import "widgets/types.proto";

            message Uses {
              Widget widget = 1;
            }
            """;

        var fixes = await FixesAsync("fix-last.proto", Proto, "Widget widget");

        // The blank line under the import block has to survive: appending after the last import's
        // line rather than before the next statement is what keeps it there.
        Assert.Equal(Expected, fixes.Applied(Assert.Single(fixes.Imports)));
    }

    [Fact]
    public async Task AFileWithNoImportsYetGetsOneUnderItsHeader()
    {
        const string Proto = """
            syntax = "proto3";

            package widgets;

            message Uses {
              Widget widget = 1;
            }
            """;

        const string Expected = """
            syntax = "proto3";

            package widgets;

            import "widgets/types.proto";

            message Uses {
              Widget widget = 1;
            }
            """;

        var fixes = await FixesAsync("fix-first-import.proto", Proto, "Widget widget");

        // Under the header and never above it: protoc rejects an import written before `syntax`,
        // so an insertion point off by one statement produces a file that does not compile.
        Assert.Equal(Expected, fixes.Applied(Assert.Single(fixes.Imports)));
    }

    [Fact]
    public async Task AHeaderWithNoBlankLineUnderItGetsTheImportSeparatedFromBothSides()
    {
        const string Proto = """
            syntax = "proto3";
            package widgets;
            message Uses {
              Widget widget = 1;
            }
            """;

        const string Expected = """
            syntax = "proto3";
            package widgets;

            import "widgets/types.proto";

            message Uses {
              Widget widget = 1;
            }
            """;

        var fixes = await FixesAsync("fix-compact-header.proto", Proto, "Widget widget");

        // Separated from the declaration below as well as from the package above. A statement
        // inserted with a blank line on one side only reads as belonging to whichever half it is
        // touching, which for an import is the message it is sitting on top of.
        Assert.Equal(Expected, fixes.Applied(Assert.Single(fixes.Imports)));
    }

    // ---- Which imports are offered ------------------------------------------------------------

    [Fact]
    public async Task OnlyTheProtoThatDeclaresTheTypeIsOffered()
    {
        const string Proto = """
            syntax = "proto3";

            package widgets;

            message Uses {
              Widget widget = 1;
            }
            """;

        var fixes = await FixesAsync("fix-one-candidate.proto", Proto, "Widget widget");

        // One action, and it names the file that really declares `widgets.Widget`. The project has
        // three compiled protos and the other two are offered by nothing, which is the half of the
        // answer a test counting actions alone would miss.
        var action = Assert.Single(fixes.Imports);
        Assert.Equal("Import \"widgets/types.proto\" for 'widgets.Widget'", action.Title);

        foreach (string other in (string[])["common/types.proto", "widgets/widgets.proto"])
            Assert.DoesNotContain(fixes.Actions, a => a.Title.Contains(other, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoImportIsOfferedForATypeTheFileAlreadyImports()
    {
        const string Proto = """
            syntax = "proto3";

            package widgets;

            import "widgets/types.proto";

            message Uses {
              Widget widget = 1;
            }
            """;

        var fixes = await FixesAsync("fix-already-imported.proto", Proto, "Widget widget");

        // The name resolves, so there is nothing to fix. An action here would write a duplicate
        // import and claim to have repaired a file that was never broken.
        Assert.Empty(fixes.Imports);
    }

    [Fact]
    public async Task NoImportIsOfferedForANameNothingInTheProjectDeclares()
    {
        const string Proto = """
            syntax = "proto3";

            package widgets;

            message Uses {
              Widgit widget = 1;
            }
            """;

        var fixes = await FixesAsync("fix-typo.proto", Proto, "Widgit widget");

        // A typo is not a missing import, and offering every .proto in the project as a candidate
        // for one would bury the fix that is wanted under a list of ones that are not.
        Assert.Empty(fixes.Imports);
    }

    [Fact]
    public async Task TheFixIsReachableFromTheLineTheSquiggleIsUnder()
    {
        const string Proto = """
            syntax = "proto3";

            package widgets;

            message Uses {
              Widget widget = 1;
            }
            """;

        var fixes = await FixesAsync(
            "fix-from-diagnostic.proto", Proto, "Widget widget",
            lineStartCaret: true, alsoAsDiagnostic: true);

        // The request's own range is the indentation, which resolves to nothing. A client draws its
        // lightbulb on the line rather than on the token, so without resolving at each diagnostic
        // in the context as well the fix would only be reachable with the caret inside the name.
        Assert.Equal(
            "Import \"widgets/types.proto\" for 'widgets.Widget'",
            Assert.Single(fixes.Imports).Title);
    }

    [Fact]
    public async Task OneNameReachedFromTwoOffsetsStillOffersOneAction()
    {
        const string Proto = """
            syntax = "proto3";

            package widgets;

            message Uses {
              Widget widget = 1;
            }
            """;

        var fixes = await FixesAsync(
            "fix-deduplicated.proto", Proto, "Widget widget", alsoAsDiagnostic: true);

        // The caret and the diagnostic are on the same name, and both are resolved. Listing the
        // same fix twice is the visible half of that; applying the winner twice would write the
        // import twice.
        Assert.Single(fixes.Imports);
    }

    // ---- Building what has never been built ---------------------------------------------------

    [Fact]
    public async Task TheBuildFixIsACommandRatherThanAnEdit()
    {
        var position = new Position(0, 0);

        var actions = await Pack().CodeActionsAsync(
            new CodeActionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(FixturePaths.ContractsProtoFile)),
                new LspRange(position, position),
                new CodeActionContext([])),
            default);

        var build = Assert.Single(actions);
        Assert.Equal("Build ProtoNeverBuiltProject to generate the C# for this file", build.Title);
        Assert.Equal("quickfix", build.Kind);

        // No edit, and that is the point: the pack must never build on its own. The client hands
        // the command back as workspace/executeCommand, so running a build stays something the
        // user asked for rather than something listing quick fixes set off.
        Assert.Null(build.Edit);

        Assert.NotNull(build.Command);
        Assert.Equal(ExecuteCommandHandler.BuildCommand, build.Command!.Name);
        Assert.Equal(
            Path.GetFullPath(FixturePaths.ProtoNeverBuiltProjectFile),
            Path.GetFullPath((string)Assert.Single(build.Command!.Arguments ?? [])),
            StringComparer.OrdinalIgnoreCase);

        // Advertised unconditionally, so the command works whether or not this pack is the one that
        // put it there — a code action carrying a command no server accepts does nothing at all.
        Assert.Contains(ExecuteCommandHandler.BuildCommand, ExecuteCommandHandler.Commands);
    }

    [Fact]
    public async Task AProtoInAProjectThatHasBeenBuiltIsNeverOfferedABuild()
    {
        var position = new Position(0, 0);

        var actions = await Pack().CodeActionsAsync(
            new CodeActionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(FixturePaths.WidgetsProtoFile)),
                new LspRange(position, position),
                new CodeActionContext([])),
            default);

        // Offering a build that has already run and will change nothing is how the fix stops being
        // believed the one time it is the answer.
        Assert.DoesNotContain(actions, a => a.Title.StartsWith("Build ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NeitherFixCarriesAnythingLeftToResolve()
    {
        const string Proto = """
            syntax = "proto3";

            package widgets;

            message Uses {
              Widget widget = 1;
            }
            """;

        var fixes = await FixesAsync("fix-resolve.proto", Proto, "Widget widget");
        var pack = Pack();

        Assert.NotEmpty(fixes.Actions);

        // An import is one line of text at an offset the parse already gave up, and the build is a
        // command rather than an edit. Neither has an expensive half, so neither carries the `data`
        // a resolve would need — and the resolve hands the action straight back.
        Assert.All(fixes.Actions, action => Assert.Null(action.Data));

        foreach (var action in fixes.Actions)
            Assert.Same(action, await pack.ResolveCodeActionAsync(action, default));
    }
}

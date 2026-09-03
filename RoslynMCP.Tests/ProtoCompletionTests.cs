using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using RoslynMCP.Services;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// textDocument/completion in a <c>.proto</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every test here asserts the <i>classification</i> of the caret rather than merely that a list
/// came back, because that is the only thing a completion provider can be wrong about in a way the
/// user notices: a menu offering <c>message</c> where only a type may be written, or scalars where
/// protobuf accepts a message and nothing else, is worse than no menu at all — it inserts something
/// protoc rejects and the file that produced it looked fine while it was being typed.
/// </para>
/// <para>
/// The documents are buffers inside the fixture project rather than files in a temp directory, and
/// both halves of that matter. Completion runs on text that has not been saved — half-written
/// <c>repeated Wid</c> with no name, no number and no semicolon is the state it exists for — and
/// every answer worth asserting is decided by the proto root the owning project gives the file:
/// which <c>.proto</c> an <c>import</c> may name, and which messages one already written makes
/// visible.
/// </para>
/// </remarks>
[Collection(SharedState.Name)]
public class ProtoCompletionTests : IDisposable
{
    private readonly string _session = $"proto-completion-{Guid.NewGuid():N}";
    private readonly List<string> _buffers = [];

    public void Dispose()
    {
        OpenDocumentStore.CloseSession(_session);

        // The parse is memoized per path, and these paths name files that do not exist: leaving an
        // entry behind would hand a later reader a document nothing on disk backs.
        foreach (string path in _buffers)
            ProtoDocumentService.Invalidate(path);
    }

    /// <summary>One completion request and the buffer it was asked about.</summary>
    private readonly record struct Completions(CompletionList List, SourceText Source)
    {
        public CompletionItem[] Items => List.Items;

        public string[] Labels => [.. List.Items.Select(item => item.Label)];

        public CompletionItem Item(string label) =>
            Assert.Single(List.Items, item => item.Label == label);

        public bool Offers(string label) => List.Items.Any(item => item.Label == label);

        /// <summary>The text a committed item would replace, which is what decides whether it
        /// completes the word under the caret or appends to it.</summary>
        public string Replaced(CompletionItem item)
        {
            Assert.NotNull(item.TextEdit);
            return Source.ToString(LspConverters.ToTextSpan(Source, item.TextEdit!.Range));
        }
    }

    /// <summary>
    /// Asks for completion at the end of <paramref name="caretAfter"/> in a buffer that exists only
    /// in the editor.
    /// </summary>
    private async Task<Completions> CompleteAsync(string fileName, string text, string caretAfter)
    {
        string path = Path.Combine(FixturePaths.ProtoProjectDir, fileName);
        var source = SourceText.From(text);

        OpenDocumentStore.Open(_session, path, source, 1);
        _buffers.Add(path);

        int index = text.IndexOf(caretAfter, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{caretAfter}' is not in the buffer");

        var caret = source.Lines.GetLinePosition(index + caretAfter.Length);

        var list = await new ProtoLanguage(new MarkdownFormatter()).CompletionAsync(
            new CompletionParams(
                new TextDocumentIdentifier(LspConverters.PathToUri(path)),
                new Position(caret.Line, caret.Character)),
            new LspResolveCache(),
            default);

        return new Completions(list, source);
    }

    // ---- Type position ------------------------------------------------------------------------

    [Fact]
    public async Task AfterALabelTheScalarsAndEveryTypeTheImportsMakeVisibleAreOffered()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            import "common/types.proto";

            message Uses {
              repeated Wid
            }
            """;

        var completions = await CompleteAsync("scratch-field-type.proto", Proto, "repeated Wid");

        // The built-ins, which is the overwhelmingly common thing a field's type is.
        foreach (string scalar in (string[])["string", "int32", "sint64", "bytes", "bool"])
            Assert.Equal(LspCompletionItemKind.Keyword, completions.Item(scalar).Kind);

        // Reached only through the import: nothing in this buffer names common/types.proto's
        // declarations, and the file resolves them because the statement above puts them in scope.
        var uuid = completions.Item("common.UUID");
        Assert.Equal(LspCompletionItemKind.Class, uuid.Kind);
        Assert.Equal("types.proto", uuid.Detail);

        // Spelled the way it resolves from the caret's scope rather than by its bare name: `UUID`
        // written in package `scratch` names nothing, so an item labelled `UUID` would insert a
        // reference protoc rejects.
        Assert.DoesNotContain("UUID", completions.Labels);

        Assert.Equal(LspCompletionItemKind.Enum, completions.Item("common.Channel").Kind);

        // The buffer's own message, under the shortest name that still resolves.
        Assert.Equal(LspCompletionItemKind.Class, completions.Item("Uses").Kind);

        // None of the keywords that open a statement: after `repeated` the grammar accepts a type
        // and nothing else, so every one of these would insert a syntax error.
        foreach (string keyword in
                 (string[])["message", "enum", "oneof", "reserved", "option", "repeated", "optional", "map"])
        {
            Assert.DoesNotContain(keyword, completions.Labels);
        }

        // `stream` leads an rpc's parentheses and may not appear on a field at all.
        Assert.DoesNotContain("stream", completions.Labels);

        // Every item replaces the partial word rather than appending to it. Getting this wrong
        // produces `repeated Widcommon.UUID`, which is the failure a user reports as "completion
        // is broken" rather than as a wrong list.
        Assert.All(completions.Items, item => Assert.Equal("Wid", completions.Replaced(item)));
    }

    [Fact]
    public async Task InsideAMapsAngleBracketsATypeIsOfferedAndAKeywordIsNot()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            import "common/types.proto";

            message Uses {
              map<string, comm
            }
            """;

        var completions = await CompleteAsync("scratch-map-value.proto", Proto, "map<string, comm");

        Assert.True(completions.Offers("common.UUID"));
        Assert.True(completions.Offers("string"));
        Assert.DoesNotContain("map", completions.Labels);
        Assert.DoesNotContain("message", completions.Labels);
    }

    // ---- Statement position -------------------------------------------------------------------

    [Fact]
    public async Task AtTheStartOfAStatementInAMessageBothTheKeywordsAndTheTypesAreOffered()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            message Uses {
              rep
            }
            """;

        var completions = await CompleteAsync("scratch-statement-message.proto", Proto, "  rep");

        Assert.Equal(LspCompletionItemKind.Keyword, completions.Item("repeated").Kind);
        Assert.True(completions.Offers("message"));
        Assert.True(completions.Offers("map"));

        // A field statement begins with its own type, so this position is type position as well —
        // and writing a field is the overwhelmingly common reason to open the menu here.
        Assert.True(completions.Offers("string"));

        // Types first: in a body that holds fields the keywords are the rarer answer, and a menu
        // that leads with `message` in front of `string` is one the user scrolls past every time.
        Assert.StartsWith("1", completions.Item("string").SortText);
        Assert.StartsWith("4", completions.Item("repeated").SortText);

        // proto3 rejects both outright, so offering either would be offering an error.
        Assert.DoesNotContain("required", completions.Labels);
        Assert.DoesNotContain("extensions", completions.Labels);
    }

    [Fact]
    public async Task TheSameCaretInAProto2FileOffersTheKeywordsProto3Dropped()
    {
        const string Proto = """
            syntax = "proto2";

            package scratch;

            message Uses {
              req
            }
            """;

        var completions = await CompleteAsync("scratch-statement-proto2.proto", Proto, "  req");

        Assert.True(completions.Offers("required"));
        Assert.True(completions.Offers("extensions"));
    }

    [Fact]
    public async Task AtFileLevelOnlyTheFileLevelKeywordsAreOffered()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            mes
            """;

        var completions = await CompleteAsync("scratch-statement-file.proto", Proto, "mes");

        foreach (string keyword in (string[])["message", "enum", "service", "import", "option", "extend"])
            Assert.Equal(LspCompletionItemKind.Keyword, completions.Item(keyword).Kind);

        // Nothing that belongs to a body: a file level statement cannot declare a field, and an
        // `rpc` outside a service is not a thing protobuf has.
        foreach (string inner in (string[])["string", "int32", "repeated", "rpc", "reserved"])
            Assert.DoesNotContain(inner, completions.Labels);
    }

    // ---- rpc parentheses ----------------------------------------------------------------------

    [Fact]
    public async Task InsideAnRpcsRequestParenthesesOnlyMessagesAndStreamAreOffered()
    {
        var completions = await CompleteAsync("scratch-rpc-request.proto", RpcProto, "rpc Do(Wid");

        // The one place `stream` may be written, and the item that distinguishes a streaming rpc
        // from a unary one at the moment it is being declared.
        Assert.Equal(LspCompletionItemKind.Keyword, completions.Item("stream").Kind);

        Assert.True(completions.Offers("widgets.Widget"));
        Assert.True(completions.Offers("Ping"));

        // An rpc takes a message and nothing else. A scalar or an enum here compiles nowhere, so
        // offering one is offering an error.
        foreach (string scalar in (string[])["string", "int32", "bool", "bytes"])
            Assert.DoesNotContain(scalar, completions.Labels);

        Assert.DoesNotContain(completions.Items, item => item.Kind == LspCompletionItemKind.Enum);
    }

    [Fact]
    public async Task InsideAnRpcsReturnsParenthesesTheSameSetIsOffered()
    {
        var completions = await CompleteAsync("scratch-rpc-response.proto", RpcProto, "returns (Pin");

        Assert.True(completions.Offers("stream"));
        Assert.True(completions.Offers("widgets.Widget"));
        Assert.DoesNotContain("string", completions.Labels);
    }

    [Fact]
    public async Task StreamIsNotOfferedASecondTimeInsideTheSameParentheses()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            import "widgets/types.proto";

            message Ping {
              int64 id = 1;
            }

            service Scratch {
              rpc Do(stream Wid) returns (Ping);
            }
            """;

        var completions = await CompleteAsync("scratch-rpc-stream.proto", Proto, "rpc Do(stream Wid");

        // It may only lead the parentheses, so a name already inside them rules it out — and the
        // types are still there, which is what proves this is the narrower list rather than none.
        Assert.DoesNotContain("stream", completions.Labels);
        Assert.True(completions.Offers("widgets.Widget"));
    }

    private const string RpcProto = """
        syntax = "proto3";

        package scratch;

        import "widgets/types.proto";

        message Ping {
          int64 id = 1;
        }

        service Scratch {
          rpc Do(Wid) returns (Pin);
        }
        """;

    // ---- Import paths -------------------------------------------------------------------------

    [Fact]
    public async Task InsideAnImportsQuotesTheProtosUnderTheRootAreOffered()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            import "common/types.proto";
            import "widg
            """;

        var completions = await CompleteAsync("scratch-import.proto", Proto, "import \"widg");

        // Relative to the proto root, which is what the statement means: an import is never
        // resolved against the importing file's own directory.
        var widgets = completions.Item("widgets/widgets.proto");
        Assert.Equal(LspCompletionItemKind.File, widgets.Kind);
        Assert.Equal("ProtoProject", widgets.Detail);

        Assert.True(completions.Offers("widgets/types.proto"));
        Assert.True(completions.Offers("NoGenerated/orphan.proto"));

        // Already written, so offering it again is offering a duplicate the file does not need.
        Assert.DoesNotContain("common/types.proto", completions.Labels);

        // Forward slashes, always. A Windows path in an import statement compiles nowhere.
        Assert.All(completions.Items, item => Assert.DoesNotContain("\\", item.Label));

        Assert.All(completions.Items, item => Assert.Equal("widg", completions.Replaced(item)));

        if (ProtoImportResolver.StandardImportsDirectory is not null)
        {
            // protoc's own imports sort last: a proto root can be a whole repository, and the file
            // being reached for is far more often one the user has in their own tree.
            var timestamp = completions.Item("google/protobuf/timestamp.proto");
            Assert.Equal("standard imports", timestamp.Detail);
            Assert.StartsWith("1", timestamp.SortText);
            Assert.StartsWith("0", widgets.SortText);
        }
    }

    [Fact]
    public async Task NothingIsOfferedInsideAnOptionsStringValue()
    {
        const string Proto = """
            syntax = "proto3";

            option csharp_namespace = "Scr
            """;

        var completions = await CompleteAsync("scratch-option-string.proto", Proto, "\"Scr");

        // An import path is the only literal in the grammar whose content this pack knows anything
        // about. Every other one is an option value whose schema lives in a descriptor nothing here
        // reads, and guessing at it would put a list of file names inside a namespace.
        Assert.Empty(completions.Items);
    }

    // ---- Field numbers ------------------------------------------------------------------------

    [Fact]
    public async Task TheNextFieldNumberOfferedIsOnePastTheHighestAndNotTheGapBelowIt()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            message Gapped {
              string first = 1;
              string third = 5;
              string next =
            }
            """;

        var completions = await CompleteAsync("scratch-field-number.proto", Proto, "string next =");

        var item = Assert.Single(completions.Items);
        Assert.Equal("6", item.Label);
        Assert.Equal(LspCompletionItemKind.Value, item.Kind);

        // Preselected, because it is the only answer and the user opened the menu to take it.
        Assert.True(item.Preselect);

        // 2, 3 and 4 are unused and none of them may be offered. A gap in the numbering is almost
        // always a field that was deleted and whose number a deployed peer still sends; handing it
        // to a new field of a different type is the classic silent protobuf corruption, and no
        // amount of "it is free" makes it safe.
        foreach (string reused in (string[])["2", "3", "4"])
            Assert.DoesNotContain(reused, completions.Labels);
    }

    [Fact]
    public async Task AFieldInAOneofIsNumberedInTheMessageAroundIt()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            message Owner {
              string a = 1;

              oneof pick {
                string b = 7;
                string c =
              }
            }
            """;

        var completions = await CompleteAsync("scratch-oneof-number.proto", Proto, "string c =");

        // A oneof opens no numbering space of its own — its members share the enclosing message's —
        // so an answer of 1 here would collide with `a` the moment it was taken.
        Assert.Equal("8", Assert.Single(completions.Items).Label);
    }

    [Fact]
    public async Task AnOptionsValueIsNotOfferedAFieldNumber()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            message Uses {
              option deprecated =
            }
            """;

        var completions = await CompleteAsync("scratch-option-number.proto", Proto, "option deprecated =");

        // `option x = …` has the shape of a field without any of its meaning. Offering a wire
        // number as an option value writes something that parses and means nothing.
        Assert.Empty(completions.Items);
    }

    [Fact]
    public async Task NothingIsOfferedInsideAComment()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            message Uses {
              // repeated Wid
            }
            """;

        var completions = await CompleteAsync("scratch-comment.proto", Proto, "// repeated Wid");

        Assert.Empty(completions.Items);
    }

    // ---- Self-containment ---------------------------------------------------------------------

    [Fact]
    public async Task EveryItemIsCompleteAsSentAndResolvesToItselfUnchanged()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            import "common/types.proto";

            message Uses {
              repeated Wid
            }
            """;

        var completions = await CompleteAsync("scratch-resolve.proto", Proto, "repeated Wid");
        var pack = new ProtoLanguage(new MarkdownFormatter());
        var cache = new LspResolveCache();

        Assert.NotEmpty(completions.Items);

        // No resolve payload on any of them, which is the contract a self-contained pack holds to:
        // an item carrying no `data` gives a client nothing to come back with.
        Assert.All(completions.Items, item => Assert.Null(item.Data));
        Assert.All(completions.Items, item => Assert.NotNull(item.TextEdit));

        // The resolve request carries no document, so an item that arrived incomplete could never
        // be completed. The identity below is what makes that safe rather than merely true today.
        foreach (var item in completions.Items)
            Assert.Same(item, await pack.ResolveCompletionAsync(item, cache, default));

        // The case that would lose data silently: a well-known type the file has not imported is
        // offered with the `import` it needs already attached, because that edit cannot be added
        // later. Committing it without the import writes a file that does not compile.
        var timestamp = completions.Item("google.protobuf.Timestamp");
        var attached = Assert.Single(timestamp.AdditionalTextEdits ?? []);
        Assert.Contains("import \"google/protobuf/timestamp.proto\";", attached.NewText);

        var resolved = await pack.ResolveCompletionAsync(timestamp, cache, default);
        Assert.Same(attached, Assert.Single(resolved.AdditionalTextEdits ?? []));
    }

    [Fact]
    public async Task AWellKnownTypeTheFileAlreadyImportsCarriesNoSecondImport()
    {
        const string Proto = """
            syntax = "proto3";

            package scratch;

            import "google/protobuf/timestamp.proto";

            message Uses {
              repeated Tim
            }
            """;

        var completions = await CompleteAsync("scratch-wellknown-imported.proto", Proto, "repeated Tim");

        var timestamp = completions.Item("google.protobuf.Timestamp");

        // The import is already there. Attaching a second one writes a duplicate statement into a
        // file the user cannot see the edit for until it lands.
        Assert.Null(timestamp.AdditionalTextEdits);
    }
}

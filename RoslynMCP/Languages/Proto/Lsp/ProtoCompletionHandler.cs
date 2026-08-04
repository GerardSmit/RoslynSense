using System.Globalization;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.Proto.Lsp;

/// <summary>
/// textDocument/completion in a <c>.proto</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every item is complete as sent. The resolve endpoint carries no document, so anything a
/// committed item needs — the type name, the import it depends on, the documentation shown beside
/// it — has to be on the item before it leaves here or it can never be added.
/// </para>
/// <para>
/// The position is classified by scanning the text rather than by walking the parse tree, for the
/// reason every completion handler in this repository does it: half-written proto —
/// <c>repeated com</c> with no name, no number and no semicolon — is exactly the state completion
/// runs in and exactly the state the parser cannot represent. The parse is still consulted, but
/// only for the questions the scan cannot answer on its own: which numbers a message has already
/// used, and which declarations the file can see.
/// </para>
/// </remarks>
internal static class ProtoCompletionHandler
{
    private static readonly CompletionList Empty = new(false, []);

    /// <summary>How many import paths one list may carry. A proto root can be a whole repository,
    /// and a menu nobody can read costs more to send than it is worth.</summary>
    private const int MaxImportItems = 500;

    /// <summary>
    /// The 15 built-ins, with what distinguishes them on the wire.
    /// </summary>
    /// <remarks>
    /// The details are not decoration. <c>int32</c> against <c>sint32</c> against <c>fixed32</c> is
    /// the choice a proto author gets wrong most often, and it is invisible from the name alone —
    /// a negative value in an <c>int32</c> costs ten bytes.
    /// </remarks>
    private static readonly (string Name, string Detail)[] Scalars =
    [
        ("double", "64-bit IEEE float"),
        ("float", "32-bit IEEE float"),
        ("int32", "variable-length signed; ten bytes for a negative"),
        ("int64", "variable-length signed; ten bytes for a negative"),
        ("uint32", "variable-length unsigned"),
        ("uint64", "variable-length unsigned"),
        ("sint32", "variable-length zig-zag signed; use where negatives are common"),
        ("sint64", "variable-length zig-zag signed; use where negatives are common"),
        ("fixed32", "always four bytes; cheaper than uint32 above 2^28"),
        ("fixed64", "always eight bytes; cheaper than uint64 above 2^56"),
        ("sfixed32", "always four bytes, signed"),
        ("sfixed64", "always eight bytes, signed"),
        ("bool", "true or false"),
        ("string", "UTF-8 text"),
        ("bytes", "an arbitrary byte sequence"),
    ];

    public static async Task<CompletionList> CompletionAsync(CompletionParams p, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);
        if (await ProtoWorkspace.GetAsync(path, ct) is not { } view)
            return Empty;

        var context = Classify(view.Text, LspConverters.ToOffset(view.Text, p.Position));

        return context.Kind switch
        {
            ProtoCompletionKind.ImportPath => ImportPaths(view, context),
            ProtoCompletionKind.FieldNumber => FieldNumber(view, context),
            ProtoCompletionKind.Statement => Statement(view, context),
            ProtoCompletionKind.Type => Types(view, context, messagesOnly: false, stream: false),
            ProtoCompletionKind.RpcRequest or ProtoCompletionKind.RpcResponse =>
                Types(view, context, messagesOnly: true, stream: context.AllowStream),
            _ => Empty,
        };
    }

    // ---- Statement position ------------------------------------------------------------------

    /// <summary>
    /// The keywords legal where the caret is — and, in a body that holds fields, the types too.
    /// </summary>
    /// <remarks>
    /// A field statement begins with its own type, so statement position inside a <c>message</c>,
    /// a <c>oneof</c> or an <c>extend</c> <i>is</i> type position. Offering only keywords there
    /// would leave <c>string</c> out of the list the user opened in order to write a field, which
    /// is the overwhelmingly common reason to open it.
    /// </remarks>
    private static CompletionList Statement(ProtoProjectView view, ProtoCompletionContext context)
    {
        var range = LspConverters.ToRange(view.Text.Lines, context.ReplaceSpan);
        var items = new List<CompletionItem>();
        bool fields = HoldsFields(context.Block);

        foreach (string keyword in Keywords(context.Block, view.Parse.SyntaxLevel))
        {
            items.Add(new CompletionItem(
                keyword,
                LspCompletionItemKind.Keyword,
                null,
                (fields ? "4" : "0") + keyword,
                keyword,
                new TextEdit(range, keyword)));
        }

        if (fields)
            items.AddRange(TypeItems(view, context, range, messagesOnly: false));

        return items.Count == 0 ? Empty : new CompletionList(false, [.. items]);
    }

    /// <summary>
    /// What may be written at the start of a statement in each body.
    /// </summary>
    /// <remarks>
    /// Gated on the dialect where the dialect decides. <c>required</c> and <c>extensions</c> are
    /// proto2 constructs that protoc rejects outright in proto3, so offering them in a proto3 file
    /// would be offering an error; editions are read here with proto3's rules, the same assumption
    /// <see cref="ProtoParser"/> makes.
    /// </remarks>
    private static string[] Keywords(ProtoBlock block, ProtoSyntaxLevel level)
    {
        bool proto2 = level == ProtoSyntaxLevel.Proto2;

        return block switch
        {
            ProtoBlock.File =>
                ["syntax", "edition", "package", "import", "option", "message", "enum", "service", "extend"],

            ProtoBlock.Message => proto2
                ? ["message", "enum", "oneof", "extend", "option", "reserved", "extensions",
                   "optional", "required", "repeated", "map"]
                : ["message", "enum", "oneof", "extend", "option", "reserved",
                   "optional", "repeated", "map"],

            // No label and no `map`: protobuf forbids both inside a oneof, so a field there is a
            // bare type and a name.
            ProtoBlock.Oneof => ["option"],

            ProtoBlock.Extend => proto2
                ? ["optional", "required", "repeated"]
                : ["optional", "repeated"],

            ProtoBlock.Enum => ["option", "reserved"],
            ProtoBlock.Service => ["rpc", "option"],
            ProtoBlock.Rpc => ["option"],
            _ => [],
        };
    }

    private static bool HoldsFields(ProtoBlock block) =>
        block is ProtoBlock.Message or ProtoBlock.Oneof or ProtoBlock.Extend;

    // ---- Type position -----------------------------------------------------------------------

    private static CompletionList Types(
        ProtoProjectView view, ProtoCompletionContext context, bool messagesOnly, bool stream)
    {
        var range = LspConverters.ToRange(view.Text.Lines, context.ReplaceSpan);
        var items = new List<CompletionItem>();

        if (stream)
        {
            items.Add(new CompletionItem(
                "stream", LspCompletionItemKind.Keyword, "a stream of this type in this direction",
                "0stream", "stream", new TextEdit(range, "stream")));
        }

        items.AddRange(TypeItems(view, context, range, messagesOnly));

        return items.Count == 0 ? Empty : new CompletionList(false, [.. items]);
    }

    /// <summary>
    /// Every type nameable at the caret: the built-ins, everything the file's import graph makes
    /// visible, and protoc's own well-known types.
    /// </summary>
    /// <param name="messagesOnly">Set for an rpc's request and response, which take a message and
    /// nothing else — not a scalar and not an enum.</param>
    private static List<CompletionItem> TypeItems(
        ProtoProjectView view, ProtoCompletionContext context, LspRange range, bool messagesOnly)
    {
        var items = new List<CompletionItem>();

        if (!messagesOnly)
        {
            foreach (var (name, detail) in Scalars)
            {
                items.Add(new CompletionItem(
                    name, LspCompletionItemKind.Keyword, detail, "1" + name, name,
                    new TextEdit(range, name)));
            }
        }

        var file = view.Parse;
        var scope = view.CreateScope();
        string caretScope = ProtoScope.ScopeOf(Enclosing(file, context.BlockBrace), file);
        var visible = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declaring in scope.VisibleFiles)
        {
            foreach (var declaration in declaring.AllDeclarations)
            {
                bool wanted = messagesOnly
                    ? declaration.Kind == ProtoDeclarationKind.Message
                    : declaration.Kind is ProtoDeclarationKind.Message or ProtoDeclarationKind.Enum;

                if (!wanted || declaration.FullName.Length == 0 || !visible.Add(declaration.FullName))
                    continue;

                string written = Qualify(scope, caretScope, declaration);

                items.Add(new CompletionItem(
                    written,
                    declaration.Kind == ProtoDeclarationKind.Enum
                        ? LspCompletionItemKind.Enum
                        : LspCompletionItemKind.Class,
                    Path.GetFileName(declaring.FilePath),
                    "2" + written,
                    written,
                    new TextEdit(range, written))
                {
                    Documentation = Documentation(declaration),
                });
            }
        }

        items.AddRange(WellKnownItems(view, scope, range, visible, messagesOnly));
        return items;
    }

    /// <summary>
    /// protoc's own types, whether or not the file has imported them yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The unimported ones carry the <c>import</c> they need in
    /// <see cref="CompletionItem.AdditionalTextEdits"/> rather than being left out of the list.
    /// A completion item is resolved without a document, so an edit that is not on the item when it
    /// is sent can never be added to it — and offering <c>google.protobuf.Timestamp</c> without the
    /// import writes a file that does not compile.
    /// </para>
    /// <para>
    /// Whether a well-known is a message is read off its declaration, which is available whenever
    /// the Grpc.Tools package is restored somewhere this process can see;
    /// <see cref="ProtoWellKnownTypes"/> records the proto name, the import path and the CLR type
    /// but not the kind. Where the package is missing the one entry that is an enum,
    /// <c>NullValue</c>, is therefore offered as an rpc's request type — on a machine where the
    /// package is missing nothing generated any C# either, so the wrong item is the smaller of the
    /// two problems on screen.
    /// </para>
    /// </remarks>
    private static IEnumerable<CompletionItem> WellKnownItems(
        ProtoProjectView view, ProtoScope scope, LspRange range,
        HashSet<string> visible, bool messagesOnly)
    {
        foreach (var wellKnown in ProtoWellKnownTypes.All)
        {
            if (visible.Contains(wellKnown.FullName))
                continue;

            if (messagesOnly && scope.Find(wellKnown.FullName)?.Declaration is ProtoEnum)
                continue;

            var import = ProtoImportEdits.TryInsert(view.Parse, wellKnown.ProtoPath);

            yield return new CompletionItem(
                wellKnown.FullName,
                LspCompletionItemKind.Class,
                wellKnown.ClrTypeName,
                "3" + wellKnown.FullName,
                wellKnown.FullName,
                new TextEdit(range, wellKnown.FullName))
            {
                AdditionalTextEdits = import is { } edit ? [edit] : null,
            };
        }
    }

    /// <summary>
    /// The shortest spelling of a declaration that resolves back to it from the caret's scope.
    /// </summary>
    /// <remarks>
    /// Asked of <see cref="ProtoScope"/> rather than derived from the two names, because the answer
    /// depends on everything else in scope: <c>Widget</c> is the right spelling until a nested
    /// message of that name shadows it, and then only <c>widgets.Widget</c> is. The rooted name is
    /// the fallback because it always resolves — ugly beats an item that inserts a reference to
    /// something else.
    /// </remarks>
    private static string Qualify(ProtoScope scope, string caretScope, ProtoDeclaration declaration)
    {
        string full = declaration.FullName;

        for (int cut = full.Length; ; )
        {
            int dot = full.LastIndexOf('.', cut - 1);
            string candidate = full[(dot + 1)..];

            if (ReferenceEquals(scope.ResolveIn(candidate, caretScope)?.Declaration, declaration))
                return candidate;

            if (dot <= 0)
                return "." + full;

            cut = dot;
        }
    }

    private static MarkupContent? Documentation(ProtoDeclaration declaration) =>
        declaration.Documentation is { Length: > 0 } text
            ? new MarkupContent("plaintext", text)
            : null;

    // ---- Field numbers -----------------------------------------------------------------------

    private static CompletionList FieldNumber(ProtoProjectView view, ProtoCompletionContext context)
    {
        if (OwningMessage(view.Parse, context.BlockBrace) is not { } message)
            return Empty;

        string number = NextFieldNumber(message).ToString(CultureInfo.InvariantCulture);

        return new CompletionList(false,
        [
            new CompletionItem(
                number,
                LspCompletionItemKind.Value,
                "the next free field number",
                "0",
                number,
                new TextEdit(LspConverters.ToRange(view.Text.Lines, context.ReplaceSpan), number))
            {
                Preselect = true,
            },
        ]);
    }

    /// <summary>
    /// One past the highest number the message has used.
    /// </summary>
    /// <remarks>
    /// One past the highest, not the lowest number nobody is using. A field's number is its
    /// identity on the wire, so a gap in the sequence is almost always a field that was deleted and
    /// whose number a deployed peer still sends; handing that number to a new field of a different
    /// type is the classic silent protobuf corruption, and the <c>reserved</c> statement exists to
    /// prevent exactly it. 19000–19999 is skipped because protoc reserves it for its own use and
    /// rejects any field in it.
    /// </remarks>
    private static int NextFieldNumber(ProtoMessage message)
    {
        int next = 1;

        foreach (var field in message.AllFields)
        {
            if (field.Number >= next)
                next = field.Number + 1;
        }

        return next is >= 19000 and <= 19999 ? 20000 : next;
    }

    /// <summary>
    /// The message whose numbering the caret's field belongs to.
    /// </summary>
    /// <remarks>
    /// A <c>oneof</c>'s members are numbered in the message around it, not in the oneof, so its
    /// free number is the message's. An <c>extend</c> block's are numbered inside an extension
    /// range declared on a type in some other file, which this cannot see and must not guess at.
    /// </remarks>
    private static ProtoMessage? OwningMessage(ProtoFile file, int brace) =>
        Enclosing(file, brace) switch
        {
            ProtoMessage message => message,
            ProtoOneof oneof => oneof.Parent as ProtoMessage,
            _ => null,
        };

    /// <summary>
    /// The declaration whose body the caret is in, found by its opening brace.
    /// </summary>
    /// <remarks>
    /// By brace rather than through <see cref="ProtoFile.DeclarationAt"/>, because completion runs
    /// on a file that is mid-edit: the declaration around the caret may not have closed yet, and
    /// then its span stops short of the very position being asked about. The brace is the one
    /// offset the parse and the scan cannot disagree on, since the scan is what found it.
    /// </remarks>
    private static ProtoDeclaration? Enclosing(ProtoFile file, int brace)
    {
        if (brace < 0)
            return null;

        foreach (var declaration in file.AllDeclarations)
        {
            if (!declaration.BodySpan.IsEmpty && declaration.BodySpan.Start == brace)
                return declaration;
        }

        return null;
    }

    // ---- Import paths ------------------------------------------------------------------------

    /// <summary>
    /// The <c>.proto</c> files reachable from this file's proto roots, written the way an
    /// <c>import</c> has to write them.
    /// </summary>
    /// <remarks>
    /// Enumerated per root and offered relative to it, because that is what the path in the
    /// statement means: an import is resolved against a proto root and never against the importing
    /// file's directory. Roots come back from <see cref="ProtoImportResolver.CandidateRoots"/> in
    /// the order protoc would try them, so a file reachable from two of them is offered under the
    /// spelling that will actually resolve, and protoc's own imports sort last because they are
    /// the least likely thing being reached for.
    /// </remarks>
    private static CompletionList ImportPaths(ProtoProjectView view, ProtoCompletionContext context)
    {
        var range = LspConverters.ToRange(view.Text.Lines, context.ReplaceSpan);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var import in view.Parse.Imports)
        {
            // Not the statement being edited: filtering that one out would empty the list the
            // moment the caret entered a path that is already there.
            if (!import.PathSpan.Contains(context.ReplaceSpan.Start))
                written.Add(import.Path);
        }

        string? standard = ProtoImportResolver.StandardImportsDirectory;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<CompletionItem>();

        foreach (string root in ProtoImportResolver.CandidateRoots(view.FilePath, view.ProjectDirectory))
        {
            bool isStandard = standard is not null && ProtoDocumentService.PathsEqual(root, standard);

            foreach (string file in ProtosUnder(root, MaxImportItems - items.Count))
            {
                if (ProtoDocumentService.PathsEqual(file, view.FilePath)
                    || Relative(root, file) is not { } relative
                    || !seen.Add(relative)
                    || written.Contains(relative))
                {
                    continue;
                }

                items.Add(new CompletionItem(
                    relative,
                    LspCompletionItemKind.File,
                    isStandard ? "standard imports" : Path.GetFileName(root),
                    (isStandard ? "1" : "0") + relative,
                    relative,
                    new TextEdit(range, relative)));
            }

            if (items.Count >= MaxImportItems)
                break;
        }

        return items.Count == 0 ? Empty : new CompletionList(false, [.. items]);
    }

    /// <summary>
    /// Every <c>.proto</c> under a directory, build output and version control excluded.
    /// </summary>
    /// <remarks>
    /// Walked a directory at a time rather than with <see cref="SearchOption.AllDirectories"/>, for
    /// two reasons that both matter here: the recursive form cannot skip a subtree, and a proto
    /// root is routinely a repository whose <c>obj</c> folders hold a copy of every generated
    /// artefact — and it throws on the first directory it may not read, where this skips it.
    /// </remarks>
    private static List<string> ProtosUnder(string root, int limit)
    {
        var results = new List<string>();

        if (limit <= 0)
            return results;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && results.Count < limit)
        {
            string directory = pending.Pop();

            try
            {
                results.AddRange(Directory.EnumerateFiles(directory, "*.proto"));

                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    if (!IsSkipped(Path.GetFileName(child)))
                        pending.Push(child);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return results;
    }

    private static bool IsSkipped(string name) =>
        name.StartsWith('.')
        || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
        || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file under a root, forward slashed as an import path is, or <c>null</c> when it
    /// is not under that root at all.</summary>
    private static string? Relative(string root, string file)
    {
        try
        {
            string relative = Path.GetRelativePath(root, file);

            return Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal)
                ? null
                : relative.Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return null;
        }
    }


    // ---- Classifying the caret ---------------------------------------------------------------

    /// <summary>What the caret is in the middle of writing.</summary>
    private enum ProtoCompletionKind
    {
        /// <summary>Nothing is offered — a comment, an option value, a name only the author can
        /// choose.</summary>
        None,

        /// <summary>The start of a statement in some body, keywords and all.</summary>
        Statement,

        /// <summary>A type is expected and nothing else: after a field's label, or inside a
        /// <c>map&lt;…&gt;</c>.</summary>
        Type,

        /// <summary>Inside an rpc's request parentheses.</summary>
        RpcRequest,

        /// <summary>Inside an rpc's <c>returns</c> parentheses.</summary>
        RpcResponse,

        /// <summary>After a field's <c>=</c>.</summary>
        FieldNumber,

        /// <summary>Inside the quoted path of an <c>import</c>.</summary>
        ImportPath,
    }

    /// <summary>Which body the caret is directly inside, which is what decides the rest.</summary>
    private enum ProtoBlock
    {
        File,
        Message,
        Enum,
        Service,
        Rpc,
        Oneof,
        Extend,

        /// <summary>Braces this pack does not model — an aggregate option value. Nothing is offered
        /// inside one, because nothing here knows the option's schema.</summary>
        Other,
    }

    /// <param name="ReplaceSpan">What a committed item replaces: the partial word under the caret,
    /// or the empty span at it.</param>
    /// <param name="BlockBrace">The offset of the enclosing body's <c>{</c>, or <c>-1</c> at file
    /// level. This is how a construct in the parse is matched to the body the scan found, which is
    /// the only match that survives an unclosed brace.</param>
    /// <param name="AllowStream">Whether <c>stream</c> may still be written — it may only lead an
    /// rpc's parentheses, so a name already inside them rules it out.</param>
    private readonly record struct ProtoCompletionContext(
        ProtoCompletionKind Kind,
        ProtoBlock Block,
        TextSpan ReplaceSpan,
        int BlockBrace,
        bool AllowStream);

    private static readonly ProtoCompletionContext NoContext =
        new(ProtoCompletionKind.None, ProtoBlock.File, default, -1, false);

    /// <summary>
    /// Walks the file to the caret, tracking the bodies it entered and the words of the statement
    /// it is in the middle of.
    /// </summary>
    /// <remarks>
    /// Forward from the start of the file rather than backward from the caret, because the block
    /// stack is what the answer turns on and a backward scan cannot build one without solving the
    /// same problem twice: a <c>{</c> above the caret means nothing until it is known which
    /// keyword opened it and whether a <c>}</c> in between already closed it. A <c>.proto</c> is a
    /// small file and this runs on a keystroke, which is affordable for a linear scan and would not
    /// be for a reparse.
    /// </remarks>
    private static ProtoCompletionContext Classify(SourceText text, int offset)
    {
        if (offset < 0 || offset > text.Length)
            return NoContext;

        var blocks = new Stack<(ProtoBlock Block, int Brace)>();
        var tokens = new List<string>();

        int parens = 0;
        int parenTokens = 0;
        int brackets = 0;
        int angles = 0;
        bool inMap = false;
        bool sawEquals = false;
        bool sawReturns = false;

        void Reset()
        {
            tokens.Clear();
            parens = 0;
            parenTokens = 0;
            angles = 0;
            inMap = false;
            sawEquals = false;
            sawReturns = false;
        }

        var replace = new TextSpan(offset, 0);
        int i = 0;

        while (i < offset)
        {
            char c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;

                // A line comment that reaches the caret contains it — including the one running to
                // the end of the file, where there is no line break to stop the scan at.
                if (i >= offset)
                    return NoContext;

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = CommentEnd(text, i + 2);

                if (end < 0 || end + 2 > offset)
                    return NoContext;

                i = end + 2;
                continue;
            }

            if (c is '"' or '\'')
            {
                int close = ClosingQuote(text, i);

                if (close < 0 || close >= offset)
                {
                    // An import path is the only literal in the grammar whose content this pack
                    // knows anything about; every other one is an option value whose schema lives
                    // in a descriptor nothing here reads.
                    if (tokens.Count == 0 || tokens[0] != "import")
                        return NoContext;

                    var (importBlock, importBrace) = Block(blocks);
                    int contentEnd = Math.Max(close < 0 ? offset : close, i + 1);

                    return new ProtoCompletionContext(
                        ProtoCompletionKind.ImportPath,
                        importBlock,
                        TextSpan.FromBounds(i + 1, contentEnd),
                        importBrace,
                        AllowStream: false);
                }

                i = close + 1;
                continue;
            }

            switch (c)
            {
                case '{':
                    // Never while an option's `[ … ]` list is open: an aggregate value in one
                    // carries braces of its own, and pushing them would lose the real body.
                    if (brackets == 0)
                        blocks.Push((BlockOf(tokens), i));

                    i++;
                    Reset();
                    continue;

                case '}':
                    if (brackets == 0 && blocks.Count > 0)
                        blocks.Pop();

                    i++;
                    Reset();
                    continue;

                case ';':
                    // The one place the option list is given up on. It is not closed at a brace,
                    // because an aggregate option value carries braces inside the list — but an
                    // unclosed `[` would otherwise silence completion for the rest of the file.
                    brackets = 0;
                    i++;
                    Reset();
                    continue;

                case '(':
                    parens++;
                    parenTokens = 0;
                    i++;
                    continue;

                case ')':
                    parens--;
                    i++;
                    continue;

                case '[':
                    brackets++;
                    i++;
                    continue;

                case ']':
                    brackets--;
                    i++;
                    continue;

                case '<':
                    inMap |= tokens.Count > 0 && tokens[^1] == "map";
                    angles++;
                    i++;
                    continue;

                case '>':
                    angles--;
                    i++;
                    continue;

                case '=':
                    sawEquals = true;
                    i++;
                    continue;
            }

            if (!IsNameStart(text, i))
            {
                i++;
                continue;
            }

            int start = i;
            int word = i;

            while (word < text.Length && IsNameChar(text[word]))
                word++;

            // The word the caret is in or at the end of is the one being typed, not one already
            // written: it is what a committed item replaces, and the scan stops on it.
            if (word >= offset)
            {
                replace = TextSpan.FromBounds(start, word);
                break;
            }

            string name = text.ToString(TextSpan.FromBounds(start, word));
            tokens.Add(name);

            if (parens > 0)
                parenTokens++;

            if (name == "returns")
                sawReturns = true;

            i = word;
        }

        var (block, brace) = Block(blocks);

        if (brackets > 0)
            return NoContext;

        if (parens > 0)
        {
            return block == ProtoBlock.Service && tokens.Count > 0 && tokens[0] == "rpc"
                ? new ProtoCompletionContext(
                    sawReturns ? ProtoCompletionKind.RpcResponse : ProtoCompletionKind.RpcRequest,
                    block, replace, brace, AllowStream: parenTokens == 0)
                : NoContext;
        }

        if (angles > 0)
        {
            return inMap
                ? new ProtoCompletionContext(ProtoCompletionKind.Type, block, replace, brace, false)
                : NoContext;
        }

        if (sawEquals)
        {
            // `option x = …` and `reserved …` share the shape of a field without sharing its
            // meaning, and neither takes a field number.
            return HoldsFields(block) && tokens.Count >= 2 && tokens[0] is not ("option" or "reserved")
                ? new ProtoCompletionContext(ProtoCompletionKind.FieldNumber, block, replace, brace, false)
                : NoContext;
        }

        if (tokens.Count == 0)
            return new ProtoCompletionContext(ProtoCompletionKind.Statement, block, replace, brace, false);

        // One word in, and it was a label: the type comes next and nothing else may. Any other
        // single word already is the type, so the caret is on the field's name — which only the
        // author can supply.
        return tokens.Count == 1 && HoldsFields(block) && IsLabel(tokens[0])
            ? new ProtoCompletionContext(ProtoCompletionKind.Type, block, replace, brace, false)
            : NoContext;
    }

    private static (ProtoBlock Block, int Brace) Block(Stack<(ProtoBlock Block, int Brace)> blocks) =>
        blocks.Count > 0 ? blocks.Peek() : (ProtoBlock.File, -1);

    /// <summary>The kind of body a <c>{</c> opened, from the words that came before it.</summary>
    private static ProtoBlock BlockOf(List<string> tokens) => tokens.Count == 0 ? ProtoBlock.Other : tokens[0] switch
    {
        "message" => ProtoBlock.Message,
        "enum" => ProtoBlock.Enum,
        "service" => ProtoBlock.Service,
        "rpc" => ProtoBlock.Rpc,
        "oneof" => ProtoBlock.Oneof,
        "extend" => ProtoBlock.Extend,
        _ => ProtoBlock.Other,
    };

    private static bool IsLabel(string token) =>
        token is "optional" or "required" or "repeated";

    /// <summary>
    /// Where a token starts.
    /// </summary>
    /// <remarks>
    /// A dot leads a name as well as separating one, and both spellings have to scan as one token:
    /// <c>.pkg.Msg</c> is a rooted reference, and an item that replaced only <c>pkg.Msg</c> would
    /// leave the dot behind and change what the name means. A digit starts one too, which no proto
    /// name may — it is the field number, and it has to be a token for the same reason: completion
    /// invoked on a number that is already written should replace it rather than append to it.
    /// </remarks>
    private static bool IsNameStart(SourceText text, int i)
    {
        char c = text[i];

        if (char.IsLetterOrDigit(c) || c == '_')
            return true;

        return c == '.'
            && i + 1 < text.Length
            && (char.IsLetter(text[i + 1]) || text[i + 1] == '_');
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.';

    private static int CommentEnd(SourceText text, int start)
    {
        for (int i = start; i + 1 < text.Length; i++)
        {
            if (text[i] == '*' && text[i + 1] == '/')
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Where a string literal ends: its closing quote, or the line break that proves it was never
    /// closed. Returning the line break rather than nothing is what stops one unterminated literal
    /// from swallowing the rest of the file and reporting every caret below it as inside a string.
    /// </summary>
    private static int ClosingQuote(SourceText text, int open)
    {
        char quote = text[open];

        for (int i = open + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == quote || text[i] == '\n')
                return i;
        }

        return -1;
    }
}

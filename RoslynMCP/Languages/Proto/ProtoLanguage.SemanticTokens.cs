using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.Proto;

internal sealed partial class ProtoLanguage : ILanguageSemanticTokensProvider
{
    /// <summary>
    /// The one colour a <c>.proto</c> needs that C# has no name for. Everything else in the file is
    /// something C# already names — a message is a class, a field is a property, an <c>rpc</c> is a
    /// method — and reusing those entries is what makes a single theme colour the contract and the
    /// code protoc generates from it the same way, with nothing for the user to configure.
    /// </summary>
    public static readonly string[] SemanticTokenTypeNames = [UnresolvedType];

    private const string UnresolvedType = "unresolvedType";

    /// <summary>
    /// Declined. A <c>.proto</c> is a contract file — hundreds of lines rather than thousands — so
    /// answering in full costs less than holding a baseline per document per session to diff
    /// against, and the session does not advertise delta for a pack that says no.
    /// </summary>
    public bool SupportsDelta => false;

    /// <summary>
    /// Every word protobuf's grammar gives a meaning to.
    /// </summary>
    /// <remarks>
    /// Protobuf reserves none of them. <c>message message = 1;</c> is a legal field and
    /// <c>enum enum { … }</c> a legal enum, so membership here is not enough to call a word a
    /// keyword — the parse gets first claim on every span it read as a name, and this list only
    /// colours what is left over.
    /// </remarks>
    private static readonly HashSet<string> s_grammarKeywords = new(StringComparer.Ordinal)
    {
        "syntax", "edition", "package", "import", "public", "weak", "option",
        "message", "enum", "service", "rpc", "returns", "stream", "extend",
        "oneof", "map", "group", "reserved", "extensions", "to", "max",
        "optional", "required", "repeated", "true", "false",
    };

    /// <summary>The legend numbers the lexical pass emits, all four of them C#'s own.</summary>
    private readonly record struct LexicalTokenTypes(int Keyword, int Number, int String, int Comment);

    public async Task<SemanticTokens> SemanticTokensFullAsync(
        SemanticTokensParams p, LanguageSession session, CancellationToken ct) =>
        new(await ComputeTokensAsync(p.TextDocument.Uri, window: null, session, ct));

    public async Task<SemanticTokens> SemanticTokensRangeAsync(
        SemanticTokensRangeParams p, LanguageSession session, CancellationToken ct) =>
        new(await ComputeTokensAsync(p.TextDocument.Uri, p.Range, session, ct));

    /// <summary>
    /// Classifies a <c>.proto</c>, including the one thing about it no grammar can know: whether a
    /// type reference resolves to anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A TextMate grammar colours <c>common.UUD</c> exactly as happily as <c>common.UUID</c>. It
    /// matches the shape of a dotted name and has no scope to ask, so a misspelling, a type moved
    /// to another package, and an <c>import</c> nobody remembered to add all read as completely
    /// ordinary code until protoc rejects the build. Answering that question needs the file's whole
    /// import graph, which is what <see cref="ProtoScope"/> is, and it is the entire reason this
    /// pack answers semanticTokens at all.
    /// </para>
    /// <para>
    /// The rest of what is emitted here is a restatement of what a grammar already knows, sent
    /// anyway for two reasons: the classification is layered, so leaving holes would colour a
    /// message name from the theme's C# palette and the <c>message</c> keyword in front of it from
    /// somewhere else; and a client with no proto grammar installed then still gets a fully
    /// coloured file rather than three coloured words per line.
    /// </para>
    /// </remarks>
    private async Task<int[]> ComputeTokensAsync(
        string uri, LspRange? window, LanguageSession session, CancellationToken ct)
    {
        var document = await ProtoDocumentService.GetAsync(LspConverters.UriToPath(uri), ct);
        if (document is null)
            return [];

        var file = document.Parse;
        var text = file.Text;
        var visible = window is null
            ? new TextSpan(0, text.Length)
            : LspConverters.ToTextSpan(text, window);

        int unresolved = TokenType(session, UnresolvedType);
        int declaredType = LanguageSession.SharedTokenType("class");
        int declaredEnum = LanguageSession.SharedTokenType("enum");
        int enumMember = LanguageSession.SharedTokenType("enumMember");
        int property = LanguageSession.SharedTokenType("property");
        int method = LanguageSession.SharedTokenType("method");
        int package = LanguageSession.SharedTokenType("namespace");
        int scalar = LanguageSession.SharedTokenType("type");

        var lexical = new LexicalTokenTypes(
            LanguageSession.SharedTokenType("keyword"),
            LanguageSession.SharedTokenType("number"),
            LanguageSession.SharedTokenType("string"),
            LanguageSession.SharedTokenType("comment"));

        var tokens = new List<(int Line, int Char, int Length, int Type)>();

        // Every span the parse accounted for. It is what keeps the keyword pass off a message
        // called `map` and off a field called `stream`, both of which protobuf allows.
        var claimed = new List<TextSpan>();

        Add(file.PackageSpan, package);
        Claim(file.PackageSpan);

        foreach (var declaration in file.AllDeclarations)
        {
            ct.ThrowIfCancellationRequested();

            // An `extend` block declares no name of its own — the one it carries is its target's,
            // on the target's span — so it is coloured as the reference it is, one loop down.
            int type = declaration.Kind switch
            {
                ProtoDeclarationKind.Message or ProtoDeclarationKind.Service => declaredType,
                ProtoDeclarationKind.Enum => declaredEnum,
                ProtoDeclarationKind.EnumValue => enumMember,

                // A oneof is a property too: protoc gives it a `…Case` property and nothing else
                // that carries its name, so colouring it as one agrees with what F12 lands on.
                ProtoDeclarationKind.Field or ProtoDeclarationKind.Oneof => property,
                ProtoDeclarationKind.Rpc => method,
                _ => -1,
            };

            if (type < 0)
                continue;

            Add(declaration.Name.Span, type);
            Claim(declaration.Name.Span);
        }

        ProtoScope? scope = null;

        foreach (var reference in file.TypeReferences)
        {
            ct.ThrowIfCancellationRequested();
            Claim(reference.Span);

            // Ahead of the resolution rather than inside Add, because resolving a name off screen
            // is the expensive half and a range request asked about neither.
            if (!reference.Span.IntersectsWith(visible))
                continue;

            if (reference.IsScalar)
            {
                Add(reference.Span, scalar);
                continue;
            }

            // Built at the first name that needs one, and not before: it reads every file the
            // import graph reaches, which a proto whose fields are all scalars never pays for.
            scope ??= document.CreateScope();

            Add(reference.Span, scope.Resolve(reference) switch
            {
                null => unresolved,
                { Declaration: ProtoEnum } => declaredEnum,

                // Anything else is a message, including a well-known type whose own `.proto` could
                // not be read: the table it resolved through holds no enums but `NullValue`.
                _ => declaredType,
            });
        }

        foreach (var (span, type) in LexicalTokens(text, claimed, lexical))
        {
            ct.ThrowIfCancellationRequested();
            Add(span, type);
        }

        return Encode(tokens);

        void Add(TextSpan span, int type)
        {
            if (type < 0 || span.IsEmpty || !span.IntersectsWith(visible))
                return;

            var lines = text.Lines.GetLinePositionSpan(span);
            for (int line = lines.Start.Line; line <= lines.End.Line; line++)
            {
                var textLine = text.Lines[line];
                int start = line == lines.Start.Line ? span.Start : textLine.Start;
                int end = line == lines.End.Line ? span.End : textLine.End;
                if (end > start)
                    tokens.Add((line, start - textLine.Start, end - start, type));
            }
        }

        void Claim(TextSpan span)
        {
            if (!span.IsEmpty)
                claimed.Add(span);
        }
    }

    /// <summary>
    /// The number this session gives one of the pack's own token types. The legend is the union of
    /// C#'s types and every enabled pack's, so a name is at a different index in a session that
    /// enabled a different set — the pack holds the order, the session holds the offset.
    /// </summary>
    private int TokenType(LanguageSession session, string name) =>
        session.TokenTypeOffset(this) + Array.IndexOf(SemanticTokenTypeNames, name);

    /// <summary>
    /// The keywords, numbers, strings and comments, which are the parts of the file the tree keeps
    /// no node for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Re-lexing rather than reading the parse: <see cref="ProtoFile"/> models declarations and
    /// keeps nothing of the tokens between them, and a second lex of a file this size is one pass
    /// over a buffer the editor is already holding.
    /// </para>
    /// <para>
    /// <paramref name="claimed"/> is sorted here and walked alongside the tokens with a single
    /// index, which is all the bookkeeping the keyword suppression needs: both sequences run in
    /// source order and the claimed spans do not overlap each other.
    /// </para>
    /// </remarks>
    private static IEnumerable<(TextSpan Span, int Type)> LexicalTokens(
        SourceText text, List<TextSpan> claimed, LexicalTokenTypes types)
    {
        claimed.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        // The lexer reports through a builder; the parser already published these diagnostics from
        // the same text, so this one is filled and dropped.
        var tokens = ProtoLexer.Lex(text, ImmutableArray.CreateBuilder<ProtoParseDiagnostic>());

        foreach (var comment in ProtoLexer.Comments(text, tokens))
            yield return (comment, types.Comment);

        int claim = 0;

        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case ProtoTokenKind.Number:
                    yield return (token.Span, types.Number);
                    break;

                case ProtoTokenKind.String:
                    yield return (token.Span, types.String);
                    break;

                case ProtoTokenKind.Identifier:
                    while (claim < claimed.Count && claimed[claim].End <= token.Span.Start)
                        claim++;

                    bool taken = claim < claimed.Count && claimed[claim].Start < token.Span.End;

                    if (!taken && s_grammarKeywords.Contains(text.ToString(token.Span)))
                        yield return (token.Span, types.Keyword);

                    break;
            }
        }
    }

    /// <summary>
    /// The LSP encoding: five ints per token, each position relative to the one before it. The list
    /// has to be sorted first — declaration names are collected before the references inside them,
    /// and the lexical pass runs after both.
    /// </summary>
    private static int[] Encode(List<(int Line, int Char, int Length, int Type)> tokens)
    {
        tokens.Sort(static (a, b) => a.Line == b.Line
            ? a.Char.CompareTo(b.Char)
            : a.Line.CompareTo(b.Line));

        var data = new int[tokens.Count * 5];
        int previousLine = 0, previousChar = 0;

        for (int i = 0; i < tokens.Count; i++)
        {
            var (line, character, length, type) = tokens[i];
            data[i * 5] = line - previousLine;
            data[i * 5 + 1] = line == previousLine ? character - previousChar : character;
            data[i * 5 + 2] = length;
            data[i * 5 + 3] = type;
            data[i * 5 + 4] = 0;
            previousLine = line;
            previousChar = character;
        }

        return data;
    }
}

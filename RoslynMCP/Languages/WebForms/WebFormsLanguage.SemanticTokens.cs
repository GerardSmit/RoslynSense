using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.WebForms.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using WebFormsCore;
using WebFormsCore.Nodes;
using WebFormsCore.SourceGenerator.Models;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.WebForms;

internal sealed partial class WebFormsLanguage : ILanguageSemanticTokensProvider
{
    /// <summary>
    /// The one colour markup needs that C# has no name for. Everything else a page contains is
    /// something C# already has a legend entry for — a control is a class, an attribute is a
    /// property or an event — and reusing those entries is what makes a theme colour markup and
    /// code alike without the user configuring anything.
    /// </summary>
    public static readonly string[] SemanticTokenTypeNames = [UnknownControlType];

    private const string UnknownControlType = "unknownControl";

    /// <summary>
    /// Declined. A markup file is small enough that a full answer costs less than keeping a
    /// baseline per document per session to diff against, and the session does not advertise
    /// delta for a pack that says no.
    /// </summary>
    public bool SupportsDelta => false;

    public async Task<SemanticTokens> SemanticTokensFullAsync(
        SemanticTokensParams p, LanguageSession session, CancellationToken ct) =>
        new(await ComputeAsync(p.TextDocument.Uri, window: null, session, ct));

    public async Task<SemanticTokens> SemanticTokensRangeAsync(
        SemanticTokensRangeParams p, LanguageSession session, CancellationToken ct) =>
        new(await ComputeAsync(p.TextDocument.Uri, p.Range, session, ct));

    /// <summary>
    /// Classifies the markup the grammar cannot: whether a tag is a control that actually
    /// resolves.
    /// </summary>
    /// <remarks>
    /// This is the whole point of answering semanticTokens for markup at all. A TextMate grammar
    /// matches <c>&lt;asp:Buton&gt;</c> exactly as happily as <c>&lt;asp:Button&gt;</c> — it has
    /// no compilation to ask — so a typo in a tag name looks completely normal until the page is
    /// requested. Binding is also why this reads the parsed document rather than
    /// <see cref="WebFormsIndex"/>, which holds names and no symbols: whether a tag resolves is a
    /// question only the compilation answers.
    /// </remarks>
    private async Task<int[]> ComputeAsync(
        string uri, LspRange? window, LanguageSession session, CancellationToken ct)
    {
        var document = await AspxDocumentService.GetAsync(LspConverters.UriToPath(uri), ct);
        if (document?.Tree is not { } root)
            return [];

        var text = document.SourceText;
        var visible = window is null
            ? new TextSpan(0, text.Length)
            : LspConverters.ToTextSpan(text, window);

        int unknownControl = TokenType(session, UnknownControlType);
        int control = LanguageSession.SharedTokenType("class");
        int directive = LanguageSession.SharedTokenType("macro");
        int property = LanguageSession.SharedTokenType("property");
        int @event = LanguageSession.SharedTokenType("event");

        var tokens = new List<(int Line, int Char, int Length, int Type)>();
        var unresolved = UnresolvedTags(document.Parse.RawDiagnostics);

        foreach (var node in root.Directives)
            Add(DirectiveHead(node, text), directive);

        foreach (var element in AspxSymbolResolver.EnumerateElements(root))
        {
            ct.ThrowIfCancellationRequested();

            // Null is a tag that is not a server control at all — plain HTML, which the grammar
            // already has right and which nothing here should recolour.
            if (AspxSymbolResolver.ElementType(element) is not { } type)
                continue;

            int tag = unresolved.Contains(element.StartTag.Name.Range.Start.Offset)
                ? unknownControl
                : control;

            Add(AspxSymbolResolver.Span(element.StartTag.ElementRange), tag);
            if (element.EndTag is not null)
                Add(AspxSymbolResolver.Span(element.EndTag.ElementRange), tag);

            foreach (var key in element.RawAttributes.Keys)
            {
                if (key.Value.Equals("runat", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (AspxSymbolResolver.TryGetEvent(type, key.Value) is not null)
                    Add(AspxSymbolResolver.Span(key.Range), @event);
                else if (Binds(type, key.Value))
                    Add(AspxSymbolResolver.Span(key.Range), property);
            }
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
    }

    /// <summary>
    /// The number this session gives one of the pack's own token types. The legend is the union
    /// of C#'s types and every enabled pack's, so a name is at a different index in a session
    /// that enabled a different set — the pack holds the order, the session holds the offset.
    /// </summary>
    private int TokenType(LanguageSession session, string name) =>
        session.TokenTypeOffset(this) + Array.IndexOf(SemanticTokenTypeNames, name);

    /// <summary>
    /// The start offsets of the tag names the parser could not resolve.
    /// </summary>
    /// <remarks>
    /// Taken from the parse diagnostics rather than from the tree, because the tree cannot say
    /// it: a prefixed tag whose type is not found still becomes a control node, standing on the
    /// <c>Control</c> base class so that the rest of the parse can carry on. WFC0007 is where the
    /// parser recorded that it had given up, and its span is the tag name itself.
    /// </remarks>
    private static HashSet<int> UnresolvedTags(ImmutableArray<ReportedDiagnostic> diagnostics)
    {
        var offsets = new HashSet<int>();

        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Descriptor.Id == TypeNotFoundInNamespace)
                offsets.Add(diagnostic.TextSpan.Start);
        }

        return offsets;
    }

    /// <summary>WFC0007 — "Could not find type '{0}' in namespace '{1}'".</summary>
    private const string TypeNotFoundInNamespace = "WFC0007";

    /// <summary>
    /// Whether an attribute name resolves to a member of the control. Only the first dashed
    /// segment is walked — <c>Font</c> of <c>Font-Bold</c> — because the attribute is one token
    /// and is coloured as a whole either way.
    /// </summary>
    private static bool Binds(ITypeSymbol type, string attributeName)
    {
        int dash = attributeName.IndexOf('-');
        return type.GetMemberDeep(dash < 0 ? attributeName : attributeName[..dash]) is not null;
    }

    /// <summary>
    /// The <c>&lt;%@ Page</c> of a directive: everything before its first attribute, so the
    /// values keep the colours the grammar gives them.
    /// </summary>
    private static TextSpan DirectiveHead(DirectiveNode node, SourceText text)
    {
        int start = node.Range.Start.Offset;
        int end = node.Attributes.Count == 0
            ? node.Range.End.Offset
            : node.Attributes.Keys.Min(key => key.Range.Start.Offset);

        end = Math.Clamp(end, start, text.Length);
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;

        return TextSpan.FromBounds(start, end);
    }

    /// <summary>
    /// The LSP encoding: five ints per token, each position relative to the one before it. The
    /// list has to be sorted first — the tree yields templates after the elements around them,
    /// and attributes in hash order.
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

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>textDocument/semanticTokens via Roslyn's Classifier. Tokens are split at
/// line boundaries (no multiline token support assumed) and delta-encoded per the LSP spec.</summary>
/// <remarks>
/// Three shapes of the same computation: <c>/full</c> classifies the document, <c>/range</c>
/// classifies only what is on screen, and <c>/full/delta</c> classifies the document but answers
/// with the difference from the array the client already holds. The last one is what keeps a
/// large file affordable to edit — a one-character change otherwise re-sends every token in it.
/// </remarks>
internal static class SemanticTokensHandler
{
    /// <summary>Legend advertised in server capabilities. Indexes into this array are the
    /// token type values in the encoded data.</summary>
    /// <remarks>
    /// Append only. Language packs are handed the indices after this array, so inserting in the
    /// middle silently renumbers every pack token. The tail past <c>label</c> is where C# stops
    /// speaking the LSP standard vocabulary: the standard has one <c>variable</c> for what C#
    /// (and Rider, and Visual Studio) colour as four different things, so the distinctions get
    /// their own names and the client is told how to degrade them in
    /// <c>contributes.semanticTokenTypes</c>.
    /// </remarks>
    public static readonly string[] TokenTypes =
    [
        "namespace", "class", "struct", "interface", "enum", "enumMember", "typeParameter",
        "method", "property", "event", "parameter", "variable", "keyword", "comment",
        "string", "number", "operator", "macro", "type", "label",
        "field", "constant", "local", "delegate", "extensionMethod",
    ];

    /// <summary>Legend of modifier bits. Index i in this array is bit 1&lt;&lt;i in the encoded
    /// modifier field.</summary>
    public static readonly string[] TokenModifiers =
    [
        "static", "readonly", "declaration", "reassigned",
    ];

    private static readonly Dictionary<string, int> s_typeIndex =
        TokenTypes.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);

    private const int StaticModifier = 1 << 0;
    private const int ReadonlyModifier = 1 << 1;
    private const int ReassignedModifier = 1 << 3;

    /// <summary>
    /// Roslyn emits additive classifications as spans that overlap the primary one — a static
    /// member gets both "method" and "static symbol" over the same text. Mapping those overlaps
    /// to modifier bits costs no extra semantic work, which is why only the additive
    /// classifications Roslyn already produces are supported here.
    /// </summary>
    private static readonly Dictionary<string, int> s_modifierMap = new()
    {
        [ClassificationTypeNames.StaticSymbol] = StaticModifier,
        ["static symbol"] = StaticModifier,
        [ClassificationTypeNames.ConstantName] = ReadonlyModifier,
        [ClassificationTypeNames.ReassignedVariable] = ReassignedModifier,
    };

    /// <summary>Roslyn classification type → legend token type. Additive classifications
    /// (static symbol, reassigned variable, …) and plain identifiers/punctuation are absent —
    /// they fall through to the client's syntax highlighting.</summary>
    private static readonly Dictionary<string, string> s_classificationMap = new()
    {
        [ClassificationTypeNames.NamespaceName] = "namespace",
        [ClassificationTypeNames.ClassName] = "class",
        [ClassificationTypeNames.RecordClassName] = "class",
        [ClassificationTypeNames.StructName] = "struct",
        [ClassificationTypeNames.RecordStructName] = "struct",
        [ClassificationTypeNames.InterfaceName] = "interface",
        [ClassificationTypeNames.EnumName] = "enum",
        [ClassificationTypeNames.EnumMemberName] = "enumMember",
        [ClassificationTypeNames.TypeParameterName] = "typeParameter",
        [ClassificationTypeNames.DelegateName] = "delegate",
        [ClassificationTypeNames.MethodName] = "method",
        [ClassificationTypeNames.ExtensionMethodName] = "extensionMethod",
        [ClassificationTypeNames.PropertyName] = "property",
        [ClassificationTypeNames.EventName] = "event",
        [ClassificationTypeNames.ParameterName] = "parameter",
        [ClassificationTypeNames.LocalName] = "local",
        [ClassificationTypeNames.FieldName] = "field",
        [ClassificationTypeNames.ConstantName] = "constant",
        // Query range variables (`from x in …`). Spelled out because the constant is not on the
        // ClassificationTypeNames surface this Roslyn version exposes.
        ["range variable name"] = "variable",
        [ClassificationTypeNames.Keyword] = "keyword",
        [ClassificationTypeNames.ControlKeyword] = "keyword",
        [ClassificationTypeNames.PreprocessorKeyword] = "macro",
        [ClassificationTypeNames.Comment] = "comment",
        [ClassificationTypeNames.XmlDocCommentText] = "comment",
        [ClassificationTypeNames.XmlDocCommentDelimiter] = "comment",
        [ClassificationTypeNames.XmlDocCommentName] = "comment",
        [ClassificationTypeNames.XmlDocCommentAttributeName] = "comment",
        [ClassificationTypeNames.XmlDocCommentAttributeValue] = "comment",
        [ClassificationTypeNames.XmlDocCommentAttributeQuotes] = "comment",
        [ClassificationTypeNames.StringLiteral] = "string",
        [ClassificationTypeNames.VerbatimStringLiteral] = "string",
        [ClassificationTypeNames.NumericLiteral] = "number",
        [ClassificationTypeNames.Operator] = "operator",
        [ClassificationTypeNames.OperatorOverloaded] = "operator",
        [ClassificationTypeNames.LabelName] = "label",
    };

    /// <summary>
    /// The last full result handed to each client, so a delta request has something to diff
    /// against. Keyed by session as well as document: two editors on one daemon ask
    /// independently and would otherwise each invalidate the other's baseline.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (string ResultId, int[] Data, long Stamp)> s_previous =
        new(StringComparer.Ordinal);

    private static int s_resultCounter;
    private static long s_stampCounter;

    /// <summary>Keeps the baseline cache from growing with every file ever opened.</summary>
    private const int MaxCachedResults = 256;

    public static async Task<SemanticTokens> SemanticTokensFullAsync(
        string sessionId, SemanticTokensParams p, CancellationToken ct)
    {
        int[] data = await ComputeAsync(p.TextDocument.Uri, window: null, ct);
        return new SemanticTokens(data, Remember(sessionId, p.TextDocument.Uri, data));
    }

    /// <summary>
    /// Tokens for one range. No result id: a partial array is not a baseline any delta could be
    /// applied to, and the spec lets the server omit it.
    /// </summary>
    public static async Task<SemanticTokens> SemanticTokensRangeAsync(
        SemanticTokensRangeParams p, CancellationToken ct) =>
        new(await ComputeAsync(p.TextDocument.Uri, p.Range, ct));

    /// <summary>
    /// The difference from what the client already has. Falls back to a full result when the
    /// baseline is unknown — evicted, or from before a restart — which the protocol allows and
    /// clients handle.
    /// </summary>
    public static async Task<object> SemanticTokensDeltaAsync(
        string sessionId, SemanticTokensDeltaParams p, CancellationToken ct)
    {
        int[] data = await ComputeAsync(p.TextDocument.Uri, window: null, ct);

        string key = CacheKey(sessionId, p.TextDocument.Uri);
        bool known = s_previous.TryGetValue(key, out var previous)
            && previous.ResultId == p.PreviousResultId;

        string resultId = Remember(sessionId, p.TextDocument.Uri, data);
        if (!known)
            return new SemanticTokens(data, resultId);

        return new SemanticTokensDelta(resultId, [.. Diff(previous.Data, data)]);
    }

    /// <summary>
    /// One replacement covering everything between the common prefix and the common suffix.
    /// A finer diff would need the token identity the encoding deliberately throws away, and
    /// the win over one edit is small: an edit is local, so the unchanged prefix and suffix are
    /// nearly always the bulk of the file.
    /// </summary>
    private static SemanticTokensEdit[] Diff(int[] before, int[] after)
    {
        int prefix = 0;
        int max = Math.Min(before.Length, after.Length);
        while (prefix < max && before[prefix] == after[prefix])
            prefix++;

        int suffix = 0;
        while (suffix < max - prefix
            && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        int deleteCount = before.Length - prefix - suffix;
        int insertCount = after.Length - prefix - suffix;
        if (deleteCount == 0 && insertCount == 0)
            return [];

        return [new SemanticTokensEdit(prefix, deleteCount, after[prefix..(prefix + insertCount)])];
    }

    private static string CacheKey(string sessionId, string uri) => sessionId + "\0" + uri;

    private static string Remember(string sessionId, string uri, int[] data)
    {
        string resultId = Interlocked.Increment(ref s_resultCounter).ToString();

        // Oldest first rather than everything at once: a dropped baseline costs one full re-send
        // for that file, and wiping the table cost one for every open file in every session.
        if (s_previous.Count > MaxCachedResults)
        {
            foreach (var stale in s_previous.ToArray()
                         .OrderBy(pair => pair.Value.Stamp)
                         .Take(s_previous.Count - MaxCachedResults + MaxCachedResults / 8))
            {
                s_previous.TryRemove(stale.Key, out _);
            }
        }

        s_previous[CacheKey(sessionId, uri)] =
            (resultId, data, Interlocked.Increment(ref s_stampCounter));
        return resultId;
    }

    /// <summary>Drops the baselines of a session that has gone away.</summary>
    public static void Forget(string sessionId)
    {
        string prefix = sessionId + "\0";
        foreach (var key in s_previous.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                s_previous.TryRemove(key, out _);
        }
    }

    private static async Task<int[]> ComputeAsync(
        string uri, Protocol.Range? window, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(LspConverters.UriToPath(uri), ct);
        if (document is null)
            return Array.Empty<int>();

        var text = await document.GetTextAsync(ct);
        var classified = window is null
            ? new TextSpan(0, text.Length)
            : LspConverters.ToTextSpan(text, window);
        var spans = await Classifier.GetClassifiedSpansAsync(document, classified, ct);

        // Classifier returns overlapping syntactic + semantic + additive results; keep only
        // mapped types, then resolve overlaps by span start (semantic names win over the
        // unmapped "identifier" they overlap, which is already filtered out).
        // Additive spans are collected first: they overlap the primary span they modify, so
        // they must not be consumed by the overlap filter below.
        var modifiersBySpan = new Dictionary<TextSpan, int>();
        foreach (var span in spans)
        {
            if (s_modifierMap.TryGetValue(span.ClassificationType, out int bit))
                modifiersBySpan[span.TextSpan] = modifiersBySpan.GetValueOrDefault(span.TextSpan) | bit;
        }

        var primary = new List<Coloured>();
        int lastEnd = -1;
        foreach (var span in spans
                     .Where(s => s_classificationMap.ContainsKey(s.ClassificationType))
                     .OrderBy(s => s.TextSpan.Start)
                     .ThenBy(s => s.TextSpan.End))
        {
            if (span.TextSpan.Start < lastEnd)
                continue;
            lastEnd = span.TextSpan.End;

            primary.Add(new Coloured(
                span.TextSpan,
                s_typeIndex[s_classificationMap[span.ClassificationType]],
                modifiersBySpan.GetValueOrDefault(span.TextSpan)));
        }

        var merged = Carve(primary, await EmbeddedTokensAsync(document, classified, ct));

        var tokens = new List<(int Line, int Char, int Length, int Type, int Modifiers)>();
        foreach (var (span, type, modifiers) in merged)
        {
            var linePositions = text.Lines.GetLinePositionSpan(span);

            // Split multi-line spans (block comments, verbatim strings) into per-line tokens.
            for (int line = linePositions.Start.Line; line <= linePositions.End.Line; line++)
            {
                var textLine = text.Lines[line];
                int start = line == linePositions.Start.Line ? span.Start : textLine.Start;
                int end = line == linePositions.End.Line ? span.End : textLine.End;
                if (end > start)
                    tokens.Add((line, start - textLine.Start, end - start, type, modifiers));
            }
        }

        // Delta encoding: [deltaLine, deltaStartChar, length, tokenType, tokenModifiers] * n.
        var data = new int[tokens.Count * 5];
        int prevLine = 0, prevChar = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var (line, ch, length, type, modifiers) = tokens[i];
            data[i * 5] = line - prevLine;
            data[i * 5 + 1] = line == prevLine ? ch - prevChar : ch;
            data[i * 5 + 2] = length;
            data[i * 5 + 3] = type;
            data[i * 5 + 4] = modifiers;
            prevLine = line;
            prevChar = ch;
        }
        return data;
    }


    /// <summary>One span of one colour, before it is split at line boundaries.</summary>
    private readonly record struct Coloured(TextSpan Span, int Type, int Modifiers);

    /// <summary>
    /// The spans an embedded language claims inside the string literals of this document.
    /// </summary>
    /// <remarks>
    /// Gated twice, because this runs on every keystroke: nothing happens unless some registered
    /// language colours literals at all, and the walk is confined to the window the request asked
    /// about, so a range request over the visible screen costs a scan of the visible screen.
    /// </remarks>
    private static async Task<List<Coloured>> EmbeddedTokensAsync(
        Document document, TextSpan window, CancellationToken ct)
    {
        var languages = RoslynEmbeddedLanguages.Current;
        if (languages.IsEmpty || !languages.Languages.OfType<IEmbeddedSemanticTokensProvider>().Any())
            return [];

        var found = new List<Coloured>();

        foreach (var context in await languages.DetectAllAsync(document, ct, window))
        {
            if (context.Language is not IEmbeddedSemanticTokensProvider provider)
                continue;

            foreach (var token in await provider.SemanticTokensAsync(context, ct))
            {
                // A name outside the C# legend is dropped rather than renumbered into whatever
                // sits at that index: a wrong colour is harder to explain than none.
                if (token.Span.Length > 0 && s_typeIndex.TryGetValue(token.TokenType, out int type))
                    found.Add(new Coloured(token.Span, type, token.Modifiers));
            }
        }

        found.Sort((left, right) => left.Span.Start.CompareTo(right.Span.Start));
        return found;
    }

    /// <summary>
    /// Cuts the embedded spans out of the C# tokens that contain them and puts them in the gaps.
    /// </summary>
    /// <remarks>
    /// The protocol says tokens must not overlap, and every embedded span is inside a string
    /// literal that Roslyn already classified as one <c>string</c> token. So the literal is
    /// replaced by the pieces between the holes — which is also what makes the result look right:
    /// the quotes and the prose stay string-coloured, and only the holes change.
    /// </remarks>
    private static List<Coloured> Carve(List<Coloured> primary, List<Coloured> embedded)
    {
        if (embedded.Count == 0)
            return primary;

        var result = new List<Coloured>(primary.Count + embedded.Count * 2);

        foreach (var token in primary)
        {
            int at = token.Span.Start;

            foreach (var hole in embedded)
            {
                if (hole.Span.Start >= token.Span.End)
                    break;
                if (hole.Span.End <= at)
                    continue;

                int start = Math.Max(hole.Span.Start, at);
                if (start > at)
                    result.Add(token with { Span = TextSpan.FromBounds(at, start) });

                at = Math.Min(hole.Span.End, token.Span.End);
            }

            if (at < token.Span.End)
                result.Add(token with { Span = TextSpan.FromBounds(at, token.Span.End) });
        }

        result.AddRange(embedded);
        result.Sort((left, right) => left.Span.Start.CompareTo(right.Span.Start));
        return result;
    }
}

using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Lsp.Handlers;

/// <summary>textDocument/semanticTokens/full via Roslyn's Classifier. Tokens are split at
/// line boundaries (no multiline token support assumed) and delta-encoded per the LSP spec.</summary>
internal static class SemanticTokensHandler
{
    /// <summary>Legend advertised in server capabilities. Indexes into this array are the
    /// token type values in the encoded data.</summary>
    public static readonly string[] TokenTypes =
    [
        "namespace", "class", "struct", "interface", "enum", "enumMember", "typeParameter",
        "method", "property", "event", "parameter", "variable", "keyword", "comment",
        "string", "number", "operator", "macro", "type", "label",
    ];

    /// <summary>Legend of modifier bits. Index i in this array is bit 1&lt;&lt;i in the encoded
    /// modifier field.</summary>
    public static readonly string[] TokenModifiers =
    [
        "static", "readonly", "declaration",
    ];

    private static readonly Dictionary<string, int> s_typeIndex =
        TokenTypes.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);

    private const int StaticModifier = 1 << 0;
    private const int ReadonlyModifier = 1 << 1;

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
        [ClassificationTypeNames.DelegateName] = "type",
        [ClassificationTypeNames.MethodName] = "method",
        [ClassificationTypeNames.ExtensionMethodName] = "method",
        [ClassificationTypeNames.PropertyName] = "property",
        [ClassificationTypeNames.EventName] = "event",
        [ClassificationTypeNames.ParameterName] = "parameter",
        [ClassificationTypeNames.LocalName] = "variable",
        [ClassificationTypeNames.FieldName] = "variable",
        [ClassificationTypeNames.ConstantName] = "variable",
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

    public static async Task<SemanticTokens> SemanticTokensFullAsync(
        SemanticTokensParams p, CancellationToken ct)
    {
        var document = await LspDocumentResolver.ResolveAsync(
            LspConverters.UriToPath(p.TextDocument.Uri), ct);
        if (document is null)
            return new SemanticTokens(Array.Empty<int>());

        var text = await document.GetTextAsync(ct);
        var spans = await Classifier.GetClassifiedSpansAsync(
            document, new TextSpan(0, text.Length), ct);

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

        var tokens = new List<(int Line, int Char, int Length, int Type, int Modifiers)>();
        int lastEnd = -1;
        foreach (var span in spans
                     .Where(s => s_classificationMap.ContainsKey(s.ClassificationType))
                     .OrderBy(s => s.TextSpan.Start)
                     .ThenBy(s => s.TextSpan.End))
        {
            if (span.TextSpan.Start < lastEnd)
                continue;
            lastEnd = span.TextSpan.End;

            int type = s_typeIndex[s_classificationMap[span.ClassificationType]];
            int modifiers = modifiersBySpan.GetValueOrDefault(span.TextSpan);
            var linePositions = text.Lines.GetLinePositionSpan(span.TextSpan);

            // Split multi-line spans (block comments, verbatim strings) into per-line tokens.
            for (int line = linePositions.Start.Line; line <= linePositions.End.Line; line++)
            {
                var textLine = text.Lines[line];
                int start = line == linePositions.Start.Line
                    ? span.TextSpan.Start : textLine.Start;
                int end = line == linePositions.End.Line
                    ? span.TextSpan.End : textLine.End;
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
        return new SemanticTokens(data);
    }
}

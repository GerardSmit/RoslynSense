using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;
using LspRange = RoslynMCP.Lsp.Protocol.Range;

namespace RoslynMCP.Languages.Dbml;

internal sealed partial class DbmlLanguage : ILanguageSemanticTokensProvider
{
    /// <summary>
    /// None of its own. Everything a <c>.dbml</c> names is something C# already names — a
    /// <c>&lt;Type&gt;</c> is a class, a column's <c>Type=</c> is whatever the compilation says it is,
    /// a key names a property — so reusing C#'s entries is what makes one theme colour the model and
    /// the code SqlMetal generates from it the same way, with nothing for the user to configure.
    /// </summary>
    public static readonly string[] SemanticTokenTypeNames = [];

    /// <summary>
    /// Declined. A model is a few hundred elements, so answering in full costs less than holding a
    /// baseline per document per session to diff against.
    /// </summary>
    public bool SupportsDelta => false;

    public async Task<SemanticTokens> SemanticTokensFullAsync(
        SemanticTokensParams p, LanguageSession session, CancellationToken ct) =>
        new(await ComputeTokensAsync(p.TextDocument.Uri, window: null, ct));

    public async Task<SemanticTokens> SemanticTokensRangeAsync(
        SemanticTokensRangeParams p, LanguageSession session, CancellationToken ct) =>
        new(await ComputeTokensAsync(p.TextDocument.Uri, p.Range, ct));

    /// <summary>
    /// Colours the names a <c>.dbml</c> writes inside its attribute values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the references, and deliberately so. The tags, the attribute names and the quotes are
    /// XML, which every client already has a grammar for; layering a full classification over that
    /// would mean restating the grammar to avoid leaving holes in it. What no grammar can do is look
    /// inside the quotes: <c>Type="System.Int32"</c> and <c>Type="Sytem.Int32"</c> are the same string
    /// to a regular expression, and the second one generates a model that fails at build time with a
    /// message about a type nobody typed.
    /// </para>
    /// <para>
    /// So a name is coloured exactly when it resolves, and left to the grammar when it does not. That
    /// makes the colour itself the feedback — a <c>Type=</c> that stays the plain attribute-value
    /// colour is one that names nothing, which is the same thing the diagnostics say and the same
    /// thing F12 refuses to jump to.
    /// </para>
    /// <para>
    /// A CLR type is coloured by what it actually is rather than as one generic "type": an enum
    /// column and a struct column look different in the model because they behave differently in the
    /// code, and the compilation is right there to be asked.
    /// </para>
    /// </remarks>
    private static async Task<int[]> ComputeTokensAsync(string uri, LspRange? window, CancellationToken ct)
    {
        if (await DbmlWorkspace.GetAsync(LspConverters.UriToPath(uri), ct) is not { } view)
            return [];

        var text = view.Text;
        var visible = window is null
            ? new TextSpan(0, text.Length)
            : LspConverters.ToTextSpan(text, window);

        int property = LanguageSession.SharedTokenType("property");
        var tokens = new List<(int Line, int Char, int Length, int Type)>();

        var types = view.Database.AllTypes().ToList();

        foreach (var reference in DbmlReferences.All(view.Document))
        {
            ct.ThrowIfCancellationRequested();

            // Before resolving rather than after: resolving a name off screen is the expensive half,
            // and a range request asked about neither.
            if (!reference.Span.IntersectsWith(visible))
                continue;

            int type = reference.Kind switch
            {
                DbmlReferenceKind.ClrType => ClrTokenType(view, reference.Name),

                DbmlReferenceKind.ModelType =>
                    Named(types, reference.Name) is null ? -1 : LanguageSession.SharedTokenType("class"),

                DbmlReferenceKind.ThisKeyColumn =>
                    Named(types, reference.OwnerTypeName)?.ColumnNamed(reference.Name) is null
                        ? -1
                        : property,

                DbmlReferenceKind.OtherKeyColumn =>
                    Named(types, reference.TargetTypeName)?.ColumnNamed(reference.Name) is null
                        ? -1
                        : property,

                _ => -1,
            };

            Add(reference.Span, type);
        }

        return Encode(tokens);

        void Add(TextSpan span, int type)
        {
            if (type < 0 || span.IsEmpty)
                return;

            var lines = text.Lines.GetLinePositionSpan(span);

            // An attribute value cannot span lines in any model anyone writes, but the encoding is
            // per line and a token that crossed one would corrupt every delta after it.
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

    private static DbmlType? Named(List<DbmlType> types, string name) =>
        name.Length == 0
            ? null
            : types.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// What C# calls the type a <c>Type=</c> names, or <c>-1</c> when nothing does.
    /// </summary>
    /// <remarks>
    /// A project that has not been opened has no compilation, and a model outside any project never
    /// will — both leave the file coloured by its grammar alone, which is what it looked like before
    /// this pack existed rather than a regression.
    /// </remarks>
    private static int ClrTokenType(DbmlView view, string typeName)
    {
        if (view.Index.Compilation is not { } compilation)
            return -1;

        if (ResolveClrType(compilation, typeName) is not { } symbol)
            return -1;

        return LanguageSession.SharedTokenType(symbol.TypeKind switch
        {
            TypeKind.Enum => "enum",
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            TypeKind.Delegate => "delegate",
            _ => "class",
        });
    }

    /// <summary>
    /// The LSP encoding: five ints per token, each position relative to the one before it. Sorted
    /// first — the walk is in document order per element, but an element's attributes are not
    /// guaranteed to be.
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

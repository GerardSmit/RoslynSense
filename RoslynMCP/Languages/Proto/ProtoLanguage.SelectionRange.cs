using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Proto.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Proto;

/// <summary>
/// textDocument/selectionRange for a <c>.proto</c> — expand and shrink selection over the parse
/// tree rather than over whatever punctuation happens to be nearby.
/// </summary>
/// <remarks>
/// <para>
/// The chain a caret in a field walks is the name, the field, the message body, the message, and
/// then one enclosing declaration per keypress up to the whole file. That is the same shape the C#
/// handler gets from Roslyn's ancestor walk, and it is assembled here from the same three things
/// every other feature in the pack reads: the flattened declaration list, which is already nested
/// and already in source order, the declaration bodies, and the resolver that says what the caret
/// is on.
/// </para>
/// <para>
/// Nothing here opens a project. Expanding a selection is a question about the text alone, and
/// making the first <c>Ctrl+W</c> in a session wait behind a workspace load would be a poor trade
/// for an answer that would not change.
/// </para>
/// </remarks>
internal sealed partial class ProtoLanguage : ILanguageSelectionRangeProvider
{
    public Task<SelectionRange[]> SelectionRangesAsync(SelectionRangeParams p, CancellationToken ct)
    {
        if (ProtoDocumentService.GetParse(LspConverters.UriToPath(p.TextDocument.Uri)) is not { } file)
            return Task.FromResult<SelectionRange[]>([]);

        var chains = new List<SelectionRange>(p.Positions.Length);

        foreach (var position in p.Positions)
        {
            ct.ThrowIfCancellationRequested();
            chains.Add(ChainAt(file, LspConverters.ToOffset(file.Text, position)));
        }

        return Task.FromResult<SelectionRange[]>([.. chains]);
    }

    private static SelectionRange ChainAt(ProtoFile file, int offset)
    {
        var spans = new List<TextSpan> { new(0, file.Text.Length) };

        // Pre-order and source-ordered, so a declaration is always listed before the ones written
        // inside it and the enclosing chain comes out widest-first for free. A field in a `oneof`
        // is reached through the oneof here even though its Parent points past it at the message,
        // because containment is what the walk tests and the oneof's braces contain it.
        foreach (var declaration in file.AllDeclarations)
        {
            if (offset < declaration.Span.Start)
                break;

            if (!Covers(declaration.Span, offset))
                continue;

            spans.Add(declaration.Span);

            // The `{ … }` between the declaration and its members: selecting a message's body
            // without its header is the step a user reaches for when moving fields around.
            if (Covers(declaration.BodySpan, offset))
                spans.Add(declaration.BodySpan);

            var number = declaration switch
            {
                ProtoField field => field.NumberSpan,
                ProtoEnumValue value => value.NumberSpan,
                _ => default,
            };

            // A wire number is the identity of a field — renaming one is safe and renumbering it is
            // not — so it earns a step of its own rather than being punctuation on the way out.
            if (Covers(number, offset))
                spans.Add(number);
        }

        // The whole `import "…";` before the path inside it. The statement is not a declaration and
        // so is in no walk above, and the resolver reports the path alone once the caret is on it.
        if (file.ImportAt(offset) is { } import)
            spans.Add(import.Span);

        // The innermost step comes from the same resolver hover and go-to-definition read the caret
        // with, so the first keypress always selects exactly what F12 would have navigated from —
        // a declaration's name, a type reference, an import path, an option name, the package.
        if (ProtoSymbolResolver.ResolveAt(file, offset) is { } hit)
            spans.Add(hit.Span);

        SelectionRange? current = null;
        foreach (var span in Nest(spans, offset))
            current = new SelectionRange(LspConverters.ToRange(file.Text.Lines, span), current);

        // Non-null: the whole-document span always survives Nest.
        return current!;
    }

    /// <summary>
    /// Keeps only the spans that hold the caret and each strictly contain the one before them,
    /// which is what makes the chain safe to build from parts assembled in several passes.
    /// </summary>
    private static List<TextSpan> Nest(List<TextSpan> spans, int offset)
    {
        var result = new List<TextSpan>(spans.Count);

        foreach (var span in spans)
        {
            if (offset < span.Start || offset > span.End)
                continue;

            if (result.Count > 0 && (result[^1] == span || !result[^1].Contains(span)))
                continue;

            result.Add(span);
        }

        return result;
    }

    /// <summary>
    /// End-inclusive, because the caret sits between characters: with it just past a field's
    /// semicolon the user is still on that field. A default span is never a hit — a field carries
    /// no body and an unnumbered enum value no number, and an empty span at offset 0 would
    /// otherwise claim the caret at the top of the file.
    /// </summary>
    private static bool Covers(TextSpan span, int offset) =>
        !span.IsEmpty && offset >= span.Start && offset <= span.End;
}

using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Formatting;

/// <summary>
/// Completion inside a specifier: the components, with what each one prints.
/// </summary>
/// <remarks>
/// <para>
/// The list is the documentation. Nobody remembers whether the month is <c>MM</c> or <c>mm</c>, and
/// the way that is usually settled is by writing one, running the page, and looking — so an offer
/// that carries the rendered output beside the component answers the question where it is asked.
/// </para>
/// <para>
/// The value's type narrows the list rather than merely describing it: a <c>decimal</c> is offered
/// digit placeholders and a <c>DateTime</c> is offered date components, because the other list is
/// literal text on that type and would produce a specifier that silently prints its own letters.
/// </para>
/// </remarks>
internal sealed partial class FormattingLanguage : IEmbeddedCompletionProvider
{
    private static readonly CompletionList Empty = new(false, []);

    public async Task<CompletionList> CompletionAsync(
        EmbeddedStringContext context, CompletionParams p, CancellationToken ct)
    {
        if (Resolve(context, ct) is not { Exact: true } at)
            return Empty;

        int offset = context.Position - at.Offset;
        if (offset < 0 || offset > at.Text.Length)
            return Empty;

        // Inside the specifier and nowhere else. A caret on the index of `{0:…}` is choosing which
        // value to print, and the components would be the wrong list for it.
        if (FormatString.HoleAt(Holes(at), offset) is not { } hole
            || offset < hole.Specifier.Start
            || offset > hole.Specifier.End)
        {
            return Empty;
        }

        var family = at.Family(hole.Index);
        string specifier = at.Text[hole.Specifier.Start..hole.Specifier.End];
        var parts = FormatString.Parts(specifier, family);

        // The run under the caret is what gets replaced, so retyping half of a component works:
        // `dd-M|M` replaces the `MM` and leaves the rest of the date alone.
        var replaced = FormatString.PartAt(parts, offset - hole.Specifier.Start) is
            { Kind: not (FormatPartKind.Literal or FormatPartKind.Escape) } run
            ? at.InDocument(new TextSpan(hole.Specifier.Start + run.Span.Start, run.Span.Length))
            : new TextSpan(context.Position, 0);

        var text = await context.Document.GetTextAsync(ct);
        var range = LspConverters.ToRange(text.Lines, replaced);

        var items = new List<CompletionItem>();
        int order = 0;

        foreach (var component in FormatString.Components(family))
        {
            string detail = FormatString.Example(component.Text, family) is { } example
                ? $"{component.Description} — {example}"
                : component.Description;

            items.Add(new CompletionItem(
                component.Text,
                LspCompletionItemKind.EnumMember,
                detail,
                order++.ToString("D2"),
                component.Text,
                new TextEdit(range, component.Text)));
        }

        return new CompletionList(false, [.. items]);
    }
}

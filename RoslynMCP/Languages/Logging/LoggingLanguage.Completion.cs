using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Logging.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Logging;

/// <summary>
/// Completion inside a hole: the values this call has to offer.
/// </summary>
/// <remarks>
/// Offered in PascalCase whatever the parameter is called, because a template hole is a property
/// name in the log event and not an identifier in the method — which is what CA1727 asks for, what
/// every sink's field list looks like, and what the source generator's case-insensitive match makes
/// safe.
/// </remarks>
internal sealed partial class LoggingLanguage : IEmbeddedCompletionProvider
{
    public async Task<CompletionList> CompletionAsync(
        EmbeddedStringContext context, CompletionParams p, CancellationToken ct)
    {
        if (!Settings.Enabled || Resolve(context, ct) is not { } at || at.Site.Values.IsEmpty)
            return new CompletionList(false, []);

        if (!at.Exact || NameSpan(at, context.Position) is not { } span)
            return new CompletionList(false, []);

        var text = await context.Document.GetTextAsync(ct);
        var range = LspConverters.ToRange(text.Lines, span);

        var items = new List<CompletionItem>();
        var taken = HoleBinding.Bind(at.Template, at.Site);

        for (int i = 0; i < at.Site.Values.Length; i++)
        {
            var value = at.Site.Values[i];
            string label = Pascal(value.Name);

            // Whichever value this position would actually render, first. Under positional binding
            // it is the only one that can be right, and typing any of the others is the mistake
            // this pack exists to catch.
            bool here = at.Site.Binding == TemplateBinding.Positional
                && taken.Any(bound => bound.Position == i && at.InDocument(bound.Hole.NameSpan) == span);

            items.Add(new CompletionItem(
                label,
                LspCompletionItemKind.Property,
                $"{value.Type} {value.Name}",
                here ? "0" + label : "1" + label,
                label,
                new TextEdit(range, label))
            {
                Preselect = here ? true : null,
            });
        }

        return new CompletionList(false, [.. items]);
    }

    /// <summary>
    /// The name part of the hole the caret is in, as a span in the document.
    /// </summary>
    /// <remarks>
    /// Scanned from the raw text rather than taken from the parsed template, because the hole being
    /// typed is <c>{Ty</c> — no closing brace, so no hole to find. The scan walks back to the
    /// nearest unescaped <c>{</c> and refuses to cross a <c>}</c>, which is what keeps the caret in
    /// <c>"{A} and here"</c> from completing into the hole in front of it.
    /// </remarks>
    private static TextSpan? NameSpan(TemplateAt at, int position)
    {
        string text = at.Token.ValueText;
        int caret = position - at.Offset;

        if (caret < 0 || caret > text.Length)
            return null;

        int open = -1;
        for (int i = caret - 1; i >= 0; i--)
        {
            if (text[i] == '}')
                return null;

            if (text[i] != '{')
                continue;

            // `{{` is a literal brace, and the caret is behind the second of them.
            if (i > 0 && text[i - 1] == '{')
                return null;

            open = i;
            break;
        }

        if (open < 0)
            return null;

        int start = open + 1;
        if (start < text.Length && (text[start] == '@' || text[start] == '$'))
            start++;

        if (caret < start)
            return null;

        // The name ends where the alignment, the format or the hole does — so completing in the
        // middle of a half-typed name replaces the whole of it rather than splicing into it.
        int end = caret;
        while (end < text.Length && text[end] is not (',' or ':' or '}' or '{'))
            end++;

        return new TextSpan(at.Offset + start, end - start);
    }

    /// <summary>The name as a log property is spelled: first letter upper, the rest untouched.</summary>
    private static string Pascal(string name) =>
        name.Length == 0 || char.IsUpper(name[0]) ? name : char.ToUpperInvariant(name[0]) + name[1..];
}

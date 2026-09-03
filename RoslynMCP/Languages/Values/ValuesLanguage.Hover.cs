using System.Text;
using RoslynMCP.Languages.Values.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Values;

/// <summary>
/// Hover on a bound literal: what this code means.
/// </summary>
/// <remarks>
/// <para>
/// One question, answered. Somebody hovering <c>ordstat_new_punchout</c> is asking what that
/// <i>is</i>, and the label beside it in the table is the answer — everything else the pack knows
/// about the literal is already on screen. The member it is compared against is the identifier two
/// characters away, and the query behind the set is a fact about the configuration rather than
/// about this code; both were in this tooltip and both did nothing but push the label down.
/// </para>
/// <para>
/// The one place the pack can be honest about not knowing. A diagnostic has to stay quiet when the
/// set failed to load — silence is the only safe answer to "is this valid?" without the values —
/// but hover was asked a question, and "the set could not be loaded, here is why" is a far better
/// answer to it than a blank tooltip that looks like the feature is off.
/// </para>
/// </remarks>
internal sealed partial class ValuesLanguage : IEmbeddedHoverProvider
{
    public async Task<Hover?> HoverAsync(EmbeddedStringContext context, CancellationToken ct)
    {
        if (Site(context, ct) is not { } site)
            return null;

        var contents = await _catalog.ContentsAsync(site.Set, ct);
        var text = await context.Document.GetTextAsync(ct);

        return new Hover(
            new MarkupContent("markdown", Describe(site, contents)),
            LspConverters.ToRange(text.Lines, Shown(context, site)));
    }

    private static string Describe(ValueSite site, ValueSetContents contents)
    {
        // The diagnostic's spelling for the same case: a bolded nothing is four literal
        // asterisks, not an empty string in bold.
        var builder = site.Written.Length == 0
            ? new StringBuilder("The empty string")
            : new StringBuilder("**").Append(site.Written).Append("**");

        if (contents.State == ValueSetState.Unavailable)
        {
            return builder
                .Append("\n\nThe value set `").Append(site.Set.Id).Append("` could not be loaded, so ")
                .Append("nothing here is being checked.\n\n").Append(contents.Problem)
                .ToString();
        }

        if (contents.Find(site.Written) is { } entry)
        {
            // The label first and on the same line, because it is the whole answer. A set whose
            // query selects no second column has none, and then the count is all there is to say.
            if (entry.Label is { Length: > 0 } label)
                builder.Append(" — ").Append(label);

            builder.Append("\n\nOne of the ").Append(contents.Values.Length)
                .Append(contents.Complete ? "" : " or more")
                .Append(" `").Append(site.Set.Id).Append("` values.");
        }
        else if (contents.Complete)
        {
            builder.Append("\n\nNot one of the ").Append(contents.Values.Length)
                .Append(" values of `").Append(site.Set.Id).Append("`.");
        }

        if (!contents.Complete && contents.Problem is { Length: > 0 } problem)
            builder.Append("\n\n").Append(problem);

        return builder.ToString();
    }
}

using System.Text;
using RoslynMCP.Languages.Values.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Values;

/// <summary>
/// Hover on a bound literal: what this code means, and where that is written down.
/// </summary>
/// <remarks>
/// The one place the pack can be honest about not knowing. A diagnostic has to stay quiet when the
/// set failed to load — silence is the only safe answer to "is this valid?" without the values —
/// but hover was asked a question, and "the set could not be loaded, here is why" is a far better
/// answer to it than a blank tooltip that looks like the feature is off.
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
            LspConverters.ToRange(text.Lines, site.Span));
    }

    private static string Describe(ValueSite site, ValueSetContents contents)
    {
        var builder = new StringBuilder("**").Append(site.Written).Append("**");

        if (contents.State == ValueSetState.Unavailable)
        {
            return builder
                .Append("\n\nThe value set `").Append(site.Set.Id).Append("` could not be loaded, so ")
                .Append("nothing here is being checked.\n\n").Append(contents.Problem)
                .ToString();
        }

        if (contents.Find(site.Written) is { } entry)
        {
            if (entry.Label is { Length: > 0 } label)
                builder.Append(" — ").Append(label);
        }
        else if (contents.Complete)
        {
            builder.Append("\n\nNot one of the ").Append(contents.Values.Length)
                .Append(" values of `").Append(site.Set.Id).Append("`.");
        }

        builder.Append("\n\n").Append(Role(site)).Append(" One of ")
            .Append(contents.Values.Length).Append(contents.Complete ? "" : " or more")
            .Append(" values of `").Append(site.Set.Id).Append("`, from ")
            .Append(site.Set.Origin).Append('.');

        if (!contents.Complete && contents.Problem is { Length: > 0 } problem)
            builder.Append("\n\n").Append(problem);

        return builder.ToString();
    }

    /// <summary>How the literal reached the set, in the words the code itself uses.</summary>
    private static string Role(ValueSite site) =>
        site.Kind == ValueSiteKind.Argument
            ? $"Passed to `{site.Subject}`."
            : $"Compared against `{site.Subject}`.";
}

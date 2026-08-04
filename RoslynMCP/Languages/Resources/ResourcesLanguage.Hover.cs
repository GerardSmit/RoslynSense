using System.Collections.Immutable;
using System.Text;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Resources;

/// <summary>
/// Hover on a resource key: what it says, and everywhere else that has an opinion about it.
/// </summary>
/// <remarks>
/// The whole family rather than one winner. The winner is a function of the portal id, the thread
/// culture and a database-configured fallback locale, and none of the three exists in an editor —
/// so the value shown is the neutral one and the translations and customizations are listed beside
/// it instead of being simulated into a single answer.
/// </remarks>
internal sealed partial class ResourcesLanguage : IEmbeddedHoverProvider
{
    public async Task<Hover?> HoverAsync(EmbeddedStringContext context, CancellationToken ct)
    {
        if (await KeyAtAsync(context, ct) is not { } match)
            return null;

        var text = await context.Document.GetTextAsync(ct);

        return new Hover(
            new MarkupContent("markdown", Describe(match)),
            LspConverters.ToRange(text.Lines, match.Span));
    }

    private static string Describe(ResourceKeySearch.CodeMatch match)
    {
        var builder = new StringBuilder("**").Append(match.Key).Append("**");
        var families = Loaded(match);
        var declaring = Declaring(families, match.Key).ToList();

        if (declaring.Count == 0)
        {
            builder.Append("\n\nNo file of ").Append(Sources(families)).Append(" declares this key.");
        }
        else
        {
            // The first entry that has a string at all: a ResXFileRef or a serialized object is a
            // key with nothing to show, and the next file in precedence order may have the text.
            if (declaring.Find(file => file.Entries[match.Key].Value is not null) is { } valued)
                builder.Append("\n\n```text\n").Append(valued.Entries[match.Key].Value).Append("\n```");

            builder.Append("\n\nDefined in ");

            for (int i = 0; i < declaring.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append('`').Append(Path.GetFileName(declaring[i].FilePath)).Append('`');
            }
        }

        if (match.Confidence == RootConfidence.Ambiguous)
        {
            builder.Append("\n\n*The call does not say which resource file it reads, so these are ")
                .Append("the ones nearest it.*");
        }

        return builder.ToString();
    }

    /// <summary>The files a key was looked for in, as a phrase. Plain text, because the missing-key
    /// diagnostic says the same thing and a diagnostic message is not markdown.</summary>
    private static string Sources(ImmutableArray<ResourceFamily> families) =>
        families is [var only]
            ? Path.Combine(Path.GetFileName(only.Directory), only.BaseName + ".resx")
            : $"the {families.Length} resource files this call could reach";
}

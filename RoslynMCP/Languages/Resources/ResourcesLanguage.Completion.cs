using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Resources;

/// <summary>
/// Completion inside the key literal: the union of every key across the family.
/// </summary>
/// <remarks>
/// The union rather than the neutral file's keys, because a key only a translation declares still
/// resolves at runtime — <c>TryGetFromResourceFile</c> reads each file directly and never requires
/// the neutral one to carry the key first. Offering fewer keys than the runtime accepts would
/// teach the user to stop trusting the list.
/// </remarks>
internal sealed partial class ResourcesLanguage : IEmbeddedCompletionProvider
{
    /// <summary>How much of a value fits in a completion item's detail before it stops being a
    /// glance.</summary>
    private const int DetailLength = 80;

    public async Task<CompletionList> CompletionAsync(
        EmbeddedStringContext context, CompletionParams p, CancellationToken ct)
    {
        if (await KeyAtAsync(context, ct) is not { } match)
            return new CompletionList(false, []);

        var text = await context.Document.GetTextAsync(ct);

        // The whole key, not the prefix typed so far: the caret can be anywhere in it, and an edit
        // that replaces only what precedes the caret leaves the rest of the old key behind.
        var range = LspConverters.ToRange(text.Lines, match.Span);

        var items = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var family in Loaded(match))
        {
            foreach (string key in family.AllKeys)
            {
                // What this site has to write for the key to resolve back to that entry: a lookup
                // that appends ".Text" reaches "Save.Text" from "Save", and inserting the entry
                // name verbatim would send the runtime looking for "Save.Text.Text".
                string written = ResourceKeySearch.WrittenForm(key, match.Suffix);

                if (!seen.Add(written))
                    continue;

                // Complete as sent: completionItem/resolve carries no document, and an embedded
                // literal has no URI of its own to route one back by.
                items.Add(new CompletionItem(
                    written,
                    LspCompletionItemKind.Value,
                    Detail(family, key),
                    written,
                    written,
                    new TextEdit(range, written)));
            }
        }

        return new CompletionList(false, [.. items]);
    }

    /// <summary>The key's value, on one line, from the first file of the family that has one.</summary>
    private static string? Detail(ResourceFamily family, string key)
    {
        foreach (var file in family.Files)
        {
            if (file.Entries.TryGetValue(key, out var entry) && entry.Value is { Length: > 0 } value)
                return Flatten(value);
        }

        return null;
    }

    private static string Flatten(string value)
    {
        string single = value.ReplaceLineEndings(" ").Trim();
        return single.Length <= DetailLength ? single : single[..DetailLength] + "…";
    }
}

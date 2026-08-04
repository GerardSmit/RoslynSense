using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Resources.Core;
using RoslynMCP.Lsp;
using RoslynMCP.Lsp.Protocol;

namespace RoslynMCP.Languages.Resources;

internal sealed partial class ResourcesLanguage : ILanguageDocumentSymbolProvider
{
    /// <summary>How much of a value the outline shows beside a key before it stops being a label.</summary>
    private const int MaxValuePreview = 60;

    /// <summary>
    /// The file's keys, nested under the name in front of their first dot.
    /// </summary>
    /// <remarks>
    /// Implicit localization is the reason the nesting exists rather than a flat list: a page's
    /// resource file names a control once per property it sets — <c>btnSave.Text</c>,
    /// <c>btnSave.ToolTip</c>, <c>btnSave.AlternateText</c> — so a flat outline of a real
    /// <c>App_LocalResources</c> file is the control list interleaved three ways over.
    /// </remarks>
    public Task<DocumentSymbol[]> DocumentSymbolAsync(DocumentSymbolParams p, CancellationToken ct)
    {
        string path = LspConverters.UriToPath(p.TextDocument.Uri);

        if (ResourceCatalogService.Text(path) is not { } text)
            return Task.FromResult(Array.Empty<DocumentSymbol>());

        return Task.FromResult(Outline(text, ResxReader.Read(text)));
    }

    private static DocumentSymbol[] Outline(SourceText text, ResxContents contents)
    {
        // Hash order out of the key table; an outline follows the file. An entry whose name could
        // not be spanned exactly is left out — there is nowhere to put it, and the reader already
        // declined to let a rename touch it for the same reason.
        var entries = contents.Entries.Values
            .Where(entry => entry.KeySpan != default)
            .OrderBy(entry => entry.KeySpan.Start)
            .ToList();

        // Runs rather than buckets: a prefix that comes back further down the file starts a second
        // group instead of one group straddling everything written between, because siblings in a
        // documentSymbol tree may not overlap and a parent's range has to contain its children's.
        var groups = new List<(string Prefix, List<ResourceEntry> Members)>();

        foreach (var entry in entries)
        {
            int dot = entry.Key.IndexOf('.');
            string prefix = dot > 0 ? entry.Key[..dot] : entry.Key;

            // Ordinal, because the runtime compares resource keys that way: "Title" and "title"
            // are two resources and folding them here would claim they are one.
            if (groups.Count == 0 || !groups[^1].Prefix.Equals(prefix, StringComparison.Ordinal))
                groups.Add((prefix, []));

            groups[^1].Members.Add(entry);
        }

        var symbols = new List<DocumentSymbol>(groups.Count);

        foreach (var (prefix, members) in groups)
        {
            if (members.Count == 1)
            {
                // A group of one is a level of nesting that hides a key behind a twisty for no
                // gain, so a name that happens to contain a dot stays whole at the top level.
                symbols.Add(Leaf(text, members[0], members[0].Key));
                continue;
            }

            symbols.Add(Group(text, prefix, members));
        }

        return [.. symbols];
    }

    /// <summary>
    /// The parent for a run of keys sharing a prefix. When one of them <em>is</em> the prefix — a
    /// <c>Title</c> beside a <c>Title.Text</c> — it becomes the parent itself rather than a child
    /// repeating its own parent's name.
    /// </summary>
    private static DocumentSymbol Group(SourceText text, string prefix, List<ResourceEntry> members)
    {
        ResourceEntry? self = null;
        var children = new List<DocumentSymbol>(members.Count);

        foreach (var member in members)
        {
            if (member.Key.Equals(prefix, StringComparison.Ordinal))
            {
                self = member;
                continue;
            }

            children.Add(Leaf(text, member, member.Key[(prefix.Length + 1)..]));
        }

        var span = TextSpan.FromBounds(
            EntrySpan(members[0]).Start, members.Max(member => EntrySpan(member).End));

        return new DocumentSymbol(
            prefix,
            self is { } own ? Preview(own) : null,
            LspSymbolKind.Object,
            LspConverters.ToRange(text.Lines, span),
            LspConverters.ToRange(text.Lines, (self ?? members[0]).KeySpan),
            [.. children]);
    }

    private static DocumentSymbol Leaf(SourceText text, ResourceEntry entry, string name) =>
        new(name,
            Preview(entry),
            LspSymbolKind.Key,
            LspConverters.ToRange(text.Lines, EntrySpan(entry)),
            LspConverters.ToRange(text.Lines, entry.KeySpan),
            []);

    /// <summary>The name through the end of the value, so folding an entry folds all of it.</summary>
    private static TextSpan EntrySpan(ResourceEntry entry) =>
        entry.ValueSpan.End > entry.KeySpan.End
            ? TextSpan.FromBounds(entry.KeySpan.Start, entry.ValueSpan.End)
            : entry.KeySpan;

    /// <summary>
    /// The value on one line, short enough to sit beside the key. Null for an entry that has no
    /// string at all — a <c>ResXFileRef</c> or a serialized object.
    /// </summary>
    private static string? Preview(ResourceEntry entry)
    {
        if (entry.Value is not { Length: > 0 } value)
            return null;

        string flat = value.ReplaceLineEndings(" ");
        return flat.Length <= MaxValuePreview ? flat : flat[..MaxValuePreview] + "…";
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMCP.Languages.Formatting;
using RoslynMCP.Languages.Formatting.Core;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>
/// A format string written in markup, and what the page says it is formatting.
/// </summary>
/// <param name="Value">The attribute value's span in the document.</param>
/// <param name="Text">The value as written, which is what <see cref="FormatString"/> parses.</param>
/// <param name="Family">The grammar the specifier is read with, from the value's type when the
/// entry named a source and <see cref="FormatFamily.Unknown"/> when it did not.</param>
internal readonly record struct MarkupFormat(
    TextSpan Value, string Text, FormatFamily Family, ITypeSymbol? Source);

/// <summary>
/// Where a page writes the attributes the configuration reads as format strings.
/// </summary>
/// <remarks>
/// <para>
/// The markup half of the format-string pack. <c>DataFormatString="{0:dd-MM-yyyy}"</c> on a grid
/// column and <c>$"{value:dd-MM-yyyy}"</c> in the code behind it are the same specifier read by the
/// same runtime, so the grammar, the colours and the hovers all come from
/// <see cref="RoslynMCP.Languages.Formatting"/> and only the question "where is one written" is
/// answered here.
/// </para>
/// <para>
/// What markup adds is the source. A composite hole in C# sits beside the value it prints; in
/// markup the value is named by a <i>sibling attribute</i> — the <c>DataField</c> of the same
/// column — and resolving that against the bound item is the only way to know whether
/// <c>MM</c> is a month or two literal Ms. The configuration entry's <c>source</c> is which sibling
/// to read.
/// </para>
/// </remarks>
internal static class MarkupFormatSites
{
    /// <summary>The format string a caret sits in, with its family resolved.</summary>
    public static async Task<MarkupFormat?> AtAsync(
        AspxDocument document, int offset, CancellationToken ct)
    {
        if (MarkupBindingSites.At(document, offset, MarkupBindingKind.Format) is not { } site)
            return null;

        var source = await SourceTypeAsync(document, site, ct);

        return new MarkupFormat(
            site.Value,
            document.Text[site.Value.Start..site.Value.End],
            FormatFamilies.Of(source),
            source);
    }

    /// <summary>Every format string in the file, for the passes that are about the whole page.</summary>
    public static async Task<IReadOnlyList<MarkupFormat>> EnumerateAsync(
        AspxDocument document, CancellationToken ct)
    {
        List<MarkupFormat>? found = null;

        foreach (var site in MarkupBindingSites.Enumerate(document))
        {
            if (site.Binding.Kind != MarkupBindingKind.Format)
                continue;

            ct.ThrowIfCancellationRequested();

            var source = await SourceTypeAsync(document, site, ct);

            (found ??= []).Add(new MarkupFormat(
                site.Value,
                document.Text[site.Value.Start..site.Value.End],
                FormatFamilies.Of(source),
                source));
        }

        return (IReadOnlyList<MarkupFormat>?)found ?? [];
    }

    /// <summary>
    /// The type of the value the entry's <c>source</c> attribute names, or null.
    /// </summary>
    /// <remarks>
    /// Null is the ordinary answer and costs nothing: an entry that named no source, a tag that did
    /// not write the sibling, a page that declared no <c>ItemType</c>. The specifier is still read
    /// and coloured — only the descriptions that would depend on the grammar stand down, which is
    /// the same rule <c>WFB0001</c> follows for a path whose item type is unknown.
    /// </remarks>
    private static async Task<ITypeSymbol?> SourceTypeAsync(
        AspxDocument document, MarkupBindingSite site, CancellationToken ct)
    {
        if (site.Binding.Source is not { Length: > 0 } sourceAttribute)
            return null;

        if (MarkupBindingSites.Attribute(document, site.Element, sourceAttribute) is not { } path)
            return null;

        if (await DataBindingService.ItemTypeAsync(document, path.Start, ct) is not { } itemType)
            return null;

        var segments = DataBindingService.Segments(document.Text, path, itemType);

        // The last segment is the value the format string formats; anything before it is the walk
        // to get there, and a path that broke part-way binds to nothing worth reading.
        return segments.Length > 0 && segments[^1].Symbol is { } member
            ? DataBindingService.MemberType(member)
            : null;
    }
}

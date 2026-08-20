using Microsoft.CodeAnalysis.Text;
using WebFormsCore.Nodes;

namespace RoslynMCP.Languages.WebForms.Core;

/// <summary>
/// An attribute value the configuration says holds a data expression, and where it is written.
/// </summary>
/// <param name="Value">The value's own span, so a caret can be tested against it.</param>
/// <param name="Element">The tag carrying it, which is where a format entry finds its source.</param>
internal readonly record struct MarkupBindingSite(
    TextSpan Value, ElementNode Element, MarkupBinding Binding);

/// <summary>
/// Where a page writes the attributes the configuration reads as data expressions.
/// </summary>
/// <remarks>
/// The same three calls an <c>Eval</c> argument goes through — item type, segments, segment at the
/// caret — over a span that came from an attribute rather than from a string literal. Everything
/// that already works on <c>Eval</c> works here for free, including the dotted path and the
/// <c>X['key']</c> indexer form.
/// </remarks>
internal static class MarkupBindingSites
{
    public static IEnumerable<MarkupBindingSite> Enumerate(AspxDocument document)
    {
        var settings = MarkupBindingSettings.Current;

        if (settings.Attributes.IsDefaultOrEmpty || document.Tree is not { } root)
            yield break;

        foreach (var element in AspxSymbolResolver.EnumerateElements(root))
        {
            foreach (var (key, value) in element.RawAttributes)
            {
                if (value.Value.Length == 0)
                    continue;

                if (settings.For(element.Namespace?.Value, element.Name.Value, key.Value)
                    is not { } binding)
                {
                    continue;
                }

                if (ToSpan(document, value.Range) is { } span)
                    yield return new MarkupBindingSite(span, element, binding);
            }
        }
    }

    /// <summary>The site of the given kind a caret sits in, or null when it sits in none.</summary>
    public static MarkupBindingSite? At(AspxDocument document, int offset, MarkupBindingKind kind)
    {
        foreach (var site in Enumerate(document))
        {
            if (site.Binding.Kind == kind
                && offset >= site.Value.Start && offset <= site.Value.End)
            {
                return site;
            }
        }

        return null;
    }

    /// <summary>
    /// The span of a named attribute's value on an element, or null when it has none.
    /// </summary>
    /// <remarks>
    /// Case-insensitively, as markup attributes are matched everywhere else, and by the name as
    /// written rather than through the control's type — a format entry names its source attribute
    /// the way the page spells it, and the vendor assembly a tag comes from may not resolve.
    /// </remarks>
    public static TextSpan? Attribute(AspxDocument document, ElementNode element, string name)
    {
        foreach (var (key, value) in element.RawAttributes)
        {
            if (key.Value.Equals(name, StringComparison.OrdinalIgnoreCase) && value.Value.Length > 0)
                return ToSpan(document, value.Range);
        }

        return null;
    }

    /// <summary>
    /// A parse-tree range as offsets into the buffer.
    /// </summary>
    /// <remarks>
    /// The tree measures in lines and characters; everything downstream of here measures in
    /// offsets. A range naming a line the buffer does not have is dropped rather than clamped —
    /// that is a parse this pass should stay out of, not a position to guess at.
    /// </remarks>
    private static TextSpan? ToSpan(AspxDocument document, LinePositionSpan range)
    {
        var lines = document.SourceText.Lines;

        if (range.Start.Line >= lines.Count || range.End.Line >= lines.Count)
            return null;

        try
        {
            int start = lines.GetPosition(range.Start);
            int end = lines.GetPosition(range.End);
            return end >= start ? TextSpan.FromBounds(start, end) : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
